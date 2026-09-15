# Codex Scoped Proxy Component Bridge for Windows

A standalone second-generation launcher for Microsoft Store Codex on Windows. It starts Codex through an already-running local HTTP proxy and provides a consent gate for companion tools that need the same **network route**.

## Choose the correct installer

| Download | Who it is for | Cockpit support |
| --- | --- | --- |
| **Scoped Proxy Launcher** (`universal-v1.1.0`) | Most people with one ChatGPT/Codex account who want Codex and optional balance/quota helpers to use an existing loopback HTTP proxy. | No. It deliberately does not change, start, or configure Cockpit. |
| **Codex Proxy Bridge Personal** (`cockpit-v1.2.7`) | People who actively use Cockpit account switching and need switches to start Codex through the scoped proxy. | Yes. It has an opt-in Cockpit direct hook, keeps the ordered Clash-to-Cockpit-to-Codex login chain, and includes the same tray controls. |

Both installers require the official ChatGPT/Codex desktop client and an already-running local HTTP proxy. Neither installer includes proxy nodes, subscriptions, credentials, system-proxy changes, or TUN mode.

## Universal installer for single-account users

[`UniversalScopedProxyLauncher`](UniversalScopedProxyLauncher) is the standalone installer version for people who simply need to start the official ChatGPT/Codex desktop application and a quota/balance floating window through their own local HTTP proxy. It deliberately excludes Cockpit account switching, Cockpit proxy hooks, and Cockpit window-pinning behavior. It does not replace ChatGPT/Codex: the official desktop client remains required.

Download `ScopedProxyLauncher-Setup.exe` from the corresponding GitHub Release. The installer asks for a program installation folder, then asks whether to create a desktop shortcut. It includes a notification-area tray helper with settings, ordered opt-in Clash-to-Codex autostart, reversible taskbar replacement, and manual file-update approval for configured extensions.

The installer creates one main launcher in the Start menu, with an optional matching Desktop shortcut. It separately asks whether to add a **Restart Codex** entry; it is absent unless chosen. First opening the main launcher displays Settings automatically. Afterwards it launches the configured apps and Settings are available only from the notification-area icon.

It is independent from the first-generation launcher. It does not include a proxy core, Clash/Mihomo configuration, subscription, node, account credential, or Codex session data.

## Security model

1. A companion tool calls `Request-CodexScopedProxyComponent.ps1` with its executable path and optional arguments.
2. It can request an immediate Windows **Yes/No** approval dialog. If Windows cannot show the dialog, the bridge records only a **pending request**. Pending requests cannot start and receive no proxy settings.
3. A pending request is shown at the next bridge launch with its exact executable path, arguments, and SHA-256 hash. You must click **Yes** to approve it.
4. Only approved files whose SHA-256 still matches are launched. They receive only `HTTP_PROXY`, `HTTPS_PROXY`, and `NO_PROXY` environment variables for a loopback HTTP proxy.
5. Codex cookies, ChatGPT login tokens, and account/session files are never read, copied, or passed to a component.

Clicking **No** discards that request. An executable changed after it requests access is also rejected rather than launched.

## Requirements

- PowerShell 7 or later on Windows.
- Microsoft Store Codex installed and signed in.
- A local HTTP proxy core already running and able to handle HTTPS `CONNECT` requests.

The proxy must be bound to a loopback address. The bridge does not alter Windows system proxy, TUN, routing, DNS, firewall settings, Clash configuration, or subscriptions.

## Install

Start your own proxy core, then run from this directory:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Install-CodexScopedProxyComponentBridge.ps1
```

The installer creates a Start Menu shortcut named **Codex Scoped Proxy Component Bridge**. Pin that shortcut to the taskbar if wanted. It uses the Codex icon and, when Codex is already open, focuses that window instead of restarting it.

## Update maintenance

The normal launcher runs `Update-CodexCockpitScopedProxyBridge.ps1` before starting Codex. It rediscovers the current Microsoft Store Codex package after an update. If an approved companion application updates, the bridge asks for a fresh Yes/No approval for its new SHA-256 hash; it never silently trusts a changed executable.

Run the maintenance-aware launcher directly with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Start-CodexBridgeWithMaintenance.ps1
```

## Request a component

After installation, run the installed request script. A request is not an approval and does not start the tool.

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -DisplayName 'Example companion'
```

Optional command-line arguments are stored exactly as shown in the approval dialog:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -Arguments '--background' -DisplayName 'Example companion'
```

Launch the bridge afterward. The consent dialog appears **before** the requested component can receive the proxy environment or start.

To ask for approval immediately instead of waiting for the next bridge launch, add `-PromptNow`. This still does not start or alter any running program:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -DisplayName 'Example companion' -PromptNow
```

## Review or remove approval

List approved components:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Manage-CodexScopedProxyComponents.ps1 -List
```

Remove a component by its listed `id`; removal requires typing `REMOVE`:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\Manage-CodexScopedProxyComponents.ps1 -Remove -ComponentId '<ID>'
```

## Limits

This bridge can supply the same local proxy route to a separate process. It cannot and does not make that process "logged in as Codex." A component with its own service authentication must complete that authentication itself.

## Uninstall

Run the installed uninstaller:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\CodexScopedProxyComponentBridge\Uninstall-CodexScopedProxyComponentBridge.ps1"
```

It removes only the bridge, its Start Menu shortcut, logs, and local approval records. It does not remove Codex, a proxy core, subscriptions, TUN, or system-proxy settings.

## License

MIT. See [LICENSE](LICENSE).
