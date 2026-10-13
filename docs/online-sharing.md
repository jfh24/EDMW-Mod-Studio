# Online friends, collaboration, and export

The source build adds a friends system and internet sharing to the Windows application. The release tagged v1.0.0 predates these changes. Both players need matching builds, the same relay URL, and a valid local game installation for exports. Shared map editing itself does not require the game to be installed.

Open **Friends**, enter the relay URL and a display name, then choose **Connect**. Exchange the generated friend codes, send a request, and have the recipient accept it. Accepted friends can invite each other into a shared map and send exports. Incoming requests and invitations require a deliberate acceptance. A display name is editable; the relay authenticates the underlying device identity.

The [user guide](user-guide.md#friends-and-online-sharing) describes the controls. [The protocol](online-protocol.md) specifies the desktop/server contract.

## Run the relay locally

Install Node.js 24 and run from the repository root:

```powershell
Set-Location relay
npm ci
npm test
$env:PORT = '10000'
$env:RELAY_DATA_DIR = Join-Path (Get-Location) 'data'
npm start
```

Connect the desktop to `http://localhost:10000`. Plain HTTP/WS is permitted only for loopback development; use HTTPS for a remote relay. Local identities and relationships live in the data directory, which is ignored by Git. Stop the server with Ctrl+C. Do not point verification clients at a production relay.

## Deploy to Render

The repository includes [render.yaml](../render.yaml). Commit the desktop sources, relay sources and lockfile, deployment blueprint, and documentation together to GitHub. Keep relay data and Windows credentials out of the commit.

1. In the [Render dashboard](https://dashboard.render.com), create a **Blueprint** from this repository and the branch containing these changes.
2. Review the single Node web service and attached persistent disk before creating it. The blueprint selects a paid service because durable disk storage is required. Render supports persistent disks on paid services. [Render disk documentation](https://render.com/docs/disks).
3. Deploy the blueprint. The service's root directory is `relay`, build command is `npm ci --omit=dev`, and start command is `npm start`. It binds `0.0.0.0` on Render's supplied `PORT`. The health endpoint is `/health`. Render routes HTTP and WebSockets through the same public listener. [Render web services](https://render.com/docs/web-services), [Render WebSockets](https://render.com/docs/websocket).
4. Copy the service's actual `https://<service-name>.onrender.com` URL from Render into **Friends > Relay URL** on each desktop. The client derives the secure `/ws` URL automatically.
5. Connect two matching builds, exchange friend codes, accept the request, and test a map invitation and a small export. Each export recipient must explicitly approve installation.

For manual service creation, choose Node, root directory `relay`, `npm ci`, `npm start`, and `/health`; attach a disk at `/var/data/edmw-relay` and set `RELAY_DATA_DIR` to that same path. Use Node 24 and exactly one service instance. The data path must be inside the disk mount to retain identities and friendships when the instance is replaced. The default ephemeral filesystem does not preserve changes across deploys. [Render deployment guide](https://render.com/docs/your-first-deploy).

The service name in the blueprint is a deployment default; the URL is assigned by Render. The code does not assume a particular public hostname. Deployment credentials and a provisioned Render service are not part of the repository.

## Identity and access

- Registration creates a random device credential. The server stores a hash; the desktop protects the credential with Windows DPAPI for the current Windows user. Credentials are scoped to the relay endpoint.
- A friend code is used to request a relationship. It is not an authentication credential. Requests alone do not authorize map or export access.
- The host invites an accepted online friend. Knowing a room ID does not authorize joining. Removing a friendship revokes invitations and associated participation or pending transfers.
- Map and civilization packages are downloaded by the intended recipient and reviewed against local game data. Size and SHA-256 checks protect transport integrity. Existing package validation governs supported content and installation.
- The relay operator can access relayed maps and package bytes. HTTPS/WSS encrypts network connections; the protocol does not implement end-to-end encryption or cross-device account recovery.

Back up the relay identity database in its data directory. Losing it invalidates existing credentials and friend codes. Losing the desktop's protected preferences likewise creates a new identity; friends must add that identity again. Do not copy protected credentials between Windows accounts.

## Session and export behavior

The relay orders completed map edits and maintains the current session snapshot. Separate cells merge; overlapping confirmed changes follow relay order. The desktop keeps newer local strokes visible while earlier strokes await confirmation, then rebases those pending edits over the confirmed map. Full map replacements require a matching revision; a rejected replacement is cancelled and the current shared document restored before the user retries. The application validates received map documents and deltas before displaying them. Named cursors use authenticated friend identities.

Map sessions close when the host disconnects. Relay restart, redeployment, network loss, or explicit disconnect ends the active session; save projects locally, reconnect, and invite again. Render can replace instances during deployment and maintenance, closing their WebSockets. [Render connection lifecycle](https://render.com/docs/websocket).

Online export installs on the sender and sends the same verified package to explicitly selected online friends. Export works without a map session. Each recipient reports installed, denied, or failed. Packages expire after ten minutes and their temporary bytes are deleted after completion or revocation. Map archives are limited to 32 MiB; civilization payloads to 128 MiB. Interrupted offers must be sent again after reconnecting. Existing LAN receiving settings govern LAN only; the online client receives while connected.

Use one relay instance. Rooms, sockets, invitations and transfer state are in memory; the identity database and temporary package files use the attached disk. Horizontal scaling requires shared storage and coordinated room routing before increasing the instance count. Disk capacity, bandwidth, and memory determine practical concurrency; package transfer is streamed rather than buffered as a whole by the server.

## Verification and GitHub documentation

Run the relay tests:

```powershell
Set-Location relay
npm ci
npm test
```

Run desktop/relay compatibility from the repository root after the embedded native DLL and installer templates have been built:

```powershell
.\tools\Verify-Online.ps1
```

This starts a local isolated relay and three real desktop protocol clients, checks friendship permissions, map invitation access, a 512 by 512 map, concurrent strokes, cursors, resize, opaque package delivery and decision status, reconnect, and friendship removal. It does not install packages or change game files. It preserves logs in `.verification` and stops the test server. Actual in-game installation and internet behavior still require validation with two deployed clients.

Include these documentation files and the verification results in the commit or pull request that introduces online sharing. The change description should mention the new Friends UI, both export types, the required single-instance Render service and disk, and transient session behavior. Do not commit production tokens, protected preferences, relay databases, downloaded game data, or generated executable files.

Validation recorded for this change: Release build and self-contained Windows publish succeeded with no compiler warnings or errors; all 11 Node relay tests passed; the real desktop/Node protocol check passed; the Friends/recipient-picker layout and pending-edit rebase checks passed; the existing map/LAN self-test passed. These checks used local listeners and isolated test data. A production Render deployment and two-client internet/game installation check remain to be performed.
