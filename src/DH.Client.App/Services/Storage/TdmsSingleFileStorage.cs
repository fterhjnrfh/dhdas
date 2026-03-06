using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DH.Client.App.Services.Storage
{
    // TDMS 单文件写入：有 NI DLL 时使用原生 TDMS；否则退化为 Binary 单文件
    // 线程安全：支持SDK回调多线程并发写入
    public class TdmsSingleFileStorage : ITdmsStorage
    {
        private IntPtr _file = IntPtr.Zero;
        private IntPtr _group = IntPtr.Zero;
        private readonly ConcurrentDictionary<int, IntPtr> _channels = new();
        // private readonly BinarySingleFileStorage _fallback = new(); // removed BIN fallback
        private bool _useTdms;
        private volatile bool _started;
        private string? _path;
        private CompressionType _compressionType; // 压缩算法类型
        private PreprocessType _preprocessType; // 预处理技术类型
        private CompressionOptions? _compressionOptions; // 压缩参数
        // TDMS性能优化：按通道缓冲批量写入，减少原生调用次数
        private readonly ConcurrentDictionary<int, List<double>> _buffers = new();
        private const int BatchSize = 4096;
        
        // 记录每个通道已写入的总样本数，用于计算 wf_start_offset
        private readonly ConcurrentDictionary<int, long> _totalSamplesWritten = new();
        
        // SHA-256 增量哈希：写入期间持续累计，用于无损验证
        private readonly ConcurrentDictionary<int, StorageVerifier.IncrementalHasher> _hashers = new();
        // 通道 ID → 通道名映射
        private readonly ConcurrentDictionary<int, string> _channelNames = new();
        
        // 线程同步锁：保护TDMS文件操作（因为TDMS DLL不是线程安全的）
        private readonly object _writeLock = new();

        private double _incrementSeconds = 0.001; // 默认 1kHz

        public void Start(string basePath, IEnumerable<int> channelIds, string sessionName, double sampleRateHz, CompressionType compressionType = CompressionType.None, PreprocessType preprocessType = PreprocessType.None, CompressionOptions? compressionOptions = null)
        {
            Directory.CreateDirectory(basePath);
            _useTdms = TdmsNative.IsAvailable;
            _compressionType = compressionType;
            _preprocessType = preprocessType;
            _compressionOptions = compressionOptions ?? new CompressionOptions();
            if (!_useTdms)
            {
                throw new InvalidOperationException("TDMS库未检测到，无法写入。请将 nilibddc.dll 放置到应用目录或配置 PATH。");
            }

            if (sampleRateHz > 0)
            {
                _incrementSeconds = 1.0 / sampleRateHz;
            }

            // 清理会话名，避免无效文件名字符；并确保目标文件路径唯一/可写
            var safeName = SanitizeName(sessionName);
            _path = Path.Combine(basePath, $"{safeName}.tdms");
            if (File.Exists(_path))
            {
                try { File.Delete(_path); }
                catch { _path = Path.Combine(basePath, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.tdms"); }
            }
            // 清理可能残留的索引文件，避免 USI 认为存储“已存在/损坏”导致 -6211
            var idx = _path + "_index";
            if (File.Exists(idx)) { try { File.Delete(idx); } catch { /* ignore */ } }
            var err = TdmsNative.DDC_CreateFile(_path, "TDMS", SanitizeAscii(safeName), "", SanitizeAscii(safeName), "DH", ref _file);
            if (err != 0)
            {
                // 如果是路径/字符集问题，尝试使用公共文档下的 ASCII 目录重试
                try
                {
                    var asciiBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "DH_ASCII");
                    Directory.CreateDirectory(asciiBase);
                    var asciiPath = Path.Combine(asciiBase, SanitizeAscii(Path.GetFileName(_path)));
                    err = TdmsNative.DDC_CreateFile(asciiPath, "TDMS", SanitizeAscii(safeName), "", SanitizeAscii(safeName), "DH", ref _file);
                    _path = asciiPath;
                }
                catch { /* ignore */ }
                if (err != 0)
                {
                    throw new IOException($"DDC_CreateFile 失败: {err} {TdmsNative.DescribeError(err)}");
                }
            }
            // 修正：DDC_AddChannelGroup 需提供 groupDescription 参数
            err = TdmsNative.DDC_AddChannelGroup(_file, "Session", SanitizeAscii(safeName), ref _group);
            if (err != 0)
            {
                try { if (_file != IntPtr.Zero) TdmsNative.DDC_CloseFile(_file); } catch { }
                _file = IntPtr.Zero;
                throw new IOException($"DDC_AddChannelGroup 失败: {err} {TdmsNative.DescribeError(err)}");
            }

            // 懒创建通道：避免在启动时为所有通道建立结构导致界面卡顿

            _started = true;
        }

        public void Write(int channelId, ReadOnlySpan<double> samples)
        {
            if (!_started || samples.Length == 0) return;
            
            // 线程安全：所有TDMS操作需要加锁
            lock (_writeLock)
            {
                if (!_started) return; // 再次检查，防止在等待锁期间被Stop
                
                if (!_channels.TryGetValue(channelId, out var ch))
                {
                    // 首次写入该通道时创建通道结构
                    string chName = DH.Contracts.ChannelNaming.TdmsChannelName(channelId);
                    int err = TdmsNative.DDC_AddChannel(_group, TdmsNative.DDCDataType.Double, chName, $"Channel {channelId}", "V", ref ch);
                    if (err != 0) throw new IOException($"DDC_AddChannel 懒创建失败 id={channelId}: {err} {TdmsNative.DescribeError(err)}");
                    _channels[channelId] = ch;
                    
                    // 设置波形属性：时间轴配置
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xname", "Time");
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xunit_string", "s");
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_increment", _incrementSeconds);
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_start_offset", 0.0); // 初始偏移为0
                    
                    _buffers[channelId] = new List<double>(BatchSize + 1024);
                    _totalSamplesWritten[channelId] = 0; // 初始化已写入样本数
                    _channelNames[channelId] = chName;
                    _hashers[channelId] = new StorageVerifier.IncrementalHasher();
                }
                if (!_buffers.TryGetValue(channelId, out var buf)) { buf = new List<double>(BatchSize + 1024); _buffers[channelId] = buf; }
                // 批量缓冲，减少调用频率
                var arr = samples.ToArray();
                buf.AddRange(arr);
                if (buf.Count >= BatchSize)
                {
                    FlushChannelBuffer(channelId, ch, buf);
                }
            }
        }

        private void FlushChannelBuffer(int channelId, IntPtr ch, List<double> buf)
        {
            var flushArr = buf.ToArray();
            
            // 记录原始数据的 SHA-256 指纹（在任何变换之前）
            if (_hashers.TryGetValue(channelId, out var hasher))
                hasher.Append(flushArr);
            
            // 使用 StorageCodec 统一处理预处理 + 压缩
            var encoded = StorageCodec.Encode(flushArr, _compressionType, _preprocessType, _compressionOptions);
            
            if (encoded != null)
            {
                var err = TdmsNative.DDC_AppendDataValuesDouble(ch, encoded, (uint)encoded.Length);
                if (err != 0) throw new IOException($"DDC_AppendDataValuesDouble 失败 ch{channelId}: {err} {TdmsNative.DescribeError(err)}");
                
                var preprocessInfo = _preprocessType != PreprocessType.None ? $", 预处理={_preprocessType}" : "";
                Console.WriteLine($"[Codec] CH{channelId}: {flushArr.Length} samples -> {encoded.Length} doubles (压缩={_compressionType}{preprocessInfo})");
            }
            else
            {
                // 不压缩也不预处理，直接写入
                var err = TdmsNative.DDC_AppendDataValuesDouble(ch, flushArr, (uint)flushArr.Length);
                if (err != 0) throw new IOException($"DDC_AppendDataValuesDouble 失败 ch{channelId}: {err} {TdmsNative.DescribeError(err)}");
            }
            
            // 更新已写入样本数统计
            if (!_totalSamplesWritten.ContainsKey(channelId))
                _totalSamplesWritten[channelId] = 0;
            _totalSamplesWritten[channelId] += flushArr.Length;
            
            buf.Clear();
        }

        public void Flush()
        {
            // 线程安全：所有TDMS操作需要加锁
            lock (_writeLock)
            {
                // 仅刷新缓冲，不调用 SaveFile，以减少频繁刷盘开销
                foreach (var kvp in _channels)
                {
                    var id = kvp.Key; var ch = kvp.Value;
                    if (_buffers.TryGetValue(id, out var buf) && buf.Count > 0)
                    {
                        FlushChannelBuffer(id, ch, buf);
                    }
                }
            }
        }

        public void Stop()
        {
            // 线程安全：所有TDMS操作需要加锁
            lock (_writeLock)
            {
                if (_useTdms)
                {
                    // 标记停止，防止新的写入操作
                    _started = false;
                    
                    // 先刷新缓冲，再保存并关闭（注意：FlushInternal不再获取锁）
                    FlushInternal();
                    if (_file != IntPtr.Zero)
                    {
                        try { TdmsNative.DDC_SaveFile(_file); } catch { }
                        try { TdmsNative.DDC_CloseFile(_file); } catch { }
                    }
                    _file = IntPtr.Zero;
                    _group = IntPtr.Zero;
                    _channels.Clear();
                    _buffers.Clear();
                    _totalSamplesWritten.Clear();
                }
            }
        }
        
        // 内部刷新方法（调用前必须已持有_writeLock）
        private void FlushInternal()
        {
            foreach (var kvp in _channels)
            {
                var id = kvp.Key; var ch = kvp.Value;
                if (_buffers.TryGetValue(id, out var buf) && buf.Count > 0)
                {
                    FlushChannelBuffer(id, ch, buf);
                }
            }
        }

        public IReadOnlyList<string> GetWrittenFiles()
        {
            return _path != null ? new[] { _path } : Array.Empty<string>();
        }

        public IReadOnlyDictionary<string, string> GetWriteHashes()
        {
            var dict = new Dictionary<string, string>();
            foreach (var kv in _hashers)
            {
                var name = _channelNames.TryGetValue(kv.Key, out var n) ? n : DH.Contracts.ChannelNaming.ChannelName(kv.Key);
                dict[name] = kv.Value.Finalize();
            }
            return dict;
        }

        public IReadOnlyDictionary<string, long> GetWriteSampleCounts()
        {
            var dict = new Dictionary<string, long>();
            foreach (var kv in _totalSamplesWritten)
            {
                var name = _channelNames.TryGetValue(kv.Key, out var n) ? n : DH.Contracts.ChannelNaming.ChannelName(kv.Key);
                dict[name] = kv.Value;
            }
            return dict;
        }

        public void Dispose() => Stop();

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "session";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            var s = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(s) ? "session" : s;
        }

        private static string SanitizeAscii(string text)
        {
            if (string.IsNullOrEmpty(text)) return "session";
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
                sb.Append(ch <= 0x7F ? ch : '_');
            var s = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(s) ? "session" : s;
        }

    }
}
