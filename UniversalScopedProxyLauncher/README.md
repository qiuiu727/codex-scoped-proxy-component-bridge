# Scoped Proxy Launcher

Windows launcher for starting selected local applications with a **local HTTP proxy**. It is designed for people who use the official ChatGPT/Codex desktop app and optional helper applications such as a Codex quota or balance floating window.

## Important boundary

This is **not** ChatGPT, Codex, Clash, a VPN, or a proxy service. Install the official ChatGPT/Codex client yourself and run a local HTTP proxy first. This project only starts the applications you select with `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, and (for Microsoft Store Codex) Chromium proxy arguments.

It never changes the Windows system proxy, does not enable TUN mode, does not manage Cockpit, does not switch accounts, and does not read or upload chat data, tokens, or proxy subscriptions.

## Installation

Download `ScopedProxyLauncher-Setup.exe` from Releases. The installer lets you choose the program folder, then asks whether to make a desktop shortcut. It always creates Start-menu entries for the tray helper, launching, and configuration.

## Tray controls

Start **Proxy launcher tray** from the Start menu. It shows a notification-area icon; left-click opens settings. Right-click can start configured applications or check extensions for updates.

- **Automatic startup** is opt-in. Select `clash-verge.exe`, then enable it. At Windows logon the task starts Clash if needed, waits for a real local `CONNECT` check, then launches every configured target through the scoped proxy.
- **Replace matching pinned ChatGPT/Codex taskbar shortcut** is opt-in. Matching shortcut files are backed up under `%LOCALAPPDATA%\ScopedProxyLauncherBackups\Taskbar`. Turning the setting off restores a backed-up shortcut only if it still points at this launcher.
- **Check app updates / approve** compares an added file's SHA-256 with the approved version. If a quota widget or extension updated, the tray asks before the changed file can launch with the proxy environment. It does not silently trust a replacement file.

Open **Configure proxy launcher** to enter your loopback HTTP proxy address and add applications (`.exe`, `.cmd`, `.bat`, or `.ps1`) such as your quota/balance floating window. The installer includes the Microsoft Store Codex desktop target; additional examples are in `proxy-launcher.example.json`. The launcher tests `CONNECT chatgpt.com:443` before it starts a target.

## Compatibility

Applications must respect standard HTTP proxy environment variables. The official Microsoft Store Codex target is discovered at runtime and receives both environment variables and Chromium proxy arguments. Other applications that ignore proxy environment variables cannot be forced through a proxy by this tool.

## Build from source

Requires Windows PowerShell 7 and the .NET Framework C# compiler:

```powershell
pwsh -NoProfile -File .\Build-Release.ps1
```

The installer is written to `dist\ScopedProxyLauncher-Setup.exe`.
