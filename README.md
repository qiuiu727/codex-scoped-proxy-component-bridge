# Codex Scoped Proxy Component Bridge for Windows

A standalone second-generation launcher for Microsoft Store Codex on Windows. It starts Codex through an already-running local HTTP proxy and provides a consent gate for companion tools that need the same **network route**.

It is independent from the first-generation launcher. It does not include a proxy core, Clash/Mihomo configuration, subscription, node, account credential, or Codex session data.

## Security model

1. A companion tool calls `Request-CodexScopedProxyComponent.ps1` with its executable path and optional arguments.
2. It can request an immediate Windows **Yes/No** approval dialog. If Windows cannot show the dialog, the bridge records only a **pending request**. Pending requests cannot start and receive no proxy settings.
3. A pending request is shown at the next bridge launch with its exact executable path, arguments, and SHA-256 hash. You must click **Yes** to approve it.
4. Only approved files whose SHA-256 still matches are launched. They receive only `HTTP_PROXY`, `HTTPS_PROXY`, and `NO_PROXY` environment variables for a loopback HTTP proxy.
5. Codex cookies, ChatGPT login tokens, and account/session files are never read, copied, or passed to a component.

Clicking **No** discards that request. An executable changed after it requests access is also rejected rather than launched.

## Requirements

- Windows PowerShell 5.1 or later on Windows.
- Microsoft Store Codex installed and signed in.
- A local HTTP proxy core already running and able to handle HTTPS `CONNECT` requests.

The proxy must be bound to a loopback address. The bridge does not alter Windows system proxy, TUN, routing, DNS, firewall settings, Clash configuration, or subscriptions.

## Install

Start your own proxy core, then run from this directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-CodexScopedProxyComponentBridge.ps1
```

The installer creates a Start Menu shortcut named **Codex Scoped Proxy Component Bridge**. Pin that shortcut to the taskbar if wanted. It uses the Codex icon and, when Codex is already open, focuses that window instead of restarting it.

## Request a component

After installation, run the installed request script. A request is not an approval and does not start the tool.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -DisplayName 'Example companion'
```

Optional command-line arguments are stored exactly as shown in the approval dialog:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -Arguments '--background' -DisplayName 'Example companion'
```

Launch the bridge afterward. The consent dialog appears **before** the requested component can receive the proxy environment or start.

To ask for approval immediately instead of waiting for the next bridge launch, add `-PromptNow`. This still does not start or alter any running program:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Request-CodexScopedProxyComponent.ps1 -ExecutablePath .\your-component.exe -DisplayName 'Example companion' -PromptNow
```

## Review or remove approval

List approved components:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Manage-CodexScopedProxyComponents.ps1 -List
```

Remove a component by its listed `id`; removal requires typing `REMOVE`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Manage-CodexScopedProxyComponents.ps1 -Remove -ComponentId '<ID>'
```

## Limits

This bridge can supply the same local proxy route to a separate process. It cannot and does not make that process "logged in as Codex." A component with its own service authentication must complete that authentication itself.

## Uninstall

Run the installed uninstaller:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\CodexScopedProxyComponentBridge\Uninstall-CodexScopedProxyComponentBridge.ps1"
```

It removes only the bridge, its Start Menu shortcut, logs, and local approval records. It does not remove Codex, a proxy core, subscriptions, TUN, or system-proxy settings.

## License

MIT. See [LICENSE](LICENSE).
