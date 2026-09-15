using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;

internal sealed class TrayHost : ApplicationContext
{
    private readonly string baseDirectory;
    private readonly NotifyIcon icon;
    private readonly Timer updateTimer;
    private readonly System.Threading.Mutex mutex;
    internal bool IsPrimary { get; private set; }
    internal TrayHost(string baseDirectory)
    {
        this.baseDirectory = baseDirectory;
        bool created;
        mutex = new System.Threading.Mutex(true, @"Local\ScopedProxyLauncherTray", out created);
        IsPrimary = created;
        if (!created) return;
        var config = Program.LoadConfig(baseDirectory);
        bool chinese = string.Equals(config.Language, "zh-CN", StringComparison.OrdinalIgnoreCase);
        var menu = new ContextMenuStrip();
        menu.Items.Add(chinese ? "打开设置" : "Open settings", null, delegate { OpenSettings(); });
        menu.Items.Add(chinese ? "启动已配置程序" : "Start configured apps", null, delegate { StartApps(); });
        menu.Items.Add(chinese ? "检测程序更新并批准" : "Check app updates / approve", null, delegate { CheckUpdates(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(chinese ? "退出托盘程序" : "Exit tray helper", null, delegate { ExitThread(); });
        icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = chinese ? "代理启动器" : "Scoped Proxy Launcher", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenSettings(); };
        updateTimer = new Timer { Interval = 10 * 60 * 1000, Enabled = true };
        updateTimer.Tick += delegate { CheckUpdatesSilent(); };
        CheckUpdatesSilent();
    }
    private void OpenSettings()
    {
        var config = Program.LoadConfig(baseDirectory);
        using (var form = new ConfigureForm(config, baseDirectory))
        {
            if (form.ShowDialog() == DialogResult.OK)
            {
                Program.SaveConfig(baseDirectory, form.Config);
                AutostartTask.Sync(baseDirectory, form.Config);
                TaskbarPin.Apply(baseDirectory, form.Config);
            }
        }
    }
    private void StartApps()
    {
        try { var config = Program.LoadConfig(baseDirectory); foreach (var target in config.Targets) Program.Launch(config, target); }
        catch (Exception error) { MessageBox.Show(error.Message, "Scoped Proxy Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void CheckUpdates() { try { Program.CheckForUpdatedTargets(Program.LoadConfig(baseDirectory), baseDirectory, true); } catch (Exception error) { MessageBox.Show(error.Message); } }
    private void CheckUpdatesSilent() { try { Program.CheckForUpdatedTargets(Program.LoadConfig(baseDirectory), baseDirectory, false); } catch { } }
    protected override void ExitThreadCore() { if (updateTimer != null) updateTimer.Dispose(); if (icon != null) { icon.Visible = false; icon.Dispose(); } if (mutex != null) { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); } base.ExitThreadCore(); }
}

internal static class AutostartTask
{
    private const string Name = "ScopedProxyLauncherAutostart";
    internal static void Sync(string launcherDirectory, LauncherConfig config)
    {
        if (!config.AutoStartEnabled) { Remove(); return; }
        string launcher = Path.Combine(launcherDirectory, "ScopedProxyLauncher.exe");
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic folder = service.GetFolder("\\"); dynamic task = service.NewTask(0);
        string user = WindowsIdentity.GetCurrent().Name; task.RegistrationInfo.Description = "Start Clash, wait for local HTTP proxy, then launch approved scoped applications."; task.Principal.UserId = user; task.Principal.LogonType = 3; task.Principal.RunLevel = 0; task.Settings.Enabled = true; task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false; task.Settings.MultipleInstances = 2;
        dynamic trigger = task.Triggers.Create(9); trigger.UserId = user; dynamic action = task.Actions.Create(0); action.Path = launcher; action.Arguments = "--autostart"; action.WorkingDirectory = launcherDirectory; folder.RegisterTaskDefinition(Name, task, 6, user, null, 3);
    }
    internal static void Remove() { try { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); service.GetFolder("\\").DeleteTask(Name, 0); } catch { } }
}

internal static class TaskbarPin
{
    internal static void Apply(string launcherDirectory, LauncherConfig config)
    {
        string pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        if (!Directory.Exists(pins)) return;
        string backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopedProxyLauncherBackups", "Taskbar");
        Directory.CreateDirectory(backup); string launcher = Path.Combine(launcherDirectory, "ScopedProxyLauncher.exe");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach (string linkPath in Directory.GetFiles(pins, "*.lnk"))
        {
            dynamic link = shell.CreateShortcut(linkPath); string target = Convert.ToString(link.TargetPath) ?? string.Empty;
            bool ours = string.Equals(target, launcher, StringComparison.OrdinalIgnoreCase);
            bool chatgpt = target.IndexOf("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
            if (config.TaskbarOverrideEnabled && chatgpt)
            {
                string saved = Path.Combine(backup, Path.GetFileName(linkPath)); if (!File.Exists(saved)) File.Copy(linkPath, saved);
                link.TargetPath = launcher; link.Arguments = "--target \"Codex Desktop (Microsoft Store)\""; link.WorkingDirectory = launcherDirectory; link.IconLocation = launcher + ",0"; link.Save();
            }
            else if (!config.TaskbarOverrideEnabled && ours)
            {
                string saved = Path.Combine(backup, Path.GetFileName(linkPath)); if (File.Exists(saved)) File.Copy(saved, linkPath, true);
            }
        }
    }
}
