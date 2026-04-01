using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Contracts;
using DH.Driver.SDK;
using HDF5DotNet;

namespace DH.Client.App.Services.Storage;

internal sealed class SdkRawCaptureHdf5MirrorResult
{
    public string OutputRootPath { get; init; } = "";

    public int FileCount { get; init; }

    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, long> SampleCounts { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public bool Faulted { get; init; }

    public string FailureReason { get; init; } = "";
}

internal sealed class SdkRawCaptureHdf5MirrorWriter : IDisposable
{
    private const string DatasetName = "samples";
    private const int FlushBlockStride = 32;
    private const long MaxPendingBlockLimit = 64;
    private const long MaxPendingPayloadByteLimit = 256L * 1024 * 1024;

    private static readonly H5PropertyListId DefaultPropertyListId = new(H5P.Template.DEFAULT);
    private static readonly H5DataTypeId NativeFloatTypeId = new(H5T.H5Type.NATIVE_FLOAT);

    private readonly Channel<SdkRawBlock> _queue = Channel.CreateUnbounded<SdkRawBlock>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly Dictionary<int, ChannelHdf5State> _channelStates = new();

    private Task? _writerTask;
    private string _outputRootPath = "";
    private int _acceptingWrites;
    private int _faulted;
    private string _failureReason = "";
    private long _pendingBlockCount;
    private long _pendingPayloadBytes;
    private long _processedBlockCount;
    private double _sampleRateHz;
    private string _sessionName = "session";
    private bool _started;

    public string OutputRootPath => _outputRootPath;

    public bool Faulted => Volatile.Read(ref _faulted) != 0;

    public string FailureReason => _failureReason;

    public void Start(
        string basePath,
        string sessionName,
        double sampleRateHz,
        IReadOnlyCollection<int>? expectedChannelIds = null,
        bool useBackgroundWriter = true)
    {
        if (_started)
        {
            throw new InvalidOperationException("HDF5 mirror writer is already started.");
        }

        ResetState();

        _sessionName = SanitizeName(sessionName);
        _sampleRateHz = sampleRateHz;
        _outputRootPath = CreateIncrementalDirectory(basePath, _sessionName);
        PrecreateDeviceDirectories(expectedChannelIds);
        _started = true;

        if (useBackgroundWriter)
        {
            Volatile.Write(ref _acceptingWrites, 1);
            _writerTask = Task.Run(ProcessQueueAsync);
        }
        else
        {
            Volatile.Write(ref _acceptingWrites, 0);
            _writerTask = null;
        }
    }

    public bool TryEnqueueClone(SdkRawBlock rawBlock)
    {
        ArgumentNullException.ThrowIfNull(rawBlock);

        if (_writerTask == null || Volatile.Read(ref _acceptingWrites) == 0)
        {
            return false;
        }

        var payloadCopy = rawBlock.PayloadSpan.ToArray();
        var clonedBlock = new SdkRawBlock
        {
            SampleTime = rawBlock.SampleTime,
            MessageType = rawBlock.MessageType,
            GroupId = rawBlock.GroupId,
            MachineId = rawBlock.MachineId,
            TotalDataCount = rawBlock.TotalDataCount,
            DataCountPerChannel = rawBlock.DataCountPerChannel,
            BufferCountBytes = rawBlock.BufferCountBytes,
            BlockIndex = rawBlock.BlockIndex,
            ChannelCount = rawBlock.ChannelCount,
            SampleRateHz = rawBlock.SampleRateHz,
            ReceivedAtUtc = rawBlock.ReceivedAtUtc,
            InterleavedSamples = payloadCopy,
            PayloadFloatCount = rawBlock.PayloadFloatCount,
            ReturnBufferToPool = false
        };

        if (!_queue.Writer.TryWrite(clonedBlock))
        {
            return false;
        }

        long pendingBlockCount = Interlocked.Increment(ref _pendingBlockCount);
        long pendingPayloadBytes = Interlocked.Add(ref _pendingPayloadBytes, clonedBlock.PayloadBytes);
        if (pendingBlockCount > MaxPendingBlockLimit || pendingPayloadBytes > MaxPendingPayloadByteLimit)
        {
            Fail(
                $"HDF5 mirror queue exceeded limit ({pendingBlockCount:N0}/{MaxPendingBlockLimit:N0} blocks, {pendingPayloadBytes:N0}/{MaxPendingPayloadByteLimit:N0} bytes).");
        }

        return true;
    }

    public void AppendRawBlock(SdkRawBlock rawBlock)
    {
        ArgumentNullException.ThrowIfNull(rawBlock);

        if (!_started)
        {
            throw new InvalidOperationException("HDF5 mirror writer is not started.");
        }

        if (_writerTask != null)
        {
            throw new InvalidOperationException("Cannot append synchronously while the background writer is active.");
        }

        if (Faulted)
        {
            return;
        }

        try
        {
            WriteBlock(rawBlock);
        }
        catch (Exception ex)
        {
            Fail($"HDF5 mirror write failed: {ex.Message}");
            throw;
        }
    }

    public SdkRawCaptureHdf5MirrorResult Complete()
    {
        if (!_started)
        {
            return new SdkRawCaptureHdf5MirrorResult();
        }

        if (_writerTask != null)
        {
            Volatile.Write(ref _acceptingWrites, 0);
            _queue.Writer.TryComplete();

            try
            {
                _writerTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Fail($"HDF5 mirror completion failed: {ex.Message}");
            }
            finally
            {
                _writerTask = null;
            }
        }
        else
        {
            try
            {
                FlushOpenFiles();
            }
            catch (Exception ex)
            {
                Fail($"HDF5 mirror completion failed: {ex.Message}");
            }
            finally
            {
                CloseAllFiles();
            }
        }

        _started = false;
        return BuildResult();
    }

    public void Dispose()
    {
        _started = false;
        Volatile.Write(ref _acceptingWrites, 0);
        _queue.Writer.TryComplete();

        if (_writerTask != null)
        {
            try
            {
                _writerTask.GetAwaiter().GetResult();
            }
            catch
            {
            }
            finally
            {
                _writerTask = null;
            }
        }

        CloseAllFiles();
    }

    private void ResetState()
    {
        _outputRootPath = "";
        _failureReason = "";
        _pendingBlockCount = 0;
        _pendingPayloadBytes = 0;
        _processedBlockCount = 0;
        _sampleRateHz = 0d;
        _sessionName = "session";
        _started = false;
        Volatile.Write(ref _acceptingWrites, 0);
        Volatile.Write(ref _faulted, 0);
        CloseAllFiles();
        _channelStates.Clear();
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _queue.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var rawBlock))
                {
                    Interlocked.Decrement(ref _pendingBlockCount);
                    Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);

                    if (Faulted)
                    {
                        continue;
                    }

                    try
                    {
                        WriteBlock(rawBlock);
                    }
                    catch (Exception ex)
                    {
                        Fail($"HDF5 mirror write failed: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            DrainPendingBlocks(reader);
            CloseAllFiles();
        }
    }

    private void WriteBlock(SdkRawBlock rawBlock)
    {
        int deviceId = SdkDeviceIdResolver.ResolveDeviceId(
            groupId: rawBlock.GroupId,
            machineId: rawBlock.MachineId);
        int channelCount = rawBlock.ChannelCount;
        int samplesPerChannel = rawBlock.DataCountPerChannel;
        var interleavedSamples = rawBlock.InterleavedSamples;

        for (int channelOffset = 0; channelOffset < channelCount; channelOffset++)
        {
            int channelId = ChannelNaming.MakeChannelId(deviceId, channelOffset + 1);
            var state = GetOrCreateChannelState(deviceId, channelId, samplesPerChannel);
            var channelSamples = state.EnsureScratchBuffer(samplesPerChannel);

            for (int sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
            {
                channelSamples[sampleIndex] = interleavedSamples[sampleIndex * channelCount + channelOffset];
            }

            state.Append(channelSamples, samplesPerChannel);
        }

        long processedBlocks = Interlocked.Increment(ref _processedBlockCount);
        if ((processedBlocks % FlushBlockStride) == 0)
        {
            FlushOpenFiles();
        }
    }

    private ChannelHdf5State GetOrCreateChannelState(int deviceId, int channelId, int suggestedChunkSize)
    {
        if (_channelStates.TryGetValue(channelId, out var existing))
        {
            return existing;
        }

        string deviceName = ChannelNaming.DeviceDisplayName(deviceId);
        string channelName = ChannelNaming.ChannelName(channelId);
        string deviceDirectory = Path.Combine(_outputRootPath, deviceName);
        Directory.CreateDirectory(deviceDirectory);

        string filePath = Path.Combine(deviceDirectory, $"{channelName}.h5");
        int chunkSize = Math.Max(1, suggestedChunkSize);
        var state = ChannelHdf5State.Create(filePath, chunkSize);
        _channelStates[channelId] = state;
        return state;
    }

    private void FlushOpenFiles()
    {
        foreach (var state in _channelStates.Values)
        {
            state.Flush();
        }
    }

    private void CloseAllFiles()
    {
        foreach (var state in _channelStates.Values)
        {
            try
            {
                state.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SdkRawCapture][HDF5] Close failed: {ex.Message}");
            }
        }
    }

    private void DrainPendingBlocks(ChannelReader<SdkRawBlock> reader)
    {
        while (reader.TryRead(out var rawBlock))
        {
            Interlocked.Decrement(ref _pendingBlockCount);
            Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);
        }
    }

    private void Fail(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _failureReason = reason;
        Volatile.Write(ref _acceptingWrites, 0);
        _queue.Writer.TryComplete();
        Console.WriteLine($"[SdkRawCapture][HDF5] {reason}");
    }

    private SdkRawCaptureHdf5MirrorResult BuildResult()
    {
        var writtenFiles = _channelStates.Values
            .Select(state => state.FilePath)
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sampleCounts = _channelStates
            .OrderBy(kvp => kvp.Key)
            .ToDictionary(
                kvp => ChannelNaming.ChannelName(kvp.Key),
                kvp => kvp.Value.SampleCount,
                StringComparer.OrdinalIgnoreCase);

        return new SdkRawCaptureHdf5MirrorResult
        {
            OutputRootPath = _outputRootPath,
            FileCount = writtenFiles.Length,
            WrittenFiles = writtenFiles,
            SampleCounts = sampleCounts,
            Faulted = Faulted,
            FailureReason = _failureReason
        };
    }

    private static string CreateIncrementalDirectory(string basePath, string folderName)
    {
        string safeName = SanitizeName(folderName);
        string initialPath = Path.Combine(basePath, safeName);
        if (!Directory.Exists(initialPath) && !File.Exists(initialPath))
        {
            Directory.CreateDirectory(initialPath);
            return initialPath;
        }

        for (int index = 1; ; index++)
        {
            string candidate = Path.Combine(basePath, $"{safeName}_{index:D3}");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "session";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        string safe = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }

    private void PrecreateDeviceDirectories(IReadOnlyCollection<int>? expectedChannelIds)
    {
        if (expectedChannelIds == null || expectedChannelIds.Count == 0)
        {
            return;
        }

        foreach (int deviceId in expectedChannelIds
            .Select(ChannelNaming.GetDeviceId)
            .Distinct()
            .OrderBy(id => id))
        {
            Directory.CreateDirectory(Path.Combine(_outputRootPath, ChannelNaming.DeviceDisplayName(deviceId)));
        }
    }

    private sealed class ChannelHdf5State : IDisposable
    {
        private readonly H5FileId _fileId;
        private readonly H5DataSetId _datasetId;
        private float[] _scratchBuffer = Array.Empty<float>();
        private bool _disposed;

        private ChannelHdf5State(string filePath, H5FileId fileId, H5DataSetId datasetId)
        {
            FilePath = filePath;
            _fileId = fileId;
            _datasetId = datasetId;
        }

        public string FilePath { get; }

        public long SampleCount { get; private set; }

        public static ChannelHdf5State Create(string filePath, int chunkSize)
        {
            var fileId = H5F.create(filePath, H5F.CreateMode.ACC_TRUNC);
            var dataSpaceId = H5S.create_simple(1, new long[] { 0 }, new long[] { -1 });
            var datasetCreatePropertyId = H5P.create(H5P.PropertyListClass.DATASET_CREATE);

            try
            {
                H5P.setChunk(datasetCreatePropertyId, new long[] { chunkSize });
                var datasetId = H5D.create(
                    fileId,
                    DatasetName,
                    H5T.H5Type.NATIVE_FLOAT,
                    dataSpaceId,
                    DefaultPropertyListId,
                    datasetCreatePropertyId,
                    DefaultPropertyListId);

                return new ChannelHdf5State(filePath, fileId, datasetId);
            }
            catch
            {
                H5F.close(fileId);
                throw;
            }
            finally
            {
                H5S.close(dataSpaceId);
                H5P.close(datasetCreatePropertyId);
            }
        }

        public float[] EnsureScratchBuffer(int requiredLength)
        {
            if (_scratchBuffer.Length != requiredLength)
            {
                _scratchBuffer = new float[requiredLength];
            }

            return _scratchBuffer;
        }

        public void Append(float[] values, int length)
        {
            long currentLength = SampleCount;
            long newLength = currentLength + length;
            H5D.setExtent(_datasetId, new long[] { newLength });

            var fileSpaceId = H5D.getSpace(_datasetId);
            var memorySpaceId = H5S.create_simple(1, new long[] { length });

            try
            {
                H5S.selectHyperslab(
                    fileSpaceId,
                    H5S.SelectOperator.SET,
                    new long[] { currentLength },
                    new long[] { length });
                H5D.write<float>(
                    _datasetId,
                    NativeFloatTypeId,
                    memorySpaceId,
                    fileSpaceId,
                    DefaultPropertyListId,
                    new H5Array<float>(values));
                SampleCount = newLength;
            }
            finally
            {
                H5S.close(memorySpaceId);
                H5S.close(fileSpaceId);
            }
        }

        public void Flush()
        {
            if (_disposed)
            {
                return;
            }

            H5F.flush(_fileId, H5F.Scope.LOCAL);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                H5F.flush(_fileId, H5F.Scope.LOCAL);
            }
            catch
            {
            }

            try
            {
                H5D.close(_datasetId);
            }
            catch
            {
            }

            try
            {
                H5F.close(_fileId);
            }
            catch
            {
            }
        }
    }
}
