using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DH.Client.App.Services.Storage
{
    // TDMS 多文件（每通道一个 .tdms）写入；无 DLL 则退化到 BinaryPerChannel 存储
    // 线程安全：支持SDK回调多线程并发写入
    public class TdmsPerChannelStorage : ITdmsStorage
    {
        private readonly ConcurrentDictionary<int, IntPtr> _files = new();
        private readonly ConcurrentDictionary<int, IntPtr> _groups = new();
        private readonly ConcurrentDictionary<int, IntPtr> _channels = new();
        // private readonly BinaryPerChannelStorage _fallback = new(); // removed BIN fallback
        private bool _useTdms;
        private volatile bool _started;
        private string? _basePath;
        private string? _sessionName;
        private CompressionType _compressionType; // 压缩算法类型
        private PreprocessType _preprocessType; // 预处理技术类型
        private CompressionOptions? _compressionOptions; // 压缩参数
        // 性能优化：每通道缓冲批量写入
        private readonly ConcurrentDictionary<int, List<double>> _buffers = new();
        private const int BatchSize = 4096;
        
        // 记录每个通道已写入的总样本数
        private readonly ConcurrentDictionary<int, long> _totalSamplesWritten = new();
        
        // SHA-256 增量哈希：写入期间持续累计
        private readonly ConcurrentDictionary<int, StorageVerifier.IncrementalHasher> _hashers = new();
        // 通道 ID → 通道名映射
        private readonly ConcurrentDictionary<int, string> _channelNames = new();
        // 已创建的文件路径
        private readonly ConcurrentDictionary<int, string> _filePaths = new();
        
        // 线程同步锁：每个通道一个锁，允许不同通道并行写入
        private readonly ConcurrentDictionary<int, object> _channelLocks = new();
        // 全局锁：用于保护启停操作
        private readonly object _globalLock = new();

        private double _incrementSeconds = 0.001; // 默认 1kHz

        public void Start(string basePath, IEnumerable<int> channelIds, string sessionName, double sampleRateHz, CompressionType compressionType = CompressionType.None, PreprocessType preprocessType = PreprocessType.None, CompressionOptions? compressionOptions = null)
        {
            Directory.CreateDirectory(basePath);
            _basePath = basePath;
            _sessionName = sessionName;
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

            // 懒创建文件/通道：启动时仅记录参数，避免为所有通道预建文件导致卡顿

            _started = true;
        }

        public void Write(int channelId, ReadOnlySpan<double> samples)
        {
            if (!_started || samples.Length == 0) return;
            
            // 获取或创建该通道的锁
            var channelLock = _channelLocks.GetOrAdd(channelId, _ => new object());
            
            lock (channelLock)
            {
                if (!_started) return; // 再次检查，防止在等待锁期间被Stop
                
                if (!_channels.TryGetValue(channelId, out var ch))
                {
                    // 首次写入时创建该通道对应的文件与结构
                    var safeName = SanitizeName(_sessionName ?? "session");
                    string baseFile = DH.Contracts.ChannelNaming.PerChannelFileName(safeName, channelId);
                    string path = Path.Combine(_basePath ?? ".", $"{baseFile}.tdms");
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch { path = Path.Combine(_basePath ?? ".", $"{baseFile}_{DateTime.Now:yyyyMMdd_HHmmss}.tdms"); }
                    }
                    var idx = path + "_index";
                    if (File.Exists(idx)) { try { File.Delete(idx); } catch { }
                    }
                    IntPtr file = IntPtr.Zero;
                    string fileNameAscii = DH.Contracts.ChannelNaming.TdmsChannelName(channelId);
                    int err = TdmsNative.DDC_CreateFile(path, "TDMS", SanitizeAscii(fileNameAscii), "", SanitizeAscii(_sessionName ?? safeName), "DH", ref file);
                    if (err != 0) throw new IOException($"DDC_CreateFile 懒创建失败 ch{channelId}: {err} {TdmsNative.DescribeError(err)}");
                    IntPtr grp = IntPtr.Zero;
                    err = TdmsNative.DDC_AddChannelGroup(file, "Session", SanitizeAscii(_sessionName ?? safeName), ref grp);
                    if (err != 0) throw new IOException($"DDC_AddChannelGroup 懒创建失败 ch{channelId}: {err} {TdmsNative.DescribeError(err)}");
                    string chName = DH.Contracts.ChannelNaming.TdmsChannelName(channelId);
                    err = TdmsNative.DDC_AddChannel(grp, TdmsNative.DDCDataType.Double, chName, $"Channel {channelId}", "V", ref ch);
                    if (err != 0) throw new IOException($"DDC_AddChannel 懒创建失败 ch{channelId}: {err} {TdmsNative.DescribeError(err)}");
                    
                    // 设置波形属性
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xname", "Time");
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xunit_string", "s");
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_increment", _incrementSeconds);
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_start_offset", 0.0); // 初始偏移为0
                    
                    _files[channelId] = file;
                    _groups[channelId] = grp;
                    _channels[channelId] = ch;
                    _buffers[channelId] = new List<double>(BatchSize + 1024);
                    _totalSamplesWritten[channelId] = 0; // 初始化已写入样本数
                    _channelNames[channelId] = chName;
                    _hashers[channelId] = new StorageVerifier.IncrementalHasher();
                    _filePaths[channelId] = path;
                }
                if (!_buffers.TryGetValue(channelId, out var buf)) { buf = new List<double>(BatchSize + 1024); _buffers[channelId] = buf; }
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
            // 线程安全：获取全局锁确保没有并发写入
            lock (_globalLock)
            {
                foreach (var kvp in _channels)
                {
                    var id = kvp.Key; var ch = kvp.Value;
                    var channelLock = _channelLocks.GetOrAdd(id, _ => new object());
                    lock (channelLock)
                    {
                        if (_buffers.TryGetValue(id, out var buf) && buf.Count > 0)
                        {
                            FlushChannelBuffer(id, ch, buf);
                        }
                    }
                }
            }
        }

        public void Stop()
        {
            // 线程安全：获取全局锁
            lock (_globalLock)
            {
                if (_useTdms)
                {
                    // 标记停止，防止新的写入操作
                    _started = false;
                    
                    foreach (var kvp in _files)
                    {
                        var id = kvp.Key; var file = kvp.Value;
                        var channelLock = _channelLocks.GetOrAdd(id, _ => new object());
                        lock (channelLock)
                        {
                            // 刷新该通道的缓冲
                            if (_channels.TryGetValue(id, out var ch) && _buffers.TryGetValue(id, out var buf) && buf.Count > 0)
                            {
                                FlushChannelBuffer(id, ch, buf);
                            }
                        }
                        try { TdmsNative.DDC_SaveFile(file); } catch { }
                        try { TdmsNative.DDC_CloseFile(file); } catch { }
                    }
                    _files.Clear();
                    _groups.Clear();
                    _channels.Clear();
                    _buffers.Clear();
                    _totalSamplesWritten.Clear();
                    _channelLocks.Clear();
                }
            }
        }

        public IReadOnlyList<string> GetWrittenFiles()
        {
            return _filePaths.Values.ToList();
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
