using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;

namespace DH.Client.App.Data
{
    public class DataBus : IDataBus
    {
        private readonly ConcurrentDictionary<int, RingBuffer<CurvePoint>> _channelBuffers = new();

        private const int DefaultBufferSize = 16384;
        private const int MinPreviewPointsPerFrame = 2;
        private const int MaxPreviewPointsPerFrame = 256;
        private const int TargetPreviewPointsPerSecond = 600;
        private readonly int _bufferSize;
        private long _previewOriginTicks;
        private readonly ConcurrentDictionary<int, long> _channelPreviewSampleCounts = new();

        public event EventHandler<int> ChannelAdded;
        public event EventHandler<int> ChannelRemoved;
        public event EventHandler<DataUpdateEventArgs> DataUpdated;

        private readonly ConcurrentDictionary<int, Channel<IDataFrame>> _channels = new();
        private readonly ConcurrentDictionary<int, int> _channelSubscriberCounts = new();

        public DataBus(int bufferSize = DefaultBufferSize)
        {
            _bufferSize = bufferSize;
        }

        public async IAsyncEnumerable<IDataFrame> SubscribeChannel(int channelId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
            _channelSubscriberCounts.AddOrUpdate(channelId, 1, static (_, count) => count + 1);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await channel.Reader.ReadAsync(ct);
                    yield return frame;
                }
            }
            finally
            {
                _channelSubscriberCounts.AddOrUpdate(channelId, 0, static (_, count) => Math.Max(0, count - 1));
            }
        }

        public async ValueTask PublishFrameAsync(IDataFrame frame, CancellationToken ct = default)
        {
            if (frame == null)
                return;

            int channelId = frame.ChannelId;
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());

            if (_channelSubscriberCounts.TryGetValue(channelId, out var subscriberCount) && subscriberCount > 0)
            {
                await channel.Writer.WriteAsync(frame, ct);
            }

            var points = ConvertFrameToCurvePoints(frame);
            PublishData(channelId, points);
        }

        public async IAsyncEnumerable<IDataFrame> SubscribeAll([EnumeratorCancellation] CancellationToken ct)
        {
            var mergedChannel = Channel.CreateUnbounded<IDataFrame>();
            var tasks = new List<Task>();

            foreach (var channelId in _channels.Keys)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await foreach (var frame in SubscribeChannel(channelId, ct))
                    {
                        await mergedChannel.Writer.WriteAsync(frame, ct);
                    }
                }, ct));
            }

            tasks.Add(Task.Run(async () =>
            {
                var knownChannels = new HashSet<int>(_channels.Keys);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct);

                    foreach (var channelId in _channels.Keys)
                    {
                        if (!knownChannels.Add(channelId))
                            continue;

                        _ = Task.Run(async () =>
                        {
                            await foreach (var frame in SubscribeChannel(channelId, ct))
                            {
                                await mergedChannel.Writer.WriteAsync(frame, ct);
                            }
                        }, ct);
                    }
                }
            }, ct));

            while (!ct.IsCancellationRequested)
            {
                var frame = await mergedChannel.Reader.ReadAsync(ct);
                yield return frame;
            }
        }

        public void EnsureChannel(int channelId)
        {
            _channelBuffers.GetOrAdd(channelId, _ =>
            {
                int cap = _bufferSize <= 0 ? DefaultBufferSize : _bufferSize;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand: false);
                OnChannelAdded(channelId);
                return newBuffer;
            });
        }

        private IReadOnlyList<CurvePoint> ConvertFrameToCurvePoints(IDataFrame frame)
        {
            var samples = frame.Samples;
            if (samples.IsEmpty)
                return Array.Empty<CurvePoint>();

            int sampleRate = frame.Header?.SampleRate ?? 1000;
            sampleRate = Math.Max(1, sampleRate);
            double frameDurationSeconds = samples.Length / (double)sampleRate;
            int targetPointCount = (int)Math.Ceiling(frameDurationSeconds * TargetPreviewPointsPerSecond);
            int pointCount = Math.Clamp(targetPointCount, MinPreviewPointsPerFrame, MaxPreviewPointsPerFrame);
            pointCount = Math.Min(samples.Length, pointCount);
            var points = new CurvePoint[pointCount];

            double timeInterval = 1.0 / sampleRate;
            long newTotalSampleCount = _channelPreviewSampleCounts.AddOrUpdate(
                frame.ChannelId,
                samples.Length,
                (_, existing) => existing + samples.Length);
            long frameStartSampleIndex = Math.Max(0, newTotalSampleCount - samples.Length);
            double startTime = frameStartSampleIndex * timeInterval;
            for (int i = 0; i < pointCount; i++)
            {
                int bucketStart = pointCount == samples.Length
                    ? i
                    : (int)Math.Floor(i * samples.Length / (double)pointCount);
                int bucketEnd = pointCount == samples.Length
                    ? i
                    : (int)Math.Floor((i + 1) * samples.Length / (double)pointCount) - 1;

                bucketStart = Math.Clamp(bucketStart, 0, samples.Length - 1);
                bucketEnd = Math.Clamp(bucketEnd, bucketStart, samples.Length - 1);

                double sum = 0.0;
                int sampleCount = bucketEnd - bucketStart + 1;
                for (int sampleIndex = bucketStart; sampleIndex <= bucketEnd; sampleIndex++)
                {
                    sum += samples.Span[sampleIndex];
                }

                int centerSampleIndex = bucketStart + ((bucketEnd - bucketStart) / 2);
                double sampleTime = startTime + (centerSampleIndex * timeInterval);
                double sampleValue = sampleCount > 0
                    ? sum / sampleCount
                    : samples.Span[centerSampleIndex];
                points[i] = new CurvePoint(sampleTime, sampleValue);
            }

            return points;
        }

        public void PublishData(int channelId, IReadOnlyList<CurvePoint> data)
        {
            if (data == null || data.Count == 0)
                return;

            var buffer = _channelBuffers.GetOrAdd(channelId, _ =>
            {
                int cap = _bufferSize <= 0 ? DefaultBufferSize : _bufferSize;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand: false);
                OnChannelAdded(channelId);
                return newBuffer;
            });

            buffer.AddRange(data);
            OnDataUpdated(channelId, data);
        }

        public IReadOnlyList<CurvePoint> GetLatestData(int channelId, int count = -1)
        {
            if (_channelBuffers.TryGetValue(channelId, out var buffer))
            {
                if (count <= 0)
                    return buffer.GetAll();
                return buffer.GetLatest(count);
            }

            return Array.Empty<CurvePoint>();
        }

        public IReadOnlyList<int> GetAvailableChannels()
        {
            return new List<int>(_channelBuffers.Keys);
        }

        public void RemoveChannel(int channelId)
        {
            if (_channelBuffers.TryRemove(channelId, out _))
            {
                OnChannelRemoved(channelId);
            }
        }

        public void ResetPreviewTimeline(bool clearBuffers = true)
        {
            Interlocked.Exchange(ref _previewOriginTicks, 0);
            _channelPreviewSampleCounts.Clear();

            if (!clearBuffers)
            {
                return;
            }

            foreach (var buffer in _channelBuffers.Values)
            {
                buffer.Clear();
            }
        }

        private void OnChannelAdded(int channelId)
        {
            ChannelAdded?.Invoke(this, channelId);
        }

        private void OnChannelRemoved(int channelId)
        {
            ChannelRemoved?.Invoke(this, channelId);
        }

        private void OnDataUpdated(int channelId, IReadOnlyList<CurvePoint> data)
        {
            DataUpdated?.Invoke(this, new DataUpdateEventArgs(channelId, data));
        }
    }

    public class DataUpdateEventArgs : EventArgs
    {
        public int ChannelId { get; }
        public IReadOnlyList<CurvePoint> Data { get; }

        public DataUpdateEventArgs(int channelId, IReadOnlyList<CurvePoint> data)
        {
            ChannelId = channelId;
            Data = data;
        }
    }
}
