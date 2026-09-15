using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr block);
    private const string PackageRegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string WatcherMutexName = @"Local\CodexProxyBridgeCockpitWatcher";
    private const string OperationMutexName = @"Local\CodexProxyBridgeOperation";
    private static readonly Regex PackageVersionPattern = new Regex(@"^OpenAI\.Codex_(?<version>\d+(?:\.\d+){3})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProxyUrlPattern = new Regex("\\\"proxyUrl\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled);
    private static readonly Regex RestartSettingPattern = new Regex("\\\"restartCodexOnLaunch\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LanguageSettingPattern = new Regex("\\\"language\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AutoStartSettingPattern = new Regex("\\\"autoStartEnabled\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TaskbarSettingPattern = new Regex("\\\"taskbarOverrideEnabled\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FirstRunSettingPattern = new Regex("\\\"firstRun\\\"\\s*:\\s*(?<value>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [STAThread]
    private static int Main(string[] args)
    {
        var baseDirectory = ResolveBaseDirectory(AppDomain.CurrentDomain.BaseDirectory);
        var settings = LoadLauncherSettings(baseDirectory);
        var watchMode = HasArgument(args, "--watch");
        var cockpitHook = HasArgument(args, "--cockpit-hook") || string.Equals(
            Path.GetFileNameWithoutExtension(Application.ExecutablePath), "CodexCockpitHook", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (HasArgument(args, "--tray"))
            {
                RunTray(baseDirectory);
                return 0;
            }
            if (HasArgument(args, "--check-updates"))
            {
                CheckApprovedUpdates(baseDirectory, settings, true);
                return 0;
            }
            if (HasArgument(args, "--configure-cockpit-hook"))
            {
                CockpitHook.Enable(baseDirectory);
                WriteLog(baseDirectory, "COCKPIT_DIRECT_HOOK_CONFIGURED Cockpit restart required to reload cached settings");
                return 0;
            }
            if (HasArgument(args, "--repair-taskbar"))
            {
                var launcher = Path.Combine(baseDirectory, "CodexBridgeLauncher.exe");
                int changed = TaskbarShortcuts.Replace(launcher);
                File.WriteAllText(Path.Combine(baseDirectory, "taskbar-grouping.enabled"), "1");
                TaskbarShortcuts.ConfigureRunningWindows(launcher);
                WriteLog(baseDirectory, "TASKBAR_REPAIRED matchingPins=" + changed);
                return 0;
            }
            if (watchMode)
            {
                return WatchCockpit(baseDirectory, settings);
            }
            if (settings.FirstRun)
            {
                using (var form = new BridgeSettingsForm(baseDirectory, settings))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        form.Settings.FirstRun = false;
                        SaveLauncherSettings(baseDirectory, form.Settings);
                        OrderedStartupTask.SetEnabled(form.Settings.AutoStartEnabled);
                    }
                }
                RunTray(baseDirectory);
                return 0;
            }

            var proxyUri = LoadAndValidateProxy(baseDirectory);
            var codexPath = FindLatestCodexExecutable();
            WriteLog(baseDirectory, string.Format("VALIDATED proxy={0}:{1} codex={2}", proxyUri.Host, proxyUri.Port, codexPath));
            if (HasArgument(args, "--validate"))
            {
                return 0;
            }

            using (var operationMutex = new Mutex(false, OperationMutexName))
            {
                if (!AcquireOperation(operationMutex, 60000))
                {
                    throw new InvalidOperationException("Another launch/restart is still running. Please retry after it finishes.");
                }
                try
                {
                    // A switch completion hook must never restart its caller or start a watcher.
                    if (!cockpitHook)
                    {
                        EnsureCockpitScoped(baseDirectory, proxyUri, settings, true, true);
                        StartOtherApprovedComponents(baseDirectory, proxyUri);
                    }
                    else WriteLog(baseDirectory, "COCKPIT_DIRECT_HOOK received");

                    var executableName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
                    var restartCodex = cockpitHook || HasRestartArgument(args, "--restart-codex")
                        || settings.RestartCodexOnLaunch
                        || executableName.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0
                        || executableName.IndexOf("重启", StringComparison.OrdinalIgnoreCase) >= 0;
                    var existing = FindRunningStoreCodex();
                    if (existing != null && existing.MainWindowHandle == IntPtr.Zero && !restartCodex)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (DateTime.UtcNow < deadline && existing != null && existing.MainWindowHandle == IntPtr.Zero)
                        {
                            Thread.Sleep(500);
                            existing = FindRunningStoreCodex();
                        }
                        if (existing != null && existing.MainWindowHandle == IntPtr.Zero)
                            restartCodex = true;
                    }
                    if (restartCodex && existing != null)
                    {
                        StopExistingCodex(baseDirectory);
                        existing = null;
                    }

                    if (existing != null)
                    {
                        FocusProcess(existing);
                        WriteLog(baseDirectory, "FOCUSED existing Codex; no restart requested");
                    }
                    else
                    {
                        LaunchAndVerifyCodex(baseDirectory, codexPath, proxyUri);
                    }

                    var groupingChoice = Path.Combine(baseDirectory, "taskbar-grouping.enabled");
                    if (File.Exists(groupingChoice)) TaskbarShortcuts.ConfigureRunningWindows(Path.Combine(baseDirectory, "CodexBridgeLauncher.exe"));
                    if (!cockpitHook) StartWatcher(baseDirectory);
                    EnsureTrayHost(baseDirectory);
                    return 0;
                }
                finally
                {
                    operationMutex.ReleaseMutex();
                }
            }
        }
        catch (Exception error)
        {
            TryWriteFailure(baseDirectory, error);
            if (!watchMode)
            {
                MessageBox.Show(error.Message, Text(settings, "Codex Proxy Bridge", "Codex 代理桥"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static bool HasArgument(IEnumerable<string> args, string expected)
    {
        return args.Any(arg => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AcquireOperation(Mutex mutex, int timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool HasRestartArgument(IEnumerable<string> args, string expected)
    {
        return HasArgument(args, expected);
    }

    private static string ResolveBaseDirectory(string executableDirectory)
    {
        if (File.Exists(Path.Combine(executableDirectory, "config.json")))
        {
            return executableDirectory;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex Proxy Bridge") + Path.DirectorySeparatorChar;
    }

    internal static LauncherSettings LoadLauncherSettings(string baseDirectory)
    {
        var settings = new LauncherSettings();
        var path = Path.Combine(baseDirectory, "launcher-settings.json");
        if (!File.Exists(path))
        {
            settings.Language = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
            return settings;
        }
        var content = File.ReadAllText(path);
        var restartMatch = RestartSettingPattern.Match(content);
        settings.RestartCodexOnLaunch = restartMatch.Success && string.Equals(restartMatch.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
        var languageMatch = LanguageSettingPattern.Match(content);
        settings.Language = languageMatch.Success ? languageMatch.Groups["value"].Value : "en-US";
        var autoStartMatch = AutoStartSettingPattern.Match(content);
        settings.AutoStartEnabled = autoStartMatch.Success && string.Equals(autoStartMatch.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
        var taskbarMatch = TaskbarSettingPattern.Match(content);
        settings.TaskbarOverrideEnabled = taskbarMatch.Success && string.Equals(taskbarMatch.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
        var firstRunMatch = FirstRunSettingPattern.Match(content);
        settings.FirstRun = firstRunMatch.Success && string.Equals(firstRunMatch.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
        return settings;
    }

    internal static void SaveLauncherSettings(string baseDirectory, LauncherSettings settings)
    {
        var serializer = new JavaScriptSerializer();
        File.WriteAllText(Path.Combine(baseDirectory, "launcher-settings.json"), serializer.Serialize(settings), new UTF8Encoding(false));
    }

    private static void EnsureTrayHost(string baseDirectory)
    {
        var executable = Path.Combine(baseDirectory, "CodexBridgeLauncher.exe");
        if (File.Exists(executable)) Process.Start(new ProcessStartInfo(executable, "--tray") { UseShellExecute = false, WorkingDirectory = baseDirectory });
    }

    private static void RunTray(string baseDirectory)
    {
        var tray = new BridgeTrayHost(baseDirectory);
        if (tray.IsPrimary) Application.Run(tray);
    }

    private static string Text(LauncherSettings settings, string english, string chinese)
    {
        return settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
    }

    private static Uri LoadAndValidateProxy(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException("config.json was not found next to CodexBridgeLauncher.exe.");
        }
        var match = ProxyUrlPattern.Match(File.ReadAllText(configPath));
        if (!match.Success)
        {
            throw new InvalidOperationException("config.json does not contain proxyUrl.");
        }
        Uri proxyUri;
        if (!Uri.TryCreate(match.Groups["value"].Value, UriKind.Absolute, out proxyUri)
            || (proxyUri.Scheme != "http" && proxyUri.Scheme != "https"))
        {
            throw new InvalidOperationException("proxyUrl must be an HTTP proxy URL.");
        }
        var addresses = Dns.GetHostAddresses(proxyUri.Host);
        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        {
            throw new InvalidOperationException("The bridge accepts only a loopback HTTP proxy.");
        }
        VerifyProxyConnect(proxyUri);
        return proxyUri;
    }

    private static void VerifyProxyConnect(Uri proxyUri)
    {
        using (var client = new TcpClient())
        {
            var connect = client.BeginConnect(proxyUri.Host, proxyUri.Port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(2000))
            {
                throw new InvalidOperationException("The local HTTP proxy did not accept a connection.");
            }
            client.EndConnect(connect);
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 3000;
                var request = Encoding.ASCII.GetBytes("CONNECT chatgpt.com:443 HTTP/1.1\r\nHost: chatgpt.com:443\r\n\r\n");
                stream.Write(request, 0, request.Length);
                var buffer = new byte[256];
                var count = stream.Read(buffer, 0, buffer.Length);
                var response = Encoding.ASCII.GetString(buffer, 0, count);
                if (!Regex.IsMatch(response, @"^HTTP/1\.[01] 2\d\d", RegexOptions.CultureInvariant))
                {
                    throw new InvalidOperationException("The local HTTP proxy could not establish an HTTPS tunnel to chatgpt.com.");
                }
            }
        }
    }

    private static string FindLatestCodexExecutable()
    {
        var candidates = new List<Candidate>();
        using (var packages = Registry.CurrentUser.OpenSubKey(PackageRegistryPath, false))
        {
            if (packages == null)
            {
                throw new InvalidOperationException("Windows package registration was not found.");
            }
            foreach (var packageName in packages.GetSubKeyNames())
            {
                var match = PackageVersionPattern.Match(packageName);
                Version version;
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out version))
                {
                    continue;
                }
                using (var package = packages.OpenSubKey(packageName, false))
                {
                    var root = package == null ? null : package.GetValue("PackageRootFolder") as string;
                    var executable = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "app", "ChatGPT.exe");
                    if (File.Exists(executable))
                    {
                        candidates.Add(new Candidate { Version = version, Path = executable });
                    }
                }
            }
        }
        var selected = candidates.OrderByDescending(candidate => candidate.Version).FirstOrDefault();
        if (selected == null)
        {
            throw new InvalidOperationException("Microsoft Store Codex was not found.");
        }
        return selected.Path;
    }

    private static void EnsureCockpitScoped(string baseDirectory, Uri proxyUri, LauncherSettings settings, bool startIfMissing, bool restartIfUnmarked)
    {
        var cockpitPath = ResolveCockpitPath(baseDirectory);
        if (!File.Exists(cockpitPath))
        {
            return;
        }
        var component = GetOrApproveCockpit(baseDirectory, cockpitPath, settings);
        if (component == null)
        {
            return;
        }
        var running = FindProcessByPath(cockpitPath);
        EnsureCockpitCodexProxyArguments(baseDirectory, proxyUri);
        if (running != null && IsMarkedCockpit(baseDirectory, running, cockpitPath))
        {
            return;
        }
        if (running != null && !restartIfUnmarked)
        {
            return;
        }
        if (running != null)
        {
            WriteLog(baseDirectory, "RESTARTING Cockpit without scoped proxy marker pid=" + running.Id);
            StopProcess(running, 10);
        }
        else if (!startIfMissing)
        {
            return;
        }
        EnsureCockpitCodexProxyArguments(baseDirectory, proxyUri);
        var process = Process.Start(CreateProxiedStartInfo(cockpitPath, component.arguments, proxyUri));
        if (process == null)
        {
            throw new InvalidOperationException("Could not start Cockpit Tools.");
        }
        SaveCockpitMarker(baseDirectory, process, cockpitPath, component.sha256);
        WriteLog(baseDirectory, "STARTED Cockpit with scoped proxy pid=" + process.Id);
    }

    private static string ResolveCockpitPath(string baseDirectory)
    {
        var document = LoadApprovedDocument(baseDirectory);
        var saved = document.components.FirstOrDefault(component => component != null
            && !string.IsNullOrWhiteSpace(component.executablePath)
            && string.Equals(Path.GetFileName(component.executablePath), "cockpit-tools.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(component.executablePath));
        return saved == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cockpit Tools", "cockpit-tools.exe")
            : saved.executablePath;
    }

    private static ApprovedComponent GetOrApproveCockpit(string baseDirectory, string cockpitPath, LauncherSettings settings)
    {
        var document = LoadApprovedDocument(baseDirectory);
        var component = document.components.FirstOrDefault(item => item != null && string.Equals(item.executablePath, cockpitPath, StringComparison.OrdinalIgnoreCase));
        var currentHash = GetSha256(cockpitPath);
        if (component != null && string.Equals(component.sha256, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            return component;
        }
        var message = Text(settings,
            string.Format("Cockpit Tools is not yet approved, or its executable changed.\n\nPath: {0}\nSHA-256: {1}\n\nRestart it through the loopback proxy?", cockpitPath, currentHash),
            string.Format("Cockpit Tools 尚未批准，或其可执行文件已经更新。\n\n路径：{0}\nSHA-256：{1}\n\n是否允许通过本地回环代理重启它？", cockpitPath, currentHash));
        var answer = MessageBox.Show(message, Text(settings, "Codex Proxy Bridge", "Codex 代理桥"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            WriteLog(baseDirectory, "SKIPPED unapproved Cockpit hash=" + currentHash);
            return null;
        }
        if (component == null)
        {
            component = new ApprovedComponent { id = Guid.NewGuid().ToString(), displayName = "Cockpit Tools", executablePath = cockpitPath, arguments = string.Empty };
            document.components.Add(component);
        }
        component.sha256 = currentHash;
        component.approvedAt = DateTime.UtcNow.ToString("o");
        SaveApprovedDocument(baseDirectory, document);
        return component;
    }

    private static void EnsureCockpitCodexProxyArguments(string baseDirectory, Uri proxyUri)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("COCKPIT_TOOLS_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_cockpit");
        }
        var path = Path.Combine(dataDirectory, "codex_instances.json");
        if (!File.Exists(path))
        {
            return;
        }
        var serializer = new JavaScriptSerializer();
        var root = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
        object settingsValue;
        if (root == null || !root.TryGetValue("defaultSettings", out settingsValue))
        {
            return;
        }
        var defaultSettings = settingsValue as Dictionary<string, object>;
        if (defaultSettings == null)
        {
            return;
        }
        object currentValue;
        var current = defaultSettings.TryGetValue("extraArgs", out currentValue) ? Convert.ToString(currentValue) : string.Empty;
        current = Regex.Replace(current ?? string.Empty, "(?:^|\\s)--proxy-server=(?:\"[^\"]*\"|\\S+)", string.Empty, RegexOptions.IgnoreCase);
        current = Regex.Replace(current, "(?:^|\\s)--proxy-bypass-list=(?:\"[^\"]*\"|\\S+)", string.Empty, RegexOptions.IgnoreCase).Trim();
        var required = string.Format("--proxy-server={0}:{1} --proxy-bypass-list=<local>", proxyUri.Host, proxyUri.Port);
        var updated = string.IsNullOrWhiteSpace(current) ? required : current + " " + required;
        if (string.Equals(Convert.ToString(currentValue), updated, StringComparison.Ordinal))
        {
            return;
        }
        var backupPath = path + ".proxybridge.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(path, backupPath, false);
        }
        defaultSettings["extraArgs"] = updated;
        var temporaryPath = path + ".proxybridge.tmp";
        File.WriteAllText(temporaryPath, serializer.Serialize(root), new UTF8Encoding(false));
        File.Replace(temporaryPath, path, null);
        WriteLog(baseDirectory, "UPDATED Cockpit default Codex proxy arguments");
    }

    private static int WatchCockpit(string baseDirectory, LauncherSettings settings)
    {
        if (File.Exists(Path.Combine(baseDirectory, "cockpit-direct-hook.enabled"))) return 0;
        using (var mutex = new Mutex(false, WatcherMutexName))
        {
            if (!mutex.WaitOne(0))
            {
                return 0;
            }
            WriteLog(baseDirectory, "WATCHER_STARTED");
            var cockpitLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_cockpit", "logs");
            var observed = new HashSet<int>(GetStoreCodexRoots().Select(p => p.Id));
            try
            {
                while (true)
                {
                    Uri proxyUri;
                    try
                    {
                        proxyUri = LoadAndValidateProxy(baseDirectory);
                    }
                    catch
                    {
                        Thread.Sleep(5000);
                        continue;
                    }
                    var path = ResolveCockpitPath(baseDirectory);
                    var running = File.Exists(path) ? FindProcessByPath(path) : null;
                    if (running != null && !IsMarkedCockpit(baseDirectory, running, path))
                    {
                        Thread.Sleep(5000);
                        running = FindProcessByPath(path);
                        if (running != null && !IsMarkedCockpit(baseDirectory, running, path))
                        {
                            using (var operationMutex = new Mutex(false, OperationMutexName))
                            {
                                if (AcquireOperation(operationMutex, 0))
                                {
                                    try
                                    {
                                        EnsureCockpitScoped(baseDirectory, proxyUri, settings, false, true);
                                    }
                                    finally
                                    {
                                        operationMutex.ReleaseMutex();
                                    }
                                }
                            }
                        }
                    }
                    // Store activation can discard the parent's scoped environment. Only
                    // handle a NEW default-instance launch reported by Cockpit itself.
                    foreach (var root in GetStoreCodexRoots())
                    {
                        if (observed.Contains(root.Id)) continue;
                        if (!WasDefaultCockpitLaunch(cockpitLog, root.Id)) continue;
                        if (GetStoreCodexRoots().Length != 1) continue;
                        observed.Add(root.Id);
                        Thread.Sleep(3000);
                        using (var operation = new Mutex(false, OperationMutexName))
                        {
                            if (!AcquireOperation(operation, 0)) { observed.Remove(root.Id); continue; }
                            try
                            {
                                var current = FindRunningStoreCodex();
                                if (current == null || current.Id != root.Id) continue;
                                WriteLog(baseDirectory, "REPAIRING Cockpit Store-activated default Codex pid=" + root.Id);
                                StopExistingCodex(baseDirectory);
                                LaunchAndVerifyCodex(baseDirectory, FindLatestCodexExecutable(), proxyUri);
                                foreach (var launched in GetStoreCodexRoots()) observed.Add(launched.Id);
                            }
                            catch (Exception error) { TryWriteFailure(baseDirectory, error); }
                            finally { operation.ReleaseMutex(); }
                        }
                    }
                    Thread.Sleep(2000);
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void StartWatcher(string baseDirectory)
    {
        if (File.Exists(Path.Combine(baseDirectory, "cockpit-direct-hook.enabled")))
        {
            WriteLog(baseDirectory, "WATCHER_DISABLED direct Cockpit hook owns switch launches");
            return;
        }
        var installedExecutable = Path.Combine(baseDirectory, "CodexBridgeLauncher.exe");
        var executable = File.Exists(installedExecutable) ? installedExecutable : Process.GetCurrentProcess().MainModule.FileName;
        WatcherTask.Ensure(executable);
        WriteLog(baseDirectory, "WATCHER_TASK_ENSURED independent of launcher parent");
    }

    private static void SaveCockpitMarker(string baseDirectory, Process process, string path, string sha256)
    {
        var dataDirectory = Path.Combine(baseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        var marker = new CockpitMarker { processId = process.Id, startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, executablePath = path, sha256 = sha256 };
        File.WriteAllText(Path.Combine(dataDirectory, "cockpit-proxy-state.json"), new JavaScriptSerializer().Serialize(marker), new UTF8Encoding(false));
    }

    private static bool IsMarkedCockpit(string baseDirectory, Process process, string path)
    {
        try
        {
            var markerPath = Path.Combine(baseDirectory, "data", "cockpit-proxy-state.json");
            if (!File.Exists(markerPath))
            {
                return false;
            }
            var marker = new JavaScriptSerializer().Deserialize<CockpitMarker>(File.ReadAllText(markerPath));
            return marker != null && marker.processId == process.Id
                && marker.startTimeUtcTicks == process.StartTime.ToUniversalTime().Ticks
                && string.Equals(marker.executablePath, path, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void StartOtherApprovedComponents(string baseDirectory, Uri proxyUri)
    {
        foreach (var component in LoadApprovedDocument(baseDirectory).components)
        {
            if (component == null || string.IsNullOrWhiteSpace(component.executablePath) || !File.Exists(component.executablePath)
                || string.Equals(Path.GetFileName(component.executablePath), "cockpit-tools.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.Equals(GetSha256(component.executablePath), component.sha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteLog(baseDirectory, "SKIPPED changed component " + component.executablePath);
                continue;
            }
            if (FindProcessByPath(component.executablePath) == null)
            {
                var process = Process.Start(CreateProxiedStartInfo(component.executablePath, component.arguments, proxyUri));
                WriteLog(baseDirectory, string.Format("STARTED component={0} pid={1}", component.displayName, process == null ? 0 : process.Id));
            }
        }
    }

    internal static void CheckApprovedUpdates(string baseDirectory, LauncherSettings settings, bool showNoChanges)
    {
        var document = LoadApprovedDocument(baseDirectory);
        bool changed = false;
        foreach (var component in document.components.Where(item => item != null && !string.IsNullOrWhiteSpace(item.executablePath) && File.Exists(item.executablePath)))
        {
            var hash = GetSha256(component.executablePath);
            if (string.Equals(hash, component.sha256, StringComparison.OrdinalIgnoreCase)) continue;
            changed = true;
            var approved = MessageBox.Show(Text(settings,
                "An approved component changed. Allow its updated version through the proxy?\n\n" + component.displayName,
                "已批准的组件发生更新。是否批准新版本通过代理启动？\n\n" + component.displayName),
                Text(settings, "Proxy component update", "代理组件更新"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (approved == DialogResult.Yes)
            {
                component.sha256 = hash;
                component.approvedAt = DateTime.UtcNow.ToString("o");
            }
        }
        if (changed) SaveApprovedDocument(baseDirectory, document);
        else if (showNoChanges) MessageBox.Show(Text(settings, "No approved component updates were found.", "没有发现已批准组件的更新。"), Text(settings, "Codex Proxy Bridge", "Codex 代理桥"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static ApprovedDocument LoadApprovedDocument(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "data", "approved-components.json");
        if (!File.Exists(path))
        {
            return new ApprovedDocument { schemaVersion = 1, components = new List<ApprovedComponent>() };
        }
        var document = new JavaScriptSerializer().Deserialize<ApprovedDocument>(File.ReadAllText(path)) ?? new ApprovedDocument();
        document.components = document.components ?? new List<ApprovedComponent>();
        document.schemaVersion = document.schemaVersion == 0 ? 1 : document.schemaVersion;
        return document;
    }

    private static void SaveApprovedDocument(string baseDirectory, ApprovedDocument document)
    {
        var directory = Path.Combine(baseDirectory, "data");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "approved-components.json"), new JavaScriptSerializer().Serialize(document), new UTF8Encoding(false));
    }

    private static Process FindProcessByPath(string executablePath)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var module = process.MainModule;
                if (module != null && string.Equals(module.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static void StopProcess(Process process, int timeoutSeconds)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
            }
        }
        catch
        {
        }
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            Thread.Sleep(250);
        }
        try
        {
            process.Kill();
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static Process FindRunningStoreCodex()
    {
        return GetStoreCodexRoots().OrderByDescending(p => p.MainWindowHandle != IntPtr.Zero).FirstOrDefault();
    }

    private static Process[] GetStoreCodexRoots()
    {
        var result = new List<Process>();
        using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='ChatGPT.exe'"))
        using (var rows = search.Get())
        foreach (ManagementObject row in rows)
        {
            var command = Convert.ToString(row["CommandLine"]);
            if (string.IsNullOrEmpty(command) || command.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            try
            {
                var process = Process.GetProcessById(Convert.ToInt32(row["ProcessId"]));
                if (IsStoreCodexProcess(process)) result.Add(process);
            }
            catch (ArgumentException) { }
        }
        return result.ToArray();
    }

    private static bool WasDefaultCockpitLaunch(string logDirectory, int pid)
    {
        if (!Directory.Exists(logDirectory)) return false;
        var file = new DirectoryInfo(logDirectory).GetFiles("app.log.*").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file == null) return false;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd().Contains("default launch phase finished, pid=" + pid + ",");
        }
    }

    private static bool HasStartupBootstrapTimeout(int pid)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex", "Logs", DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
        if (!Directory.Exists(directory)) return false;
        foreach (var file in Directory.GetFiles(directory, "*-" + pid + "-t0-*.log"))
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            if (reader.ReadToEnd().Contains("Timed out while fetching post-login Statsig bootstrap")) return true;
        }
        return false;
    }

    private static bool IsStoreCodexProcess(Process process)
    {
        try
        {
            var module = process.MainModule;
            var path = module == null ? string.Empty : module.FileName;
            return path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
                && path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void StopExistingCodex(string baseDirectory)
    {
        foreach (var process in GetStoreCodexRoots())
        {
            StopProcess(process, 5);
        }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = Process.GetProcessesByName("ChatGPT").Where(IsStoreCodexProcess).ToArray();
            if (remaining.Length == 0) { WriteLog(baseDirectory, "STOPPED all verified Codex desktop processes"); return; }
            foreach (var process in remaining)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception error)
                {
                    WriteLog(baseDirectory, "STOP_RETRY pid=" + process.Id + " nativeError=" + error.NativeErrorCode);
                }
            }
            Thread.Sleep(250);
        }
        if (Process.GetProcessesByName("ChatGPT").Any(IsStoreCodexProcess))
            throw new InvalidOperationException("Codex has not exited; refusing a competing launch.");
        WriteLog(baseDirectory, "STOPPED existing Codex for requested restart");
    }

    private static void FocusProcess(Process process)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch
        {
        }
    }

    private static ProcessStartInfo CreateProxiedStartInfo(string path, string arguments, Uri proxyUri)
    {
        var proxy = proxyUri.GetLeftPart(UriPartial.Authority);
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppDomain.CurrentDomain.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            Arguments = arguments ?? string.Empty
        };
        // Build from the user's logon environment, not a watcher/agent parent's
        // transient Electron, Node, CODEX_HOME or runtime overrides.
        IntPtr block;
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!CreateEnvironmentBlock(out block, identity.Token, false))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the user logon environment.");
            try
            {
                startInfo.EnvironmentVariables.Clear();
                var cursor = block;
                while (true)
                {
                    var entry = Marshal.PtrToStringUni(cursor);
                    if (string.IsNullOrEmpty(entry)) break;
                    var separator = entry.IndexOf('=');
                    if (separator > 0) startInfo.EnvironmentVariables[entry.Substring(0, separator)] = entry.Substring(separator + 1);
                    cursor = IntPtr.Add(cursor, (entry.Length + 1) * 2);
                }
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        startInfo.EnvironmentVariables["HTTP_PROXY"] = proxy;
        startInfo.EnvironmentVariables["HTTPS_PROXY"] = proxy;
        startInfo.EnvironmentVariables["http_proxy"] = proxy;
        startInfo.EnvironmentVariables["https_proxy"] = proxy;
        startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
        startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1,::1";
        startInfo.EnvironmentVariables.Remove("ALL_PROXY");
        startInfo.EnvironmentVariables.Remove("all_proxy");
        startInfo.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
        return startInfo;
    }

    private static void LaunchAndVerifyCodex(string baseDirectory, string codexPath, Uri proxyUri)
    {
        // Do not count crashpad/GPU processes as a running application.
        if (FindRunningStoreCodex() == null && Process.GetProcessesByName("ChatGPT").Any(IsStoreCodexProcess))
            StopExistingCodex(baseDirectory);
        LaunchCodex(codexPath, proxyUri);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var root = FindRunningStoreCodex();
            if (root != null && root.MainWindowHandle != IntPtr.Zero)
            {
                Thread.Sleep(1000);
                root.Refresh();
                if (!root.HasExited && root.MainWindowHandle != IntPtr.Zero)
                {
                    FocusProcess(root);
                    if (File.Exists(Path.Combine(baseDirectory, "taskbar-grouping.enabled")))
                        TaskbarShortcuts.ConfigureRunningWindows(Path.Combine(baseDirectory, "CodexBridgeLauncher.exe"));
                    WriteLog(baseDirectory, "VERIFIED Codex window and live main process pid=" + root.Id);
                    return;
                }
            }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException("Codex launch was sent but no live main window appeared within 40 seconds. See the bridge and Codex logs.");
    }

    private static void LaunchCodex(string codexPath, Uri proxyUri)
    {
        var arguments = string.Format("--proxy-server={0}:{1} --proxy-bypass-list=<local>", proxyUri.Host, proxyUri.Port);
        if (Process.Start(CreateProxiedStartInfo(codexPath, arguments, proxyUri)) == null)
        {
            throw new InvalidOperationException("Could not start Microsoft Store Codex.");
        }
    }

    private static string GetSha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static void TryWriteFailure(string baseDirectory, Exception error)
    {
        try { WriteLog(baseDirectory, "FAILED " + error.ToString()); } catch { }
    }

    private static void WriteLog(string baseDirectory, string message)
    {
        var directory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "codex-bridge-launcher.log"), string.Format("{0:yyyy-MM-dd HH:mm:ss} {1}{2}", DateTime.Now, message, Environment.NewLine), Encoding.UTF8);
    }

    private sealed class Candidate
    {
        public Version Version;
        public string Path;
    }

    internal sealed class LauncherSettings
    {
        public bool RestartCodexOnLaunch;
        public string Language = "en-US";
        public bool AutoStartEnabled;
        public bool TaskbarOverrideEnabled;
        public bool FirstRun;
    }

    public sealed class ApprovedDocument
    {
        public int schemaVersion { get; set; }
        public List<ApprovedComponent> components { get; set; }
    }

    public sealed class ApprovedComponent
    {
        public string id { get; set; }
        public string displayName { get; set; }
        public string executablePath { get; set; }
        public string arguments { get; set; }
        public string sha256 { get; set; }
        public string approvedAt { get; set; }
    }

    public sealed class CockpitMarker
    {
        public int processId { get; set; }
        public long startTimeUtcTicks { get; set; }
        public string executablePath { get; set; }
        public string sha256 { get; set; }
    }
}
