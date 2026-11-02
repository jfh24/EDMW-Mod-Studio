# Development and verification

The 1.4.0 beta consists of a Windows Forms editor, installer templates, native game components, and a separate online relay. Matchmaking rooms, binary datagram forwarding, and a guarded x86 hook for the supported TCP/IP provider are implemented; live cross-internet session validation remains pending. This document does not claim that every verification has been run on every game installation. See the [v1.4.0 beta notes](releases/v1.4.0.md), [v1.3.0 stable release notes](releases/v1.3.0.md), and [changelog](../CHANGELOG.md).

## Build pipeline

Run the complete build from the repository root:

```powershell
.\build.ps1
```

The prerequisites and clean-clone commands are in the [README](../README.md#build-from-source).

The production build follows these dependencies:

1. `native/EDMWAnchored/build-native.cmd` discovers Visual Studio through `vswhere` and initializes the x86 compiler environment.
2. It compiles `ResourceAlignment.cpp`, builds and runs `ResourceAlignmentTests.exe`, then produces `EDMWAnchored.dll`. MSVC warnings are treated as errors.
3. `native/EDMWEffects/build-native.cmd` builds and runs the native effect regression suites, then produces `EDMWEffects.dll` and `EDMWEffectsLauncher.exe` with the x86 compiler and warnings treated as errors. The installation-rule test declares `asInvoker` to avoid Windows installer detection requesting elevation during unattended builds.
4. `native/EDMWRelayBridge/build-native.cmd` compiles the x86 provider hook and guarded launcher; run it before building Mod Studio so both native files can be embedded.
5. The map and civilization installer projects are published as self-contained, single-file Windows executables. Their generated templates are copied into the editor's assets for embedding.
6. The editor embeds the native generator, effect runtime, game relay hook and launcher, installer templates, and UI artwork.
7. The editor is published for `win-x64` and the deliverable is copied to `dist/EDMW Mod Studio.exe`.

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
| `relay` | Node.js HTTP/WebSocket service for authenticated identities, accepted friendships, map sessions, game rooms and bounded packet forwarding, and temporary export delivery. |
| `render.yaml` | Free Render Node service, ephemeral working directory, and generated stable signing secret; no disk or database. |

The legacy `EDMWMapDesigner` directory and namespace are intentional. The rebrand changes the public product and executable name while retaining resource identities, persisted formats, and existing project paths.

### Map flow

An editable `MapProject` is analyzed and rendered using the selected game's original random-map templates. Export produces an RMV script plus an `.edmwa` sidecar carrying authored terrain and markers. A map-specific generator DLL is installed alongside the game; original game DLLs and stock maps are preserved.

A LAN map package carries a generator, script, sidecar, and preview. The recipient validates paths, sizes, payload identity, and the generator against the copy embedded in its own editor before installation. Use matching editor versions on the participating machines.

### Civilization flow

The editor reads game database records, localized names, producer and prerequisite links, archives, models, textures, and animations. Selecting stock content is read-only. Authoring creates saved overrides in `.edmwciv` projects.

Export reviews supported targets and creates a verified package. Installation tracks ownership and backups; uninstall checks owned bytes before restoring them. Different, nonoverlapping fields can be independently owned. Overlapping edits or model replacements must have their owning package removed first. Some progression operations own entire affected tables, so their conflict scope is broader than an individual field.

Independent runtime ability packages compose their native command additions and source-object action registrations. `CivilizationAbilityTableComposition` verifies exact source snapshots, permits registered command additions plus mapped scalar changes, and preserves other packages when removing an edit. Technology field patches follow stable record identities when variable-length command additions shift their byte offsets. `CivilizationRuntimePackageRebaser` assigns available native command and stored identities, updates project registrations and runtime rules together, and retains the original portable export separately from the installed representation. Installed-export checks corroborate that representation against protected pre-install technology bytes. Canonical dependency fingerprints remove tracked command additions while retaining checks for unrelated structural changes. Progression producer rewrites and unsupported whole-table changes keep their existing exclusive ownership. Earlier ability exports can compose when their exact source baseline can be reconstructed; retained legacy or malformed exports keep their original uninstall/recovery path.

The LAN recipient rebuilds its review using local game data. Received packages are installed through the package system rather than by executing a received installer.

### Home inventory sharing and repainting

Installed-map and civilization-package rows show text, with **Details**, **Share**, and **Uninstall** in a context menu. Share opens a **LAN** or **Friends** choice. The main form checks the listed game folder against the current selection, snapshots the selected installed item, and obtains Friends recipients before starting any transfer. Sharing has no local installation delegate; it leaves the open map and civilization documents untouched.

Managed civilization rows also expose **Merge with…** through the same mouse and keyboard context menu. The chooser preselects the current entry, accepts multiple complete managed entries or existing groups, and requires a custom name and at least two selected entries. Merging flattens the constituent packages into one named inventory entry. **Unmerge** restores the original entries while preserving installed game bytes and runtime settings. Sharing a merged entry reuses the LAN/Friends paths with an envelope containing its original portable component packages; accepted recipients retain those components and can unmerge locally too.

`CustomMapInstallationCatalog.CreateSharePackage` and `MapLanPackage.FromInstalledFiles` assemble a bounded archive from the installed RMV, native generator, and sidecar. Expected file locations, map identity, supported sidecar format/integrity, script generator reference, plain paths, and the installed generator's x86 structure are validated. The RMV and sidecar bytes remain exact. If the installed generator differs from `EmbeddedGenerator.Read()`, the archive substitutes the current verified embedded copy; it never writes the sender's files or forwards an unverified generator. The review preview comes from the validated terrain sidecar. The usual archive decoder still requires the recipient's matching verified generator. No terrain regeneration or project load is required.

`CivilizationPackageManager.CreateSharePackage` selects the full persisted package by its installation ID and checks its owned fields/files and protected original backups against the current installed state. The saved package retains original baselines, portable project, replacement model bytes, and progression changes; it is not rebuilt from cumulative game modifications. Legacy installations without a portable package, externally changed owned data, damaged backups, and unfinished transactions are rejected. The ordinary recipient review, compatibility checks, and tracked installation remain in use.

LAN and Friends delivery use the same per-recipient status workflow with distinct destination identities. A separate outbound transport avoids receiver lifetime changes and excludes this app's receiver instance from discovery, preventing an offer back to the sender. LAN receiving preferences do not disable outbound sharing. Canceling Friends selection aborts the share; closing the status dialog withdraws unfinished offers through the existing transports.

`DashboardPanel` buffers Home and composes its transparent descendants. It caches the full-screen background at the current client size, invalidates that cache when the image/size/color changes, and disposes it with the control. This targets Home's repaint glitch without changing the other workspaces' native painting lifecycle.

### Online friends and sharing

The desktop authenticates a signed device identity over HTTPS/WSS and stores its credential and accepted friendship receipts using Windows DPAPI. Friends require a request and acceptance. The free relay's stable `IDENTITY_SECRET` verifies recovery credentials after its ephemeral filesystem is replaced. A recovered friendship authorizes sharing only after both authenticated devices declare the same signed acceptance. A peer's current omission removes the stale counterpart receipt, preventing unilateral restoration of a removed friendship. The relay controls presence, invitation and export access, room membership, and ordered map revisions. Online map editing uses the existing `MapDelta` and `MapProject` formats. Public connections require TLS; unencrypted loopback endpoints are available for local verification.

Online exports reuse map archives and `CivilizationInstallerPackageCodec` payloads. The relay checks upload length and SHA-256, permits downloads only by the intended accepted friend, and forwards recipient decisions. The desktop then applies the existing package validation, review, and tracked installation flow. Received installer executables are never run. Identity and accepted-contact recovery uses desktop credentials and the stable environment secret; pending requests, rooms and package offers remain transient. Connect polls the relay's health endpoint with a bounded, cancellable timeout to accommodate free-service cold starts.

See [online setup and operations](online-sharing.md) and [the wire protocol](online-protocol.md). The relay is a separate service and is not embedded in the Windows executable. The v1.1.0 desktop release includes the online feature; v1.0.0 predates it.

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
| `--verify-installed-sharing <output-folder>` | Installed-map RMV/sidecar bytes and identity, verified generator reuse/upgrade, previews/redaction, isolated recipient installation, invalid/incomplete/changed/oversized inputs, and read-only sender preservation; also exercises discovery with temporary loopback receivers to check sender exclusion. No UI, online connection, or package offers. |
| `--verify-civilization-installed-share <game-folder> <output-folder>` | Domain-only installed civilization package selection/reuse, retained baselines and payloads, and rejection of changed/legacy/interrupted state using disposable game fixtures; no UI or network. |
| `--verify-installed-content-ui <output-folder>` | Home context actions, share destination choice, named merge selection and validation, and offscreen layout captures; no network transfers. |
| `--verify-online <loopback-relay-url> [control-directory]` | Actual C# clients against Node: authentication, friendship permissions, map invitations, map masks, simultaneous edits, cursors, export decisions, reconnect, and revocation. The isolated runner supplies a control directory to verify identity/contact recovery across complete filesystem replacements. Does not install game data. |
| `--verify-online-live <https-relay-url> <output-folder>` | Explicit public-relay check using three temporary profiles: friends, 512 by 512 map synchronization, valid map/civilization archive transfer and decoding, integrity failures, cancellation, disconnect, reconnect and revocation. Reports cleanup and PASS/FAIL; does not install game data or restart the relay. |
| `--verify-online-ui <output-folder>` | Offscreen Friends/picker default, minimum, expanded relay, and scaled layout; invitation gating, toolbar availability, and pending local stroke preservation across acknowledgements; no network connection or account changes. |
| `--verify-civilization-navigation` | Catalog selection shortcuts, paging, and resizing; does not require an installed game. |
| `--verify-unit-effects <game-folder> <output-folder>` | Card layouts at default/minimum sizes, all 51 stat outputs and numeric ranges, multiple outputs, portrait stat overviews, attachment history, save/reopen and unchanged source files. |
| `--verify-runtime-effects <game-folder> <output-folder>` | Runtime formats and stat validation, concurrent package installation, command collision rebasing, shifted technology fields, toggles, sharing, selective uninstall, protected backups and metadata checks using isolated fixtures. |
| `--verify-civilization-merges <game-folder> <output-folder>` | Named merge/unmerge, portable component sharing and recipient review, grouped toggles/removal, rollback and interrupted-operation recovery in isolated fixtures. |
| `--verify-runtime-coexistence <game-folder> <output-folder>` | Focused concurrent runtime installation, command collisions, shared recipient review, existing legacy edits, toggles, protected backup integrity and arbitrary uninstall order. |
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

Custom unit effects use a shared 51-output catalog with stable persisted identities and explicit units. Each effect owns one trigger, a target set, activation state and optional command, plus 1–51 distinct stat outputs. Legacy scalar project attachments remain readable. Runtime format `EDMWEF05` stores the output count at byte 56 of the 60-byte record header, followed by `(uint32 stat, float32 amount)` pairs and caption bytes; readers retain formats 1–4. Output lists are compared by value when checking package ownership and runtime configuration.

The affected-target popup stages a multi-selection independently of the parent effect draft. Search and type/civilization/age filters use the cached game catalog and verified technology availability; paging does not clear selections. Project attachments optionally persist `targetObjectIds` as a sorted distinct set and mirror the first entry in legacy `targetObjectId`. One proximity attachment expands to one existing V5 runtime rule per target, with deterministic SHA-256-derived identities under a fixed namespace; a single target retains its authored identity. Reordering selections preserves the exported bytes, while adding a target preserves other expanded identities. Activated effects require exactly their source type. Package validation rejects duplicate authored or expanded identities and enforces the combined 1,024-rule capacity before installation. Headless fixtures cover project/history/codec/install/share/aggregation/selective uninstall and managed-to-native multi-target parsing.

The launch configuration aggregates enabled research and unit rules across installed packages. Disabled ability packages retain inactive native button bindings, so those rules still count toward the 1,024-rule limit and global effect/command identity checks. Installation, runtime toggles and launch all validate the same retained-binding capacity.

Custom ability nodes must remain in the native technology group selected by `technologyId / 1000 - 1`; reserved IDs 10000–14999 belong to groups 9–13. A flattened record parser alone cannot verify this lookup constraint. Export and incoming install/review checks validate all 14 group boundaries, and launch checks reject a previously installed malformed ability table. Retained package manifests and journals remain readable so older exports can still be uninstalled or recovered. Regression fixtures cover each reserved ID range, unchanged stock records and group counts, incoming rejection without mutation, and uninstall of a retained malformed export.

Those reserved IDs also imply native minimum ages 9–13. The process-only ability binding hooks correct minimum age only for registered kind-3/command-51/event-0 nodes on verified live player trees for the binding's civilization. After native tree initialization, the normal prerequisite evaluator receives the age-zero prerequisite, including for groups beyond the civilization's final age; HUD lookups never refresh availability. Native zero-based civilization rows are normalized to the persisted Phoenix IDs only at runtime rule-matching boundaries. Neither correction changes game files or package formats. Package-owned rules and button tables retain normal backup/uninstall tracking, and a launch without installed runtime rules bypasses runtime extraction and injection.

Button placement uses physical HUD cells rather than treating explicit commands' icon fallback slots as occupied production slots. The nine fixed command cells alias dynamic slots `0,1,2,14,15,16,28,29,30`; the twenty middle action controls alias `3..12,17..26`. Custom effects reserve both regions, offering only `13,27,31..41` after excluding existing production/research placements. The editor offers free positions and reports exhaustion. Binary regressions independently cover the fixed-command mapping, Chinese Citizen commands, all playable unit/building contexts, multiple custom abilities, exhaustion and unchanged stock records.

For already-installed exports, the existing registered-node lookup hook also relocates unsafe button placements in memory. It reads the source's canonical action list, reserves stock placements across all ages, and assigns custom buttons in technology-ID order using captured authored preferences. Only the registered definition's matching-civilization slot is changed; shared button appearances, native controls, files and package manifests stay intact. Exhausted positions use the native hidden-slot sentinel, and malformed slot metadata hides only the registered node. No additional executable hook or wire-format change is required. Retained overlapping packages remain readable, shareable and uninstallable without rewriting their stored data.

Synchronized commands carry the player's network identity at command +0x0A, resolved by player byte +0x480; effect snapshots use the separate internal index at player +0x478. Ability execution validates both identities, the live registry and actor ownership, then forwards the internal index to effect activation. Production actor scopes use the same conversion. Regression cases include identity zero resolving to internal index one and identities above fifteen, plus timed Chinese Citizen model/movement effects and expiry. In-game help contains gameplay outputs and durations in seconds; implementation notes remain in editor documentation. The existing per-launch log records bounded command acceptance/rejection and effect transitions without adding player-facing messages or changing installed game files.

Group ability execution enumerates the owner's synchronized selection at +0xD44 using its native count/item interface. The command's validated anchor must belong to that selection before expansion; otherwise only the explicit anchor is used. Owned, living source-type actors are deduplicated and passed to one batch activation with one simulation timestamp. Mixed toggle groups enable together, entirely active groups disable together, and timed groups restart together. The checked node command-key hook changes only the group HUD caller's registered ability merge key, keeping separate custom buttons visible while preserving native command 51/event zero. It does not change unrelated callers or on-disk records. Trigger-parity tests compare identical proximity/activated outputs through the common stat projection, and bounded postapplication logs record native maximum/current health and effective sight to distinguish calculated bonuses from visible application.

Proximity matching excludes a provider whose unit ID equals the target's ID. This preserves same-type auras between separate units while preventing an isolated emitter from satisfying its own radius condition. The condition is confined to proximity; individual and group activated abilities still affect their activating units. Existing installed configurations use the corrected condition on the next runtime launch without any package, wire-format or on-disk game change. Portable tests cover isolated emitters, mutual same-type sources, entry/exit, exact radius boundaries, ownership, dead providers and non-stacking.

The source build also supports `ProximityAbility` (trigger 3 on the wire). This attachment defines a conditional toggle directly on the emitter, with multiple recipient types and the usual stat outputs/button fields. Export registers a command on each recipient type and persists the recipient-to-command map in the prepared project. Grant payloads use `EDMWEF06`, retaining the V5 record size and output framing; record +28 identifies the family's lowest command ID. All family members must agree on emitter, civilization, radius, outputs, caption and enabled state, and name an existing family command. Older effects continue to encode as V5 and older readers reject trigger 3.

The simulation computes grants with the same spatial predicate used for ordinary auras before pruning activation state. Losing the final provider erases the toggle, and re-entry grants availability without reactivating it. Recipient action lists are filtered in borrowed per-instance prototype/stat projections even while the toggle is off. The single-unit HUD reads unit +0x64; group collection reads unit +0x6C then statistics +0xB4, so both see the same filtered prototype without changing the canonical action list or global player availability. The family command is the group HUD merge key; a single batch evaluates mixed toggle state across eligible selected recipient types. Disabled grants hide only their registered nodes. All projections are restored before native teardown/research, and installed command tables retain normal package ownership and uninstall restoration. Offline tests cover grant/revoke, overlapping providers, mixed groups, pooled identities, clock reset, action masks, V6 validation, package sharing and removal. No live validation of this new grant path is implied.

Named effects use `EDMWEF07`, retaining V6's 60-byte header, outputs and ability caption, followed by bounded length-prefixed UTF-8 authored and target names. Older V1–V6 configurations remain readable; unnamed rules retain the V5/V6 encoder. Launch snapshots recover missing authored names from validated installed projects and read target names from the local Language2 resource field at object-record +296, without rewriting installed files. Names follow expanded target identities through package validation, sharing, aggregation and removal. Equivalent per-target descriptions are grouped into one effect with a list of player-facing target names.

Permanent output actions and ability conditions use `EDMWEF08`. The stable 51 stat IDs and previous formats remain unchanged. V8 permits zero stat entries when at least one action exists. After V7's names it stores target-player scope and action count, then each 24-byte action header (kind, object, count, owner, player slot, UTF-8 name byte length) and bounded object name. Its final 44 bytes contain the authored family GUID, proximity delay, total limit, simultaneous limit, and food/wood/gold/stone prices. At most 64 combined outputs and 64 spawned objects are allowed per activation. Instant activation is wire mode 3 and requires actions without temporary stats. Source-family IDs share limits across expanded target types.

The simulation preserves admitted recipients and their original provider while valid, starts the delay on continuous entry, and releases reservations on exit or identity changes. Completed permanent actions consume distinct-recipient total allowance and release concurrent capacity; temporary stat effects retain concurrent capacity. Pending operations reserve total capacity before another activation can claim it. Resource preflight uses the same deterministic activation plan as command dispatch, charging only new starts. Native costs are paid through the verified player purchase virtual and failed action-only requests refund through the verified resource-credit method. Canonical installed command records keep zero fallback costs so a plain game launch cannot charge for an unbound action. Tooltip prices come from the registered runtime binding.

Ownership and transformation use full native replacement routines, after restoring borrowed stat/geometry/vision/model/resource/population/production overlays. Spawn uses the canonical object's factory, native terrain/occupancy validation, and native completed-building transition. Permanent operations are deferred until after actor-list iteration, revalidate IDs/generations and native registration, and run in bounded deterministic batches of at most 128 units of native work per simulation frame. Newly created/replaced actors inherit the current entry latch to prevent self-spawning cascades. These hooks and launch snapshots modify no executable, DLL, language resource or untracked game file. Installation, disabling, sharing and uninstall retain ordinary package ownership and original-file restoration. Static ABI evidence is in `UnitEffectActionsEvidence.md`, `UnitEffectSpawnEvidence.md` and `UnitEffectAbilityCostsEvidence.md`; model, codec and fixture checks do not replace live gameplay acceptance.

The runtime adds descriptions to the game's existing selected-unit information tooltip without creating floating controls. Selection callbacks capture IDs rather than retaining actor pointers. Descriptions combine read-only per-rule receive/provide snapshots, preserving separate effects even when their summed stat deltas cancel. Authored effect names head their descriptions; granted toggles report availability and on/off state. Empty selections or actors with no applicable effects leave native help unchanged. Hook signatures are documented in `UnitHookEvidence.md`; portable tests verify description content, selection guards and state transitions. Final appearance still requires manual game testing.

The native runtime evaluates bonuses independently by unit and stat. It uses borrowed per-instance prototype/stat snapshots for mapped scalar fields, guarded getters for sight and movement, and separate adapters for model scale, resource inventory, health regeneration, damage hit chance, population, and producer costs/timing/limits. It never installs a unit's snapshots in the shared player/type registry. Native research and lifecycle calls restore canonical pointers before running; refreshes retain native changes. Related names can refer to the same verified native parameter: transport/garrison capacity, steering/turn rate, construction interval/build rate, and square required-space/footprint dimensions. These aliases and native interval directions are described in the editor.

Build limit and queue capacity are positive custom limits, rather than additive native fields. Concurrent limits use the most restrictive value. Accuracy adds to a custom 100% outgoing damage hit chance and leaves projectile flight native. Costs and production timing apply to the affected producer's units and research. The portrait opens a read-only catalog overview with project overrides; unavailable schema values are marked explicitly. See `native/EDMWEffects/UnitHookEvidence.md` for checked addresses, native mappings and remaining live checks. Offline fixtures and portable tests do not establish live gameplay or multiplayer synchronization. Native game saves with active effects are unsupported: serialized per-instance overlays would require a complete save suspension/restoration boundary to avoid treating them as canonical baselines on load. Mod Studio project persistence is covered separately by the authoring checks.

For the Node relay, run `npm ci` and `npm test` inside `relay`. The tests use temporary storage and local listeners. For desktop/relay compatibility, run `tools/Verify-Online.ps1` from the repository root after generating the embedded resources with `build.ps1`. It starts `tools/OnlineVerificationRelay.mjs` on loopback, builds the editor, runs `--verify-online`, replaces the relay's filesystem twice while retaining its signing secret, and stops the relay. The restart control files exist only in this local verification runner; production exposes no reset endpoint. Test identities and output stay under ignored `.verification`. Document results with the GitHub change; exclude credentials, relay data, game fixtures, and generated executables.

For an explicitly selected deployed relay, run `tools/Verify-LiveOnline.ps1 -Endpoint https://<service-name>.onrender.com`. It builds the editor, starts the desktop check with hidden process windows, writes a unique `.verification/live-online-*` directory, and limits execution to four minutes. `-SkipBuild` reuses an existing Release assembly. The independent Node check is `node tools/Verify-LiveRelay.mjs https://<service-name>.onrender.com .verification/live-relay/node-report.json`; it bounds application traffic to 256 KiB and waits up to 150 seconds for Render readiness. These checks create named test profiles, remove their friendship, and disconnect them. Offline identity records remain because the protocol has no account-delete operation. See the [live relay verification record](live-relay-verification.md) for results and coverage limits.

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
