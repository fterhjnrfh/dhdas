using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal sealed class CompressionBenchmarkInputBuilder
{
    private const int BenchmarkBatchSize = CompressionBenchmarkDefaults.BatchSize;

    public CompressionSessionSnapshot BuildSnapshot(
        string filePath,
        CompressionBenchmarkReplayMode requestedReplayMode,
        CompressionType selectedCompressionType,
        PreprocessType selectedPreprocessType,
        CompressionOptions compressionOptions)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Benchmark file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Benchmark file not found.", filePath);
        }

        if (SdkRawCaptureFormat.IsRawCaptureFile(filePath))
        {
            return BuildRawCaptureSnapshot(
                filePath,
                requestedReplayMode,
                selectedCompressionType,
                selectedPreprocessType,
                compressionOptions);
        }

        string extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".tdms", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tdm", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTdmsSnapshot(
                filePath,
                requestedReplayMode,
                selectedCompressionType,
                selectedPreprocessType,
                compressionOptions);
        }

        if (string.Equals(extension, ".h5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hdf5", StringComparison.OrdinalIgnoreCase))
        {
            return BuildHdf5Snapshot(
                filePath,
                requestedReplayMode,
                selectedCompressionType,
                selectedPreprocessType,
                compressionOptions);
        }

        throw new InvalidOperationException("Unsupported benchmark file type. Please choose .sdkraw.bin, .tdms, .tdm, .h5, or .hdf5.");
    }

    public static bool IsSupportedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (SdkRawCaptureFormat.IsRawCaptureFile(filePath))
        {
            return true;
        }

        string extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".tdms", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tdm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".h5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hdf5", StringComparison.OrdinalIgnoreCase);
    }

    private static CompressionSessionSnapshot BuildRawCaptureSnapshot(
        string filePath,
        CompressionBenchmarkReplayMode requestedReplayMode,
        CompressionType selectedCompressionType,
        PreprocessType selectedPreprocessType,
        CompressionOptions compressionOptions)
    {
        SdkRawCaptureFormat.TryLoadManifest(filePath, out var manifest);
        var channelIds = SdkRawCaptureConverter.ResolveChannelIds(filePath, manifest);
        var fileInfo = new FileInfo(filePath);

        var channelSampleCounts = manifest?.ChannelSampleCounts?
            .ToDictionary(
                kvp => ChannelNaming.ParseChannelName(kvp.Key),
                kvp => kvp.Value)
            ?? new Dictionary<int, long>();

        var channelSnapshots = channelIds
            .Select(channelId =>
            {
                channelSampleCounts.TryGetValue(channelId, out long sampleCount);
                long rawBytes = sampleCount > 0 ? sampleCount * sizeof(double) : 0L;
                return new CompressionChannelSnapshot
                {
                    ChannelId = channelId,
                    BatchCount = sampleCount > 0 ? (sampleCount + BenchmarkBatchSize - 1) / BenchmarkBatchSize : 0L,
                    SampleCount = sampleCount,
                    RawBytes = rawBytes,
                    CodecBytes = rawBytes,
                    TdmsPayloadBytes = rawBytes,
                    EncodeSeconds = 0d,
                    WriteSeconds = 0d
                };
            })
            .ToArray();

        long totalSamples = channelSnapshots.Sum(channel => channel.SampleCount);
        long rawBytes = totalSamples * sizeof(double);
        var effectiveReplayMode = ResolveReplayMode(requestedReplayMode, fileInfo.Length, channelIds.Count, totalSamples);
        DateTime timestamp = fileInfo.LastWriteTime;

        return new CompressionSessionSnapshot
        {
            SessionName = Path.GetFileName(filePath),
            StorageMode = CompressionStorageMode.SingleFile,
            CompressionType = selectedCompressionType,
            PreprocessType = selectedPreprocessType,
            CompressionOptions = compressionOptions.Clone(),
            SampleRateHz = manifest?.SampleRateHz ?? 0d,
            ChannelCount = channelIds.Count,
            BatchCount = manifest?.BlockCount ?? 0L,
            TotalSamples = totalSamples,
            RawBytes = rawBytes,
            CodecBytes = rawBytes,
            TdmsPayloadBytes = rawBytes,
            StoredBytes = rawBytes,
            EncodeSeconds = 0d,
            WriteSeconds = 0d,
            StartedAt = timestamp,
            StoppedAt = timestamp,
            Elapsed = TimeSpan.Zero,
            Channels = channelSnapshots,
            BenchmarkSource = CompressionBenchmarkSource.RawCaptureReplay,
            BenchmarkSourcePath = filePath,
            BenchmarkBatchSize = BenchmarkBatchSize,
            BenchmarkReplayMode = effectiveReplayMode,
            WrittenFiles = new[] { filePath }
        };
    }

    private static CompressionSessionSnapshot BuildTdmsSnapshot(
        string filePath,
        CompressionBenchmarkReplayMode requestedReplayMode,
        CompressionType selectedCompressionType,
        PreprocessType selectedPreprocessType,
        CompressionOptions compressionOptions)
    {
        var groups = TdmsReaderUtil.ListGroupsAndChannels(filePath);
        var descriptors = groups
            .SelectMany(
                group => group.Value.Select(channelName => new TdmsBenchmarkChannelDescriptor(
                    group.Key,
                    channelName,
                    ResolveChannelId($"{group.Key}/{channelName}", channelName))))
            .ToList();

        if (descriptors.Count == 0)
        {
            throw new InvalidOperationException("The selected TDMS file does not contain readable channels.");
        }

        var fileInfo = new FileInfo(filePath);
        var effectiveReplayMode = ResolveReplayMode(requestedReplayMode, fileInfo.Length, descriptors.Count, 0L);
        var selectedChannelIds = SelectChannelIdsForReplayMode(descriptors.Select(item => item.ChannelId).ToList(), effectiveReplayMode);
        var selectedDescriptors = descriptors
            .Where(descriptor => selectedChannelIds.Contains(descriptor.ChannelId))
            .ToList();

        return BuildSampledSnapshot(
            filePath,
            effectiveReplayMode,
            selectedCompressionType,
            selectedPreprocessType,
            compressionOptions,
            selectedDescriptors.Count,
            selectedDescriptors.Select(descriptor =>
            {
                double[] samples = TdmsReaderUtil.ReadChannelData(filePath, descriptor.GroupName, descriptor.ChannelName);
                var properties = TdmsReaderUtil.ReadChannelProperties(filePath, descriptor.GroupName, descriptor.ChannelName);
                return new BenchmarkChannelData(
                    descriptor.ChannelId,
                    descriptor.ChannelName,
                    samples,
                    ResolveSampleRateHz(properties));
            }));
    }

    private static CompressionSessionSnapshot BuildHdf5Snapshot(
        string filePath,
        CompressionBenchmarkReplayMode requestedReplayMode,
        CompressionType selectedCompressionType,
        PreprocessType selectedPreprocessType,
        CompressionOptions compressionOptions)
    {
        var descriptors = Hdf5ReaderUtil.ListDevicesAndChannels(filePath)
            .SelectMany(group => group.Value)
            .ToList();

        if (descriptors.Count == 0)
        {
            throw new InvalidOperationException("The selected HDF5 file does not contain readable channels.");
        }

        var fileInfo = new FileInfo(filePath);
        long totalSamples = descriptors.Sum(descriptor => Math.Max(0L, descriptor.SampleCount));
        var effectiveReplayMode = ResolveReplayMode(requestedReplayMode, fileInfo.Length, descriptors.Count, totalSamples);
        var selectedChannelIds = SelectChannelIdsForReplayMode(descriptors.Select(item => item.ChannelId).ToList(), effectiveReplayMode);
        var selectedDescriptors = descriptors
            .Where(descriptor => selectedChannelIds.Contains(descriptor.ChannelId))
            .ToList();

        return BuildSampledSnapshot(
            filePath,
            effectiveReplayMode,
            selectedCompressionType,
            selectedPreprocessType,
            compressionOptions,
            selectedDescriptors.Count,
            selectedDescriptors.Select(descriptor =>
            {
                double[] samples = Hdf5ReaderUtil.ReadChannelData(descriptor.FilePath);
                var properties = Hdf5ReaderUtil.ReadChannelProperties(descriptor.FilePath);
                return new BenchmarkChannelData(
                    descriptor.ChannelId,
                    descriptor.ChannelName,
                    samples,
                    ResolveSampleRateHz(properties));
            }));
    }

    private static CompressionSessionSnapshot BuildSampledSnapshot(
        string filePath,
        CompressionBenchmarkReplayMode replayMode,
        CompressionType selectedCompressionType,
        PreprocessType selectedPreprocessType,
        CompressionOptions compressionOptions,
        int selectedChannelCount,
        IEnumerable<BenchmarkChannelData> channels)
    {
        long sampleCapPerChannel = replayMode == CompressionBenchmarkReplayMode.Fast
            ? (long)BenchmarkBatchSize * CompressionBenchmarkDefaults.FastModeMaxBatchesPerChannel
            : long.MaxValue;

        var benchmarkSamples = new List<CompressionBenchmarkSample>();
        var channelSnapshots = new List<CompressionChannelSnapshot>();
        long totalSamples = 0L;
        long rawBytes = 0L;
        long batchCount = 0L;
        double sampleRateHz = 0d;

        foreach (var channel in channels)
        {
            int sampleCount = (int)Math.Min(channel.Samples.Length, sampleCapPerChannel);
            if (sampleCount <= 0)
            {
                continue;
            }

            if (sampleRateHz <= 0d && channel.SampleRateHz > 0d)
            {
                sampleRateHz = channel.SampleRateHz;
            }

            long channelRawBytes = (long)sampleCount * sizeof(double);
            long channelBatchCount = (sampleCount + BenchmarkBatchSize - 1) / BenchmarkBatchSize;
            totalSamples += sampleCount;
            rawBytes += channelRawBytes;
            batchCount += channelBatchCount;

            channelSnapshots.Add(new CompressionChannelSnapshot
            {
                ChannelId = channel.ChannelId,
                BatchCount = channelBatchCount,
                SampleCount = sampleCount,
                RawBytes = channelRawBytes,
                CodecBytes = channelRawBytes,
                TdmsPayloadBytes = channelRawBytes,
                EncodeSeconds = 0d,
                WriteSeconds = 0d
            });

            for (int offset = 0; offset < sampleCount; offset += BenchmarkBatchSize)
            {
                int length = Math.Min(BenchmarkBatchSize, sampleCount - offset);
                var batch = new double[length];
                Array.Copy(channel.Samples, offset, batch, 0, length);
                benchmarkSamples.Add(new CompressionBenchmarkSample
                {
                    ChannelId = channel.ChannelId,
                    Samples = batch
                });
            }
        }

        if (benchmarkSamples.Count == 0)
        {
            throw new InvalidOperationException("The selected file does not contain enough samples for benchmarking.");
        }

        DateTime timestamp = File.GetLastWriteTime(filePath);
        return new CompressionSessionSnapshot
        {
            SessionName = Path.GetFileName(filePath),
            StorageMode = CompressionStorageMode.SingleFile,
            CompressionType = selectedCompressionType,
            PreprocessType = selectedPreprocessType,
            CompressionOptions = compressionOptions.Clone(),
            SampleRateHz = sampleRateHz,
            ChannelCount = selectedChannelCount,
            BatchCount = batchCount,
            TotalSamples = totalSamples,
            RawBytes = rawBytes,
            CodecBytes = rawBytes,
            TdmsPayloadBytes = rawBytes,
            StoredBytes = rawBytes,
            EncodeSeconds = 0d,
            WriteSeconds = 0d,
            StartedAt = timestamp,
            StoppedAt = timestamp,
            Elapsed = TimeSpan.Zero,
            Channels = channelSnapshots.ToArray(),
            BenchmarkSamples = benchmarkSamples.ToArray(),
            BenchmarkSource = CompressionBenchmarkSource.SampledBatches,
            BenchmarkSourcePath = filePath,
            BenchmarkBatchSize = BenchmarkBatchSize,
            BenchmarkReplayMode = replayMode,
            WrittenFiles = new[] { filePath }
        };
    }

    private static CompressionBenchmarkReplayMode ResolveReplayMode(
        CompressionBenchmarkReplayMode requestedReplayMode,
        long fileBytes,
        int channelCount,
        long totalSamples)
    {
        if (requestedReplayMode != CompressionBenchmarkReplayMode.Auto)
        {
            return requestedReplayMode;
        }

        if (fileBytes > CompressionBenchmarkDefaults.AutoFullMaxRawBytes
            || channelCount > CompressionBenchmarkDefaults.AutoFullMaxChannels
            || totalSamples > CompressionBenchmarkDefaults.AutoFullMaxSamples)
        {
            return CompressionBenchmarkReplayMode.Fast;
        }

        return CompressionBenchmarkReplayMode.Full;
    }

    private static List<int> SelectChannelIdsForReplayMode(IReadOnlyList<int> channelIds, CompressionBenchmarkReplayMode replayMode)
    {
        if (replayMode != CompressionBenchmarkReplayMode.Fast
            || channelIds.Count <= CompressionBenchmarkDefaults.FastModeMaxChannels)
        {
            return channelIds.Distinct().OrderBy(channelId => channelId).ToList();
        }

        int maxChannels = Math.Max(1, CompressionBenchmarkDefaults.FastModeMaxChannels);
        var ordered = channelIds.Distinct().OrderBy(channelId => channelId).ToList();
        var selected = new List<int>(maxChannels);
        double step = (ordered.Count - 1d) / Math.Max(1, maxChannels - 1);

        for (int index = 0; index < maxChannels; index++)
        {
            int sourceIndex = (int)Math.Round(index * step, MidpointRounding.AwayFromZero);
            sourceIndex = Math.Clamp(sourceIndex, 0, ordered.Count - 1);
            int channelId = ordered[sourceIndex];
            if (!selected.Contains(channelId))
            {
                selected.Add(channelId);
            }
        }

        return selected.OrderBy(channelId => channelId).ToList();
    }

    private static int ResolveChannelId(string fallbackKey, string channelName)
    {
        int parsed = ChannelNaming.ParseChannelName(channelName);
        return parsed > 0 ? parsed : Math.Abs(fallbackKey.GetHashCode());
    }

    private static double ResolveSampleRateHz(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue("sample_rate_hz", out object? sampleRate)
            && TryGetDouble(sampleRate, out double hz)
            && hz > 0d)
        {
            return hz;
        }

        if (properties.TryGetValue("wf_increment", out object? incrementValue)
            && TryGetDouble(incrementValue, out double increment)
            && increment > 0d)
        {
            return 1d / increment;
        }

        return 0d;
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case string s when double.TryParse(s, out double parsed):
                result = parsed;
                return true;
            default:
                result = 0d;
                return false;
        }
    }

    private sealed record TdmsBenchmarkChannelDescriptor(string GroupName, string ChannelName, int ChannelId);

    private sealed record BenchmarkChannelData(int ChannelId, string ChannelName, double[] Samples, double SampleRateHz);
}
