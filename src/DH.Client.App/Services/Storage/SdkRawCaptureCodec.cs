using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DH.Client.App.Services.Storage;

internal readonly struct SdkRawCaptureFileHeaderInfo
{
    public int Version { get; }

    public long StartedAtUtcTicks { get; }

    public double SampleRateHz { get; }

    public int ExpectedChannelCount { get; }

    public CompressionType CompressionType { get; }

    public PreprocessType PreprocessType { get; }

    public bool UsesEncodedPayload =>
        Version >= SdkRawCaptureFormat.FormatVersion
        && (CompressionType != CompressionType.None || PreprocessType != PreprocessType.None);

    public SdkRawCaptureFileHeaderInfo(
        int version,
        long startedAtUtcTicks,
        double sampleRateHz,
        int expectedChannelCount,
        CompressionType compressionType,
        PreprocessType preprocessType)
    {
        Version = version;
        StartedAtUtcTicks = startedAtUtcTicks;
        SampleRateHz = sampleRateHz;
        ExpectedChannelCount = expectedChannelCount;
        CompressionType = compressionType;
        PreprocessType = preprocessType;
    }
}

internal sealed class SdkRawCapturePayloadEncodeResult
{
    public byte[] PayloadBytes { get; init; } = Array.Empty<byte>();

    public int RawBytes { get; init; }

    public int CodecBytes { get; init; }
}

internal static class SdkRawCaptureFormatCodec
{
    private const int StorageSettingsMask = 0xFFFF;
    private const int PreprocessShift = 16;

    public static int SelectFormatVersion(CompressionType compressionType, PreprocessType preprocessType)
        => compressionType == CompressionType.None && preprocessType == PreprocessType.None
            ? SdkRawCaptureFormat.LegacyFormatVersion
            : SdkRawCaptureFormat.FormatVersion;

    public static int PackStorageSettings(CompressionType compressionType, PreprocessType preprocessType)
        => (((int)preprocessType & StorageSettingsMask) << PreprocessShift)
            | ((int)compressionType & StorageSettingsMask);

    public static SdkRawCaptureFileHeaderInfo ReadFileHeader(BinaryReader reader)
    {
        ulong magic = reader.ReadUInt64();
        if (magic != SdkRawCaptureFormat.FileMagic)
        {
            throw new InvalidDataException("The selected file is not a valid SDK raw capture.");
        }

        int version = reader.ReadInt32();
        if (!SdkRawCaptureFormat.IsSupportedVersion(version))
        {
            throw new InvalidDataException($"Unsupported raw capture version: {version}.");
        }

        long startedAtUtcTicks = reader.ReadInt64();
        double sampleRateHz = reader.ReadDouble();
        int expectedChannelCount = reader.ReadInt32();
        int packedStorageSettings = reader.ReadInt32();

        CompressionType compressionType = CompressionType.None;
        PreprocessType preprocessType = PreprocessType.None;
        if (version >= SdkRawCaptureFormat.FormatVersion)
        {
            compressionType = DecodeCompressionType(packedStorageSettings & StorageSettingsMask);
            preprocessType = DecodePreprocessType((packedStorageSettings >> PreprocessShift) & StorageSettingsMask);
        }

        return new SdkRawCaptureFileHeaderInfo(
            version,
            startedAtUtcTicks,
            sampleRateHz,
            expectedChannelCount,
            compressionType,
            preprocessType);
    }

    private static CompressionType DecodeCompressionType(int value)
        => Enum.IsDefined(typeof(CompressionType), value)
            ? (CompressionType)value
            : CompressionType.None;

    private static PreprocessType DecodePreprocessType(int value)
        => Enum.IsDefined(typeof(PreprocessType), value)
            ? (PreprocessType)value
            : PreprocessType.None;
}

internal static class SdkRawCapturePayloadCodec
{
    public static SdkRawCapturePayloadEncodeResult Encode(
        ReadOnlySpan<float> interleavedSamples,
        int channelCount,
        int samplesPerChannel,
        CompressionType compressionType,
        PreprocessType preprocessType,
        CompressionOptions? compressionOptions = null)
    {
        int sampleCount = checked(channelCount * samplesPerChannel);
        if (sampleCount == 0)
        {
            return new SdkRawCapturePayloadEncodeResult();
        }

        if (interleavedSamples.Length < sampleCount)
        {
            throw new InvalidDataException("Raw capture payload does not contain enough samples.");
        }

        int rawBytes = checked(sampleCount * sizeof(float));
        if (compressionType == CompressionType.None && preprocessType == PreprocessType.None)
        {
            var rawPayload = new byte[rawBytes];
            MemoryMarshal.AsBytes(interleavedSamples[..sampleCount]).CopyTo(rawPayload);
            return new SdkRawCapturePayloadEncodeResult
            {
                PayloadBytes = rawPayload,
                RawBytes = rawBytes,
                CodecBytes = rawBytes
            };
        }

        float[] channelMajorSamples = ToChannelMajor(interleavedSamples[..sampleCount], channelCount, samplesPerChannel);
        if (preprocessType != PreprocessType.None)
        {
            ApplyPreprocessInPlace(channelMajorSamples, channelCount, samplesPerChannel, preprocessType);
        }

        var channelMajorBytes = new byte[rawBytes];
        Buffer.BlockCopy(channelMajorSamples, 0, channelMajorBytes, 0, rawBytes);

        if (compressionType == CompressionType.None)
        {
            return new SdkRawCapturePayloadEncodeResult
            {
                PayloadBytes = channelMajorBytes,
                RawBytes = rawBytes,
                CodecBytes = rawBytes
            };
        }

        var (compressedBytes, compressedSize) = StorageCodec.CompressBytes(channelMajorBytes, compressionType, compressionOptions);
        byte[] exactPayload = compressedBytes.Length == compressedSize
            ? compressedBytes
            : compressedBytes.AsSpan(0, compressedSize).ToArray();

        return new SdkRawCapturePayloadEncodeResult
        {
            PayloadBytes = exactPayload,
            RawBytes = rawBytes,
            CodecBytes = compressedSize
        };
    }

    public static float[] Decode(
        ReadOnlySpan<byte> payloadBytes,
        int channelCount,
        int samplesPerChannel,
        CompressionType compressionType,
        PreprocessType preprocessType)
    {
        int sampleCount = checked(channelCount * samplesPerChannel);
        if (sampleCount == 0)
        {
            return Array.Empty<float>();
        }

        int rawBytes = checked(sampleCount * sizeof(float));
        byte[] channelMajorBytes;
        if (compressionType == CompressionType.None)
        {
            if (payloadBytes.Length != rawBytes)
            {
                throw new InvalidDataException("Raw capture payload metadata is inconsistent.");
            }

            channelMajorBytes = payloadBytes.ToArray();
        }
        else
        {
            channelMajorBytes = StorageCodec.DecompressBytes(payloadBytes.ToArray(), payloadBytes.Length, rawBytes, compressionType);
        }

        if (channelMajorBytes.Length != rawBytes)
        {
            throw new InvalidDataException("Raw capture payload length is inconsistent after decoding.");
        }

        var channelMajorSamples = new float[sampleCount];
        Buffer.BlockCopy(channelMajorBytes, 0, channelMajorSamples, 0, rawBytes);

        if (preprocessType != PreprocessType.None)
        {
            ReversePreprocessInPlace(channelMajorSamples, channelCount, samplesPerChannel, preprocessType);
        }

        return ToInterleaved(channelMajorSamples, channelCount, samplesPerChannel);
    }

    private static float[] ToChannelMajor(ReadOnlySpan<float> interleavedSamples, int channelCount, int samplesPerChannel)
    {
        if (channelCount <= 1)
        {
            return interleavedSamples.ToArray();
        }

        var channelMajor = new float[interleavedSamples.Length];
        for (int sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
        {
            int interleavedOffset = sampleIndex * channelCount;
            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                channelMajor[channelIndex * samplesPerChannel + sampleIndex] = interleavedSamples[interleavedOffset + channelIndex];
            }
        }

        return channelMajor;
    }

    private static float[] ToInterleaved(ReadOnlySpan<float> channelMajorSamples, int channelCount, int samplesPerChannel)
    {
        if (channelCount <= 1)
        {
            return channelMajorSamples.ToArray();
        }

        var interleaved = new float[channelMajorSamples.Length];
        for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            int channelOffset = channelIndex * samplesPerChannel;
            for (int sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
            {
                interleaved[sampleIndex * channelCount + channelIndex] = channelMajorSamples[channelOffset + sampleIndex];
            }
        }

        return interleaved;
    }

    private static void ApplyPreprocessInPlace(float[] channelMajorSamples, int channelCount, int samplesPerChannel, PreprocessType preprocessType)
    {
        if (samplesPerChannel <= 1 || preprocessType == PreprocessType.None)
        {
            return;
        }

        for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            int start = channelIndex * samplesPerChannel;
            switch (preprocessType)
            {
                case PreprocessType.DiffOrder1:
                    ApplyDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
                case PreprocessType.DiffOrder2:
                    ApplyDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    ApplyDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
                case PreprocessType.LinearPrediction:
                    ApplyLinearPredictionInPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
            }
        }
    }

    private static void ReversePreprocessInPlace(float[] channelMajorSamples, int channelCount, int samplesPerChannel, PreprocessType preprocessType)
    {
        if (samplesPerChannel <= 1 || preprocessType == PreprocessType.None)
        {
            return;
        }

        for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            int start = channelIndex * samplesPerChannel;
            switch (preprocessType)
            {
                case PreprocessType.DiffOrder1:
                    ReverseDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
                case PreprocessType.DiffOrder2:
                    ReverseDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    ReverseDiffOrder1InPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
                case PreprocessType.LinearPrediction:
                    ReverseLinearPredictionInPlace(channelMajorSamples, start, samplesPerChannel);
                    break;
            }
        }
    }

    private static void ApplyDiffOrder1InPlace(float[] samples, int start, int length)
    {
        for (int index = start + length - 1; index > start; index--)
        {
            samples[index] -= samples[index - 1];
        }
    }

    private static void ReverseDiffOrder1InPlace(float[] samples, int start, int length)
    {
        for (int index = start + 1; index < start + length; index++)
        {
            samples[index] += samples[index - 1];
        }
    }

    private static void ApplyLinearPredictionInPlace(float[] samples, int start, int length)
    {
        for (int index = start + length - 1; index >= start + 2; index--)
        {
            float predicted = (2f * samples[index - 1]) - samples[index - 2];
            samples[index] -= predicted;
        }

        if (length > 1)
        {
            samples[start + 1] -= samples[start];
        }
    }

    private static void ReverseLinearPredictionInPlace(float[] samples, int start, int length)
    {
        if (length > 1)
        {
            samples[start + 1] += samples[start];
        }

        for (int index = start + 2; index < start + length; index++)
        {
            float predicted = (2f * samples[index - 1]) - samples[index - 2];
            samples[index] += predicted;
        }
    }
}
