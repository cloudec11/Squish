using System.Runtime.InteropServices;
using System.Text;

namespace Squish.App;

/// <summary>Small Win32 helpers: file-drop clipboard, taskbar progress, keep-awake, Explorer select.</summary>
internal static partial class Native
{
    // ---------------- Clipboard: CF_HDROP, which Discord reads on Ctrl+V.

    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002, GMEM_ZEROINIT = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int ptX, ptY;
        public int fNC;
        public int fWide;
    }

    [LibraryImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint hWnd);
    [LibraryImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint uFormat, nint hMem);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint h);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint h);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalFree(nint h);

    public static bool CopyFilesToClipboard(nint hwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        // DROPFILES header, then double-null-terminated UTF-16 list.
        var list = string.Join('\0', paths) + "\0\0";
        byte[] chars = Encoding.Unicode.GetBytes(list);
        int header = Marshal.SizeOf<DROPFILES>();

        nint hDrop = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)(header + chars.Length));
        if (hDrop == 0) return false;
        nint p = GlobalLock(hDrop);
        Marshal.StructureToPtr(new DROPFILES { pFiles = (uint)header, fWide = 1 }, p, false);
        Marshal.Copy(chars, 0, p + header, chars.Length);
        GlobalUnlock(hDrop);

        // "Preferred DropEffect" = copy, so apps that honour it don't treat the paste as a move.
        nint hEffect = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 4);
        nint pe = GlobalLock(hEffect);
        Marshal.WriteInt32(pe, 1); // DROPEFFECT_COPY
        GlobalUnlock(hEffect);

        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(hwnd))
            {
                try
                {
                    EmptyClipboard();
                    bool ok = SetClipboardData(CF_HDROP, hDrop) != 0;
                    if (!ok) GlobalFree(hDrop);
                    if (SetClipboardData(RegisterClipboardFormatW("Preferred DropEffect"), hEffect) == 0) GlobalFree(hEffect);
                    return ok;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(30); // another app holds the clipboard
        }
        GlobalFree(hDrop);
        GlobalFree(hEffect);
        return false;
    }

    // ---------------- Keep awake while jobs run.

    [LibraryImport("kernel32.dll")] private static partial uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    // ---------------- Explorer: open the folder with the file selected.

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint psfgaoOut);
    [LibraryImport("shell32.dll")] private static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint apidl, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoTaskMemFree(nint pv);

    public static void ShowInFolder(string path)
    {
        if (SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 && pidl != 0)
        {
            try { SHOpenFolderAndSelectItems(pidl, 0, 0, 0); }
            finally { CoTaskMemFree(pidl); }
        }
        else
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            { ArgumentList = { Path.GetDirectoryName(path) ?? path }, UseShellExecute = false });
        }
    }

    // ---------------- Command line parsing for redirected activations.

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string cmdLine, out int argc);
    [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint h);

    public static string[] SplitCommandLine(string cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return [];
        nint argv = CommandLineToArgvW(cmdLine, out int argc);
        if (argv == 0) return [];
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(argv); }
    }

    // ---------------- Window focus

    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool AllowSetForegroundWindow(int processId);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
}

