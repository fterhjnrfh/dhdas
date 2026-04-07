using System;

namespace DH.Client.App.Services.Storage;

internal readonly struct Hdf5CompressionSettings
{
    public Hdf5CompressionSettings(
        CompressionType requestedType,
        CompressionType effectiveType,
        int deflateLevel,
        bool requestedButUnsupported)
    {
        RequestedType = requestedType;
        EffectiveType = effectiveType;
        DeflateLevel = deflateLevel;
        RequestedButUnsupported = requestedButUnsupported;
    }

    public CompressionType RequestedType { get; }

    public CompressionType EffectiveType { get; }

    public int DeflateLevel { get; }

    public bool RequestedButUnsupported { get; }

    public bool CompressionApplied => EffectiveType == CompressionType.Zlib && DeflateLevel > 0;

    public string Summary
        => CompressionApplied
            ? $"HDF5 Deflate(level {DeflateLevel})"
            : RequestedButUnsupported
                ? $"HDF5 native compression only supports Zlib/Deflate. Requested {RequestedType}, exported without compression"
                : RequestedType == CompressionType.Zlib
                    ? "HDF5 export without compression (Zlib level 0)"
                    : "HDF5 export without compression";

    public static Hdf5CompressionSettings From(CompressionType requestedType, CompressionOptions? options)
    {
        if (requestedType == CompressionType.None)
        {
            return new Hdf5CompressionSettings(
                requestedType,
                CompressionType.None,
                deflateLevel: 0,
                requestedButUnsupported: false);
        }

        if (requestedType == CompressionType.Zlib)
        {
            int deflateLevel = Math.Clamp(options?.ZlibLevel ?? 6, 0, 9);
            return new Hdf5CompressionSettings(
                requestedType,
                deflateLevel > 0 ? CompressionType.Zlib : CompressionType.None,
                deflateLevel,
                requestedButUnsupported: false);
        }

        return new Hdf5CompressionSettings(
            requestedType,
            CompressionType.None,
            deflateLevel: 0,
            requestedButUnsupported: true);
    }
}
