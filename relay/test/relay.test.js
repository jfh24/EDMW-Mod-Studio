import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { watch } from 'node:fs';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { once } from 'node:events';
import { WebSocket } from 'ws';
import { createRelay } from '../src/server.js';
import { applyDelta, validateProject } from '../src/project.js';

const sha256 = value => createHash('sha256').update(value).digest('hex');
const MiB = 1024 * 1024;
function project(size = 8) {
  return {
    name: 'Shared map', author: 'Host', gridSize: size, variance: 20, cells: Array(size * size).fill(3),
    componentRoles: Buffer.alloc(size * size).toString('base64'), rampMask: Buffer.alloc(size * size).toString('base64'),
    cliffMask: Buffer.alloc(size * size).toString('base64'), elevationMask: Array(size * size).fill(127),
    manualResources: [], manualPlayerStarts: [],
  };
}
function delta(indices = [], cells = [], changes = {}) {
  return {
    gridSize: 8, indices, cells, componentRoles: Buffer.alloc(indices.length).toString('base64'),
    rampMask: Buffer.alloc(indices.length).toString('base64'), cliffMask: Buffer.alloc(indices.length).toString('base64'),
    elevationMask: Array(indices.length).fill(127), ...changes,
  };
}
async function client(url, identity, displayName = identity.displayName, friendReceipts = identity.friendReceipts ?? []) {
  const socket = new WebSocket(url);
  const responses = new Map(), events = [], waiters = [];
  socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'friends.changed' || message.type === 'response' && message.ok && Array.isArray(message.data?.friendReceipts))
      identity.friendReceipts = [...message.data.friendReceipts];
    if (message.type === 'response') {
      const resolve = responses.get(message.requestId); responses.delete(message.requestId); resolve?.(message);
    } else {
      events.push(message);
      for (const waiter of [...waiters]) if (waiter.type === message.type && waiter.predicate(message.data)) {
        waiters.splice(waiters.indexOf(waiter), 1); clearTimeout(waiter.timer); waiter.resolve(message.data);
      }
    }
  });
  await once(socket, 'open');
  const api = {
    identity, socket, events,
    async command(type, fields = {}, expectedOk = true) {
      const requestId = randomUUID();
      const response = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { responses.delete(requestId); reject(new Error(`Timed out: ${type}`)); }, 3000);
        responses.set(requestId, value => { clearTimeout(timeout); resolve(value); });
      });
      socket.send(JSON.stringify({ type, requestId, ...fields }));
      const result = await response;
      assert.equal(result.ok, expectedOk, `${type}: ${result.error ?? 'unexpected success'}`);
      return expectedOk ? result.data : result.error;
    },
    event(type, predicate = () => true) {
      const seen = events.find(e => e.type === type && predicate(e.data));
      if (seen) return Promise.resolve(seen.data);
      return new Promise((resolve, reject) => {
        const waiter = { type, predicate, resolve, timer: setTimeout(() => {
          waiters.splice(waiters.indexOf(waiter), 1); reject(new Error(`Timed out waiting for ${type}`));
        }, 3000) }; waiters.push(waiter);
      });
    },
    async close() {
      if (socket.readyState === WebSocket.CLOSED) return;
      const closed = once(socket, 'close'); socket.close(); await closed;
    },
  };
  api.friends = await api.command('auth', { protocol: 1, userId: identity.userId, token: identity.token, displayName, friendReceipts });
  return api;
}
async function fixture(t, options = {}) {
  const dataDir = await mkdtemp(path.join(os.tmpdir(), 'edmw-relay-test-'));
  let relayOptions = options;
  let relay = await createRelay({ dataDir, ...relayOptions });
  let address = await relay.listen(0, '127.0.0.1');
  let base = `http://127.0.0.1:${address.port}`, ws = base.replace('http:', 'ws:') + '/ws';
  const clients = [];
  t.after(async () => {
    for (const c of clients) c.socket.terminate();
    await relay.close();
    assert.ok(path.resolve(dataDir).startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(dataDir).startsWith('edmw-relay-test-'));
    await rm(dataDir, { recursive: true, force: true });
  });
  async function register(displayName) {
    const response = await fetch(base + '/v1/register', { method: 'POST', body: JSON.stringify({ displayName }) });
    assert.equal(response.status, 201); return response.json();
  }
  async function connect(identity, displayName, receipts) { const c = await client(ws, identity, displayName, receipts); clients.push(c); return c; }
  async function user(displayName) { return connect(await register(displayName)); }
  function headers(identity) { return { Authorization: `Bearer ${identity.token}`, 'X-User-Id': identity.userId }; }
  async function friends(a, b) {
    await a.command('friends.request', { friendCode: b.identity.friendCode });
    return b.command('friends.accept', { userId: a.identity.userId });
  }
  async function offer(a, b, bytes, fields = {}) {
    return a.command('export.offer', { userId: b.identity.userId, kind: 'map', name: 'Map package', size: bytes.length, sha256: sha256(bytes), ...fields });
  }
  async function restart({ wipe = false, ...replacement } = {}) {
    await relay.close();
    if (wipe) {
      assert.ok(path.resolve(dataDir).startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(dataDir).startsWith('edmw-relay-test-'));
      await rm(dataDir, { recursive: true, force: true });
    }
    relayOptions = { ...relayOptions, ...replacement };
    relay = await createRelay({ dataDir, ...relayOptions });
    address = await relay.listen(0, '127.0.0.1');
    base = `http://127.0.0.1:${address.port}`; ws = base.replace('http:', 'ws:') + '/ws';
  }
  async function rejectedAuth(identity, receipts = identity.friendReceipts ?? [], fields = {}) {
    const socket = new WebSocket(ws); await once(socket, 'open');
    const response = once(socket, 'message');
    socket.send(JSON.stringify({ type: 'auth', requestId: randomUUID(), protocol: 1, userId: identity.userId,
      token: identity.token, displayName: identity.displayName, friendReceipts: receipts, ...fields }));
    const [raw] = await response; socket.terminate();
    const parsed = JSON.parse(raw.toString()); assert.equal(parsed.ok, false); return parsed.error;
  }
  return { get relay() { return relay; }, dataDir, get base() { return base; }, get ws() { return ws; }, user, register, connect, headers, friends, offer, restart, rejectedAuth };
}

test('identities and explicitly accepted friends persist; token hashes, presence and renamed profiles are correct', async t => {
  const f = await fixture(t);
  const a = await f.user('Alice'), b = await f.user('Bob');
  assert.equal((await a.command('friends.list')).friends.length, 0);
  const requested = await a.command('friends.request', { friendCode: b.identity.friendCode.toLowerCase() });
  assert.equal(requested.outgoing[0].userId, b.identity.userId);
  assert.equal(requested.friends.length, 0);
  await a.command('friends.accept', { userId: b.identity.userId }, false);
  await b.command('friends.accept', { userId: a.identity.userId });
  assert.equal((await a.command('friends.list')).friends[0].online, true);
  await b.close();
  const offline = await a.event('friends.changed', d => d.friends.some(p => p.userId === b.identity.userId && !p.online));
  assert.equal(offline.friends[0].displayName, 'Bob');
  const bob = await f.connect(b.identity, 'Bobby');
  const renamed = await a.event('friends.changed', d => d.friends.some(p => p.displayName === 'Bobby' && p.online));
  assert.equal(renamed.friends[0].friendCode, b.identity.friendCode);
  const stored = JSON.parse(await readFile(path.join(f.dataDir, 'identities.json'), 'utf8'));
  assert.equal(stored.users[a.identity.userId].tokenHash, sha256(a.identity.token));
  assert.ok(!JSON.stringify(stored).includes(a.identity.token));
  await a.close(); await bob.close(); await f.relay.close();
  const restarted = await createRelay({ dataDir: f.dataDir });
  t.after(() => restarted.close());
  const addr = await restarted.listen(0, '127.0.0.1');
  const resumed = await client(`ws://127.0.0.1:${addr.port}/ws`, a.identity);
  t.after(() => resumed.close());
  assert.equal(resumed.friends.friends[0].displayName, 'Bobby');
  assert.equal(resumed.friends.friends[0].online, false);
});

test('invitation and friendship ACL gates sessions; ordered concurrent edits merge and stale replacement is rejected', async t => {
  const f = await fixture(t);
  const a = await f.user('Host'), b = await f.user('Friend'), outsider = await f.user('Outsider');
  const room = await a.command('collab.create', { project: project() });
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId }, false);
  await f.friends(a, b);
  await b.command('collab.join', { sessionId: room.sessionId }, false);
  await outsider.command('collab.sync', { sessionId: room.sessionId }, false);
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId });
  assert.equal((await b.event('collab.invite')).from.userId, a.identity.userId);
  const joined = await b.command('collab.join', { sessionId: room.sessionId });
  assert.equal(joined.members.length, 2);
  const results = await Promise.all([
    a.command('collab.delta', { sessionId: room.sessionId, delta: delta([0], [5], {
      manualResources: [{ kind: 4, x: 0, y: 0 }], manualResourceChanges: [{ x: 0, y: 0, resource: { kind: 4, x: 0, y: 0 } }],
      manualPlayerStarts: [{ playerSlot: 1, x: 0, y: 0 }], manualPlayerStartChanges: [{ playerSlot: 1, playerStart: { playerSlot: 1, x: 0, y: 0 } }],
    }) }),
    b.command('collab.delta', { sessionId: room.sessionId, delta: delta([1], [7], {
      manualResources: [{ kind: 5, x: 1, y: 0 }], manualResourceChanges: [{ x: 1, y: 0, resource: { kind: 5, x: 1, y: 0 } }],
      manualPlayerStarts: [{ playerSlot: 2, x: 1, y: 0 }], manualPlayerStartChanges: [{ playerSlot: 2, playerStart: { playerSlot: 2, x: 1, y: 0 } }],
    }) }),
  ]);
  assert.deepEqual(results.map(r => r.revision).sort(), [1, 2]);
  const synchronized = await b.command('collab.sync', { sessionId: room.sessionId });
  assert.deepEqual(synchronized.project.cells.slice(0, 2), [5, 7]);
  assert.equal(synchronized.project.manualResources.length, 2); assert.equal(synchronized.project.manualPlayerStarts.length, 2);
  await a.command('collab.project', { sessionId: room.sessionId, project: project(), baseRevision: 0 }, false);
  assert.equal((await a.command('collab.sync', { sessionId: room.sessionId })).revision, 2);
  await a.command('collab.project', { sessionId: room.sessionId, project: project(16), baseRevision: 2 });
  const replacement = await b.event('collab.project', d => d.revision === 3);
  assert.equal(replacement.project.gridSize, 16);
  assert.deepEqual(a.events.filter(e => e.type === 'collab.delta').map(e => e.data.revision), [1, 2]);
  assert.deepEqual(b.events.filter(e => e.type === 'collab.delta').map(e => e.data.revision), [1, 2]);
  await b.command('collab.cursor', { sessionId: room.sessionId, x: 2, y: 3 });
  assert.equal((await a.event('collab.cursor')).userId, b.identity.userId);
  assert.equal(b.events.filter(e => e.type === 'collab.cursor').length, 0);
  await a.close();
  assert.match((await b.event('collab.ended')).message, /host disconnected/i);
  await b.command('collab.sync', { sessionId: room.sessionId }, false);
});

test('terrain/mask semantics and malformed deltas validate atomically; maximum sized maps round trip', async t => {
  const f = await fixture(t), a = await f.user('Host');
  const initial = project();
  initial.manualResources = [{ kind: 4, x: 0, y: 0 }]; initial.manualPlayerStarts = [{ playerSlot: 1, x: 0, y: 0 }];
  const room = await a.command('collab.create', { project: initial });
  await a.command('collab.delta', { sessionId: room.sessionId, delta: delta([0], [0]) });
  let synced = await a.command('collab.sync', { sessionId: room.sessionId });
  assert.equal(synced.project.manualResources.length, 0); assert.equal(synced.project.manualPlayerStarts.length, 0);
  for (const malformed of [delta([64], [3]), delta([0], [99]), delta([0], [3], { rampMask: 'Ag==' }),
    delta([0], [3], { componentRoles: [0] }), delta([0], [3], { elevationMask: [126] }),
    delta([0], [3], { elevationMask: [-101] }), delta([0], [3], { cliffMask: 'AA' }), delta([0], [3], { cliffMask: null }),
    delta([], [], { manualResourceChanges: [{ x: 0, y: 0, resource: null }, { x: 0, y: 0, resource: null }] }),
    delta([], [], { manualPlayerStartChanges: [{ playerSlot: 9, playerStart: null }] }),
    delta([], [], { manualResources: [{ kind: 2, x: 1, y: 1 }] }),
    delta([], [], { manualResources: [{ kind: 3, x: 1, y: 1 }] }),
    delta([], [], { generationSeedChanged: true, generationSeed: 2147483648 }),
  ]) await a.command('collab.delta', { sessionId: room.sessionId, delta: malformed }, false);
  synced = await a.command('collab.sync', { sessionId: room.sessionId });
  assert.equal(synced.revision, 1); assert.equal(synced.project.cells[0], 0);
  await a.command('collab.delta', { sessionId: room.sessionId, delta: delta([0], [6], { elevationMask: [126] }) });
  assert.equal((await a.command('collab.sync', { sessionId: room.sessionId })).project.elevationMask[0], 126);
  await a.command('collab.project', { sessionId: room.sessionId, baseRevision: 2, project: project(512) });
  const full = await a.command('collab.sync', { sessionId: room.sessionId });
  assert.equal(full.project.cells.length, 262144); assert.equal(full.project.elevationMask[262143], 127);
  await a.command('collab.project', { sessionId: room.sessionId, baseRevision: 3, project: { ...project(), elevationMask: '' } }, false);
});

test('friend removal revokes membership and unused invitations; removing outgoing requests needs no acceptance', async t => {
  const f = await fixture(t), a = await f.user('Host'), b = await f.user('Member'), c = await f.user('Invitee');
  await f.friends(a, b); await f.friends(a, c);
  const room = await a.command('collab.create', { project: project() });
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId });
  await b.command('collab.join', { sessionId: room.sessionId });
  await a.command('collab.invite', { sessionId: room.sessionId, userId: c.identity.userId });
  await b.command('friends.remove', { userId: a.identity.userId });
  await b.event('collab.ended');
  await b.command('collab.delta', { sessionId: room.sessionId, delta: delta([0], [4]) }, false);
  assert.equal((await a.command('collab.sync', { sessionId: room.sessionId })).members.length, 1);
  await c.command('friends.remove', { userId: a.identity.userId });
  await c.command('collab.join', { sessionId: room.sessionId }, false);
  await a.command('friends.request', { friendCode: b.identity.friendCode });
  await a.command('friends.remove', { userId: b.identity.userId });
  assert.equal((await b.command('friends.list')).incoming.length, 0);
});

test('exports require accepted online friends, verify bytes/hash, restrict download and wait for recipient decisions', async t => {
  const f = await fixture(t), a = await f.user('Sender'), b = await f.user('Recipient'), outsider = await f.user('Outsider');
  const bytes = Buffer.from('reviewable map archive bytes');
  await a.command('export.offer', { userId: b.identity.userId, kind: 'map', name: 'Map', size: bytes.length, sha256: sha256(bytes) }, false);
  await f.friends(a, b);
  const { exportId } = await f.offer(a, b, bytes);
  const url = f.base + '/v1/exports/' + exportId;
  assert.equal((await fetch(url, { headers: f.headers(b.identity) })).status, 409);
  assert.equal(b.events.filter(e => e.type === 'export.offer').length, 0);
  assert.equal((await fetch(url, { method: 'PUT', headers: f.headers(outsider.identity), body: bytes })).status, 403);
  await b.command('export.decide', { exportId, status: 'installed' }, false);
  assert.equal((await fetch(url, { method: 'PUT', headers: f.headers(a.identity), body: bytes })).status, 200);
  const ready = await b.event('export.offer'); assert.equal(ready.sha256, sha256(bytes)); assert.equal(ready.from.userId, a.identity.userId);
  assert.equal(a.events.filter(e => e.type === 'export.result').length, 0);
  assert.equal((await fetch(url, { headers: f.headers(a.identity) })).status, 403);
  assert.equal((await fetch(url, { headers: f.headers(outsider.identity) })).status, 403);
  const download = await fetch(url, { headers: f.headers(b.identity) }); assert.equal(download.status, 200);
  assert.equal(download.headers.get('x-content-sha256'), sha256(bytes));
  assert.deepEqual(Buffer.from(await download.arrayBuffer()), bytes);
  await outsider.command('export.decide', { exportId, status: 'installed' }, false);
  await b.command('export.decide', { exportId, status: 'automatic' }, false);
  await b.command('export.decide', { exportId, status: 'denied', message: 'User declined the review dialog.' });
  assert.equal((await a.event('export.result', d => d.exportId === exportId)).status, 'denied');
  assert.equal((await fetch(url, { headers: f.headers(b.identity) })).status, 404);
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
  const civilization = await f.offer(a, b, bytes, { kind: 'civilization', name: 'Civilization package' });
  await fetch(f.base + '/v1/exports/' + civilization.exportId, { method: 'PUT', headers: f.headers(a.identity), body: bytes });
  await b.command('export.decide', { exportId: civilization.exportId, status: 'installed', message: 'Installed after approval.' });
  assert.equal((await a.event('export.result', d => d.exportId === civilization.exportId)).status, 'installed');
  await b.close();
  await a.event('friends.changed', d => d.friends.some(p => !p.online));
  await a.command('export.offer', { userId: b.identity.userId, kind: 'map', name: 'Map', size: bytes.length, sha256: sha256(bytes) }, false);
});

test('failed integrity and expired exports never become installable and delete temporary bytes', async t => {
  let clock = 1_000;
  const f = await fixture(t, { now: () => clock }), a = await f.user('Sender'), b = await f.user('Recipient');
  await f.friends(a, b);
  const bytes = Buffer.from('payload');
  const corrupt = await f.offer(a, b, bytes, { sha256: sha256('incorrect') });
  const failure = await fetch(f.base + '/v1/exports/' + corrupt.exportId, { method: 'PUT', headers: f.headers(a.identity), body: bytes });
  assert.equal(failure.status, 400);
  assert.equal((await a.event('export.result', d => d.exportId === corrupt.exportId)).status, 'failed');
  assert.equal(b.events.filter(e => e.type === 'export.offer').length, 0);
  const expiring = await f.offer(a, b, bytes);
  await fetch(f.base + '/v1/exports/' + expiring.exportId, { method: 'PUT', headers: f.headers(a.identity), body: bytes });
  clock += 600_001; await f.relay.sweep();
  assert.match((await a.event('export.result', d => d.exportId === expiring.exportId)).message, /expired/i);
  assert.equal((await fetch(f.base + '/v1/exports/' + expiring.exportId, { headers: f.headers(b.identity) })).status, 404);
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
  for (const fields of [{ size: 32 * MiB + 1 }, { size: 128 * MiB + 1, kind: 'civilization' }, { sha256: 'bad' }, { name: '../\nunsafe' }, { kind: 'arbitrary' }])
    await a.command('export.offer', { userId: b.identity.userId, kind: 'map', name: 'Map', size: bytes.length, sha256: sha256(bytes), ...fields }, false);
});

test('friend revocation closes an active streamed download and revokes pending offers', async t => {
  const f = await fixture(t), a = await f.user('Sender'), b = await f.user('Recipient');
  await f.friends(a, b);
  const bytes = Buffer.alloc(8 * MiB, 7), ready = await f.offer(a, b, bytes);
  await fetch(f.base + '/v1/exports/' + ready.exportId, { method: 'PUT', headers: f.headers(a.identity), body: bytes });
  const pending = await f.offer(a, b, Buffer.from('pending'));
  let request, response;
  const download = new Promise((resolve, reject) => {
    request = http.get(f.base + '/v1/exports/' + ready.exportId, { headers: f.headers(b.identity) }, incoming => {
      response = incoming; incoming.pause(); incoming.on('error', () => {}); resolve();
    }); request.on('error', reject);
  });
  await download;
  assert.equal(response.statusCode, 200);
  await a.command('friends.remove', { userId: b.identity.userId });
  assert.equal((await a.event('export.result', d => d.exportId === ready.exportId)).status, 'failed');
  assert.equal((await a.event('export.result', d => d.exportId === pending.exportId)).status, 'failed');
  response.resume();
  await new Promise(resolve => { if (response.destroyed) resolve(); else { response.once('close', resolve); request.setTimeout(2000, () => { request.destroy(); resolve(); }); } });
  assert.equal(response.complete, false);
  await b.command('export.decide', { exportId: ready.exportId, status: 'installed' }, false);
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
});

test('credentials, protocol, registration JSON and websocket envelopes are validated', async t => {
  const f = await fixture(t);
  const response = await fetch(f.base + '/v1/register', { method: 'POST', body: '{broken' }); assert.equal(response.status, 400);
  const empty = await fetch(f.base + '/v1/register', { method: 'POST', body: JSON.stringify({ displayName: ' ' }) }); assert.equal(empty.status, 400);
  const oversized = await fetch(f.base + '/v1/register', { method: 'POST', body: JSON.stringify({ displayName: 'a'.repeat(4096) }) }); assert.equal(oversized.status, 413);
  const health = await fetch(f.base + '/health'); assert.equal((await health.json()).protocol, 1);
  const identity = await f.register('User');
  const socket = new WebSocket(f.ws); await once(socket, 'open');
  function rawCommand(fields) {
    const response = once(socket, 'message'); socket.send(JSON.stringify({ requestId: randomUUID(), ...fields }));
    return response.then(([raw]) => JSON.parse(raw.toString()));
  }
  assert.equal((await rawCommand({ type: 'friends.list' })).ok, false);
  assert.equal((await rawCommand({ type: 'auth', protocol: 2, ...identity })).ok, false);
  assert.equal((await rawCommand({ type: 'auth', protocol: 1, ...identity, token: 'wrong' })).ok, false);
  assert.equal((await rawCommand({ type: 'auth', protocol: 1, ...identity, token: randomUUID().replaceAll('-', '') + 'aaaaaaaaaaa' })).ok, false);
  const closed = once(socket, 'close'); socket.send('{invalid');
  const [code] = await closed; assert.equal(code, 1007);
  assert.equal((await fetch(f.base + '/v1/exports/' + randomUUID())).status, 401);
});

test('only sender may withdraw; withdrawal aborts a streamed upload and both clients see cancellation', async t => {
  const f = await fixture(t), a = await f.user('Sender'), b = await f.user('Recipient');
  await f.friends(a, b);
  const bytes = Buffer.alloc(8 * MiB, 3), offered = await f.offer(a, b, bytes);
  await b.command('export.cancel', { exportId: offered.exportId }, false);
  const created = new Promise(resolve => {
    const watcher = watch(path.join(f.dataDir, 'exports'), (type, file) => {
      if (file === offered.exportId + '.part') { watcher.close(); resolve(); }
    });
    t.after(() => watcher.close());
  });
  let upload;
  const failed = new Promise(resolve => {
    upload = http.request(f.base + '/v1/exports/' + offered.exportId, {
      method: 'PUT', headers: { ...f.headers(a.identity), 'Content-Length': bytes.length },
    }, response => { response.resume(); response.on('close', resolve); });
    upload.on('error', resolve);
  });
  upload.write(bytes.subarray(0, 64 * 1024));
  await created;
  await a.command('export.cancel', { exportId: offered.exportId });
  await failed;
  assert.equal((await a.event('export.result', d => d.exportId === offered.exportId)).status, 'failed');
  assert.match((await b.event('export.cancelled', d => d.exportId === offered.exportId)).message, /sender cancelled/i);
  assert.equal((await fetch(f.base + '/v1/exports/' + offered.exportId, { headers: f.headers(b.identity) })).status, 404);
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
});

test('participant disconnect and device reconnection cancel exports and remove session state', async t => {
  const f = await fixture(t), a = await f.user('Sender'), b = await f.user('Recipient');
  await f.friends(a, b);
  const bytes = Buffer.from('pending payload'), pending = await f.offer(a, b, bytes);
  await b.close();
  assert.match((await a.event('export.result', d => d.exportId === pending.exportId)).message, /disconnected/i);
  const again = await f.connect(b.identity);
  const room = await a.command('collab.create', { project: project() });
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId });
  await again.command('collab.join', { sessionId: room.sessionId });
  const next = await f.offer(a, again, bytes);
  await fetch(f.base + '/v1/exports/' + next.exportId, { method: 'PUT', headers: f.headers(a.identity), body: bytes });
  const oldSocketClosed = once(a.socket, 'close');
  const newSender = await f.connect(a.identity);
  assert.equal((await oldSocketClosed)[0], 4001);
  await again.event('export.cancelled', d => d.exportId === next.exportId);
  await again.event('collab.ended', d => d.sessionId === room.sessionId);
  await newSender.command('collab.sync', { sessionId: room.sessionId }, false);
  assert.equal((await newSender.command('friends.list')).friends[0].online, true);
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
});

test('legacy optional cliff masks preserve authorship; automatic elevation clears forced cliffs', () => {
  const base = project(); base.elevationMask[0] = 45;
  const cliffs = Buffer.alloc(64); cliffs[0] = 1; base.cliffMask = cliffs.toString('base64');
  const preserved = applyDelta(validateProject(base), delta([0], [3], { cliffMask: '', elevationMask: [60] }));
  assert.equal(Buffer.from(preserved.cliffMask, 'base64')[0], 1);
  assert.equal(preserved.elevationMask[0], 60);
  const automatic = applyDelta(preserved, delta([0], [3], { cliffMask: 'AQ==', elevationMask: [127] }));
  assert.equal(Buffer.from(automatic.cliffMask, 'base64')[0], 0);
});

const stableSecret = 'test signing secret that survives Render ephemeral restarts';
test('free relay restores signed device identity and offline friend roster after total filesystem loss; sharing requires bilateral proofs', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const a = await f.user('Alice'), b = await f.user('Bob'); await f.friends(a, b);
  await a.command('friends.list');
  const savedA = structuredClone(a.identity), savedB = structuredClone(b.identity);
  assert.ok(savedA.token.startsWith('d1.') && savedA.token.length <= 512);
  assert.equal(savedA.friendReceipts.length, 1); assert.ok(savedA.friendReceipts[0].startsWith('f1.'));
  const proof = JSON.parse(Buffer.from(savedA.friendReceipts[0].split('.')[1], 'base64url'));
  assert.equal(proof.users.length, 2); assert.ok(!JSON.stringify(proof).includes(savedA.token));
  assert.deepEqual(savedA.friendReceipts, savedB.friendReceipts);
  await f.restart({ wipe: true });
  const first = await f.connect(savedA);
  assert.equal(first.friends.user.userId, savedA.userId); assert.equal(first.friends.user.friendCode, savedA.friendCode);
  assert.equal(first.friends.friends[0].displayName, 'Bob'); assert.equal(first.friends.friends[0].online, false);
  const staged = JSON.parse(await readFile(path.join(f.dataDir, 'identities.json'), 'utf8'));
  assert.equal(staged.friendships.length, 0); assert.equal(staged.users[savedB.userId].declared, false);
  assert.equal(staged.users[savedB.userId].tokenHash, undefined);
  const room = await first.command('collab.create', { project: project() });
  await first.command('collab.invite', { sessionId: room.sessionId, userId: savedB.userId }, false);
  await first.command('export.offer', { userId: savedB.userId, kind: 'map', name: 'Map', size: 7, sha256: sha256('payload') }, false);
  const second = await f.connect(savedB);
  assert.equal(second.friends.user.userId, savedB.userId); assert.equal(second.friends.user.friendCode, savedB.friendCode);
  assert.equal(second.friends.friends[0].online, true);
  assert.equal((await first.command('friends.list')).friends[0].online, true);
  await first.command('collab.invite', { sessionId: room.sessionId, userId: savedB.userId });
  assert.equal((await second.command('collab.join', { sessionId: room.sessionId })).members.length, 2);
  const bytes = Buffer.from('payload'), offered = await f.offer(first, second, bytes);
  assert.equal((await fetch(f.base + '/v1/exports/' + offered.exportId, { method: 'PUT', headers: f.headers(savedA), body: bytes })).status, 200);
  assert.equal((await fetch(f.base + '/v1/exports/' + offered.exportId, { headers: f.headers(savedB) })).status, 200);
  await second.command('export.decide', { exportId: offered.exportId, status: 'denied' });
});

test('removed friendship receipts cannot resurrect after a wiped filesystem in either reconnection order', async t => {
  for (const staleFirst of [true, false]) await t.test(staleFirst ? 'stale device first' : 'removing device first', async sub => {
    const f = await fixture(sub, { identitySecret: stableSecret });
    const a = await f.user('Remover'), b = await f.user('Offline stale device'); await f.friends(a, b);
    await a.command('friends.list');
    const staleB = structuredClone(b.identity), oldProofs = [...staleB.friendReceipts];
    await b.close(); await a.command('friends.remove', { userId: b.identity.userId });
    const currentA = structuredClone(a.identity); assert.deepEqual(currentA.friendReceipts, []);
    await f.restart({ wipe: true });
    let remover, stale;
    if (staleFirst) { stale = await f.connect(staleB); assert.equal(stale.friends.friends[0].online, false); remover = await f.connect(currentA); }
    else { remover = await f.connect(currentA); stale = await f.connect(staleB); }
    assert.deepEqual((await remover.command('friends.list')).friends, []);
    assert.deepEqual((await stale.command('friends.list')).friends, []);
    assert.deepEqual(stale.identity.friendReceipts, []);
    const room = await stale.command('collab.create', { project: project() });
    await stale.command('collab.invite', { sessionId: room.sessionId, userId: currentA.userId }, false);
    await stale.command('export.offer', { userId: currentA.userId, kind: 'map', name: 'Map', size: 1, sha256: sha256('a') }, false);
    // Even presenting the revoked proof from both devices in this relay cannot undo removal.
    await stale.close(); await remover.close();
    stale = await f.connect(staleB, undefined, oldProofs);
    remover = await f.connect(currentA, undefined, oldProofs);
    assert.deepEqual((await stale.command('friends.list')).friends, []);
    assert.deepEqual((await remover.command('friends.list')).friends, []);
  });
});

test('receipt forgery, cross-user proofs, overlong declarations and a changed signing key fail authentication', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const a = await f.user('Alice'), b = await f.user('Bob'), outsider = await f.user('Outsider'); await f.friends(a, b);
  await a.command('friends.list');
  const saved = structuredClone(a.identity), receipt = saved.friendReceipts[0]; await a.close();
  const mutated = receipt.slice(0, -1) + (receipt.endsWith('a') ? 'b' : 'a');
  assert.match(await f.rejectedAuth(saved, [mutated]), /receipt/i);
  assert.match(await f.rejectedAuth(outsider.identity, [receipt]), /belong/i);
  assert.match(await f.rejectedAuth(saved, Array(257).fill(receipt)), /256/i);
  assert.match(await f.rejectedAuth(saved, [receipt, receipt]), /duplicate/i);
  const badDevice = saved.token.slice(0, -1) + (saved.token.endsWith('a') ? 'b' : 'a');
  assert.match(await f.rejectedAuth(saved, [], { token: badDevice }), /credentials/i);
  await f.restart({ wipe: true, identitySecret: 'another signing secret that cannot authenticate existing devices' });
  assert.match(await f.rejectedAuth(saved), /credentials/i);
});

test('bilateral restore clears duplicate requests without issuing a second receipt that would lock out reconnect', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const a = await f.user('Alice'), b = await f.user('Bob'); await f.friends(a, b); await a.command('friends.list');
  const savedA = structuredClone(a.identity), savedB = structuredClone(b.identity), proof = savedA.friendReceipts[0];
  await f.restart({ wipe: true });
  const first = await f.connect(savedA);
  await first.command('friends.request', { friendCode: savedB.friendCode });
  const second = await f.connect(savedB);
  assert.deepEqual((await second.command('friends.list')).incoming, []);
  await second.command('friends.accept', { userId: savedA.userId }, false);
  assert.deepEqual(first.identity.friendReceipts, [proof]); assert.deepEqual(second.identity.friendReceipts, [proof]);
  await first.close(); await second.close();
  const reconnectedA = await f.connect(first.identity), reconnectedB = await f.connect(second.identity);
  assert.equal((await reconnectedA.command('friends.list')).friends[0].online, true);
  assert.deepEqual(reconnectedB.friends.friendReceipts, [proof]);
});

test('offline request acceptance delivers a fresh receipt on reconnect; removal clears that pending handoff', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const a = await f.user('Acceptor'), b = await f.user('Requester');
  await b.command('friends.request', { friendCode: a.identity.friendCode }); await b.close();
  await a.event('friends.changed', value => value.incoming.some(peer => peer.userId === b.identity.userId && !peer.online));
  const accepted = await a.command('friends.accept', { userId: b.identity.userId });
  assert.equal(accepted.friendReceipts.length, 1); assert.equal(accepted.friends[0].online, false);
  assert.deepEqual(b.identity.friendReceipts, []);
  let requester = await f.connect(b.identity);
  assert.equal(requester.friends.friendReceipts.length, 1); assert.equal(requester.friends.friends[0].online, true);
  await requester.command('friends.remove', { userId: a.identity.userId });
  a.events.length = 0;
  await requester.command('friends.request', { friendCode: a.identity.friendCode }); await requester.close();
  // Wait until the current socket has been removed before testing deferred delivery.
  await a.command('friends.list');
  await a.event('friends.changed', value => value.incoming.some(peer => peer.userId === b.identity.userId && !peer.online));
  await a.command('friends.accept', { userId: b.identity.userId });
  await a.command('friends.remove', { userId: b.identity.userId });
  requester = await f.connect(b.identity);
  assert.deepEqual(requester.friends.friendReceipts, []); assert.deepEqual(requester.friends.friends, []);
});

test('relay restart aborts active uploads and never recovers offers from temporary files', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const a = await f.user('Sender'), b = await f.user('Recipient'); await f.friends(a, b);
  const bytes = Buffer.alloc(8 * MiB, 3), offered = await f.offer(a, b, bytes);
  let upload;
  const failed = new Promise(resolve => {
    upload = http.request(f.base + '/v1/exports/' + offered.exportId, { method: 'PUT', headers: { ...f.headers(a.identity), 'Content-Length': bytes.length } },
      response => { response.resume(); response.on('close', resolve); }); upload.on('error', resolve);
  });
  const created = new Promise(resolve => {
    const watcher = watch(path.join(f.dataDir, 'exports'), (type, file) => { if (file === offered.exportId + '.part') { watcher.close(); resolve(); } });
    t.after(() => watcher.close());
  });
  upload.write(bytes.subarray(0, 64 * 1024)); await created;
  await f.restart(); await failed;
  assert.deepEqual(await readdir(path.join(f.dataDir, 'exports')), []);
  const resumedA = await f.connect(a.identity), resumedB = await f.connect(b.identity);
  assert.equal((await resumedA.command('friends.list')).friends[0].online, true);
  assert.equal((await fetch(f.base + '/v1/exports/' + offered.exportId, { headers: f.headers(resumedB.identity) })).status, 404);
  await resumedB.command('export.decide', { exportId: offered.exportId, status: 'installed' }, false);
});

test('free Render blueprint has no disk or paid dependency and requires a durable environment signing key', async t => {
  const blueprint = await readFile(new URL('../../render.yaml', import.meta.url), 'utf8');
  assert.match(blueprint, /plan: free/); assert.doesNotMatch(blueprint, /\bdisk:|plan: starter|databases:/);
  assert.match(blueprint, /key: IDENTITY_SECRET\s+generateValue: true/); assert.match(blueprint, /value: \/tmp\/edmw-relay/);
  const f = await fixture(t, { identitySecret: stableSecret }); await f.relay.close();
  await assert.rejects(createRelay({ dataDir: f.dataDir, identitySecret: '', requireIdentitySecret: true }), /IDENTITY_SECRET is required/);
  await assert.rejects(createRelay({ dataDir: f.dataDir, identitySecret: 'short' }), /32-1024/);
});

test('free relay bounds room memory and rejects an oversized replacement without losing the prior snapshot', async t => {
  const f = await fixture(t, { identitySecret: stableSecret });
  const hosts = [];
  for (let i = 0; i < 8; i++) {
    const host = await f.user(`Host ${i}`); hosts.push(host);
    await host.command('collab.create', { project: project(512) });
  }
  const extra = await f.user('Extra host');
  await extra.command('collab.create', { project: project() }, false);
  await hosts[0].command('collab.leave');
  const small = await extra.command('collab.create', { project: project() });
  const filler = await f.user('Filler host');
  await filler.command('collab.create', { project: project(511) });
  await extra.command('collab.project', { sessionId: small.sessionId, baseRevision: 0, project: project(512) }, false);
  const intact = await extra.command('collab.sync', { sessionId: small.sessionId });
  assert.equal(intact.revision, 0); assert.equal(intact.project.gridSize, 8);
  await filler.command('collab.leave');
  await extra.command('collab.project', { sessionId: small.sessionId, baseRevision: 0, project: project(512) });
});
