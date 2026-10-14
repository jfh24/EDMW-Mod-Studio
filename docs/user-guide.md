# EDMW Mod Studio user guide

EDMW Mod Studio is a Windows design tool for **Empires: Dawn of the Modern World**. Its Map Designer provides a visual canvas for painting terrain, shaping elevation, defining map structure, and exporting maps over LAN. Its separate Civilization Editor provides an editable project workspace for civilization rules, units, buildings, technologies, abilities, economy, ages, assets, and AI. LAN export installs the export on the sender and offers it to other running designers for approval.

This guide covers version 1.1.0, including online friends and sharing installed content from Home. For source builds, architecture, and verification, see [development](development.md).

## What the app does

- Paints deep water, shallows, beach, grassland, forest, hills, mountains, and desert.
- Provides twenty standard brush shapes and all 50 US state silhouettes, with independent length, width, scale, and rotation controls. The **US States** dropdown lists state brushes alphabetically.
- Generates organic random land while letting you control how much existing land is overwritten.
- Generates complete, repeatable terrain maps from a saved seed, including coasts, shallows, mountain ranges, and forests.
- Places exact starts for player/lobby slots 1–8 in automatic slot order.
- Paints a color-coded elevation mask from `-100` to `+100`.
- Forces painted elevation slopes into cliffs with the **Paint cliffs** checkbox.
- Progressively converts negative elevation from shallows to deep water.
- Supports user-drawn ramps and keeps the elevation mask visible while ramps are painted.
- Creates three broad, low-gradient access slopes through enclosed elevated rings.
- Preserves painted terrain, land and water membership, manual marker data, and original game files.
- Saves editable projects as `.edmwmap` files, including elevation masks and manual ramps.
- Lets any number of editors paint the same project over a trusted LAN connection. One person hosts and shares their displayed local address; others join it from **Collaborate**. Completed paint strokes synchronize immediately, with the most recently completed change taking precedence if multiple people change the same cells. Each collaborator's map-author name appears beside their live cursor.
- Exports maps and civilization edits over LAN, installing them locally and offering them to other players for approval.
- Sends map and civilization exports to every running designer discovered on the LAN with one click and per-recipient approval status.
- Shares installed maps and civilization edits directly from Home over LAN, to selected online friends, or both, without opening their editable project files.

## Getting started

1. Download and open [EDMW.Mod.Studio.exe from the v1.1.0 release](https://github.com/jfh24/EDMW-Mod-Studio/releases/download/v1.1.0/EDMW.Mod.Studio.exe), or open `dist/EDMW Mod Studio.exe` after a local source build.
2. Use the small settings button in the top-right toolbar to confirm or change the EDMW installation folder.
3. Choose a terrain type and paint directly on the map canvas.
4. Use the left rail to adjust the brush or generate random land. Terrain, resources, and player starts are arranged as full-width sections in the right inspector, with Elevation and Ramps side by side at the bottom.
5. Save the editable project from **File > Save**.
6. Select **Export over LAN** to install the map on this computer and send it to every receiving designer found on the network.
7. Follow the small status popup; other players choose **Accept** or **Deny** in their own designers.

Use **Hide map preview** in the top bar before exporting to replace the terrain preview with a large, angled red **Redacted** notice.

Choose **Export over LAN** in the Map Designer to install the current map locally and send it to every receiving designer found on the local network. A small themed popup shows **You (this computer)** and each recipient's waiting, accepted, denied, or failed status. Each recipient reviews the map preview and chooses **Accept** to install it or **Deny** to leave their game unchanged. Installed maps appear in Home's installed maps list and can be uninstalled there. LAN export honors **Hide map preview** and installs locally even when no other designers are found. Use **Export to friends** to send the same reviewed map package online.

Map delivery works while the designer is open on any workspace, independently of LAN collaboration. It requires no Host/Join session. **Settings > Receive maps over LAN** controls receiving. Map transfers use TCP **42821** by default, configurable in Settings, with discovery on UDP **42822**. Configure the sender's game folder in Settings before exporting.

## Friends and online sharing

Online sharing requires a running relay and matching application builds. The relay operator can follow [online sharing and Render setup](online-sharing.md).

1. Open **Friends** from Home, the Map Designer, or the Civilization Editor.
2. Enter **Display name**, then choose **Connect**. Expand **Relay settings** to see **Relay URL**, already filled with `https://edmw-mod-studio.onrender.com` or your saved custom relay URL. Edit it while disconnected to use another service. Both players must use the same relay.
3. Copy **Your friend code** and exchange it with the other player. Enter their code under **Friend code** and choose **Send request**.
4. The other player selects the incoming request and chooses **Accept**. Accepted friends show **Online** or **Offline**. **Remove/cancel** revokes a friendship or pending request.

In the Map Designer, select an accepted online friend in **Friends** and choose **Invite to map**. This starts a shared session for the displayed map if needed. The recipient selects an incoming invitation and chooses **Join map**; joining replaces their current map after the usual unsaved-changes prompt. Strokes, resources, player starts, elevation, and named cursors synchronize in relay order. Choose **Leave session** to leave. Host departure ends the room. LAN and online map sessions cannot be active together; only the online host can invite more friends.

The Friends dialog groups connection, friend codes, friends/requests, and map invitations. Request actions change to **Decline request** or **Cancel request** when appropriate. Empty lists show a short placeholder, and the **Leave session** action appears only during a session. Close the dialog with its title-bar **×** or **Esc**. Tooltips describe unavailable actions; failed actions show a warning dialog.

Choose **Export to friends** from the Map Designer toolbar or the Civilization Editor's **Game** menu. Select accepted online friends, then **Send**. The status dialog reports local installation and each recipient's installed, denied, or failed result. Export does not require a shared editing session. The receiver validates the package with their local game installation and chooses **Accept** or **Deny** before any game files change. **Hide map preview** applies to online map exports too.

Keep the application open and connected to receive invitations and exports in any workspace. The free Render relay can sleep when idle; **Connect** waits for it to wake and can be cancelled by closing Friends. Pending exports expire after ten minutes. A restart or lost connection ends active rooms and transfers; reconnect through **Friends** and invite again. Save editable projects normally. The desktop retains signed identity and friendship approvals so both friends can recover their accepted contact when reconnecting to the same relay. A saved contact stays offline until its peer confirms the approval; removing a friend clears the saved approval. The relay must keep its signing secret unchanged. Credentials are protected for the current Windows user and stored separately for each relay endpoint; another Windows account has a separate identity. Saved custom relay URLs remain selected when you reopen Friends. Profiles from the earlier relay build update once to a new friend code and must exchange codes again.

## Share installed content from Home

Home's **Custom maps** and **Civilization edits** panels let you share the item already installed in the selected game folder. Your currently open map and civilization documents are not used or replaced.

1. Beside the item, check **LAN**, **Friends**, or both. Select at least one destination to enable **Share**.
2. Choose **Share**. If **Friends** is selected, connect if needed and choose one or more accepted online friends. Canceling that selection stops the entire share, including LAN delivery.
3. Follow the shared status dialog. LAN offers go to receiving designers discovered on the network; Friends offers go to the friends you selected. Each recipient returns accepted/installed, denied, or failed status. Closing an active status dialog cancels unfinished offers.

Sharing reads and validates the installed item without reinstalling it on this computer. A map retains its installed `.rmv`, `.edmwa` sidecar, name, and identity. If its installed x86 generator differs from this editor's verified copy, the shared package uses the current verified generator; the sender's installed DLL stays unchanged. Its review preview is reconstructed from the sidecar, and the Map Designer's **Hide map preview** selection also applies to Home map sharing. A civilization edit uses the selected package's saved installation manifest, retaining the original source project, expected values, model payloads, and progression data. It does not combine other installed civilization edits into the offer.

Incomplete/orphaned maps cannot be shared. Maps with a damaged or unsupported sidecar, changed script identity, or invalid/non-x86 generator are rejected; install a compatible export first. A valid older x86 generator does not require opening the project again: the shared package contains the current verified copy. Civilization packages must still be installed with their owned data and protected original backups intact and no interrupted transaction pending. Earlier direct civilization installations have no saved share package; restore them and install through the Civ Editor before using Home sharing. Recipients still apply the normal validation and approval flow; a verified identical civilization package already installed is acknowledged without another install.

## Civilization Editor

Choose **Civ Editor** on the home screen to open the dedicated civilization workspace. Its own framed panel and crowned citadel-and-quill emblem use the same medieval artwork as the other home actions. The top toolbar switches between the Map Designer and Civilization Editor while retaining both documents. Returning Home also keeps both documents in memory; closing the app prompts for unsaved changes.

Civilizations, categories and roster entries use icon cards throughout the editor. Choice fields use selector buttons, including named ages. Editing forms keep readable widths and thin input borders. Large catalogs use compact page controls while retaining search, filters and selections across the full catalog. The catalog and installed-model pickers use searchable cards; the model picker shows a live preview before a selection is applied. Generated category artwork and its prompts are documented in [progression artwork notes](../EDMWMapDesigner/Assets/Progression/README.md).

- Select one of the nine existing playable civilizations, then choose Overview, Units, Buildings, Technologies, Abilities, Economy, Ages, Assets, or AI.
- Units, buildings, research, commands, and civilization powers load automatically for the selected civilization and category, using native permissions, producer links, supported ages, and configured power assignments. Item cards display the game's localized names. Selecting stock items leaves the project unchanged; editing an item creates a saved override.
- Current game values populate the supported fields, including health, attack, range, reload, armor types, sight, movement parameters, resource costs, population, worker coefficients, HUD flags, and graphics references. Source details show production, commands, prerequisites, effects, model/icon mappings, age unlocks, starting-resource profiles, and AI scripts/roles. Shared match resource presets and unverified native parameters are identified as source data. The compact **Project**, **Edit**, and **Game** menus contain project actions, undo/redo, resets, and both LAN and friends export. **Edit > Show unsupported fields** exposes additional fields whose mappings are still pending; unsupported stock fields are read-only.
- Add and duplicate project entries, clear an existing item's edits, and reset fields to current game values. Copies referencing the same stock definition share the installation target; incompatible edits are rejected during installation review.
- Preview unit and building models with their game textures, transparent surfaces, player colors and animated child parts. Drag to rotate, scroll to zoom, and double-click to reset. Right-click the model to choose a native player color. Animation checkboxes use the game's localized names, frame ranges and timing; checked clips repeat in order and unchecking all pauses playback. Animation-specific meshes and skins follow the unit definition. Browse installed model files or upload `.cem` and `.obj` replacements. Replacement bytes are embedded in the project, with their filename and SHA-256 fingerprint, and can be compared with the original model or restored. Preview lighting is computed locally; appended engine-specific effect sections are retained in model files but are not displayed.
- Save and reopen independent `.edmwciv` projects. Civilization undo/redo and keyboard shortcuts operate independently of the map document.
- **Ages > Progression** contains inline item cards and an editor. Use **Add items** to choose an existing compatible unit, building, technology or age advance for the selected civilization; **Remove** excludes the selected item from that civilization and **Restore** restores its original membership. Foreign items offer a compatible production or research location. Search and filter by item type, membership and age. Age names appear throughout, including **WWI** and **WWII**. Edit availability, native costs, production/research parameters and supported modifier effects beside the selected card. Undo/redo and save/reopen retain these edits; LAN export loads the same changes locally and on accepting recipients. Membership is specific to the selected civilization; other fields retain their shared native scope. Adding production locations owns the affected database tables as one tracked export, so conflicting installed civilization exports must be removed first.

Use **Game > Export over LAN** to install the authored edits on this computer and send the same package directly to every receiving designer found on the local network. Export starts with one click; a small themed popup shows the sender's installation result and each recipient's acceptance, denial, or failure status. Each recipient sees the project, the sender's IP address, and locally verified changes with **Accept** and **Deny**. Acceptance installs through the same tracked package system; denying or withdrawing an unaccepted offer leaves game files unchanged. The sender installs even with no other designers present. Re-exporting an unchanged installed project reuses its original verified package. Use **Game > Export to friends** for online delivery through accepted friendships.

The designer receives LAN offers automatically while open, including on Home and in the Map Designer. No Host/Join session or pairing is required. **Settings > Receive civ edits over LAN** controls receiving and shows the local address. The default TCP port is **42819**, configurable in Settings; discovery uses UDP **42820**. Accepted edits appear in Home's **Civilization edits** panel for Details and Uninstall. Recipients need the designer open with receiving enabled, and LAN discovery and transfer must be allowed by their firewall.

The Home screen's **Civilization edits** panel lists installed projects with their recorded changes, installation time, Details, Share, and Uninstall. Returning Home or activating the app refreshes the list; Refresh also detects changes made by a separately run installer. Different fields can be installed as independent packages and uninstalled in either order. Overlapping fields and model replacements are rejected until their owning package is uninstalled. The installer validates the supported executable and source records, retains transaction backups, recovers interrupted operations, and refuses to overwrite externally changed owned fields. Earlier installations made directly by the designer also appear for uninstall.

Installation supports verified existing object fields, unit/building/research/command costs, civilization age/name-reference fields, verified scalar modifier effects, and native `.cem` replacement models. Civilization powers expose their actual assignments and activation chains; only verified linked effect operands are writable. `.obj` files can be uploaded and previewed, but native conversion is required before installation. New civilization/definition insertion, unmapped rules and native power parameters, and in-game/multiplayer validation remain incomplete; an unsupported change blocks export rather than being silently skipped. Every multiplayer participant needs matching installed gameplay data.

## Editing controls

- **Left drag:** paint with the active terrain or enabled paint mode.
- **Right click:** sample terrain or elevation; in ramp mode, remove a marked ramp cell.
- **Player Starts mode:** use the toggle to select the drawing tool, then left-click safe land to place P1 through P8 in order. Right-click anywhere on the canvas removes the most recently placed slot. Placed starts are always active regardless of whether a lobby slot contains a human or computer player.
- **Resources mode:** Forest, gold, stone, berry bushes, Fish, Dolphin, Deer, Bighorn Sheep, Bear, Squirrel, Sheep, Chicken, Wild Pig, and Elk are active placement tools. Fish and Dolphin transactionally replace only stock Fish; land fauna transactionally replace only stock Animal. An unauthored or invalid category retains its complete stock behavior. All other fauna choices are absent from the palette. Their old numeric values remain readable but inert solely for project compatibility. Forest uses the normal variable-size brush; the other resources use point placement.
- **Brush Controls:** select Length, Width, Scale, or Rotate, then use the shared slider. Length and Width range from 25–200%, Scale from 1–128 cells, and rotation from 0–359°.
- **US States:** select a state by name in the brush panel, then paint or stamp its silhouette on the canvas. State brushes use the same augments, preview, and paint modes as standard shapes. Increase Scale to reveal coastline and island details; selecting a standard shape returns to that brush.
- **Seeded map:** enter a whole-number seed in the left rail and choose **Generate map**. **New seed** chooses another seed; leaving the field empty also chooses one when generating. The same seed and grid size recreate the same terrain, including mountain ranges as cliffs. Generation replaces the current terrain and clears its authored resources, starts, anchors, elevations, and ramps; **Ctrl+Z** restores the entire previous map. The map name, author, grid size, and variance setting are retained. Seeds are saved with projects and shared over LAN. Manual edits remain editable; generating again recreates the original terrain for that seed. Game-side randomized resources and stock variation are separate from the terrain seed.
- **Generated layouts:** land can extend through the map edges and corners. Seeds vary the placement, length, width, and number of rivers, lakes, mountain ranges, and forest belts; large features can divide continents. Shores can meet forests, cliffs, hills, or open ground, with beaches and shallows appearing in selected stretches. Newly generated projects preserve these coastlines during export.
- **State geography:** independently switch **Mountain ranges**, **Water bodies**, and **Forests** on or off below the state selector. Terrain strokes include mapped mountain regions as cliffs, lakes/rivers, and selected major forest areas in their geographic positions. The preview shows cliffs in gray, water in blue, and forests in dark green. Choices are remembered per state during the session. All three switches start off and affect future terrain strokes, leaving elevation, ramp, resource, and player-start tools unchanged. Larger brush scales reveal more detail. Water takes priority over mountains, which take priority over forests where features overlap. Cliff strokes replace existing elevations and ramp marks within the mountain footprint; paint ramps afterward to create passages. Repainting with ordinary terrain or elevation removes the cliff behavior.
- **Paint cliffs:** check this in the Elevation panel before painting to save the selected height as a target and make its slopes cliffs. Export enforces a minimum cliff step when needed. These strokes override variance, automatic access slopes, smoothing, and painted ramps. Unchecking affects future strokes; repaint with ordinary elevation to remove the forced cliff. Forced cliffs persist through save/load, undo/redo, resizing, LAN editing, and export. Changing only a terrain texture retains the authored height and cliff flag.
- **Map view:** use the mouse wheel to zoom and middle-drag to pan; the dedicated zoom panel is hidden.
- **Ctrl+Z / Ctrl+Y:** undo or redo.
- **Ctrl+S:** save the current project.

Only one specialized paint mode is intended to be active at a time. Turn on Player Starts, Resources, Elevation, or Ramps, then paint on the central canvas. The player-start and resource toggles select drawing tools only; supported placements are active in exported maps. Manual starts are keyed to the game lobby's numbered player slots; configured active slots are fixed and all other slots retain stock placement. Map analysis and warnings are available from the collapsed overlay in the lower-right corner of the canvas.

Player-start markers bind only to numbered lobby slots. The human or computer occupying that slot does not affect its fixed location.

State geography uses generalized map data, so lower hills, small ranges, streams, and ponds can be omitted. **Settings > Geography credits** contains source attribution. Mountain footprints are adapted from Snethlage et al. (2022), [GMBA Mountain Inventory v2](https://doi.org/10.48601/earthenv-t9k2-1407), © Global Mountain Biodiversity Assessment, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); see the [Scientific Data publication](https://doi.org/10.1038/s41597-022-01256-y). The mapped regions share the Census state projection and are rasterized and clipped to each state, preserving the original mountain mapping. Detailed source attribution and offline regeneration inputs are in [geographic source documentation](../EDMWMapDesigner/Assets/UsStateBrushes/README.md).

Water geography is made with [Natural Earth](https://www.naturalearthdata.com/downloads/10m-physical-vectors/) public-domain lake and river data, with Hawaii, Delaware, and Rhode Island supplements from the U.S. Geological Survey's public-domain [National Hydrography Dataset](https://www.usgs.gov/national-hydrography/national-hydrography-dataset). Rivers use a small cartographic width so they remain visible at useful brush scales. All geographic masks are bundled for offline use.

Forest areas are selected, generalized dense tree-cover cores from [ESA WorldCover 2021 v200](https://doi.org/10.5281/zenodo.7254221), capped to preserve open terrain. © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium. Used under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Adaptations select forest cores, project them into each state brush, and rasterize them; they represent selected major forests rather than exhaustive tree cover.

## Project and export files

- `.edmwmap` is the editable project format used by the Map Designer.
- `.rmv`, terrain data, and the native generator are internal contents of the map LAN export.
- `.edmwciv` is an editable civilization project; LAN exports carry its verified installation package.
- The designer reads the selected EDMW installation for source data. Workspace export installs on the sender, while remote installation requires recipient acceptance. Home tracks installed maps and civilization packages for sharing and uninstall; Home sharing does not reinstall them on the sender.

Map installation adds generated map files without replacing the game's original maps or DLLs. Civilization packages modify supported game data and model targets with tracked backups and uninstall.


## Build and compatibility

See [the project README](../README.md) for the source build and [development](development.md#game-compatibility) for the supported game checks. The editor exposes source data and supported installation fields; unsupported authored changes block export. Complete in-game and multiplayer validation remains outside the claims made by the offline checks.

See [credits](credits.md) for geographic source notices and [the wildlife engineering report](manual-wildlife-and-fish.md) for implementation evidence.
