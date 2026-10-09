using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kale.Matroska;
using Jellyfin.Plugin.Kale.Repair;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

public class DefaultFlagEditorTests
{
    /// <summary>Charlie and the Chocolate Factory's shape, measured 2026-10-04: English written
    /// as 0, the Mandarin dub with NO FlagDefault (so default), Cantonese written as 0, and a
    /// 1,369-byte Void after Tracks.</summary>
    private static byte[] Charlie() => new MkvBuilder()
        .Video()
        .Audio("eng", 0)
        .Audio("chi", null)
        .Audio("chi", 0)
        .Void(1369)
        .Build();

    private static (MatroskaHead Head, byte[] Bytes) Located(byte[] bytes)
    {
        using var stream = MkvBuilder.Stream(bytes);
        var (head, refusal) = DefaultFlagEditor.Locate(stream);
        Assert.Equal(EditRefusal.None, refusal);
        return (head!, bytes);
    }

    [Fact]
    public void ReadsCharliesFlagsTheWayFfprobeDoes()
    {
        var (head, _) = Located(Charlie());
        var tracks = DefaultFlagEditor.AudioTracks(head);
        Assert.Equal(new[] { false, true, false }, tracks.Select(t => t.IsDefault));
        Assert.Null(tracks[1].FlagDefault); // absent, which Matroska reads as 1
    }

    [Fact]
    public void MakingEnglishDefaultWritesTheDubsFlagIntoThePadding()
    {
        var bytes = Charlie();
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);

        Assert.True(plan.CanApply);
        Assert.Equal(new[] { true, false, false }, plan.After.Select(t => t.IsDefault));
        Assert.Equal(plan.Original.Length, plan.Patched.Length);

        using var stream = MkvBuilder.Stream(bytes);
        Assert.Equal(EditRefusal.None, DefaultFlagEditor.Apply(stream, plan));
        var edited = stream.ToArray();

        Assert.Equal(bytes.Length, edited.Length);
        // Everything outside Tracks + Void is byte-identical — the media cannot have moved.
        long regionEnd = plan.Offset + plan.Original.Length;
        Assert.True(bytes.AsSpan(0, (int)plan.Offset).SequenceEqual(edited.AsSpan(0, (int)plan.Offset)));
        Assert.True(bytes.AsSpan((int)regionEnd).SequenceEqual(edited.AsSpan((int)regionEnd)));

        // And it reads back as planned, Void three bytes shorter.
        var (after, _) = Located(edited);
        Assert.Equal(new[] { true, false, false }, DefaultFlagEditor.AudioTracks(after).Select(t => t.IsDefault));
        Assert.Equal(1366, after.VoidLength);
    }

    [Fact]
    public void WhenEveryFlagIsWrittenItIsAFlipAndNeedsNoPadding()
    {
        var bytes = new MkvBuilder().Video().Audio("eng", 0).Audio("fre", 1).Build(); // no Void at all
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);

        Assert.True(plan.CanApply);
        Assert.Equal(new[] { true, false }, plan.After.Select(t => t.IsDefault));
        int changed = plan.Original.Zip(plan.Patched).Count(p => p.First != p.Second);
        Assert.Equal(2, changed); // two flag bytes, nothing else
    }

    [Fact]
    public void AnInsertWithNoPaddingIsRefused()
    {
        var (head, _) = Located(new MkvBuilder().EntrySizeWidth(1).Video().Audio("eng", 0).Audio("chi", null).Build());
        Assert.Equal(EditRefusal.NoRoom, DefaultFlagEditor.PlanMakeDefault(head, 0).Refusal);
    }

    [Theory]
    [InlineData(3, true)]  // consumed exactly: no Void left
    [InlineData(4, false)] // one byte left over cannot be a Void
    [InlineData(5, true)]  // two bytes left: an empty Void
    public void ThePaddingMustBeUsableExactly(int voidLength, bool canApply)
    {
        // 1-byte size fields: nothing to narrow, so only the Void can supply the room.
        var (head, _) = Located(new MkvBuilder().EntrySizeWidth(1).Video().Audio("eng", 0).Audio("chi", null).Void(voidLength).Build());
        Assert.Equal(canApply, DefaultFlagEditor.PlanMakeDefault(head, 0).CanApply);
    }

    /// <summary>ffmpeg writes a CRC-32 into Tracks by default (found on the sandbox, 2026-10-04):
    /// every file it muxed would otherwise be refused.</summary>
    [Fact]
    public void AValidChecksumOnTracksIsRecomputedAndStillVerifies()
    {
        var bytes = new MkvBuilder().Video().Audio("eng", 0).Audio("fre", 1).CrcOnTracks().Build();
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);
        Assert.True(plan.CanApply);

        using var stream = MkvBuilder.Stream(bytes);
        Assert.Equal(EditRefusal.None, DefaultFlagEditor.Apply(stream, plan));
        var (after, _) = Located(stream.ToArray());
        Assert.Equal(new[] { true, false }, DefaultFlagEditor.AudioTracks(after).Select(t => t.IsDefault));

        // The rewritten checksum covers the rewritten entries.
        var region = after.Region;
        var tracks = Ebml.ReadElement(region, 0);
        var crc = Ebml.ReadElement(region, (int)tracks.DataStart);
        Assert.Equal(MatroskaId.Crc32, crc.Id);
        Assert.True(Crc32.Matches(region.AsSpan((int)crc.DataStart, 4), region.AsSpan((int)crc.End, (int)(tracks.End - crc.End))));
    }

    /// <summary>An ffmpeg-muxed file: the default track has NO FlagDefault (ffmpeg writes the flag
    /// only when it is 0), entry sizes are 8 bytes wide, Tracks has a CRC and nothing follows
    /// Tracks but the next element. The room comes from narrowing the rewritten entry's size.</summary>
    [Fact]
    public void AnFfmpegFileFindsRoomInItsOwnWideSizeFields()
    {
        var bytes = new MkvBuilder().EntrySizeWidth(8).Video().Audio("eng", 0).Audio("fre", null).CrcOnTracks().Build();
        var (head, _) = Located(bytes);
        Assert.Equal(0, head.VoidLength);

        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);
        Assert.True(plan.CanApply, plan.Refusal.ToString());

        using var stream = MkvBuilder.Stream(bytes);
        Assert.Equal(EditRefusal.None, DefaultFlagEditor.Apply(stream, plan));
        var edited = stream.ToArray();
        Assert.Equal(bytes.Length, edited.Length);
        long regionEnd = plan.Offset + plan.Original.Length;
        Assert.True(bytes.AsSpan((int)regionEnd).SequenceEqual(edited.AsSpan((int)regionEnd)));

        var (after, _) = Located(edited);
        Assert.Equal(new[] { true, false }, DefaultFlagEditor.AudioTracks(after).Select(t => t.IsDefault));
        Assert.True(after.VoidLength >= 2); // the freed bytes became padding
    }

    [Fact]
    public void TheSmallestEditIsPreferredWhenThereIsPadding()
    {
        // Wide sizes AND a Void: the Void is used and no size field is touched.
        var bytes = new MkvBuilder().EntrySizeWidth(8).Video().Audio("eng", 0).Audio("fre", null).Void(32).Build();
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);
        Assert.True(plan.CanApply);
        var (after, _) = Located(plan.Patched.Length == 0 ? bytes : Patch(bytes, plan));
        Assert.Equal(29, after.VoidLength);
    }

    private static byte[] Patch(byte[] bytes, EditPlan plan)
    {
        var copy = (byte[])bytes.Clone();
        plan.Patched.CopyTo(copy, plan.Offset);
        return copy;
    }

    [Fact]
    public void AChecksumThatIsAlreadyWrongIsRefusedNotPapered()
    {
        var (head, _) = Located(new MkvBuilder().Video().Audio("eng", 0).Audio("fre", 1).CrcOnTracks(wrong: true).Build());
        Assert.Equal(EditRefusal.ChecksumPresent, DefaultFlagEditor.PlanMakeDefault(head, 0).Refusal);
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(System.Text.Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void ANonMatroskaFileIsRefused()
    {
        using var stream = MkvBuilder.Stream(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0 });
        Assert.Equal(EditRefusal.NotMatroska, DefaultFlagEditor.Locate(stream).Refusal);
    }

    [Fact]
    public void TracksAfterTheMediaIsRefusedRatherThanSearchedFor()
    {
        using var stream = MkvBuilder.Stream(new MkvBuilder().Video().Audio("eng", 0).ClusterBeforeTracks().Build());
        Assert.Equal(EditRefusal.TracksAfterMedia, DefaultFlagEditor.Locate(stream).Refusal);
    }

    [Fact]
    public void AlreadyTheDefaultIsNothingToChange()
    {
        var (head, _) = Located(new MkvBuilder().Video().Audio("eng", 1).Audio("fre", 0).Build());
        Assert.Equal(EditRefusal.NothingToChange, DefaultFlagEditor.PlanMakeDefault(head, 0).Refusal);
    }

    [Fact]
    public void AFileThatChangedSincePlanningIsLeftAlone()
    {
        var bytes = Charlie();
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);

        var moved = (byte[])bytes.Clone();
        moved[(int)plan.Offset + 20] ^= 0xFF; // someone else wrote here in between
        using var stream = MkvBuilder.Stream(moved);

        Assert.Equal(EditRefusal.ChangedUnderneath, DefaultFlagEditor.Apply(stream, plan));
        Assert.True(stream.ToArray().SequenceEqual(moved));
    }

    [Fact]
    public void UndoRestoresWhatEveryPlayerReads()
    {
        var bytes = Charlie();
        var (head, _) = Located(bytes);
        var plan = DefaultFlagEditor.PlanMakeDefault(head, 0);
        using var stream = MkvBuilder.Stream(bytes);
        DefaultFlagEditor.Apply(stream, plan);

        var (edited, _) = Located(stream.ToArray());
        // Undo writes each original EFFECTIVE value (absent → 1).
        var desired = plan.Before
            .Where(t => (t.FlagDefault ?? 1) != (DefaultFlagEditor.AudioTracks(edited)[t.Ordinal].FlagDefault ?? 1))
            .ToDictionary(t => t.Ordinal, t => t.FlagDefault ?? 1);
        var undo = DefaultFlagEditor.Plan(edited, desired);

        Assert.True(undo.CanApply);
        Assert.Equal(plan.Before.Select(t => t.IsDefault), undo.After.Select(t => t.IsDefault));
    }

    [Theory]
    [InlineData("chi", "zh", true)]
    [InlineData("zho", "chi", true)]
    [InlineData("fre", "fra", true)]
    [InlineData("eng", "en-US", true)]
    [InlineData("und", "fre", true)]
    [InlineData("", "jpn", true)]
    [InlineData("eng", "fre", false)]
    public void LanguagesCompareAcrossCodeSystems(string a, string b, bool same) =>
        Assert.Equal(same, Languages.Same(a, b));

    [Fact]
    public void TracksMustLineUpWithJellyfinsStreamsOrTheRequestIsRefused()
    {
        var (head, _) = Located(Charlie());
        var fileTracks = DefaultFlagEditor.AudioTracks(head);
        var jellyfin = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "chi" },
            new() { Index = 3, Type = MediaStreamType.Audio, Language = "chi" },
        };
        Assert.True(DefaultAudioRepair.LinesUp(fileTracks, jellyfin));

        jellyfin[0].Language = "spa";
        Assert.False(DefaultAudioRepair.LinesUp(fileTracks, jellyfin));
        Assert.False(DefaultAudioRepair.LinesUp(fileTracks, jellyfin.Take(2).ToList()));
    }

    [Fact]
    public void LinkCountReadsThisPlatform()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            Assert.Equal(1, LinkCount.Of(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
