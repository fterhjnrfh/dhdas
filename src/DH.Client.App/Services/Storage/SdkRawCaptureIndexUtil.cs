using System;
using System.Collections.Generic;
using System.IO;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCaptureIndexFormat
{
    public const string FileSuffix = ".idx";
    public const ulong FileMagic = 0x3149585741524844UL;
    public const int FormatVersion = 1;

    public static string GetIndexPath(string capturePath)
        => Path.ChangeExtension(capturePath, FileSuffix);

    public static void Save(string capturePath, double sampleRateHz, IReadOnlyList<SdkRawCaptureIndexEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        ArgumentNullException.ThrowIfNull(entries);

        string indexPath = GetIndexPath(capturePath);
        long captureFileBytes = File.Exists(capturePath)
            ? new FileInfo(capturePath).Length
            : 0L;

        using var stream = new FileStream(
            indexPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream);

        writer.Write(FileMagic);
        writer.Write(FormatVersion);
        writer.Write(DateTime.UtcNow.Ticks);
        writer.Write(sampleRateHz);
        writer.Write(captureFileBytes);
        writer.Write(entries.Count);
        writer.Write(Path.GetFileName(capturePath) ?? string.Empty);

        foreach (var entry in entries)
        {
            writer.Write(entry.BlockOffset);
            writer.Write(entry.PayloadOffset);
            writer.Write(entry.PayloadBytes);
            writer.Write(entry.DeviceId);
            writer.Write(entry.GroupId);
            writer.Write(entry.MachineId);
            writer.Write(entry.ChannelCount);
            writer.Write(entry.DataCountPerChannel);
            writer.Write(entry.BlockIndex);
            writer.Write(entry.TotalDataCount);
            writer.Write(entry.FirstSampleIndexPerChannel);
            writer.Write(entry.SampleTime);
            writer.Write(entry.ReceivedAtUtcTicks);
        }
    }

    public static bool TryLoad(string capturePath, out SdkRawCaptureIndex index)
    {
        index = new SdkRawCaptureIndex();

        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return false;
        }

        string indexPath = GetIndexPath(capturePath);
        if (!File.Exists(indexPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            ulong magic = reader.ReadUInt64();
            if (magic != FileMagic)
            {
                return false;
            }

            int version = reader.ReadInt32();
            if (version != FormatVersion)
            {
                return false;
            }

            long createdAtUtcTicks = reader.ReadInt64();
            double sampleRateHz = reader.ReadDouble();
            long captureFileBytes = reader.ReadInt64();
            int entryCount = reader.ReadInt32();
            string captureFileName = reader.ReadString();

            if (entryCount < 0)
            {
                return false;
            }

            if (File.Exists(capturePath))
            {
                long actualCaptureFileBytes = new FileInfo(capturePath).Length;
                if (captureFileBytes > 0 && actualCaptureFileBytes != captureFileBytes)
                {
                    return false;
                }
            }

            var entries = new List<SdkRawCaptureIndexEntry>(entryCount);
            for (int i = 0; i < entryCount; i++)
            {
                entries.Add(new SdkRawCaptureIndexEntry(
                    reader.ReadInt64(),
                    reader.ReadInt64(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt64(),
                    reader.ReadInt64(),
                    reader.ReadInt64(),
                    reader.ReadInt64()));
            }

            index = new SdkRawCaptureIndex
            {
                CaptureFileName = captureFileName,
                CreatedAtUtc = new DateTime(createdAtUtcTicks, DateTimeKind.Utc),
                SampleRateHz = sampleRateHz,
                CaptureFileBytes = captureFileBytes,
                Entries = entries
            };
            return true;
        }
        catch
        {
            index = new SdkRawCaptureIndex();
            return false;
        }
    }
}

internal sealed class SdkRawCaptureIndex
{
    public string CaptureFileName { get; init; } = "";

    public DateTime CreatedAtUtc { get; init; }

    public double SampleRateHz { get; init; }

    public long CaptureFileBytes { get; init; }

    public IReadOnlyList<SdkRawCaptureIndexEntry> Entries { get; init; } = Array.Empty<SdkRawCaptureIndexEntry>();
}

internal readonly struct SdkRawCaptureIndexEntry
{
    public long BlockOffset { get; }

    public long PayloadOffset { get; }

    public int PayloadBytes { get; }

    public int DeviceId { get; }

    public int GroupId { get; }

    public int MachineId { get; }

    public int ChannelCount { get; }

    public int DataCountPerChannel { get; }

    public int BlockIndex { get; }

    public long TotalDataCount { get; }

    public long FirstSampleIndexPerChannel { get; }

    public long SampleTime { get; }

    public long ReceivedAtUtcTicks { get; }

    public SdkRawCaptureIndexEntry(
        long blockOffset,
        long payloadOffset,
        int payloadBytes,
        int deviceId,
        int groupId,
        int machineId,
        int channelCount,
        int dataCountPerChannel,
        int blockIndex,
        long totalDataCount,
        long firstSampleIndexPerChannel,
        long sampleTime,
        long receivedAtUtcTicks)
    {
        BlockOffset = blockOffset;
        PayloadOffset = payloadOffset;
        PayloadBytes = payloadBytes;
        DeviceId = deviceId;
        GroupId = groupId;
        MachineId = machineId;
        ChannelCount = channelCount;
        DataCountPerChannel = dataCountPerChannel;
        BlockIndex = blockIndex;
        TotalDataCount = totalDataCount;
        FirstSampleIndexPerChannel = firstSampleIndexPerChannel;
        SampleTime = sampleTime;
        ReceivedAtUtcTicks = receivedAtUtcTicks;
    }
}
