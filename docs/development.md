# Development

EDMW Mod Studio is a Windows Forms application with native game components and a separate Node.js relay. This guide covers source builds and the main project areas.

## Build

Requirements:

- 64-bit Windows 10 or later.
- .NET SDK 9.0 or later.
- Visual Studio or Build Tools with the C++ workload and Windows SDK.
- PowerShell and Git.

From the repository root, run:

```powershell
.\build.ps1
```

The script builds the x86 game components and installer templates, embeds them in the editor, then publishes the self-contained x64 application to `dist/EDMW Mod Studio.exe`. The native build steps run their regression suites.

## Source layout

| Path | Purpose |
| --- | --- |
| `EDMWMapDesigner/Models`, `Analysis`, `Generation` | Map projects, terrain analysis, generation, and export. |
| `EDMWMapDesigner/Civilizations` | Civilization data, models, projects, and package installation. |
| `EDMWMapDesigner/Collaboration` | Shared map editing. |
| `EDMWMapDesigner/Networking` | LAN transfer, friends, matchmaking, and relay clients. |
| `EDMWMapDesigner/UI` | Home, editors, and dialogs. |
| `native/EDMWAnchored` | Map generator loaded by the game. |
| `native/EDMWEffects` | Optional civilization runtime effects. |
| `native/EDMWRelayBridge` | Temporary multiplayer provider bridge. |
| `relay` | HTTP/WebSocket service for friends, rooms, and online traffic. |
| `EDMWMapDesigner/Assets` | Embedded resources and attributed geographic data. |

The `EDMWMapDesigner` directory and namespace remain for compatibility. The public application and executable name is **EDMW Mod Studio**.

## Online game networking

The client connects to the authenticated relay for presence, rooms, and ICE signaling. Game traffic uses direct ICE data channels when enabled and available. The room-scoped WSS relay can carry UDP, TCP, and session-control frames when direct gameplay transport is unavailable or disabled. The Matchmaking settings control these gameplay paths and optional text logging. See the [protocol](online-protocol.md) for frame routing and the [online guide](online-sharing.md) for setup.

## Game compatibility

Game integration requires a separately installed, supported 32-bit copy of Empires: Dawn of the Modern World. The editor and installer applications target x64 Windows; the game-facing generator and provider bridge are x86. The bridge validates supported game/provider identities before loading and is extracted for the game process only. It does not replace installed game executables or DLLs.

Civilization installation accepts only supported mapped fields and data. Unsupported authored changes are blocked from export. Map generation uses the game's original generator interface and templates.

## Documentation

- [User guide](user-guide.md)
- [Online sharing and relay setup](online-sharing.md)
- [Online protocol](online-protocol.md)
- [Geographic and artwork credits](credits.md)
- [Release notes](releases/v1.4.0.md)
