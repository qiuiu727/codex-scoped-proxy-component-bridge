using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Setup
{
    private const string Product = "Scoped Proxy Launcher";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScopedProxyLauncher";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (Has(args, "--self-test")) return Assembly.GetExecutingAssembly().GetManifestResourceStream("ScopedProxyLauncher.exe") == null ? 2 : 0;
        if (Has(args, "--uninstall-worker")) return UninstallWorker(args);
        if (Has(args, "--uninstall") || Has(args, "/uninstall")) return BeginUninstall();
        return Install();
    }
    private static int Install()
    {
        bool chinese = MessageBox.Show("请选择安装语言。\n\n是：简体中文\n否：English\n取消：退出。", "Language / 安装语言", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question) == DialogResult.Yes;
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product);
        using (var form = new InstallPathForm(chinese, defaultPath)) { if (form.ShowDialog() != DialogResult.OK) return 1; defaultPath = form.InstallPath; }
        if (string.IsNullOrWhiteSpace(defaultPath)) return 1;
        bool desktop = MessageBox.Show(chinese ? "是否创建主启动器的桌面快捷方式？\n\n主启动器始终会放入开始菜单。" : "Create a desktop shortcut for the main launcher?\n\nThe main launcher is always placed in Start.", Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        bool restart = MessageBox.Show(chinese ? "是否额外创建“重启 Codex”入口？\n\n选择“是”后，会在开始菜单中创建；如已选择桌面快捷方式，也会同时放到桌面。" : "Also create a Restart Codex entry?\n\nYes adds it to Start and, if selected, to the Desktop.", Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        try
        {
            Directory.CreateDirectory(defaultPath); string launcher = Path.Combine(defaultPath, "ScopedProxyLauncher.exe"); Extract("ScopedProxyLauncher.exe", launcher); File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(defaultPath, "Uninstall.exe"), true);
            if (!File.Exists(Path.Combine(defaultPath, "proxy-launcher.json"))) File.WriteAllText(Path.Combine(defaultPath, "proxy-launcher.json"), "{\"ProxyUrl\":\"http://127.0.0.1:7890\",\"Language\":\"" + (chinese ? "zh-CN" : "en-US") + "\",\"Configured\":false,\"Targets\":[{\"Name\":\"Codex Desktop (Microsoft Store)\",\"Kind\":\"codexStore\",\"Path\":\"\",\"Arguments\":\"\",\"WorkingDirectory\":\"\"}]}", new UTF8Encoding(false));
            CreateStartMenu(launcher, defaultPath, chinese, restart); CreateDesktopShortcuts(launcher, defaultPath, chinese, desktop, restart);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) { key.SetValue("DisplayName", Product); key.SetValue("DisplayVersion", "1.2.0"); key.SetValue("Publisher", "qiuiu727"); key.SetValue("InstallLocation", defaultPath); key.SetValue("DisplayIcon", launcher + ",0"); key.SetValue("UninstallString", "\"" + Path.Combine(defaultPath, "Uninstall.exe") + "\" --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1); }
            MessageBox.Show(chinese ? "安装完成。请打开“代理启动器”；首次打开会自动显示设置，之后请从右下角隐藏图标打开设置。它不会安装 ChatGPT/Codex，也不会改系统代理或 TUN。" : "Installed. Open the main launcher; the first opening shows Settings, and later Settings are available from the notification-area icon. It does not install ChatGPT/Codex or change system proxy/TUN.", Product); return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static void CreateStartMenu(string launcher, string directory, bool chinese, bool restart) { string group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product); Directory.CreateDirectory(group); foreach (string path in Directory.GetFiles(group, "*.lnk")) File.Delete(path); CreateShortcut(Path.Combine(group, chinese ? "代理启动器.lnk" : "Proxy Launcher.lnk"), launcher, string.Empty, directory, chinese ? "通过代理启动 Codex 和已配置程序" : "Launch Codex and configured apps through the proxy"); if (restart) CreateShortcut(Path.Combine(group, chinese ? "重启 Codex.lnk" : "Restart Codex.lnk"), launcher, "--restart-codex", directory, chinese ? "重启并通过代理启动 Codex" : "Restart Codex through the proxy"); }
    private static void CreateDesktopShortcuts(string launcher, string directory, bool chinese, bool desktop, bool restart) { string root = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); string main = Path.Combine(root, chinese ? "代理启动器.lnk" : "Proxy Launcher.lnk"); string reboot = Path.Combine(root, chinese ? "重启 Codex.lnk" : "Restart Codex.lnk"); string legacy = Path.Combine(root, Product + ".lnk"); if (File.Exists(main)) File.Delete(main); if (File.Exists(reboot)) File.Delete(reboot); if (File.Exists(legacy)) File.Delete(legacy); if (!desktop) return; CreateShortcut(main, launcher, string.Empty, directory, chinese ? "通过代理启动 Codex 和已配置程序" : "Launch Codex and configured apps through the proxy"); if (restart) CreateShortcut(reboot, launcher, "--restart-codex", directory, chinese ? "重启并通过代理启动 Codex" : "Restart Codex through the proxy"); }
    private static void CreateShortcut(string link, string target, string args, string work, string description) { dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic item = shell.CreateShortcut(link); item.TargetPath = target; item.Arguments = args; item.WorkingDirectory = work; item.IconLocation = target + ",0"; item.Description = description; item.Save(); }
    private static void Extract(string name, string target) { using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (input == null) throw new InvalidOperationException("Embedded launcher missing."); using (var output = new FileStream(target, FileMode.Create, FileAccess.Write)) input.CopyTo(output); } }
    private static int BeginUninstall() { if (MessageBox.Show("Uninstall Scoped Proxy Launcher?", "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 1; string worker = Path.Combine(Path.GetTempPath(), "ScopedProxyLauncherUninstall-" + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(Assembly.GetExecutingAssembly().Location, worker, true); Process.Start(new ProcessStartInfo(worker, "--uninstall-worker \"" + AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + "\" " + Process.GetCurrentProcess().Id) { UseShellExecute = false, CreateNoWindow = true }); return 0; }
    private static int UninstallWorker(string[] args) { try { string directory = args[1]; int parent; if (int.TryParse(args[2], out parent)) try { Process.GetProcessById(parent).WaitForExit(10000); } catch { } string group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product); if (Directory.Exists(group)) Directory.Delete(group, true); string root = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); foreach (string name in new[] { Product + ".lnk", "代理启动器.lnk", "Proxy Launcher.lnk", "重启 Codex.lnk", "Restart Codex.lnk" }) { string desktop = Path.Combine(root, name); if (File.Exists(desktop)) File.Delete(desktop); } Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); if (Directory.Exists(directory)) Directory.Delete(directory, true); MoveFileEx(Assembly.GetExecutingAssembly().Location, null, 4); return 0; } catch { return 1; } }
    private static bool Has(string[] args, string value) { foreach (string item in args) if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true; return false; }
}

internal sealed class InstallPathForm : Form
{
    private readonly TextBox box; public string InstallPath { get { return box.Text.Trim(); } }
    public InstallPathForm(bool chinese, string defaultPath) { Text = chinese ? "选择安装位置" : "Choose installation folder"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; ClientSize = new Size(580, 130); Controls.Add(new Label { Left = 15, Top = 15, Width = 545, Text = chinese ? "选择软件安装目录：" : "Choose application installation folder:" }); box = new TextBox { Left = 15, Top = 43, Width = 450, Text = defaultPath }; Controls.Add(box); var browse = new Button { Left = 475, Top = 41, Width = 85, Text = chinese ? "浏览" : "Browse" }; browse.Click += delegate { using (var dialog = new FolderBrowserDialog { SelectedPath = box.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = Path.Combine(dialog.SelectedPath, InstallProductName()); }; Controls.Add(browse); var ok = new Button { Left = 385, Top = 88, Width = 85, Text = chinese ? "安装" : "Install", DialogResult = DialogResult.OK }; var cancel = new Button { Left = 475, Top = 88, Width = 85, Text = chinese ? "取消" : "Cancel", DialogResult = DialogResult.Cancel }; Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel; }
    private static string InstallProductName() { return "Scoped Proxy Launcher"; }
}
