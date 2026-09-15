using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    private const string ProductName = "Scoped Proxy Launcher";
    private const string ConfigFileName = "proxy-launcher.json";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            if (HasArgument(args, "--self-test")) return 0;
            var config = LoadConfig(baseDirectory);
            if (HasArgument(args, "--tray"))
            {
                Application.Run(new TrayHost(baseDirectory));
                return 0;
            }
            if (HasArgument(args, "--autostart"))
            {
                RunAutostart(config);
                return 0;
            }
            if (HasArgument(args, "--check-updates"))
            {
                CheckForUpdatedTargets(config, baseDirectory, true);
                return 0;
            }
            if (HasArgument(args, "--configure"))
            {
                using (var form = new ConfigureForm(config, baseDirectory))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        SaveConfig(baseDirectory, form.Config);
                        AutostartTask.Sync(baseDirectory, form.Config);
                        TaskbarPin.Apply(baseDirectory, form.Config);
                    }
                }
                return 0;
            }
            if (HasArgument(args, "--validate"))
            {
                ValidateProxy(config.ProxyUrl);
                return 0;
            }
            string name = GetArgumentValue(args, "--target");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var target = config.Targets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (target == null) throw new InvalidOperationException("The selected application no longer exists in this launcher's configuration.");
                Launch(config, target);
                return 0;
            }
            if (!HasArgument(args, "--launch"))
            {
                Application.Run(new TrayHost(baseDirectory));
                return 0;
            }
            using (var form = new LaunchForm(config, baseDirectory))
            {
                if (form.ShowDialog() == DialogResult.OK && form.SelectedTarget != null) Launch(config, form.SelectedTarget);
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static LauncherConfig LoadConfig(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, ConfigFileName);
        if (!File.Exists(path))
        {
            return new LauncherConfig { ProxyUrl = "http://127.0.0.1:7890", Targets = DiscoverDefaultTargets() };
        }
        var config = new JavaScriptSerializer().Deserialize<LauncherConfig>(File.ReadAllText(path));
        if (config == null) throw new InvalidOperationException("Configuration could not be read.");
        if (config.Targets == null) config.Targets = new List<LaunchTarget>();
        return config;
    }

    internal static void SaveConfig(string baseDirectory, LauncherConfig config)
    {
        var serializer = new JavaScriptSerializer();
        string temporary = Path.Combine(baseDirectory, ConfigFileName + ".new");
        File.WriteAllText(temporary, serializer.Serialize(config), new UTF8Encoding(false));
        File.Copy(temporary, Path.Combine(baseDirectory, ConfigFileName), true);
        File.Delete(temporary);
    }

    private static List<LaunchTarget> DiscoverDefaultTargets()
    {
        var targets = new List<LaunchTarget>();
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")))
        {
            targets.Add(new LaunchTarget { Name = "Codex Desktop (Microsoft Store)", Kind = "codexStore", Path = "" });
        }
        return targets;
    }

    internal static void Launch(LauncherConfig config, LaunchTarget target)
    {
        Uri proxy = ValidateProxy(config.ProxyUrl);
        ValidateTargetApproval(target);
        ProcessStartInfo start = CreateStartInfo(target, proxy);
        Process.Start(start);
    }

    internal static ProcessStartInfo CreateStartInfo(LaunchTarget target, Uri proxy)
    {
        string executable = target.Path;
        string arguments = target.Arguments ?? string.Empty;
        if (string.Equals(target.Kind, "codexStore", StringComparison.OrdinalIgnoreCase))
        {
            executable = FindLatestCodexExecutable();
            string browserArgs = "--proxy-server=" + proxy.Host + ":" + proxy.Port + " --proxy-bypass-list=<local>";
            arguments = browserArgs + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
        }
        else if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("The target application file was not found. Open Configure and select it again.", executable);
        }
        var info = new ProcessStartInfo();
        if (executable.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = "pwsh.exe";
            info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + executable + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
        }
        else if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /c \"\"" + executable + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments) + "\"";
        }
        else
        {
            info.FileName = executable;
            info.Arguments = arguments;
        }
        info.UseShellExecute = false;
        info.CreateNoWindow = false;
        info.WorkingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory) ? Path.GetDirectoryName(executable) : target.WorkingDirectory;
        ApplyProxyEnvironment(info, proxy);
        return info;
    }

    internal static void ApplyProxyEnvironment(ProcessStartInfo info, Uri proxy)
    {
        string value = proxy.AbsoluteUri.TrimEnd('/');
        info.EnvironmentVariables["HTTP_PROXY"] = value;
        info.EnvironmentVariables["HTTPS_PROXY"] = value;
        info.EnvironmentVariables["http_proxy"] = value;
        info.EnvironmentVariables["https_proxy"] = value;
        info.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
        info.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1,::1";
        info.EnvironmentVariables.Remove("ALL_PROXY");
        info.EnvironmentVariables.Remove("all_proxy");
        info.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
    }

    internal static void RunAutostart(LauncherConfig config)
    {
        if (!config.AutoStartEnabled) return;
        if (string.IsNullOrWhiteSpace(config.ClashPath) || !File.Exists(config.ClashPath))
            throw new InvalidOperationException("Automatic startup is enabled but Clash Verge has not been selected in settings.");
        string processName = Path.GetFileNameWithoutExtension(config.ClashPath);
        if (Process.GetProcessesByName(processName).Length == 0) Process.Start(new ProcessStartInfo(config.ClashPath) { UseShellExecute = false });
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            try { ValidateProxy(config.ProxyUrl); break; }
            catch (Exception error)
            {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Clash did not make the local HTTP proxy ready within 120 seconds.", error);
                System.Threading.Thread.Sleep(500);
            }
        }
        // The scheduled task is a short-lived bootstrapper.  Start a separate
        // tray host once the proxy is usable so the settings remain available.
        string self = Process.GetCurrentProcess().MainModule.FileName;
        Process.Start(new ProcessStartInfo(self, "--tray") { UseShellExecute = false });
        foreach (var target in config.Targets) Launch(config, target);
    }

    internal static bool CheckForUpdatedTargets(LauncherConfig config, string baseDirectory, bool showNoChanges)
    {
        bool changed = false;
        foreach (var target in config.Targets.Where(item => string.Equals(item.Kind, "file", StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path)))
        {
            string hash = ComputeFileHash(target.Path);
            if (string.IsNullOrWhiteSpace(target.ApprovedHash)) { target.ApprovedHash = hash; changed = true; continue; }
            if (string.Equals(hash, target.ApprovedHash, StringComparison.OrdinalIgnoreCase)) continue;
            changed = true;
            var answer = MessageBox.Show("The selected application changed after it was approved:\n\n" + target.Name + "\n" + target.Path + "\n\nApprove this updated version for proxy launch?", "Proxy application update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) target.ApprovedHash = hash;
        }
        if (changed) SaveConfig(baseDirectory, config);
        if (!changed && showNoChanges) MessageBox.Show("No selected application updates were detected.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return changed;
    }

    internal static string ComputeFileHash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    internal static void ValidateTargetApproval(LaunchTarget target)
    {
        if (!string.Equals(target.Kind, "file", StringComparison.OrdinalIgnoreCase)) return;
        string actual = ComputeFileHash(target.Path);
        if (string.IsNullOrWhiteSpace(target.ApprovedHash) || !string.Equals(actual, target.ApprovedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This application changed after its last approval. Use the tray icon's Check for updates command to review it before launching.");
    }

    internal static Uri ValidateProxy(string proxyUrl)
    {
        Uri proxy;
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out proxy) || (proxy.Scheme != "http" && proxy.Scheme != "https"))
            throw new InvalidOperationException("Proxy address must be an HTTP proxy URL, such as http://127.0.0.1:7890.");
        IPAddress[] addresses = Dns.GetHostAddresses(proxy.Host);
        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("For safety, only a local loopback proxy is accepted.");
        using (var client = new TcpClient())
        {
            var task = client.BeginConnect(proxy.Host, proxy.Port, null, null);
            if (!task.AsyncWaitHandle.WaitOne(1500)) throw new InvalidOperationException("The local proxy did not accept a connection.");
            client.EndConnect(task);
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 2000;
                byte[] request = Encoding.ASCII.GetBytes("CONNECT chatgpt.com:443 HTTP/1.1\r\nHost: chatgpt.com:443\r\n\r\n");
                stream.Write(request, 0, request.Length);
                byte[] response = new byte[128];
                int count = stream.Read(response, 0, response.Length);
                string firstLine = Encoding.ASCII.GetString(response, 0, count).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                if (!firstLine.Contains(" 2")) throw new InvalidOperationException("The local proxy did not complete its CONNECT check.");
            }
        }
        return proxy;
    }

    private static string FindLatestCodexExecutable()
    {
        const string keyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, false))
        {
            if (key != null)
            {
                var candidates = key.GetSubKeyNames().Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                    .Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", name, "app", "ChatGPT.exe"))
                    .Where(File.Exists).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length > 0) return candidates[0];
            }
        }
        throw new FileNotFoundException("Microsoft Store Codex Desktop was not found. Install the official Codex desktop app first.");
    }

    private static bool HasArgument(string[] args, string expected) { return args.Any(item => string.Equals(item, expected, StringComparison.OrdinalIgnoreCase)); }
    private static string GetArgumentValue(string[] args, string expected)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

internal sealed class LauncherConfig { public string ProxyUrl { get; set; } public List<LaunchTarget> Targets { get; set; } public bool AutoStartEnabled { get; set; } public string ClashPath { get; set; } public bool TaskbarOverrideEnabled { get; set; } }
internal sealed class LaunchTarget { public string Name { get; set; } public string Kind { get; set; } public string Path { get; set; } public string Arguments { get; set; } public string WorkingDirectory { get; set; } public string ApprovedHash { get; set; } public override string ToString() { return Name; } }

internal sealed class LaunchForm : Form
{
    private readonly LauncherConfig config; private readonly string baseDirectory; private readonly ListBox list; public LaunchTarget SelectedTarget { get; private set; }
    public LaunchForm(LauncherConfig config, string baseDirectory)
    {
        this.config = config; this.baseDirectory = baseDirectory; Text = "Scoped Proxy Launcher"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new System.Drawing.Size(450, 255); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Controls.Add(new Label { Left = 15, Top = 15, Width = 420, Text = "Choose an application to start with a local HTTP proxy:" });
        list = new ListBox { Left = 15, Top = 42, Width = 420, Height = 140 }; list.DataSource = config.Targets; Controls.Add(list);
        var launch = new Button { Left = 255, Top = 200, Width = 85, Text = "Launch" }; launch.Click += delegate { SelectedTarget = list.SelectedItem as LaunchTarget; if (SelectedTarget == null) { MessageBox.Show("Add an application in Configure first."); return; } DialogResult = DialogResult.OK; Close(); }; Controls.Add(launch);
        var settings = new Button { Left = 145, Top = 200, Width = 95, Text = "Configure" }; settings.Click += delegate { using (var form = new ConfigureForm(config, baseDirectory)) { if (form.ShowDialog(this) == DialogResult.OK) { Program.SaveConfig(baseDirectory, form.Config); AutostartTask.Sync(baseDirectory, form.Config); TaskbarPin.Apply(baseDirectory, form.Config); list.DataSource = null; list.DataSource = config.Targets; } } }; Controls.Add(settings);
        var close = new Button { Left = 355, Top = 200, Width = 80, Text = "Close" }; close.Click += delegate { Close(); }; Controls.Add(close);
    }
}

internal sealed class ConfigureForm : Form
{
    private readonly TextBox proxy; private readonly TextBox clash; private readonly CheckBox autoStart; private readonly CheckBox taskbar; private readonly ListBox list; private readonly LauncherConfig config; public LauncherConfig Config { get { return config; } }
    public ConfigureForm(LauncherConfig config, string baseDirectory)
    {
        this.config = config; Text = "Configure Scoped Proxy Launcher"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new System.Drawing.Size(560, 475); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Controls.Add(new Label { Left = 15, Top = 15, Width = 520, Text = "Local HTTP proxy address (only 127.0.0.1 / localhost is accepted):" }); proxy = new TextBox { Left = 15, Top = 40, Width = 525, Text = config.ProxyUrl }; Controls.Add(proxy);
        autoStart = new CheckBox { Left = 15, Top = 76, Width = 520, Text = "Start Clash, wait for proxy, then start configured applications at logon", Checked = config.AutoStartEnabled }; Controls.Add(autoStart);
        Controls.Add(new Label { Left = 15, Top = 104, Width = 100, Text = "Clash Verge:" }); clash = new TextBox { Left = 115, Top = 101, Width = 330, Text = config.ClashPath ?? "" }; Controls.Add(clash);
        var browseClash = new Button { Left = 455, Top = 100, Width = 85, Text = "Browse" }; browseClash.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "Executable|*.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) clash.Text = dialog.FileName; }; Controls.Add(browseClash);
        taskbar = new CheckBox { Left = 15, Top = 134, Width = 520, Text = "Replace matching pinned ChatGPT/Codex taskbar shortcut with proxy launch", Checked = config.TaskbarOverrideEnabled }; Controls.Add(taskbar);
        Controls.Add(new Label { Left = 15, Top = 164, Width = 520, Text = "Applications and extensions launched with this scoped proxy:" });
        list = new ListBox { Left = 15, Top = 187, Width = 525, Height = 185, DataSource = config.Targets }; Controls.Add(list);
        var add = new Button { Left = 15, Top = 388, Width = 100, Text = "Add app" }; add.Click += delegate { using (var dialog = new TargetForm()) { if (dialog.ShowDialog(this) == DialogResult.OK) { dialog.Target.ApprovedHash = Program.ComputeFileHash(dialog.Target.Path); config.Targets.Add(dialog.Target); RefreshTargets(); } } }; Controls.Add(add);
        var remove = new Button { Left = 125, Top = 388, Width = 100, Text = "Remove" }; remove.Click += delegate { var item = list.SelectedItem as LaunchTarget; if (item != null) { config.Targets.Remove(item); RefreshTargets(); } }; Controls.Add(remove);
        var save = new Button { Left = 365, Top = 408, Width = 85, Text = "Save" }; save.Click += delegate { config.ProxyUrl = proxy.Text.Trim(); config.AutoStartEnabled = autoStart.Checked; config.ClashPath = clash.Text.Trim(); config.TaskbarOverrideEnabled = taskbar.Checked; try { Program.ValidateProxy(config.ProxyUrl); if (config.AutoStartEnabled && !File.Exists(config.ClashPath)) throw new InvalidOperationException("Select the Clash Verge executable before enabling automatic startup."); DialogResult = DialogResult.OK; Close(); } catch (Exception error) { MessageBox.Show(error.Message); } }; Controls.Add(save);
        var cancel = new Button { Left = 455, Top = 408, Width = 85, Text = "Cancel" }; cancel.Click += delegate { Close(); }; Controls.Add(cancel);
    }
    private void RefreshTargets() { list.DataSource = null; list.DataSource = config.Targets; }
}

internal sealed class TargetForm : Form
{
    private readonly TextBox name; private readonly TextBox path; private readonly TextBox args; public LaunchTarget Target { get; private set; }
    public TargetForm()
    {
        Text = "Add application"; StartPosition = FormStartPosition.CenterParent; ClientSize = new System.Drawing.Size(560, 205); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Controls.Add(new Label { Left = 15, Top = 15, Width = 100, Text = "Display name:" }); name = new TextBox { Left = 115, Top = 12, Width = 425 }; Controls.Add(name);
        Controls.Add(new Label { Left = 15, Top = 52, Width = 100, Text = "Program/script:" }); path = new TextBox { Left = 115, Top = 49, Width = 330 }; Controls.Add(path);
        var browse = new Button { Left = 455, Top = 48, Width = 85, Text = "Browse" }; browse.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "Programs and scripts|*.exe;*.cmd;*.bat;*.ps1|All files|*.*" }) { if (dialog.ShowDialog(this) == DialogResult.OK) { path.Text = dialog.FileName; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName); } } }; Controls.Add(browse);
        Controls.Add(new Label { Left = 15, Top = 89, Width = 100, Text = "Arguments:" }); args = new TextBox { Left = 115, Top = 86, Width = 425 }; Controls.Add(args);
        var ok = new Button { Left = 365, Top = 140, Width = 85, Text = "Add" }; ok.Click += delegate { if (string.IsNullOrWhiteSpace(name.Text) || !File.Exists(path.Text)) { MessageBox.Show("Enter a name and select an existing file."); return; } Target = new LaunchTarget { Name = name.Text.Trim(), Kind = "file", Path = path.Text, Arguments = args.Text }; DialogResult = DialogResult.OK; Close(); }; Controls.Add(ok);
        var cancel = new Button { Left = 455, Top = 140, Width = 85, Text = "Cancel" }; cancel.Click += delegate { Close(); }; Controls.Add(cancel);
    }
}
