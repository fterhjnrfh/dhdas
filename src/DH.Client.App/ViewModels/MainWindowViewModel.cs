using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Contracts.Models;
using DH.Driver;
using DH.Driver.SDK;
using DH.Datamanage.Realtime;
using DH.Algorithms.Builtins;
using DH.Client.App.Data;
using DH.Client.App.Services.Storage;
using DH.Client.App.Controls;

namespace DH.Client.App.ViewModels;

/// <summary>文件列表项：携带路径和格式化的显示文本（含文件大小）</summary>
public sealed class TdmsFileItem
{
    public string FullPath { get; }
    public string DisplayText { get; }

    public TdmsFileItem(FileInfo fi)
    {
        FullPath = fi.FullName;
        // 显示所在文件夹名（时间命名子文件夹）+ 文件名 + 大小
        var folderName = fi.Directory?.Name ?? "";
        var folderPrefix = !string.IsNullOrEmpty(folderName) && folderName != "data"
            ? $"[{folderName}] "
            : "";
        DisplayText = $"{folderPrefix}{fi.Name}  ({FormatSize(fi.Length)})";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    public override string ToString() => DisplayText;
}

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ChannelInfo> Channels { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<ChannelStatus> DeviceChannels { get; } = new();
    public ObservableCollection<ChannelStatus> OnlineChannels { get; } = new();
    
    // 在线通道统计
    public string OnlineChannelStatus => $"在线通道: {Channels.Count(c => c.Online)}/{Channels.Count}";
    [ObservableProperty] private int _selectedTab = 3;
    [ObservableProperty] private string _storagePath = "./data";
    // 新增：存储控制与模式
    public enum StorageModeOption { SingleFile, PerChannel }
    [ObservableProperty] private StorageModeOption _storageMode = StorageModeOption.SingleFile;
    [ObservableProperty] private bool _storageEnabled;
    [ObservableProperty] private string _storageSessionName = "session";
    [ObservableProperty] private int _storageModeIndex = 0; // 0: 单文件, 1: 多文件
    [ObservableProperty] private int _compressionTypeIndex = 0; // 0: 不压缩, 1: LZ4, 2: Zstd, 3: Brotli, 4: Snappy, 5: Zlib, 6: LZ4_HC, 7: BZip2
    [ObservableProperty] private int _preprocessTypeIndex = 0; // 0: 不预处理, 1: 一阶差分, 2: 二阶差分, 3: 线性预测
    
    // 压缩参数配置
    private CompressionOptions _compressionOptions = new();
    [ObservableProperty] private int _lz4Level = 0;
    [ObservableProperty] private int _zstdLevel = 3;
    [ObservableProperty] private int _zstdWindowLog = 23;
    [ObservableProperty] private int _brotliQuality = 4;
    [ObservableProperty] private int _brotliWindowBits = 22;
    [ObservableProperty] private int _zlibLevel = 6;
    [ObservableProperty] private int _bzip2BlockSize = 9;
    [ObservableProperty] private int _lz4hcLevel = 12;
    
    // 压缩算法选项转换
    public CompressionType SelectedCompressionType => (CompressionType)CompressionTypeIndex;
    // 预处理技术选项转换
    public PreprocessType SelectedPreprocessType => (PreprocessType)PreprocessTypeIndex;
    // 文件无损验证结果
    [ObservableProperty] private string _fileVerifyResult = "";
    [ObservableProperty] private bool _fileVerifyPassed;
    // 写入哈希缓存：文件路径 → {通道名 → hash/sampleCount}（支持跨文件手动验证）
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _writeHashesByFile = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _writeSampleCountsByFile = new();
    private IReadOnlyList<string>? _lastWrittenFiles;
    // 新增：存储状态与最近文件列表
    [ObservableProperty] private string _storageStatusMessage = "未开始写入";
    // 写入计时器
    [ObservableProperty] private string _storageElapsed = "00:00:00";
    private DateTime _storageStartTime;
    private Avalonia.Threading.DispatcherTimer? _storageTimer;
    public ObservableCollection<TdmsFileItem> RecentTdmsFiles { get; } = new();
    [ObservableProperty] private TdmsFileItem? _selectedTdmsFile;

    // 命令：存储控制
    public IRelayCommand StartStorageCommand { get; }
    public IRelayCommand StopStorageCommand { get; }
    // 新增：最近文件与读取相关命令
    public IRelayCommand RefreshRecentFilesCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand TestReadSelectedFileCommand { get; }
    public IRelayCommand VerifyStoredFileCommand { get; }
    private ITdmsStorage? _storage;
    [ObservableProperty] private int _maWindow = 16;
    [ObservableProperty] private bool _isRunning;
    
    [ObservableProperty] private string _tcpServerIp = "127.0.0.1";
    [ObservableProperty] private string _tcpServerPort = "4008";
    [ObservableProperty] private string _tcpConnectionStatus = "未连接";
    [ObservableProperty] private bool _isTcpConnected;
    [ObservableProperty] private bool _isDataVerified;
    [ObservableProperty] private bool _isDataActive;
    [ObservableProperty] private int _channelId = 1;
    
    [ObservableProperty] private int _sampleRate = 1000; // 默认采样频率 1000Hz

    private const int DefaultOnlineChannelCount = 8;

    // 计算属性：根据连接状态返回颜色
    public IBrush TcpStatusColor => IsTcpConnected ? Brushes.Green : Brushes.Red;

    private Task? _consumerTask;
    private readonly TcpDriverManager _tcpDriverManager;
    private readonly DataBus _bus = new();
    private readonly StreamTable _table;
    
    private CancellationTokenSource? _cts = new();
    
    private MovingAverageAlgorithm _algo;
    private OnlineChannelManager _onlineChannelManager;
    private LocalTestServer? _localServer;
    private System.Timers.Timer? _channelTimeUpdateTimer; // 通道计时器

    // ==================== SDK模式相关属性 ====================
    private SdkDriverManager? _sdkDriverManager;
    
    /// <summary>
    /// 数据源模式: 0=TCP, 1=SDK
    /// </summary>
    [ObservableProperty] private int _dataSourceMode = 0;
    
    /// <summary>
    /// SDK配置路径
    /// </summary>
    [ObservableProperty] private string _sdkConfigPath = "";
    
    /// <summary>
    /// SDK连接状态
    /// </summary>
    [ObservableProperty] private string _sdkConnectionStatus = "SDK未初始化";
    
    /// <summary>
    /// SDK是否已初始化
    /// </summary>
    [ObservableProperty] private bool _isSdkInitialized;
    
    /// <summary>
    /// SDK是否正在采样
    /// </summary>
    [ObservableProperty] private bool _isSdkSampling;
    
    /// <summary>
    /// SDK数据是否活跃
    /// </summary>
    [ObservableProperty] private bool _isSdkDataActive;
    
    // SDK模式计算属性
    public IBrush SdkStatusColor => IsSdkInitialized ? (IsSdkSampling ? Brushes.Green : Brushes.Orange) : Brushes.Red;
    
    /// <summary>
    /// 设备统计摘要（显示在通道管理界面）
    /// </summary>
    public string DeviceSummary
    {
        get
        {
            if (DataSourceMode == 1 && IsSdkInitialized && _sdkDriverManager != null) // SDK模式
            {
                int deviceCount = _sdkDriverManager.OnlineDeviceCount;
                int channelCount = _sdkDriverManager.TotalChannelCount;
                return $"📊 在线设备: {deviceCount} 台 | 总通道数: {channelCount} 个 | 采样率: {SampleRate}Hz";
            }
            else if (DataSourceMode == 0 && IsTcpConnected) // TCP模式
            {
                int onlineDevices = Devices.Count(d => d.Online);
                int onlineChannels = Channels.Count(c => c.Online);
                return $"📊 在线设备: {onlineDevices} 台 | 在线通道: {onlineChannels} 个";
            }
            return "📊 未连接数据源";
        }
    }
    
    // SDK命令
    public IRelayCommand InitializeSdkCommand { get; private set; } = null!;
    public IRelayCommand StartSdkSamplingCommand { get; private set; } = null!;
    public IRelayCommand StopSdkSamplingCommand { get; private set; } = null!;
    public IRelayCommand BrowseSdkConfigCommand { get; private set; } = null!;
    // ==================== SDK模式相关属性结束 ====================


    
    public IRelayCommand ApplyAlgoCommand { get; }
    public IRelayCommand ConnectTcpCommand { get; }
    public IRelayCommand DisconnectTcpCommand { get; }
    public IRelayCommand SendTestPacketCommand { get; }
    public IRelayCommand StartLocalServerCommand { get; }
    public IRelayCommand StopLocalServerCommand { get; }
    
    // 批量通道管理命令
    public IRelayCommand SetAllOnlineCommand { get; }
    public IRelayCommand SetAllOfflineCommand { get; }
    public IRelayCommand SetCh1To32OnlineCommand { get; }
    public IRelayCommand SetCh33To64OnlineCommand { get; }
    public IRelayCommand SetSelectedDeviceCommand { get; }
    
    // 采样频率调节命令
    public IRelayCommand SampleRateChangedCommand { get; }
    public DataBus Bus => _bus;
    // 默认选中设备0（支持SDK的nGroupID从0开始）
    [ObservableProperty] private int _selectedDeviceId = 0;
    public DeviceInfo? SelectedDevice => Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
    public string SelectedDeviceTitle => $"通道在线状态 - AI{SelectedDeviceId}";

    public MainWindowViewModel()
    {
        _bus = new DataBus();
        _table = new StreamTable(_bus);
        
        _algo = new MovingAverageAlgorithm(_maWindow);
        // 将当前算法配置窗口应用到所有曲线视图的绘制平滑
        SkiaMultiChannelView.SetGlobalMovingAverage(true, _maWindow);
        _onlineChannelManager = new OnlineChannelManager();

        // 预创建通道，支持设备ID从0开始（SDK的nGroupID可能从0开始）
        // 设备0的通道ID: 1-64, 设备1的通道ID: 101-164, ...
        for (int d = 0; d < 64; d++)
        {
            for (int c = 1; c <= 64; c++)
            {
                int id = d * 100 + c;
                var ch = _table.EnsureChannel(id, DH.Contracts.ChannelNaming.ChannelName(id));
                Channels.Add(ch);
            }
        }

        BuildDevices();
        EnsureDeviceChannelStatuses($"AI{SelectedDeviceId:D2}");

        // 同步Channels初始在线状态到管理器的默认集合
        foreach (var channel in Channels)
        {
            channel.Online = _onlineChannelManager.IsChannelOnline(channel.ChannelId);
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));

        // 监听在线通道变化事件
        _onlineChannelManager.OnlineChannelsChanged += OnOnlineChannelsChanged;
        
        // 启动通道计时器，每秒更新一次在线时长
        _channelTimeUpdateTimer = new System.Timers.Timer(1000); // 1秒
        _channelTimeUpdateTimer.Elapsed += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var ch in DeviceChannels)
                {
                    ch.UpdateOnlineTime();
                }
            });
        };
        _channelTimeUpdateTimer.Start();

        // 创建TCP驱动管理器，传入数据总线和流表       
        _tcpDriverManager = new TcpDriverManager(_bus, _table, OnTcpStatusChanged);
        _tcpDriverManager.VerifiedChanged += v => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsDataVerified = v;
            if (!v)
            {
                foreach (var c in Channels) c.Online = false;
                foreach (var dev in Devices)
                {
                    dev.OnlineChannelCount = 0;
                    dev.Online = false;
                }
                UpdateDeviceChannels();
            }
            if (v)
            {
                var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
                if (dev != null) dev.Online = true;
                UpdateDeviceChannels();
            }
        });
        _tcpDriverManager.ActivityChanged += a => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsDataActive = a;
            if (!a)
            {
                foreach (var c in Channels) c.Online = false;
                foreach (var dev in Devices)
                {
                    dev.OnlineChannelCount = 0;
                    dev.Online = false;
                }
                UpdateDeviceChannels();
            }
        });

        // 同步在线通道与数据总线（连接成功后由数据到达驱动）
        _bus.ChannelAdded += (_, ch) => { };
        _bus.ChannelRemoved += (_, ch) => { };
        _bus.DataUpdated += (_, e) =>
        {
            // TCP模式：检查连接状态
            bool tcpActive = IsTcpConnected && IsDataVerified && IsDataActive;
            // SDK模式：检查SDK初始化和采样状态
            bool sdkActive = DataSourceMode == 1 && IsSdkInitialized && IsSdkSampling;
            
            if (tcpActive || sdkActive)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var ci = Channels.FirstOrDefault(c => c.ChannelId == e.ChannelId);
                        if (ci != null) ci.Online = true;
                        _onlineChannelManager.SetChannelOnline(e.ChannelId, true);
                        foreach (var dev in Devices.ToList())
                        {
                            int cnt = dev.Channels.Count(c => c.Online);
                            dev.OnlineChannelCount = cnt;
                            dev.Online = cnt > 0;
                        }
                        OnPropertyChanged(nameof(OnlineChannelStatus));
                        UpdateDeviceChannels();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DataUpdated] UI更新异常: {ex.Message}");
                    }
                });
            }
        };

        //命令初始化
        ApplyAlgoCommand = new RelayCommand(ApplyAlgo);
        ConnectTcpCommand = new RelayCommand(ConnectTcp, () => !IsTcpConnected);
        DisconnectTcpCommand = new RelayCommand(DisconnectTcp, () => IsTcpConnected);
        SendTestPacketCommand = new RelayCommand(SendTestPacket, () => _tcpDriverManager.IsConnected);
        StartLocalServerCommand = new RelayCommand(StartLocalServer);
        StopLocalServerCommand = new RelayCommand(StopLocalServer);

        SetAllOnlineCommand = new RelayCommand(SetAllOnline);
        SetAllOfflineCommand = new RelayCommand(SetAllOffline);
        SetCh1To32OnlineCommand = new RelayCommand(SetCh1To32Online);
        SetCh33To64OnlineCommand = new RelayCommand(SetCh33To64Online);

        // 采样频率调节命令初始化
        SampleRateChangedCommand = new RelayCommand<int>(OnSampleRateChangedCommand);
        SetSelectedDeviceCommand = new RelayCommand<int>(id =>
        {
            // 支持设备ID从0开始（SDK的nGroupID可能从0开始）
            SelectedDeviceId = Math.Clamp(id, 0, 63);
            EnsureDeviceChannelStatuses($"AI{SelectedDeviceId:D2}");
            UpdateDeviceChannels();
            OnPropertyChanged(nameof(SelectedDevice));
            OnPropertyChanged(nameof(SelectedDeviceTitle));
        });

        // 存储命令初始化
        StartStorageCommand = new AsyncRelayCommand(StartStorageAsync, () => !StorageEnabled);
        StopStorageCommand = new RelayCommand(StopStorage, () => StorageEnabled);
        // 新增命令初始化
        RefreshRecentFilesCommand = new RelayCommand(RefreshRecentFiles);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        TestReadSelectedFileCommand = new RelayCommand(TestReadSelectedFile, () => !string.IsNullOrEmpty(SelectedTdmsFile?.FullPath));
        VerifyStoredFileCommand = new AsyncRelayCommand(VerifyStoredFileAsync, () => !string.IsNullOrEmpty(SelectedTdmsFile?.FullPath));

        // 运行时诊断：输出 TDMS 原生库可用性
        try
        {
            var tdmsAvail = DH.Client.App.Services.Storage.TdmsNative.IsAvailable;
            Console.WriteLine($"[TDMS] nilibddc.dll available: {tdmsAvail}");
            if (!tdmsAvail)
            {
                StorageStatusMessage = "TDMS库未检测到，存储不可用（请放置 nilibddc.dll 到应用目录或配置 PATH）";
            }
            else
            {
                StorageStatusMessage = "TDMS库已检测到（写入将生成 .tdms）";
            }
        }
        catch { /* ignore */ }

        // ==================== SDK模式初始化 ====================
        InitializeSdkSupport();
    }

    /// <summary>
    /// 初始化SDK支持
    /// </summary>
    private void InitializeSdkSupport()
    {
        // 设置默认SDK配置路径（尝试查找Config文件夹）
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(basePath, "Config");
        if (Directory.Exists(configPath))
        {
            SdkConfigPath = configPath;
        }
        else
        {
            SdkConfigPath = basePath;
        }

        // 创建SDK驱动管理器
        _sdkDriverManager = new SdkDriverManager(_bus, _table, OnSdkStatusChanged);
        _sdkDriverManager.DataActivityChanged += active => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                IsSdkDataActive = active;
                OnPropertyChanged(nameof(SdkStatusColor));
                
                // SDK数据到达时更新通道状态
                if (active && DataSourceMode == 1 && Devices != null) // SDK模式
                {
                    foreach (var dev in Devices.ToList()) // 使用ToList()避免并发修改
                    {
                        if (dev.Channels != null)
                        {
                            int cnt = dev.Channels.Count(c => c.Online);
                            dev.OnlineChannelCount = cnt;
                            dev.Online = cnt > 0;
                        }
                    }
                    OnPropertyChanged(nameof(OnlineChannelStatus));
                    UpdateDeviceChannels();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SDK] DataActivityChanged处理异常: {ex.Message}");
            }
        });

        // SDK模式下，DataBus数据更新事件
        _bus.DataUpdated += (_, e) =>
        {
            if (DataSourceMode == 1 && IsSdkSampling && IsSdkDataActive) // SDK模式
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var ci = Channels?.FirstOrDefault(c => c.ChannelId == e.ChannelId);
                        if (ci != null) ci.Online = true;
                        _onlineChannelManager?.SetChannelOnline(e.ChannelId, true);
                        
                        if (Devices != null)
                        {
                            foreach (var dev in Devices.ToList()) // 使用ToList()避免并发修改
                            {
                                if (dev.Channels != null)
                                {
                                    int cnt = dev.Channels.Count(c => c.Online);
                                    dev.OnlineChannelCount = cnt;
                                    dev.Online = cnt > 0;
                                }
                            }
                        }
                        
                        OnPropertyChanged(nameof(OnlineChannelStatus));
                        UpdateDeviceChannels();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SDK] DataUpdated处理异常: {ex.Message}");
                    }
                });
            }
        };

        // SDK命令初始化
        InitializeSdkCommand = new RelayCommand(InitializeSdk, () => !IsSdkInitialized);
        StartSdkSamplingCommand = new RelayCommand(StartSdkSampling, () => IsSdkInitialized && !IsSdkSampling);
        StopSdkSamplingCommand = new RelayCommand(StopSdkSampling, () => IsSdkSampling);
        BrowseSdkConfigCommand = new AsyncRelayCommand(BrowseSdkConfigAsync);
    }

    /// <summary>
    /// SDK状态变更回调
    /// </summary>
    private void OnSdkStatusChanged(bool isConnected, string status)
    {
        Console.WriteLine($"[SDK] 状态更新: {status}, 已连接: {isConnected}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SdkConnectionStatus = status;
            OnPropertyChanged(nameof(SdkStatusColor));
            
            // 更新命令可用性
            (InitializeSdkCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    /// <summary>
    /// 初始化SDK
    /// </summary>
    private void InitializeSdk()
    {
        if (_sdkDriverManager == null) return;
        
        string configPath = SdkConfigPath;
        if (string.IsNullOrEmpty(configPath))
        {
            SdkConnectionStatus = "请先设置配置路径";
            return;
        }
        
        if (!Directory.Exists(configPath))
        {
            SdkConnectionStatus = $"配置路径不存在: {configPath}";
            return;
        }
        
        bool result = _sdkDriverManager.Initialize(configPath);
        IsSdkInitialized = result;
        
        if (result)
        {
            // 根据SDK返回的设备信息更新UI
            UpdateDevicesFromSdk();
            
            // 更新采样率
            SampleRate = (int)_sdkDriverManager.SampleRate;
        }
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (InitializeSdkCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    
    /// <summary>
    /// 根据SDK返回的设备信息更新Devices集合
    /// </summary>
    private void UpdateDevicesFromSdk()
    {
        if (_sdkDriverManager == null) return;
        
        var sdkDevices = _sdkDriverManager.DeviceInfoList;
        int onlineDeviceCount = _sdkDriverManager.OnlineDeviceCount;
        int totalChannelCount = _sdkDriverManager.TotalChannelCount;
        
        Console.WriteLine($"[SDK] 更新设备信息: 在线设备={onlineDeviceCount}, 总通道数={totalChannelCount}");
        
        // 清空现有设备
        Devices.Clear();
        Channels.Clear();
        
        // 收集所有在线通道ID
        var onlineChannelIds = new List<int>();
        
        // 只添加在线且有通道的设备
        foreach (var sdkDev in sdkDevices.Where(d => d.IsOnline && d.ChannelCount > 0))
        {
            var dev = new DeviceInfo { DeviceId = sdkDev.DeviceIndex + 1 }; // UI显示从1开始，使用设备索引
            dev.Online = sdkDev.IsOnline;
            dev.OnlineChannelCount = sdkDev.ChannelCount;
            
            // 为该设备创建通道
            for (int ch = 1; ch <= sdkDev.ChannelCount; ch++)
            {
                // 使用 MachineId 构建通道ID（与SDK回调一致）
                int channelId = sdkDev.MachineId * 100 + ch;
                var channelInfo = new ChannelInfo
                {
                    ChannelId = channelId,
                    Name = DH.Contracts.ChannelNaming.ChannelName(channelId),
                    Online = sdkDev.IsOnline
                };
                Channels.Add(channelInfo);
                dev.Channels.Add(channelInfo);
                
                // 添加到在线通道列表
                if (sdkDev.IsOnline)
                {
                    onlineChannelIds.Add(channelId);
                }
            }
            
            Devices.Add(dev);
            Console.WriteLine($"[SDK] 添加设备: DeviceId={dev.DeviceId}, MachineId={sdkDev.MachineId}, 通道数={sdkDev.ChannelCount}, 在线={sdkDev.IsOnline}");
        }
        
        // 同步在线通道到OnlineChannelManager（供结果显示页面使用）
        _onlineChannelManager.SetOnlineChannels(onlineChannelIds.ToArray());
        Console.WriteLine($"[SDK] 已同步 {onlineChannelIds.Count} 个在线通道到OnlineChannelManager");
        
        // 更新选中设备
        if (Devices.Count > 0)
        {
            SelectedDeviceId = Devices[0].DeviceId;
        }
        
        OnPropertyChanged(nameof(OnlineChannelStatus));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDeviceTitle));
        UpdateDeviceChannels();
    }

    /// <summary>
    /// 启动SDK采样
    /// </summary>
    private void StartSdkSampling()
    {
        if (_sdkDriverManager == null || !IsSdkInitialized) return;
        
        bool result = _sdkDriverManager.StartSampling();
        IsSdkSampling = result;
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 停止SDK采样
    /// </summary>
    private void StopSdkSampling()
    {
        if (_sdkDriverManager == null) return;
        
        _sdkDriverManager.StopSampling();
        IsSdkSampling = false;
        IsSdkDataActive = false;
        
        // 清除在线状态
        foreach (var c in Channels) c.Online = false;
        foreach (var dev in Devices)
        {
            dev.OnlineChannelCount = 0;
            dev.Online = false;
        }
        UpdateDeviceChannels();
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 浏览SDK配置文件夹
    /// </summary>
    private async Task BrowseSdkConfigAsync()
    {
        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            
            if (topLevel == null) return;
            
            var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择SDK配置文件夹（包含Config的目录）",
                AllowMultiple = false
            });
            
            if (folder.Count > 0)
            {
                SdkConfigPath = folder[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SDK] 浏览文件夹异常: {ex.Message}");
        }
    }

    private void BuildDevices()
    {
        Devices.Clear();
        int deviceCount = 64;  // 支持0-63共64台设备
        int channelsPerDevice = 64;
        // 从设备0开始，支持SDK的nGroupID从0开始的情况
        for (int d = 0; d < deviceCount; d++)
        {
            var dev = new DeviceInfo { DeviceId = d };
            for (int idx = 1; idx <= channelsPerDevice; idx++)
            {
                int id = d * 100 + idx;  // 设备0: 1-64, 设备1: 101-164
                var ch = Channels.FirstOrDefault(c => c.ChannelId == id);
                if (ch != null)
                {
                    dev.Channels.Add(ch);
                }
            }
            dev.OnlineChannelCount = dev.Channels.Count(c => c.Online);
            dev.Online = dev.OnlineChannelCount > 0;
            Devices.Add(dev);
        }
    }

    private static int MapPortToDevice(int port)
    {
        int basePort = 4008;
        int dev = port - basePort + 1;
        if (dev < 1) dev = 1;
        if (dev > 64) dev = 64;
        return dev;
    }

    private void OnTcpStatusChanged(bool isConnected, string status)
    {
        Console.WriteLine($"TCP状态更新: {status}, 连接: {isConnected}");
        // 确保在UI线程上更新属性
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTcpConnected = isConnected;
            TcpConnectionStatus = status;

            if (!isConnected)
            {
                IsDataVerified = false;
                IsDataActive = false;
            }

            // 断开连接时清空在线通道，避免离线显示曲线
            if (!isConnected)
            {
                _onlineChannelManager.SetOnlineChannels(Array.Empty<int>());
            }

            // 通知命令的可执行状态变化
            (ConnectTcpCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DisconnectTcpCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Console.WriteLine($"TCP状态更新: {status}, 连接: {isConnected}");
        });
    }

    private void ConnectTcp()
    {
        if (int.TryParse(TcpServerPort, out var port))
        {
            SelectedDeviceId = MapPortToDevice(port);
            _tcpDriverManager.Connect(TcpServerIp, port);
            OnPropertyChanged(nameof(SelectedDevice));
            OnPropertyChanged(nameof(SelectedDeviceTitle));
        }
    }

    private void DisconnectTcp()
    {
        Console.WriteLine("[MainWindowViewModel] TCP disconnected 1111 ");
        _tcpDriverManager.Disconnect();
        Console.WriteLine("[MainWindowViewModel] TCP disconnected 2222");
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null) dev.Online = false;
    }

    private void SendTestPacket()
    {
        if (!_tcpDriverManager.IsConnected) return;
        int pktCount = 128;
        var ch1 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Sin(2 * Math.PI * i / pktCount)).ToArray();
        var ch2 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Cos(2 * Math.PI * i / pktCount)).ToArray();
        var channels = new[] { ch1, ch2 };
        var names = new[] { "AI1-01,mV", "AI1-02,mV" };
        _tcpDriverManager.SendTimeSeriesPacket((ulong)pktCount, channels, names, DateTime.UtcNow);
    }

    private void StartLocalServer()
    {
        if (_localServer != null) return;
        if (!int.TryParse(TcpServerPort, out var port)) return;
        _localServer = new LocalTestServer("127.0.0.1", port);
        _localServer.Start();
    }

    private void StopLocalServer()
    {
        _localServer?.Stop();
        _localServer = null;
    }

    

    private void ApplyAlgo()
    {
        _algo = new MovingAverageAlgorithm(MaWindow);
        // 同步到曲线视图的全局可视化移动平均设置
        SkiaMultiChannelView.SetGlobalMovingAverage(true, MaWindow);
        Console.WriteLine($"[MainWindowViewModel] Algorithm applied with window size: {MaWindow}");
    }

    private void OnOnlineChannelsChanged(object sender, OnlineChannelsChangedEventArgs e)
    {
        // 事件可能来自后台线程，调度到UI线程更新绑定
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var set = new HashSet<int>(e.OnlineChannels);
            foreach (var channel in Channels)
            {
                channel.Online = set.Contains(channel.ChannelId);
            }
            foreach (var dev in Devices)
            {
                int cnt = dev.Channels.Count(c => c.Online);
                dev.OnlineChannelCount = cnt;
                dev.Online = cnt > 0;
            }
            
            OnPropertyChanged(nameof(OnlineChannelStatus));
            UpdateDeviceChannels();
        });
    }

    public class DeviceInfo : ObservableObject
    {
        public int DeviceId { get; init; }
        public ObservableCollection<ChannelInfo> Channels { get; } = new();
        private bool _online;
        public bool Online { get => _online; set => SetProperty(ref _online, value); }
        private int _onlineChannelCount;
        public int OnlineChannelCount { get => _onlineChannelCount; set => SetProperty(ref _onlineChannelCount, value); }
    }

    public class ChannelStatus : ObservableObject
    {
        public string DeviceId { get; set; } = string.Empty;
        public int ChannelNumber { get; set; }
        private bool _isOnline;
        public bool IsOnline 
        { 
            get => _isOnline; 
            set 
            {
                if (SetProperty(ref _isOnline, value))
                {
                    if (value && !_onlineStartTime.HasValue)
                    {
                        // 变为在线状态，开始计时
                        _onlineStartTime = DateTimeOffset.UtcNow;
                        OnPropertyChanged(nameof(OnlineTimeText));
                    }
                    else if (!value)
                    {
                        // 变为离线状态，重置计时
                        _onlineStartTime = null;
                        OnPropertyChanged(nameof(OnlineTimeText));
                    }
                }
            }
        }
        private DateTimeOffset _lastActiveTime;
        public DateTimeOffset LastActiveTime { get => _lastActiveTime; set => SetProperty(ref _lastActiveTime, value); }
        
        // 在线开始时间
        private DateTimeOffset? _onlineStartTime;
        
        // 计算在线时长文本（格式：HH:MM:SS）
        public string OnlineTimeText
        {
            get
            {
                if (_isOnline && _onlineStartTime.HasValue)
                {
                    var duration = DateTimeOffset.UtcNow - _onlineStartTime.Value;
                    return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
                }
                return "00:00:00";
            }
        }
        
        // 更新在线时长显示（由外部定时器调用）
        public void UpdateOnlineTime()
        {
            if (_isOnline && _onlineStartTime.HasValue)
            {
                OnPropertyChanged(nameof(OnlineTimeText));
            }
        }
    }

    private void EnsureDeviceChannelStatuses(string deviceId)
    {
        if (DeviceChannels.Count != 64 || DeviceChannels.FirstOrDefault()?.DeviceId != deviceId)
        {
            DeviceChannels.Clear();
            for (int i = 1; i <= 64; i++)
            {
                DeviceChannels.Add(new ChannelStatus { DeviceId = deviceId, ChannelNumber = i, IsOnline = false, LastActiveTime = DateTimeOffset.MinValue });
            }
        }
    }

    private void UpdateDeviceChannels()
    {
        try
        {
            var devIdText = $"AI{SelectedDeviceId:D2}";
            
            // SDK模式下使用不同的逻辑
            if (DataSourceMode == 1 && IsSdkInitialized)
            {
                UpdateDeviceChannelsForSdk();
                return;
            }
            
            // TCP模式
            EnsureDeviceChannelStatuses(devIdText);
            var access = _tcpDriverManager.GetChannelAccessTimes();
            var now = DateTimeOffset.UtcNow;
            var deviceChannels = access
                .Select(kv =>
                {
                    var parsed = ChannelIdentifierExtensions.ParseCanonicalKey(kv.Key);
                    if (!parsed.HasValue || parsed.Value.DeviceId != devIdText) return ((int, DateTimeOffset)?)null;
                    return (parsed.Value.ChannelNumber, kv.Value);
                })
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToList();

            foreach (var ch in DeviceChannels)
            {
                var found = deviceChannels.FirstOrDefault(dc => dc.Item1 == ch.ChannelNumber);
                if (found != default)
                {
                    ch.LastActiveTime = found.Item2;
                    ch.IsOnline = (now - found.Item2).TotalSeconds < 5;
                }
                else
                {
                    ch.IsOnline = false;
                }
            }

            OnlineChannels.Clear();
            foreach (var ch in DeviceChannels.Where(c => c.IsOnline)) OnlineChannels.Add(ch);

            // 更新所有设备的在线统计
            var parsedAll = access
                .Select(kv => ChannelIdentifierExtensions.ParseCanonicalKey(kv.Key))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
            var groups = parsedAll.GroupBy(p => p.DeviceId);
            foreach (var g in groups)
            {
                int devNum = int.TryParse(new string(g.Key.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
                var devInfo = Devices.FirstOrDefault(d => d.DeviceId == devNum);
                if (devInfo != null)
                {
                    // 在线判断：5秒内活跃计数
                    int cnt = g.Count(x => (now - access[$"{GetEndpointText()}/{g.Key}/CH{x.ChannelNumber:D2}"]).TotalSeconds < 5);
                    devInfo.OnlineChannelCount = cnt;
                    devInfo.Online = cnt > 0;
                }
            }
        }
        catch { /* ignore */ }
    }
    
    /// <summary>
    /// SDK模式下更新设备通道状态
    /// </summary>
    private void UpdateDeviceChannelsForSdk()
    {
        try
        {
            // 安全检查：确保Devices集合非空
            if (Devices == null || Devices.Count == 0)
            {
                DeviceChannels.Clear();
                OnlineChannels.Clear();
                return;
            }
            
            // 找到当前选中的设备
            var selectedDevice = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
            if (selectedDevice == null)
            {
                // 如果找不到选中的设备，选择第一个
                selectedDevice = Devices.FirstOrDefault();
                if (selectedDevice != null)
                {
                    SelectedDeviceId = selectedDevice.DeviceId;
                }
            }
            
            if (selectedDevice == null)
            {
                DeviceChannels.Clear();
                OnlineChannels.Clear();
                return;
            }
            
            var devIdText = $"AI{SelectedDeviceId:D2}";
            
            // 快照设备通道列表，避免并发修改
            var deviceChannelSnapshot = selectedDevice.Channels?.ToList();
            
            // 获取该设备的通道数量
            int channelCount = deviceChannelSnapshot?.Count ?? 0;
            if (channelCount == 0) channelCount = 16; // 默认16通道
            
            // 确保DeviceChannels有正确数量的通道
            bool needRebuild = DeviceChannels.Count != channelCount;
            if (!needRebuild && DeviceChannels.Count > 0)
            {
                var firstCh = DeviceChannels.FirstOrDefault();
                needRebuild = firstCh?.DeviceId != devIdText;
            }
            
            if (needRebuild)
            {
                DeviceChannels.Clear();
                for (int i = 1; i <= channelCount; i++)
                {
                    // 检查该通道在Channels集合中的在线状态
                    var channelInfo = deviceChannelSnapshot?.FirstOrDefault(c => c.ChannelId % 100 == i);
                    bool isOnline = channelInfo?.Online ?? selectedDevice.Online;
                    
                    DeviceChannels.Add(new ChannelStatus 
                    { 
                        DeviceId = devIdText, 
                        ChannelNumber = i, 
                        IsOnline = isOnline, 
                        LastActiveTime = isOnline ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue 
                    });
                }
            }
            else
            {
                // 更新现有通道的在线状态
                foreach (var ch in DeviceChannels.ToList()) // 使用ToList()避免枚举时修改
                {
                    var channelInfo = deviceChannelSnapshot?.FirstOrDefault(c => c.ChannelId % 100 == ch.ChannelNumber);
                    ch.IsOnline = channelInfo?.Online ?? selectedDevice.Online;
                    if (ch.IsOnline)
                    {
                        ch.LastActiveTime = DateTimeOffset.UtcNow;
                    }
                }
            }
            
            // 更新在线通道列表：先收集再批量更新，减少UI中间状态
            var onlineList = DeviceChannels.Where(c => c.IsOnline).ToList();
            OnlineChannels.Clear();
            foreach (var ch in onlineList)
            {
                OnlineChannels.Add(ch);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SDK] UpdateDeviceChannelsForSdk异常: {ex.Message}");
        }
    }

    private string GetEndpointText()
    {
        try
        {
            var ip = IPAddress.Parse(TcpServerIp);
            if (int.TryParse(TcpServerPort, out var port))
            {
                var ep = new IPEndPoint(ip, port);
                return ep.ToString();
            }
        }
        catch { }
        return "127.0.0.1:0";
    }

    // 批量通道管理方法
    private void SetAllOnline()
    {
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null)
        {
            foreach (var channel in dev.Channels)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetAllOffline()
    {
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null)
        {
            foreach (var channel in dev.Channels)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetCh1To32Online()
    {
        var devId = SelectedDeviceId;
        for (int i = 1; i <= 32; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        for (int i = 33; i <= 64; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetCh33To64Online()
    {
        var devId = SelectedDeviceId;
        for (int i = 1; i <= 32; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        for (int i = 33; i <= 64; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }

    // 公开在线通道管理器，供UI绑定
    public OnlineChannelManager OnlineChannelManager => _onlineChannelManager;
    
    // 采样频率变更事件
    public event EventHandler<int>? SampleRateChanged;

    // 存储控制方法
    // 解析存储路径：绝对路径直接返回；相对路径相对于仓库根（包含 DH.sln）
    private static string ResolveStoragePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var root = GetRepoRoot() ?? Environment.CurrentDirectory;
                return Path.Combine(root, "data");
            }
            if (Path.IsPathRooted(path)) return path;
            var baseDir = GetRepoRoot() ?? Environment.CurrentDirectory;
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static string? GetRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var sln = Path.Combine(dir.FullName, "DH.sln");
                if (File.Exists(sln)) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private async Task StartStorageAsync()
    {
        if (StorageEnabled) return;
        var basePath = ResolveStoragePath(StoragePath);
        Directory.CreateDirectory(basePath);
        _storage = StorageMode == StorageModeOption.SingleFile
            ? new TdmsSingleFileStorage() as ITdmsStorage
            : new TdmsPerChannelStorage() as ITdmsStorage;
        var channelIds = Channels.Select(c => c.ChannelId).ToArray();
        await Task.Run(() =>
        {
            try
            {
                // 启动前检查上次是否异常退出
                var recovery = StorageGuard.CheckRecovery(basePath);
                if (recovery != null)
                {
                    Console.WriteLine($"[StorageGuard] 检测到上次异常退出: {recovery.ToUserMessage()}");
                    StorageGuard.ClearRecovery(basePath);
                }

                _storage!.Start(basePath, channelIds, StorageSessionName, SampleRate, SelectedCompressionType, SelectedPreprocessType, _compressionOptions.Clone());
                
                // 激活断电保护：周期性刷盘 + 进程退出钩子
                StorageGuard.Activate(_storage, basePath, StorageSessionName);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _bus.DataUpdated += OnDataUpdatedForStorage;
                    StorageEnabled = true;
                    // 启动写入计时器
                    _storageStartTime = DateTime.Now;
                    StorageElapsed = "00:00:00";
                    _storageTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _storageTimer.Tick += (_, _) =>
                    {
                        var elapsed = DateTime.Now - _storageStartTime;
                        StorageElapsed = elapsed.ToString(@"hh\:mm\:ss");
                    };
                    _storageTimer.Start();
                    var compressionStatus = SelectedCompressionType != CompressionType.None 
                        ? $"，{SelectedCompressionType}压缩已启用" 
                        : "";
                    var preprocessStatus = SelectedPreprocessType != PreprocessType.None
                        ? $"，{SelectedPreprocessType}预处理已启用"
                        : "";
                    var recoveryHint = recovery != null ? " ⚠️ 已恢复上次异常中断的数据" : "";
                    StorageStatusMessage = $"写入已开始（{(StorageMode == StorageModeOption.SingleFile ? "单文件" : "多文件/每通道")}{compressionStatus}{preprocessStatus}），目录：{basePath}{recoveryHint}";
                    (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Start failed: {ex.Message}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _storage = null;
                    StorageEnabled = false;
                    StorageStatusMessage = $"写入启动失败: {ex.Message}";
                    (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                });
            }
        });
    }

    private void StopStorage()
    {
        if (!StorageEnabled) return;
        
        // 停止写入计时器
        _storageTimer?.Stop();
        _storageTimer = null;
        var finalElapsed = DateTime.Now - _storageStartTime;
        StorageElapsed = finalElapsed.ToString(@"hh\:mm\:ss");

        // 先标记为未启用，防止新的写入
        StorageEnabled = false;
        
        // 取消事件订阅
        _bus.DataUpdated -= OnDataUpdatedForStorage;
        
        // 停用断电保护（正常停止路径）
        StorageGuard.Deactivate();
        
        try 
        { 
            _storage?.Flush();

            // 在 Stop 之前抓取写入期间的 SHA-256 指纹
            var hashes = _storage?.GetWriteHashes();
            var counts = _storage?.GetWriteSampleCounts();
            _lastWrittenFiles = _storage?.GetWrittenFiles();

            // 将哈希与文件路径关联，支持后续手动验证任意文件
            if (_lastWrittenFiles != null && hashes != null && counts != null)
            {
                foreach (var fp in _lastWrittenFiles)
                {
                    _writeHashesByFile[fp] = hashes;
                    _writeSampleCountsByFile[fp] = counts;

                    // ★ 持久化 SHA-256 指纹到 .sha256 文件
                    try
                    {
                        StorageVerifier.SaveManifest(fp, hashes, counts);
                    }
                    catch (Exception saveEx)
                    {
                        Console.WriteLine($"[Storage] 保存 SHA-256 清单失败: {saveEx.Message}");
                    }
                }
            }

            _storage?.Stop();
            _storage?.Dispose(); // 确保释放所有资源
            
            // ★ 将文件整理到以时间命名的文件夹中
            var (organizedFolder, newPaths) = StorageGuard.OrganizeToTimestampFolder(_lastWrittenFiles);
            if (organizedFolder != null && newPaths.Count > 0)
            {
                // 更新文件路径引用为新位置
                _lastWrittenFiles = newPaths;
                
                // 重新关联哈希到新路径
                var newHashByFile = new Dictionary<string, IReadOnlyDictionary<string, string>>();
                var newCountByFile = new Dictionary<string, IReadOnlyDictionary<string, long>>();
                foreach (var np in newPaths)
                {
                    if (hashes != null) newHashByFile[np] = hashes;
                    if (counts != null) newCountByFile[np] = counts;
                }
                foreach (var kv in newHashByFile) _writeHashesByFile[kv.Key] = kv.Value;
                foreach (var kv in newCountByFile) _writeSampleCountsByFile[kv.Key] = kv.Value;
                
                StorageStatusMessage = $"写入已停止，文件已整理到: {Path.GetFileName(organizedFolder)}，正在自动验证…";
            }
            else
            {
                StorageStatusMessage = "写入已停止，正在自动验证文件无损性…";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Storage] 停止写入时出错: {ex.Message}");
            StorageStatusMessage = $"停止写入时出错: {ex.Message}";
        }
        finally 
        { 
            _storage = null; 
        }
        
        RefreshRecentFiles();
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();

        // 自动执行文件无损验证
        _ = AutoVerifyAfterStopAsync();
    }

    /// <summary>停止写入后自动验证最近写入的文件</summary>
    private async Task AutoVerifyAfterStopAsync()
    {
        if (_lastWrittenFiles == null || _lastWrittenFiles.Count == 0)
        {
            FileVerifyResult = "";
            return;
        }

        try
        {
            var allResults = new List<string>();
            bool allPassed = true;

            foreach (var file in _lastWrittenFiles)
            {
                if (!File.Exists(file)) continue;
                _writeHashesByFile.TryGetValue(file, out var hashes);
                _writeSampleCountsByFile.TryGetValue(file, out var counts);

                // 内存中没有哈希时，尝试从 .sha256 清单文件加载
                if (hashes == null || hashes.Count == 0)
                {
                    var (loadedHashes, loadedCounts) = StorageVerifier.LoadManifest(file);
                    hashes ??= loadedHashes;
                    counts ??= loadedCounts;
                }

                var result = await Task.Run(() => StorageVerifier.Verify(file, hashes, counts));
                allResults.Add(result.Summary);
                if (!result.AllLossless) allPassed = false;
            }

            FileVerifyPassed = allPassed;
            FileVerifyResult = allResults.Count > 0
                ? string.Join("\n───────────────────\n", allResults)
                : "未找到可验证的文件";
            StorageStatusMessage = allPassed ? "写入已停止 ✅ 自动验证通过" : "写入已停止 ❌ 自动验证发现差异";
        }
        catch (Exception ex)
        {
            FileVerifyPassed = false;
            FileVerifyResult = $"自动验证异常: {ex.Message}";
        }
    }

    private void OnDataUpdatedForStorage(object? sender, DataUpdateEventArgs e)
    {
        try
        {
            // 检查存储是否已启用（可能在写入过程中被停止）
            if (!StorageEnabled || _storage == null) return;
            
            // 检查通道是否在线
            if (!_onlineChannelManager.IsChannelOnline(e.ChannelId)) return;
            
            var list = e.Data;
            if (list == null || list.Count == 0) return;
            
            var arr = new double[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i].Y;
            _storage.Write(e.ChannelId, arr);
        }
        catch (Exception ex)
        {
            // 记录错误但不中断数据流
            Console.WriteLine($"[Storage] 写入通道 {e.ChannelId} 数据时出错: {ex.Message}");
            // 如果是严重错误，考虑停止存储
            if (ex is IOException || ex is ObjectDisposedException)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StorageStatusMessage = $"写入错误: {ex.Message}";
                    // 不自动停止，让用户决定
                });
            }
        }
    }

    partial void OnStorageModeIndexChanged(int value)
    {
        StorageMode = value == 0 ? StorageModeOption.SingleFile : StorageModeOption.PerChannel;
    }

    partial void OnStorageModeChanged(StorageModeOption value)
    {
        StorageModeIndex = value == StorageModeOption.SingleFile ? 0 : 1;
    }
    
    partial void OnStorageEnabledChanged(bool value)
    {
        // 当存储状态改变时，通知命令重新评估可用性
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    // 压缩参数变化处理
    partial void OnLz4LevelChanged(int value) => _compressionOptions.LZ4Level = value;
    partial void OnZstdLevelChanged(int value) => _compressionOptions.ZstdLevel = value;
    partial void OnZstdWindowLogChanged(int value) => _compressionOptions.ZstdWindowLog = value;
    partial void OnBrotliQualityChanged(int value) => _compressionOptions.BrotliQuality = value;
    partial void OnBrotliWindowBitsChanged(int value) => _compressionOptions.BrotliWindowBits = value;
    partial void OnZlibLevelChanged(int value) => _compressionOptions.ZlibLevel = value;
    partial void OnBzip2BlockSizeChanged(int value) => _compressionOptions.BZip2BlockSize = value;
    partial void OnLz4hcLevelChanged(int value) => _compressionOptions.LZ4HCLevel = value;

    /// <summary>手动验证选中的 TDMS 文件（使用上次写入的哈希或仅回读验证）</summary>
    private async Task VerifyStoredFileAsync()
    {
        var filePath = SelectedTdmsFile?.FullPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            FileVerifyResult = "请先从列表中选择一个文件";
            FileVerifyPassed = false;
            return;
        }

        FileVerifyResult = "正在验证…";
        try
        {
            _writeHashesByFile.TryGetValue(filePath, out var hashes);
            _writeSampleCountsByFile.TryGetValue(filePath, out var counts);

            // 内存中没有哈希时，尝试从 .sha256 清单文件加载
            if (hashes == null || hashes.Count == 0)
            {
                var (loadedHashes, loadedCounts) = StorageVerifier.LoadManifest(filePath);
                hashes ??= loadedHashes;
                counts ??= loadedCounts;
            }

            var result = await Task.Run(() =>
                StorageVerifier.Verify(filePath, hashes, counts));
            FileVerifyPassed = result.AllLossless;
            FileVerifyResult = result.Summary;
        }
        catch (Exception ex)
        {
            FileVerifyPassed = false;
            FileVerifyResult = $"验证异常: {ex.Message}";
        }
    }

    partial void OnSelectedTdmsFileChanged(TdmsFileItem? value)
    {
        (TestReadSelectedFileCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (VerifyStoredFileCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RefreshRecentFiles()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(StoragePath)) return;
            var path = ResolveStoragePath(StoragePath);
            Directory.CreateDirectory(path);
            // 递归搜索子目录（文件整理后 .tdms 位于时间命名的子文件夹中）
            var tdmsFiles = Directory.EnumerateFiles(path, "*.tdms", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase));
            var tdmFiles = Directory.EnumerateFiles(path, "*.tdm", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase));
            var binFiles = Directory.EnumerateFiles(path, "*.bin", SearchOption.AllDirectories);
            // 也收集公共文档下的 TDMS/TDM（ASCII 路径回退时产生）
            var altBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "DH", "TDMS");
            var altTdmsFiles = Directory.Exists(altBase)
                ? Directory.EnumerateFiles(altBase, "*.tdms", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
            var altTdmFiles = Directory.Exists(altBase)
                ? Directory.EnumerateFiles(altBase, "*.tdm", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
            var files = tdmsFiles.Concat(tdmFiles).Concat(binFiles).Concat(altTdmsFiles).Concat(altTdmFiles)
                .Select(fp => new FileInfo(fp))
                .Where(fi => fi.Exists)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(20)
                .Select(fi => new TdmsFileItem(fi))
                .ToArray();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecentTdmsFiles.Clear();
                foreach (var f in files) RecentTdmsFiles.Add(f);
            });
        }
        catch (Exception ex)
        {
            StorageStatusMessage = $"刷新列表失败: {ex.Message}";
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(StoragePath)) return;
            var fullPath = ResolveStoragePath(StoragePath);
            Directory.CreateDirectory(fullPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{fullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StorageStatusMessage = $"打开目录失败: {ex.Message}";
        }
    }

    private void TestReadSelectedFile()
    {
        var fp = SelectedTdmsFile?.FullPath;
        if (string.IsNullOrEmpty(fp)) return;
        try
        {
            var map = TdmsReaderUtil.ListGroupsAndChannels(fp);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"文件: {Path.GetFileName(fp)}");
            foreach (var kv in map)
            {
                sb.AppendLine($"组: {kv.Key} 通道: {string.Join(", ", kv.Value)}");
            }
            var g = map.Keys.FirstOrDefault();
            var ch = g != null ? map[g].FirstOrDefault() : null;
            if (g != null && ch != null)
            {
                var data = TdmsReaderUtil.ReadChannelData(fp, g, ch);
                var sample = string.Join(", ", data.Take(10).Select(v => v.ToString("F3")));
                sb.AppendLine($"示例({g}/{ch}): {sample}");
            }
            FileVerifyResult = sb.ToString();
            FileVerifyPassed = true;
        }
        catch (Exception ex)
        {
            FileVerifyResult = $"读取失败: {ex.Message}";
            FileVerifyPassed = false;
        }
    }

    private void OnSampleRateChangedCommand(int newSampleRate)
    {
        // 确保采样频率在合理范围内 (100-10000 Hz)
        if (newSampleRate < 100 || newSampleRate > 10000)
            return;

        SampleRate = newSampleRate;
        Console.WriteLine($"[MainWindowViewModel] Sample rate changed to: {SampleRate} Hz");
        
        // 通知所有曲线面板更新采样频率
        UpdateAllCurvePanelsSampleRate();
        
        // 如果模拟数据正在运行，重启它以应用新的采样频率
        
    }
    
    // 更新所有曲线面板的采样频率
    private void UpdateAllCurvePanelsSampleRate()
    {
        // 这个方法将在主窗口中实现，通过事件或消息机制通知所有CurvePanel
        SampleRateChanged?.Invoke(this, SampleRate);
    }

    partial void OnSelectedDeviceIdChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDeviceTitle));
    }

    private sealed class LocalTestServer
    {
        private readonly string _ip;
        private readonly int _port;
        private TcpListener? _listener;
        private Thread? _thread;
        private CancellationTokenSource? _cts;
        public LocalTestServer(string ip, int port) { _ip = ip; _port = port; }
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Parse(_ip), _port);
            _listener.Start();
            _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true };
            _thread.Start();
        }
        public void Stop()
        {
            _cts?.Cancel();
            try { _thread?.Join(500); } catch { }
            _listener?.Stop();
            _thread = null;
            _listener = null;
            _cts = null;
        }
        private void Run(CancellationToken ct)
        {
            using var client = _listener!.AcceptTcpClient();
            using var stream = client.GetStream();
            int pktCount = 128;
            var names = new[] { "AI1-01,mV", "AI1-02,mV" };
            var rand = new Random();
            ulong total = 0;
            while (!ct.IsCancellationRequested)
            {
                var ch1 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Sin(2 * Math.PI * i / pktCount)).ToArray();
                var ch2 = Enumerable.Range(0, pktCount).Select(i => (float)(Math.Cos(2 * Math.PI * i / pktCount) + 0.05 * (rand.NextDouble() - 0.5))).ToArray();
                var packet = BuildPacket(total, new[] { ch1, ch2 }, names, DateTime.UtcNow);
                stream.Write(packet, 0, packet.Length);
                total += (ulong)pktCount;
                Thread.Sleep(50);
            }
        }
        private static byte[] BuildPacket(ulong total, float[][] channels, string[] channelNames, DateTime timestampUtc)
        {
            int chCount = channels.Length;
            int pktCount = channels[0].Length;
            var payload = new List<byte>();
            void WLE(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); payload.AddRange(b); }
            WLE(BitConverter.GetBytes(total));
            WLE(BitConverter.GetBytes((uint)pktCount));
            WLE(BitConverter.GetBytes((uint)chCount));
            for (int p = 0; p < pktCount; p++) for (int c = 0; c < chCount; c++) WLE(BitConverter.GetBytes(channels[c][p]));
            var namesStr = string.Join("|", channelNames);
            var nameBytes = Encoding.ASCII.GetBytes(namesStr);
            WLE(BitConverter.GetBytes((uint)nameBytes.Length));
            payload.AddRange(nameBytes);
            var dto = new DateTimeOffset(timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime());
            ulong epochSec = (ulong)dto.ToUnixTimeSeconds();
            long ticksInSec = timestampUtc.Ticks % TimeSpan.TicksPerSecond;
            uint usec = (uint)(ticksInSec / 10);
            WLE(BitConverter.GetBytes(epochSec));
            WLE(BitConverter.GetBytes(usec));
            uint magic = 0x55AAAA55;
            uint cmd = 0x7C;
            uint len = (uint)payload.Count;
            var packet = new List<byte>(12 + payload.Count);
            void WH(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); packet.AddRange(b); }
            WH(BitConverter.GetBytes(magic));
            WH(BitConverter.GetBytes(cmd));
            WH(BitConverter.GetBytes(len));
            packet.AddRange(payload);
            return packet.ToArray();
        }
    }
    
    // 清理资源（当窗口关闭时调用）
    public void Cleanup()
    {
        _channelTimeUpdateTimer?.Stop();
        _channelTimeUpdateTimer?.Dispose();
        _channelTimeUpdateTimer = null;
    }
}