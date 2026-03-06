namespace DH.Client.App.Services.Storage;

/// <summary>
/// 压缩算法参数配置
/// 每个算法支持不同的参数范围
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// LZ4 压缩级别 (0-16)：0=快速, 16=最优
    /// </summary>
    public int LZ4Level { get; set; } = 0;

    /// <summary>
    /// Zstd 压缩级别 (1-22)：1=快速, 22=最优
    /// </summary>
    public int ZstdLevel { get; set; } = 3;

    /// <summary>
    /// Zstd 窗口大小 (10-31)：2^10 ~ 2^31 字节
    /// </summary>
    public int ZstdWindowLog { get; set; } = 23;

    /// <summary>
    /// Brotli 质量级别 (0-11)：0=快速, 11=最优
    /// </summary>
    public int BrotliQuality { get; set; } = 4;

    /// <summary>
    /// Brotli 窗口大小 (10-24)：2^10 ~ 2^24 字节
    /// </summary>
    public int BrotliWindowBits { get; set; } = 22;

    /// <summary>
    /// Zlib 压缩级别 (0-9)：0=不压缩, 9=最优
    /// </summary>
    public int ZlibLevel { get; set; } = 6;

    /// <summary>
    /// BZip2 块大小 (1-9)：100k ~ 900k 字节
    /// </summary>
    public int BZip2BlockSize { get; set; } = 9;

    /// <summary>
    /// LZ4_HC 压缩级别 (等同于 LZ4，最高为 12)
    /// </summary>
    public int LZ4HCLevel { get; set; } = 12;

    /// <summary>
    /// 复制当前配置
    /// </summary>
    public CompressionOptions Clone()
    {
        return new CompressionOptions
        {
            LZ4Level = this.LZ4Level,
            ZstdLevel = this.ZstdLevel,
            ZstdWindowLog = this.ZstdWindowLog,
            BrotliQuality = this.BrotliQuality,
            BrotliWindowBits = this.BrotliWindowBits,
            ZlibLevel = this.ZlibLevel,
            BZip2BlockSize = this.BZip2BlockSize,
            LZ4HCLevel = this.LZ4HCLevel,
        };
    }
}
