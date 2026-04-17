namespace DH.Client.App.Services.Storage;

internal static class CompressionReportFormatting
{
    public static string FormatCompressionType(CompressionType type)
        => type.ToString();

    public static string FormatPreprocessType(PreprocessType type)
        => type switch
        {
            PreprocessType.None => "无",
            PreprocessType.DiffOrder1 => "一阶差分编码",
            PreprocessType.DiffOrder2 => "二阶差分编码",
            PreprocessType.LinearPrediction => "线性预测编码",
            _ => type.ToString(),
        };

    public static string FormatCompressionOptions(CompressionType type, CompressionOptions? options)
    {
        var opts = options ?? new CompressionOptions();
        return type switch
        {
            CompressionType.None => "-",
            CompressionType.LZ4 => $"Level {opts.LZ4Level}",
            CompressionType.Zstd => $"Level {opts.ZstdLevel}, WindowLog {opts.ZstdWindowLog}",
            CompressionType.Brotli => $"Quality {opts.BrotliQuality}, WindowBits {opts.BrotliWindowBits}",
            CompressionType.Snappy => "-",
            CompressionType.Zlib => $"Level {opts.ZlibLevel}",
            CompressionType.LZ4_HC => $"Level {opts.LZ4HCLevel}",
            CompressionType.BZip2 => $"Block {opts.BZip2BlockSize} x100K",
            _ => "-",
        };
    }
}
