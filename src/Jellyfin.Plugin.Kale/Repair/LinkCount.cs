using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Kale.Repair;

/// <summary>
/// How many directory entries point at a file. More than one means a hard link — on a
/// media server, usually a torrent client's copy — and an in-place edit would change
/// that copy too and break its piece hashes. .NET has no API for this, so it is asked of
/// the OS directly. Null means the OS could not say, which callers treat as a refusal.
/// </summary>
public static partial class LinkCount
{
    public static long? Of(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return Linux(path);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacOS(path);
            }

            if (OperatingSystem.IsWindows())
            {
                return Windows(path);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // statx's layout is the same on every architecture, unlike stat's.
    private const int AtFdCwd = -100;
    private const uint StatxNlink = 0x4;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatX(int dirfd, string path, int flags, uint mask, byte* buffer);

    private static unsafe long? Linux(string path)
    {
        byte* buffer = stackalloc byte[256];
        if (StatX(AtFdCwd, path, 0, StatxNlink, buffer) != 0)
        {
            return null;
        }

        uint returnedMask = *(uint*)buffer;
        if ((returnedMask & StatxNlink) == 0)
        {
            return null;
        }

        return *(uint*)(buffer + 16); // stx_nlink
    }

    // Darwin 64-bit-inode stat: dev_t (4) mode_t (2) nlink_t (2).
    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int DarwinStat(string path, byte* buffer);

    private static unsafe long? MacOS(string path)
    {
        byte* buffer = stackalloc byte[512];
        if (DarwinStat(path, buffer) != 0)
        {
            return null;
        }

        return *(ushort*)(buffer + 6);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    private static long? Windows(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : null;
    }
}
