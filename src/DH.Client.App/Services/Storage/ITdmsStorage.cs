using System;
using System.Collections.Generic;

namespace DH.Client.App.Services.Storage
{
    public interface ITdmsStorage : IDisposable
    {
        // 初始化并打开存储（创建文件/通道等）
        void Start(string basePath, IEnumerable<int> channelIds, string sessionName, double sampleRateHz, 
                   CompressionType compressionType = CompressionType.None, PreprocessType preprocessType = PreprocessType.None,
                   CompressionOptions? compressionOptions = null);
        
        // 写入一批采样点（实时追加）
        void Write(int channelId, ReadOnlySpan<double> samples);
        
        // 刷盘
        void Flush();
        
        // 停止并关闭存储
        void Stop();

        /// <summary>获取写入期间已生成的 TDMS 文件路径列表</summary>
        IReadOnlyList<string> GetWrittenFiles();

        /// <summary>获取每个通道写入时累计的 SHA-256 哈希（通道名 → hash hex string）</summary>
        IReadOnlyDictionary<string, string> GetWriteHashes();

        /// <summary>获取每个通道写入的总样本数（通道名 → sample count）</summary>
        IReadOnlyDictionary<string, long> GetWriteSampleCounts();
    }
}