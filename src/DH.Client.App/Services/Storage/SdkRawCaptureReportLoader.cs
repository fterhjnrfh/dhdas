using System;
using System.IO;
using System.Linq;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCaptureReportLoader
{
    public static CompressionSessionSnapshot LoadSnapshot(string capturePath)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        if (!File.Exists(capturePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", capturePath);
        }

        if (!SdkRawCaptureFormat.IsRawCaptureFile(capturePath))
        {
            throw new InvalidOperationException("The selected file is not a supported SDK raw capture.");
        }

        if (!SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest) || manifest == null)
        {
            throw new InvalidOperationException($"Raw capture manifest was not found: {SdkRawCaptureFormat.GetManifestPath(capturePath)}");
        }

        long storedBytes = manifest.CaptureFileBytes > 0
            ? manifest.CaptureFileBytes
            : new FileInfo(capturePath).Length;
        long codecPayloadBytes = manifest.CodecPayloadBytes > 0
            ? manifest.CodecPayloadBytes
            : manifest.RawPayloadBytes;

        var deviceBatchCounts = manifest.DeviceIntegrity
            .GroupBy(device => device.DeviceId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(device => Math.Max(0L, device.BlockCount)));

        var channels = manifest.ChannelSampleCounts
            .OrderBy(kvp => ChannelNaming.ParseChannelName(kvp.Key))
            .Select(kvp =>
            {
                int channelId = ChannelNaming.ParseChannelName(kvp.Key);
                long sampleCount = Math.Max(0L, kvp.Value);
                long rawBytes = sampleCount * sizeof(float);
                double rawShare = manifest.RawPayloadBytes > 0
                    ? (double)rawBytes / manifest.RawPayloadBytes
                    : 0d;
                int deviceId = channelId > 0 ? ChannelNaming.GetDeviceId(channelId) : 0;
                long batchCount = deviceBatchCounts.TryGetValue(deviceId, out var deviceBlockCount)
                    ? deviceBlockCount
                    : Math.Max(0L, manifest.BlockCount);

                return new CompressionChannelSnapshot
                {
                    ChannelId = channelId,
                    BatchCount = batchCount,
                    SampleCount = sampleCount,
                    RawBytes = rawBytes,
                    CodecBytes = codecPayloadBytes > 0
                        ? (long)Math.Round(codecPayloadBytes * rawShare)
                        : rawBytes,
                    TdmsPayloadBytes = codecPayloadBytes > 0
                        ? (long)Math.Round(codecPayloadBytes * rawShare)
                        : rawBytes,
                    EncodeSeconds = manifest.EncodeSeconds > 0d
                        ? manifest.EncodeSeconds * rawShare
                        : 0d,
                    WriteSeconds = manifest.WriteSeconds > 0d
                        ? manifest.WriteSeconds * rawShare
                        : 0d
                };
            })
            .Where(channel => channel.ChannelId > 0)
            .ToArray();

        return new CompressionSessionSnapshot
        {
            SessionName = manifest.SessionName,
            StorageMode = CompressionStorageMode.SingleFile,
            PayloadKind = CompressionPayloadKind.RawCaptureBlockPayload,
            CompressionType = manifest.CompressionType,
            PreprocessType = manifest.PreprocessType,
            CompressionOptions = manifest.CompressionOptions?.Clone() ?? new CompressionOptions(),
            SampleRateHz = manifest.SampleRateHz,
            ChannelCount = Math.Max(manifest.ObservedChannelCount, manifest.ExpectedChannelCount),
            BatchCount = Math.Max(0L, manifest.BlockCount),
            TotalSamples = Math.Max(0L, manifest.TotalSamples),
            RawBytes = Math.Max(0L, manifest.RawPayloadBytes),
            CodecBytes = codecPayloadBytes,
            TdmsPayloadBytes = codecPayloadBytes,
            StoredBytes = storedBytes,
            EncodeSeconds = Math.Max(0d, manifest.EncodeSeconds),
            WriteSeconds = Math.Max(0d, manifest.WriteSeconds),
            StartedAt = manifest.StartedAtUtc.ToLocalTime(),
            StoppedAt = manifest.StoppedAtUtc.ToLocalTime(),
            Elapsed = manifest.StoppedAtUtc >= manifest.StartedAtUtc
                ? manifest.StoppedAtUtc - manifest.StartedAtUtc
                : TimeSpan.Zero,
            Channels = channels,
            ChannelMetricsEstimated = true,
            WrittenFiles = new[] { capturePath },
            BenchmarkSource = CompressionBenchmarkSource.RawCaptureReplay,
            BenchmarkSourcePath = capturePath,
            BenchmarkBatchSize = CompressionBenchmarkDefaults.BatchSize,
            BenchmarkSamples = Array.Empty<CompressionBenchmarkSample>()
        };
    }
}
