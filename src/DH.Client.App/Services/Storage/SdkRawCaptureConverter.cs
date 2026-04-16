using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DH.Contracts;
using DH.Driver.SDK;

namespace DH.Client.App.Services.Storage;

internal sealed class SdkRawCaptureConversionProgress
{
    public long BlocksProcessed { get; init; }

    public long TotalBlocks { get; init; }

    public long SamplesProcessed { get; init; }
}

internal sealed class SdkRawCaptureConversionResult
{
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, long> SampleCounts { get; init; } = new Dictionary<string, long>();

    public CompressionSessionSnapshot? Snapshot { get; init; }

    public string OutputBasePath { get; init; } = "";

    public string Summary { get; init; } = "";
}

internal sealed class SdkRawCaptureExportAlignmentPlan
{
    public IReadOnlyDictionary<int, long> DeviceSampleTargets { get; init; } = new Dictionary<int, long>();

    public long TargetSamplesPerChannel { get; init; }

    public int DeviceCount { get; init; }

    public bool Applied => DeviceCount > 1 && TargetSamplesPerChannel > 0;
}

internal sealed class SdkRawCaptureConverter
{
    public SdkRawCaptureConversionResult Convert(
        string capturePath,
        bool perChannel,
        CompressionType compressionType,
        PreprocessType preprocessType,
        CompressionOptions compressionOptions,
        IReadOnlyCollection<int>? selectedChannelIds = null,
        Action<SdkRawCaptureConversionProgress>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        if (!File.Exists(capturePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", capturePath);
        }

        if (!TdmsNative.IsAvailable)
        {
            throw new InvalidOperationException("TDMS library is not available.");
        }

        SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest);
        var channelIds = ResolveChannelIds(capturePath, manifest);
        if (channelIds.Count == 0)
        {
            throw new InvalidOperationException("No channels were found in the raw capture.");
        }

        var targetChannelIds = ResolveRequestedChannelIds(channelIds, selectedChannelIds);
        if (targetChannelIds.Count == 0)
        {
            throw new InvalidOperationException("The current TDMS export selection does not match any channels.");
        }

        string basePath = Path.GetDirectoryName(capturePath) ?? ".";
        string sessionName = BuildOutputSessionName(capturePath, manifest, targetChannelIds.Count, channelIds.Count);
        double sampleRateHz = manifest?.SampleRateHz ?? ReadFileHeader(capturePath).SampleRateHz;
        long totalBlocks = manifest?.BlockCount ?? 0;
        var alignmentPlan = BuildExportAlignmentPlan(manifest, targetChannelIds);

        ITdmsStorage storage = perChannel
            ? new TdmsPerChannelStorage()
            : new TdmsSingleFileStorage();

        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            storage.Start(
                basePath,
                targetChannelIds,
                sessionName,
                sampleRateHz,
                compressionType,
                preprocessType,
                compressionOptions.Clone());

            var selectedChannelSet = new HashSet<int>(targetChannelIds);
            var writtenSamplesPerDevice = new Dictionary<int, long>();
            long blocksProcessed = 0;
            long samplesProcessed = 0;

            using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            var fileHeader = ReadFileHeader(reader);

            while (TryReadBlock(reader, fileHeader, out var rawBlock))
            {
                samplesProcessed += WriteRawBlockToStorage(storage, rawBlock, selectedChannelSet, alignmentPlan, writtenSamplesPerDevice);
                blocksProcessed++;

                if (progressCallback != null
                    && (blocksProcessed == 1 || (blocksProcessed % 32) == 0 || (totalBlocks > 0 && blocksProcessed == totalBlocks)))
                {
                    progressCallback(new SdkRawCaptureConversionProgress
                    {
                        BlocksProcessed = blocksProcessed,
                        TotalBlocks = totalBlocks,
                        SamplesProcessed = samplesProcessed
                    });
                }
            }

            storage.Flush();
            var snapshot = storage.GetCompressionSessionSnapshot();
            var hashes = storage.GetWriteHashes();
            var counts = storage.GetWriteSampleCounts();
            var writtenFiles = storage.GetWrittenFiles();

            storage.Stop();

            foreach (string file in writtenFiles)
            {
                try
                {
                    StorageVerifier.SaveManifest(file, hashes, counts);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SdkRawCaptureConverter] Failed to save manifest: {ex.Message}");
                }
            }

            stopwatch.Stop();

            if (snapshot != null)
            {
                snapshot.StartedAt = startedAt;
                snapshot.StoppedAt = DateTime.Now;
                snapshot.Elapsed = stopwatch.Elapsed;
                snapshot.WrittenFiles = writtenFiles.ToArray();
                snapshot.StoredBytes = writtenFiles
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path).Length)
                    .Sum();
                snapshot.BenchmarkSource = CompressionBenchmarkSource.RawCaptureReplay;
                snapshot.BenchmarkSourcePath = capturePath;
                snapshot.BenchmarkBatchSize = CompressionBenchmarkDefaults.BatchSize;
            }

            return new SdkRawCaptureConversionResult
            {
                WrittenFiles = writtenFiles,
                Hashes = hashes,
                SampleCounts = counts,
                Snapshot = snapshot,
                OutputBasePath = basePath,
                Summary = BuildSummary(capturePath, perChannel, writtenFiles, counts, targetChannelIds.Count, channelIds.Count, alignmentPlan)
            };
        }
        finally
        {
            storage.Dispose();
        }
    }

    internal static List<int> ResolveChannelIds(string capturePath, SdkRawCaptureManifest? manifest)
    {
        if (manifest?.ChannelSampleCounts?.Count > 0)
        {
            var idsFromManifest = manifest.ChannelSampleCounts.Keys
                .Select(ChannelNaming.ParseChannelName)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (idsFromManifest.Count > 0)
            {
                return idsFromManifest;
            }
        }

        var ids = new SortedSet<int>();
        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        _ = ReadFileHeader(reader);

        while (TryReadBlockHeader(reader, out var header))
        {
            int deviceId = SdkRawCaptureWriter.ResolveChannelDeviceId(header.GroupId, header.MachineId);
            for (int channelIndex = 0; channelIndex < header.ChannelCount; channelIndex++)
            {
                ids.Add(ChannelNaming.MakeChannelId(deviceId, channelIndex + 1));
            }

            reader.BaseStream.Seek(header.PayloadBytes, SeekOrigin.Current);
        }

        return ids.ToList();
    }

    private static string BuildSummary(
        string capturePath,
        bool perChannel,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyDictionary<string, long> sampleCounts,
        int selectedChannelCount,
        int totalChannelCount,
        SdkRawCaptureExportAlignmentPlan alignmentPlan)
    {
        string modeText = perChannel ? "per-channel TDMS" : "single-file TDMS";
        string firstOutput = writtenFiles.Count > 0 ? Path.GetFileName(writtenFiles[0]) : "(none)";
        long totalSamples = sampleCounts.Values.Sum();
        string selectionText = selectedChannelCount >= totalChannelCount
            ? $"all({totalChannelCount})"
            : $"{selectedChannelCount}/{totalChannelCount}";
        string alignmentText = alignmentPlan.Applied
            ? $", aligned={alignmentPlan.TargetSamplesPerChannel:N0} samples/ch across {alignmentPlan.DeviceCount} devices"
            : string.Empty;
        return $"Raw export completed: {Path.GetFileName(capturePath)} -> {modeText}, files={writtenFiles.Count}, samples={totalSamples:N0}, channels={selectionText}{alignmentText}, first={firstOutput}";
    }

    private static string BuildOutputSessionName(string capturePath, SdkRawCaptureManifest? manifest, int selectedChannelCount, int totalChannelCount)
    {
        string baseName = manifest?.SessionName;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = SdkRawCaptureFormat.GetCaptureStem(capturePath);
        }

        string scope = selectedChannelCount >= totalChannelCount
            ? "all"
            : $"{selectedChannelCount}ch";
        return $"{baseName}_converted_{scope}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static List<int> ResolveRequestedChannelIds(IReadOnlyList<int> availableChannelIds, IReadOnlyCollection<int>? selectedChannelIds)
    {
        if (selectedChannelIds == null || selectedChannelIds.Count == 0)
        {
            return availableChannelIds.OrderBy(id => id).ToList();
        }

        var availableSet = new HashSet<int>(availableChannelIds);
        return selectedChannelIds
            .Where(availableSet.Contains)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private static long WriteRawBlockToStorage(
        ITdmsStorage storage,
        SdkRawBlock rawBlock,
        IReadOnlySet<int> selectedChannelIds,
        SdkRawCaptureExportAlignmentPlan alignmentPlan,
        IDictionary<int, long> writtenSamplesPerDevice)
    {
        int deviceId = SdkRawCaptureWriter.ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);
        int channelCount = rawBlock.ChannelCount;
        int samplesPerChannel = rawBlock.DataCountPerChannel;
        int samplesToWrite = GetSamplesToWrite(deviceId, samplesPerChannel, alignmentPlan, writtenSamplesPerDevice);
        if (samplesToWrite <= 0)
        {
            return 0;
        }

        int selectedChannelCount = 0;

        for (int channelOffset = 0; channelOffset < channelCount; channelOffset++)
        {
            int channelId = ChannelNaming.MakeChannelId(deviceId, channelOffset + 1);
            if (!selectedChannelIds.Contains(channelId))
            {
                continue;
            }

            selectedChannelCount++;

            var values = new double[samplesToWrite];
            for (int sampleIndex = 0; sampleIndex < samplesToWrite; sampleIndex++)
            {
                values[sampleIndex] = rawBlock.InterleavedSamples[sampleIndex * channelCount + channelOffset];
            }

            storage.Write(channelId, values);
        }

        if (selectedChannelCount == 0)
        {
            return 0;
        }

        if (alignmentPlan.Applied)
        {
            writtenSamplesPerDevice.TryGetValue(deviceId, out long writtenSamples);
            writtenSamplesPerDevice[deviceId] = writtenSamples + samplesToWrite;
        }

        return (long)selectedChannelCount * samplesToWrite;
    }

    private static int GetSamplesToWrite(
        int deviceId,
        int samplesPerChannel,
        SdkRawCaptureExportAlignmentPlan alignmentPlan,
        IDictionary<int, long> writtenSamplesPerDevice)
    {
        if (!alignmentPlan.Applied
            || !alignmentPlan.DeviceSampleTargets.TryGetValue(deviceId, out long targetSamplesPerChannel))
        {
            return samplesPerChannel;
        }

        writtenSamplesPerDevice.TryGetValue(deviceId, out long writtenSamples);
        long remainingSamples = targetSamplesPerChannel - writtenSamples;
        if (remainingSamples <= 0)
        {
            return 0;
        }

        return (int)Math.Min(samplesPerChannel, remainingSamples);
    }

    private static SdkRawCaptureExportAlignmentPlan BuildExportAlignmentPlan(
        SdkRawCaptureManifest? manifest,
        IReadOnlyCollection<int> selectedChannelIds)
    {
        if (manifest?.DeviceIntegrity == null || manifest.DeviceIntegrity.Count == 0)
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        var selectedDeviceIds = selectedChannelIds
            .Select(ChannelNaming.GetDeviceId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (selectedDeviceIds.Count <= 1)
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        var deviceMap = manifest.DeviceIntegrity
            .GroupBy(d => d.DeviceId)
            .ToDictionary(g => g.Key, g => g.First());
        if (selectedDeviceIds.Any(deviceId => !deviceMap.ContainsKey(deviceId)))
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        var selectedDevices = selectedDeviceIds
            .Select(deviceId => deviceMap[deviceId])
            .ToList();
        if (selectedDevices.Any(HasMeaningfulContinuityIssueForAlignment))
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        var blockSizes = selectedDevices
            .Select(d => d.SamplesPerBlockPerChannel)
            .Where(size => size > 0)
            .Distinct()
            .ToList();
        if (blockSizes.Count != 1)
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        long minSamplesPerChannel = selectedDevices.Min(d => d.SamplesPerChannel);
        long maxSamplesPerChannel = selectedDevices.Max(d => d.SamplesPerChannel);
        long spread = maxSamplesPerChannel - minSamplesPerChannel;
        int commonBlockSize = blockSizes[0];
        if (spread <= 0
            || commonBlockSize <= 0
            || spread > commonBlockSize
            || (spread % commonBlockSize) != 0)
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        if (selectedDevices.Any(device =>
            {
                long tailSpread = device.SamplesPerChannel - minSamplesPerChannel;
                return tailSpread < 0
                    || tailSpread > commonBlockSize
                    || (tailSpread % commonBlockSize) != 0;
            }))
        {
            return new SdkRawCaptureExportAlignmentPlan();
        }

        return new SdkRawCaptureExportAlignmentPlan
        {
            DeviceSampleTargets = selectedDeviceIds.ToDictionary(deviceId => deviceId, _ => minSamplesPerChannel),
            TargetSamplesPerChannel = minSamplesPerChannel,
            DeviceCount = selectedDeviceIds.Count
        };
    }

    private static bool HasMeaningfulContinuityIssueForAlignment(SdkRawCaptureDeviceIntegrity device)
    {
        bool constantBlockIndexArtifact =
            !device.BlockIndexContinuityEnabled
            || (device.BlockCount > 1
                && device.FirstBlockIndex == device.LastBlockIndex
                && device.NonMonotonicBlockCount >= device.BlockCount - 1
                && device.MissingBlockCount == 0
                && device.TotalDataGapSampleCount == 0
                && device.TotalDataRegressionCount == 0);

        bool hasBlockIndexIssue = !constantBlockIndexArtifact
            && (device.MissingBlockCount > 0 || device.NonMonotonicBlockCount > 0);

        return hasBlockIndexIssue
            || device.TotalDataGapSampleCount > 0
            || device.TotalDataRegressionCount > 0
            || device.ChannelLayoutChanged
            || device.BlockSizeChanged
            || device.SamplesPerChannel <= 0;
    }

    private static SdkRawCaptureFileHeaderInfo ReadFileHeader(string capturePath)
    {
        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        return ReadFileHeader(reader);
    }

    internal static SdkRawCaptureFileHeaderInfo SkipFileHeader(BinaryReader reader)
        => ReadFileHeader(reader);

    private static SdkRawCaptureFileHeaderInfo ReadFileHeader(BinaryReader reader)
        => SdkRawCaptureFormatCodec.ReadFileHeader(reader);

    private static bool TryReadBlock(BinaryReader reader, SdkRawCaptureFileHeaderInfo fileHeader, out SdkRawBlock rawBlock)
    {
        rawBlock = new SdkRawBlock();

        if (!TryReadBlockHeader(reader, out var header))
        {
            return false;
        }

        if (header.Version != fileHeader.Version)
        {
            throw new InvalidDataException("Raw capture block version does not match file header version.");
        }

        if (header.PayloadBytes < 0 || header.PayloadFloatCount < 0)
        {
            throw new InvalidDataException("Invalid payload length in raw capture block.");
        }

        int expectedFloatCount = checked(header.ChannelCount * header.DataCountPerChannel);
        if (header.PayloadFloatCount != expectedFloatCount)
        {
            throw new InvalidDataException("Raw capture payload sample count is inconsistent.");
        }

        byte[] payloadBytes = reader.ReadBytes(header.PayloadBytes);
        if (payloadBytes.Length != header.PayloadBytes)
        {
            throw new EndOfStreamException("Unexpected end of file while reading raw capture payload.");
        }

        float[] interleavedSamples;
        if (fileHeader.UsesEncodedPayload)
        {
            interleavedSamples = SdkRawCapturePayloadCodec.Decode(
                payloadBytes,
                header.ChannelCount,
                header.DataCountPerChannel,
                fileHeader.CompressionType,
                fileHeader.PreprocessType);
        }
        else
        {
            if (header.PayloadFloatCount * sizeof(float) != header.PayloadBytes)
            {
                throw new InvalidDataException("Raw capture payload metadata is inconsistent.");
            }

            interleavedSamples = new float[header.PayloadFloatCount];
            Buffer.BlockCopy(payloadBytes, 0, interleavedSamples, 0, header.PayloadBytes);
        }

        rawBlock = new SdkRawBlock
        {
            SampleTime = header.SampleTime,
            MessageType = header.MessageType,
            GroupId = header.GroupId,
            MachineId = header.MachineId,
            TotalDataCount = header.TotalDataCount,
            DataCountPerChannel = header.DataCountPerChannel,
            BufferCountBytes = header.BufferCountBytes,
            BlockIndex = header.BlockIndex,
            ChannelCount = header.ChannelCount,
            SampleRateHz = header.SampleRateHz,
            ReceivedAtUtc = new DateTime(header.ReceivedAtUtcTicks, DateTimeKind.Utc),
            InterleavedSamples = interleavedSamples,
            PayloadFloatCount = header.PayloadFloatCount
        };

        return true;
    }

    internal static bool TryReadRawBlock(BinaryReader reader, SdkRawCaptureFileHeaderInfo fileHeader, out SdkRawBlock rawBlock)
        => TryReadBlock(reader, fileHeader, out rawBlock);

    private static bool TryReadBlockHeader(BinaryReader reader, out RawBlockHeader header)
    {
        header = default;
        if (reader.BaseStream.Position >= reader.BaseStream.Length)
        {
            return false;
        }

        uint magic = reader.ReadUInt32();
        if (magic != SdkRawCaptureFormat.BlockMagic)
        {
            throw new InvalidDataException("Invalid raw capture block marker.");
        }

        int version = reader.ReadInt32();
        if (!SdkRawCaptureFormat.IsSupportedVersion(version))
        {
            throw new InvalidDataException($"Unsupported raw capture block version: {version}.");
        }

        header = new RawBlockHeader(
            version,
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

        return true;
    }

    private readonly struct RawBlockHeader
    {
        public int Version { get; }

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

        public RawBlockHeader(
            int version,
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
            Version = version;
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
}
