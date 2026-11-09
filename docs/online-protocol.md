# Online protocol

Protocol version 1 uses HTTPS and a JSON WebSocket at `/ws`. HTTP/WS is permitted only for loopback development. JSON fields use camelCase; IDs are UUID strings.

Each command includes `type` and `requestId`. Replies use `{type:"response",requestId,ok,data}` or `{type:"response",requestId,ok:false,error}`. Server events use `{type,data}`.

## Identity and friends

`POST /v1/register` accepts `{displayName}` and returns `{userId,token,friendCode,displayName}`. The relay signs the opaque `d1` device token with `IDENTITY_SECRET`; the desktop protects it with Windows DPAPI. HTTP requests use `Authorization: Bearer <token>` and `X-User-Id: <userId>`.

The WebSocket `auth` command includes `{protocol:1,userId,token,displayName,friendReceipts}` and returns the friends snapshot. `heartbeat` keeps the session active; clients send it every 60 seconds. Friend commands are `friends.list`, `friends.request` (`friendCode`), `friends.accept` (`userId`), and `friends.remove` (`userId`). Changes emit `friends.changed`. Accepted friendships use signed `f1` receipts that identify both profiles without containing device tokens.

## Map collaboration

`collab.create` starts a session from a project. The host can invite an accepted friend with `collab.invite`; the invitee joins with `collab.join`. Users can belong to one collaboration session at a time. `collab.leave` ends a member's participation; host departure closes the session.

`collab.delta` applies map changes in relay order. `collab.project` replaces a project only when its `baseRevision` matches the current revision. `collab.sync` returns the current snapshot, and `collab.cursor` shares a member's cursor. Projects and deltas use the desktop's .NET camelCase JSON representation.

## Game rooms

`game.create` creates a room with `name`, `visibility` (`friends` or `public`), and `maxPlayers` (2–8). `game.rooms.list` returns visible rooms. Public rooms are visible to connected users; friends-only rooms are visible to the host, accepted friends, and invited players.

The host can invite an accepted online friend with `game.invite`. Players join with `game.join` and leave with `game.leave`. `game.host.set` selects which room member hosts EDMW. The room member list carries each member's room-local relay route ID; it is not a network port on the relay server. Room departure, host disconnect, or relay restart ends the room.

ICE signaling uses `game.signal` to relay offers, answers, and candidates between current room members. The relay rejects signaling between users outside the same room.

## Gameplay transport

Clients negotiate direct WebRTC peer connections after room membership changes. The lower user UUID creates the offer. ICE signaling uses the authenticated WSS connection; STUN discovery uses `stun:stun.l.google.com:19302`. Direct peers open an unordered, unreliable `edmw-game-udp` channel and an ordered, reliable `edmw-game-tcp` channel.

The Matchmaking settings independently enable ICE gameplay and WSS gameplay relay. When enabled, direct ICE is preferred; WSS can carry gameplay when a direct channel is unavailable. Disabling ICE gameplay does not disable ICE signaling. Disabling both gameplay transports prevents gameplay frames from being sent.

WSS gameplay frames use the `EDMR` header:

| Field | Size | Description |
| --- | ---: | --- |
| Magic | 4 bytes | ASCII `EDMR` |
| Type | 1 byte | `1` UDP, `2` TCP, `3` session control |
| Room ID | 16 bytes | UUID in network order |
| Destination | 2 bytes | Big-endian room route ID; `0` broadcasts UDP to other members |

Client-to-relay headers are 23 bytes. Relay-to-client headers add a 16-byte sender ID. The relay checks room membership, frame type and size, and rate limits. Per-client limits are 2,048 gameplay frames and 2 MiB of input per second; a client with more than 2 MiB buffered is disconnected. WSS fallback is room-scoped, requires no inbound port forwarding, and uses relay bandwidth. The relay can read forwarded gameplay payloads; there is no additional end-to-end encryption.

UDP payloads contain source and target game ports followed by 1–1,200 datagram bytes. TCP payloads contain one `EDWT` stream frame. Session-control payloads are opaque frames up to 16 KiB.

Direct UDP data-channel frames use `EDGU`, followed by source and target game ports and 1–1,200 datagram bytes. Room broadcasts are sent to each direct peer or relay route.

## Game bridge and virtual IPs

The temporary x86 `EDMWRelayBridge.dll` hooks the supported `TCP Protocol.dll` Winsock imports in the launched game process. It maps room route IDs `40000`–`40007` to virtual addresses `10.254.255.1`–`10.254.255.8`, then injects received datagrams into the game with the sender's virtual address. The virtual IP is a game route; it is not an ICE candidate.

The bridge maps the selected local IPv4 adapter and matching routes. It uses ICE's selected local host/base address when available, the `raddr` for server-reflexive candidates, then a gathered local IPv4 candidate or Windows' default-route adapter. It does not use the public STUN-mapped address as a local adapter. Only matching adapter and route-table addresses are rewritten.

Provider TCP connects are redirected to a local proxy. `EDWT` frames include a 16-byte stream ID, source and target game ports, a type (`1` open, `2` accepted, `3` data, `4` closed, `5` rejected), and up to 1,100 data bytes. The bridge and helper are extracted for the launch and do not replace game files. The launcher checks supported `Empires_DMW.exe` and `TCP Protocol.dll` identities before starting.

**Prepare game network** sets up the virtual address mapping and launches EDMW at its native UI. The designated host creates the game; other players use the game's **Join by IP** with the displayed host virtual address.

## Traffic logging

Optional network logging writes plain text to `%LOCALAPPDATA%\EDMW Map Designer\logs\network-traffic.log`. It records gameplay transport, protocol, direction, virtual endpoints, frame sizes, ICE candidate endpoints, and WSS relay DNS addresses; packet contents are excluded. The log rotates at 5 MiB. WSS addresses are DNS-resolved candidates, not a guarantee of the selected socket endpoint.
