using System;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("LZ4 Compression Test");
        
        // 创建测试数据
        double[] originalData = new double[1000];
        for (int i = 0; i < originalData.Length; i++)
        {
            originalData[i] = Math.Sin(i * 0.1) * 100.0 + i * 0.5;
        }
        
        Console.WriteLine($"Original data length: {originalData.Length}");
        
        // 压缩数据（模拟TdmsStorage中的压缩逻辑）
        byte[] rawBytes = new byte[originalData.Length * sizeof(double)];
        Buffer.BlockCopy(originalData, 0, rawBytes, 0, rawBytes.Length);
        
        byte[] compressedBytes = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
        int compressedSize = LZ4Codec.Encode(
            rawBytes, 0, rawBytes.Length,
            compressedBytes, 0, compressedBytes.Length,
            LZ4Level.L00_FAST);
        
        Console.WriteLine($"Raw bytes: {rawBytes.Length}, Compressed bytes: {compressedSize}");
        
        // 模拟存储到TDMS的格式：[原始样本数, 压缩字节数, 压缩数据...]
        var metaAndCompressed = new double[2 + (compressedSize + 7) / 8];
        metaAndCompressed[0] = originalData.Length; // 元数据：原始样本数
        metaAndCompressed[1] = compressedSize; // 元数据：压缩后字节数
        
        // 修复后的压缩数据转换逻辑（与TdmsStorage中的一致）
        for (int i = 0; i < (compressedSize + 7) / 8; i++)
        {
            byte[] temp = new byte[8];
            int bytesToCopy = Math.Min(8, compressedSize - i * 8);
            Buffer.BlockCopy(compressedBytes, i * 8, temp, 0, bytesToCopy);
            double val = BitConverter.ToDouble(temp, 0);
            metaAndCompressed[2 + i] = val;
        }
        
        Console.WriteLine($"Meta+Compressed data length: {metaAndCompressed.Length}");
        
        // 测试解压缩（模拟TdmsReaderUtil中的解压缩逻辑）
        double[] decompressedData = DecompressIfNeeded(metaAndCompressed);
        
        Console.WriteLine($"Decompressed data length: {decompressedData.Length}");
        
        // 验证数据是否一致（只比较原始数据部分）
        if (decompressedData.Length == originalData.Length)
        {
            bool isEqual = true;
            for (int i = 0; i < originalData.Length; i++)
            {
                if (Math.Abs(originalData[i] - decompressedData[i]) > 1e-10)
                {
                    Console.WriteLine($"Data mismatch at index {i}: {originalData[i]} vs {decompressedData[i]}");
                    isEqual = false;
                    break;
                }
            }
            
            if (isEqual)
            {
                Console.WriteLine("SUCCESS: All data matches!");
            }
            else
            {
                Console.WriteLine("ERROR: Data mismatch detected!");
            }
        }
        else
        {
            Console.WriteLine($"ERROR: Length mismatch - expected {originalData.Length}, got {decompressedData.Length}");
        }
    }
    
    /// <summary>
    /// 检测并解压缩 LZ4 压缩的数据。
    /// 压缩格式：[原始样本数, 压缩字节数, 压缩数据...]
    /// </summary>
    private static double[] DecompressIfNeeded(double[] data)
    {
        if (data == null || data.Length < 3)
        {
            Console.WriteLine("[LZ4解压] 数据为空或长度不足");
            return data;
        }
        
        // 检测是否为压缩数据：
        // 1. 第一个值应该是合理的样本数（正整数）
        // 2. 第二个值应该是合理的字节数（正整数）
        // 3. 数据长度应该匹配格式：2（元数据）+ ceil(压缩字节数/8)
        
        int originalSampleCount = (int)data[0];
        int compressedByteSize = (int)data[1];
        
        Console.WriteLine($"[LZ4解压] 检测到元数据 - 原始样本数: {originalSampleCount}, 压缩字节数: {compressedByteSize}");
        
        // 合理性检查
        // 压缩后的字节数可能大于原始字节数（压缩率>100%），所以我们只检查基本的合理性
        if (originalSampleCount <= 0 || originalSampleCount > 100_000_000 ||
            compressedByteSize <= 0 || compressedByteSize > 100_000_000)
        {
            Console.WriteLine("[LZ4解压] 元数据不合理，不是压缩格式");
            // 不是压缩格式，直接返回
            return data;
        }
        
        int expectedDoubleCount = 2 + (compressedByteSize + 7) / 8;
        if (data.Length < expectedDoubleCount || data.Length > expectedDoubleCount + 1)
        {
            Console.WriteLine($"[LZ4解压] 数据长度不匹配 - 期望: {expectedDoubleCount}, 实际: {data.Length}");
            // 长度不匹配，不是压缩格式
            return data;
        }
        
        Console.WriteLine("[LZ4解压] 开始解压缩...");
        
        try
        {
            // 提取压缩数据
            byte[] compressedBytes = new byte[compressedByteSize];
            for (int i = 0; i < (compressedByteSize + 7) / 8; i++)
            {
                double val = data[2 + i];
                byte[] doubleBytes = BitConverter.GetBytes(val);
                int bytesToCopy = Math.Min(8, compressedByteSize - i * 8);
                Buffer.BlockCopy(doubleBytes, 0, compressedBytes, i * 8, bytesToCopy);
            }
            
            // LZ4 解压缩
            int originalByteSize = originalSampleCount * sizeof(double);
            byte[] decompressedBytes = new byte[originalByteSize];
            int decompressedSize = LZ4Codec.Decode(
                compressedBytes, 0, compressedByteSize,
                decompressedBytes, 0, originalByteSize);
            
            if (decompressedSize != originalByteSize)
            {
                Console.WriteLine($"[LZ4解压] 警告：解压后大小 {decompressedSize} 不匹配预期 {originalByteSize}");
                return data; // 解压失败，返回原始数据
            }
            
            // 转换回 double[]
            double[] decompressedData = new double[originalSampleCount];
            Buffer.BlockCopy(decompressedBytes, 0, decompressedData, 0, originalByteSize);
            
            Console.WriteLine($"[LZ4解压] 成功：{compressedByteSize} bytes -> {originalSampleCount} samples ({originalByteSize} bytes)");
            return decompressedData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LZ4解压] 失败：{ex.Message}，返回原始数据");
            return data; // 解压失败，返回原始数据
        }
    }
}
