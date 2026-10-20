using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AudionVoiceTools.Core;

/// <summary>
/// «Скачать»: a copy into the folder Downloads of Windows - one press, no question of where (the owner, 19.10.2026). A name
/// already there gets « (2)», « (3)» before its extension: nothing of the person is written over.
/// </summary>
public static class Downloads
{
    static Guid _downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The folder Downloads where Windows keeps it (it may be moved to another disk); the one of the profile else.</summary>
    public static string Folder()
    {
        try
        {
            if (SHGetKnownFolderPath(ref _downloads, 0, IntPtr.Zero, out var path) == 0)
            {
                try { return Marshal.PtrToStringUni(path) ?? ""; }
                finally { Marshal.FreeCoTaskMem(path); }
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>The file copied into Downloads under <paramref name="name"/> or its own name; the path of the copy.</summary>
    public static string Copy(string file, string? name = null)
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        name ??= Path.GetFileName(file);
        var target = Path.Combine(folder, name);
        for (int n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        File.Copy(file, target);
        return target;
    }
}
