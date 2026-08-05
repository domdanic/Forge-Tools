# Forge Tools

Forge Tools is a cross-platform Avalonia desktop host for streamer tools. The core owns discovery, installation, updates, settings, shared connections, and UI rendering; capabilities live in plugins.

## Features

- Uses an always-portable `data` folder beside the executable. Deleting the Forge folder removes the complete installation.
- Discovers plugins under `data\plugins`.
- Generates settings tabs from each plugin's `ui.json`.
- Persists settings per plugin without plugin-specific host code.
- Reads a curated `catalog.json` and identifies installs/updates.
- Downloads ZIP packages, verifies SHA-256, blocks ZIP path traversal, validates identity, and uses rollback-aware replacement.

## Core platform

- Avalonia 12 host for Windows and Linux.
- Authenticated OBS WebSocket 5 connection with automatic reconnect-on-start behavior.
- Twitch Device Code OAuth with validation, refresh, and revocation.
- Shared permission-gated OBS and Twitch connection interfaces for plugins.
- Plugin lifecycle (`Initialize`, `Start`, `Stop`, `Dispose`) with collectible load contexts and lifecycle-exception containment.
- Typed event bus for Forge, profile, OBS, and Twitch events.
- Stable category-change events that let plugins cooperate without depending directly on one another.
- Plugin minimum-Core requirements enforced in the catalogue, installer, and runtime.
- Per-plugin JSON settings export/import without credentials; Capture Switcher can remap OBS source names during import.
- Permission-gated Twitch EventSub chat/ad events plus chat sending for optional plugin commands and announcements.
- Enable, disable, permission review, update, and uninstall controls.
- Per-profile settings and permission grants.
- Sanitized local logging and credential-free diagnostics bundles.
- SHA-256 package verification, safe extraction, manifest validation, and optional trusted ECDSA publisher signatures.
- Core update download/staging plus a separate updater with backup and rollback.
- Windows DPAPI credential protection; Linux/macOS credentials remain session-only pending native vault adapters.

## Plugin development

Copy `templates/Forge.PluginTemplate`, change its ID and metadata, and implement `IForgePlugin`. Package it with:

```powershell
.\tools\pack-plugin.ps1 -PluginDirectory .\path\to\plugin\bin\Release\net10.0 -OutputPath .\plugin.zip
```

Plugins declare all requested permissions in `plugin.json`. Forge does not expose shared connection services until the user grants those permissions.

Forge's maintained plugins include Category Switcher, Capture Switcher, Timed Announcements, and Chat Games. Timed Announcements combines elapsed-time and chat-activity thresholds and uses Twitch's actual ad schedule/events. Chat Games provides a persistent virtual-points economy with independently configurable slots, roulette, blackjack, and cooperative boss battles.

Chat Games only awards passive points to accounts it has actually observed speaking in chat recently; Twitch does not provide a complete reliable viewer list. Its points have no cash value. Plugin configuration is included in Forge's settings export, while live balances and timer state remain portable in the profile's plugin-data folder and survive updates.

Declarative plugins contain no executable code and are the safe default for open community distribution. Executable .NET plugins currently run in-process and must be treated as trusted code: service permissions prevent accidental API access but cannot sandbox arbitrary operating-system calls. A future out-of-process worker is required before Forge should advertise unreviewed executable plugins as isolated.

## Run

```powershell
dotnet run --project src\Forge.App\Forge.App.csproj
```

The host targets .NET 10 with Avalonia 12 and is designed for Windows, Linux, and macOS. The plugin SDK and declarative UI contract are host-neutral.

## Package contract

A plugin ZIP has `plugin.json` and its declared UI file at the archive root. Forge Plugin API 2 supports lifecycle plugins, shared permission-gated connections, typed events, and declarative controls including Forge's guided mapping editors.

The bundled catalogue points to `catalog/catalog.json` in this repository and checks it dynamically for available plugins and updates.

## Portable storage

Forge requires its folder to be writable and creates `data\plugins`, `data\settings`, `data\profiles`, `data\cache`, `data\logs`, and `data\credentials`. The entire folder can be copied to another drive or PC. Future login secrets will be encrypted with device-bound Windows protection, so copied integrations will ask the user to reconnect. Forge does not write application data to AppData or the registry.

OBS connection settings include host, WebSocket port, password, and automatic connection. The password is masked and excluded from portable JSON settings; persistent storage will use the device-bound credential service when the live OBS WebSocket client is implemented.

Twitch uses the OAuth Device Code flow with Forge's public client ID. Forge opens Twitch's activation page, polls for approval, validates the resulting user token, refreshes it when necessary, and revokes it on sign-out. Windows token records are encrypted for the current Windows user inside `data\credentials`; Linux and macOS currently keep Twitch tokens only for the running session until their native credential-vault adapters are implemented.

Portable runtime data and common secret-file extensions are excluded by `.gitignore`. Twitch's Client ID is public; access tokens, refresh tokens, OBS passwords, and files under `data` must never be committed.
