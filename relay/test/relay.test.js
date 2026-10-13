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
async function client(url, identity, displayName = identity.displayName) {
  const socket = new WebSocket(url);
  const responses = new Map(), events = [], waiters = [];
  socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
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
  api.friends = await api.command('auth', { protocol: 1, userId: identity.userId, token: identity.token, displayName });
  return api;
}
async function fixture(t, options = {}) {
  const dataDir = await mkdtemp(path.join(os.tmpdir(), 'edmw-relay-test-'));
  const relay = await createRelay({ dataDir, ...options });
  const address = await relay.listen(0, '127.0.0.1');
  const base = `http://127.0.0.1:${address.port}`, ws = base.replace('http:', 'ws:') + '/ws';
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
  async function connect(identity, displayName) { const c = await client(ws, identity, displayName); clients.push(c); return c; }
  async function user(displayName) { return connect(await register(displayName)); }
  function headers(identity) { return { Authorization: `Bearer ${identity.token}`, 'X-User-Id': identity.userId }; }
  async function friends(a, b) {
    await a.command('friends.request', { friendCode: b.identity.friendCode });
    return b.command('friends.accept', { userId: a.identity.userId });
  }
  async function offer(a, b, bytes, fields = {}) {
    return a.command('export.offer', { userId: b.identity.userId, kind: 'map', name: 'Map package', size: bytes.length, sha256: sha256(bytes), ...fields });
  }
  return { relay, dataDir, base, ws, user, register, connect, headers, friends, offer };
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
