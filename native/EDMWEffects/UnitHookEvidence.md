# Unit ability hook investigation

Static inspection only; no live activation, save/load or multiplayer verification.
Executable SHA-256: `9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3`.

| Path | Evidence |
| --- | --- |
| Visible native action | Object loader `0x006FDAFC` copies `dbobjects.dat` +896 count / +1136 technology IDs to prototype +0x34. HUD `0x0076CDAC` enumerates these and `0x0076CE8D` binds a technology through `0x0076E871`. |
| Button placement | `dbbuttons.dat` has a four-byte count and 228-byte records. Loader `0x00519253` copies +216 to button +0x3C. Technology civilization slot overrides this fallback; the HUD caps the slot at 41. |
| Action creation | `0x00758E86` creates the generic research action (`0x007FB3B0`) for an immediate node without a created object. It retains technology ID and selected actor ID. |
| Synchronized activation | Action `0x0075884E` constructs command `0x52` (`EEMResearchTechnology`, vtable `0x007EC194`) and submits it to the existing command queue. Execution slot +0x28 points to `0x005072D1`. Command +0x0A identifies the player; +0x10 is the actor network ID; +0x14 is the technology ID. |
| Actor validation | Execution resolves player with `0x005926D2` and actor with `0x005A3D5C`, checks actor +0x90 alive, and routes kind-3 commands through `0x006B0595`. The custom-command hook preserves other native commands and validates ownership, civilization, actor type and node availability. |
| Native command distinction | `0x006714D6` converts kind-3 node commands: command 51 uses event ID; commands 36/37 use technology ID. Research completion alone is therefore not a general unit activation hook. |

`CivilizationUnitEffectBindings` reserves unused technology IDs 10000–14999, appends immediate nodes and attaches them to the source object's action list. It rejects occupied HUD slots and verifies existing icon/source identities. Costs are zero; command 51 targets verified empty event zero if the sidecar is absent. `NativeAbilityBindings.cpp` consumes registered IDs at synchronized command execution. Technology disk +176 uses caption ID 1000000+technology; +232/+236/+240 use tooltip ID 2000000+technology. The checked `UResourceFile::GetString` IAT hook at `0x007E4894` supplies those strings only for the game's Language2/Language resource instances (`0x0099FC94`/`0x0099FC90`); all other lookups remain native.

## Proximity prototype

`UnitEffectNative.cpp` implements the following guarded hooks for the exact executable above. These are supported by static disassembly, not live-game verification.

| Path | Evidence |
| --- | --- |
| Simulation update | Call `0x005B1DF8` invokes unit update `0x005B2BD2` inside the accepted network simulation tick. The adapter evaluates after the original call. Physics +0xE8 is simulation milliseconds, converted from accumulated +0xA4; timestep +0xA8 is seconds multiplied by 1000 at `0x005B2973`. Paused/wall-clock updates do not expire modeled timers. |
| Units | Players at `0x0099EED8`, count `0x0099EF30`; player +0x464 starts its owned-unit list, unit +0x0C is next. Only verified `EEComplexUnit` vtable `0x007F6C48` instances are read. Unit +0x24 is network ID, +0x18 owner, +0x64 prototype, prototype +0x1AC object ID, +0x90 health; +0x1C/+0x20 supply integer tile positions. Civilization comes from player +0xD58 technology tree, tree +0x16D4. |
| Per-unit sight | `0x006B6B35` returns sight from shared player stats +0xE8, including native status modifiers. The hook adds a cached per-instance bonus. Blind/zero-sight states remain native; adjusted visible sight is bounded to 1–32. Sight removal `0x006C29E3` runs before changing the cached bonus; sight addition `0x006C2891` runs afterward. This preserves the old radius for native movement/death removal. |
| Object reuse | Constructor `0x006AFB2B` clears cached pointer state before native construction; destructor `0x006AFD9A` clears it after native teardown. Pool checkout `0x006C0DE9` also clears recycled instances before caller initialization, because pooled objects skip construction. The sight cache additionally checks native unit ID. |

The portable core verifies range boundaries, non-stacking providers, owner changes, death, independent rules, deterministic ordering, and toggle/duration behavior. Format 3 connects proximity and activated rules to the native adapter. Activation state is not saved; new or recycled unit instances start without activation state. Save/load fog reconstruction and activation reset, actual in-game radius/button presentation, live activation and two-player synchronization remain unverified.
