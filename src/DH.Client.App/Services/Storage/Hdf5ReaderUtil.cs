using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DH.Contracts;
using HDF5DotNet;

namespace DH.Client.App.Services.Storage;

public sealed class Hdf5ChannelDescriptor
{
    public string DeviceDisplayName { get; init; } = "";

    public string DeviceFolderName { get; init; } = "";

    public string ChannelName { get; init; } = "";

    public string FilePath { get; init; } = "";

    public int ChannelId { get; init; }

    public long SampleCount { get; init; }

    public double SampleRateHz { get; init; }
}

public static class Hdf5ReaderUtil
{
    private const string DatasetName = "samples";
    private const string SampleRateAttributeName = "sample_rate_hz";

    public static IReadOnlyDictionary<string, IReadOnlyList<Hdf5ChannelDescriptor>> ListDevicesAndChannels(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        string sessionRoot = ResolveSessionRoot(filePath);
        var deviceDirectories = Directory.Exists(sessionRoot)
            ? Directory.EnumerateDirectories(sessionRoot)
                .Where(dir => LooksLikeDeviceDirectory(Path.GetFileName(dir)))
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        var files = new List<string>();
        if (deviceDirectories.Length > 0)
        {
            foreach (string dir in deviceDirectories)
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.h5", SearchOption.TopDirectoryOnly));
                files.AddRange(Directory.EnumerateFiles(dir, "*.hdf5", SearchOption.TopDirectoryOnly));
            }
        }
        else if (File.Exists(filePath))
        {
            files.Add(filePath);
        }

        var groups = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(BuildDescriptor)
            .GroupBy(descriptor => descriptor.DeviceDisplayName)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Hdf5ChannelDescriptor>)group
                    .OrderBy(item => item.ChannelId)
                    .ThenBy(item => item.ChannelName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return groups;
    }

    public static double[] ReadChannelData(string filePath)
    {
        var fileId = H5F.open(filePath, H5F.OpenMode.ACC_RDONLY);
        try
        {
            var datasetId = H5D.open(fileId, DatasetName);
            try
            {
                var fileSpaceId = H5D.getSpace(datasetId);
                try
                {
                    long[] dims = H5S.getSimpleExtentDims(fileSpaceId);
                    int length = dims.Length > 0 ? checked((int)dims[0]) : 0;
                    if (length <= 0)
                    {
                        return Array.Empty<double>();
                    }

                    var buffer = new float[length];
                    H5D.read<float>(
                        datasetId,
                        new H5DataTypeId(H5T.H5Type.NATIVE_FLOAT),
                        new H5Array<float>(buffer));

                    return buffer.Select(value => (double)value).ToArray();
                }
                finally
                {
                    H5S.close(fileSpaceId);
                }
            }
            finally
            {
                H5D.close(datasetId);
            }
        }
        finally
        {
            H5F.close(fileId);
        }
    }

    public static IReadOnlyDictionary<string, object> ReadChannelProperties(string filePath)
    {
        var props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        double sampleRateHz = ReadSampleRateHz(filePath);
        if (sampleRateHz > 0d)
        {
            props[SampleRateAttributeName] = sampleRateHz;
            props["wf_increment"] = 1d / sampleRateHz;
            props["wf_start_offset"] = 0d;
        }

        return props;
    }

    public static double ReadSampleRateHz(string filePath)
    {
        var fileId = H5F.open(filePath, H5F.OpenMode.ACC_RDONLY);
        try
        {
            try
            {
                var attrId = H5A.open(fileId, SampleRateAttributeName);
                try
                {
                    var values = new double[1];
                    H5A.read<double>(
                        attrId,
                        new H5DataTypeId(H5T.H5Type.NATIVE_DOUBLE),
                        new H5Array<double>(values));
                    return values[0];
                }
                finally
                {
                    H5A.close(attrId);
                }
            }
            catch
            {
                return 0d;
            }
        }
        finally
        {
            H5F.close(fileId);
        }
    }

    public static string ResolveSessionRoot(string filePath)
    {
        if (Directory.Exists(filePath))
        {
            var dirInfo = new DirectoryInfo(filePath);
            return LooksLikeDeviceDirectory(dirInfo.Name) && dirInfo.Parent != null
                ? dirInfo.Parent.FullName
                : dirInfo.FullName;
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("HDF5 file not found.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Directory == null)
        {
            throw new InvalidOperationException("Cannot resolve HDF5 session root.");
        }

        return LooksLikeDeviceDirectory(fileInfo.Directory.Name) && fileInfo.Directory.Parent != null
            ? fileInfo.Directory.Parent.FullName
            : fileInfo.Directory.FullName;
    }

    private static Hdf5ChannelDescriptor BuildDescriptor(string filePath)
    {
        string channelName = Path.GetFileNameWithoutExtension(filePath);
        int channelId = ChannelNaming.ParseChannelName(channelName);
        int deviceId = channelId >= 0
            ? ChannelNaming.GetDeviceId(channelId)
            : ParseDeviceIdFromFolder(Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty));
        string deviceFolder = deviceId >= 0
            ? ChannelNaming.DeviceDisplayName(deviceId)
            : (Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty) ?? "Unknown");
        string deviceDisplay = deviceId >= 0
            ? $"设备 {deviceId} ({ChannelNaming.DeviceDisplayName(deviceId)})"
            : $"设备 ({deviceFolder})";

        long sampleCount = 0;
        double sampleRateHz = 0d;
        try
        {
            sampleRateHz = ReadSampleRateHz(filePath);

            var fileId = H5F.open(filePath, H5F.OpenMode.ACC_RDONLY);
            try
            {
                var datasetId = H5D.open(fileId, DatasetName);
                try
                {
                    var fileSpaceId = H5D.getSpace(datasetId);
                    try
                    {
                        long[] dims = H5S.getSimpleExtentDims(fileSpaceId);
                        sampleCount = dims.Length > 0 ? dims[0] : 0;
                    }
                    finally
                    {
                        H5S.close(fileSpaceId);
                    }
                }
                finally
                {
                    H5D.close(datasetId);
                }
            }
            finally
            {
                H5F.close(fileId);
            }
        }
        catch
        {
        }

        return new Hdf5ChannelDescriptor
        {
            DeviceDisplayName = deviceDisplay,
            DeviceFolderName = deviceFolder,
            ChannelName = channelName,
            FilePath = filePath,
            ChannelId = channelId >= 0 ? channelId : Math.Abs(channelName.GetHashCode()),
            SampleCount = sampleCount,
            SampleRateHz = sampleRateHz
        };
    }

    private static bool LooksLikeDeviceDirectory(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && name.StartsWith("AI", StringComparison.OrdinalIgnoreCase)
            && name.Skip(2).All(char.IsDigit);

    private static int ParseDeviceIdFromFolder(string? folderName)
    {
        if (!LooksLikeDeviceDirectory(folderName))
        {
            return -1;
        }

        return int.TryParse(folderName![2..], out int deviceId)
            ? deviceId
            : -1;
    }
}
