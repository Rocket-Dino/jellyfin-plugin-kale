using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Kale.Matroska;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kale.Repair;

/// <summary>One audio track, numbered the way Jellyfin numbers it.</summary>
public sealed record RepairTrack(int AudioStreamIndex, string Language, string? Title, bool IsDefault);

/// <summary>What happened, or would happen. <see cref="Outcome"/> is stable for the app to branch on;
/// <see cref="Message"/> is the sentence to show.</summary>
public sealed record RepairResult(
    string Outcome,
    string Message,
    bool CanApply,
    bool Applied,
    IReadOnlyList<RepairTrack> Before,
    IReadOnlyList<RepairTrack> After,
    bool UndoAvailable);

/// <summary>
/// Makes one audio track a file's default, in place (ADR-019 amendment). Every refusal is
/// decided here on the server, so no client can talk the plugin past one.
/// </summary>
public class DefaultAudioRepair
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<DefaultAudioRepair> _logger;

    public DefaultAudioRepair(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<DefaultAudioRepair> logger)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public RepairResult Check(Guid itemId, int audioStreamIndex) => Run(itemId, audioStreamIndex, apply: false);

    public RepairResult Apply(Guid itemId, int audioStreamIndex) => Run(itemId, audioStreamIndex, apply: true);

    public RepairResult Undo(Guid itemId)
    {
        var record = UndoStore.Read(itemId);
        if (record is null)
        {
            return Fail("noUndo", "There's nothing to undo for this file.");
        }

        if (_library.GetItemById(itemId) is not Video video || video.Path != record.Path)
        {
            return Fail("notFound", "kale couldn't find that item on this server.");
        }

        return WithFile(video, (stream, head, streams) =>
        {
            var now = DefaultFlagEditor.AudioTracks(head);
            var desired = new Dictionary<int, int>();
            foreach (var original in record.Before)
            {
                int effective = original.FlagDefault ?? 1;
                var current = now.FirstOrDefault(t => t.Ordinal == original.Ordinal);
                if (current is not null && (current.FlagDefault ?? 1) != effective)
                {
                    desired[original.Ordinal] = effective;
                }
            }

            if (desired.Count == 0)
            {
                UndoStore.Delete(itemId);
                return Result("nothingToChange", "This file is already back the way it was.", false, false, now, now, streams, undo: false);
            }

            var plan = DefaultFlagEditor.Plan(head, desired);
            if (!plan.CanApply)
            {
                return Refused(plan.Refusal, plan.Before, streams, undo: true);
            }

            var outcome = DefaultFlagEditor.Apply(stream, plan);
            if (outcome != EditRefusal.None)
            {
                return Refused(outcome, plan.Before, streams, undo: true);
            }

            UndoStore.Delete(itemId);
            Refresh(video);
            _logger.LogInformation("Kale: restored the original default soundtrack flags on {Path}", video.Path);
            return Result("undone", "Put back. The file's soundtracks are marked as they were.", false, true, plan.Before, plan.After, streams, undo: false);
        });
    }

    private RepairResult Run(Guid itemId, int audioStreamIndex, bool apply)
    {
        if (_library.GetItemById(itemId) is not Video video || string.IsNullOrEmpty(video.Path))
        {
            return Fail("notFound", "kale couldn't find that item on this server.");
        }

        return WithFile(video, (stream, head, streams) =>
        {
            int ordinal = streams.FindIndex(s => s.Index == audioStreamIndex);
            if (ordinal < 0)
            {
                return Refused(EditRefusal.TrackNotFound, DefaultFlagEditor.AudioTracks(head), streams, UndoStore.Read(itemId) is not null);
            }

            var plan = DefaultFlagEditor.PlanMakeDefault(head, ordinal);
            bool undoAvailable = UndoStore.Read(itemId) is not null;
            if (!plan.CanApply)
            {
                return Refused(plan.Refusal, plan.Before, streams, undoAvailable);
            }

            string language = Describe(streams[ordinal]);
            if (!apply)
            {
                return Result("ready", $"kale can make {language} this file's default soundtrack. It changes a few bytes of the file's track list and nothing else, and it can be undone.", true, false, plan.Before, plan.After, streams, undoAvailable);
            }

            UndoStore.Write(itemId, new UndoRecord(video.Path, DateTimeOffset.UtcNow, plan.Before.ToList()));
            var outcome = DefaultFlagEditor.Apply(stream, plan);
            if (outcome != EditRefusal.None)
            {
                if (!undoAvailable)
                {
                    UndoStore.Delete(itemId);
                }

                return Refused(outcome, plan.Before, streams, undoAvailable);
            }

            Refresh(video);
            _logger.LogInformation("Kale: made audio stream {Index} the default on {Path}", audioStreamIndex, video.Path);
            return Result("applied", $"Done. {language} is now this file's default soundtrack, in every app.", false, true, plan.Before, plan.After, streams, undo: true);
        });
    }

    private delegate RepairResult FileWork(FileStream stream, MatroskaHead head, List<MediaStream> streams);

    /// <summary>The checks that come before any byte is read, in the order a person would want to hear them.</summary>
    private RepairResult WithFile(Video video, FileWork work)
    {
        string path = video.Path;
        if (!File.Exists(path))
        {
            return Fail("fileMissing", "Jellyfin lists this item, but its file isn't where Jellyfin says it is.");
        }

        var streams = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).ToList();

        long? links = LinkCount.Of(path);
        if (links is null)
        {
            return Fail("linkCountUnknown", "kale couldn't check whether this file is linked to another copy on disk, so it left it alone.");
        }

        if (links > 1)
        {
            return Fail("hardlinked", "This file has another copy linked to it on disk, usually a download client that's still seeding it. Changing it here would change that copy too, so kale won't.");
        }

        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(TimeSpan.FromSeconds(10)))
        {
            return Fail("busy", "kale is already working on this file. Try again in a moment.");
        }

        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return Fail("readOnly", "Jellyfin can read this file but not change it. That's a sensible setup; to let kale fix it, give Jellyfin write access to this folder.");
            }

            using (stream)
            {
                var (head, refusal) = DefaultFlagEditor.Locate(stream);
                if (head is null)
                {
                    return Refused(refusal, Array.Empty<AudioTrackFlags>(), streams, UndoStore.Read(video.Id) is not null);
                }

                var fileTracks = DefaultFlagEditor.AudioTracks(head);
                if (!LinesUp(fileTracks, streams))
                {
                    return Refused(EditRefusal.TrackNotFound, fileTracks, streams, UndoStore.Read(video.Id) is not null);
                }

                return work(stream, head, streams);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Jellyfin's audio streams and the file's audio tracks must agree in count and language, or
    /// the plugin cannot know which track a request means — and it does not guess.</summary>
    internal static bool LinesUp(IReadOnlyList<AudioTrackFlags> fileTracks, IReadOnlyList<MediaStream> streams)
    {
        if (fileTracks.Count != streams.Count)
        {
            return false;
        }

        for (int i = 0; i < streams.Count; i++)
        {
            if (!Languages.Same(fileTracks[i].Language, streams[i].Language))
            {
                return false;
            }
        }

        return true;
    }

    private void Refresh(Video video)
    {
        // A full refresh re-probes the file, which is what picks up the new default flag.
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
        };
        _providers.QueueRefresh(video.Id, options, RefreshPriority.High);
    }

    private static string Describe(MediaStream stream) =>
        Languages.DisplayName(stream.Language) ?? stream.DisplayTitle ?? "That soundtrack";

    private static RepairResult Fail(string outcome, string message) =>
        new(outcome, message, false, false, Array.Empty<RepairTrack>(), Array.Empty<RepairTrack>(), false);

    private static RepairResult Refused(EditRefusal refusal, IReadOnlyList<AudioTrackFlags> before, List<MediaStream> streams, bool undo)
    {
        var (outcome, message) = refusal switch
        {
            EditRefusal.NotMatroska => ("notMatroska", "kale can only fix this in MKV files, and this one isn't."),
            EditRefusal.TracksNotFound or EditRefusal.TracksAfterMedia => ("tracksNotFound", "kale couldn't find this file's track list where it expected it, so it left the file alone."),
            EditRefusal.ChecksumPresent => ("checksum", "This file's track list fails its own checksum, which suggests it's already damaged, so kale left it alone."),
            EditRefusal.NoRoom => ("noRoom", "This file has no spare room for the change, so it would need rewriting. That's a job for a repair tool like Unmanic."),
            EditRefusal.TrackNotFound => ("tracksDisagree", "The file's soundtracks don't line up with what Jellyfin lists, so kale won't guess which one to change. Refreshing this item's metadata in Jellyfin may fix that."),
            EditRefusal.NothingToChange => ("nothingToChange", "That soundtrack is already the default."),
            EditRefusal.ChangedUnderneath => ("changed", "The file changed while kale was working on it, so nothing was written. Try again."),
            EditRefusal.VerificationFailed => ("verificationFailed", "The change didn't read back correctly, so kale put the file back exactly as it was."),
            _ => ("refused", "kale left this file alone."),
        };
        var tracks = Map(before, streams);
        return new RepairResult(outcome, message, false, false, tracks, tracks, undo);
    }

    private static RepairResult Result(string outcome, string message, bool canApply, bool applied, IReadOnlyList<AudioTrackFlags> before, IReadOnlyList<AudioTrackFlags> after, List<MediaStream> streams, bool undo) =>
        new(outcome, message, canApply, applied, Map(before, streams), Map(after, streams), undo);

    private static IReadOnlyList<RepairTrack> Map(IReadOnlyList<AudioTrackFlags> tracks, List<MediaStream> streams) =>
        tracks.Select(t => t.Ordinal < streams.Count
                ? new RepairTrack(streams[t.Ordinal].Index, streams[t.Ordinal].Language ?? t.Language, streams[t.Ordinal].Title, t.IsDefault)
                : new RepairTrack(-1, t.Language, null, t.IsDefault))
            .ToList();
}

/// <summary>The flags as they were before Kale's change, kept so Undo is the same edit backwards.</summary>
public sealed record UndoRecord(string Path, DateTimeOffset ChangedAt, List<AudioTrackFlags> Before);

internal static class UndoStore
{
    private static string Folder =>
        Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "undo");

    private static string FileFor(Guid itemId) => Path.Combine(Folder, $"{itemId:N}.json");

    public static UndoRecord? Read(Guid itemId)
    {
        try
        {
            var file = FileFor(itemId);
            return File.Exists(file) ? JsonSerializer.Deserialize<UndoRecord>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    public static void Write(Guid itemId, UndoRecord record)
    {
        // Never overwrite the first record: Undo goes back to how the file was before Kale
        // ever touched it, not to the previous Kale edit.
        if (Read(itemId) is not null)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileFor(itemId), JsonSerializer.Serialize(record));
    }

    public static void Delete(Guid itemId)
    {
        var file = FileFor(itemId);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}
