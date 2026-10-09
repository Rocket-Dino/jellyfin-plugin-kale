using System;
using System.IO;

namespace Jellyfin.Plugin.Kale.Matroska;

/// <summary>Element IDs this plugin reads. Matroska spec, IDs kept with their marker bits.</summary>
internal static class MatroskaId
{
    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint Segment = 0x18538067;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackType = 0x83;
    public const uint FlagDefault = 0x88;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint Cluster = 0x1F43B675;
    public const uint Void = 0xEC;
    public const uint Crc32 = 0xBF;

    public const ulong TrackTypeAudio = 2;
}

/// <summary>One element header: where it starts, how wide its fields are, where its data ends.</summary>
internal readonly record struct EbmlElement(
    uint Id, long Start, int IdWidth, int SizeWidth, long DataStart, long Size, bool UnknownSize)
{
    public long End => DataStart + Size;
    public long HeaderLength => DataStart - Start;
}

internal static class Ebml
{
    /// <summary>Read a variable-length integer from a buffer.</summary>
    /// <param name="keepMarker">IDs keep the length marker; sizes drop it.</param>
    public static (ulong Value, int Width) ReadVint(ReadOnlySpan<byte> buffer, int position, bool keepMarker)
    {
        if (position >= buffer.Length)
        {
            throw new EndOfStreamException("vint past the end of the buffer");
        }

        byte first = buffer[position];
        int width = 1;
        int mask = 0x80;
        while (width <= 8 && (first & mask) == 0)
        {
            width++;
            mask >>= 1;
        }

        if (width > 8)
        {
            throw new InvalidDataException("vint wider than 8 bytes");
        }

        if (position + width > buffer.Length)
        {
            throw new EndOfStreamException("vint past the end of the buffer");
        }

        ulong value = keepMarker ? first : (ulong)(first & (mask - 1));
        for (int i = 1; i < width; i++)
        {
            value = (value << 8) | buffer[position + i];
        }

        return (value, width);
    }

    public static EbmlElement ReadElement(ReadOnlySpan<byte> buffer, int position, long baseOffset = 0)
    {
        var (id, idWidth) = ReadVint(buffer, position, keepMarker: true);
        var (size, sizeWidth) = ReadVint(buffer, position + idWidth, keepMarker: false);
        bool unknown = size == (1UL << (7 * sizeWidth)) - 1;
        long dataStart = baseOffset + position + idWidth + sizeWidth;
        return new EbmlElement((uint)id, baseOffset + position, idWidth, sizeWidth, dataStart, (long)size, unknown);
    }

    /// <summary>Read an element header from a stream at <paramref name="position"/> (at most 12 bytes).</summary>
    public static EbmlElement ReadElement(Stream stream, long position)
    {
        Span<byte> header = stackalloc byte[12];
        stream.Seek(position, SeekOrigin.Begin);
        int read = stream.ReadAtLeast(header, 2, throwOnEndOfStream: false);
        if (read < 2)
        {
            throw new EndOfStreamException("element header past the end of the file");
        }

        return ReadElement(header[..read], 0, position);
    }

    /// <summary>Encode a size with at least <paramref name="minWidth"/> bytes, widening if it must.</summary>
    public static byte[] EncodeSize(long value, int minWidth)
    {
        for (int width = Math.Max(1, minWidth); width <= 8; width++)
        {
            // All-ones is reserved for "unknown size".
            if ((ulong)value < (1UL << (7 * width)) - 1)
            {
                var bytes = new byte[width];
                ulong encoded = (1UL << (7 * width)) | (ulong)value;
                for (int i = width - 1; i >= 0; i--)
                {
                    bytes[i] = (byte)(encoded & 0xFF);
                    encoded >>= 8;
                }

                return bytes;
            }
        }

        throw new InvalidDataException($"size {value} does not fit an EBML vint");
    }

    public static ulong ReadUInt(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
