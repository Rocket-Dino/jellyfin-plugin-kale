using System;

namespace Jellyfin.Plugin.Kale.Matroska;

/// <summary>
/// The CRC-32 Matroska's CRC-32 element carries: IEEE 802.3 (polynomial 0xEDB88320,
/// reflected), stored little-endian, over every byte of the parent element's data
/// after the CRC element itself. ffmpeg writes one into each level-1 element by default,
/// so any file it muxed has one on Tracks.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The 6-byte CRC-32 element for <paramref name="covered"/>.</summary>
    public static byte[] Element(ReadOnlySpan<byte> covered)
    {
        uint crc = Compute(covered);
        return new byte[] { 0xBF, 0x84, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24) };
    }

    public static bool Matches(ReadOnlySpan<byte> crcData, ReadOnlySpan<byte> covered) =>
        crcData.Length == 4 && BitConverter.ToUInt32(crcData) == Compute(covered);
}
