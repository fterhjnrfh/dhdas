using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCaptureReaderUtil
{
    public static IReadOnlyDictionary<string, string[]> ListGroupsAndChannels(string capturePath)
    {
        var channelIds = ResolveChannelIds(capturePath);
        return channelIds
            .GroupBy(ChannelNaming.TdmsGroupName)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(ChannelNaming.GetChannelNumber)
                    .Select(channelId => ChannelNaming.ChannelName(channelId))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static double[] ReadChannelData(string capturePath, string groupName, string channelName)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            throw new ArgumentException("Channel name is required.", nameof(channelName));
        }

        int channelId = ChannelNaming.ParseChannelName(channelName);
        if (channelId < 0)
        {
            throw new InvalidOperationException($"Unsupported raw channel name: {channelName}");
        }

        int deviceId = ChannelNaming.GetDeviceId(channelId);
        int channelNumber = ChannelNaming.GetChannelNumber(channelId);
        var fileHeader = ReadFileHeader(capturePath);

        if (SdkRawCaptureIndexFormat.TryLoad(capturePath, out var index)
            && index.Entries.Count > 0)
        {
            return ReadChannelDataFromIndex(capturePath, index, fileHeader, deviceId, channelNumber);
        }

        return ReadChannelDataSequential(capturePath, fileHeader, deviceId, channelNumber);
    }

    public static IReadOnlyDictionary<string, object> ReadChannelProperties(string capturePath, string groupName, string channelName)
    {
        double sampleRateHz = ReadSampleRateHz(capturePath);
        double increment = sampleRateHz > 0d ? 1d / sampleRateHz : 1d;

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = channelName,
            ["group"] = string.IsNullOrWhiteSpace(groupName) ? ChannelNaming.TdmsGroupName(ChannelNaming.ParseChannelName(channelName)) : groupName,
            ["wf_xname"] = "Time",
            ["wf_xunit_string"] = "s",
            ["wf_increment"] = increment,
            ["wf_start_offset"] = 0d
        };
    }

    private static List<int> ResolveChannelIds(string capturePath)
    {
        if (SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest)
            && manifest?.ChannelSampleCounts?.Count > 0)
        {
            var ids = manifest.ChannelSampleCounts.Keys
                .Select(ChannelNaming.ParseChannelName)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (ids.Count > 0)
            {
                return ids;
            }
        }

        if (SdkRawCaptureIndexFormat.TryLoad(capturePath, out var index)
            && index.Entries.Count > 0)
        {
            return index.Entries
                .GroupBy(entry => entry.DeviceId)
                .OrderBy(group => group.Key)
                .SelectMany(group =>
                {
                    int channelCount = group.Max(entry => entry.ChannelCount);
                    return Enumerable.Range(1, Math.Max(0, channelCount))
                        .Select(channelNumber => ChannelNaming.MakeChannelId(group.Key, channelNumber));
                })
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        return SdkRawCaptureConverter.ResolveChannelIds(capturePath, manifest: null);
    }

    private static double[] ReadChannelDataFromIndex(
        string capturePath,
        SdkRawCaptureIndex index,
        SdkRawCaptureFileHeaderInfo fileHeader,
        int deviceId,
        int channelNumber)
    {
        var entries = index.Entries
            .Where(entry => entry.DeviceId == deviceId && channelNumber <= entry.ChannelCount)
            .OrderBy(entry => entry.FirstSampleIndexPerChannel)
            .ToArray();
        if (entries.Length == 0)
        {
            return Array.Empty<double>();
        }

        int capacity = TryGetExpectedSampleCount(capturePath, ChannelNaming.ChannelName(deviceId, channelNumber), entries);
        var values = new List<double>(capacity);

        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] payloadBuffer = Array.Empty<byte>();
        foreach (var entry in entries)
        {
            if (entry.PayloadBytes <= 0)
            {
                continue;
            }

            if (payloadBuffer.Length < entry.PayloadBytes)
            {
                payloadBuffer = new byte[entry.PayloadBytes];
            }

            stream.Position = entry.PayloadOffset;
            ReadExactly(stream, payloadBuffer, entry.PayloadBytes);

            if (fileHeader.UsesEncodedPayload)
            {
                var interleaved = SdkRawCapturePayloadCodec.Decode(
                    payloadBuffer.AsSpan(0, entry.PayloadBytes),
                    entry.ChannelCount,
                    entry.DataCountPerChannel,
                    fileHeader.CompressionType,
                    fileHeader.PreprocessType);
                AppendChannelValues(values, interleaved, entry.ChannelCount, channelNumber - 1, entry.DataCountPerChannel);
            }
            else
            {
                var interleaved = MemoryMarshal.Cast<byte, float>(payloadBuffer.AsSpan(0, entry.PayloadBytes));
                AppendChannelValues(values, interleaved, entry.ChannelCount, channelNumber - 1, entry.DataCountPerChannel);
            }
        }

        return values.ToArray();
    }

    private static double[] ReadChannelDataSequential(string capturePath, SdkRawCaptureFileHeaderInfo fileHeader, int deviceId, int channelNumber)
    {
        int capacity = TryGetExpectedSampleCount(capturePath, ChannelNaming.ChannelName(deviceId, channelNumber), entries: null);
        var values = new List<double>(capacity);

        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        _ = SdkRawCaptureConverter.SkipFileHeader(reader);

        while (SdkRawCaptureConverter.TryReadRawBlock(reader, fileHeader, out var rawBlock))
        {
            int rawDeviceId = SdkRawCaptureWriter.ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);
            if (rawDeviceId != deviceId || channelNumber > rawBlock.ChannelCount)
            {
                continue;
            }

            AppendChannelValues(
                values,
                rawBlock.InterleavedSamples.AsSpan(0, rawBlock.PayloadFloatCount),
                rawBlock.ChannelCount,
                channelNumber - 1,
                rawBlock.DataCountPerChannel);
        }

        return values.ToArray();
    }

    private static int TryGetExpectedSampleCount(
        string capturePath,
        string channelName,
        IReadOnlyList<SdkRawCaptureIndexEntry>? entries)
    {
        if (SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest)
            && manifest?.ChannelSampleCounts != null
            && manifest.ChannelSampleCounts.TryGetValue(channelName, out long manifestSamples)
            && manifestSamples > 0
            && manifestSamples <= int.MaxValue)
        {
            return (int)manifestSamples;
        }

        if (entries != null)
        {
            long indexedSamples = entries.Sum(entry => (long)entry.DataCountPerChannel);
            if (indexedSamples > 0 && indexedSamples <= int.MaxValue)
            {
                return (int)indexedSamples;
            }
        }

        return 0;
    }

    private static double ReadSampleRateHz(string capturePath)
        => ReadFileHeader(capturePath).SampleRateHz;

    private static SdkRawCaptureFileHeaderInfo ReadFileHeader(string capturePath)
    {
        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        return SdkRawCaptureConverter.SkipFileHeader(reader);
    }

    private static void AppendChannelValues(
        List<double> destination,
        ReadOnlySpan<float> interleaved,
        int channelCount,
        int channelOffset,
        int samplesPerChannel)
    {
        if (channelCount <= 0 || channelOffset < 0 || channelOffset >= channelCount || samplesPerChannel <= 0)
        {
            return;
        }

        for (int sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
        {
            int sourceIndex = (sampleIndex * channelCount) + channelOffset;
            if ((uint)sourceIndex >= (uint)interleaved.Length)
            {
                break;
            }

            destination.Add(interleaved[sourceIndex]);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of file while reading indexed raw capture payload.");
            }

            offset += read;
        }
    }
}
