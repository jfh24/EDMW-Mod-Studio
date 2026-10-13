# Deployed relay verification — 2026-10-13

Relay tested: [https://edmw-mod-studio.onrender.com](https://edmw-mod-studio.onrender.com).
The tests used HTTPS and WSS against the deployed Node service. The relay runtime
sources were unchanged; the findings below required a desktop client update.

| Verification | Result |
| --- | --- |
| Independent Node protocol suite | 10 of 10 check groups passed; 54,945 application bytes transferred. |
| Actual Windows desktop protocol clients | 11 of 11 check groups passed in 35.6 seconds, including a 512 by 512 map and real package codecs. |
| Isolated desktop/Node regression suite | Passed, including two complete relay filesystem replacements, signed identity/contact recovery, removed-contact persistence, and simulated 503/HTML wake responses with cancellation. |
| Release compilation and runner syntax | Passed; compilation had no warnings or errors. |

The live checks covered:

- Three isolated device profiles, signed identities, friend requests, explicit
  acceptance, matching signed receipts, online presence and stranger rejection.
- Invitation-only rooms, concurrent map strokes, authoritative synchronization,
  stale replacement rejection, malformed delta atomicity, authenticated cursors
  and whole-map replacement. Desktop validation also rejects incompatible grids.
- Map and civilization routing, sender-only upload, recipient-only download,
  exact bytes and SHA-256 checks, corrupt upload rejection, and recipient decisions.
  The desktop transferred a valid 152,711-byte map archive with preview and native
  payload, plus a 1,334-byte synthetic civilization package. Both decoded and
  validated after download; incorrect advertised hashes and sizes were rejected.
- Cancellation before upload, withdrawal of a ready offer, recipient disconnect,
  host room closure, reconnect with the same IDs/codes/receipt, and unfriend
  revocation of pending exports and future sharing.

The initial desktop run exposed abort-only socket teardown: disconnecting the
host locally did not notify the deployed relay within the verification deadline.
The client now ends local state immediately, sends a bounded normal WebSocket
close, and waits for complete cleanup before reconnecting. It ignores data from
detached sockets and uses an abort only when orderly close fails or times out.

A normal client close was acknowledged before the relay reported the recipient
offline. Export revocation and offline presence were confirmed 10.11 seconds
after initiating the recipient disconnect in the final run. The live verifier
allows twenty seconds for that result and also verifies offline presence. A
client-side close acknowledgement alone is not treated as server cleanup proof.

The runners removed their own test friendships, ended their rooms/transfers and
disconnected their sockets. Offline test identity records remain in the relay's
disposable cache because there is no account-delete endpoint. Credentials and
friendship receipts were kept in memory and excluded from reports and Git.

No game installation was performed. The `installed` decision was a transport
acknowledgement simulation; a real two-machine UI and in-game installation check
remains outstanding. The live tests did not force a Render restart, wait for
idle spin-down or ten-minute expiry, or exercise capacity/stress limits. Restart
recovery and cold-start response handling were verified with the isolated relay.

Reproduction commands and prerequisites are in the
[online sharing guide](online-sharing.md#verification-and-github-documentation)
and [development guide](development.md). Raw JSON reports and logs are local,
ignored artifacts under `.verification`; this record is the GitHub evidence.
