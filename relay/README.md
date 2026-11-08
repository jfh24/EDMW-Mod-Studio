# EDMW online relay

Node.js 24 HTTP/WebSocket relay for EDMW Mod Studio. It provides device
identities, accepted friendships and online presence, invitation-only map
collaboration, public and friends-only game rooms, authenticated ICE signaling,
room-scoped gameplay frames over WebSocket, and map/civilization package
delivery with recipient decisions.

The desktop matchmaking UI launches the supported game build with the x86
provider hook. Players host or join through the game's native TCP/IP LAN flow.
ICE uses STUN for preferred direct peer connections. If direct ICE is
unavailable, authenticated members can exchange UDP, TCP, and setup frames
through the same WebSocket connection used for matchmaking. The relay verifies
room membership, destination, frame bounds, and per-client rate limits. This
does not require inbound port forwarding; relay latency and bandwidth apply.
Live two-PC cross-internet game validation remains pending. See the
[wire protocol](../docs/online-protocol.md) for room permissions and frame
limits.

## Run locally

From this directory:

```powershell
npm ci
npm test
$env:PORT = '10000'
$env:RELAY_DATA_DIR = Join-Path (Get-Location) 'data'
npm start
```

Connect the desktop's **Friends > Relay URL** to `http://localhost:10000`.
Use HTTPS/WSS for public deployments. Health check: `GET /health`.

The bounded public-service checks are `tools/Verify-LiveRelay.mjs` and
`tools/Verify-LiveOnline.ps1` in the repository root. See the
[verification instructions](../docs/online-sharing.md#verification-and-github-documentation)
and [deployed relay test record](../docs/live-relay-verification.md).

## Deploy on Render

Use the repository's root [render.yaml](../render.yaml) as a Render Blueprint.
The service root is `relay`, build command is `npm ci --omit=dev`, and start
command is `npm start`. The blueprint uses the **Free** plan with no disk or
database. It generates `IDENTITY_SECRET` once; keep that environment value
unchanged across redeploys. `RELAY_DATA_DIR=/tmp/edmw-relay` holds disposable
working files. Copy the actual onrender.com URL into the desktop client.

See the [deployment and operations guide](../docs/online-sharing.md) and
[protocol specification](../docs/online-protocol.md). Source repository:
[jfh24/EDMW-Mod-Studio](https://github.com/jfh24/EDMW-Mod-Studio).

Free Render services sleep after idle time and discard local files on restart.
The desktop stores relay-signed device credentials and acceptance receipts;
both friends must confirm the same saved approval to recover sharing access.
Rooms, pending requests, and package offers are transient. Local development
without `IDENTITY_SECRET` generates a key inside `data`; Render requires the
environment secret. Do not commit data or `.env`. The relay operator can access
relayed content; the protocol uses transport encryption and does not implement
end-to-end encryption. See [Render's free limits](https://render.com/docs/free).
