# Development and verification

EDMW Mod Studio version 1.0.0 consists of a Windows Forms editor, two installer templates, and a native map generator. This document describes the source as shipped; it does not claim that every verification has been run on every game installation.

## Build pipeline

Run the complete build from the repository root:

```powershell
.\build.ps1
```

The prerequisites and clean-clone commands are in the [README](../README.md#build-from-source).

The production build follows these dependencies:

1. `native/EDMWAnchored/build-native.cmd` discovers Visual Studio through `vswhere` and initializes the x86 compiler environment.
2. It compiles `ResourceAlignment.cpp`, builds and runs `ResourceAlignmentTests.exe`, then produces `EDMWAnchored.dll`. MSVC warnings are treated as errors.
3. The map and civilization installer projects are published as self-contained, single-file Windows executables. Their generated templates are copied into the editor's assets for embedding.
4. The editor embeds the native generator, installer templates, and UI artwork.
5. The editor is published for `win-x64` and the deliverable is copied to `dist/EDMW Mod Studio.exe`.

The .NET projects target `net9.0-windows`, enable nullable analysis, and treat warnings as errors. The editor assembly name is `EDMW Mod Studio`. The native generator is a 32-bit DLL because the game loads it into an x86 process.

The generated executable in `dist/`, staging/publish directories, `bin/`, `obj/`, native build directories, and generated installer executables are build products. Keep them out of source commits. `dist/README.md` is tracked and explains release downloads and local output. The complete build recreates the embedded binaries from their included source.

Use the full build after native or shared installer-domain changes. A standalone editor build cannot resolve its embedded generated resources in a fresh checkout until they have been produced.

## Architecture

| Source area | Responsibility |
| --- | --- |
| `EDMWMapDesigner/Models` | Map project persistence, terrain kinds, semantic anchors, resources, and player-start markers. |
| `EDMWMapDesigner/Analysis` | Land/water topology and semantic anchor analysis used by generation and warnings. |
| `EDMWMapDesigner/Generation` | Random and seeded terrain, original stock-template import, RMV generation, native sidecars, map packages, installation helpers, and map LAN transfer. |
| `EDMWMapDesigner/Civilizations` | Civilization project/history/schema, original game catalog and asset reading, verified native bindings, model decoding, package review, installation, rollback, and ownership. |
| `EDMWMapDesigner/Collaboration` | Host-ordered map deltas, project synchronization, collaborator presence, and cursors. |
| `EDMWMapDesigner/Networking` | Shared LAN package transport, online relay client, friends models, protected device preferences, and desktop/relay verification. |
| `EDMWMapDesigner/UI` | Home, both workspaces, canvas, themed controls, model viewport, review dialogs, and existing verification entry points. |
| `EDMWMapDesigner/Assets` | Embedded art and retained, attributed geography sources for offline mask regeneration. |
| `EDMWMultiplayerInstaller` | Map package executable template, package integrity checks, and installation UI. |
| `EDMWCivilizationInstaller` | Civilization package executable template and UI; links the editor's package and game-installation domain code. |
| `native/EDMWAnchored` | Game-facing generator exports, stock generator integration, authored terrain/elevation/marker behavior, and resource alignment. |
| `relay` | Node.js HTTP/WebSocket service for authenticated identities, accepted friendships, map sessions, and temporary export delivery. |
| `render.yaml` | Render deployment blueprint with one Node service and durable identity storage. |

The legacy `EDMWMapDesigner` directory and namespace are intentional. The rebrand changes the public product and executable name while retaining resource identities, persisted formats, and existing project paths.

### Map flow

An editable `MapProject` is analyzed and rendered using the selected game's original random-map templates. Export produces an RMV script plus an `.edmwa` sidecar carrying authored terrain and markers. A map-specific generator DLL is installed alongside the game; original game DLLs and stock maps are preserved.

A LAN map package carries a generator, script, sidecar, and preview. The recipient validates paths, sizes, payload identity, and the generator against the copy embedded in its own editor before installation. Use matching editor versions on the participating machines.

### Civilization flow

The editor reads game database records, localized names, producer and prerequisite links, archives, models, textures, and animations. Selecting stock content is read-only. Authoring creates saved overrides in `.edmwciv` projects.

Export reviews supported targets and creates a verified package. Installation tracks ownership and backups; uninstall checks owned bytes before restoring them. Different, nonoverlapping fields can be independently owned. Overlapping edits or model replacements must have their owning package removed first. Some progression operations own entire affected tables, so their conflict scope is broader than an individual field.

The LAN recipient rebuilds its review using local game data. Received packages are installed through the package system rather than by executing a received installer.

### Online friends and sharing

The desktop authenticates a device identity over HTTPS/WSS and stores its credential using Windows DPAPI. Friends require a request and acceptance. The relay controls presence, invitation and export access, room membership, and ordered map revisions. Online map editing uses the existing `MapDelta` and `MapProject` formats. Public connections require TLS; unencrypted loopback endpoints are available for local verification.

Online exports reuse map archives and `CivilizationInstallerPackageCodec` payloads. The relay checks upload length and SHA-256, permits downloads only by the intended accepted friend, and forwards recipient decisions. The desktop then applies the existing package validation, review, and tracked installation flow. Received installer executables are never run. Friends and identities are durable; rooms and package offers are transient.

See [online setup and operations](online-sharing.md) and [the wire protocol](online-protocol.md). The relay is a separate service and is not embedded in the Windows executable. Source builds include the online feature; the existing v1.0.0 release does not.

## Game compatibility

The application needs a game folder containing `Empires_DMW.exe`, original random-map scripts, and the database/assets required by the selected workspace.

Civilization installation checks this executable SHA-256, declared in `CivilizationGameInstaller.SupportedExecutableSha256`:

```text
9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3
```

It also validates source identities and semantic ranges. The executable itself is not patched. A different executable or incompatible data may be readable in parts of the editor but is not thereby approved for installation. Unsupported authored changes block export.

Map generation also depends on the game's native generator interface and original templates. The editor's offline checks and reports are evidence for their tested paths, not a guarantee of universal game-version support or completed multiplayer validation.

## Existing verification commands

The command-line switches below are implemented in `EDMWMapDesigner/Program.cs`. Verification commands return exit code `0` on success and `1` on failure. UI checks require Windows and a usable desktop session. Keep outputs in a scratch directory outside the game folder.

After the complete build, capture a self-test result in PowerShell:

```powershell
$app = Join-Path (Get-Location) 'dist\EDMW Mod Studio.exe'
$checks = Join-Path (Get-Location) '.verification'
New-Item -ItemType Directory -Path $checks -Force | Out-Null

$process = Start-Process -FilePath $app -ArgumentList '--self-test' `
    -WindowStyle Hidden -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $checks 'self-test.log') `
    -RedirectStandardError (Join-Path $checks 'self-test-errors.log')

Get-Content -LiteralPath (Join-Path $checks 'self-test.log')
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $checks 'self-test-errors.log')
    throw "Self-test failed with exit code $($process.ExitCode)"
}
```

The self-test exercises project persistence and identity, generated scripts and sidecars, seeded/random maps, state layers, elevation/cliff policies, fauna capability, and loopback collaboration. Native resource-alignment checks already run during the native build.

| Switch and arguments | Coverage / output |
| --- | --- |
| `--self-test` | Core map and collaboration checks; does not require an installed game. |
| `--verify-online <loopback-relay-url>` | Actual C# clients against Node: authentication, friendship permissions, map invitations, full-sized map masks, simultaneous edits, cursors, export bytes and decision status, reconnect, and revocation. Requires an isolated local relay; does not install game data. |
| `--verify-online-ui <output-folder>` | Offscreen Friends and recipient picker layout, toolbar availability, and pending local stroke preservation across acknowledgements; no network connection or account changes. |
| `--verify-civilization-navigation` | Catalog selection shortcuts, paging, and resizing; does not require an installed game. |
| `--verify-stock-profiles <stock-scripts-folder>` | Original map-template/resource policy import across 13 profiles, five sizes, and 2–8-player branches. |
| `--verify-map-lan <game-folder> <output-folder>` | Map export integration, preview redaction, approval and sender dialogs, and map transfer checks. |
| `--verify-civilization-sources <game-folder> <output-folder>` | Read-only source joins, rosters, and source-data reports. |
| `--verify-civilization-editor <game-folder> <output-folder>` | Civilization project/workspace acceptance, game imports, supported overrides, and model fixtures. |
| `--verify-civilization-models <game-folder> <output-folder>` | Model/texture decoding, software rasterizer, animation playback, and offscreen controls; no screenshots. |
| `--verify-civilization-repaint <game-folder> <output-folder>` | Offscreen native paint-message and layout diagnostics; writes `civilization-repaint.json`. |
| `--verify-civilization-packages <game-folder> <output-folder>` | Installation/uninstall, ownership, recovery, and adversarial package checks using isolated fixtures. |
| `--verify-civilization-lan <game-folder> <output-folder>` | Civilization approval, local export, transport, and package behavior. |
| `--verify-age-progression <game-folder> <output-folder>` | Progression editing, membership, packages, and LAN behavior using isolated sender/recipient copies. |
| `--verify-age-progression-layout <game-folder> <output-folder>` | Progression layout checks and captures with game catalog data. |

Pass the folder `Data\Random Map Scripts` within the installed game for `<stock-scripts-folder>`. Quote any paths containing spaces. For example, an argument string for the same `Start-Process` pattern is:

```powershell
$arguments = '--verify-stock-profiles "C:\Games\Empires DMW\Data\Random Map Scripts"'
```

Game-dependent fixture outputs can contain copied game data. They are local verification products and are ignored by Git; do not distribute them with the source.

The civilization installer also provides `--verify-layout <game-folder>` and `--verify-package <game-folder>`. The latter reviews the package appended to a generated installer executable without installing it; a bare template does not contain an authored package.

These checks do not replace in-game visual inspection or a real multiplayer session with matching installed gameplay data.

For the Node relay, run `npm ci` and `npm test` inside `relay`. The tests use temporary storage and local listeners. For desktop/relay compatibility, run `tools/Verify-Online.ps1` from the repository root after generating the embedded resources with `build.ps1`. It starts an isolated loopback relay, builds the editor, runs `--verify-online`, and stops the relay. Test identities and output stay under the ignored `.verification` directory. Document check results and remaining limitations with the GitHub change; exclude credentials, local relay data, game fixtures, and generated executables from commits.

## Maintenance and evidence commands

These entry points perform work rather than merely verifying:

| Command | Purpose |
| --- | --- |
| `--upgrade-resource-policy <script.rmv> <stock-scripts-folder>` | Updates an existing generated RMV's stock resource policy and records a backup. |
| `--build-fauna-evidence-probe <output-folder> <stock-scripts-folder>` | Builds an evidence package, editable project, sidecar, installer, manifest, and instructions for live fauna investigation. |
| `tools/Analyze-FaunaEvidence.ps1 -TracePath <trace> -FaunaJsonlPath <events.jsonl> [-Markdown]` | Analyzes captured native fauna evidence. |

Read the [manual wildlife and fish report](manual-wildlife-and-fish.md) before using the live evidence workflow. Its remaining visual confirmation should not be presented as a completed acceptance result.

Set `EDMW_DIAGNOSTICS=1` in the build environment to compile the native generator with its optional diagnostic definition. Diagnostic builds are for investigation; routine builds leave that variable unset.

## Geography regeneration

The runtime brush data is embedded in generated `UI/UsState*Data.g.cs` files. Retained source inputs and manifests allow offline regeneration; these inputs are not fetched by the normal application build.

With Python 3.10 or newer, run from the repository root:

```powershell
python tools/Generate-UsStateBrushes.py
python tools/Generate-UsStateMountains.py
python tools/Generate-UsStateWater.py
python tools/Generate-UsStateForests.py
```

The boundary, mountain, and water generators use the Python standard library. Forest generation additionally requires NumPy and Pillow. Optional preview contact sheets for the other generators require Pillow. Their optional `--preview` and, where supported, `--statistics` arguments write inspection artifacts.

`tools/StateBrushProjection.py` shares the projection and clipping conventions. `tools/Download-UsStateForests.py` is an optional reacquisition tool for the documented WorldCover WMS requests; routine regeneration uses retained inputs.

See [credits](credits.md) and the source-specific READMEs for exact source versions, hashes, transformation choices, and attribution to preserve.

## Optional native diagnostics

These components are included as source for investigation and are not required by `build.ps1`:

- `native/EDMWResourceSnapshot`: process-memory resource capture helper.
- `native/EDMWStringProbe`: localized string inspection in resource DLLs.
- `tools/ThreadSampler.cpp`: native thread/module/context sampling.
- `tools/DescriptorSampler.cpp`: descriptor-memory sampling for fauna analysis.
- `tools/WorldCellSampler.cpp`: final world-cell sampling aligned to an authored payload.

Each has an adjacent `build-native.cmd` or `build-*-sampler.cmd`. The sampler build scripts share `tools/setup-native-toolchain.cmd` to discover the Visual Studio x86 tools and write executables and objects to `tools/build/`. The optional probes write their outputs beneath their own `native/*/build/` directories. Review their source and the engineering report for the expected game/process inputs.

## Source changes

Keep the public product name consistent with **EDMW Mod Studio**. Preserve legacy identifiers when changing them would break resource lookup, project persistence, native payloads, or installed-package compatibility.

Use the existing verification appropriate to the affected area. Native payload, generation, or resource-alignment changes need the complete native/editor build; civilization changes should also be checked against the relevant source/package fixtures. Keep generated diagnostics and game-dependent data outside source commits.
