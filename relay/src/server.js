import http from 'node:http';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, readFile, writeFile, rename, unlink, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes, randomUUID, createHash, timingSafeEqual } from 'node:crypto';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { WebSocket, WebSocketServer } from 'ws';
import { applyDelta, validateProject } from './project.js';
import { identitySigner, loadIdentitySecret } from './identity.js';

const MiB = 1024 * 1024;
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const hashToken = token => createHash('sha256').update(token).digest('hex');
class PublicError extends Error {
  constructor(message, status = 400) { super(message); this.status = status; }
}
function check(condition, message, status) { if (!condition) throw new PublicError(message, status); }
function validId(value) { return typeof value === 'string' && idPattern.test(value); }
function name(value, max = 64) {
  check(typeof value === 'string' && value.trim().length > 0 && value.length <= max && !/[\x00-\x1f\x7f]/.test(value), 'Invalid name.');
  return value.trim();
}
function jsonObject(value) {
  check(value && typeof value === 'object' && !Array.isArray(value), 'Expected a JSON object.');
  const queue = [[value, 0]];
  while (queue.length) {
    const [next, depth] = queue.pop();
    check(depth <= 64, 'JSON nesting is too deep.');
    for (const nested of Object.values(next)) if (nested && typeof nested === 'object') queue.push([nested, depth + 1]);
  }
  return value;
}
async function discard(file) { await unlink(file).catch(error => { if (error.code !== 'ENOENT') throw error; }); }

export async function createRelay(options = {}) {
  const dataDir = path.resolve(options.dataDir ?? process.env.RELAY_DATA_DIR ?? process.env.DATA_DIR ?? './data');
  const exportDir = path.join(dataDir, 'exports');
  const dbFile = path.join(dataDir, 'identities.json');
  const now = options.now ?? Date.now;
  const exportTtlMs = options.exportTtlMs ?? 10 * 60 * 1000;
  const heartbeatMs = options.heartbeatMs ?? 30_000;
  const maxWsBytes = 16 * MiB;
  const maxExportBytes = 128 * MiB;
  const maxRoomCells = 8 * 512 * 512;
  let queuedMessageBytes = 0;
  const connections = new Map(), sessions = new Map(), userSessions = new Map(), offers = new Map();
  const registrationRates = new Map();
  let commandQueue = Promise.resolve(), stopping = false;
  await mkdir(exportDir, { recursive: true });
  const signer = identitySigner(await loadIdentitySecret(dataDir, options));
  // Packages and rooms are intentionally transient; a restart never replays an offer.
  for (const file of await readdir(exportDir)) if (/^[0-9a-f-]{36}\.(?:part|bin)$/.test(file)) await discard(path.join(exportDir, file));
  let db;
  try { db = JSON.parse(await readFile(dbFile, 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; db = { version: 1, users: {}, friendships: [], requests: [] }; }
  check(db.version === 1 && db.users && Array.isArray(db.friendships) && Array.isArray(db.requests), 'Relay identity database is invalid.');
  db.receipts ??= {};
  db.revokedReceiptHashes ??= {};
  const pairKey = (a, b) => [a, b].sort().join(':');
  for (const user of Object.values(db.users)) {
    user.declared ??= Boolean(user.tokenHash);
    user.declaredReceipts ??= [];
    user.pendingReceiptDelivery ??= [];
  }
  // Existing disk-backed relays migrate accepted pairs to signed receipts.
  for (const [a, b] of db.friendships) {
    const key = pairKey(a, b);
    db.receipts[key] ??= signer.receipt({ userId: a, ...db.users[a] }, { userId: b, ...db.users[b] });
    for (const id of [a, b]) if (!db.users[id].declaredReceipts.includes(db.receipts[key])) db.users[id].declaredReceipts.push(db.receipts[key]);
  }

  function enqueue(action) {
    const result = commandQueue.then(action);
    commandQueue = result.catch(() => {});
    return result;
  }
  async function updateDatabase(action) {
    const candidate = structuredClone(db);
    const result = action(candidate);
    const temp = `${dbFile}.${randomUUID()}.tmp`;
    try {
      await writeFile(temp, JSON.stringify(candidate), { mode: 0o600 });
      await rename(temp, dbFile);
      db = candidate;
    } finally { await discard(temp); }
    return result;
  }
  function profile(userId) {
    const user = db.users[userId];
    return { userId, displayName: user.displayName, friendCode: user.friendCode, online: isOnline(userId) };
  }
  function isOnline(userId) { return connections.get(userId)?.readyState === WebSocket.OPEN; }
  function friendIds(userId) { return db.friendships.filter(pair => pair.includes(userId)).map(pair => pair.find(id => id !== userId)); }
  function areFriends(a, b) { return a !== b && db.friendships.some(pair => pair.includes(a) && pair.includes(b)); }
  function contactIds(userId) {
    const ids = new Set(friendIds(userId));
    for (const receipt of db.users[userId].declaredReceipts) {
      const other = signer.verifyReceipt(receipt).users.find(user => user.userId !== userId);
      if (other) ids.add(other.userId);
    }
    return [...ids];
  }
  function snapshot(userId) {
    return {
      user: profile(userId), friends: contactIds(userId).map(id => ({ ...profile(id), online: areFriends(userId, id) && isOnline(id) })),
      friendReceipts: db.users[userId].declaredReceipts,
      incoming: db.requests.filter(pair => pair[1] === userId).map(pair => profile(pair[0])),
      outgoing: db.requests.filter(pair => pair[0] === userId).map(pair => profile(pair[1])),
    };
  }
  function wireSend(socket, value) {
    if (!socket || socket.readyState !== WebSocket.OPEN) return;
    const payload = JSON.stringify(value);
    const buffered = [...wss.clients].reduce((sum, client) => sum + client.bufferedAmount, 0);
    if (socket.bufferedAmount > 32 * MiB || buffered + Buffer.byteLength(payload) > 64 * MiB) {
      socket.close(4008, 'Relay backpressure limit reached; reconnect and synchronize.');
      const timeout = setTimeout(() => socket.terminate(), 1000); timeout.unref(); return;
    }
    socket.send(payload, error => { if (error) socket.terminate(); });
  }
  function send(socket, type, data) { wireSend(socket, { type, data }); }
  function reply(socket, requestId, data, error) {
    wireSend(socket, error ? { type: 'response', requestId, ok: false, error } : { type: 'response', requestId, ok: true, data });
  }
  function notifyFriends(userId) {
    for (const id of new Set([userId, ...contactIds(userId), ...db.requests.filter(p => p.includes(userId)).flat()]))
      if (connections.has(id)) send(connections.get(id), 'friends.changed', snapshot(id));
  }
  function sessionSnapshot(room) {
    return { sessionId: room.id, revision: room.revision, project: room.project, members: [...room.members].map(profile) };
  }
  function roomFor(userId, sessionId) {
    check(validId(sessionId), 'Invalid session ID.');
    const room = sessions.get(sessionId);
    check(room && room.members.has(userId) && userSessions.get(userId) === sessionId, 'You are not a member of this session.', 403);
    return room;
  }
  function broadcast(room, type, data, except) {
    for (const id of room.members) if (id !== except) send(connections.get(id), type, data);
  }
  function leave(userId, message = 'The host left the collaboration session.') {
    const room = sessions.get(userSessions.get(userId));
    if (!room) return;
    if (room.hostId === userId) {
      broadcast(room, 'collab.ended', { sessionId: room.id, message });
      for (const id of room.members) userSessions.delete(id);
      sessions.delete(room.id);
    } else {
      room.members.delete(userId); userSessions.delete(userId);
      send(connections.get(userId), 'collab.ended', { sessionId: room.id, message: 'You left the collaboration session.' });
      broadcast(room, 'collab.members', { sessionId: room.id, members: [...room.members].map(profile) });
    }
  }
  async function removeOffer(offer, status, message) {
    if (!offers.delete(offer.id)) return;
    const closed = [...offer.streams].map(stream => new Promise(resolve => {
      if (stream.closed) return resolve();
      stream.once('close', resolve);
      stream.destroy(new Error(message ?? 'Export closed.'));
    }));
    if (status) send(connections.get(offer.senderId), 'export.result', { exportId: offer.id, status, message: message ?? '' });
    if (status === 'failed') send(connections.get(offer.recipientId), 'export.cancelled', { exportId: offer.id, message: message ?? 'Export cancelled.' });
    await Promise.all(closed);
    await Promise.all([discard(offer.file), discard(offer.partial)]);
  }
  async function revokeUserExports(userId, message) {
    for (const offer of [...offers.values()]) if (offer.senderId === userId || offer.recipientId === userId) await removeOffer(offer, 'failed', message);
  }
  function offerEvent(offer) {
    return { exportId: offer.id, from: profile(offer.senderId), kind: offer.kind, name: offer.name, size: offer.size, sha256: offer.sha256 };
  }
  async function revokeFriendship(a, b) {
    for (const room of sessions.values()) {
      if (room.hostId === a) { room.invites.delete(b); if (room.members.has(b)) leave(b); }
      if (room.hostId === b) { room.invites.delete(a); if (room.members.has(a)) leave(a); }
    }
    for (const offer of [...offers.values()])
      if ([offer.senderId, offer.recipientId].includes(a) && [offer.senderId, offer.recipientId].includes(b))
        await removeOffer(offer, 'failed', 'Friendship removed; export revoked.');
  }
  function authenticate(userId, token, recover = false) {
    check(validId(userId) && typeof token === 'string' && token.length <= 512, 'Invalid credentials.', 401);
    const user = db.users[userId], candidate = Buffer.from(hashToken(token), 'hex');
    if (token.startsWith('d1.')) {
      let identity;
      try { identity = signer.verifyDevice(token); } catch { throw new PublicError('Invalid credentials.', 401); }
      check(identity.userId === userId && (!user || user.friendCode === identity.friendCode), 'Invalid credentials.', 401);
      if (user?.tokenHash) check(timingSafeEqual(candidate, Buffer.from(user.tokenHash, 'hex')), 'Invalid credentials.', 401);
      check(recover || user?.tokenHash, 'Authenticate the device first.', 401);
      return identity;
    }
    // Legacy opaque credentials remain valid while their disk record survives.
    check(/^[A-Za-z0-9_-]{43}$/.test(token) && user && typeof user.tokenHash === 'string' && user.tokenHash.length === 64 &&
      timingSafeEqual(candidate, Buffer.from(user.tokenHash, 'hex')), 'Invalid credentials.', 401);
    return { userId, friendCode: user.friendCode };
  }
  function authorizeHttp(request) {
    const authorization = request.headers.authorization;
    check(typeof authorization === 'string' && authorization.startsWith('Bearer '), 'Authentication required.', 401);
    return authenticate(request.headers['x-user-id'], authorization.slice(7)).userId;
  }

  function parseDeclarations(userId, friendCode, values) {
    check(Array.isArray(values) && values.length <= 256, 'At most 256 friend receipts may be declared.');
    const seen = new Set();
    return values.map(receipt => {
      let proof;
      try { proof = signer.verifyReceipt(receipt); } catch { throw new PublicError('Invalid friend receipt.'); }
      const self = proof.users.find(user => user.userId === userId);
      check(self && self.friendCode === friendCode, 'Friend receipt does not belong to this device.');
      const peer = proof.users.find(user => user.userId !== userId);
      check(!seen.has(peer.userId), 'Duplicate friend receipt for one contact.'); seen.add(peer.userId);
      return { receipt, peer };
    });
  }
  async function recoverDeclarations(identity, token, displayName, declared) {
    const userId = identity.userId;
    // Older local clients which omit the new field retain their known disk state.
    // An unknown identity after an ephemeral wipe has no implicit declarations.
    const provided = declared ?? db.users[userId]?.declaredReceipts ?? [];
    const proofs = parseDeclarations(userId, identity.friendCode, provided);
    const submitted = new Set(provided);
    const pending = db.users[userId]?.pendingReceiptDelivery ?? [];
    // A request explicitly accepted while its sender was offline has one delivery
    // waiting in the current database. It is never reconstructed from a replayed proof.
    for (const receipt of pending) {
      const peer = signer.verifyReceipt(receipt).users.find(user => user.userId !== userId);
      if (!proofs.some(proof => proof.peer.userId === peer.userId)) proofs.push(...parseDeclarations(userId, identity.friendCode, [receipt]));
    }
    const outcome = await updateDatabase(next => {
      check(next.users[userId] || Object.keys(next.users).length < 100_000, 'Relay identity limit reached.', 503);
      const affected = new Set([userId]), revoked = [];
      const self = next.users[userId] = {
        ...next.users[userId], friendCode: identity.friendCode, displayName, tokenHash: hashToken(token),
        declared: true, declaredReceipts: [], pendingReceiptDelivery: pending.filter(receipt => !submitted.has(receipt)),
      };
      for (const { receipt, peer } of proofs) {
        affected.add(peer.userId);
        const digest = hashToken(receipt), known = next.users[peer.userId];
        if (next.revokedReceiptHashes[digest]) continue;
        check(!known || known.friendCode === peer.friendCode, 'Friend receipt identity mismatch.');
        // A known peer's explicit omission wins over another endpoint's old proof.
        if (known?.declared && !known.declaredReceipts.includes(receipt)) {
          next.revokedReceiptHashes[digest] = true; continue;
        }
        if (!known) {
          check(Object.keys(next.users).length < 100_000, 'Relay identity limit reached.', 503);
          next.users[peer.userId] = { displayName: peer.displayName, friendCode: peer.friendCode,
            declared: false, declaredReceipts: [], pendingReceiptDelivery: [] };
        }
        next.receipts[pairKey(userId, peer.userId)] = receipt;
        self.declaredReceipts.push(receipt);
      }
      for (const [key, receipt] of Object.entries(next.receipts)) {
        const users = signer.verifyReceipt(receipt).users.map(user => user.userId);
        if (!users.includes(userId)) continue;
        for (const id of users) affected.add(id);
        const [a, b] = users;
        const agreed = users.every(id => next.users[id].declared && next.users[id].declaredReceipts.includes(receipt));
        const rejected = next.revokedReceiptHashes[hashToken(receipt)] || users.some(id => next.users[id].declared && !next.users[id].declaredReceipts.includes(receipt));
        if (rejected) {
          next.revokedReceiptHashes[hashToken(receipt)] = true;
          delete next.receipts[key];
          for (const id of users) {
            next.users[id].declaredReceipts = next.users[id].declaredReceipts.filter(value => value !== receipt);
            next.users[id].pendingReceiptDelivery = next.users[id].pendingReceiptDelivery.filter(value => value !== receipt);
          }
          next.friendships = next.friendships.filter(pair => !(pair.includes(a) && pair.includes(b)));
          revoked.push(users);
        } else if (agreed) {
          if (!next.friendships.some(pair => pair.includes(a) && pair.includes(b))) next.friendships.push(users);
          next.requests = next.requests.filter(pair => !(pair.includes(a) && pair.includes(b)));
        }
      }
      return { affected: [...affected], revoked };
    });
    for (const [a, b] of outcome.revoked) await revokeFriendship(a, b);
    return outcome.affected;
  }

  async function command(socket, message) {
    const type = message.type;
    if (type === 'auth') {
      check(!socket.userId, 'This connection is already authenticated.');
      check(message.protocol === 1, 'Unsupported protocol version.');
      const identity = authenticate(message.userId, message.token, true), id = identity.userId;
      const displayName = name(message.displayName ?? db.users[id]?.displayName ?? 'Player');
      const affected = await recoverDeclarations(identity, message.token, displayName, message.friendReceipts);
      const previous = connections.get(id);
      // Tear down the old room before transferring a device identity to a new socket.
      if (previous) {
        leave(id, 'The host reconnected. Start a new collaboration session.');
        await revokeUserExports(id, 'Device reconnected; export cancelled.');
        previous.close(4001, 'Device connected elsewhere.');
      }
      socket.userId = id; connections.set(id, socket); clearTimeout(socket.authTimer);
      for (const contact of affected) if (connections.has(contact)) send(connections.get(contact), 'friends.changed', snapshot(contact));
      notifyFriends(id);
      return snapshot(id);
    }
    const userId = socket.userId;
    check(userId && connections.get(userId) === socket, 'Authenticate first.', 401);
    if (type === 'friends.list') return snapshot(userId);
    if (type === 'friends.request') {
      check(typeof message.friendCode === 'string' && /^[A-Z0-9]{12}$/i.test(message.friendCode), 'Invalid friend code.');
      const recipient = Object.keys(db.users).find(id => db.users[id].friendCode === message.friendCode.toUpperCase());
      check(recipient && recipient !== userId, 'Friend code was not found.');
      check(!areFriends(userId, recipient), 'You are already friends.');
      check(db.requests.filter(pair => pair.includes(userId)).length < 128, 'Too many pending friend requests.');
      check(db.requests.filter(pair => pair.includes(recipient)).length < 128, 'This user has too many pending friend requests.');
      await updateDatabase(next => { if (!next.requests.some(p => p[0] === userId && p[1] === recipient)) next.requests.push([userId, recipient]); });
      notifyFriends(userId); notifyFriends(recipient); return snapshot(userId);
    }
    if (type === 'friends.accept') {
      const other = message.userId;
      check(validId(other) && db.requests.some(p => p[0] === other && p[1] === userId), 'No incoming request from this user.');
      check((contactIds(userId).length < 256 || contactIds(userId).includes(other)) &&
        (contactIds(other).length < 256 || contactIds(other).includes(userId)), 'Friend list is full.');
      if (areFriends(userId, other)) {
        await updateDatabase(next => { next.requests = next.requests.filter(pair => !(pair.includes(userId) && pair.includes(other))); });
        notifyFriends(userId); return snapshot(userId);
      }
      const receipt = signer.receipt(profile(userId), profile(other));
      await updateDatabase(next => {
        next.requests = next.requests.filter(p => !(p.includes(userId) && p.includes(other)));
        for (const id of [userId, other]) {
          const prior = next.users[id].declaredReceipts.filter(value => signer.verifyReceipt(value).users.some(peer => peer.userId === (id === userId ? other : userId)));
          for (const old of prior) next.revokedReceiptHashes[hashToken(old)] = true;
          next.users[id].declaredReceipts = next.users[id].declaredReceipts.filter(value => !prior.includes(value));
          next.users[id].pendingReceiptDelivery = next.users[id].pendingReceiptDelivery.filter(value => !prior.includes(value));
        }
        next.receipts[pairKey(userId, other)] = receipt;
        for (const id of [userId, other]) next.users[id].declaredReceipts.push(receipt);
        if (!isOnline(other)) next.users[other].pendingReceiptDelivery.push(receipt);
        if (!next.friendships.some(p => p.includes(userId) && p.includes(other))) next.friendships.push([userId, other]);
      });
      notifyFriends(userId); return snapshot(userId);
    }
    if (type === 'friends.remove') {
      const other = message.userId;
      check(validId(other) && other !== userId && db.users[other], 'Invalid friend identity.');
      await updateDatabase(next => {
        next.requests = next.requests.filter(p => !(p.includes(userId) && p.includes(other)));
        next.friendships = next.friendships.filter(p => !(p.includes(userId) && p.includes(other)));
        const key = pairKey(userId, other), receipt = next.receipts[key];
        if (receipt) {
          next.revokedReceiptHashes[hashToken(receipt)] = true; delete next.receipts[key];
          for (const id of [userId, other]) {
            next.users[id].declaredReceipts = next.users[id].declaredReceipts.filter(value => value !== receipt);
            next.users[id].pendingReceiptDelivery = next.users[id].pendingReceiptDelivery.filter(value => value !== receipt);
          }
        }
      });
      await revokeFriendship(userId, other);
      send(connections.get(other), 'friends.changed', snapshot(other)); notifyFriends(userId); return snapshot(userId);
    }
    if (type === 'collab.create') {
      check(sessions.size < 128, 'The relay has reached its session limit.');
      const project = validateProject(message.project);
      const prior = sessions.get(userSessions.get(userId));
      const cells = [...sessions.values()].reduce((sum, room) => sum + room.project.cells.length, 0);
      check(cells - (prior?.hostId === userId ? prior.project.cells.length : 0) + project.cells.length <= maxRoomCells, 'The relay has reached its map capacity.');
      leave(userId);
      const room = { id: randomUUID(), hostId: userId, members: new Set([userId]), invites: new Set(), revision: 0, project };
      sessions.set(room.id, room); userSessions.set(userId, room.id); return sessionSnapshot(room);
    }
    if (type === 'collab.invite') {
      const room = roomFor(userId, message.sessionId), other = message.userId;
      check(room.hostId === userId, 'Only the host can invite friends.', 403);
      check(validId(other) && areFriends(userId, other), 'Only accepted friends can be invited.', 403);
      check(isOnline(other), 'The friend is offline.');
      check(!room.members.has(other), 'The friend is already in this session.');
      check(room.invites.size < 256, 'Too many invitations.');
      room.invites.add(other);
      send(connections.get(other), 'collab.invite', { sessionId: room.id, from: profile(userId), mapName: room.project.name });
      return { sessionId: room.id };
    }
    if (type === 'collab.join') {
      check(validId(message.sessionId), 'Invalid session ID.');
      const room = sessions.get(message.sessionId);
      check(room && room.invites.has(userId) && areFriends(userId, room.hostId), 'An invitation from an accepted friend is required.', 403);
      check(room.members.size < 16, 'The session is full.');
      leave(userId); room.invites.delete(userId); room.members.add(userId); userSessions.set(userId, room.id);
      broadcast(room, 'collab.members', { sessionId: room.id, members: [...room.members].map(profile) }); return sessionSnapshot(room);
    }
    if (type === 'collab.leave') { leave(userId); return {}; }
    if (type === 'collab.sync') return sessionSnapshot(roomFor(userId, message.sessionId));
    if (type === 'collab.delta') {
      const room = roomFor(userId, message.sessionId);
      room.project = applyDelta(room.project, message.delta); room.revision++;
      broadcast(room, 'collab.delta', { sessionId: room.id, revision: room.revision, delta: message.delta, authorId: userId });
      return { revision: room.revision };
    }
    if (type === 'collab.project') {
      const room = roomFor(userId, message.sessionId);
      check(Number.isSafeInteger(message.baseRevision) && message.baseRevision === room.revision, 'Project revision changed; synchronize before retrying.');
      const project = validateProject(message.project);
      const cells = [...sessions.values()].reduce((sum, existing) => sum + existing.project.cells.length, 0);
      check(cells - room.project.cells.length + project.cells.length <= maxRoomCells, 'The relay has reached its map capacity.');
      room.project = project; room.revision++;
      broadcast(room, 'collab.project', { sessionId: room.id, revision: room.revision, project: room.project, authorId: userId }); return { revision: room.revision };
    }
    if (type === 'collab.cursor') {
      const room = roomFor(userId, message.sessionId);
      check(Number.isInteger(message.x) && Number.isInteger(message.y) && message.x >= 0 && message.y >= 0 && message.x < room.project.gridSize && message.y < room.project.gridSize, 'Cursor is outside the map.');
      if (now() - (socket.lastCursorAt ?? -Infinity) >= 40) {
        socket.lastCursorAt = now();
        broadcast(room, 'collab.cursor', { sessionId: room.id, userId, displayName: db.users[userId].displayName, x: message.x, y: message.y }, userId);
      }
      return {};
    }
    if (type === 'export.offer') {
      const other = message.userId;
      check(validId(other) && areFriends(userId, other), 'Exports require an accepted friend.', 403);
      check(isOnline(other), 'The friend is offline.');
      check(['map', 'civilization'].includes(message.kind), 'Unknown export kind.');
      check(Number.isSafeInteger(message.size) && message.size > 0 && message.size <= (message.kind === 'map' ? 32 * MiB : maxExportBytes), 'Export exceeds the size limit.');
      check(typeof message.sha256 === 'string' && /^[a-f0-9]{64}$/i.test(message.sha256), 'Invalid SHA-256 digest.');
      check([...offers.values()].filter(o => o.senderId === userId || o.recipientId === userId).length < 8, 'Too many pending exports.');
      check([...offers.values()].reduce((sum, o) => sum + o.size, 0) + message.size <= 512 * MiB, 'Relay temporary storage is full.');
      const id = randomUUID();
      const offer = { id, senderId: userId, recipientId: other, kind: message.kind, name: name(message.name, 128),
        size: message.size, sha256: message.sha256.toLowerCase(), expiresAt: now() + exportTtlMs,
        ready: false, uploading: false, streams: new Set(), file: path.join(exportDir, `${id}.bin`), partial: path.join(exportDir, `${id}.part`) };
      offers.set(id, offer); return { exportId: id };
    }
    if (type === 'export.decide') {
      const offer = offers.get(message.exportId);
      check(offer && offer.recipientId === userId && offer.ready && areFriends(userId, offer.senderId), 'Export is unavailable.', 403);
      check(['installed', 'denied', 'failed'].includes(message.status), 'Invalid export decision.');
      check(message.message == null || typeof message.message === 'string' && message.message.length <= 512, 'Decision message is too long.');
      await removeOffer(offer, message.status, message.message ?? ''); return {};
    }
    if (type === 'export.cancel') {
      const offer = offers.get(message.exportId);
      check(offer && offer.senderId === userId, 'Only the sender can cancel this export.', 403);
      await removeOffer(offer, 'failed', 'The sender cancelled this export.'); return {};
    }
    throw new PublicError('Unknown command.');
  }

  function httpJson(response, status, value) {
    if (response.destroyed) return;
    response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    response.end(JSON.stringify(value));
  }
  async function register(request, response) {
    const ip = request.socket.remoteAddress ?? 'unknown';
    const bucket = registrationRates.get(ip) ?? { start: now(), count: 0 };
    if (now() - bucket.start >= 60_000) { bucket.start = now(); bucket.count = 0; }
    check(++bucket.count <= 60, 'Registration rate limit reached.', 429); registrationRates.set(ip, bucket);
    const chunks = []; let length = 0;
    for await (const chunk of request) { length += chunk.length; check(length <= 4096, 'Registration body is too large.', 413); chunks.push(chunk); }
    let input;
    try { input = jsonObject(JSON.parse(Buffer.concat(chunks).toString('utf8'))); } catch { throw new PublicError('Invalid registration JSON.'); }
    const displayName = name(input.displayName);
    const result = await enqueue(async () => {
      check(Object.keys(db.users).length < 100_000, 'Relay identity limit reached.', 503);
      const userId = randomUUID();
      let friendCode;
      do { friendCode = randomBytes(6).toString('hex').toUpperCase(); } while (Object.values(db.users).some(user => user.friendCode === friendCode));
      const token = signer.device(userId, friendCode);
      await updateDatabase(next => { next.users[userId] = { displayName, friendCode, tokenHash: hashToken(token),
        declared: false, declaredReceipts: [], pendingReceiptDelivery: [] }; });
      return { userId, token, friendCode, displayName };
    });
    httpJson(response, 201, result);
  }
  async function transfer(request, response, exportId) {
    const userId = authorizeHttp(request);
    const offer = offers.get(exportId);
    check(offer && offer.expiresAt > now() && areFriends(offer.senderId, offer.recipientId), 'Export is unavailable.', 404);
    if (request.method === 'PUT') {
      check(userId === offer.senderId, 'Only the sender can upload this export.', 403);
      check(!offer.ready && !offer.uploading, 'Export was already uploaded.', 409);
      if (request.headers['content-length'] != null) check(Number(request.headers['content-length']) === offer.size, 'Upload size differs from the offer.');
      offer.uploading = true;
      const hash = createHash('sha256'); let size = 0;
      const verify = new Transform({ transform(chunk, encoding, callback) {
        size += chunk.length;
        if (size > offer.size) callback(new PublicError('Upload exceeds the offered size.', 413));
        else { hash.update(chunk); callback(null, chunk); }
      } });
      const destination = createWriteStream(offer.partial, { flags: 'wx', mode: 0o600 });
      offer.streams.add(verify); offer.streams.add(destination);
      const timeout = setTimeout(() => verify.destroy(new PublicError('Upload timed out.', 408)), 120_000); timeout.unref();
      try {
        // pipeline applies file/network backpressure without buffering the package.
        await pipeline(request, verify, destination);
        check(size === offer.size && hash.digest('hex') === offer.sha256, 'Upload size or SHA-256 verification failed.');
        await enqueue(async () => {
          check(offers.get(exportId) === offer && offer.expiresAt > now() && areFriends(offer.senderId, offer.recipientId), 'Export was revoked or expired.', 410);
          await rename(offer.partial, offer.file); offer.ready = true;
          send(connections.get(offer.recipientId), 'export.offer', offerEvent(offer));
        });
        httpJson(response, 200, { exportId, size });
      } catch (error) {
        await enqueue(() => removeOffer(offer, 'failed', 'Upload failed integrity, timeout, or authorization checks.'));
        throw error;
      } finally { clearTimeout(timeout); offer.streams.delete(verify); offer.streams.delete(destination); }
      return;
    }
    check(request.method === 'GET', 'Method not allowed.', 405);
    check(userId === offer.recipientId, 'Only the recipient can download this export.', 403);
    check(offer.ready, 'Export upload is not complete.', 409);
    check(offer.streams.size < 2, 'A download is already in progress.', 409);
    const source = createReadStream(offer.file);
    offer.streams.add(source);
    const timeout = setTimeout(() => source.destroy(new PublicError('Download timed out.', 408)), 120_000); timeout.unref();
    response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': offer.size,
      'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'X-Content-Sha256': offer.sha256 });
    try { await pipeline(source, response); }
    finally { clearTimeout(timeout); offer.streams.delete(source); }
  }
  const server = http.createServer((request, response) => {
    (async () => {
      check(!stopping, 'Relay is shutting down.', 503);
      const url = new URL(request.url, 'http://relay.local');
      if (request.method === 'GET' && url.pathname === '/health') return httpJson(response, 200, { status: 'ok', protocol: 1 });
      if (request.method === 'POST' && url.pathname === '/v1/register') return register(request, response);
      const match = /^\/v1\/exports\/([0-9a-f-]{36})$/i.exec(url.pathname);
      if (match && validId(match[1])) return transfer(request, response, match[1]);
      throw new PublicError('Not found.', 404);
    })().catch(error => {
      if (response.headersSent) response.destroy();
      else httpJson(response, error instanceof PublicError ? error.status : 500, { error: error instanceof PublicError ? error.message : 'Relay request failed.' });
    });
  });
  server.requestTimeout = 130_000; server.headersTimeout = 15_000; server.keepAliveTimeout = 5_000;
  const wss = new WebSocketServer({ noServer: true, maxPayload: maxWsBytes, perMessageDeflate: false });
  server.on('upgrade', (request, socket, head) => {
    if (stopping || request.url !== '/ws' || wss.clients.size >= 1024) { socket.write('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n'); socket.destroy(); return; }
    wss.handleUpgrade(request, socket, head, ws => wss.emit('connection', ws, request));
  });
  wss.on('connection', socket => {
    socket.alive = true; socket.pendingCommands = 0; socket.pendingBytes = 0; socket.rateStart = now(); socket.rateCount = 0;
    socket.authTimer = setTimeout(() => socket.close(4003, 'Authentication timed out.'), 15_000); socket.authTimer.unref();
    socket.on('pong', () => { socket.alive = true; });
    socket.on('error', () => {});
    socket.on('message', (raw, binary) => {
      if (binary) { socket.close(1003, 'JSON messages are required.'); return; }
      if (socket.pendingBytes + raw.length > 32 * MiB || queuedMessageBytes + raw.length > 64 * MiB) {
        socket.close(4008, 'Relay message queue is full; reconnect and synchronize.'); return;
      }
      let message;
      try {
        message = jsonObject(JSON.parse(raw.toString('utf8')));
        check(typeof message.type === 'string' && message.type.length <= 64 && typeof message.requestId === 'string' && message.requestId.length > 0 && message.requestId.length <= 128, 'Command type and requestId are required.');
      } catch { socket.close(1007, 'Invalid JSON command.'); return; }
      if (now() - socket.rateStart >= 1000) { socket.rateStart = now(); socket.rateCount = 0; }
      if (++socket.rateCount > 120 || ++socket.pendingCommands > 128) { socket.close(4008, 'Command rate limit reached.'); return; }
      socket.pendingBytes += raw.length; queuedMessageBytes += raw.length;
      enqueue(async () => {
        if (socket.readyState !== WebSocket.OPEN) return;
        try { const data = await command(socket, message); reply(socket, message.requestId, data); }
        catch (error) {
          // Validation errors contain map field names only, never credentials or paths.
          const safe = error instanceof PublicError || error.constructor === Error && message.type.startsWith('collab.');
          reply(socket, message.requestId, undefined, safe ? error.message : 'Relay command failed.');
        }
      }).finally(() => { socket.pendingCommands--; socket.pendingBytes -= raw.length; queuedMessageBytes -= raw.length; }).catch(() => { socket.terminate(); });
    });
    socket.on('close', () => {
      clearTimeout(socket.authTimer);
      enqueue(async () => {
        if (socket.userId && connections.get(socket.userId) === socket) {
          connections.delete(socket.userId); leave(socket.userId, 'The host disconnected.');
          await revokeUserExports(socket.userId, 'A participant disconnected; export cancelled.');
          notifyFriends(socket.userId);
        }
      }).catch(() => {});
    });
  });
  async function sweep() {
    await enqueue(async () => {
      for (const offer of [...offers.values()]) if (offer.expiresAt <= now()) await removeOffer(offer, 'failed', 'Export expired after 10 minutes.');
      for (const [ip, rate] of registrationRates) if (now() - rate.start > 60_000) registrationRates.delete(ip);
    });
  }
  const expiryTimer = setInterval(() => sweep().catch(() => {}), Math.min(30_000, exportTtlMs)); expiryTimer.unref();
  const heartbeatTimer = setInterval(() => {
    for (const socket of wss.clients) {
      if (!socket.alive) { socket.terminate(); continue; }
      socket.alive = false; socket.ping();
    }
  }, heartbeatMs); heartbeatTimer.unref();
  return {
    server,
    sweep,
    async listen(port = Number(process.env.PORT ?? 10000), host = '0.0.0.0') {
      await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, host, resolve); });
      return server.address();
    },
    async close() {
      stopping = true; clearInterval(expiryTimer); clearInterval(heartbeatTimer);
      for (const socket of wss.clients) socket.terminate();
      await enqueue(async () => { for (const offer of [...offers.values()]) await removeOffer(offer); });
      await new Promise(resolve => wss.close(resolve));
      await new Promise(resolve => { if (server.listening) server.close(resolve); else resolve(); });
    },
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const relay = await createRelay();
  const address = await relay.listen();
  console.log(`EDMW relay listening on port ${address.port}`);
  let closing = false;
  const stop = async () => { if (closing) return; closing = true; await relay.close(); process.exit(0); };
  process.on('SIGTERM', stop); process.on('SIGINT', stop);
}
