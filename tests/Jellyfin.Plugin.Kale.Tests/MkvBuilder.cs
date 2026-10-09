using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>Builds just enough Matroska to exercise the editor: EBML header, Segment,
/// optional Info, Tracks, an optional Void, and a Cluster stand-in of known bytes.</summary>
internal sealed class MkvBuilder
{
    private readonly List<byte[]> _entries = new();
    private int _voidLength;
    private bool _clusterBeforeTracks;
    private bool _crcOnTracks;
    private bool _crcWrong;
    private int _entrySizeWidth = 2;

    /// <summary>ffmpeg writes TrackEntry sizes 8 bytes wide.</summary>
    public MkvBuilder EntrySizeWidth(int width)
    {
        _entrySizeWidth = width;
        return this;
    }

    public static byte[] Element(uint id, byte[] data, int sizeWidth = 0)
    {
        var idBytes = id switch
        {
            <= 0xFF => new[] { (byte)id },
            <= 0xFFFF => new[] { (byte)(id >> 8), (byte)id },
            <= 0xFFFFFF => new[] { (byte)(id >> 16), (byte)(id >> 8), (byte)id },
            _ => new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id },
        };
        var size = Matroska.Ebml.EncodeSize(data.Length, sizeWidth == 0 ? 1 : sizeWidth);
        return idBytes.Concat(size).Concat(data).ToArray();
    }

    public static byte[] UInt(uint id, ulong value) => Element(id, new[] { (byte)value });

    public MkvBuilder Video() => Track(1, null, null);

    public MkvBuilder Audio(string language, int? flagDefault) => Track(2, language, flagDefault);

    private MkvBuilder Track(int type, string? language, int? flagDefault)
    {
        var kids = new List<byte>();
        kids.AddRange(UInt(0xD7, (ulong)_entries.Count + 1)); // TrackNumber
        kids.AddRange(UInt(0x83, (ulong)type));
        if (flagDefault is not null)
        {
            kids.AddRange(UInt(0x88, (ulong)flagDefault));
        }

        if (language is not null)
        {
            kids.AddRange(Element(0x22B59C, System.Text.Encoding.ASCII.GetBytes(language)));
        }

        kids.AddRange(Element(0x86, System.Text.Encoding.ASCII.GetBytes(type == 1 ? "V_MPEG4/ISO/AVC" : "A_AC3")));
        _entries.Add(Element(0xAE, kids.ToArray(), sizeWidth: _entrySizeWidth));
        return this;
    }

    public MkvBuilder Void(int totalLength)
    {
        _voidLength = totalLength;
        return this;
    }

    public MkvBuilder ClusterBeforeTracks()
    {
        _clusterBeforeTracks = true;
        return this;
    }

    public MkvBuilder CrcOnTracks(bool wrong = false)
    {
        _crcOnTracks = true;
        _crcWrong = wrong;
        return this;
    }

    /// <summary>Bytes that stand in for the media. The editor must never change them.</summary>
    public static readonly byte[] ClusterPayload = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray();

    public byte[] Build()
    {
        var ebml = Element(0x1A45DFA3, UInt(0x4282, 1).Concat(Element(0x4282, System.Text.Encoding.ASCII.GetBytes("matroska"))).ToArray());
        var info = Element(0x1549A966, UInt(0x2AD7B1, 1)); // Info { TimecodeScale }
        var tracksData = new List<byte>();
        foreach (var e in _entries)
        {
            tracksData.AddRange(e);
        }

        if (_crcOnTracks)
        {
            var crc = Matroska.Crc32.Element(tracksData.ToArray());
            if (_crcWrong)
            {
                crc[2] ^= 0xFF;
            }

            tracksData.InsertRange(0, crc);
        }

        var tracks = Element(0x1654AE6B, tracksData.ToArray(), sizeWidth: 2);
        var voidElement = _voidLength == 0 ? new byte[0] : VoidOf(_voidLength);
        var cluster = Element(0x1F43B675, ClusterPayload, sizeWidth: 4);

        var segmentData = new List<byte>(info);
        if (_clusterBeforeTracks)
        {
            segmentData.AddRange(cluster);
        }

        segmentData.AddRange(tracks);
        segmentData.AddRange(voidElement);
        if (!_clusterBeforeTracks)
        {
            segmentData.AddRange(cluster);
        }

        var segment = Element(0x18538067, segmentData.ToArray(), sizeWidth: 8);
        return ebml.Concat(segment).ToArray();
    }

    private static byte[] VoidOf(int total)
    {
        // 0xEC + 1-byte size + zeros, or a 2-byte size for long ones.
        int width = total - 2 < 127 ? 1 : 2;
        var size = Matroska.Ebml.EncodeSize(total - 1 - width, width);
        return new[] { (byte)0xEC }.Concat(size).Concat(new byte[total - 1 - width]).ToArray();
    }

    public static MemoryStream Stream(byte[] bytes)
    {
        var s = new MemoryStream();
        s.Write(bytes);
        s.Position = 0;
        return s;
    }
}
