# Engineering Report: Manual Wildlife and Fish

**Project:** EDMW Mod Studio (Map Designer)  
**Date:** 2026-09-06  
**Status:** Parser-free native implementation passes offline verification; the reduced v7 live run loaded with all 10 approved fauna placements committed and player starts unchanged; final visual confirmation remains

## Outcome

Manual placement is implemented for the ten user-approved fauna choices:

- Water: Fish, Dolphin
- Land: Deer, Bighorn Sheep, Bear, Squirrel, Sheep, Chicken, Wild Pig, Elk

Every other fauna choice is absent from the placement palette. The old numeric values for generic `Wildlife`, Goat, Tiger, Shark, Blue Bird/Eagle, Walrus, Wolf, and Horse remain defined only so existing projects and payloads can be read without mutation; they are hidden and inert. Turkey, Vulture, Lizard, and Ox were research candidates only and were never added.

The established manual-player-start path is not part of fauna placement and was not changed. The fauna planner reads finalized player and neutral centers only to reserve six-cell exclusion disks. It never writes player slots, start coordinates, radii, member vectors, or start-topology state.

## Proved cause of the repeated hang

The signed parser-placeholder design was unsafe. Removing the placeholder records from the visible unit vector did not undo a second parser side effect: `Default.dll` had already folded their impossible coordinates into cached map bounds at generator context `+0x450/+0x454`.

The final hung process was sampled while it was consuming a full CPU core. Its hot instruction range was `Default.dll+0x1175F..0x117A9`. The live inputs included a valid neutral center `(161,81)`, but the cached bounds were `-1,048,591` and `-11,861,391`—the exact signed ordinal and map-ID placeholder coordinates. The stock player-land routine was therefore rasterizing an approximately eleven-million-cell rectangle. That is why scrubbing the visible 24-byte records did not prevent the hang.

The fix is architectural: current maps emit no manual-fauna `PlaceUnit`, `PlaceWorldUnit`, or ring directives at all. The placeholder constants, directive-emission logic, capture cache, scrubber, and related tests have been removed. The stock parser and its cached bounds now receive the same input they received before manual fauna was added.

## Direct native runtime seam

The custom DLL owns manual fauna after stock world generation:

1. The Version 7 sidecar carries authored fauna markers and capability bit `0x00000008`; its existing nine-byte resource records and enum byte values are unchanged.
2. `EERMPIMCreateTrees` first lets `Default.dll` finish Trees.
3. The DLL reads finalized topology, height, shore distance, playable-edge distance, resource reservations, occupancy, and finalized start centers.
4. It projects each authored source-grid marker into the active world exactly once and performs stable nearest-valid snapping when the exact projected cell is unavailable.
5. Fish is written as resource kind `5`. Named animals are written as validated 24-byte world-unit records into the final unified vector consumed after Resources and Trees.
6. Water and land plans commit independently. An invalid or incomplete category keeps its entire stock category.

The unified unit-vector object is at context `+0x2BC`; its x86 begin/end/capacity range is `+0x2C0/+0x2C4/+0x2C8`, and its record stride is `0x18`. Static analysis of the installed `Default.dll` vector-growth path proved that storage comes from the imported MSVCRT `operator new` and is retired with the matching `operator delete`. The DLL uses that exact allocator pair. If either the unit or resource replacement buffer is needed, both allocations are completed and validated before any shared vector field is changed.

The finalized generator height grid remains authoritative for terrain, elevation, and slope policy. Live evidence proved that the later host world-cell height field uses a different numeric scale, so it is checked only for readability and finiteness. The final cell-kind gate mirrors the host consumers exactly: Dolphin requires kind `1`; Fish accepts kinds `1` or `10`; named land units have no positive final-kind requirement. A consumer-incompatible water cell is blocked and deterministically replanned before commit.

The 24-byte unit record's final byte is genuine padding. Static analysis proved that both stock writers leave it uninitialized and that the host consumer never reads it. Direct records zero that byte deterministically rather than copying the stock writer's stack residue.

## Exact installed identities

Named descriptor indices are compiled into the known-build path and are never obtained by adding parser records. At runtime, every descriptor `+0x14` must still match its expected installed global object before the category can commit.

| Kind | Token | Descriptor | Global object |
|---|---|---:|---:|
| Deer | `Res-Deer` | 59 | 261 |
| Bighorn Sheep | `Res-BighornSheep` | 67 | 292 |
| Bear | `Res-Bear` | 72 | 301 |
| Fish | `Res-Fish` | 77 | 315 |
| Squirrel | `Res-Squirrel` | 79 | 325 |
| Sheep | `Res-Sheep` | 80 | 326 |
| Chicken | `Res-Chicken` | 93 | 372 |
| Dolphin | `Res-Dolphin` | 134 | 661 |
| Wild Pig | `Res-WildPig` | 510 | 1501 |
| Elk | `Res-Elk` | 611 | 1618 |

## Model-fidelity root cause

The v6 run proved that the direct records carried the requested descriptor/global identities, but several names rendered another animal. Installed database and archive analysis proved that this is authored EDMW data, not a failed load and not a record-layout bug. `dbobjects.dat` is 1,991 fixed `0x790`-byte records; record field `+0x130` is the graphics identity.

| Removed choice | Descriptor / global | Shipped graphics result |
|---|---:|---|
| Goat (localized by the game as Elephant) | 66 / 291 | Sheep (`Ares_xSheep_06`) |
| Tiger | 70 / 295 | Sheep |
| Shark | 76 / 314 | Dolphin (`amb_xDolphin_00`) |
| Walrus | 83 / 349 | Sheep |
| Wolf | 92 / 370 | Sheep |
| Horse | 94 / 373 | Elk (`amb_xElk_00`) |

The v6 `Res-Eagle` object is localized as Blue Bird and uses Blue Jay graphics; it rendered correctly, but Blue Bird was removed from the product at the user's direction. No parser or preload call can create distinct Goat, Tiger, Shark, Walrus, Wolf, or Horse wildlife because those distinct model/UDF pairs do not exist in the shipped data. Static analysis additionally proved that the parser's unit opcodes only append the same 24-byte record; they perform no preload side effect. The final executable consumer resolves descriptor `+0x14` to the global object registry and invokes that object's creator, which then uses the database's fixed graphics identity.

The implementation is gated to the inspected x86 builds:

- `Empires_DMW.exe`: SHA-256 `9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3`
- `Default.dll`: SHA-256 `C479CD99DA79BF879854B6A144A2D9ABC2C401915AC4CD532EB354E0F374605C`
- `dbobjects.dat`: SHA-256 `32DA362FCA9F1DB1AB2B4BE909E66C560B0F652D7EA9D6399F999C8F398A558A`
- `dbgraphics.dat`: SHA-256 `555D9D8F14FB97C17E20FCC760C22F86287B90B3C81720BBB15187CC25650BB2`
- `dbanimals.dat`: SHA-256 `77716FC764A2F17C3CEAE765C68CFC2A18A60EC23E967F265FA246C0ABCD8FE2`
- `Language2.dll`: SHA-256 `9DCC43352D7C594746473A93BA3D7C5924428F7B9DCEA0F8AC98284BD5466EEC`

## Fish and resource-grid semantics

`Default.dll` context `+0x118` is mutable resource-reservation state, not a permanent land mask. The DLL snapshots it before the temporary position-domain use and restores it before height and resource generation. Stock and manual Fish therefore see water cells as unreserved unless an actual resource has claimed them.

Context `+0x1F0` is not a universal occupancy grid. Bit `0x08` identifies eligible cells whose surface ID is zero; Fish does not use it as a rejection gate.

Manual Fish requires a playable deep-water footprint, sufficient edge/shore clearance, finite compatible height, and a clear `+0x118` reservation. Named land fauna require live land, the stock herd 2x2 eligibility/slope footprint, the Animal elevation ceiling at `+0x4E4`, resource clearance, and a valid final world cell.

## Evidence from the first parser-free acceptance run

The map loaded normally. The authored player starts remained correct, and the user observed the manual Gold, Berry, Stone, and rectangular forest. This proves that removing fauna from the RMV parser eliminated the previous CPU-bound generation hang without regressing the established starts or resource/forest paths.

The diagnostic trace then isolated a separate, non-hanging fauna defect:

- Water planned and committed all 3 authored requests. The final unit vector contained the direct Shark and Dolphin records; Fish was staged through the kind-`5` resource path.
- Land planned 14 requests but committed 0, so its transaction correctly retained the stock fallback. The nine Elk and three Wild Pig records in the final vector are exactly the animals the user saw.
- The failed land validator compared generator thresholds `0.4545455–0.5454546` with host world heights such as `24566`. The generator grid contained 24,566 land cells, but the mismatched comparison recognized only 796.
- A live world-cell matrix also showed that finalized generator and host cell kinds are not identical at every projected cell. The v6 guard therefore keeps generator topology authoritative for land while applying the exact host water-kind requirements before commit.

The incorrect cross-scale comparison was removed and regression-tested. New diagnostic events recorded every final planned placement plus the post-Trees resource vector, allowing the v6 run to prove all 17 then-authored records before the separate graphics-alias finding narrowed the product palette.

## v6 runtime result and visual-model finding

The corrected v6 map loaded successfully in process `10732`. The diagnostic handoff completed with:

- Water: 3 requested, 3 committed — Fish `(155,37)`, Shark `(225,97)`, Dolphin `(279,161)`.
- Land: 14 requested, 14 committed — exact descriptors `59, 66, 67, 70, 72, 79, 80, 81, 83, 92, 93, 94, 510, 611`.
- Final unified unit vector: 38 records total — 22 preserved initial-unit records plus exactly 16 authored named-fauna records.
- Final resource vector: 289 records with exactly one kind-`5` Fish and no stock Animal-category records.
- Manual player starts remained `(161,81)` and `(240,161)` in the runtime world.
- Resources and Trees both returned success, and the process remained responsive after loading.

Every planned water cell had the exact host-compatible kind `1`; every land record used the finalized generator topology and a readable finite host cell. The final named-unit records all carry `worldCoordinates=1`, `inRing=0`, and `engineGenerated=1`, so the post-Trees host consumer traverses them through its proved global-unit branch.

The user then visually confirmed that Fish, Chicken, Bear, Elk, Deer, Bighorn Sheep, Squirrel, Dolphin, and Blue Bird rendered correctly, while the alias objects rendered the models listed above. This separated successful placement/identity handoff from the independent shipped-graphics alias problem.

## v7 reduced-palette live result

The v7 probe removes every rejected choice and contains exactly Fish, Dolphin, Deer, Bighorn Sheep, Bear, Squirrel, Sheep, Chicken, Wild Pig, and Elk. It loaded successfully in process `23224`, which remained responsive. Runtime diagnostics proved:

- Water: 2 requested, 2 committed — Fish `(155,37)` and Dolphin `(279,162)`.
- Land: 8 requested, 8 committed — Deer `(154,83)`, Bighorn Sheep `(220,119)`, Bear `(245,167)`, Squirrel `(122,112)`, Sheep `(156,135)`, Chicken `(104,137)`, Wild Pig `(172,193)`, and Elk `(159,235)`.
- Final unified unit vector: 31 records — 22 preserved initial-unit records plus exactly 9 approved named-fauna records with descriptors `134, 59, 67, 72, 79, 80, 93, 510, 611`.
- Final resource vector: 289 records with exactly one kind-`5` Fish and no stock Animal-category records.
- Both manual player-start records remained valid and unchanged: packed runtime coordinates `P1=(161,81)` and `P2=(240,161)`.
- Resources and Trees both returned success.

No removed fauna kind occurs in the v7 project, manifest, RMV, or payload. The RMV contains no fauna unit directive or legacy sentinel coordinate.

## v8 orientation-corrected live result

The v8 probe corrected the live top-to-bottom mismatch without changing the established resource or player-start paths. It loaded successfully in process `2028`, and the user visually confirmed that everything matches the editor.

- Water: 2 requested, 2 committed — Fish final resource coordinates `(37,155)` and Dolphin `(161,280)`.
- Land: 8 requested, 8 committed — Deer `(85,159)`, Bighorn Sheep `(122,201)`, Bear `(166,241)`, Squirrel `(107,127)`, Sheep `(140,147)`, Chicken `(134,106)`, Wild Pig `(193,163)`, and Elk `(238,154)`.
- Final unified unit vector: 31 records — 22 preserved initial-unit records plus exactly 9 approved named-fauna records.
- Final resource vector: 289 records with exactly one Fish and no stock Animal-category records.
- Both manual player starts remained exactly `P1=(161,81)` and `P2=(240,161)`.
- Player and neutral invalid-position counts were both zero; Resources and Trees completed normally.
- The user confirmed that terrain, Dolphin, and all named land fauna are now correct top-to-bottom, while player starts, Fish, forest, Gold, Stone, and Berry remain correct.

## Determinism and stock preservation

- Stable request order: category, kind byte, authored Y, authored X, original sequence.
- Stable snapping order: squared distance, world Y, world X.
- No random-number calls in manual fauna planning.
- Digests include category, kind, final world coordinates, original sequence, and exact descriptor.
- An unauthored water category preserves stock Fish and stock water fauna.
- An unauthored land category preserves stock Animal and stock land fauna.
- A failed category preserves its complete stock records.
- Parser-authored and unrelated unit records are retained; only matching engine-generated stock fauna are replaceable.
- `kEnablePostGenerationPlacementRewrite` remains `false`.
- Forests and manual Berry, Gold, and Stone reconciliation remain unchanged.
- Production diagnostics remain absent unless built with `EDMW_DIAGNOSTICS=1`.

The neutral-position safeguard is separate from fauna. It is a deterministic, neutral-only, all-or-none repair that activates only if a stock neutral center is outside painted land. Valid neutral centers are left alone, and player-start records remain byte-unchanged.

## Offline verification

| Check | Result |
|---|---|
| Native x86 `/W4 /WX` build | Passed |
| Native placement tests | `ResourceAlignment tests passed.` |
| Managed generation/persistence matrix | `SELF-TEST PASS (7545 chars, 39.1% land)` |
| No manual-fauna RMV directives | Passed for mixed, Fish-only, all-named, duplicate, reordered, and acceptance maps |
| No legacy signed sentinel coordinates | Passed |
| Source-to-world projection exactly once | Passed |
| Exact token/descriptor/global identity matrix | All 9 approved named fauna passed; Fish uses the separately proved kind-`5` consumer |
| Fish deep-water and reservation gates | Passed |
| Land terrain/elevation/eligibility/slope gates | Passed |
| Final world-height scale regression (`24566` land) | Passed; no generator threshold is applied to host height |
| Exact host water-kind gates | Passed: Dolphin `1`; Fish `1` or `10`; removed Shark has no active policy |
| Exact active palette | Passed: only the 10 user-approved fauna choices; removed and research-only candidates absent |
| Category rollback and stock preservation | Passed |
| Supported playable-mask edge/coast handling | Passed |
| Determinism | 100 identical-peer repetitions passed |
| Valid empty unit/resource vector reading | Passed source audit and native build |
| Engine vector allocator/ownership | Proved by installed `Default.dll` disassembly and independent source audit |
| Post-Trees consumer timing | Proved: unit consumer call `0x007987C3`, Fish/resource consumer call `0x007987CA` |
| Unit record byte 23 | Proved unused padding; zeroed deterministically |
| Player-start isolation | Independent audit passed; starts are read-only in the fauna path |

## Final artifacts

- Production native DLL: 252,928 bytes; SHA-256 `C9D41494FF2F610E204E3B163F051533422AD64E857370CD349274CC4B6453E5`
- Production application: 152,860,501 bytes; SHA-256 `4AD85541D741CD37C6699D79239DFBD31952E32BEEE8624B5E1E7064B2018FE8`
- Production DLL diagnostic-string audit: passed; diagnostic event and log-path strings are absent
- Acceptance schema: `edmw-fauna-acceptance-v8`
- Acceptance installer: SHA-256 `161FE56E805CE963DFED1DFB089B2E54142EE2A977192AA6879123B05EB50713`
- Acceptance package: SHA-256 `8769CD51153FE3E4C283F85B55BD6B0CE30ABED3FC162F5260CD51B6E8D9285B`
- Packaged diagnostic DLL: 302,080 bytes; SHA-256 `996D387935FE946CD396D5FE7C358390FB9876B618D1B09EFD55CFC35E7270CB`
- Acceptance RMV: SHA-256 `118544B3336CFF4A7280C14667CD460E0825E5E98A64721C0ACF0A5C958261B9`; zero fauna directives, removed fauna names, and legacy sentinel coordinates
- Acceptance payload: SHA-256 `E4BBC05A09AC1BE81280AD38716F658EBC5BB0286422F4699049079B11DC3A64`

## Accepted orientation correction

The v7 visual run exposed a record-boundary difference that was not visible in the offline topology checks: terrain, Dolphin, and every named land unit were mirrored top-to-bottom relative to the editor, while player starts, Fish, forests, Gold, Stone, and Berry were already correct. The native code now keeps two explicit projections. Terrain semantics and direct named-unit records use the editor-Y-mirrored projection; resource/tree records retain their existing one-time coordinate transpose; the established manual-player-start writer remains unchanged. The v8 live run passed this correction and is the accepted final behavior.
