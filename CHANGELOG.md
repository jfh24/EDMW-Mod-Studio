# Changelog

## 1.4.0 Beta — 2026-11-02

- Added public and friends-only relay game rooms, online friend invitations, and room discovery. Mod Studio now connects to the relay at launch and sends a 60-second heartbeat. Map collaboration options are grouped in the map script page panel, with online friend invites, incoming invitations, and clearly labeled LAN hosting/joining; the Friends dialog now has Friends and Matchmaking tabs.
- Added direct ICE/STUN peer connections for online rooms and an authenticated WSS gameplay fallback for networks where direct ICE cannot connect. UDP game datagrams, TCP stream frames, and session-control messages can be forwarded only between current members of the same matchmaking room. The fallback uses the existing relay connection and requires no inbound port forwarding; relay latency and bandwidth apply.
- Added an x86 native bridge for the supported game's TCP/IP provider and a local TCP stream proxy so the game's LAN host/join flow can use the direct peer channels.
- Hardened provider hooking for valid PE imports without an original lookup table, and made Matchmaking report when the TCP/IP provider has not loaded yet.
- Forwarded limited, multicast, and subnet-directed LAN discovery broadcasts to all relay room members, including when the in-game host is not the relay room owner.
- Restored and foregrounded the game window after launching from Matchmaking so the modal Friends dialog does not block game input.
- Added direct ICE and WSS relay counters to Matchmaking status, alongside provider hookup, UDP sockets, room broadcasts, received packets, and local game delivery.
- Made **Prepare game network** network-only: it requires an authenticated room relay and a usable local IPv4 mapping, configures virtual IPv4 and route mappings, then opens EDMW's normal UI. Direct ICE remains preferred when available; players can still use the game's normal UI when direct ICE fails.
- Added reliable peer setup messages and per-player launch/readiness errors. The bridge chooses a local IPv4 ICE interface rather than the public STUN-mapped address and self-checks the game adapter and matching route-table mappings before launch.
- Added hidden two-client loopback verification for matchmaking, virtual-IP mapping, provider hooks, launch coordination, and bidirectional UDP, TCP, and control traffic through WSS. This does not establish an actual in-game session over the public internet; a two-PC cross-internet game test remains pending.
- Added a guarded launcher that checks the supported game and provider hashes before injecting the bridge; it does not replace game files. Live cross-internet session validation remains pending.
- Updated application and installer template version metadata to 1.4.0.

## 1.3.0 — 2026-10-21

See the [release notes](docs/releases/v1.3.0.md) for compatibility and verification scope.

- Enabled compatible civilization edits to run together, with automatic ability-command reassignment, support for older installed edits, and selective uninstall.
- Added named **Merge with…** and **Unmerge** actions on Home, including LAN/Friends sharing with recoverable component packages.
- Added ownership transfer, unit/building transformation, spawning, instant abilities, proximity delays and recipient limits, and resource activation costs.
- Added proximity-granted toggle abilities for multiple recipient types, plus named effect descriptions and toggle states in the existing unit tooltip.
- Fixed action completion and erroneous refunds, instant-ability startup, and effective attack-range modifiers. Expanded offline installation, sharing, rollback, and recovery checks.
- Prevented native installation-rule tests from requesting Windows administrator access during builds.

## 1.2.1 — 2026-10-15

See the [release notes](docs/releases/v1.2.1.md) for usage, compatibility, and verification scope.

- Replaced the custom effect form with compact cards, all 51 stat output choices, multiple outputs per effect, and portrait stat overviews.
- Added a searchable target popup with type, civilization, and age filters; several unit/building types can share one proximity effect, and selections persist across filters and pages.
- Added synchronized group activation alongside individual abilities, with shared timed restarts and consistent group toggle behavior. Proximity affects other nearby units and excludes its own emitter.
- Corrected custom ability technology groups, native age gates, civilization/player identity matching, and physical HUD button placement while preserving stock commands.
- Preserved legacy project and runtime formats; canonical target sets expand into stable runtime identities with capacity and duplicate-identity checks. Portable packages retain tracked, reversible install/uninstall behavior.
- Expanded offline regression coverage for authoring, history, packages, runtime parsing, groups, proximity boundaries, sharing, and restoration. No new GUI or game launch is part of release preparation; live validation of all stat adapters and multiplayer remains incomplete, and native game saves with active effects are unsupported.

## 1.2.0 — 2026-10-14

- Added prototype research, proximity, and activated effects, editable unit/building attachments, per-mod controls, and game icons in the Civ Editor.

## 1.1.1 — 2026-10-14

- Added enable/disable controls for Technologies, Units, and Buildings in the Civ Editor.
- Simplified Home mod entries with context-menu actions, LAN/Friends sharing choices, and map previews in Details.

## 1.1.0 — 2026-10-13

This release includes all source changes since v1.0.0, including online work published between releases and the Home/Friends updates. See the [release notes](docs/releases/v1.1.0.md) for usage, compatibility, and verification scope.

### Added

- Online friend codes, explicit requests/acceptance, presence, invitation-only map collaboration, and map/civilization exports to selected friends.
- A Node.js HTTPS/WSS relay, Render Free deployment blueprint without a disk/database, and protocol/setup documentation.
- Protected desktop device identities and signed friendship approvals, scoped to each relay, with bilateral contact recovery after ephemeral relay data loss.
- Direct Home sharing for installed maps and civilization edits: per-item **LAN** and **Friends** checkboxes plus **Share**, with a combined recipient status dialog and no need to load the project.
- Read-only installed-map archive reconstruction and reuse of full saved civilization packages, plus checks for incomplete, incompatible, externally changed, legacy, or interrupted installed content.
- Isolated and deployed relay verification runners and a published record of live results; installed sharing regression checks using disposable game fixtures.
- Application screenshots, release download links, and retained Friends UX design references.

### Changed

- The relay URL defaults to `https://edmw-mod-studio.onrender.com`; saved custom URLs remain editable under **Relay settings** while disconnected.
- Home map sharing retains installed script/terrain bytes and map identity, and supplies the current verified generator when the installed x86 copy comes from an earlier build. The sender's files remain unchanged.
- Home sharing controls match the dark/gold theme, with framed checkboxes, visible gold checkmarks/focus, and **Share** sized and aligned with **Details**/**Uninstall**.
- Friends uses grouped connection/code/contact/invitation sections, clearer action labels, readable disabled controls, collapsible relay settings, and scrolling at constrained/scaled sizes.
- Redundant Friends headings, helper text, selection hints, status footer, and **Done** button were removed. Title-bar **×** and **Esc** close the dialog; tooltips retain action guidance and warnings show failures.
- Friends request actions distinguish **Remove friend**, **Decline request**, and **Cancel request**. **Leave session** is shown only for active sessions.
- README, distribution guide, user guide, and development/online documentation now describe the v1.1.0 binary and installed sharing.

### Fixed

- Home repaint glitching: its artwork is cached at the current size and transparent controls are composed within a buffered Home panel.
- Graceful online socket shutdown and bounded cleanup before reconnect, including stale-socket event isolation and relay presence/session/export cleanup.
- Pending local map edits stay visible while acknowledgements arrive and are rebased over confirmed relay changes; stale full-map replacement is canceled and synchronized.
- Map invitations are unavailable while a LAN session is active or an online guest lacks host permission; tooltips explain the blocked action.
- Online toolbar availability checks cover direct Friends and map export actions at narrower window sizes.

### Verification

- The full native/installer/editor build and latest editor publish passed for version 1.1.0.
- Installed-map and civilization sharing suites passed, including generator normalization/recipient upgrade, sender exclusion, retained package payloads, integrity failures, and unchanged source game files.
- Desktop self-test passed; all 21 Node relay tests passed. New UI/visual acceptance and real remote approval testing remain manual.

## 1.0.0 — 2026-10-12

- Initial EDMW Mod Studio Windows release with visual map design, terrain/marker generation, standard and US state brushes, Civilization Editor and model previews, tracked installation/uninstall, and LAN collaboration/export.
- Source build pipeline, compatibility notes, geographic/artwork credits, and user/development documentation.
