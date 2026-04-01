using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DH.Driver.SDK;

namespace DH.Client.App.Services.Storage;

internal sealed class SdkRawCaptureHdf5ExportProgress
{
    public long BlocksProcessed { get; init; }

    public long TotalBlocks { get; init; }

    public long SamplesProcessed { get; init; }
}

internal sealed class SdkRawCaptureHdf5ExportResult
{
    public string CapturePath { get; init; } = "";

    public string OutputRootPath { get; init; } = "";

    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, long> SampleCounts { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public long BlocksProcessed { get; init; }

    public long SamplesProcessed { get; init; }

    public string Summary { get; init; } = "";
}

internal sealed class SdkRawCaptureHdf5Exporter
{
    public SdkRawCaptureHdf5ExportResult Export(
        string capturePath,
        string outputBasePath,
        string? sessionName = null,
        Action<SdkRawCaptureHdf5ExportProgress>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            throw new ArgumentException("Capture path is required.", nameof(capturePath));
        }

        if (!File.Exists(capturePath))
        {
            throw new FileNotFoundException("Raw capture file not found.", capturePath);
        }

        if (string.IsNullOrWhiteSpace(outputBasePath))
        {
            throw new ArgumentException("Output base path is required.", nameof(outputBasePath));
        }

        Directory.CreateDirectory(outputBasePath);

        if (!SdkRawCaptureFormat.TryLoadManifest(capturePath, out var manifest) || manifest == null)
        {
            throw new InvalidOperationException($"Raw capture manifest is missing: {SdkRawCaptureFormat.GetManifestPath(capturePath)}");
        }

        var channelIds = SdkRawCaptureConverter.ResolveChannelIds(capturePath, manifest);
        if (channelIds.Count == 0)
        {
            throw new InvalidOperationException("No channels were found in the raw capture.");
        }

        string effectiveSessionName = string.IsNullOrWhiteSpace(sessionName)
            ? manifest.SessionName
            : sessionName;

        using var writer = new SdkRawCaptureHdf5MirrorWriter();
        writer.Start(
            outputBasePath,
            effectiveSessionName,
            manifest.SampleRateHz,
            channelIds,
            useBackgroundWriter: false);

        long totalBlocks = manifest.BlockCount;
        long blocksProcessed = 0;
        long samplesProcessed = 0;

        using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        SdkRawCaptureConverter.SkipFileHeader(reader);

        while (SdkRawCaptureConverter.TryReadRawBlock(reader, out var rawBlock))
        {
            try
            {
                writer.AppendRawBlock(rawBlock);
                blocksProcessed++;
                samplesProcessed += (long)rawBlock.ChannelCount * rawBlock.DataCountPerChannel;

                if (progressCallback != null
                    && (blocksProcessed == 1 || (blocksProcessed % 32) == 0 || (totalBlocks > 0 && blocksProcessed == totalBlocks)))
                {
                    progressCallback(new SdkRawCaptureHdf5ExportProgress
                    {
                        BlocksProcessed = blocksProcessed,
                        TotalBlocks = totalBlocks,
                        SamplesProcessed = samplesProcessed
                    });
                }
            }
            finally
            {
                rawBlock.ReleasePayload();
            }
        }

        var result = writer.Complete();
        return new SdkRawCaptureHdf5ExportResult
        {
            CapturePath = capturePath,
            OutputRootPath = result.OutputRootPath,
            WrittenFiles = result.WrittenFiles,
            SampleCounts = result.SampleCounts,
            BlocksProcessed = blocksProcessed,
            SamplesProcessed = samplesProcessed,
            Summary = BuildSummary(result, blocksProcessed, totalBlocks, samplesProcessed)
        };
    }

    private static string BuildSummary(
        SdkRawCaptureHdf5MirrorResult result,
        long blocksProcessed,
        long totalBlocks,
        long samplesProcessed)
    {
        string blockText = totalBlocks > 0
            ? $"{blocksProcessed:N0}/{totalBlocks:N0} 块"
            : $"{blocksProcessed:N0} 块";
        string sampleText = samplesProcessed > 0
            ? $"{samplesProcessed:N0} 个样本"
            : "无样本";
        return $"HDF5 已导出到 {result.OutputRootPath}，共 {result.FileCount:N0} 个通道文件，处理 {blockText}，{sampleText}。";
    }
}
