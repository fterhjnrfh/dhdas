using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;

namespace DH.Client.App.Data
{
    public class DataBus : IDataBus
    {
        // 使用环形缓冲区存储每个通道的最新数据
        private readonly ConcurrentDictionary<int, RingBuffer<CurvePoint>> _channelBuffers = new();
        
        // 默认缓冲区大小
        private const int DefaultBufferSize = 0;
        private readonly int _bufferSize;
        
        // 事件：通道添加
        public event EventHandler<int> ChannelAdded;
        
        // 事件：通道移除
        public event EventHandler<int> ChannelRemoved;
        
        // 事件：数据更新
        public event EventHandler<DataUpdateEventArgs> DataUpdated;
        
        // 数据帧发布和订阅的同步对象
        private readonly ConcurrentDictionary<int, Channel<IDataFrame>> _channels = new();
        
        public DataBus(int bufferSize = DefaultBufferSize)
        {
            _bufferSize = bufferSize;
        }
        
        // 实现IDataBus接口方法：订阅特定通道
        public async IAsyncEnumerable<IDataFrame> SubscribeChannel(int channelId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
            
            while (!ct.IsCancellationRequested)
            {
                var frame = await channel.Reader.ReadAsync(ct);
                yield return frame;
            }
        }
        
        // 实现IDataBus接口方法：发布数据帧
        public async ValueTask PublishFrameAsync(IDataFrame frame, CancellationToken ct = default)
        {
            if (frame == null)
                return;
                
            var channelId = frame.ChannelId;
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
            
            await channel.Writer.WriteAsync(frame, ct);
            
            // 同时转换为CurvePoint并发布到内部缓冲区
            var points = ConvertFrameToCurvePoints(frame);
            PublishData(channelId, points);
        }
        
        // 实现IDataBus接口方法：订阅所有通道
        public async IAsyncEnumerable<IDataFrame> SubscribeAll([EnumeratorCancellation] CancellationToken ct)
        {
            // 创建一个合并所有通道的Channel
            var mergedChannel = Channel.CreateUnbounded<IDataFrame>();
            
            // 为每个现有通道创建一个转发任务
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
            
            // 监听新通道的创建
            tasks.Add(Task.Run(async () =>
            {
                var knownChannels = new HashSet<int>(_channels.Keys);
                
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct); // 定期检查新通道
                    
                    foreach (var channelId in _channels.Keys)
                    {
                        if (!knownChannels.Contains(channelId))
                        {
                            knownChannels.Add(channelId);
                            
                            // 为新通道创建转发任务
                            _ = Task.Run(async () =>
                            {
                                await foreach (var frame in SubscribeChannel(channelId, ct))
                                {
                                    await mergedChannel.Writer.WriteAsync(frame, ct);
                                }
                            }, ct);
                        }
                    }
                }
            }, ct));
            
            // 返回合并后的数据流
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
                var cap = _bufferSize <= 0 ? 1024 : _bufferSize;
                var allowExpand = _bufferSize <= 0;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand);
                OnChannelAdded(channelId);
                return newBuffer;
            });
        }
        
        // 辅助方法：将数据帧转换为曲线点
        private IReadOnlyList<CurvePoint> ConvertFrameToCurvePoints(IDataFrame frame)
        {
            var samples = frame.Samples;
            var points = new List<CurvePoint>(samples.Length);
            
            // 计算样本时间间隔（假设采样率来自Header，默认为1000Hz）
            var sampleRate = frame.Header?.SampleRate ?? 1000;
            double timeInterval = 1.0 / sampleRate;
            
            // 使用帧的时间戳作为起始时间，加上样本索引对应的时间偏移
            double startTime = frame.Timestamp.Ticks / 10_000.0; // 转换为毫秒
            
            for (int i = 0; i < samples.Length; i++)
            {
                // 计算每个样本的实际时间戳
                double sampleTime = startTime + (i * timeInterval * 1000); // 转换为毫秒
                points.Add(new CurvePoint(sampleTime, samples.Span[i]));
            }
            
            return points;
        }
        
        // 发布数据到总线
        public void PublishData(int channelId, IReadOnlyList<CurvePoint> data)
        {
            if (data == null || data.Count == 0)
                return;
                
            // 确保通道缓冲区存在
            var buffer = _channelBuffers.GetOrAdd(channelId, _ => 
            {
                var cap = _bufferSize <= 0 ? 1024 : _bufferSize;
                var allowExpand = _bufferSize <= 0;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand);
                OnChannelAdded(channelId);
                return newBuffer;
            });
            
            // 将数据添加到缓冲区
            foreach (var point in data)
            {
                buffer.Add(point);
            }
            
            // 触发数据更新事件
            OnDataUpdated(channelId, data);
        }
        
        // 获取通道的最新数据
        public IReadOnlyList<CurvePoint> GetLatestData(int channelId, int count = -1)
        {
            if (_channelBuffers.TryGetValue(channelId, out var buffer))
            {
                // count = -1 表示获取所有数据，用于存储和完整显示
                if (count <= 0)
                    return buffer.GetAll();
                return buffer.GetLatest(count);
            }
            
            return Array.Empty<CurvePoint>();
        }
        
        // 获取所有可用通道
        public IReadOnlyList<int> GetAvailableChannels()
        {
            return new List<int>(_channelBuffers.Keys);
        }
        
        // 移除通道
        public void RemoveChannel(int channelId)
        {
            if (_channelBuffers.TryRemove(channelId, out _))
            {
                OnChannelRemoved(channelId);
            }
        }
        
        // 触发通道添加事件
        private void OnChannelAdded(int channelId)
        {
            ChannelAdded?.Invoke(this, channelId);
        }
        
        // 触发通道移除事件
        private void OnChannelRemoved(int channelId)
        {
            ChannelRemoved?.Invoke(this, channelId);
        }
        
        // 触发数据更新事件
        private void OnDataUpdated(int channelId, IReadOnlyList<CurvePoint> data)
        {
            DataUpdated?.Invoke(this, new DataUpdateEventArgs(channelId, data));
        }
    }
    
    // 数据更新事件参数
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
