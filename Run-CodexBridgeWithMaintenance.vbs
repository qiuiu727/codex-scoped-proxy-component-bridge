Set shell = CreateObject("Wscript.Shell")
shell.Run "pwsh -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""D:\CodexScripts\CodexScopedProxyComponentBridge\Start-CodexBridgeWithMaintenance.ps1""", 0, False
