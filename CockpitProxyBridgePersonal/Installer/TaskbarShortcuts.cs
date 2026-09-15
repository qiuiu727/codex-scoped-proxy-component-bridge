using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class TaskbarShortcuts
{
    internal const string CodexAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);

    internal static string ReadWindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
        var key = Key(5);
        PropVariant value;
        store.GetValue(ref key, out value);
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : ""; }
        finally { PropVariantClear(ref value); Marshal.FinalReleaseComObject(store); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, out IPropertyStore store);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint change, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }

    private static string PinDirectory
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"); }
    }
    private static string BackupDirectory
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProxyBridgeBackups", "Taskbar"); }
    }

    internal static int Replace(string launcher)
    {
        return ReplaceInDirectory(launcher, PinDirectory, BackupDirectory);
    }

    internal static int ReplaceInDirectory(string launcher, string pinDirectory, string backupDirectory)
    {
        if (!Directory.Exists(pinDirectory)) return 0;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        int count = 0;
        foreach (var path in Directory.GetFiles(pinDirectory, "*.lnk"))
        {
            dynamic old = shell.CreateShortcut(path);
            string target = old.TargetPath;
            bool matching = target.IndexOf(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(Path.GetFileName(target), "CodexProxyLauncher.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, launcher, StringComparison.OrdinalIgnoreCase);
            // Resolve Store links by their explicit AppUserModelID, never by a generic display name.
            if (!matching) matching = IsCodexStoreLink(path);
            Marshal.FinalReleaseComObject(old);
            if (!matching) continue;
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, Path.GetFileName(path));
            if (!File.Exists(backup)) File.Copy(path, backup);
            var temporary = Path.Combine(pinDirectory, Guid.NewGuid().ToString("N") + ".lnk");
            dynamic link = shell.CreateShortcut(temporary);
            link.TargetPath = launcher;
            link.Arguments = "";
            link.WorkingDirectory = Path.GetDirectoryName(launcher);
            link.IconLocation = launcher + ",0";
            link.Description = "Launch Codex through Codex Proxy Bridge";
            link.Save();
            Marshal.FinalReleaseComObject(link);
            SetMetadata(temporary, launcher);
            File.Copy(temporary, path, true);
            File.Delete(temporary);
            dynamic verify = shell.CreateShortcut(path);
            if (!string.Equals((string)verify.TargetPath, launcher, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Taskbar shortcut target verification failed.");
            Marshal.FinalReleaseComObject(verify);
            count++;
        }
        Marshal.FinalReleaseComObject(shell);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return count;
    }

    private static bool IsCodexStoreLink(string path)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
            var key = Key(5);
            PropVariant value;
            store.GetValue(ref key, out value);
            try { return value.Type == 31 && Marshal.PtrToStringUni(value.Value) == "OpenAI.Codex_2p2nqsd0c76g0!App"; }
            finally { PropVariantClear(ref value); Marshal.FinalReleaseComObject(store); }
        }
        catch { return false; }
    }

    private static PropertyKey Key(uint id)
    {
        return new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
    }
    private static void SetMetadata(string path, string launcher)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store);
        try
        {
            Set(store, 5, CodexAppId);
            Set(store, 2, "\"" + launcher + "\"");
            Set(store, 3, launcher + ",0");
            Set(store, 4, "Codex Proxy Bridge");
            store.Commit();
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    private static void Set(IPropertyStore store, uint id, string text)
    {
        var key = Key(id);
        var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(text) };
        try { store.SetValue(ref key, ref value); }
        finally { PropVariantClear(ref value); }
    }

    internal static void ConfigureRunningWindows(string launcher)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                if (p.MainModule.FileName.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0 || p.MainWindowHandle == IntPtr.Zero) continue;
                var iid = typeof(IPropertyStore).GUID;
                IPropertyStore store;
                SHGetPropertyStoreForWindow(p.MainWindowHandle, ref iid, out store);
                try
                {
                    Set(store, 2, "\"" + launcher + "\"");
                    Set(store, 3, launcher + ",0");
                    Set(store, 4, "Codex via Proxy Bridge");
                    Set(store, 5, CodexAppId);
                    store.Commit();
                }
                finally { Marshal.FinalReleaseComObject(store); }
                if (ReadWindowAppId(p.MainWindowHandle) != CodexAppId) throw new IOException("Window grouping identity verification failed.");
            }
            catch (ArgumentException) { }
        }
    }

    internal static void Restore(string launcher)
    {
        if (!Directory.Exists(BackupDirectory)) return;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach (var backup in Directory.GetFiles(BackupDirectory, "*.lnk"))
        {
            var path = Path.Combine(PinDirectory, Path.GetFileName(backup));
            if (!File.Exists(path)) continue;
            dynamic link = shell.CreateShortcut(path);
            bool ours = string.Equals((string)link.TargetPath, launcher, StringComparison.OrdinalIgnoreCase);
            Marshal.FinalReleaseComObject(link);
            if (ours) File.Copy(backup, path, true);
        }
        Marshal.FinalReleaseComObject(shell);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
