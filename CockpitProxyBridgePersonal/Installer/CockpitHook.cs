using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Collections.Generic;

internal static class CockpitHook
{
    internal static string ConfigPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_cockpit", "config.json"); } }
    internal static readonly string[] Keys = { "codex_app_path", "codex_launch_on_switch", "codex_specified_app_path", "codex_restart_specified_app_on_switch" };
    internal static string Rewrite(string text, string hook)
    {
        var json = new JavaScriptSerializer();
        var data = json.Deserialize<Dictionary<string, object>>(text);
        object[] values = { "", false, hook, true };
        for (int i = 0; i < Keys.Length; i++)
        {
            if (!data.ContainsKey(Keys[i])) throw new InvalidOperationException("Unsupported Cockpit configuration: " + Keys[i]);
            text = Replace(text, Keys[i], json.Serialize(values[i]));
        }
        json.DeserializeObject(text);
        return text;
    }
    private static string Replace(string text, string key, string value)
    {
        var pattern = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|true|false|null)");
        if (pattern.Matches(text).Count != 1) throw new InvalidOperationException("Ambiguous Cockpit setting: " + key);
        return pattern.Replace(text, m => m.Groups[1].Value + value, 1);
    }
    internal static void Enable(string directory)
    {
        string hook = Path.Combine(directory, "CodexCockpitHook.exe");
        string original = File.ReadAllText(ConfigPath);
        string updated = Rewrite(original, hook);
        string backup = Path.Combine(directory, "backups", "cockpit-hook-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        File.Copy(ConfigPath, Path.Combine(backup, "config.json"));
        string rollback = Path.Combine(directory, "cockpit-hook-original.json");
        if (!File.Exists(rollback)) File.WriteAllText(rollback, original, new UTF8Encoding(false));
        File.Copy(Path.Combine(directory, "CodexBridgeLauncher.exe"), hook, true);
        if (File.ReadAllText(ConfigPath) != original) throw new IOException("Cockpit settings changed concurrently. Retry after closing its settings page.");
        File.WriteAllText(ConfigPath, updated, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "cockpit-direct-hook.enabled"), "1");
        WatcherTask.Remove();
    }
    internal static void Restore(string directory)
    {
        string backup = Path.Combine(directory, "cockpit-hook-original.json");
        if (!File.Exists(backup) || !File.Exists(ConfigPath)) return;
        var json = new JavaScriptSerializer();
        string current = File.ReadAllText(ConfigPath);
        var actual = json.Deserialize<Dictionary<string, object>>(current);
        string hook = Path.Combine(directory, "CodexCockpitHook.exe");
        if (!actual.ContainsKey(Keys[2]) || !string.Equals(Convert.ToString(actual[Keys[2]]), hook, StringComparison.OrdinalIgnoreCase)) return;
        var prior = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(backup));
        foreach (string key in Keys) current = Replace(current, key, json.Serialize(prior[key]));
        File.WriteAllText(ConfigPath, current, new UTF8Encoding(false));
    }
}
