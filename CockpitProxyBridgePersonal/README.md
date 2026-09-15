# Codex Proxy Bridge Personal

This is the **Cockpit edition** of the Windows scoped proxy launcher. Download `CodexProxyBridge-Setup.exe` from the `cockpit-v1.2.7` GitHub Release only if you use Cockpit account switching.

## What it adds

- Optional Cockpit direct hook: after an account switch, Cockpit starts Codex through this bridge instead of its ordinary Store activation path.
- Ordered user-logon startup: the existing Clash -> Cockpit -> Codex chain can be enabled or disabled from the notification-area tray settings.
- Optional reversible ChatGPT/Codex taskbar shortcut replacement.
- Hash-approval checks for Cockpit and separately approved balance/quota helper applications.
- A tray icon: left-click it for settings; its menu can launch/restart Codex through the proxy or manually check changed helper programs.

It does not change Windows system proxy, TUN, nodes, subscriptions, ChatGPT login data, account-switch chat-sync settings, or any proxy credentials.

## Requirements

- Windows 10/11 and PowerShell 7.
- Official Microsoft Store ChatGPT/Codex installed and signed in.
- An existing loopback HTTP proxy which can make an HTTPS CONNECT tunnel.
- Cockpit installed only when you intend to use the Cockpit hook.

## Installation and use

Run the installer. On a new installation it offers the Cockpit integration and taskbar replacement choices. On an update it preserves an already enabled Cockpit direct hook, ordered-startup task, taskbar setting, local proxy address, logs, and approved components.

After installation, use the **Codex Proxy Bridge** Start-menu group:

- **Start Codex**: starts or focuses Codex through the proxy.
- **Restart Codex**: restarts Codex through the proxy.
- **Proxy Bridge Tray**: keeps the notification-area controls available.

Do not put `CodexCockpitHook.exe` in Cockpit's ordinary Codex app-path field. The setup configures Cockpit's specified-application hook instead. After enabling or updating that hook, exit and reopen Cockpit so it reloads its settings.

## Build

Run `pwsh -NoProfile -File .\Build-Release.ps1`. The installer is written to `dist\CodexProxyBridge-Setup.exe`.

## Privacy and source safety

The repository intentionally excludes per-machine `config.json`, Cockpit configuration backups, approval records, logs, and proxy credentials. Do not commit or publish them.
