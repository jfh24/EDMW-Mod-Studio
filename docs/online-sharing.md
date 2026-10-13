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

Connect the desktop to `http://localhost:10000`. Plain HTTP/WS is permitted only for loopback development; use HTTPS for a remote relay. Local identities and relationships live in the data directory, which is ignored by Git. Stop the server with Ctrl+C. The local verification runner is restricted to loopback; use the separate bounded live runners only against an explicitly selected public service.

## Deploy to Render

The repository includes [render.yaml](../render.yaml). Commit the desktop sources, relay sources and lockfile, deployment blueprint, and documentation together to GitHub. Keep relay data and Windows credentials out of the commit.

1. In the [Render dashboard](https://dashboard.render.com), create a **Blueprint** from this repository and the branch containing these changes.
2. Confirm the Node web service uses the **Free** instance type. The blueprint provisions no disk, database, or paid service. It generates `IDENTITY_SECRET` as an environment variable. Keep that value unchanged across deployments; it verifies saved desktop identities and friend approvals.
3. Deploy the blueprint. The service's root directory is `relay`, build command is `npm ci --omit=dev`, and start command is `npm start`. It binds `0.0.0.0` on Render's supplied `PORT`. The health endpoint is `/health`. Render routes HTTP and WebSockets through the same public listener. [Render web services](https://render.com/docs/web-services), [Render WebSockets](https://render.com/docs/websocket).
4. Copy the service's actual `https://<service-name>.onrender.com` URL from Render into **Friends > Relay URL** on each desktop. The client derives the secure `/ws` URL automatically.
5. Connect two matching builds, exchange friend codes, accept the request, and test a map invitation and a small export. Each export recipient must explicitly approve installation.

For manual service creation, choose **Free**, Node 24, root directory `relay`, build `npm ci --omit=dev`, start `npm start`, and health check `/health`. Set `RELAY_DATA_DIR=/tmp/edmw-relay` and add a private `IDENTITY_SECRET` containing at least 32 random bytes encoded as base64. One way to generate it locally is `node -e "console.log(require('node:crypto').randomBytes(32).toString('base64'))"`; put the resulting value into Render's Environment settings, not GitHub. Run exactly one instance and attach no disk. The Blueprint generates the secret automatically and keeps an existing value on updates. [Render secret generation](https://render.com/docs/blueprint-spec#generating-random-secrets).

Free services sleep after 15 minutes without inbound traffic. Waking takes about a minute, and sleeping, restarting, or redeploying discards local files. The desktop waits for a valid health response before authentication, with a cancellable 150-second connection deadline. Free workspace usage allowances still apply. [Render's free-service documentation](https://render.com/docs/free).

The service name in the blueprint is a deployment default; the URL is assigned by Render. The code does not assume a particular public hostname. Deployment credentials and a provisioned Render service are not part of the repository.

## Identity and access

- Registration creates a relay-signed device credential binding the user ID and friend code. The desktop protects it with Windows DPAPI for the current Windows user. The stable environment secret allows the relay to verify the credential after losing its files. Credentials are scoped to the relay endpoint.
- A friend code is used to request a relationship. It is not an authentication credential. Requests alone do not authorize map or export access.
- The host invites an accepted online friend. Knowing a room ID does not authorize joining. Removing a friendship revokes invitations and associated participation or pending transfers.
- Explicit acceptance produces a signed friendship receipt, saved by each desktop. After a reset, the first reconnecting device retains an offline contact; sharing becomes available when both authenticated devices declare the same saved approval. A single device cannot restore access on behalf of a removed friend. The peer's current omission clears a stale receipt.
- Map and civilization packages are downloaded by the intended recipient and reviewed against local game data. Size and SHA-256 checks protect transport integrity. Existing package validation governs supported content and installation.
- The relay operator can access relayed maps and package bytes. HTTPS/WSS encrypts network connections; the protocol does not implement end-to-end encryption or cross-device account recovery.

Keep `IDENTITY_SECRET` unchanged and store a private recovery copy. Changing it invalidates signed identities and friendship approvals. The relay's data directory is a disposable cache and temporary transfer area; it does not provide durable storage on Free. Accepted-contact recovery relies on each desktop retaining its protected preferences. Losing those preferences or changing Windows accounts creates a separate identity. Do not copy protected credentials between Windows accounts. Profiles created by the earlier relay build update once to a signed identity and new friend code; exchange codes again for that one-time migration.

Pending friend requests are transient. A completed friendship survives a reset once both desktops have received and saved the approval. If approval happened while the requester was offline, the current relay can deliver it on their next connection; a reset before delivery requires a new request. Removing a friend clears the saved receipt before the app reports success. The desktop's current saved contact list is authoritative; do not restore an old profile backup to recover contacts you intentionally removed.

## Session and export behavior

The relay orders completed map edits and maintains the current session snapshot. Separate cells merge; overlapping confirmed changes follow relay order. The desktop keeps newer local strokes visible while earlier strokes await confirmation, then rebases those pending edits over the confirmed map. Full map replacements require a matching revision; a rejected replacement is cancelled and the current shared document restored before the user retries. The application validates received map documents and deltas before displaying them. Named cursors use authenticated friend identities.

Map sessions close when the host disconnects. Relay restart, redeployment, network loss, or explicit disconnect ends the active session; save projects locally, reconnect, and invite again. Render can replace instances during deployment and maintenance, closing their WebSockets. [Render connection lifecycle](https://render.com/docs/websocket).

Online export installs on the sender and sends the same verified package to explicitly selected online friends. Export works without a map session. Each recipient reports installed, denied, or failed. Packages expire after ten minutes and their temporary bytes are deleted after completion or revocation. Map archives are limited to 32 MiB; civilization payloads to 128 MiB. Interrupted offers must be sent again after reconnecting. Existing LAN receiving settings govern LAN only; the online client receives while connected.

Use one relay instance. Rooms, sockets, invitations and transfer state are in memory; cache and temporary package files use the ephemeral filesystem. The stable signing secret lives in Render's environment configuration. Horizontal scaling requires shared state and coordinated room routing. Free compute and usage allowances determine practical concurrency; package transfer is streamed rather than buffered as a whole by the server.

The relay limits active rooms to 2,097,152 map cells in total, equivalent to eight full 512 by 512 maps. Smaller rooms share that capacity. Queued WebSocket messages and outbound buffers are also capped; slow clients are disconnected. A capacity error rejects a new session or replacement without changing the existing shared map.

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

This starts a local isolated relay and three real desktop protocol clients, checks friendship permissions, map invitation access, a 512 by 512 map, concurrent strokes, cursors, resize, package delivery and decision status, reconnect, and friendship removal. It also replaces the relay filesystem twice while retaining its signing secret, verifying stable device IDs/codes, bilateral contact recovery, and continued removal. It does not install packages or change game files. Logs stay in `.verification`; the runner stops the test server.

For a deployed service, run these separate checks from the repository root:

```powershell
node tools/Verify-LiveRelay.mjs https://edmw-mod-studio.onrender.com .verification/live-relay/node-report.json
.\tools\Verify-LiveOnline.ps1 -Endpoint https://edmw-mod-studio.onrender.com
```

Use the URL of the relay you intend to test. The Node runner verifies HTTP/WSS permissions, ordered edits, malformed-delta rejection, byte integrity, export decisions, cancellation and reconnect. The desktop runner adds actual C# serialization and valid map/civilization package codec checks. Both use temporary named profiles and clean up their own friendships, rooms and transfers. Credentials stay in memory; reports contain no tokens or friendship receipts. Neither runner restarts the service or installs game files. They simulate the `installed` acknowledgement to verify status delivery. A real two-machine UI and in-game installation check remains separate.

Include these documentation files and verification results in the GitHub change. Mention the Free service with no disk/database, stable signing secret, desktop receipt recovery, cold-start handling, both export types, and transient sessions/requests. Do not commit signing secrets, production tokens, protected preferences, relay caches, downloaded game data, or generated executables.

The relay suite covers free-service filesystem loss, stable signed identities, bilateral restore, removed-friend replay, tampered credentials/receipts, and the existing map/export behavior. The desktop integration checks the same recovery flow across the actual C#/Node protocol. Deployed-service results and remaining coverage are recorded in [live relay verification](live-relay-verification.md).

Validation for the Free-plan change: all 21 relay tests passed; Release compilation succeeded with no warnings or errors; the desktop/Node integration passed across two complete filesystem replacements and checked wake-up 503/HTML responses plus cancellation. No Render resources or paid services were created by these checks.
