using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DH.Contracts;
using DH.Driver.SDK;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCompressionBenchmarkService
{
    private const int ProgressBlockStride = 8;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<CompressionBenchmarkRow> BuildBenchmarkRows(
        CompressionSessionSnapshot snapshot,
        Action<CompressionBenchmarkProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        string capturePath = snapshot.BenchmarkSourcePath;
        if (string.IsNullOrWhiteSpace(capturePath) || !File.Exists(capturePath))
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest);
        var candidateChannelIds = ResolveBenchmarkChannelIds(snapshot, capturePath, manifest);
        if (candidateChannelIds.Count == 0)
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        CompressionBenchmarkReplayMode replayMode = snapshot.BenchmarkReplayMode == CompressionBenchmarkReplayMode.Auto
            ? CompressionBenchmarkService.ResolveReplayMode(snapshot)
            : snapshot.BenchmarkReplayMode;
        var selectedChannelIds = SelectChannelIdsForReplayMode(candidateChannelIds, replayMode);
        if (selectedChannelIds.Count == 0)
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        int batchSize = snapshot.BenchmarkBatchSize > 0
            ? snapshot.BenchmarkBatchSize
            : CompressionBenchmarkDefaults.BatchSize;
        var targetSamplesPerChannel = ApplyReplayModeSampleTargets(
            ResolveTargetSamplesPerChannel(snapshot, manifest, selectedChannelIds),
            replayMode,
            batchSize);
        selectedChannelIds = selectedChannelIds
            .Where(channelId => targetSamplesPerChannel.TryGetValue(channelId, out long targetSamples) && targetSamples > 0)
            .ToList();
        if (selectedChannelIds.Count == 0)
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        bool targetsFullyKnown = selectedChannelIds.All(channelId =>
            targetSamplesPerChannel.TryGetValue(channelId, out long targetSamples)
            && targetSamples > 0
            && targetSamples < long.MaxValue);
        long totalTargetSamples = targetsFullyKnown
            ? selectedChannelIds.Sum(channelId => targetSamplesPerChannel[channelId])
            : 0L;
        long totalBlocks = manifest?.BlockCount ?? 0L;

        var replayBuffers = selectedChannelIds.ToDictionary(
            channelId => channelId,
            _ => new ReplayChannelBuffer(batchSize));
        var replayedSamplesPerChannel = selectedChannelIds.ToDictionary(
            channelId => channelId,
            _ => 0L);
        var accumulators = CompressionBenchmarkDefaults.Algorithms
            .Select(static algorithm => new AlgorithmAccumulator(algorithm))
            .ToArray();
        Action<double[]> batchRecorder = batch => RecordBatch(accumulators, batch, snapshot, cancellationToken);
        var progressReporter = new ProgressReporter(
            progressCallback,
            replayMode,
            selectedChannelIds.Count,
            candidateChannelIds.Count,
            totalBlocks,
            totalTargetSamples,
            targetsFullyKnown,
            accumulators);

        long blocksProcessed = 0L;
        long samplesProcessed = 0L;
        int completedChannelCount = CountCompletedChannels(selectedChannelIds, replayedSamplesPerChannel, targetSamplesPerChannel);

        progressReporter.Report(blocksProcessed, samplesProcessed, force: true);

        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        var fileHeader = SdkRawCaptureConverter.SkipFileHeader(reader);

        while (SdkRawCaptureConverter.TryReadRawBlock(reader, fileHeader, out var rawBlock))
        {
            cancellationToken.ThrowIfCancellationRequested();

            blocksProcessed++;
            samplesProcessed += ReplayRawBlock(
                rawBlock,
                targetSamplesPerChannel,
                replayBuffers,
                replayedSamplesPerChannel,
                batchRecorder,
                ref completedChannelCount,
                cancellationToken);

            progressReporter.Report(
                blocksProcessed,
                samplesProcessed,
                force: blocksProcessed == 1 || blocksProcessed % ProgressBlockStride == 0);

            if (completedChannelCount >= selectedChannelIds.Count)
            {
                break;
            }
        }

        foreach (var replayBuffer in replayBuffers.Values)
        {
            replayBuffer.FlushRemaining(batchRecorder);
        }

        progressReporter.Report(
            blocksProcessed,
            progressReporter.HasKnownTargetSamples ? totalTargetSamples : samplesProcessed,
            force: true,
            completed: true);

        if (accumulators.All(static accumulator => accumulator.BatchCount == 0))
        {
            return Array.Empty<CompressionBenchmarkRow>();
        }

        return accumulators
            .Select(accumulator => accumulator.ToRow(snapshot))
            .ToArray();
    }

    private static List<int> ResolveBenchmarkChannelIds(
        CompressionSessionSnapshot snapshot,
        string capturePath,
        SdkRawCaptureManifest? manifest)
    {
        var channelIds = snapshot.Channels
            .Select(static channel => channel.ChannelId)
            .Where(static channelId => channelId > 0)
            .Distinct()
            .OrderBy(static channelId => channelId)
            .ToList();

        if (channelIds.Count > 0)
        {
            return channelIds;
        }

        return SdkRawCaptureConverter.ResolveChannelIds(capturePath, manifest);
    }

    private static List<int> SelectChannelIdsForReplayMode(
        IReadOnlyList<int> channelIds,
        CompressionBenchmarkReplayMode replayMode)
    {
        if (replayMode != CompressionBenchmarkReplayMode.Fast
            || channelIds.Count <= CompressionBenchmarkDefaults.FastModeMaxChannels)
        {
            return channelIds.ToList();
        }

        int maxChannels = Math.Max(1, CompressionBenchmarkDefaults.FastModeMaxChannels);
        var selected = new List<int>(maxChannels);
        var selectedSet = new HashSet<int>();
        double step = (channelIds.Count - 1d) / Math.Max(1, maxChannels - 1);

        for (int i = 0; i < maxChannels; i++)
        {
            int index = (int)Math.Round(i * step, MidpointRounding.AwayFromZero);
            index = Math.Clamp(index, 0, channelIds.Count - 1);
            int channelId = channelIds[index];
            if (selectedSet.Add(channelId))
            {
                selected.Add(channelId);
            }
        }

        for (int index = 0; selected.Count < maxChannels && index < channelIds.Count; index++)
        {
            int channelId = channelIds[index];
            if (selectedSet.Add(channelId))
            {
                selected.Add(channelId);
            }
        }

        selected.Sort();
        return selected;
    }

    private static Dictionary<int, long> ApplyReplayModeSampleTargets(
        IReadOnlyDictionary<int, long> targetSamplesPerChannel,
        CompressionBenchmarkReplayMode replayMode,
        int batchSize)
    {
        if (replayMode != CompressionBenchmarkReplayMode.Fast)
        {
            return targetSamplesPerChannel.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
        }

        long cappedSamplesPerChannel = (long)Math.Max(1, batchSize) * CompressionBenchmarkDefaults.FastModeMaxBatchesPerChannel;
        return targetSamplesPerChannel.ToDictionary(
            static kvp => kvp.Key,
            kvp =>
            {
                if (kvp.Value <= 0)
                {
                    return 0L;
                }

                if (kvp.Value == long.MaxValue)
                {
                    return cappedSamplesPerChannel;
                }

                return Math.Min(kvp.Value, cappedSamplesPerChannel);
            });
    }

    private static IReadOnlyDictionary<int, long> ResolveTargetSamplesPerChannel(
        CompressionSessionSnapshot snapshot,
        SdkRawCaptureManifest? manifest,
        IReadOnlyCollection<int> selectedChannelIds)
    {
        var selectedChannelSet = selectedChannelIds.ToHashSet();
        var targets = snapshot.Channels
            .Where(channel => selectedChannelSet.Contains(channel.ChannelId) && channel.SampleCount > 0)
            .ToDictionary(channel => channel.ChannelId, channel => channel.SampleCount);

        if (manifest?.ChannelSampleCounts?.Count > 0)
        {
            foreach (var kvp in manifest.ChannelSampleCounts)
            {
                int channelId = ChannelNaming.ParseChannelName(kvp.Key);
                if (channelId <= 0 || !selectedChannelSet.Contains(channelId) || targets.ContainsKey(channelId))
                {
                    continue;
                }

                if (kvp.Value > 0)
                {
                    targets[channelId] = kvp.Value;
                }
            }
        }

        foreach (int channelId in selectedChannelIds)
        {
            targets.TryAdd(channelId, long.MaxValue);
        }

        return targets;
    }

    private static int CountCompletedChannels(
        IReadOnlyList<int> selectedChannelIds,
        IReadOnlyDictionary<int, long> replayedSamplesPerChannel,
        IReadOnlyDictionary<int, long> targetSamplesPerChannel)
    {
        int completedChannelCount = 0;
        foreach (int channelId in selectedChannelIds)
        {
            if (targetSamplesPerChannel.TryGetValue(channelId, out long targetSamples)
                && replayedSamplesPerChannel.TryGetValue(channelId, out long replayedSamples)
                && replayedSamples >= targetSamples)
            {
                completedChannelCount++;
            }
        }

        return completedChannelCount;
    }

    private static long ReplayRawBlock(
        SdkRawBlock rawBlock,
        IReadOnlyDictionary<int, long> targetSamplesPerChannel,
        IReadOnlyDictionary<int, ReplayChannelBuffer> replayBuffers,
        IDictionary<int, long> replayedSamplesPerChannel,
        Action<double[]> batchRecorder,
        ref int completedChannelCount,
        CancellationToken cancellationToken)
    {
        int deviceId = SdkRawCaptureWriter.ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);
        int channelCount = rawBlock.ChannelCount;
        ReadOnlySpan<float> payload = rawBlock.PayloadSpan;
        long replayedSamplesThisBlock = 0L;

        for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            int channelId = ChannelNaming.MakeChannelId(deviceId, channelIndex + 1);
            if (!replayBuffers.TryGetValue(channelId, out var replayBuffer))
            {
                continue;
            }

            long targetSamples = targetSamplesPerChannel.TryGetValue(channelId, out long target)
                ? target
                : long.MaxValue;
            long replayedSamples = replayedSamplesPerChannel[channelId];
            if (replayedSamples >= targetSamples)
            {
                continue;
            }

            bool crossedTarget = false;
            for (int sampleIndex = 0; sampleIndex < rawBlock.DataCountPerChannel && replayedSamples < targetSamples; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int payloadIndex = sampleIndex * channelCount + channelIndex;
                if ((uint)payloadIndex >= (uint)payload.Length)
                {
                    break;
                }

                replayBuffer.Append(payload[payloadIndex], batchRecorder);
                replayedSamples++;
                replayedSamplesThisBlock++;
            }

            if (replayedSamples >= targetSamples && replayedSamplesPerChannel[channelId] < targetSamples)
            {
                crossedTarget = true;
            }

            replayedSamplesPerChannel[channelId] = replayedSamples;
            if (crossedTarget)
            {
                completedChannelCount++;
            }
        }

        return replayedSamplesThisBlock;
    }

    private static void RecordBatch(
        IReadOnlyList<AlgorithmAccumulator> accumulators,
        double[] batch,
        CompressionSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var accumulator in accumulators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.RecordBatch(batch, snapshot.PreprocessType, snapshot.CompressionOptions);
        }
    }

    private sealed class ProgressReporter
    {
        private readonly Action<CompressionBenchmarkProgress>? _callback;
        private readonly CompressionBenchmarkReplayMode _replayMode;
        private readonly int _selectedChannelCount;
        private readonly int _totalCandidateChannelCount;
        private readonly long _totalBlocks;
        private readonly long _totalTargetSamples;
        private readonly bool _targetsFullyKnown;
        private readonly IReadOnlyList<AlgorithmAccumulator> _accumulators;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastReportedBlocks = -1;

        public ProgressReporter(
            Action<CompressionBenchmarkProgress>? callback,
            CompressionBenchmarkReplayMode replayMode,
            int selectedChannelCount,
            int totalCandidateChannelCount,
            long totalBlocks,
            long totalTargetSamples,
            bool targetsFullyKnown,
            IReadOnlyList<AlgorithmAccumulator> accumulators)
        {
            _callback = callback;
            _replayMode = replayMode;
            _selectedChannelCount = Math.Max(0, selectedChannelCount);
            _totalCandidateChannelCount = Math.Max(_selectedChannelCount, totalCandidateChannelCount);
            _totalBlocks = Math.Max(0, totalBlocks);
            _totalTargetSamples = Math.Max(0, totalTargetSamples);
            _targetsFullyKnown = targetsFullyKnown && totalTargetSamples > 0;
            _accumulators = accumulators;
        }

        public bool HasKnownTargetSamples => _targetsFullyKnown;

        public void Report(long blocksProcessed, long samplesProcessed, bool force = false, bool completed = false)
        {
            if (_callback is null)
            {
                return;
            }

            if (!force
                && blocksProcessed == _lastReportedBlocks
                && _stopwatch.Elapsed < ProgressInterval)
            {
                return;
            }

            _lastReportedBlocks = blocksProcessed;
            _stopwatch.Restart();

            double progressPercent = 0d;
            bool isIndeterminate = false;
            if (completed)
            {
                progressPercent = 100d;
            }
            else if (_targetsFullyKnown)
            {
                progressPercent = Math.Min(100d, samplesProcessed * 100d / _totalTargetSamples);
            }
            else if (_totalBlocks > 0)
            {
                progressPercent = Math.Min(100d, blocksProcessed * 100d / _totalBlocks);
            }
            else
            {
                isIndeterminate = true;
            }

            long encodedBatchCount = _accumulators.Count == 0 ? 0L : _accumulators[0].BatchCount;
            string blockText = _totalBlocks > 0
                ? $"{blocksProcessed:N0}/{_totalBlocks:N0} 块"
                : $"{blocksProcessed:N0} 块";
            string sampleText = _targetsFullyKnown
                ? $"{samplesProcessed:N0}/{_totalTargetSamples:N0} 点"
                : $"{samplesProcessed:N0} 点";
            string modeText = CompressionReportFormatting.FormatBenchmarkReplayMode(_replayMode);
            string progressText = isIndeterminate ? "处理中" : $"{progressPercent:F1}%";
            string statusText = completed
                ? $"已完成基于已保存原始数据的压缩算法评估（{modeText}，通道 {_selectedChannelCount:N0}/{_totalCandidateChannelCount:N0}，{blockText}，{sampleText}，编码批次 {encodedBatchCount:N0}）。"
                : $"正在基于已保存原始数据评估各压缩算法性能（{modeText}，通道 {_selectedChannelCount:N0}/{_totalCandidateChannelCount:N0}，{blockText}，{sampleText}，编码批次 {encodedBatchCount:N0}，{progressText}）...";

            _callback(new CompressionBenchmarkProgress
            {
                Source = CompressionBenchmarkSource.RawCaptureReplay,
                ReplayMode = _replayMode,
                BlocksProcessed = blocksProcessed,
                TotalBlocks = _totalBlocks,
                SamplesProcessed = samplesProcessed,
                TargetSamples = _totalTargetSamples,
                EncodedBatchCount = encodedBatchCount,
                SelectedChannelCount = _selectedChannelCount,
                TotalCandidateChannelCount = _totalCandidateChannelCount,
                ProgressPercent = progressPercent,
                IsIndeterminate = isIndeterminate,
                StatusText = statusText
            });
        }
    }

    private sealed class ReplayChannelBuffer
    {
        private readonly double[] _buffer;
        private int _count;

        public ReplayChannelBuffer(int batchSize)
        {
            _buffer = new double[Math.Max(1, batchSize)];
        }

        public void Append(float sample, Action<double[]> onFullBatch)
        {
            _buffer[_count++] = sample;
            if (_count < _buffer.Length)
            {
                return;
            }

            onFullBatch(_buffer);
            _count = 0;
        }

        public void FlushRemaining(Action<double[]> onBatch)
        {
            if (_count == 0)
            {
                return;
            }

            var finalBatch = new double[_count];
            Array.Copy(_buffer, finalBatch, _count);
            _count = 0;
            onBatch(finalBatch);
        }
    }

    private sealed class AlgorithmAccumulator
    {
        private const int MaxLatencySampleCount = 4096;
        private readonly List<double> _encodeLatencyMsSamples = new();

        private long _batchCount;
        private long _sampleCount;
        private long _rawBytes;
        private long _codecBytes;
        private long _payloadBytes;
        private double _encodeSeconds;

        public AlgorithmAccumulator(CompressionType algorithm)
        {
            Algorithm = algorithm;
        }

        public CompressionType Algorithm { get; }

        public long BatchCount => _batchCount;

        public void RecordBatch(double[] batch, PreprocessType preprocessType, CompressionOptions compressionOptions)
        {
            var sw = Stopwatch.StartNew();
            var encodeResult = StorageCodec.EncodeWithMetrics(batch, Algorithm, preprocessType, compressionOptions);
            sw.Stop();

            _batchCount++;
            _sampleCount += batch.Length;
            _rawBytes += encodeResult.RawBytes;
            _codecBytes += encodeResult.CodecBytes;
            _payloadBytes += encodeResult.PayloadBytes;
            _encodeSeconds += sw.Elapsed.TotalSeconds;
            AppendLatency(_encodeLatencyMsSamples, sw.Elapsed.TotalMilliseconds, _batchCount);
        }

        public CompressionBenchmarkRow ToRow(CompressionSessionSnapshot snapshot)
        {
            return new CompressionBenchmarkRow
            {
                CompressionType = Algorithm,
                PreprocessType = snapshot.PreprocessType,
                CompressionOptions = snapshot.CompressionOptions.Clone(),
                BatchCount = _batchCount,
                SampleCount = _sampleCount,
                RawBytes = _rawBytes,
                CodecBytes = _codecBytes,
                TdmsPayloadBytes = _payloadBytes,
                EstimatedStoredBytes = EstimateStoredBytes(snapshot.StoredBytes, snapshot.TdmsPayloadBytes, _payloadBytes),
                EncodeSeconds = _encodeSeconds,
                EncodeLatencyMsSamples = _encodeLatencyMsSamples.ToArray(),
                IsCurrentAlgorithm = Algorithm == snapshot.CompressionType,
            };
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

        private static long EstimateStoredBytes(long actualStoredBytes, long actualPayloadBytes, long newPayloadBytes)
        {
            if (actualStoredBytes <= 0)
            {
                return newPayloadBytes;
            }

            long fixedOverhead = Math.Max(0, actualStoredBytes - actualPayloadBytes);
            return fixedOverhead + newPayloadBytes;
        }
    }
}
