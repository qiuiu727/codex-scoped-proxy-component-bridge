using System;
using System.IO;
using System.Security.Principal;

internal static class WatcherTask
{
    internal const string Name = "CodexProxyBridgeWatcher";
    internal static void Ensure(string launcher)
    {
        if (File.Exists(Path.Combine(Path.GetDirectoryName(launcher), "cockpit-direct-hook.enabled"))) return;
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic task = service.NewTask(0);
        string user = WindowsIdentity.GetCurrent().Name;
        task.RegistrationInfo.Description = "Keep the Codex proxy bridge watcher independent of Codex app restarts.";
        task.Principal.UserId = user;
        task.Principal.LogonType = 3;
        task.Principal.RunLevel = 0;
        task.Settings.Enabled = true;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.MultipleInstances = 2;
        task.Settings.RestartInterval = "PT1M";
        task.Settings.RestartCount = 3;
        dynamic trigger = task.Triggers.Create(9);
        trigger.UserId = user;
        dynamic action = task.Actions.Create(0);
        action.Path = launcher;
        action.Arguments = "--watch";
        action.WorkingDirectory = Path.GetDirectoryName(launcher);
        dynamic registered = folder.RegisterTaskDefinition(Name, task, 6, user, null, 3);
        if ((int)registered.State != 4) registered.Run(null);
    }
    internal static void Remove()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(Name, 0); }
        catch (Exception e)
        {
            if (e.HResult != unchecked((int)0x80070002)) throw;
        }
    }
}
