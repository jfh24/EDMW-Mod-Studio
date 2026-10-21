# Permanent effect spawning

The supported executable is the same SHA-256 checked by the effects launcher.
The following evidence comes from offline disassembly, without launching or
modifying the game. In-game acceptance remains a separate manual check.

## Native creation ABI

`EEObjectPrototype` virtual slot `+0x48` takes six four-byte arguments:
`(int32 x, int32 y, uint32 internalPlayerIndex, bool suppressCreationEvents,
void* producer, float heading)`, with the prototype in ECX and `ret 0x18`.
The base implementation is `0x00703C3F`. Its supported subtype overrides are
`0x006D7686`, `0x006D9500`, `0x006DE5E5`, `0x006DD49A` and `0x006DF0E8`.
The native replacement caller at `0x0072D953..0x0072D978` pushes those exact six
arguments. The adapter passes false for suppressing creation events and null
for a producer. It never constructs native units in its own allocator.

The base factory resolves the owner by world index (`0x00703C7D..0x00703C89`),
creates the native pooled complex actor (`0x0070400D..0x0070401B`), initializes
its subtype, inserts it in the owner's list (`0x00704088..0x00704095`), and
queues its world registration (`0x007040A4..0x007040AD`). Native methods retain
population/type counts, occupancy, fog and lifecycle bookkeeping. The returned
object's `+0x68` points to its complex actor. The adapter validates the actor's
vtable, owner, prototype and health before returning it. A valid newly created
actor can still have `UINT32_MAX` in its ID field until native registration;
immediate world-ID lookup is not a creation-success condition.

The call at `0x007040AD` reaches `0x005A3DBF`, which retains the actor and appends
an eight-byte `(actor, producer)` pair to world `+0x04` using `0x005A3F59` /
`0x005A3F7C`. Base actor initialization `0x006F2DF2` calls `0x0071471F` /
`0x00714759`, resetting actor `+0x24` to `UINT32_MAX`. The separate player
registration routine `0x005DBF53` assigns the player-prefixed ID at
`0x005DBF62..0x005DBF83` and inserts it into the owner's lookup at `0x005DBF95`.
ID-dependent effect entry tracking must wait for that native registration;
successful native creation must not trigger a refund while the actor is pending.

The building initializer `0x006D7BDB` calls `0x006BC86C(false,true,true)` at
`0x006D7BFE..0x006D7C10`, creating its normal foundation state. Native scenario
placement then finishes a building with `0x006BC86C(true,false,true)` at
`0x0063BA08..0x0063BA10`; effects use that same transition. Completion, render
state and gameplay registration are native operations, never direct flag writes.

## Placement

The native footprint field is canonical prototype `+0x284`, a square grid size.
`0x0072BC9E` iterates all tiles in a footprint and calls `0x0073B25B` for each.
The latter is cdecl `(owner,objectId,x,y,placementMode,flag,flag)`, returning AL.
Its full implementation `0x0073B2E1` checks terrain type, slope, boundaries,
occupancy and native player construction restrictions. The adapter uses the
placement mode that bypasses selection-dependent movement-connectivity checks
(`0x0073B3CC..0x0073B3D0`); effect spawning must not depend on HUD selection.
The remaining two flags are false. All prospective footprint tiles must pass.

Map size comes from `*0x0099EF20 + 0x150`, also used by the native factory.
An outer tile margin preserves the predicate's adjacent-terrain reads. Search
uses fixed clockwise square rings around the affected actor, no RNG, and
retains deterministic limits of 1024 candidate footprints and 16384 native
tile checks per created object. An obstructed search creates nothing. Subsequent
objects see native occupancy from earlier objects. Placement order, boundaries,
64 unique placements, invalid inputs and the candidate budget have portable
tests in `UnitEffectSpawnNativeTests.cpp`.

All calls occur from effect action dispatch after completing actor-list
iteration. There are no game-file writes or global prototype/statistics edits.
