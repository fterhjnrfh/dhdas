using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DH.Contracts;
using DH.Contracts.Models;

namespace DH.Client.App.Services.Storage;

public sealed class SdkRawChannelDescriptor
{
    public string DeviceDisplayName { get; init; } = "";

    public string ChannelName { get; init; } = "";

    public string FilePath { get; init; } = "";

    public int ChannelId { get; init; }

    public int DeviceId { get; init; }

    public long SampleCount { get; init; }

    public double SampleRateHz { get; init; }
}

public static class SdkRawCaptureReaderUtil
{
    public static IReadOnlyDictionary<string, IReadOnlyList<SdkRawChannelDescriptor>> ListDevicesAndChannels(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", filePath);
        }

        var index = SdkRawCaptureIndexFormat.LoadOrBuild(filePath);
        SdkRawCaptureFormat.TryLoadManifest(filePath, out var manifest);

        var sampleCounts = ResolveChannelSampleCounts(index, manifest);
        var descriptors = sampleCounts
            .Select(kvp =>
            {
                int channelId = ChannelNaming.ParseChannelName(kvp.Key);
                if (channelId <= 0 && kvp.Key.StartsWith("AI00_", StringComparison.OrdinalIgnoreCase))
                {
                    channelId = ChannelNaming.ParseChannelName(kvp.Key);
                }

                return new
                {
                    ChannelId = channelId,
                    SampleCount = kvp.Value
                };
            })
            .Where(item => item.ChannelId >= 0)
            .Select(item => new SdkRawChannelDescriptor
            {
                DeviceId = ChannelNaming.GetDeviceId(item.ChannelId),
                DeviceDisplayName = BuildDeviceDisplayName(ChannelNaming.GetDeviceId(item.ChannelId)),
                ChannelId = item.ChannelId,
                ChannelName = ChannelNaming.ChannelName(item.ChannelId),
                FilePath = filePath,
                SampleCount = item.SampleCount,
                SampleRateHz = ResolveSampleRate(index, manifest)
            })
            .GroupBy(descriptor => descriptor.DeviceDisplayName)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SdkRawChannelDescriptor>)group
                    .OrderBy(item => item.ChannelId)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return descriptors;
    }

    public static Dictionary<int, IReadOnlyList<CurvePoint>> ReadChannelCurves(
        string filePath,
        IReadOnlyCollection<int> channelIds,
        int maxPoints = 100_000)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", filePath);
        }

        if (channelIds == null || channelIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<CurvePoint>>();
        }

        var index = SdkRawCaptureIndexFormat.LoadOrBuild(filePath);
        SdkRawCaptureFormat.TryLoadManifest(filePath, out var manifest);
        var sampleCounts = ResolveChannelSampleCounts(index, manifest);
        double fallbackSampleRateHz = ResolveSampleRate(index, manifest);

        var requestsByDevice = channelIds
            .Distinct()
            .Select(channelId => CreateReadRequest(channelId, sampleCounts, index, fallbackSampleRateHz, maxPoints))
            .GroupBy(request => request.DeviceId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(request => request.ChannelOffset).ToList());

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] payloadBuffer = Array.Empty<byte>();

        foreach (var entry in index.Blocks.OrderBy(item => item.Offset))
        {
            if (!requestsByDevice.TryGetValue(entry.DeviceId, out var requests))
            {
                continue;
            }

            int requiredBytes = Math.Max(0, entry.PayloadBytes);
            if (requiredBytes == 0)
            {
                continue;
            }

            if (payloadBuffer.Length < requiredBytes)
            {
                payloadBuffer = new byte[requiredBytes];
            }

            stream.Position = entry.PayloadOffset;
            ReadExactly(stream, payloadBuffer, requiredBytes);
            var payload = MemoryMarshal.Cast<byte, float>(payloadBuffer.AsSpan(0, requiredBytes));

            for (int localSampleIndex = 0; localSampleIndex < entry.DataCountPerChannel; localSampleIndex++)
            {
                long globalSampleIndex = entry.SampleStartPerChannel + localSampleIndex;

                foreach (var request in requests)
                {
                    if (request.ChannelOffset >= entry.ChannelCount)
                    {
                        continue;
                    }

                    if ((globalSampleIndex % request.Stride) != 0
                        && globalSampleIndex != request.TotalSamples - 1)
                    {
                        continue;
                    }

                    int payloadIndex = (localSampleIndex * entry.ChannelCount) + request.ChannelOffset;
                    if ((uint)payloadIndex >= (uint)payload.Length)
                    {
                        continue;
                    }

                    double x = request.SampleRateHz > 0d
                        ? globalSampleIndex / request.SampleRateHz
                        : globalSampleIndex;
                    request.Points.Add(new CurvePoint(x, payload[payloadIndex]));
                }
            }
        }

        return requestsByDevice.Values
            .SelectMany(static requests => requests)
            .ToDictionary(
                request => request.ChannelId,
                request => (IReadOnlyList<CurvePoint>)request.Points,
                EqualityComparer<int>.Default);
    }

    private static ChannelReadRequest CreateReadRequest(
        int channelId,
        IReadOnlyDictionary<string, long> sampleCounts,
        SdkRawCaptureIndex index,
        double fallbackSampleRateHz,
        int maxPoints)
    {
        int deviceId = ChannelNaming.GetDeviceId(channelId);
        int channelNumber = ChannelNaming.GetChannelNumber(channelId);
        string channelName = ChannelNaming.ChannelName(channelId);
        long totalSamples = sampleCounts.TryGetValue(channelName, out long samplesFromManifest)
            ? samplesFromManifest
            : index.Devices.FirstOrDefault(device => device.DeviceId == deviceId)?.SamplesPerChannel ?? 0L;
        int stride = totalSamples > maxPoints && maxPoints > 0
            ? (int)Math.Ceiling((double)totalSamples / maxPoints)
            : 1;

        return new ChannelReadRequest(
            channelId,
            deviceId,
            channelNumber - 1,
            Math.Max(1, stride),
            Math.Max(0L, totalSamples),
            fallbackSampleRateHz);
    }

    private static IReadOnlyDictionary<string, long> ResolveChannelSampleCounts(
        SdkRawCaptureIndex index,
        SdkRawCaptureManifest? manifest)
    {
        if (manifest?.ChannelSampleCounts != null && manifest.ChannelSampleCounts.Count > 0)
        {
            return manifest.ChannelSampleCounts;
        }

        if (index.ChannelSampleCounts.Count > 0)
        {
            return index.ChannelSampleCounts;
        }

        var fallback = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in index.Devices)
        {
            for (int channelNumber = 1; channelNumber <= Math.Max(0, device.ChannelCount); channelNumber++)
            {
                int channelId = ChannelNaming.MakeChannelId(device.DeviceId, channelNumber);
                fallback[ChannelNaming.ChannelName(channelId)] = device.SamplesPerChannel;
            }
        }

        return fallback;
    }

    private static double ResolveSampleRate(SdkRawCaptureIndex index, SdkRawCaptureManifest? manifest)
    {
        if (manifest?.SampleRateHz > 0d)
        {
            return manifest.SampleRateHz;
        }

        if (index.SampleRateHz > 0d)
        {
            return index.SampleRateHz;
        }

        return 0d;
    }

    private static string BuildDeviceDisplayName(int deviceId)
        => $"设备 {deviceId} ({ChannelNaming.DeviceDisplayName(deviceId)})";

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of raw capture while reading payload.");
            }

            offset += read;
        }
    }

    private sealed class ChannelReadRequest
    {
        public int ChannelId { get; }
        public int DeviceId { get; }
        public int ChannelOffset { get; }
        public int Stride { get; }
        public long TotalSamples { get; }
        public double SampleRateHz { get; }
        public List<CurvePoint> Points { get; } = new();

        public ChannelReadRequest(
            int channelId,
            int deviceId,
            int channelOffset,
            int stride,
            long totalSamples,
            double sampleRateHz)
        {
            ChannelId = channelId;
            DeviceId = deviceId;
            ChannelOffset = Math.Max(0, channelOffset);
            Stride = Math.Max(1, stride);
            TotalSamples = Math.Max(0L, totalSamples);
            SampleRateHz = sampleRateHz;
        }
    }
}
