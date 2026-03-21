using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DH.Client.App.Services.Storage
{
    public class TdmsSingleFileStorage : ITdmsStorage
    {
        private IntPtr _file = IntPtr.Zero;
        private IntPtr _group = IntPtr.Zero;
        private readonly ConcurrentDictionary<int, IntPtr> _channels = new();
        private readonly ConcurrentDictionary<int, List<double>> _buffers = new();
        private readonly ConcurrentDictionary<int, long> _totalSamplesWritten = new();
        private readonly ConcurrentDictionary<int, StorageVerifier.IncrementalHasher> _hashers = new();
        private readonly ConcurrentDictionary<int, string> _channelNames = new();
        private readonly object _writeLock = new();

        private bool _useTdms;
        private volatile bool _started;
        private string? _path;
        private CompressionType _compressionType;
        private PreprocessType _preprocessType;
        private CompressionOptions? _compressionOptions;
        private CompressionMetricsCollector? _metricsCollector;
        private double _incrementSeconds = 0.001;

        private const int BatchSize = 4096;

        public void Start(
            string basePath,
            IEnumerable<int> channelIds,
            string sessionName,
            double sampleRateHz,
            CompressionType compressionType = CompressionType.None,
            PreprocessType preprocessType = PreprocessType.None,
            CompressionOptions? compressionOptions = null)
        {
            Directory.CreateDirectory(basePath);

            _useTdms = TdmsNative.IsAvailable;
            _compressionType = compressionType;
            _preprocessType = preprocessType;
            _compressionOptions = compressionOptions ?? new CompressionOptions();
            _metricsCollector = new CompressionMetricsCollector(
                CompressionStorageMode.SingleFile,
                sessionName,
                compressionType,
                preprocessType,
                _compressionOptions,
                sampleRateHz,
                new HashSet<int>(channelIds).Count);

            if (!_useTdms)
            {
                throw new InvalidOperationException("TDMS library is not available. Please place nilibddc.dll in the app directory or PATH.");
            }

            if (sampleRateHz > 0)
            {
                _incrementSeconds = 1.0 / sampleRateHz;
            }

            var safeName = SanitizeName(sessionName);
            _path = Path.Combine(basePath, $"{safeName}.tdms");
            if (File.Exists(_path))
            {
                try
                {
                    File.Delete(_path);
                }
                catch
                {
                    _path = Path.Combine(basePath, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.tdms");
                }
            }

            var idx = _path + "_index";
            if (File.Exists(idx))
            {
                try
                {
                    File.Delete(idx);
                }
                catch
                {
                }
            }

            int err = TdmsNative.DDC_CreateFile(_path, "TDMS", SanitizeAscii(safeName), "", SanitizeAscii(safeName), "DH", ref _file);
            if (err != 0)
            {
                try
                {
                    var asciiBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "DH_ASCII");
                    Directory.CreateDirectory(asciiBase);
                    var asciiPath = Path.Combine(asciiBase, SanitizeAscii(Path.GetFileName(_path)));
                    err = TdmsNative.DDC_CreateFile(asciiPath, "TDMS", SanitizeAscii(safeName), "", SanitizeAscii(safeName), "DH", ref _file);
                    _path = asciiPath;
                }
                catch
                {
                }

                if (err != 0)
                {
                    throw new IOException($"DDC_CreateFile failed: {err} {TdmsNative.DescribeError(err)}");
                }
            }

            err = TdmsNative.DDC_AddChannelGroup(_file, "Session", SanitizeAscii(safeName), ref _group);
            if (err != 0)
            {
                try
                {
                    if (_file != IntPtr.Zero)
                    {
                        TdmsNative.DDC_CloseFile(_file);
                    }
                }
                catch
                {
                }

                _file = IntPtr.Zero;
                throw new IOException($"DDC_AddChannelGroup failed: {err} {TdmsNative.DescribeError(err)}");
            }

            _started = true;
        }

        public void Write(int channelId, ReadOnlySpan<double> samples)
        {
            if (!_started || samples.Length == 0)
            {
                return;
            }

            lock (_writeLock)
            {
                if (!_started)
                {
                    return;
                }

                if (!_channels.TryGetValue(channelId, out var ch))
                {
                    string chName = DH.Contracts.ChannelNaming.TdmsChannelName(channelId);
                    int err = TdmsNative.DDC_AddChannel(_group, TdmsNative.DDCDataType.Double, chName, $"Channel {channelId}", "V", ref ch);
                    if (err != 0)
                    {
                        throw new IOException($"DDC_AddChannel failed, channel={channelId}: {err} {TdmsNative.DescribeError(err)}");
                    }

                    _channels[channelId] = ch;
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xname", "Time");
                    TdmsNative.DDC_CreateChannelPropertyString(ch, "wf_xunit_string", "s");
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_increment", _incrementSeconds);
                    TdmsNative.DDC_CreateChannelPropertyDouble(ch, "wf_start_offset", 0.0);

                    _buffers[channelId] = new List<double>(BatchSize + 1024);
                    _totalSamplesWritten[channelId] = 0;
                    _channelNames[channelId] = chName;
                    _hashers[channelId] = new StorageVerifier.IncrementalHasher();
                }

                if (!_buffers.TryGetValue(channelId, out var buf))
                {
                    buf = new List<double>(BatchSize + 1024);
                    _buffers[channelId] = buf;
                }

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
            if (_hashers.TryGetValue(channelId, out var hasher))
            {
                hasher.Append(flushArr);
            }

            var encodeStopwatch = Stopwatch.StartNew();
            var encodeResult = StorageCodec.EncodeWithMetrics(flushArr, _compressionType, _preprocessType, _compressionOptions);
            encodeStopwatch.Stop();

            var writeStopwatch = Stopwatch.StartNew();
            if (encodeResult.EncodedSamples != null)
            {
                int err = TdmsNative.DDC_AppendDataValuesDouble(ch, encodeResult.EncodedSamples, (uint)encodeResult.EncodedSamples.Length);
                if (err != 0)
                {
                    throw new IOException($"DDC_AppendDataValuesDouble failed, channel={channelId}: {err} {TdmsNative.DescribeError(err)}");
                }
            }
            else
            {
                int err = TdmsNative.DDC_AppendDataValuesDouble(ch, flushArr, (uint)flushArr.Length);
                if (err != 0)
                {
                    throw new IOException($"DDC_AppendDataValuesDouble failed, channel={channelId}: {err} {TdmsNative.DescribeError(err)}");
                }
            }
            writeStopwatch.Stop();

            _metricsCollector?.RecordBatch(channelId, flushArr, encodeResult, encodeStopwatch.Elapsed, writeStopwatch.Elapsed);
            _totalSamplesWritten[channelId] += flushArr.Length;
            buf.Clear();
        }

        public void Flush()
        {
            lock (_writeLock)
            {
                foreach (var kvp in _channels)
                {
                    int id = kvp.Key;
                    IntPtr ch = kvp.Value;
                    if (_buffers.TryGetValue(id, out var buf) && buf.Count > 0)
                    {
                        FlushChannelBuffer(id, ch, buf);
                    }
                }
            }
        }

        public void Stop()
        {
            lock (_writeLock)
            {
                if (!_useTdms)
                {
                    return;
                }

                _started = false;
                FlushInternal();

                if (_file != IntPtr.Zero)
                {
                    try
                    {
                        TdmsNative.DDC_SaveFile(_file);
                    }
                    catch
                    {
                    }

                    try
                    {
                        TdmsNative.DDC_CloseFile(_file);
                    }
                    catch
                    {
                    }
                }

                _file = IntPtr.Zero;
                _group = IntPtr.Zero;
                _channels.Clear();
                _buffers.Clear();
                _totalSamplesWritten.Clear();
            }
        }

        private void FlushInternal()
        {
            foreach (var kvp in _channels)
            {
                int id = kvp.Key;
                IntPtr ch = kvp.Value;
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
                string name = _channelNames.TryGetValue(kv.Key, out var value)
                    ? value
                    : DH.Contracts.ChannelNaming.ChannelName(kv.Key);
                dict[name] = kv.Value.Finalize();
            }

            return dict;
        }

        public IReadOnlyDictionary<string, long> GetWriteSampleCounts()
        {
            var dict = new Dictionary<string, long>();
            foreach (var kv in _totalSamplesWritten)
            {
                string name = _channelNames.TryGetValue(kv.Key, out var value)
                    ? value
                    : DH.Contracts.ChannelNaming.ChannelName(kv.Key);
                dict[name] = kv.Value;
            }

            return dict;
        }

        public CompressionSessionSnapshot? GetCompressionSessionSnapshot()
        {
            return _metricsCollector?.CreateSnapshot();
        }

        public void Dispose()
        {
            Stop();
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "session";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            var safe = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
        }

        private static string SanitizeAscii(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "session";
            }

            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                sb.Append(ch <= 0x7F ? ch : '_');
            }

            var safe = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
        }
    }
}
