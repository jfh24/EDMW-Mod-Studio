# Changelog

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
