// DH.Driver/SDK/SdkDataProcessor.cs
// SDK数据处理器 - 将SDK回调数据转换为DH项目格式
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;
using DH.Datamanage.Realtime;

namespace DH.Driver.SDK;

/// <summary>
/// SDK设备信息
/// </summary>
public class SdkDeviceInfo
{
    /// <summary>
    /// 设备索引（从0开始）
    /// </summary>
    public int DeviceIndex { get; set; }
    
    /// <summary>
    /// 机器ID
    /// </summary>
    public int MachineId { get; set; }

    /// <summary>
    /// 回调/通道使用的设备ID（与 nGroupID 保持一致）
    /// </summary>
    public int ChannelDeviceId { get; set; }
    
    /// <summary>
    /// 机器IP地址
    /// </summary>
    public string MachineIp { get; set; } = "";
    
    /// <summary>
    /// 通道数量
    /// </summary>
    public int ChannelCount { get; set; }
    
    /// <summary>
    /// 是否在线
    /// </summary>
    public bool IsOnline { get; set; }
}

/// <summary>
/// SDK数据处理器
/// 负责接收SDK回调数据，解析后发布到DataBus
/// </summary>
public class SdkDataProcessor : IDisposable
{
    private readonly IDataBus _dataBus;
    private readonly StreamTable _streamTable;
    private readonly Action<bool, string> _statusCallback;
    
    // 保持回调委托引用，防止GC回收
    private HardwareSDK.SampleDataChangeEventHandle? _callbackDelegate;
    
    // 数据缓存 - 按通道缓冲
    private readonly ConcurrentDictionary<int, ConcurrentQueue<float>> _channelBuffers = new();
    private const int MinChunkSize = 512;
    private const int MaxChunkSize = 4096;
    private const int TargetCallbackBytes = 4 * 1024 * 1024;
    private int _chunkSize = 2048;
    private int _sdkCallbackDataCount = 2048;
    
    // 日志标记，防止重复日志
    private readonly ConcurrentDictionary<int, bool> _firstDataLogged = new();
    private readonly ConcurrentDictionary<int, bool> _firstPublishLogged = new();
    
    // 状态
    private bool _isInitialized;
    private bool _isSampling;
    private volatile bool _realtimePublishEnabled = true;
    private float _sampleRate = 1000f;
    private int _onlineDeviceCount;
    private int _totalChannelCount;
    
    // 设备信息存储
    private readonly List<SdkDeviceInfo> _deviceInfoList = new();
    
    // 线程同步
    private readonly SynchronizationContext? _syncContext;
    
    public event EventHandler<bool>? StatusChanged;
    public event EventHandler<bool>? DataActivityChanged;
    public event Action<SdkRawBlock>? RawBlockReceived;
    
    private DateTime _lastDataTime;
    private Timer? _activityTimer;
    private bool _isActive;

    public bool IsInitialized => _isInitialized;
    public bool IsSampling => _isSampling;
    public bool RealtimePublishEnabled => _realtimePublishEnabled;
    public float SampleRate => _sampleRate;
    
    /// <summary>
    /// 在线设备数量
    /// </summary>
    public int OnlineDeviceCount => _onlineDeviceCount;
    
    /// <summary>
    /// 总通道数量
    /// </summary>
    public int TotalChannelCount => _totalChannelCount;
    
    /// <summary>
    /// 获取设备信息列表（只读）
    /// </summary>
    public IReadOnlyList<SdkDeviceInfo> DeviceInfoList => _deviceInfoList.AsReadOnly();

    public void SetRealtimePublishEnabled(bool enabled)
    {
        _realtimePublishEnabled = enabled;
        if (!enabled)
        {
            ClearBufferedChannels();
        }
    }

    public SdkDataProcessor(IDataBus dataBus, StreamTable streamTable, Action<bool, string> statusCallback)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _streamTable = streamTable ?? throw new ArgumentNullException(nameof(streamTable));
        _statusCallback = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
        _syncContext = SynchronizationContext.Current;
        
        // 活动检测定时器
        _activityTimer = new Timer(CheckActivity, null, 500, 500);
    }

    private const string SDK_OWNER = "DH.Client.App.MainWindow";

    /// <summary>
    /// 初始化SDK
    /// </summary>
    /// <param name="configPath">配置文件夹路径</param>
    /// <returns>是否成功</returns>
    public bool Initialize(string configPath)
    {
        try
        {
            // 尝试获取SDK锁
            if (!SdkGlobalLock.TryAcquire(SDK_OWNER))
            {
                UpdateStatus(false, $"SDK已被 '{SdkGlobalLock.CurrentOwner}' 占用，请先断开该连接");
                return false;
            }
            
            UpdateStatus(false, $"正在初始化SDK: {configPath}");
            
            // 确保路径以反斜杠结尾
            if (!configPath.EndsWith("\\"))
            {
                configPath += "\\";
            }
            
            // 检查配置路径是否存在
            if (!System.IO.Directory.Exists(configPath.TrimEnd('\\')))
            {
                UpdateStatus(false, $"SDK配置路径不存在: {configPath}");
                return false;
            }
            
            Console.WriteLine($"[SDK] 初始化配置路径: {configPath}");
            
            // 尝试先释放之前的SDK实例（如果有）
            try
            {
                // 先停止采样（如果正在采样）
                try { HardwareSDK.StopMacSample(); } catch { }
                System.Threading.Thread.Sleep(100);
                
                // 释放SDK
                HardwareSDK.QuitMacControl();
                Console.WriteLine("[SDK] 已释放之前的SDK实例");
                
                // 等待一段时间让资源完全释放
                System.Threading.Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SDK] 释放SDK实例时出现异常（可忽略）: {ex.Message}");
            }
            
            // 初始化SDK（注意：返回值不是错误码，Demo_C#中也不检查返回值）
            int result = HardwareSDK.InitMacControl(configPath);
            Console.WriteLine($"[SDK] InitMacControl 返回: {result} (0x{result:X8})");
            // Demo_C#源码中不检查InitMacControl返回值，直接继续执行
            
            // 注册回调函数 - 必须保持委托引用
            _callbackDelegate = OnSampleDataReceived;
            int callbackResult = HardwareSDK.SetDataChangeCallBackFun(_callbackDelegate);
            Console.WriteLine($"[SDK] SetDataChangeCallBackFun 返回: {callbackResult}");
            // 回调注册也不检查返回值，与Demo_C#保持一致
            
            // 查找并连接设备
            bool connected = HardwareSDK.RefindAndConnecMac();
            Console.WriteLine($"[SDK] RefindAndConnecMac 返回: {connected}");
            
            int deviceCount = HardwareSDK.GetAllMacOnlineCount();
            Console.WriteLine($"[SDK] GetAllMacOnlineCount 返回: {deviceCount}");
            _onlineDeviceCount = deviceCount;
            
            // 获取每个设备的详细信息
            _deviceInfoList.Clear();
            _totalChannelCount = 0;
            for (int i = 0; i < deviceCount; i++)
            {
                try
                {
                    // 分配IP字符串缓冲区
                    IntPtr ipBuffer = Marshal.AllocHGlobal(64);
                    try
                    {
                        int machineId;
                        int usedBuffer;
                        int infoResult = HardwareSDK.GetMacInfoFromIndex(i, out machineId, ipBuffer, 64, out usedBuffer);
                        
                        string machineIp = "";
                        if (infoResult >= 0 && usedBuffer > 0)
                        {
                            machineIp = Marshal.PtrToStringAnsi(ipBuffer) ?? "";
                        }
                        
                        // 获取该设备的通道数量
                        int channelCount = HardwareSDK.GetMacCurrentChnCount(machineId, machineIp);
                        if (channelCount < 0) channelCount = 0;
                        
                        // 获取设备连接状态
                        byte linkStatus = HardwareSDK.GetMacLinkStatus(machineId, machineIp);
                        bool isOnline = linkStatus > 0;
                        
                        var deviceInfo = new SdkDeviceInfo
                        {
                            DeviceIndex = i,
                            MachineId = machineId,
                            ChannelDeviceId = SdkDeviceIdResolver.ResolveDeviceId(
                                groupId: -1,
                                machineId: machineId,
                                channelDeviceId: i,
                                deviceIndex: i),
                            MachineIp = machineIp,
                            ChannelCount = channelCount,
                            IsOnline = isOnline
                        };
                        _deviceInfoList.Add(deviceInfo);
                        _totalChannelCount += channelCount;
                        
                        Console.WriteLine($"[SDK] 设备{i}: MachineId={machineId}, IP={machineIp}, 通道数={channelCount}, 在线={isOnline}");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ipBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SDK] 获取设备{i}信息异常: {ex.Message}");
                }
            }
            
            // 获取当前采样率
            _sampleRate = HardwareSDK.GetMacCurrentSampleFreq();
            if (_sampleRate <= 0) _sampleRate = 1000f;
            
            _isInitialized = true;
            UpdateStatus(true, $"SDK初始化成功，在线设备: {deviceCount}，总通道数: {_totalChannelCount}，采样率: {_sampleRate}Hz");
            StatusChanged?.Invoke(this, true);
            
            return true;
        }
        catch (DllNotFoundException ex)
        {
            UpdateStatus(false, $"找不到SDK DLL: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"SDK初始化异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动采样
    /// </summary>
    public bool StartSampling()
    {
        if (!_isInitialized)
        {
            UpdateStatus(false, "SDK未初始化，无法启动采样");
            return false;
        }
        
        try
        {
            // 清空缓冲区
            _channelBuffers.Clear();
            
            // 设置每次获取的数据量
            RefreshIngestBatchSettings();
            HardwareSDK.SetGetDataCountEveryTime(_sdkCallbackDataCount);
            Console.WriteLine($"[SDK] Callback block size={_sdkCallbackDataCount}, publish chunk size={_chunkSize}, total channels={_totalChannelCount}, sample rate={_sampleRate}Hz");
            
            // 启动采样
            HardwareSDK.StartMacSample();
            
            _isSampling = true;
            UpdateStatus(true, "SDK采样已启动");
            
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"启动采样失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 停止采样
    /// </summary>
    public void StopSampling()
    {
        if (!_isSampling) return;
        
        try
        {
            HardwareSDK.StopMacSample();
            FlushBufferedChannels();
            _isSampling = false;
            _isActive = false;
            DataActivityChanged?.Invoke(this, false);
            UpdateStatus(true, "SDK采样已停止");
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"停止采样失败: {ex.Message}");
        }
    }

    /// <summary>
    /// SDK数据回调处理
    /// </summary>
    private void OnSampleDataReceived(
        long sampleTime,
        int groupIdSize,
        IntPtr groupInfo,
        int nMessageType,
        int nGroupID,
        int nChannelStyle,
        int nChannelID,
        int nMachineID,
        long nTotalDataCount,
        int nDataCountPerChannel,
        int nBufferCount,
        int nBlockIndex,
        long varSampleData)
    {
        try
        {
            // 更新活动时间
            UpdateObservedDeviceMapping(nMachineID, nGroupID);
            _lastDataTime = DateTime.UtcNow;
            if (!_isActive)
            {
                _isActive = true;
                DataActivityChanged?.Invoke(this, true);
                Console.WriteLine($"[SDK回调] 数据活动开始，MessageType={nMessageType}, GroupID={nGroupID}, MachineID={nMachineID}, 每通道数据量={nDataCountPerChannel}");
            }
            
            // 检查消息类型
            if (nMessageType != SdkMessageTypes.SAMPLE_ANALOG_DATA &&
                nMessageType != SdkMessageTypes.SAMPLE_ANALOG_MULTICHN_DATA &&
                nMessageType != SdkMessageTypes.SAMPLE_SINGLEGROUP_ANALOGDATA)
            {
                return;
            }
            
            if (nDataCountPerChannel <= 0 || nBufferCount <= 0)
            {
                return;
            }
            
            // 从指针读取数据
            
            
            // 计算通道数
            int floatCount = nBufferCount / sizeof(float);
            int channelCount = floatCount / nDataCountPerChannel;
            if (channelCount <= 0) channelCount = 1;
            
            // 解析float数据
            bool needsRawBlock = RawBlockReceived is not null;
            bool needsRealtimePublish = _realtimePublishEnabled;
            if (!needsRawBlock && !needsRealtimePublish)
            {
                return;
            }

            float[] allData = SdkRawFloatBufferPool.Rent(floatCount);
            bool bufferOwnedByRawBlock = false;

            try
            {
                Marshal.Copy((IntPtr)varSampleData, allData, 0, floatCount);

                if (needsRawBlock)
            {
                var rawBlock = new SdkRawBlock
                {
                    SampleTime = sampleTime,
                    MessageType = nMessageType,
                    GroupId = nGroupID,
                    MachineId = nMachineID,
                    TotalDataCount = nTotalDataCount,
                    DataCountPerChannel = nDataCountPerChannel,
                    BufferCountBytes = nBufferCount,
                    BlockIndex = nBlockIndex,
                    ChannelCount = channelCount,
                    SampleRateHz = _sampleRate,
                    ReceivedAtUtc = DateTime.UtcNow,
                    InterleavedSamples = allData,
                    PayloadFloatCount = floatCount,
                    ReturnBufferToPool = true
                };

                bufferOwnedByRawBlock = true;

                try
                {
                    RawBlockReceived?.Invoke(rawBlock);
                }
                catch (Exception rawEx)
                {
                    Console.WriteLine($"[SdkDataProcessor] 原始块旁路异常: {rawEx.Message}");
                }
            }

            if (!needsRealtimePublish)
            {
                return;
            }
              
            // 使用 nGroupID 作为设备标识符（与SdkRealtimeIngestor保持一致）
            int deviceId = SdkDeviceIdResolver.ResolveDeviceId(
                groupId: nGroupID,
                machineId: nMachineID);
            
            // 按通道分发数据 - 使用交织格式解析（与SdkRealtimeIngestor保持一致）
            // 数据格式: [Ch0_S0, Ch1_S0, Ch2_S0... Ch0_S1, Ch1_S1, Ch2_S1...]
            for (int ch = 0; ch < channelCount; ch++)
            {
                // 构建通道ID: 设备ID * 100 + 通道索引(1-based)
                int channelId = deviceId * 100 + (ch + 1);
                
                // 交织格式解析：提取该通道的数据
                float[] channelData = new float[nDataCountPerChannel];
                for (int i = 0; i < nDataCountPerChannel; i++)
                {
                    int idx = i * channelCount + ch;
                    if (idx < floatCount)
                    {
                        channelData[i] = allData[idx];
                    }
                }
                
                // 发布到DataBus
                PublishChannelData(channelId, channelData, nTotalDataCount);
            }
            }
            finally
            {
                if (!bufferOwnedByRawBlock)
                {
                    SdkRawFloatBufferPool.Return(allData);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkDataProcessor] 回调处理异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 发布通道数据到DataBus
    /// </summary>
    private void PublishChannelData(int channelId, float[] samples, long totalCount)
    {
        // 确保通道存在
        _streamTable.EnsureChannel(channelId, DH.Contracts.ChannelNaming.ChannelName(channelId));
        _dataBus.EnsureChannel(channelId);
        
        // 获取或创建通道缓冲区
        var buffer = _channelBuffers.GetOrAdd(channelId, _ => new ConcurrentQueue<float>());
        
        // 添加数据到缓冲区
        int offset = 0;
        if (buffer.IsEmpty)
        {
            if (samples.Length >= _chunkSize)
            {
                while (offset + _chunkSize <= samples.Length)
                {
                    var directChunk = new float[_chunkSize];
                    Array.Copy(samples, offset, directChunk, 0, _chunkSize);
                    PublishChunk(channelId, directChunk);
                    offset += _chunkSize;
                }
            }
            else if (samples.Length >= MinChunkSize)
            {
                var directChunk = new float[samples.Length];
                Array.Copy(samples, directChunk, samples.Length);
                PublishChunk(channelId, directChunk);

                if (_firstDataLogged.TryAdd(channelId, true))
                {
                    Console.WriteLine($"[SDK数据] 通道{channelId}直接发布回调块, 样本数={samples.Length}");
                }

                return;
            }
        }

        for (int i = offset; i < samples.Length; i++)
        {
            buffer.Enqueue(samples[i]);
        }
        
        // 记录首次数据到达的日志
        if (_firstDataLogged.TryAdd(channelId, true))
        {
            Console.WriteLine($"[SDK数据] 通道{channelId}首次收到数据，样本数={samples.Length}, 缓冲区大小={buffer.Count}");
        }
        
        // 达到批次大小时发布
        while (buffer.Count >= _chunkSize)
        {
            var chunk = new float[_chunkSize];
            for (int i = 0; i < _chunkSize; i++)
            {
                if (!buffer.TryDequeue(out chunk[i]))
                    break;
            }
            
            // 创建数据帧
            
            
            // 异步发布
            PublishChunk(channelId, chunk);
            
            // 记录发布日志（仅首次）
            if (_firstPublishLogged.TryAdd(channelId, true))
            {
                Console.WriteLine($"[SDK发布] 通道{channelId}首次发布数据帧，采样率={_sampleRate}Hz, chunk大小={_chunkSize}");
            }
        }
    }

    /// <summary>
    /// 检查数据活动状态
    /// </summary>
    private void PublishChunk(int channelId, float[] chunk)
    {
        var frame = new SimpleFrame
        {
            ChannelId = channelId,
            Timestamp = DateTime.UtcNow,
            Samples = chunk,
            Header = new FrameHeader { SampleRate = (int)_sampleRate }
        };

        _ = _streamTable.PublishAsync(frame, CancellationToken.None);

        if (_firstPublishLogged.TryAdd(channelId, true))
        {
            Console.WriteLine($"[SDKé™æˆç«·] é–«æ°¶äº¾{channelId}æ££æ ¨î‚¼é™æˆç«·éç‰ˆåµç”¯Ñç´é–²å›¨ç‰±éœ?{_sampleRate}Hz, chunkæ¾¶Ñƒçš¬={_chunkSize}");
        }
    }

    private void FlushBufferedChannels()
    {
        foreach (var kvp in _channelBuffers)
        {
            int count = kvp.Value.Count;
            if (count <= 0)
            {
                continue;
            }

            var remaining = new float[count];
            int actualCount = 0;
            while (actualCount < remaining.Length && kvp.Value.TryDequeue(out var sample))
            {
                remaining[actualCount++] = sample;
            }

            if (actualCount <= 0)
            {
                continue;
            }

            if (actualCount != remaining.Length)
            {
                Array.Resize(ref remaining, actualCount);
            }

            PublishChunk(kvp.Key, remaining);
        }
    }

    private void ClearBufferedChannels()
    {
        foreach (var kvp in _channelBuffers)
        {
            while (kvp.Value.TryDequeue(out _))
            {
            }
        }
    }

    private void UpdateObservedDeviceMapping(int machineId, int channelDeviceId)
    {
        if (channelDeviceId < 0)
        {
            return;
        }

        int canonicalDeviceId = SdkDeviceIdResolver.ResolveDeviceId(
            groupId: channelDeviceId,
            machineId: machineId);

        lock (_deviceInfoList)
        {
            var device = _deviceInfoList.Find(d => d.MachineId == machineId);
            if (device != null)
            {
                device.ChannelDeviceId = canonicalDeviceId;
                return;
            }

            if (canonicalDeviceId >= 0)
            {
                int index = canonicalDeviceId;
                if (index >= 0 && index < _deviceInfoList.Count)
                {
                    _deviceInfoList[index].ChannelDeviceId = canonicalDeviceId;
                }
            }
        }
    }

    private void RefreshIngestBatchSettings()
    {
        int channelCount = Math.Max(1, _totalChannelCount);
        int samplesByBytes = Math.Max(MinChunkSize, TargetCallbackBytes / Math.Max(1, channelCount * sizeof(float)));
        int normalized = NormalizePowerOfTwo(samplesByBytes);
        _sdkCallbackDataCount = Math.Clamp(normalized, MinChunkSize, MaxChunkSize);
        _chunkSize = _sdkCallbackDataCount;
    }

    private static int NormalizePowerOfTwo(int value)
    {
        int result = 1;
        while (result < value && result < MaxChunkSize)
        {
            result <<= 1;
        }

        return result;
    }

    private void CheckActivity(object? state)
    {
        if (_isActive && (DateTime.UtcNow - _lastDataTime).TotalMilliseconds > 500)
        {
            _isActive = false;
            DataActivityChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// 更新状态
    /// </summary>
    private void UpdateStatus(bool isConnected, string message)
    {
        Console.WriteLine($"[SDK] {message}");
        _statusCallback?.Invoke(isConnected, message);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        try
        {
            _activityTimer?.Dispose();
            _activityTimer = null;
            
            if (_isSampling)
            {
                StopSampling();
            }
            
            if (_isInitialized)
            {
                HardwareSDK.QuitMacControl();
                _isInitialized = false;
            }
            
            _callbackDelegate = null;
            _channelBuffers.Clear();
            _firstDataLogged.Clear();
            _firstPublishLogged.Clear();
            
            // 释放SDK锁
            SdkGlobalLock.Release(SDK_OWNER);
            
            StatusChanged?.Invoke(this, false);
            UpdateStatus(false, "SDK已释放");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkDataProcessor] 释放资源异常: {ex.Message}");
        }
    }
}
