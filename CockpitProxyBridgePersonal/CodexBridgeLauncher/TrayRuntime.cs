using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed class BridgeTrayHost : ApplicationContext
{
    private readonly string baseDirectory;
    private readonly NotifyIcon icon;
    private readonly System.Threading.Mutex mutex;

    internal BridgeTrayHost(string directory)
    {
        baseDirectory = directory;
        bool created;
        mutex = new System.Threading.Mutex(true, @"Local\CodexProxyBridgeTray", out created);
        if (!created) { ExitThread(); return; }
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, delegate { OpenSettings(); });
        menu.Items.Add("启动 / 重启 Codex（经代理）", null, delegate { StartBridge("--restart-codex"); });
        menu.Items.Add("检测组件更新并批准", null, delegate { Program.CheckApprovedUpdates(baseDirectory, Program.LoadLauncherSettings(baseDirectory), true); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出托盘程序", null, delegate { ExitThread(); });
        icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "Codex 代理桥", Visible = true, ContextMenuStrip = menu };
        icon.MouseClick += delegate(object sender, MouseEventArgs args) { if (args.Button == MouseButtons.Left) OpenSettings(); };
    }

    private void StartBridge(string arguments)
    {
        var launcher = Path.Combine(baseDirectory, "CodexBridgeLauncher.exe");
        Process.Start(new ProcessStartInfo(launcher, arguments) { UseShellExecute = false, WorkingDirectory = baseDirectory });
    }

    private void OpenSettings()
    {
        var settings = Program.LoadLauncherSettings(baseDirectory);
        using (var form = new BridgeSettingsForm(baseDirectory, settings))
        {
            if (form.ShowDialog() != DialogResult.OK) return;
            Program.SaveLauncherSettings(baseDirectory, form.Settings);
            OrderedStartupTask.SetEnabled(form.Settings.AutoStartEnabled);
            var launcher = Path.Combine(baseDirectory, "CodexBridgeLauncher.exe");
            if (form.Settings.TaskbarOverrideEnabled)
            {
                TaskbarShortcuts.Replace(launcher);
                File.WriteAllText(Path.Combine(baseDirectory, "taskbar-grouping.enabled"), "1");
                TaskbarShortcuts.ConfigureRunningWindows(launcher);
            }
            else
            {
                TaskbarShortcuts.Restore(launcher);
                var marker = Path.Combine(baseDirectory, "taskbar-grouping.enabled");
                if (File.Exists(marker)) File.Delete(marker);
            }
        }
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class BridgeSettingsForm : Form
{
    private readonly CheckBox autoStart;
    private readonly CheckBox taskbar;
    internal Program.LauncherSettings Settings { get; private set; }

    internal BridgeSettingsForm(string baseDirectory, Program.LauncherSettings settings)
    {
        Settings = settings;
        Text = "Codex 代理桥设置"; Width = 540; Height = 260; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Left = 18, Top = 18, Width = 490, Height = 38, Text = "此个人版保留 Cockpit 直接 Hook、代理注入和账号切换后的 Codex 启动。" });
        autoStart = new CheckBox { Left = 18, Top = 65, Width = 490, Text = "Windows 登录后自动执行现有的 Clash -> Cockpit -> Codex 代理启动链", Checked = OrderedStartupTask.IsEnabled() }; Controls.Add(autoStart);
        taskbar = new CheckBox { Left = 18, Top = 98, Width = 490, Text = "用代理桥覆盖已固定的 ChatGPT / Codex 任务栏入口（可恢复）", Checked = File.Exists(Path.Combine(baseDirectory, "taskbar-grouping.enabled")) }; Controls.Add(taskbar);
        Controls.Add(new Label { Left = 18, Top = 132, Width = 490, Height = 35, Text = "“检测组件更新”会对 Cockpit、余额悬浮窗等已批准程序重新请求授权。" });
        var save = new Button { Left = 330, Top = 178, Width = 85, Text = "保存" }; save.Click += delegate { Settings.AutoStartEnabled = autoStart.Checked; Settings.TaskbarOverrideEnabled = taskbar.Checked; DialogResult = DialogResult.OK; Close(); }; Controls.Add(save);
        var cancel = new Button { Left = 425, Top = 178, Width = 85, Text = "取消" }; cancel.Click += delegate { Close(); }; Controls.Add(cancel);
    }
}

internal static class OrderedStartupTask
{
    private const string Name = "CodexOrderedStartup";
    internal static bool IsEnabled()
    {
        try { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic task = service.GetFolder("\\").GetTask(Name); return task.Enabled; }
        catch { return false; }
    }
    internal static void SetEnabled(bool enabled)
    {
        try { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic task = service.GetFolder("\\").GetTask(Name); task.Enabled = enabled; }
        catch (Exception error) { throw new InvalidOperationException("无法修改现有 CodexOrderedStartup 登录任务。", error); }
    }
}
