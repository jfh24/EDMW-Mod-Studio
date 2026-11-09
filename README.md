# EDMW Mod Studio

EDMW Mod Studio is a Windows tool for creating maps and civilization edits for **Empires: Dawn of the Modern World**. It includes a visual Map Designer, a Civilization Editor, LAN sharing, online friends, and game matchmaking.

## Download

Version **1.4.0** is the current stable release.

[Download EDMW Mod Studio for Windows](https://github.com/jfh24/EDMW-Mod-Studio/releases/download/v1.4.0/EDMW.Mod.Studio.exe) · [Release notes](docs/releases/v1.4.0.md) · [Changelog](CHANGELOG.md)

The self-contained executable runs on 64-bit Windows 10 or later. A compatible game installation is required for game data, installation, and gameplay features.

## Features

- Paint terrain, elevation, ramps, resources, wildlife, and player starts; generate random or seeded maps.
- Use standard brushes or any of the 50 US state silhouettes.
- Edit supported civilization data, assets, and custom effects.
- Preview game models, textures, player colors, and animation clips.
- Collaborate on maps and share map or civilization packages over a LAN or with online friends.
- Manage installed content from Home, including sharing, merge/unmerge, and uninstall actions.
- Create public or friends-only matchmaking rooms and invite accepted friends.
- Route game traffic over direct ICE or the authenticated WSS relay; choose transports and optional gameplay traffic logging in Matchmaking settings.

## Build from source

Requirements: 64-bit Windows, .NET SDK 9.0 or later, PowerShell, Git, and Visual Studio or Build Tools with the C++ workload and Windows SDK.

```powershell
git clone https://github.com/jfh24/EDMW-Mod-Studio.git
Set-Location EDMW-Mod-Studio
.\build.ps1
```

The self-contained application is written to `dist/EDMW Mod Studio.exe`. See [development](docs/development.md) for the build layout and [dist/README.md](dist/README.md) for distribution details.

## Documentation

- [User guide](docs/user-guide.md)
- [Online sharing and relay setup](docs/online-sharing.md)
- [Online protocol](docs/online-protocol.md)
- [Release notes](docs/releases/v1.4.0.md)
- [Geographic and artwork credits](docs/credits.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

The relay is a separate Node.js service; see the [deployment guide](docs/online-sharing.md#host-a-relay). Game files and generated executables are not included in the source repository. The game-facing generator and multiplayer bridge are x86 to match the supported game process; the editor is x64.
