# EDMW online relay

The relay is a Node.js 24 HTTP/WebSocket service for EDMW Mod Studio. It handles device identities, friends, presence, map collaboration, matchmaking rooms, ICE signaling, room-scoped gameplay forwarding, and package delivery.

The desktop prefers direct ICE gameplay when enabled and available. Authenticated room members can use the WSS connection to forward UDP, TCP, and session-control traffic when a direct gameplay channel is unavailable. The relay enforces room membership, frame bounds, and rate limits. See the [protocol guide](../docs/online-protocol.md).

## Run locally

From this directory:

```powershell
npm ci
$env:PORT = '10000'
$env:RELAY_DATA_DIR = Join-Path (Get-Location) 'data'
npm start
```

Connect the desktop to `http://localhost:10000` in **Friends > Relay settings**. Loopback HTTP/WS is for development; public deployments must use HTTPS/WSS. Health check: `GET /health`.

## Deploy on Render

Create a Render Blueprint from the repository's [render.yaml](../render.yaml). It deploys the `relay` directory with `npm ci --omit=dev`, starts with `npm start`, and uses `/health` as the health check. The blueprint uses Render's Free plan and no persistent disk or database.

Keep the generated `IDENTITY_SECRET` unchanged across deployments. `RELAY_DATA_DIR` holds temporary state. Copy the service's actual `onrender.com` URL into the desktop client. See the [deployment guide](../docs/online-sharing.md#host-a-relay).

The service keeps rooms, pending requests, and package offers in memory; they end when the service restarts. Render may sleep after idle time and discards local files on restart. Do not commit relay data, `.env` files, or secrets. The relay can read relayed content; transport encryption is not end-to-end encryption.
