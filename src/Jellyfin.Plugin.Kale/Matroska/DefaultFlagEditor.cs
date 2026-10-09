using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Kale.Matroska;

/// <summary>An audio track as the file's own Tracks element describes it.</summary>
/// <param name="Ordinal">Position among the file's audio tracks, 0-based — how Jellyfin's audio streams line up.</param>
/// <param name="Language">The track's language tag; Matroska's default is "eng" when absent.</param>
/// <param name="FlagDefault">The written flag, or null when absent (which Matroska reads as 1).</param>
public sealed record AudioTrackFlags(int Ordinal, string Language, int? FlagDefault)
{
    public bool IsDefault => FlagDefault != 0;
}

/// <summary>Why an edit was declined. Every value has a sentence a person can act on.</summary>
public enum EditRefusal
{
    None,
    NotMatroska,
    TracksNotFound,
    TracksAfterMedia,
    ChecksumPresent,
    NoRoom,
    TrackNotFound,
    NothingToChange,
    ChangedUnderneath,
    VerificationFailed,
}

/// <summary>The region of the file the edit may touch: Tracks plus the Void directly after it.</summary>
public sealed class MatroskaHead
{
    internal MatroskaHead(long regionStart, byte[] region, EbmlElement tracks, long voidLength)
    {
        RegionStart = regionStart;
        Region = region;
        Tracks = tracks;
        VoidLength = voidLength;
    }

    public long RegionStart { get; }

    /// <summary>Bytes from the start of Tracks to the end of the Void after it.</summary>
    public byte[] Region { get; }

    internal EbmlElement Tracks { get; }

    /// <summary>Total length of the Void element after Tracks (0 when there is none).</summary>
    public long VoidLength { get; }
}

public sealed record EditPlan(
    EditRefusal Refusal,
    long Offset,
    byte[] Original,
    byte[] Patched,
    IReadOnlyList<AudioTrackFlags> Before,
    IReadOnlyList<AudioTrackFlags> After)
{
    public bool CanApply => Refusal == EditRefusal.None;

    public static EditPlan Refused(EditRefusal refusal, IReadOnlyList<AudioTrackFlags>? before = null) =>
        new(refusal, 0, Array.Empty<byte>(), Array.Empty<byte>(), before ?? Array.Empty<AudioTrackFlags>(), Array.Empty<AudioTrackFlags>());
}

/// <summary>
/// Changes which audio track a Matroska file marks as default — in place, without
/// moving a single media byte (ADR-019 amendment, research/default-flag-inplace-spike.md).
///
/// Only the Tracks element and the Void directly after it may change. The file keeps its
/// length, and every byte after that Void is untouched, so no SeekHead or Cues offset
/// moves. An absent FlagDefault means 1, so turning a track OFF may need the 3-byte
/// element written; the room comes out of the Void. No Void, no room: refuse.
/// </summary>
public static class DefaultFlagEditor
{
    /// <summary>How far into a file Tracks may sit. Real files put it in the first few KB.</summary>
    public const long MaxHeadScan = 16 * 1024 * 1024;

    /// <summary>The largest Tracks + Void region this will hold in memory.</summary>
    public const int MaxRegion = 4 * 1024 * 1024;

    public static (MatroskaHead? Head, EditRefusal Refusal) Locate(Stream stream)
    {
        EbmlElement header;
        try
        {
            header = Ebml.ReadElement(stream, 0);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            return (null, EditRefusal.NotMatroska);
        }

        if (header.Id != MatroskaId.EbmlHeader)
        {
            return (null, EditRefusal.NotMatroska);
        }

        var segment = Ebml.ReadElement(stream, header.End);
        if (segment.Id != MatroskaId.Segment)
        {
            return (null, EditRefusal.NotMatroska);
        }

        long position = segment.DataStart;
        for (int i = 0; i < 64 && position < MaxHeadScan; i++)
        {
            EbmlElement element;
            try
            {
                element = Ebml.ReadElement(stream, position);
            }
            catch (EndOfStreamException)
            {
                return (null, EditRefusal.TracksNotFound);
            }

            if (element.Id == MatroskaId.Cluster)
            {
                return (null, EditRefusal.TracksAfterMedia);
            }

            if (element.Id == MatroskaId.Tracks)
            {
                if (element.UnknownSize)
                {
                    return (null, EditRefusal.TracksNotFound);
                }

                long voidLength = 0;
                if (element.End < stream.Length)
                {
                    var next = Ebml.ReadElement(stream, element.End);
                    if (next.Id == MatroskaId.Void && !next.UnknownSize)
                    {
                        voidLength = next.End - next.Start;
                    }
                }

                long regionLength = element.End - element.Start + voidLength;
                if (regionLength > MaxRegion)
                {
                    return (null, EditRefusal.TracksNotFound);
                }

                var region = new byte[regionLength];
                stream.Seek(element.Start, SeekOrigin.Begin);
                stream.ReadExactly(region);
                return (new MatroskaHead(element.Start, region, element, voidLength), EditRefusal.None);
            }

            if (element.UnknownSize)
            {
                return (null, EditRefusal.TracksNotFound);
            }

            position = element.End;
        }

        return (null, EditRefusal.TracksNotFound);
    }

    public static IReadOnlyList<AudioTrackFlags> AudioTracks(MatroskaHead head)
    {
        var tracks = new List<AudioTrackFlags>();
        foreach (var entry in Entries(head))
        {
            if (entry.Type == MatroskaId.TrackTypeAudio)
            {
                tracks.Add(new AudioTrackFlags(tracks.Count, entry.Language, entry.FlagDefault));
            }
        }

        return tracks;
    }

    /// <summary>Make one audio track the only default.</summary>
    public static EditPlan PlanMakeDefault(MatroskaHead head, int audioOrdinal)
    {
        var before = AudioTracks(head);
        if (audioOrdinal < 0 || audioOrdinal >= before.Count)
        {
            return EditPlan.Refused(EditRefusal.TrackNotFound, before);
        }

        var desired = new Dictionary<int, int>();
        foreach (var track in before)
        {
            if (track.Ordinal == audioOrdinal)
            {
                if (track.FlagDefault != 1)
                {
                    desired[track.Ordinal] = 1;
                }
            }
            else if (track.IsDefault)
            {
                desired[track.Ordinal] = 0;
            }
        }

        return Plan(head, desired);
    }

    /// <summary>
    /// Write explicit flags for the named audio tracks; everything else is copied byte for
    /// byte. Tries the smallest edit first (every field keeps its width); if that leaves no
    /// room, re-encodes the size fields of the entries it is rewriting anyway at their
    /// natural width. ffmpeg writes TrackEntry sizes 8 bytes wide and leaves no Void after
    /// Tracks, so its files have room only there (found on the sandbox, 2026-10-04).
    /// </summary>
    public static EditPlan Plan(MatroskaHead head, IReadOnlyDictionary<int, int> desired)
    {
        var plan = Plan(head, desired, compact: false);
        return plan.Refusal == EditRefusal.NoRoom ? Plan(head, desired, compact: true) : plan;
    }

    private static EditPlan Plan(MatroskaHead head, IReadOnlyDictionary<int, int> desired, bool compact)
    {
        var before = AudioTracks(head);
        if (desired.Count == 0)
        {
            return EditPlan.Refused(EditRefusal.NothingToChange, before);
        }

        if (desired.Keys.Any(k => k < 0 || k >= before.Count))
        {
            return EditPlan.Refused(EditRefusal.TrackNotFound, before);
        }

        ReadOnlySpan<byte> region = head.Region;
        var tracks = head.Tracks;
        int tracksDataStart = (int)(tracks.DataStart - head.RegionStart);
        int tracksEnd = (int)(tracks.End - head.RegionStart);

        var newData = new List<byte>(tracksEnd - tracksDataStart + 16);
        int audioSeen = 0;
        int position = tracksDataStart;
        bool tracksCrc = false;
        while (position < tracksEnd)
        {
            var child = Ebml.ReadElement(region, position);
            int childEnd = (int)(child.End);
            if (child.Id == MatroskaId.Crc32)
            {
                // Only meaningful as the first child, and only rewritten if it is right today:
                // fixing a checksum that was already wrong would hide damage, not repair it.
                if (position != tracksDataStart
                    || !Crc32.Matches(region[(int)child.DataStart..childEnd], region[childEnd..tracksEnd]))
                {
                    return EditPlan.Refused(EditRefusal.ChecksumPresent, before);
                }

                tracksCrc = true;
                position = childEnd;
                continue;
            }

            if (child.Id != MatroskaId.TrackEntry)
            {
                newData.AddRange(region[position..childEnd].ToArray());
                position = childEnd;
                continue;
            }

            var entry = ReadEntry(region, child);
            if (entry.HasCrc && !EntryCrcIsValid(region, child))
            {
                return EditPlan.Refused(EditRefusal.ChecksumPresent, before);
            }

            int? want = null;
            if (entry.Type == MatroskaId.TrackTypeAudio)
            {
                if (desired.TryGetValue(audioSeen, out int value))
                {
                    want = value;
                }

                audioSeen++;
            }

            if (want is null)
            {
                newData.AddRange(region[position..childEnd].ToArray());
                position = childEnd;
                continue;
            }

            var body = new List<byte>((int)child.Size + 3);
            bool wrote = false;
            int kid = (int)child.DataStart;
            while (kid < childEnd)
            {
                var k = Ebml.ReadElement(region, kid);
                if (k.Id == MatroskaId.Crc32)
                {
                    kid = (int)k.End; // recomputed below
                    continue;
                }

                if (k.Id == MatroskaId.FlagDefault)
                {
                    body.AddRange(new byte[] { 0x88, 0x81, (byte)want.Value });
                    wrote = true;
                }
                else
                {
                    body.AddRange(region[kid..(int)k.End].ToArray());
                }

                kid = (int)k.End;
            }

            if (!wrote)
            {
                body.AddRange(new byte[] { 0x88, 0x81, (byte)want.Value });
            }

            if (entry.HasCrc)
            {
                body.InsertRange(0, Crc32.Element(body.ToArray()));
            }

            newData.AddRange(region[position..(position + child.IdWidth)].ToArray());
            newData.AddRange(Ebml.EncodeSize(body.Count, compact ? 1 : child.SizeWidth));
            newData.AddRange(body);
            position = childEnd;
        }

        if (tracksCrc)
        {
            newData.InsertRange(0, Crc32.Element(newData.ToArray()));
        }

        var newTracks = new List<byte>(newData.Count + 12);
        newTracks.AddRange(region[0..tracks.IdWidth].ToArray());
        newTracks.AddRange(Ebml.EncodeSize(newData.Count, tracks.SizeWidth));
        newTracks.AddRange(newData);

        long leftover = region.Length - newTracks.Count;
        if (leftover != 0 && leftover < 2)
        {
            return EditPlan.Refused(EditRefusal.NoRoom, before);
        }

        var patched = new byte[region.Length];
        newTracks.CopyTo(patched);
        if (leftover > 0)
        {
            byte[]? voidHeader = null;
            for (int width = 1; width <= 8; width++)
            {
                long payload = leftover - 1 - width;
                if (payload >= 0 && (ulong)payload < (1UL << (7 * width)) - 1)
                {
                    voidHeader = Ebml.EncodeSize(payload, width);
                    if (voidHeader.Length == width)
                    {
                        break;
                    }
                }
            }

            if (voidHeader is null)
            {
                return EditPlan.Refused(EditRefusal.NoRoom, before);
            }

            patched[newTracks.Count] = (byte)MatroskaId.Void;
            voidHeader.CopyTo(patched, newTracks.Count + 1);
            // The rest of the Void stays zero.
        }

        var after = AudioTracks(new MatroskaHead(head.RegionStart, patched, Ebml.ReadElement(patched, 0, head.RegionStart), leftover));
        return new EditPlan(EditRefusal.None, head.RegionStart, head.Region, patched, before, after);
    }

    /// <summary>
    /// Write the plan, then read the file back and prove it says what was planned.
    /// Refuses if the region changed since it was read; puts the original bytes back if
    /// the read-back disagrees.
    /// </summary>
    public static EditRefusal Apply(Stream stream, EditPlan plan)
    {
        if (!plan.CanApply)
        {
            return plan.Refusal;
        }

        long length = stream.Length;
        var current = new byte[plan.Original.Length];
        stream.Seek(plan.Offset, SeekOrigin.Begin);
        stream.ReadExactly(current);
        if (!current.AsSpan().SequenceEqual(plan.Original))
        {
            return EditRefusal.ChangedUnderneath;
        }

        Write(stream, plan.Offset, plan.Patched);

        var (head, refusal) = Locate(stream);
        bool verified = refusal == EditRefusal.None
            && head is not null
            && stream.Length == length
            && head.RegionStart == plan.Offset
            && head.Region.AsSpan().SequenceEqual(plan.Patched)
            && AudioTracks(head).SequenceEqual(plan.After);
        if (!verified)
        {
            Write(stream, plan.Offset, plan.Original);
            return EditRefusal.VerificationFailed;
        }

        return EditRefusal.None;
    }

    private static void Write(Stream stream, long offset, byte[] bytes)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(bytes);
        if (stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    private static bool EntryCrcIsValid(ReadOnlySpan<byte> region, EbmlElement entry)
    {
        var first = Ebml.ReadElement(region, (int)entry.DataStart);
        return first.Id == MatroskaId.Crc32
            && Crc32.Matches(region[(int)first.DataStart..(int)first.End], region[(int)first.End..(int)entry.End]);
    }

    private sealed record Entry(ulong? Type, string Language, int? FlagDefault, bool HasCrc);

    private static IEnumerable<Entry> Entries(MatroskaHead head)
    {
        var region = head.Region;
        var tracks = head.Tracks;
        int position = (int)(tracks.DataStart - head.RegionStart);
        int end = (int)(tracks.End - head.RegionStart);
        while (position < end)
        {
            var child = Ebml.ReadElement(region, position);
            if (child.Id == MatroskaId.TrackEntry)
            {
                yield return ReadEntry(region, child);
            }

            position = (int)child.End;
        }
    }

    private static Entry ReadEntry(ReadOnlySpan<byte> region, EbmlElement entry)
    {
        ulong? type = null;
        string language = "eng";
        string? bcp47 = null;
        int? flag = null;
        bool crc = false;
        int position = (int)entry.DataStart;
        int end = (int)entry.End;
        while (position < end)
        {
            var k = Ebml.ReadElement(region, position);
            var data = region[(int)k.DataStart..(int)k.End];
            switch (k.Id)
            {
                case MatroskaId.TrackType:
                    type = Ebml.ReadUInt(data);
                    break;
                case MatroskaId.FlagDefault:
                    flag = (int)Ebml.ReadUInt(data);
                    break;
                case MatroskaId.Language:
                    language = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                    break;
                case MatroskaId.LanguageBcp47:
                    bcp47 = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                    break;
                case MatroskaId.Crc32:
                    crc = true;
                    break;
            }

            position = (int)k.End;
        }

        return new Entry(type, bcp47 ?? language, flag, crc);
    }
}
