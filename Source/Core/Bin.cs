using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AudionVoiceTools.Core;

/// <summary>
/// The Recycle Bin and the links of NTFS, by Windows itself. What the library takes off - the sound of an old dictation,
/// a dictation deleted - goes to the Bin (SHFileOperation, as Audion Media Tools does it), quietly: it runs off the
/// window thread and asks nothing. A drive without a Bin - a flash drive, a share - would destroy instead, so nothing is
/// taken off there (<see cref="HasBin"/>).
/// </summary>
public static class Bin
{
    /// <summary>The drive of the path keeps a Recycle Bin: a fixed local disk.</summary>
    public static bool HasBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    const uint FoDelete = 3;
    const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref FileOperation operation);

    /// <summary>The files to the Recycle Bin in one quiet operation of the shell; the reason when it did not go, "" when it did.</summary>
    public static string ToBin(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return "";
        // The list of the shell: every path ends with a zero, the list with one more.
        var from = Marshal.StringToHGlobalUni(string.Join('\0', files) + "\0\0");
        try
        {
            var operation = new FileOperation
            {
                Function = FoDelete, From = from, Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
            };
            int code = SHFileOperationW(ref operation);
            return code == 0 && !operation.Aborted ? "" : "0x" + code.ToString("X");
        }
        finally { Marshal.FreeHGlobal(from); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    /// <summary>
    /// A second name of the file in another folder of the same disk - no byte copied, the file stays where and as it was;
    /// a copy when the disk cannot (another drive, FAT). True - linked, false - copied.
    /// </summary>
    public static bool LinkOrCopy(string existing, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (CreateHardLinkW(target, existing, IntPtr.Zero)) return true;
        File.Copy(existing, target, overwrite: false);
        return false;
    }
}
