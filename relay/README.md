# EDMW online relay

Node.js 24 HTTP/WebSocket relay for EDMW Mod Studio. It provides device
identities, accepted friendships and online presence, invitation-only map
collaboration, and map/civilization package delivery with recipient decisions.

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

## Deploy on Render

Use the repository's root [render.yaml](../render.yaml) as a Render Blueprint.
The service root is `relay`, build command is `npm ci --omit=dev`, and start
command is `npm start`. Run one instance and keep `RELAY_DATA_DIR` inside the
persistent disk. The included blueprint selects a paid starter service with
durable storage. Copy the actual onrender.com URL into the desktop client.

See the [deployment and operations guide](../docs/online-sharing.md) and
[protocol specification](../docs/online-protocol.md). Source repository:
[jfh24/EDMW-Mod-Studio](https://github.com/jfh24/EDMW-Mod-Studio).

Identities and friendships persist; rooms and package offers end on disconnect
or server restart. Do not commit the data directory or `.env`. The relay
operator can access relayed content; the protocol uses transport encryption
and does not implement end-to-end encryption.
