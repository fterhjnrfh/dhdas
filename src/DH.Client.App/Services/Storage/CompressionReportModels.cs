using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DH.Client.App.Services.Storage;

public enum CompressionStorageMode
{
    SingleFile,
    PerChannel,
}

public enum CompressionPayloadKind
{
    TdmsEncodedSamples,
    RawCaptureBlockPayload,
}

public enum CompressionBenchmarkSource
{
    None,
    SampledBatches,
    RawCaptureReplay,
}

public enum CompressionBenchmarkReplayMode
{
    Auto,
    Fast,
    Full,
}

internal static class CompressionBenchmarkDefaults
{
    public const int BatchSize = 4096;
    public const int FastModeMaxChannels = 32;
    public const int FastModeMaxBatchesPerChannel = 128;
    public const long AutoFullMaxRawBytes = 256L * 1024 * 1024;
    public const int AutoFullMaxChannels = 64;
    public const long AutoFullMaxSamples = 64L * 1024 * 1024;

    public static readonly CompressionType[] Algorithms =
    {
        CompressionType.None,
        CompressionType.LZ4,
        CompressionType.Zstd,
        CompressionType.Brotli,
        CompressionType.Snappy,
        CompressionType.Zlib,
        CompressionType.LZ4_HC,
        CompressionType.BZip2,
    };
}

public sealed class CompressionBenchmarkProgress
{
    public CompressionBenchmarkSource Source { get; init; }

    public CompressionBenchmarkReplayMode ReplayMode { get; init; }

    public long BlocksProcessed { get; init; }

    public long TotalBlocks { get; init; }

    public long SamplesProcessed { get; init; }

    public long TargetSamples { get; init; }

    public long EncodedBatchCount { get; init; }

    public int SelectedChannelCount { get; init; }

    public int TotalCandidateChannelCount { get; init; }

    public double ProgressPercent { get; init; }

    public bool IsIndeterminate { get; init; }

    public string StatusText { get; init; } = "";
}

public sealed class CompressionMetricCard
{
    public string Title { get; init; } = "";

    public string Value { get; init; } = "-";

    public string Hint { get; init; } = "";
}

public sealed class CompressionBenchmarkSample
{
    public int ChannelId { get; init; }

    public double[] Samples { get; init; } = Array.Empty<double>();

    public int SampleCount => Samples.Length;

    public long RawBytes => (long)Samples.Length * sizeof(double);
}

public sealed class CompressionChannelSnapshot
{
    public int ChannelId { get; init; }

    public long BatchCount { get; init; }

    public long SampleCount { get; init; }

    public long RawBytes { get; init; }

    public long CodecBytes { get; init; }

    public long TdmsPayloadBytes { get; init; }

    public double EncodeSeconds { get; init; }

    public double WriteSeconds { get; init; }

    public double WorstEncodeMs { get; init; }

    public double WorstWriteMs { get; init; }

    public string ChannelText => $"CH{ChannelId}";

    public string SampleCountText => SampleCount.ToString("N0", CultureInfo.InvariantCulture);

    public string RawBytesText => CompressionReportFormatting.FormatBytes(RawBytes);

    public string CodecBytesText => CompressionReportFormatting.FormatBytes(CodecBytes);

    public string TdmsPayloadBytesText => CompressionReportFormatting.FormatBytes(TdmsPayloadBytes);

    public string CompressionRatioText => CompressionReportFormatting.FormatRatio(CompressionRatio);

    public string EncodeBandwidthText => CompressionReportFormatting.FormatBandwidth(EncodeBandwidthMBps);

    public string WriteBandwidthText => CompressionReportFormatting.FormatBandwidth(WriteBandwidthMBps);

    public string WorstEncodeText => CompressionReportFormatting.FormatMilliseconds(WorstEncodeMs);

    public string WorstWriteText => CompressionReportFormatting.FormatMilliseconds(WorstWriteMs);

    public double CompressionRatio => RawBytes > 0 && CodecBytes > 0
        ? (double)RawBytes / CodecBytes
        : 0d;

    public double EncodeBandwidthMBps => EncodeSeconds > 0
        ? RawBytes / 1024d / 1024d / EncodeSeconds
        : 0d;

    public double WriteBandwidthMBps => WriteSeconds > 0
        ? TdmsPayloadBytes / 1024d / 1024d / WriteSeconds
        : 0d;
}

public sealed class CompressionSessionSnapshot
{
    public string SessionName { get; set; } = "";

    public CompressionStorageMode StorageMode { get; set; }

    public CompressionPayloadKind PayloadKind { get; set; } = CompressionPayloadKind.TdmsEncodedSamples;

    public CompressionType CompressionType { get; set; }

    public PreprocessType PreprocessType { get; set; }

    public CompressionOptions CompressionOptions { get; set; } = new();

    public double SampleRateHz { get; set; }

    public int ChannelCount { get; set; }

    public long BatchCount { get; set; }

    public long TotalSamples { get; set; }

    public long RawBytes { get; set; }

    public long CodecBytes { get; set; }

    public long TdmsPayloadBytes { get; set; }

    public long StoredBytes { get; set; }

    public double EncodeSeconds { get; set; }

    public double WriteSeconds { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime StoppedAt { get; set; }

    public TimeSpan Elapsed { get; set; }

    public double[] EncodeLatencyMsSamples { get; set; } = Array.Empty<double>();

    public double[] WriteLatencyMsSamples { get; set; } = Array.Empty<double>();

    public IReadOnlyList<CompressionChannelSnapshot> Channels { get; set; } = Array.Empty<CompressionChannelSnapshot>();

    public IReadOnlyList<CompressionBenchmarkSample> BenchmarkSamples { get; set; } = Array.Empty<CompressionBenchmarkSample>();

    public IReadOnlyList<string> WrittenFiles { get; set; } = Array.Empty<string>();

    public bool ChannelMetricsEstimated { get; set; }

    public CompressionBenchmarkSource BenchmarkSource { get; set; }

    public string BenchmarkSourcePath { get; set; } = "";

    public int BenchmarkBatchSize { get; set; } = CompressionBenchmarkDefaults.BatchSize;

    public CompressionBenchmarkReplayMode BenchmarkReplayMode { get; set; } = CompressionBenchmarkReplayMode.Auto;

    public string StorageModeText => StorageMode == CompressionStorageMode.SingleFile ? "单文件" : "每通道单文件";

    public string CompressionTypeText => CompressionReportFormatting.FormatCompressionType(CompressionType);

    public string PreprocessTypeText => CompressionReportFormatting.FormatPreprocessType(PreprocessType);

    public string ParameterSummary => CompressionReportFormatting.FormatCompressionOptions(CompressionType, CompressionOptions);

    public string BenchmarkReplayModeText => CompressionReportFormatting.FormatBenchmarkReplayMode(BenchmarkReplayMode);

    public string PayloadBytesLabel => PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
        ? "编码载荷"
        : "TDMS载荷";

    public string PayloadCompressionRatioLabel => PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
        ? "载荷压缩比"
        : "落盘压缩比";

    public string CurrentAlgorithmSectionTitle => PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
        ? "当前算法真实写入表现"
        : "当前算法真实表现";

    public string SampleRateText => SampleRateHz > 0
        ? $"{SampleRateHz:N0} Hz"
        : "-";

    public string TotalSamplesText => TotalSamples.ToString("N0", CultureInfo.InvariantCulture);

    public string RawBytesText => CompressionReportFormatting.FormatBytes(RawBytes);

    public string CodecBytesText => CompressionReportFormatting.FormatBytes(CodecBytes);

    public string TdmsPayloadBytesText => CompressionReportFormatting.FormatBytes(TdmsPayloadBytes);

    public string StoredBytesText => CompressionReportFormatting.FormatBytes(StoredBytes);

    public string CompressionRatioText => CompressionReportFormatting.FormatRatio(CompressionRatio);

    public string PayloadCompressionRatioText => CompressionReportFormatting.FormatRatio(PayloadCompressionRatio);

    public string StorageCompressionRatioText => CompressionReportFormatting.FormatRatio(StorageCompressionRatio);

    public string SpaceSavingText => CompressionReportFormatting.FormatPercent(SpaceSavingPercent);

    public string EncodeBandwidthText => CompressionReportFormatting.FormatBandwidth(EncodeBandwidthMBps);

    public string WriteBandwidthText => CompressionReportFormatting.FormatBandwidth(WriteBandwidthMBps);

    public string EndToEndBandwidthText => CompressionReportFormatting.FormatBandwidth(EndToEndBandwidthMBps);

    public string ElapsedText => Elapsed > TimeSpan.Zero
        ? Elapsed.ToString(@"hh\:mm\:ss")
        : "00:00:00";

    public string BatchCountText => BatchCount.ToString("N0", CultureInfo.InvariantCulture);

    public string P50EncodeText => CompressionReportFormatting.FormatMilliseconds(P50EncodeMs);

    public string P95EncodeText => CompressionReportFormatting.FormatMilliseconds(P95EncodeMs);

    public string P99EncodeText => CompressionReportFormatting.FormatMilliseconds(P99EncodeMs);

    public string WorstEncodeText => CompressionReportFormatting.FormatMilliseconds(WorstEncodeMs);

    public string P50WriteText => CompressionReportFormatting.FormatMilliseconds(P50WriteMs);

    public string P95WriteText => CompressionReportFormatting.FormatMilliseconds(P95WriteMs);

    public string WorstWriteText => CompressionReportFormatting.FormatMilliseconds(WorstWriteMs);

    public string SessionTimeText => StartedAt == default
        ? "-"
        : $"{StartedAt:yyyy-MM-dd HH:mm:ss} - {StoppedAt:yyyy-MM-dd HH:mm:ss}";

    public string FilesSummaryText => WrittenFiles.Count == 0
        ? "-"
        : $"{WrittenFiles.Count} 个文件";

    public string CodecSizeSummaryText => PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
        ? $"压缩字节: {CodecBytesText}"
        : $"压缩载荷: {CodecBytesText}";

    public string PayloadSizeSummaryText => $"{PayloadBytesLabel}: {TdmsPayloadBytesText}";

    public string PayloadCompressionRatioSummaryText => $"{PayloadCompressionRatioLabel}: {PayloadCompressionRatioText}";

    public string ChannelCodecColumnTitle => ChannelMetricsEstimated
        ? "估算压缩载荷"
        : "压缩载荷";

    public string ChannelPayloadColumnTitle => ChannelMetricsEstimated
        ? "估算编码载荷"
        : PayloadBytesLabel;

    public string ChannelBandwidthColumnTitle => ChannelMetricsEstimated
        ? "估算带宽"
        : "压缩带宽";

    public string ChannelWorstEncodeColumnTitle => ChannelMetricsEstimated
        ? "估算最慢批次"
        : "最慢批次";

    public string ChannelMetricsHintText => ChannelMetricsEstimated
        ? "样本数、批次数和原始数据量为真实值；由于 BIN 按整块压缩，单通道载荷、带宽和最慢批次无法精确拆分，以下列按原始数据占比估算。"
        : "";

    public bool HasChannelMetricsHint => !string.IsNullOrWhiteSpace(ChannelMetricsHintText);

    public string BenchmarkEstimateHintText
    {
        get
        {
            if (BenchmarkSource != CompressionBenchmarkSource.RawCaptureReplay)
            {
                return "";
            }

            if (BenchmarkReplayMode == CompressionBenchmarkReplayMode.Fast)
            {
                return "快速模式只回放部分通道或样本，“估算文件”会按当前样本覆盖率外推；上方真实表现仍来自完整写入结果。";
            }

            return PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
                ? "当前对比结果按 BIN 实际整块编码路径回放，和原始写入口径一致。"
                : "";
        }
    }

    public bool HasBenchmarkEstimateHint => !string.IsNullOrWhiteSpace(BenchmarkEstimateHintText);

    public string BenchmarkSampleSummaryText => BenchmarkSamples.Count == 0
        ? BenchmarkSource switch
        {
            CompressionBenchmarkSource.RawCaptureReplay => string.IsNullOrWhiteSpace(BenchmarkSourcePath)
                ? PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
                    ? "已保存原始数据按原始块回放，与 BIN 实际写入路径一致"
                    : $"已保存原始数据分层回放，按 {BenchmarkBatchSize:N0} 点/通道重组批次"
                : PayloadKind == CompressionPayloadKind.RawCaptureBlockPayload
                    ? $"已保存原始数据 {Path.GetFileName(BenchmarkSourcePath)} 按原始块回放，与 BIN 实际写入路径一致"
                    : $"已保存原始数据 {Path.GetFileName(BenchmarkSourcePath)} 分层回放，按 {BenchmarkBatchSize:N0} 点/通道重组批次",
            _ => "未采集到 benchmark 样本"
        }
        : $"{BenchmarkSamples.Count} 个采样批次，原始数据 {CompressionReportFormatting.FormatBytes(BenchmarkSampleBytes)}";

    public long BenchmarkSampleBytes => BenchmarkSamples.Sum(static sample => sample.RawBytes);

    public double CompressionRatio => RawBytes > 0 && CodecBytes > 0
        ? (double)RawBytes / CodecBytes
        : 0d;

    public double PayloadCompressionRatio => RawBytes > 0 && TdmsPayloadBytes > 0
        ? (double)RawBytes / TdmsPayloadBytes
        : 0d;

    public double StorageCompressionRatio => RawBytes > 0 && StoredBytes > 0
        ? (double)RawBytes / StoredBytes
        : 0d;

    public double SpaceSavingPercent => RawBytes > 0 && StoredBytes > 0
        ? 1d - (double)StoredBytes / RawBytes
        : 0d;

    public double EncodeBandwidthMBps => EncodeSeconds > 0
        ? RawBytes / 1024d / 1024d / EncodeSeconds
        : 0d;

    public double WriteBandwidthMBps => WriteSeconds > 0
        ? TdmsPayloadBytes / 1024d / 1024d / WriteSeconds
        : 0d;

    public double EndToEndBandwidthMBps => Elapsed.TotalSeconds > 0
        ? RawBytes / 1024d / 1024d / Elapsed.TotalSeconds
        : 0d;

    public double P50EncodeMs => CompressionReportFormatting.Percentile(EncodeLatencyMsSamples, 0.50);

    public double P95EncodeMs => CompressionReportFormatting.Percentile(EncodeLatencyMsSamples, 0.95);

    public double P99EncodeMs => CompressionReportFormatting.Percentile(EncodeLatencyMsSamples, 0.99);

    public double WorstEncodeMs => EncodeLatencyMsSamples.Length == 0 ? 0d : EncodeLatencyMsSamples.Max();

    public double P50WriteMs => CompressionReportFormatting.Percentile(WriteLatencyMsSamples, 0.50);

    public double P95WriteMs => CompressionReportFormatting.Percentile(WriteLatencyMsSamples, 0.95);

    public double WorstWriteMs => WriteLatencyMsSamples.Length == 0 ? 0d : WriteLatencyMsSamples.Max();
}

public sealed class CompressionBenchmarkRow
{
    public CompressionType CompressionType { get; init; }

    public PreprocessType PreprocessType { get; init; }

    public CompressionOptions CompressionOptions { get; init; } = new();

    public long BatchCount { get; init; }

    public long SampleCount { get; init; }

    public long RawBytes { get; init; }

    public long CodecBytes { get; init; }

    public long TdmsPayloadBytes { get; init; }

    public long EstimatedStoredBytes { get; init; }

    public double EncodeSeconds { get; init; }

    public double[] EncodeLatencyMsSamples { get; init; } = Array.Empty<double>();

    public bool IsCurrentAlgorithm { get; init; }

    public string AlgorithmText => CompressionReportFormatting.FormatCompressionType(CompressionType);

    public string ParameterSummary => CompressionReportFormatting.FormatCompressionOptions(CompressionType, CompressionOptions);

    public string CurrentTagText => IsCurrentAlgorithm ? "当前" : "";

    public string CompressionRatioText => CompressionReportFormatting.FormatRatio(CompressionRatio);

    public string SavingText => CompressionReportFormatting.FormatPercent(SavingPercent);

    public string EncodeBandwidthText => CompressionReportFormatting.FormatBandwidth(EncodeBandwidthMBps);

    public string P95EncodeText => CompressionReportFormatting.FormatMilliseconds(P95EncodeMs);

    public string WorstEncodeText => CompressionReportFormatting.FormatMilliseconds(WorstEncodeMs);

    public string EstimatedStoredBytesText => CompressionReportFormatting.FormatBytes(EstimatedStoredBytes);

    public double CompressionRatio => RawBytes > 0 && CodecBytes > 0
        ? (double)RawBytes / CodecBytes
        : 0d;

    public double SavingPercent => RawBytes > 0 && CodecBytes > 0
        ? 1d - (double)CodecBytes / RawBytes
        : 0d;

    public double EncodeBandwidthMBps => EncodeSeconds > 0
        ? RawBytes / 1024d / 1024d / EncodeSeconds
        : 0d;

    public double P95EncodeMs => CompressionReportFormatting.Percentile(EncodeLatencyMsSamples, 0.95);

    public double WorstEncodeMs => EncodeLatencyMsSamples.Length == 0 ? 0d : EncodeLatencyMsSamples.Max();
}

internal sealed class CompressionMetricsCollector
{
    private const int MaxLatencySampleCount = 4096;
    private const int MaxBenchmarkSampleCount = 96;
    private const long MaxBenchmarkSampleBytes = 64L * 1024 * 1024;
    private const int KeepInitialBenchmarkBatches = 12;
    private const int BenchmarkStride = 32;

    private readonly object _syncRoot = new();
    private readonly CompressionStorageMode _storageMode;
    private readonly string _sessionName;
    private readonly CompressionType _compressionType;
    private readonly PreprocessType _preprocessType;
    private readonly CompressionOptions _compressionOptions;
    private readonly double _sampleRateHz;
    private readonly int _configuredChannelCount;
    private readonly int _benchmarkBatchSize;
    private readonly Dictionary<int, ChannelAccumulator> _channels = new();
    private readonly List<double> _encodeLatencyMsSamples = new();
    private readonly List<double> _writeLatencyMsSamples = new();
    private readonly List<CompressionBenchmarkSample> _benchmarkSamples = new();

    private long _batchCount;
    private long _totalSamples;
    private long _rawBytes;
    private long _codecBytes;
    private long _tdmsPayloadBytes;
    private double _encodeSeconds;
    private double _writeSeconds;
    private long _benchmarkSampleBytes;

    public CompressionMetricsCollector(
        CompressionStorageMode storageMode,
        string sessionName,
        CompressionType compressionType,
        PreprocessType preprocessType,
        CompressionOptions compressionOptions,
        double sampleRateHz,
        int configuredChannelCount,
        int benchmarkBatchSize = CompressionBenchmarkDefaults.BatchSize)
    {
        _storageMode = storageMode;
        _sessionName = sessionName;
        _compressionType = compressionType;
        _preprocessType = preprocessType;
        _compressionOptions = compressionOptions.Clone();
        _sampleRateHz = sampleRateHz;
        _configuredChannelCount = configuredChannelCount;
        _benchmarkBatchSize = benchmarkBatchSize > 0 ? benchmarkBatchSize : CompressionBenchmarkDefaults.BatchSize;
    }

    public void RecordBatch(int channelId, double[] rawSamples, StorageCodec.StorageEncodeResult encodeResult, TimeSpan encodeElapsed, TimeSpan writeElapsed)
    {
        lock (_syncRoot)
        {
            _batchCount++;
            _totalSamples += rawSamples.Length;
            _rawBytes += encodeResult.RawBytes;
            _codecBytes += encodeResult.CodecBytes;
            _tdmsPayloadBytes += encodeResult.PayloadBytes;
            _encodeSeconds += encodeElapsed.TotalSeconds;
            _writeSeconds += writeElapsed.TotalSeconds;

            AppendLatency(_encodeLatencyMsSamples, encodeElapsed.TotalMilliseconds, _batchCount);
            AppendLatency(_writeLatencyMsSamples, writeElapsed.TotalMilliseconds, _batchCount);

            if (!_channels.TryGetValue(channelId, out var accumulator))
            {
                accumulator = new ChannelAccumulator(channelId);
                _channels[channelId] = accumulator;
            }

            accumulator.Record(rawSamples.Length, encodeResult.RawBytes, encodeResult.CodecBytes, encodeResult.PayloadBytes, encodeElapsed, writeElapsed);
            CaptureBenchmarkSampleIfNeeded(channelId, rawSamples);
        }
    }

    public CompressionSessionSnapshot CreateSnapshot()
    {
        lock (_syncRoot)
        {
            return new CompressionSessionSnapshot
            {
                SessionName = _sessionName,
                StorageMode = _storageMode,
                PayloadKind = CompressionPayloadKind.TdmsEncodedSamples,
                CompressionType = _compressionType,
                PreprocessType = _preprocessType,
                CompressionOptions = _compressionOptions.Clone(),
                SampleRateHz = _sampleRateHz,
                ChannelCount = _configuredChannelCount,
                BatchCount = _batchCount,
                TotalSamples = _totalSamples,
                RawBytes = _rawBytes,
                CodecBytes = _codecBytes,
                TdmsPayloadBytes = _tdmsPayloadBytes,
                EncodeSeconds = _encodeSeconds,
                WriteSeconds = _writeSeconds,
                EncodeLatencyMsSamples = _encodeLatencyMsSamples.ToArray(),
                WriteLatencyMsSamples = _writeLatencyMsSamples.ToArray(),
                Channels = _channels.Values
                    .OrderBy(static channel => channel.ChannelId)
                    .Select(static channel => channel.ToSnapshot())
                    .ToArray(),
                ChannelMetricsEstimated = false,
                BenchmarkSource = _benchmarkSamples.Count > 0
                    ? CompressionBenchmarkSource.SampledBatches
                    : CompressionBenchmarkSource.None,
                BenchmarkBatchSize = _benchmarkBatchSize,
                BenchmarkSamples = _benchmarkSamples
                    .Select(static sample => new CompressionBenchmarkSample
                    {
                        ChannelId = sample.ChannelId,
                        Samples = (double[])sample.Samples.Clone(),
                    })
                    .ToArray(),
            };
        }
    }

    private void CaptureBenchmarkSampleIfNeeded(int channelId, double[] rawSamples)
    {
        if (!ShouldCaptureBenchmarkBatch(_batchCount))
        {
            return;
        }

        long rawBytes = (long)rawSamples.Length * sizeof(double);
        if (_benchmarkSamples.Count >= MaxBenchmarkSampleCount || _benchmarkSampleBytes + rawBytes > MaxBenchmarkSampleBytes)
        {
            return;
        }

        var copy = new double[rawSamples.Length];
        Array.Copy(rawSamples, copy, rawSamples.Length);
        _benchmarkSamples.Add(new CompressionBenchmarkSample
        {
            ChannelId = channelId,
            Samples = copy,
        });
        _benchmarkSampleBytes += rawBytes;
    }

    private static bool ShouldCaptureBenchmarkBatch(long batchIndex)
    {
        return batchIndex <= KeepInitialBenchmarkBatches || batchIndex % BenchmarkStride == 0;
    }

    private static void AppendLatency(List<double> latencies, double value, long sampleIndex)
    {
        if (latencies.Count < MaxLatencySampleCount)
        {
            latencies.Add(value);
            return;
        }

        latencies[(int)(sampleIndex % MaxLatencySampleCount)] = value;
    }

    private sealed class ChannelAccumulator
    {
        public ChannelAccumulator(int channelId)
        {
            ChannelId = channelId;
        }

        public int ChannelId { get; }

        public long BatchCount { get; private set; }

        public long SampleCount { get; private set; }

        public long RawBytes { get; private set; }

        public long CodecBytes { get; private set; }

        public long TdmsPayloadBytes { get; private set; }

        public double EncodeSeconds { get; private set; }

        public double WriteSeconds { get; private set; }

        public double WorstEncodeMs { get; private set; }

        public double WorstWriteMs { get; private set; }

        public void Record(int sampleCount, int rawBytes, int codecBytes, int payloadBytes, TimeSpan encodeElapsed, TimeSpan writeElapsed)
        {
            BatchCount++;
            SampleCount += sampleCount;
            RawBytes += rawBytes;
            CodecBytes += codecBytes;
            TdmsPayloadBytes += payloadBytes;
            EncodeSeconds += encodeElapsed.TotalSeconds;
            WriteSeconds += writeElapsed.TotalSeconds;
            WorstEncodeMs = Math.Max(WorstEncodeMs, encodeElapsed.TotalMilliseconds);
            WorstWriteMs = Math.Max(WorstWriteMs, writeElapsed.TotalMilliseconds);
        }

        public CompressionChannelSnapshot ToSnapshot()
        {
            return new CompressionChannelSnapshot
            {
                ChannelId = ChannelId,
                BatchCount = BatchCount,
                SampleCount = SampleCount,
                RawBytes = RawBytes,
                CodecBytes = CodecBytes,
                TdmsPayloadBytes = TdmsPayloadBytes,
                EncodeSeconds = EncodeSeconds,
                WriteSeconds = WriteSeconds,
                WorstEncodeMs = WorstEncodeMs,
                WorstWriteMs = WorstWriteMs,
            };
        }
    }
}

public static class CompressionBenchmarkService
{
    public static CompressionBenchmarkReplayMode ResolveReplayMode(
        CompressionSessionSnapshot snapshot,
        CompressionBenchmarkReplayMode? requestedMode = null)
    {
        CompressionBenchmarkReplayMode mode = requestedMode ?? snapshot.BenchmarkReplayMode;
        if (snapshot.BenchmarkSource != CompressionBenchmarkSource.RawCaptureReplay)
        {
            return mode == CompressionBenchmarkReplayMode.Auto
                ? CompressionBenchmarkReplayMode.Full
                : mode;
        }

        if (mode != CompressionBenchmarkReplayMode.Auto)
        {
            return mode;
        }

        long rawBytes = snapshot.RawBytes;
        if (rawBytes <= 0
            && !string.IsNullOrWhiteSpace(snapshot.BenchmarkSourcePath)
            && File.Exists(snapshot.BenchmarkSourcePath))
        {
            try
            {
                rawBytes = new FileInfo(snapshot.BenchmarkSourcePath).Length;
            }
            catch
            {
            }
        }

        if (rawBytes > CompressionBenchmarkDefaults.AutoFullMaxRawBytes
            || snapshot.ChannelCount > CompressionBenchmarkDefaults.AutoFullMaxChannels
            || snapshot.TotalSamples > CompressionBenchmarkDefaults.AutoFullMaxSamples)
        {
            return CompressionBenchmarkReplayMode.Fast;
        }

        return CompressionBenchmarkReplayMode.Full;
    }

    public static bool HasBenchmarkInput(CompressionSessionSnapshot snapshot)
    {
        if (snapshot.BenchmarkSource == CompressionBenchmarkSource.RawCaptureReplay
            && !string.IsNullOrWhiteSpace(snapshot.BenchmarkSourcePath)
            && File.Exists(snapshot.BenchmarkSourcePath))
        {
            return true;
        }

        return snapshot.BenchmarkSamples.Count > 0;
    }

    public static IReadOnlyList<CompressionBenchmarkRow> BuildBenchmarkRows(
        CompressionSessionSnapshot snapshot,
        CancellationToken cancellationToken = default,
        Action<CompressionBenchmarkProgress>? progressCallback = null)
    {
        if (snapshot.BenchmarkSource == CompressionBenchmarkSource.RawCaptureReplay
            && !string.IsNullOrWhiteSpace(snapshot.BenchmarkSourcePath)
            && File.Exists(snapshot.BenchmarkSourcePath))
        {
            return SdkRawCompressionBenchmarkService.BuildBenchmarkRows(snapshot, progressCallback, cancellationToken);
        }

        if (snapshot.BenchmarkSamples.Count == 0)
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        var rows = new List<CompressionBenchmarkRow>(CompressionBenchmarkDefaults.Algorithms.Length);
        int algorithmCount = CompressionBenchmarkDefaults.Algorithms.Length;
        for (int index = 0; index < algorithmCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var algorithm = CompressionBenchmarkDefaults.Algorithms[index];
            rows.Add(BuildRow(snapshot, algorithm, cancellationToken));
            progressCallback?.Invoke(new CompressionBenchmarkProgress
            {
                Source = CompressionBenchmarkSource.SampledBatches,
                ReplayMode = CompressionBenchmarkReplayMode.Full,
                EncodedBatchCount = index + 1,
                ProgressPercent = (index + 1) * 100d / algorithmCount,
                IsIndeterminate = false,
                StatusText = $"正在基于采样批次评估各压缩算法性能（{index + 1}/{algorithmCount}，{(index + 1) * 100d / algorithmCount:F1}%）..."
            });
        }

        return rows;
    }

    private static CompressionBenchmarkRow BuildRow(CompressionSessionSnapshot snapshot, CompressionType algorithm, CancellationToken cancellationToken)
    {
        long rawBytes = 0;
        long codecBytes = 0;
        long payloadBytes = 0;
        long sampleCount = 0;
        long batchCount = 0;
        double encodeSeconds = 0d;
        var encodeLatencies = new List<double>(snapshot.BenchmarkSamples.Count);

        foreach (var sample in snapshot.BenchmarkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            var encodeResult = StorageCodec.EncodeWithMetrics(sample.Samples, algorithm, snapshot.PreprocessType, snapshot.CompressionOptions);
            sw.Stop();

            batchCount++;
            sampleCount += sample.Samples.Length;
            rawBytes += encodeResult.RawBytes;
            codecBytes += encodeResult.CodecBytes;
            payloadBytes += encodeResult.PayloadBytes;
            encodeSeconds += sw.Elapsed.TotalSeconds;
            encodeLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        long estimatedStoredBytes = EstimateStoredBytesForFullDataset(
            snapshot.RawBytes,
            rawBytes,
            snapshot.StoredBytes,
            snapshot.TdmsPayloadBytes,
            payloadBytes);
        return new CompressionBenchmarkRow
        {
            CompressionType = algorithm,
            PreprocessType = snapshot.PreprocessType,
            CompressionOptions = snapshot.CompressionOptions.Clone(),
            BatchCount = batchCount,
            SampleCount = sampleCount,
            RawBytes = rawBytes,
            CodecBytes = codecBytes,
            TdmsPayloadBytes = payloadBytes,
            EstimatedStoredBytes = estimatedStoredBytes,
            EncodeSeconds = encodeSeconds,
            EncodeLatencyMsSamples = encodeLatencies.ToArray(),
            IsCurrentAlgorithm = algorithm == snapshot.CompressionType,
        };
    }

    internal static long EstimatePayloadBytesForFullDataset(long totalRawBytes, long benchmarkRawBytes, long benchmarkPayloadBytes)
    {
        if (benchmarkPayloadBytes <= 0)
        {
            return 0;
        }

        if (totalRawBytes > 0 && benchmarkRawBytes > 0 && benchmarkRawBytes < totalRawBytes)
        {
            return (long)Math.Round(benchmarkPayloadBytes * ((double)totalRawBytes / benchmarkRawBytes));
        }

        return benchmarkPayloadBytes;
    }

    internal static long EstimateStoredBytesForFullDataset(
        long totalRawBytes,
        long benchmarkRawBytes,
        long actualStoredBytes,
        long actualPayloadBytes,
        long benchmarkPayloadBytes)
    {
        long estimatedPayloadBytes = EstimatePayloadBytesForFullDataset(totalRawBytes, benchmarkRawBytes, benchmarkPayloadBytes);
        if (actualStoredBytes <= 0)
        {
            return estimatedPayloadBytes;
        }

        long fixedOverhead = Math.Max(0, actualStoredBytes - actualPayloadBytes);
        return fixedOverhead + estimatedPayloadBytes;
    }
}

internal static class CompressionReportFormatting
{
    public static string FormatCompressionType(CompressionType type)
    {
        return type.ToString();
    }

    public static string FormatPreprocessType(PreprocessType type)
    {
        return type switch
        {
            PreprocessType.None => "无",
            PreprocessType.DiffOrder1 => "一阶差分",
            PreprocessType.DiffOrder2 => "二阶差分",
            PreprocessType.LinearPrediction => "线性预测",
            _ => type.ToString(),
        };
    }

    public static string FormatBenchmarkReplayMode(CompressionBenchmarkReplayMode mode)
    {
        return mode switch
        {
            CompressionBenchmarkReplayMode.Auto => "自动",
            CompressionBenchmarkReplayMode.Fast => "快速模式",
            CompressionBenchmarkReplayMode.Full => "全量模式",
            _ => mode.ToString(),
        };
    }

    public static string FormatCompressionOptions(CompressionType type, CompressionOptions? options)
    {
        var opts = options ?? new CompressionOptions();
        return type switch
        {
            CompressionType.None => "-",
            CompressionType.LZ4 => $"Level {opts.LZ4Level}",
            CompressionType.Zstd => $"Level {opts.ZstdLevel}, WindowLog {opts.ZstdWindowLog}",
            CompressionType.Brotli => $"Quality {opts.BrotliQuality}, WindowBits {opts.BrotliWindowBits}",
            CompressionType.Snappy => "-",
            CompressionType.Zlib => $"Level {opts.ZlibLevel}",
            CompressionType.LZ4_HC => $"Level {opts.LZ4HCLevel}",
            CompressionType.BZip2 => $"Block {opts.BZip2BlockSize} x100K",
            _ => "-",
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024d && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    public static string FormatRatio(double ratio)
    {
        return ratio > 0 ? $"{ratio:F2}x" : "-";
    }

    public static string FormatPercent(double fraction)
    {
        return $"{fraction * 100d:F1}%";
    }

    public static string FormatBandwidth(double mbps)
    {
        return mbps > 0 ? $"{mbps:F2} MB/s" : "-";
    }

    public static string FormatMilliseconds(double milliseconds)
    {
        return milliseconds > 0 ? $"{milliseconds:F2} ms" : "-";
    }

    public static double Percentile(double[] samples, double percentile)
    {
        if (samples == null || samples.Length == 0)
        {
            return 0d;
        }

        var copy = new double[samples.Length];
        Array.Copy(samples, copy, samples.Length);
        Array.Sort(copy);

        if (copy.Length == 1)
        {
            return copy[0];
        }

        double position = percentile * (copy.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return copy[lower];
        }

        double weight = position - lower;
        return copy[lower] + (copy[upper] - copy[lower]) * weight;
    }
}
