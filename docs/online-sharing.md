# Online sharing and relay setup

EDMW Mod Studio uses an HTTPS/WebSocket relay for friends, online map collaboration, exports, and game matchmaking. Use the [user guide](user-guide.md) for desktop workflows and the [online protocol](online-protocol.md) for message and traffic details.

## Connect with friends

Open **Friends**, enter a display name, and choose **Connect**. The default relay is `https://edmw-mod-studio.onrender.com`. To use another relay, expand **Relay settings** and edit **Relay URL** while disconnected. Exchange friend codes and accept requests before inviting friends to map sessions, exports, or matchmaking rooms. Both players must use the same relay.

## Matchmaking

Open **Friends > Matchmaking** to create a public or friends-only room, invite an accepted friend, or join an available room. The room host can assign the EDMW game host. Each player chooses **Prepare game network** to open the game's native multiplayer UI with a room virtual IP mapping. The designated host creates a game in EDMW; other players join with **Join by IP** using the host address shown in Matchmaking.

The gear beside **Current room** opens settings for direct ICE gameplay, WSS gameplay relay, and optional traffic logging. Settings persist between launches. Direct ICE is preferred when enabled and available; room gameplay can use the authenticated WSS connection when direct gameplay is unavailable or disabled. Login, room control, and ICE signaling continue to use WSS. Disable ICE gameplay on each player and keep WSS gameplay enabled to route gameplay through the relay.

The optional text log is at `%LOCALAPPDATA%\EDMW Map Designer\logs\network-traffic.log`. It records gameplay frame direction, protocol, endpoints, and ICE/WSS address metadata without packet contents. WSS server addresses in the log are DNS-resolved candidates.

## Host a relay

The relay is a separate Node.js service. The included [Render blueprint](../render.yaml) deploys it on Render's Free plan without a persistent disk or external database.

For local development, install Node.js 24 and run:

```powershell
Set-Location relay
npm ci
$env:PORT = '10000'
$env:RELAY_DATA_DIR = Join-Path (Get-Location) 'data'
npm start
```

In **Friends > Relay settings**, connect to `http://localhost:10000`. Plain HTTP/WS is supported only for loopback development; public relays must use HTTPS/WSS.

For Render, create a Blueprint from this repository. The service uses the `relay` root directory, `npm ci --omit=dev` as its build command, `npm start` as its start command, and `/health` as its health check. Keep the generated `IDENTITY_SECRET` unchanged across deployments. The default public relay is `https://edmw-mod-studio.onrender.com`.

## Identity and availability

The desktop protects its relay credential with Windows DPAPI and keeps a separate identity per relay endpoint. Accepted friendships are saved by both clients. Keep the relay signing secret stable so saved identities and approvals remain valid.

The relay keeps rooms, invitations, and active transfers in memory. A restart, redeploy, or disconnect ends them. Run one relay instance; horizontal scaling requires shared state and coordinated room routing. The desktop reconnects to the configured relay automatically when it starts.

Map archives are limited to 32 MiB and civilization packages to 128 MiB. Pending exports expire after ten minutes. Recipients review packages through the normal validation and approval flow. Do not commit relay secrets, user credentials, caches, game data, or generated executables.
