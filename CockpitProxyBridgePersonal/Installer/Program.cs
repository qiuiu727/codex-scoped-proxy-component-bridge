using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class InstallerProgram
{
    private const string ProductName = "Codex Proxy Bridge";
    private const string ProductVersion = "1.2.7";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexProxyBridge";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexProxyBridgeWatcher";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (HasArgument(args, "--self-test"))
        {
            return RunSelfTest();
        }
        if (HasArgument(args, "--uninstall-worker"))
        {
            return RunUninstallWorker(args);
        }
        if (HasArgument(args, "/uninstall") || HasArgument(args, "--uninstall"))
        {
            return BeginUninstall();
        }
        return Install(HasArgument(args, "--upgrade-preserve"));
    }

    private static int RunSelfTest()
    {
        try
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexBridgeLauncher.exe"))
            {
                if (input == null || input.Length < 1024 || input.ReadByte() != 0x4D || input.ReadByte() != 0x5A)
                {
                    return 2;
                }
            }
            return 0;
        }
        catch
        {
            return 3;
        }
    }

    private static int Install(bool unattended)
    {
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);
        var existingSettingsPath = Path.Combine(installDirectory, "launcher-settings.json");
        var existingChinese = File.Exists(existingSettingsPath) && File.ReadAllText(existingSettingsPath).IndexOf("zh-CN", StringComparison.OrdinalIgnoreCase) >= 0;
        var languageChoice = unattended ? (existingChinese ? DialogResult.Yes : DialogResult.No) : MessageBox.Show(
            "请选择安装语言。\n\n是：简体中文\n否：English\n取消：退出安装\n\nSelect the setup language.\n\nYes: Simplified Chinese\nNo: English\nCancel: Exit setup",
            "Language / 安装语言",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (languageChoice == DialogResult.Cancel)
        {
            return 1;
        }
        var chinese = languageChoice == DialogResult.Yes;
        var existingDesktopShortcuts = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "启动 Codex.exe")) || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Start Codex.exe"));
        var createDesktopShortcuts = unattended ? existingDesktopShortcuts : MessageBox.Show(
            chinese
                ? "是否在桌面创建“启动 Codex”和“重启 Codex”快捷程序？\n\n无论选择是或否，开始菜单的“Codex Proxy Bridge”文件夹中都会保留这两个程序。"
                : "Create 'Start Codex' and 'Restart Codex' launchers on the Desktop?\n\nBoth entries are always installed in the 'Codex Proxy Bridge' Start menu folder.",
            chinese ? "桌面快捷方式" : "Desktop shortcuts",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        var existingTaskbarChoice = File.Exists(Path.Combine(installDirectory, "taskbar-grouping.enabled"));
        var replaceTaskbar = existingTaskbarChoice || (!unattended && MessageBox.Show(
            chinese ? "是否将任务栏中已有的 Codex 图标替换为代理启动器？\n\n是：备份原快捷方式，再让该入口通过代理桥启动。\n否：保留任务栏原样。\n\nWindows 若仍显示缓存入口，安装后需要取消固定旧图标，再固定启动器。"
                : "Replace existing pinned Codex shortcuts with the proxy launcher?\n\nYes: back up the old shortcuts and route them through the bridge.\nNo: leave the taskbar unchanged.\n\nIf Windows retains a cached pin, unpin it and pin the launcher after setup.",
            chinese ? "任务栏启动入口" : "Taskbar launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes);
        var previousInstallDirectory = ReadPreviousInstallLocation();
        bool existingDirectHook = File.Exists(Path.Combine(installDirectory, "cockpit-direct-hook.enabled"));
        bool directHook = existingDirectHook || (!unattended && File.Exists(CockpitHook.ConfigPath) && MessageBox.Show(
            chinese ? "是否让 Cockpit 切号后直接通过代理桥启动 Codex？\n\n将关闭切号后的 Store 启动和旧监视器，启用指定应用联动。不会修改聊天同步设置。安装后需退出并重新打开 Cockpit。单独的重启实例按钮不属于此入口。"
                : "Use the proxy bridge directly after Cockpit account switches? This disables Store auto-launch and the old watcher. Restart Cockpit after setup. The separate instance restart button is not covered.",
            "Cockpit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

        try
        {
            Directory.CreateDirectory(installDirectory);
            var launcherPath = Path.Combine(installDirectory, "CodexBridgeLauncher.exe");
            StopExactLauncher(launcherPath);
            StopExactLauncher(Path.Combine(installDirectory, "CodexCockpitHook.exe"));
            ExtractResource("CodexBridgeLauncher.exe", launcherPath);
            if (directHook) CockpitHook.Enable(installDirectory);
            else if (File.Exists(Path.Combine(installDirectory, "cockpit-direct-hook.enabled")))
                File.Copy(launcherPath, Path.Combine(installDirectory, "CodexCockpitHook.exe"), true);

            var configPath = Path.Combine(installDirectory, "config.json");
            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, "{\r\n  \"proxyUrl\": \"http://127.0.0.1:7890\"\r\n}\r\n", new UTF8Encoding(false));
            }
            var settingsPath = Path.Combine(installDirectory, "launcher-settings.json");
            if (!File.Exists(settingsPath))
                File.WriteAllText(settingsPath, string.Format("{{\r\n  \"language\": \"{0}\",\r\n  \"restartCodexOnLaunch\": false,\r\n  \"autoStartEnabled\": true,\r\n  \"taskbarOverrideEnabled\": false\r\n}}\r\n", chinese ? "zh-CN" : "en-US"), new UTF8Encoding(false));

            var uninstallerPath = Path.Combine(installDirectory, "Uninstall.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstallerPath, true);
            CreateDesktopExecutables(launcherPath, createDesktopShortcuts, chinese);
            CreateShortcuts(launcherPath, installDirectory, chinese);
            RegisterStartupWatcher(launcherPath);
            RegisterUninstaller(installDirectory, launcherPath, uninstallerPath);
            if (replaceTaskbar)
            {
                int replaced = TaskbarShortcuts.Replace(launcherPath);
                File.WriteAllText(Path.Combine(installDirectory, "taskbar-grouping.enabled"), "1");
                TaskbarShortcuts.ConfigureRunningWindows(launcherPath);
                WriteInstallLog(installDirectory, "Taskbar replacement requested; verified shortcut files=" + replaced);
                if (replaced == 0)
                    MessageBox.Show(chinese ? "没有发现可替换的 Codex 任务栏快捷方式。请在开始菜单找到“启动 Codex”，右键固定到任务栏。" : "No replaceable Codex taskbar shortcut was found. Pin Start Codex from the Start menu to the taskbar.");
            }
            WriteInstallLog(installDirectory, "Installed version " + ProductVersion + "; desktopShortcuts=" + createDesktopShortcuts);
            RemovePreviousDesktopFolder(previousInstallDirectory, installDirectory);
            WatcherTask.Ensure(launcherPath);

            if (!unattended) MessageBox.Show(
                chinese
                    ? "安装完成。“启动 Codex”和“重启 Codex”已放入开始菜单的“Codex Proxy Bridge”文件夹。桌面内容按你的选择创建。程序也已注册到 Windows“已安装的应用”。"
                    : "Installation completed. 'Start Codex' and 'Restart Codex' are in the 'Codex Proxy Bridge' Start menu folder. Desktop launchers follow your selection. The app is also registered in Windows Installed apps.",
                chinese ? "安装完成" : "Setup completed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            try { WriteInstallLog(installDirectory, "FAILED " + error); } catch { }
            MessageBox.Show(error.Message, chinese ? "安装失败" : "Setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string ReadPreviousInstallLocation()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false))
        {
            return key == null ? string.Empty : Convert.ToString(key.GetValue("InstallLocation"));
        }
    }

    private static void CreateDesktopExecutables(string launcherPath, bool createShortcuts, bool chinese)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        DeleteDesktopExecutable("启动 Codex.exe");
        DeleteDesktopExecutable("重启 Codex.exe");
        DeleteDesktopExecutable("Start Codex.exe");
        DeleteDesktopExecutable("Restart Codex.exe");
        if (createShortcuts)
        {
            File.Copy(launcherPath, Path.Combine(desktop, chinese ? "启动 Codex.exe" : "Start Codex.exe"), true);
            File.Copy(launcherPath, Path.Combine(desktop, chinese ? "重启 Codex.exe" : "Restart Codex.exe"), true);
        }
    }

    private static void RemovePreviousDesktopFolder(string previousInstallDirectory, string newInstallDirectory)
    {
        if (string.IsNullOrWhiteSpace(previousInstallDirectory)
            || string.Equals(previousInstallDirectory, newInstallDirectory, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(previousInstallDirectory))
        {
            return;
        }
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName);
        if (string.Equals(Path.GetFullPath(previousInstallDirectory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(previousInstallDirectory, true);
        }
    }

    private static void WriteInstallLog(string installDirectory, string message)
    {
        File.AppendAllText(Path.Combine(installDirectory, "install.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8);
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (input == null)
            {
                throw new InvalidOperationException("Embedded launcher resource was not found.");
            }
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }
        }
    }

    private static void CreateShortcuts(string launcherPath, string installDirectory, bool chinese)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        DeleteShortcut("Codex Proxy Bridge.lnk");
        DeleteShortcut("Restart Codex through Proxy.lnk");
        var group = Path.Combine(programs, ProductName);
        Directory.CreateDirectory(group);
        CreateShortcut(Path.Combine(group, chinese ? "启动 Codex.lnk" : "Start Codex.lnk"), launcherPath, string.Empty, installDirectory,
            chinese ? "通过本地代理打开 Codex" : "Open Codex through the local proxy");
        CreateShortcut(Path.Combine(group, chinese ? "重启 Codex.lnk" : "Restart Codex.lnk"), launcherPath, "--restart-codex", installDirectory,
            chinese ? "关闭并通过本地代理重新启动 Codex" : "Close and restart Codex through the local proxy");
        CreateShortcut(Path.Combine(group, chinese ? "代理桥托盘.lnk" : "Proxy Bridge Tray.lnk"), launcherPath, "--tray", installDirectory,
            chinese ? "打开 Codex 代理桥托盘设置" : "Open Codex Proxy Bridge tray settings");
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Description = description;
        shortcut.Save();
    }

    private static void RegisterStartupWatcher(string launcherPath)
    {
        WatcherTask.Ensure(launcherPath);
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    private static void RegisterUninstaller(string installDirectory, string launcherPath, string uninstallerPath)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
        {
            key.SetValue("DisplayName", ProductName);
            key.SetValue("DisplayVersion", ProductVersion);
            key.SetValue("Publisher", "qiuiu727");
            key.SetValue("InstallLocation", installDirectory);
            key.SetValue("DisplayIcon", launcherPath + ",0");
            key.SetValue("UninstallString", "\"" + uninstallerPath + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + uninstallerPath + "\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static int BeginUninstall()
    {
        var installDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var settingsPath = Path.Combine(installDirectory, "launcher-settings.json");
        var chinese = File.Exists(settingsPath) && File.ReadAllText(settingsPath).IndexOf("zh-CN", StringComparison.OrdinalIgnoreCase) >= 0;
        var answer = MessageBox.Show(
            chinese ? "确定要卸载 Codex Proxy Bridge 吗？\n\n卸载不会关闭 Codex 或 Cockpit。" : "Uninstall Codex Proxy Bridge?\n\nUninstall will not close Codex or Cockpit.",
            chinese ? "卸载" : "Uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return 1;
        }
        var workerPath = Path.Combine(Path.GetTempPath(), "CodexProxyBridgeUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, workerPath, true);
        Process.Start(new ProcessStartInfo(workerPath, "--uninstall-worker \"" + installDirectory + "\" " + Process.GetCurrentProcess().Id)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return 0;
    }

    private static int RunUninstallWorker(string[] args)
    {
        try
        {
            var index = Array.FindIndex(args, value => string.Equals(value, "--uninstall-worker", StringComparison.OrdinalIgnoreCase));
            var installDirectory = args[index + 1];
            int parentId;
            if (int.TryParse(args[index + 2], out parentId))
            {
                try { Process.GetProcessById(parentId).WaitForExit(10000); } catch { }
            }
            StopExactLauncher(Path.Combine(installDirectory, "CodexBridgeLauncher.exe"));
            StopExactLauncher(Path.Combine(installDirectory, "CodexCockpitHook.exe"));
            CockpitHook.Restore(installDirectory);
            WatcherTask.Remove();
            TaskbarShortcuts.Restore(Path.Combine(installDirectory, "CodexBridgeLauncher.exe"));
            using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                runKey.DeleteValue(RunValueName, false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            DeleteShortcut("Codex Proxy Bridge.lnk");
            DeleteShortcut("Restart Codex through Proxy.lnk");
            DeleteStartMenuGroup();
            DeleteDesktopExecutable("启动 Codex.exe");
            DeleteDesktopExecutable("重启 Codex.exe");
            DeleteDesktopExecutable("Start Codex.exe");
            DeleteDesktopExecutable("Restart Codex.exe");
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, true);
            }
            MoveFileEx(Assembly.GetExecutingAssembly().Location, null, 4);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void DeleteDesktopExecutable(string name)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteShortcut(string name)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteStartMenuGroup()
    {
        var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
        if (Directory.Exists(group))
        {
            Directory.Delete(group, true);
        }
    }

    private static void StopExactLauncher(string launcherPath)
    {
        foreach (var process in Process.GetProcessesByName("CodexBridgeLauncher"))
        {
            try
            {
                if (process.Id != Process.GetCurrentProcess().Id
                    && string.Equals(process.MainModule.FileName, launcherPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }
    }

    private static bool HasArgument(string[] args, string expected)
    {
        return Array.Exists(args, value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class InstallPathForm : Form
{
    private readonly TextBox pathBox;

    public string InstallPath { get { return pathBox.Text; } }

    public InstallPathForm(bool chinese, string defaultPath)
    {
        Text = chinese ? "选择安装位置" : "Choose install location";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 125);

        var label = new Label { Left = 15, Top = 15, Width = 525, Text = chinese ? "安装文件夹（留空则使用桌面）：" : "Install folder (blank uses Desktop):" };
        pathBox = new TextBox { Left = 15, Top = 42, Width = 445, Text = defaultPath };
        var browse = new Button { Left = 470, Top = 40, Width = 75, Text = chinese ? "浏览" : "Browse" };
        browse.Click += delegate
        {
            using (var dialog = new FolderBrowserDialog { SelectedPath = string.IsNullOrWhiteSpace(pathBox.Text) ? defaultPath : pathBox.Text })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    pathBox.Text = Path.Combine(dialog.SelectedPath, "Codex Proxy Bridge");
                }
            }
        };
        var install = new Button { Left = 380, Top = 82, Width = 80, Text = chinese ? "安装" : "Install", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 470, Top = 82, Width = 75, Text = chinese ? "取消" : "Cancel", DialogResult = DialogResult.Cancel };
        AcceptButton = install;
        CancelButton = cancel;
        Controls.Add(label);
        Controls.Add(pathBox);
        Controls.Add(browse);
        Controls.Add(install);
        Controls.Add(cancel);
    }
}
