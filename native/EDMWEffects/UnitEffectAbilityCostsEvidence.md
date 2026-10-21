# Native ability resource costs

The supported `Empires_DMW.exe` has SHA-256
`9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3`.
The following evidence is from read-only disk disassembly. No game or GUI was
launched to develop this adapter.

| Native operation | Verified ABI and behavior |
| --- | --- |
| Synchronized research purchase | The native `EEMResearchTechnology` execution handler starts at `0x005072D1`. It resolves the issuing player at `0x005072F7`, the actor at `0x0050730D`, and forwards native purchases to unit queue `0x006B0595` at `0x0050795D`. The custom ability dispatcher intercepts this existing synchronized command. |
| Affordability | Player purchase validation `0x005CCCF4` compares signed integer balances against the costs before deduction. Its all-resource loop starts with balance pointer `owner+0xEC4` and resource ID 2 at `0x005CCD6E..0x005CCD7B`. It compares each available balance against the requested amount at `0x005CCDAF..0x005CCDB2`. `0x006B0711..0x006B0735` likewise captures native balances from `owner+0xEC4` through `+0xED4` before purchasing. |
| Resource IDs | Native food = 2, wood = 3, stone = 4, gold = 5. Existing production cost adapter uses the same verified IDs. The public ability cost array is food, wood, gold, stone and explicitly maps to native IDs `{2,3,5,4}`; the contiguous copied balances therefore map via indices `{0,1,3,2}`. |
| Native deduction | Player virtual `+0xD8` is `void __thiscall(owner, uint32 resource, int32 amount)`. Base `EEPlayer` table `0x007EFA78` and `EEHumanPlayer` table `0x007E9FDC` point to `0x005D44BD`. The complete 20-byte function loads resource from stack `+4`, addresses `owner+0xEBC+resource*4`, subtracts the stack `+8` amount, and returns with `ret 8`. Native technology payment dispatches this virtual at `0x005D4608`; other payment callers include `0x005D49CE`, `0x005D49E8`, and `0x005D4A17`. |
| Computer players | RTTI identifies `EEComputerPlayer` table `0x007E770C`, whose virtual `+0xD8` is `0x00444724`. Its complete 23-byte body skips amounts <= 0 and forwards positive amounts to `0x005D44BD`, then returns with `ret 8`. The adapter accepts only these verified base/human/computer table and virtual pairs. |
| Refund | `0x005C8694` is `void __thiscall(owner, uint32 resource, uint32 amount)`, with a complete 29-byte body. It adds the amount to `owner+0xEBC+resource*4`, calls native resource statistics `0x0072DE06`, and returns with `ret 8`. The computer player's grant wrapper at `0x00439A1E` forwards both arguments to this same routine. The adapter uses this already supported native grant route for refunds and never attempts a negative payment (which the computer payment virtual would discard). |

`CanPayEffectCost` validates the registered player/tree, supported native payment
virtual, group count (1..4096), each authored cost (0..100000), and the complete
group's 64-bit products before comparing all four resources. `PayEffectCost`
repeats the complete preflight before making any native deduction. It then calls
the player's native payment virtual only for positive totals. These particular
verified payment implementations allocate nothing and invoke no intermediate
game callbacks, so a caller running on the synchronized simulation thread can
perform the whole purchase before applying any output. Insufficient resources
leave every balance untouched. No synthetic technology record is constructed,
and the adapter never directly assigns a resource balance.

The caller must reserve/validate the complete eligible activation group before
payment, charge only when enabling an ability, and avoid charging for a rejected,
already active, or toggle-off request. Warmup completion must not charge again.
`RefundEffectCost` returns a previously paid recipient's cost through the native
grant route when no output applied. As with existing research bonus grants, it
clamps to signed balance headroom if income reached the cap during warmup.
Portable tests cover exact group affordability, rejection of a group with one
short resource, count/cost bounds and overflow inputs, zero costs, complete
payment/refund round trips, and delayed-refund overflow protection.
Gameplay verification remains separate from static ABI checks and compilation.

Successful native factories enqueue world registration through `0x005A3DBF`;
the returned actor can still have world ID `UINT32_MAX`. Requiring immediate
world lookup previously misclassified a completed transformation as a failure
and refunded its price. Output completion now validates the returned live
actor, its owner and requested type. Subsequent outputs wait for registration
using the retained pointer and generation, and do not replay or charge again.
Proximity entry latches are installed when the actor receives its world ID,
before the next proximity evaluation. Genuine action-only failures still refund.

Focused action tests cover paying 50 gold before transformation, recognizing
the pending result before ID assignment, keeping the payment and successful
recipient allowance, and rejecting reused pointers or changed ownership/type.
The runtime trace records payment balances and genuine refunds for manual
gameplay verification.
