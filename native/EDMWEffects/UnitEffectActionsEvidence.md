# Native unit ownership and transformation outputs

These mappings were verified by read-only disassembly of the supported
`Empires_DMW.exe`, SHA-256
`9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3`.
No game, editor, or GUI was launched. Runtime gameplay acceptance remains separate
from this static ABI evidence.

| Operation | Verified native route |
| --- | --- |
| Ownership | `0x0073A285` is `void* __cdecl(unit, playerIndex, bool, bool, bool)`. Its complete six-byte prologue is `55 8B EC 83 EC 74`; the function returns the replacement complex unit in EAX at `0x0073AADF` and uses a plain return at `0x0073AAE5`. The normal conversion caller at `0x00702E02..0x00702E0A` supplies `(unit, newOwner+0x478, 1, 1, 0)`. The separate tribute command at `0x0050C96C..0x0050C97B` instead supplies `(unit, destinationIndex, 0, 1, 1)` and cleans 20 stack bytes. The adapter uses those tribute handoff arguments, preserving the exact unit/building type rather than conversion's civilization-equivalent replacement. Tribute purchase logic runs in its command handler before this lifecycle call (`0x0050C925`), and is not invoked by the adapter. |
| Transformation | `0x0072CF16` is `void* __cdecl(unit, playerIndex, targetPrototype, removeOriginal)`, with prologue `55 8B EC 83 EC 10`, plain return at `0x0072D11E`, and replacement complex unit returned in EAX at `0x0072D118`. The scenario action requires requested prototype `+0x1A4 == 1` at `0x0065B495..0x0065B49C`, then at `0x0065B553..0x0065B573` resolves the object from `0x009D1BF8`, reads the current owner's player index, calls with `removeOriginal = 1`, and cleans 16 stack bytes. The adapter preserves this complex-object restriction. Research replacement callers at `0x0066A484`, `0x0066A4F9`, and `0x0066B532` use the same route. |
| Native replacement | Both routes delegate to `0x0072D6F0`, whose prologue is `55 8B EC 83 EC 7C`. It selects the requested/current owner, retains native coordinates/orientation, creates the target through prototype virtual `+0x48` (`0x0072D95A..0x0072D978`), copies the original through new-unit virtual `+0x08` (`0x0072D986..0x0072D98B`), and uses native world/owner registration. The transformation wrapper preserves health fraction (`0x0072D000..0x0072D082`), updates physics (`0x0072D091`), invokes the old prototype's replacement callback (`0x0072D0A8`), and retires the original through unit virtual `+0x78` (`0x0072D0BB`). The ownership wrapper additionally updates owner unit collections (`0x0073A345..0x0073A369`), selection (`0x0073A971`), and original retirement (`0x0073A9AB`). The third flag being false omits conversion-specific score bookkeeping (`0x0073A9CE..0x0073AA4F`). |
| Prototype registry | Initialization at `0x0070899C..0x007089A6` clears exactly `0x801` (2049) entries starting at `0x009D1BF8`. Requested indices are bounded before indexing, and prototype `+0x1AC` must match. The supported EEUC subclasses use creation virtual `+0x48` values `0x00703C3F` (ordinary units), `0x006D7686` (building), `0x006D9500` (gate), `0x006DE5E5` (mobile building), `0x006DD49A` (mining building), or `0x006DF0E8` (naval building). RTTI identifies these tables and their factory slots. |

The adapters validate the live player registry and tree, the canonical global
prototype, the owner's bounded canonical statistics vector, and the supported
complex-unit layout. They reject actors absent from the map (including the native
`-10000` garrison coordinates). The caller must restore per-instance custom
projections before entry, then stop using the old actor and revalidate the returned
identity. No raw owner, prototype, player list, or selection field is assigned by
the adapter. Same-owner and same-type requests preserve the original actor.

The stock tribute fifth flag bypasses the conversion/civilization path except
when category 34 from the pointer vector at `0x00877678` both contains the source
object and sets byte `+0x1A5`. The native check at `0x0073A388..0x0073A3C5` calls
`bool __thiscall 0x0051A29A(category, object, 0)` (nine-byte prefix
`53 56 57 8B 7C 24 10 32 C0`, `ret 8` at `0x0051A326`). The adapter validates the
vector and applies the same predicate before any ownership mutation. Such an
exceptional object is rejected rather than silently changing its type. Actual
category membership has not been established from the static catalog; there is
no blanket restriction by unit role or building class. For accepted objects,
the native wrapper preserves its saved original prototype and passes false for
the lower routine's civilization-mapping flag (`0x0073A451..0x0073A45C`).

Installation checks both lifecycle entry points, their shared lower-level entry,
the exact scenario/conversion/tribute caller instructions, the category predicate,
and the registry bound bytes.
The new code modifies no executable/database file and owns no game allocation.

## Deferred world registration

The native lifecycle returns a live replacement before its world ID lookup is
necessarily available. The complex-unit factory `0x006B3A47` checks out a pooled
actor and calls `0x006B7DB2`; its base initialization calls `0x006F2DF2` at
`0x006B7DF0`. That routine calls `0x0071471F`, which resets actor `+0x24` to
`UINT32_MAX` through `0x00714759`, then assigns the owner, prototype and complex
actor self-pointer at `0x006F2E1E..0x006F2E24`.

The factory's later call at `0x007040AD` goes to `0x005A3DBF`. This increments
the actor reference count and appends an `(actor, producer)` pair to the vector
at world `+0x04` through `0x005A3F59` / `0x005A3F7C`. It does not synchronously
insert into the owner's ID registry. The separate player registration routine
`0x005DBF53` assigns the player-prefixed ID at `0x005DBF62..0x005DBF83` and
inserts it through `0x005DC278` at `0x005DBF95`.

Therefore a successful replacement must not be refunded merely because its
returned actor still has `UINT32_MAX`, or because immediate `0x005A3D5C` lookup
does not return it. Validate the returned actor's live layout, owner, prototype
and pool generation first; defer ID-dependent work until normal registration
has made its identity available. The engine retains ownership of registration.
