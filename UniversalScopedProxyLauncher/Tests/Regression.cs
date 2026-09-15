using System;
using System.Diagnostics;
using System.Reflection;

internal static class Regression
{
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var program = assembly.GetType("Program", true);
            var targetType = assembly.GetType("LaunchTarget", true);
            var target = Activator.CreateInstance(targetType);
            targetType.GetProperty("Name").SetValue(target, "Test", null);
            targetType.GetProperty("Kind").SetValue(target, "file", null);
            targetType.GetProperty("Path").SetValue(target, Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\notepad.exe", null);
            var info = (ProcessStartInfo)program.GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { target, new Uri("http://127.0.0.1:7890") });
            Assert(!info.UseShellExecute && !info.CreateNoWindow, "visible direct launch");
            Assert(info.EnvironmentVariables["HTTP_PROXY"] == "http://127.0.0.1:7890", "HTTP proxy environment");
            Assert(info.EnvironmentVariables["HTTPS_PROXY"] == "http://127.0.0.1:7890", "HTTPS proxy environment");
            Assert(info.EnvironmentVariables["NO_PROXY"].Contains("127.0.0.1"), "local bypass");
            Assert(info.EnvironmentVariables["ELECTRON_RUN_AS_NODE"] == null, "Electron isolation");
            string hash = (string)program.GetMethod("ComputeFileHash", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { targetType.GetProperty("Path").GetValue(target, null) });
            targetType.GetProperty("ApprovedHash").SetValue(target, hash, null);
            program.GetMethod("ValidateTargetApproval", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { target });
            Assert(hash.Length == 64, "update approval hash");
            Console.WriteLine("PASS: direct target launch, scoped proxy environment, local bypass, Electron isolation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}
