Option Explicit

Dim shell, fso, scriptRoot, bridgeScript, command, exitCode
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptRoot = fso.GetParentFolderName(WScript.ScriptFullName)
bridgeScript = scriptRoot & "\Start-CodexWithApprovedComponents.ps1"

If Not fso.FileExists(bridgeScript) Then
    MsgBox "Codex component bridge could not start because its launcher file is missing:" & vbCrLf & bridgeScript, 16, "Codex Scoped Proxy Component Bridge"
    WScript.Quit 2
End If

command = "powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & bridgeScript & """"
exitCode = shell.Run(command, 0, True)

If exitCode <> 0 Then
    MsgBox "Codex component bridge could not complete. See the local log:" & vbCrLf & scriptRoot & "\logs\component-bridge.log", 16, "Codex Scoped Proxy Component Bridge"
End If
