using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCaptureIndexFormat
{
    public const int IndexVersion = 1;

    public static string GetIndexPath(string capturePath)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return string.Empty;
        }

        if (capturePath.EndsWith(SdkRawCaptureFormat.FileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return capturePath[..^4] + ".idx.json";
        }

        return capturePath + ".idx.json";
    }

    public static bool TryLoadIndex(string capturePath, out SdkRawCaptureIndex? index)
    {
        index = null;

        try
        {
            string indexPath = GetIndexPath(capturePath);
            if (!File.Exists(indexPath))
            {
                return false;
            }

            var json = File.ReadAllText(indexPath);
            index = JsonSerializer.Deserialize<SdkRawCaptureIndex>(json);
            return IsUsable(index, capturePath);
        }
        catch
        {
            index = null;
            return false;
        }
    }

    public static SdkRawCaptureIndex LoadOrBuild(string capturePath)
    {
        if (TryLoadIndex(capturePath, out var existing) && existing != null)
        {
            return existing;
        }

        var rebuilt = BuildFromCapture(capturePath);
        TryPersistIndex(capturePath, rebuilt);
        return rebuilt;
    }

    public static void TryPersistIndex(string capturePath, SdkRawCaptureIndex index)
    {
        if (string.IsNullOrWhiteSpace(capturePath) || index == null)
        {
            return;
        }

        try
        {
            string indexPath = GetIndexPath(capturePath);
            var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(indexPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkRawCapture][Index] Failed to persist index: {ex.Message}");
        }
    }

    public static SdkRawCaptureIndex BuildFromCapture(string capturePath)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        if (!File.Exists(capturePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", capturePath);
        }

        SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest);

        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        var fileHeader = ReadFileHeader(reader);

        var blocks = new List<SdkRawCaptureBlockIndexEntry>();
        var deviceStates = new Dictionary<int, DeviceIndexState>();

        while (TryReadBlockHeader(reader, out var header, out long blockOffset, out long payloadOffset))
        {
            int deviceId = SdkRawCaptureWriter.ResolveChannelDeviceId(header.GroupId, header.MachineId);
            if (!deviceStates.TryGetValue(deviceId, out var deviceState))
            {
                deviceState = new DeviceIndexState(deviceId);
                deviceStates[deviceId] = deviceState;
            }

            long sampleStart = deviceState.NextSampleStartPerChannel;
            deviceState.ChannelCount = Math.Max(deviceState.ChannelCount, header.ChannelCount);
            deviceState.BlockCount++;
            deviceState.NextSampleStartPerChannel += header.DataCountPerChannel;

            blocks.Add(new SdkRawCaptureBlockIndexEntry
            {
                Offset = blockOffset,
                PayloadOffset = payloadOffset,
                DeviceId = deviceId,
                GroupId = header.GroupId,
                MachineId = header.MachineId,
                BlockIndex = header.BlockIndex,
                ChannelCount = header.ChannelCount,
                DataCountPerChannel = header.DataCountPerChannel,
                SampleStartPerChannel = sampleStart,
                PayloadBytes = header.PayloadBytes,
                PayloadFloatCount = header.PayloadFloatCount,
                SampleRateHz = header.SampleRateHz,
                SampleTime = header.SampleTime,
                ReceivedAtUtcTicks = header.ReceivedAtUtcTicks
            });

            reader.BaseStream.Seek(header.PayloadBytes, SeekOrigin.Current);
        }

        var devices = deviceStates.Values
            .OrderBy(state => state.DeviceId)
            .Select(state => new SdkRawCaptureDeviceIndexSummary
            {
                DeviceId = state.DeviceId,
                ChannelCount = state.ChannelCount,
                SamplesPerChannel = state.NextSampleStartPerChannel,
                BlockCount = state.BlockCount
            })
            .ToList();

        var channelSampleCounts = manifest?.ChannelSampleCounts != null && manifest.ChannelSampleCounts.Count > 0
            ? new Dictionary<string, long>(manifest.ChannelSampleCounts, StringComparer.OrdinalIgnoreCase)
            : BuildChannelSampleCountsFromDevices(devices);

        return new SdkRawCaptureIndex
        {
            Version = IndexVersion,
            SessionName = !string.IsNullOrWhiteSpace(manifest?.SessionName)
                ? manifest.SessionName
                : SdkRawCaptureFormat.GetCaptureStem(capturePath),
            CaptureFileName = Path.GetFileName(capturePath),
            CaptureFileBytes = stream.Length,
            SampleRateHz = fileHeader.SampleRateHz > 0d
                ? fileHeader.SampleRateHz
                : manifest?.SampleRateHz ?? 0d,
            CreatedAtUtc = DateTime.UtcNow,
            Devices = devices,
            Blocks = blocks,
            ChannelSampleCounts = channelSampleCounts
        };
    }

    private static Dictionary<string, long> BuildChannelSampleCountsFromDevices(
        IReadOnlyList<SdkRawCaptureDeviceIndexSummary> devices)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            for (int channelNumber = 1; channelNumber <= Math.Max(0, device.ChannelCount); channelNumber++)
            {
                int channelId = DH.Contracts.ChannelNaming.MakeChannelId(device.DeviceId, channelNumber);
                result[DH.Contracts.ChannelNaming.ChannelName(channelId)] = device.SamplesPerChannel;
            }
        }

        return result;
    }

    private static bool IsUsable(SdkRawCaptureIndex? index, string capturePath)
    {
        if (index == null || index.Version != IndexVersion || index.Blocks.Count == 0)
        {
            return false;
        }

        if (!File.Exists(capturePath))
        {
            return false;
        }

        long actualBytes = new FileInfo(capturePath).Length;
        return index.CaptureFileBytes <= 0 || index.CaptureFileBytes == actualBytes;
    }

    private static RawFileHeader ReadFileHeader(BinaryReader reader)
    {
        ulong magic = reader.ReadUInt64();
        if (magic != SdkRawCaptureFormat.FileMagic)
        {
            throw new InvalidDataException("The selected file is not a valid SDK raw capture.");
        }

        int version = reader.ReadInt32();
        if (version != SdkRawCaptureFormat.FormatVersion)
        {
            throw new InvalidDataException($"Unsupported raw capture version: {version}.");
        }

        _ = reader.ReadInt64();
        double sampleRateHz = reader.ReadDouble();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        return new RawFileHeader(sampleRateHz);
    }

    private static bool TryReadBlockHeader(
        BinaryReader reader,
        out IndexedRawBlockHeader header,
        out long blockOffset,
        out long payloadOffset)
    {
        header = default;
        blockOffset = 0L;
        payloadOffset = 0L;

        if (reader.BaseStream.Position >= reader.BaseStream.Length)
        {
            return false;
        }

        blockOffset = reader.BaseStream.Position;
        uint magic = reader.ReadUInt32();
        if (magic != SdkRawCaptureFormat.BlockMagic)
        {
            throw new InvalidDataException("Invalid raw capture block marker.");
        }

        int version = reader.ReadInt32();
        if (version != SdkRawCaptureFormat.FormatVersion)
        {
            throw new InvalidDataException($"Unsupported raw capture block version: {version}.");
        }

        header = new IndexedRawBlockHeader(
            reader.ReadInt64(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt64(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadSingle(),
            reader.ReadInt64(),
            reader.ReadInt32(),
            reader.ReadInt32());

        payloadOffset = reader.BaseStream.Position;
        return true;
    }

    private readonly struct RawFileHeader
    {
        public double SampleRateHz { get; }

        public RawFileHeader(double sampleRateHz)
        {
            SampleRateHz = sampleRateHz;
        }
    }

    private readonly struct IndexedRawBlockHeader
    {
        public long SampleTime { get; }
        public int MessageType { get; }
        public int GroupId { get; }
        public int MachineId { get; }
        public long TotalDataCount { get; }
        public int DataCountPerChannel { get; }
        public int BufferCountBytes { get; }
        public int BlockIndex { get; }
        public int ChannelCount { get; }
        public float SampleRateHz { get; }
        public long ReceivedAtUtcTicks { get; }
        public int PayloadFloatCount { get; }
        public int PayloadBytes { get; }

        public IndexedRawBlockHeader(
            long sampleTime,
            int messageType,
            int groupId,
            int machineId,
            long totalDataCount,
            int dataCountPerChannel,
            int bufferCountBytes,
            int blockIndex,
            int channelCount,
            float sampleRateHz,
            long receivedAtUtcTicks,
            int payloadFloatCount,
            int payloadBytes)
        {
            SampleTime = sampleTime;
            MessageType = messageType;
            GroupId = groupId;
            MachineId = machineId;
            TotalDataCount = totalDataCount;
            DataCountPerChannel = dataCountPerChannel;
            BufferCountBytes = bufferCountBytes;
            BlockIndex = blockIndex;
            ChannelCount = channelCount;
            SampleRateHz = sampleRateHz;
            ReceivedAtUtcTicks = receivedAtUtcTicks;
            PayloadFloatCount = payloadFloatCount;
            PayloadBytes = payloadBytes;
        }
    }

    private sealed class DeviceIndexState
    {
        public int DeviceId { get; }
        public int ChannelCount { get; set; }
        public int BlockCount { get; set; }
        public long NextSampleStartPerChannel { get; set; }

        public DeviceIndexState(int deviceId)
        {
            DeviceId = deviceId;
        }
    }
}

internal sealed class SdkRawCaptureIndex
{
    public int Version { get; set; } = SdkRawCaptureIndexFormat.IndexVersion;

    public string SessionName { get; set; } = "";

    public string CaptureFileName { get; set; } = "";

    public long CaptureFileBytes { get; set; }

    public double SampleRateHz { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<SdkRawCaptureDeviceIndexSummary> Devices { get; set; } = new();

    public List<SdkRawCaptureBlockIndexEntry> Blocks { get; set; } = new();

    public Dictionary<string, long> ChannelSampleCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class SdkRawCaptureDeviceIndexSummary
{
    public int DeviceId { get; set; }

    public int ChannelCount { get; set; }

    public long SamplesPerChannel { get; set; }

    public int BlockCount { get; set; }
}

internal sealed class SdkRawCaptureBlockIndexEntry
{
    public long Offset { get; set; }

    public long PayloadOffset { get; set; }

    public int DeviceId { get; set; }

    public int GroupId { get; set; }

    public int MachineId { get; set; }

    public int BlockIndex { get; set; }

    public int ChannelCount { get; set; }

    public int DataCountPerChannel { get; set; }

    public long SampleStartPerChannel { get; set; }

    public int PayloadBytes { get; set; }

    public int PayloadFloatCount { get; set; }

    public float SampleRateHz { get; set; }

    public long SampleTime { get; set; }

    public long ReceivedAtUtcTicks { get; set; }
}
