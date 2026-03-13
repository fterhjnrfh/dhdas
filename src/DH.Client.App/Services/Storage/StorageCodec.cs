using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using ZstdSharp;
using ZstdSharp.Unsafe;
using Snappier;

namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 统一的存储编解码器：负责预处理 + 压缩的编码（写入端）。
    /// 使用 v2 统一格式头：[signature, compressionType, preprocessType, sampleCount, compressedSize, data...]
    /// </summary>
    internal static class StorageCodec
    {
        /// <summary>v2 统一签名（8字节 ASCII → 1 个 double）</summary>
        internal static readonly byte[] V2Signature = Encoding.ASCII.GetBytes("DHCMPv2!");

        /// <summary>
        /// 对原始样本进行预处理 + 压缩，返回可直接写入 TDMS 的 double 数组（含 v2 头部）。
        /// 当压缩和预处理均为 None 时返回 null，调用方应直接写入原始数据。
        /// </summary>
        public static double[]? Encode(double[] samples, CompressionType compression, PreprocessType preprocess, CompressionOptions? options = null)
        {
            if (compression == CompressionType.None && preprocess == PreprocessType.None)
                return null;

            // Step 1: 预处理
            double[] processed = preprocess != PreprocessType.None
                ? DataPreprocessor.Apply(samples, preprocess)
                : samples;

            // Step 2: 转换为字节
            byte[] rawBytes = new byte[processed.Length * sizeof(double)];
            Buffer.BlockCopy(processed, 0, rawBytes, 0, rawBytes.Length);

            // Step 3: 压缩（如果指定了压缩算法）
            byte[] compressedBytes;
            int compressedSize;

            if (compression == CompressionType.None)
            {
                // 仅预处理，不压缩
                compressedBytes = rawBytes;
                compressedSize = rawBytes.Length;
            }
            else
            {
                (compressedBytes, compressedSize) = CompressBytes(rawBytes, compression, options);
            }

            // Step 4: 构建 v2 格式输出
            var result = new List<double>();

            // 签名
            double sig = BitConverter.ToDouble(V2Signature, 0);
            result.Add(sig);

            // 元数据：压缩类型、预处理类型、原始样本数、压缩字节数
            result.Add((double)(int)compression);
            result.Add((double)(int)preprocess);
            result.Add((double)samples.Length); // 原始样本数（预处理前）
            result.Add((double)compressedSize);

            // 压缩数据转为 double 数组（每 8 字节一个 double）
            int doubleCount = (compressedSize + 7) / 8;
            for (int i = 0; i < doubleCount; i++)
            {
                byte[] temp = new byte[8];
                int bytesToCopy = Math.Min(8, compressedSize - i * 8);
                Buffer.BlockCopy(compressedBytes, i * 8, temp, 0, bytesToCopy);
                result.Add(BitConverter.ToDouble(temp, 0));
            }

            return result.ToArray();
        }

        /// <summary>
        /// 根据压缩类型对字节数组进行压缩
        /// </summary>
        internal static (byte[] bytes, int size) CompressBytes(byte[] rawBytes, CompressionType type, CompressionOptions? options = null)
        {
            var opts = options ?? new CompressionOptions();
            switch (type)
            {
                case CompressionType.LZ4:
                {
                    // LZ4Level: 0(L00_FAST) ~ 12, 映射到 LZ4Level 枚举
                    var level = (K4os.Compression.LZ4.LZ4Level)Math.Clamp(opts.LZ4Level, 0, 12);
                    var buf = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
                    int size = LZ4Codec.Encode(rawBytes, 0, rawBytes.Length, buf, 0, buf.Length, level);
                    return (buf, size);
                }
                case CompressionType.LZ4_HC:
                {
                    // LZ4_HC: 3(L03_HC) ~ 12(L12_MAX)
                    var level = (K4os.Compression.LZ4.LZ4Level)Math.Clamp(opts.LZ4HCLevel, 3, 12);
                    var buf = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
                    int size = LZ4Codec.Encode(rawBytes, 0, rawBytes.Length, buf, 0, buf.Length, level);
                    return (buf, size);
                }
                case CompressionType.Zstd:
                {
                    // Zstd: level 1~22, windowLog 10~31
                    int level = Math.Clamp(opts.ZstdLevel, 1, 22);
                    int windowLog = Math.Clamp(opts.ZstdWindowLog, 10, 31);
                    using var compressor = new Compressor(level);
                    compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
                    var compressed = compressor.Wrap(rawBytes);
                    var bytes = compressed.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.Brotli:
                {
                    // Brotli quality 0~11, windowBits 10~24
                    int quality = Math.Clamp(opts.BrotliQuality, 0, 11);
                    int windowBits = Math.Clamp(opts.BrotliWindowBits, 10, 24);
                    // .NET 6 使用 BrotliEncoder 支持 quality + window 参数
                    using var encoder = new BrotliEncoder(quality, windowBits);
                    using var ms = new MemoryStream();
                    var input = rawBytes.AsSpan();
                    var buffer = new byte[65536];
                    while (true)
                    {
                        var status = encoder.Compress(input, buffer.AsSpan(), out int bytesConsumed, out int bytesWritten, input.Length == 0);
                        if (bytesWritten > 0) ms.Write(buffer, 0, bytesWritten);
                        if (bytesConsumed > 0) input = input.Slice(bytesConsumed);
                        if (status == OperationStatus.Done) break;
                        if (status == OperationStatus.InvalidData) throw new InvalidOperationException("Brotli compress error");
                    }
                    // flush remaining
                    while (true)
                    {
                        var status = encoder.Flush(buffer.AsSpan(), out int bytesWritten);
                        if (bytesWritten > 0) ms.Write(buffer, 0, bytesWritten);
                        if (status == OperationStatus.Done) break;
                    }
                    var bytes = ms.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.Snappy:
                {
                    // Snappy 无参数配置
                    var bytes = Snappy.CompressToArray(rawBytes);
                    return (bytes, bytes.Length);
                }
                case CompressionType.Zlib:
                {
                    // Zlib level 0~9 → 映射到 CompressionLevel
                    var level = opts.ZlibLevel switch
                    {
                        0 => CompressionLevel.NoCompression,
                        <= 3 => CompressionLevel.Fastest,
                        <= 6 => CompressionLevel.Optimal,
                        _ => CompressionLevel.SmallestSize,
                    };
                    using var ms = new MemoryStream();
                    using (var zlib = new ZLibStream(ms, level, leaveOpen: true))
                        zlib.Write(rawBytes, 0, rawBytes.Length);
                    var bytes = ms.ToArray();
                    return (bytes, bytes.Length);
                }
                case CompressionType.BZip2:
                {
                    // BZip2 blockSize 1~9 (100k ~ 900k)
                    int blockSize = Math.Clamp(opts.BZip2BlockSize, 1, 9);
                    using var input = new MemoryStream(rawBytes);
                    using var output = new MemoryStream();
                    ICSharpCode.SharpZipLib.BZip2.BZip2.Compress(input, output, false, blockSize);
                    var bytes = output.ToArray();
                    return (bytes, bytes.Length);
                }
                default:
                    throw new NotSupportedException($"不支持的压缩类型: {type}");
            }
        }

        /// <summary>
        /// 根据压缩类型对字节数组进行解压缩
        /// </summary>
        internal static byte[] DecompressBytes(byte[] compressedBytes, int compressedSize, int originalByteSize, CompressionType type)
        {
            switch (type)
            {
                case CompressionType.None:
                    return compressedBytes;

                case CompressionType.LZ4:
                case CompressionType.LZ4_HC: // LZ4_HC 与 LZ4 使用相同的解压算法
                {
                    var result = new byte[originalByteSize];
                    int decoded = LZ4Codec.Decode(compressedBytes, 0, compressedSize, result, 0, originalByteSize);
                    if (decoded != originalByteSize)
                        throw new InvalidDataException($"LZ4 解压大小不匹配: 期望 {originalByteSize}, 实际 {decoded}");
                    return result;
                }
                case CompressionType.Zstd:
                {
                    using var decompressor = new Decompressor();
                    var result = decompressor.Unwrap(compressedBytes.AsSpan(0, compressedSize)).ToArray();
                    if (result.Length != originalByteSize)
                        throw new InvalidDataException($"Zstd 解压大小不匹配: 期望 {originalByteSize}, 实际 {result.Length}");
                    return result;
                }
                case CompressionType.Brotli:
                {
                    using var ms = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    brotli.CopyTo(output);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                        throw new InvalidDataException($"Brotli 解压大小不匹配: 期望 {originalByteSize}, 实际 {result.Length}");
                    return result;
                }
                case CompressionType.Snappy:
                {
                    var result = Snappy.DecompressToArray(compressedBytes.AsSpan(0, compressedSize));
                    if (result.Length != originalByteSize)
                        throw new InvalidDataException($"Snappy 解压大小不匹配: 期望 {originalByteSize}, 实际 {result.Length}");
                    return result;
                }
                case CompressionType.Zlib:
                {
                    using var ms = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                        throw new InvalidDataException($"Zlib 解压大小不匹配: 期望 {originalByteSize}, 实际 {result.Length}");
                    return result;
                }
                case CompressionType.BZip2:
                {
                    using var input = new MemoryStream(compressedBytes, 0, compressedSize);
                    using var output = new MemoryStream();
                    ICSharpCode.SharpZipLib.BZip2.BZip2.Decompress(input, output, false);
                    var result = output.ToArray();
                    if (result.Length != originalByteSize)
                        throw new InvalidDataException($"BZip2 解压大小不匹配: 期望 {originalByteSize}, 实际 {result.Length}");
                    return result;
                }
                default:
                    throw new NotSupportedException($"不支持的压缩类型: {type}");
            }
        }
    }
}
