// Bounded public relay checks using disposable test profiles; no game installation.
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const require = createRequire(new URL('../relay/package.json', import.meta.url));
const { WebSocket } = require('ws');
const workspace = fileURLToPath(new URL('../', import.meta.url));
if (!process.argv[2]) throw new Error('Pass an explicit root HTTPS relay URL to verify.');
const endpoint = new URL(process.argv[2]);
if (endpoint.protocol !== 'https:' || endpoint.username || endpoint.password || endpoint.pathname !== '/' || endpoint.search || endpoint.hash)
  throw new Error('Pass the root HTTPS relay URL without credentials, query, or fragment.');
const reportPath = path.resolve(process.argv[3] ?? path.join(workspace, '.verification', 'live-relay-node.json'));
const reportRelative = path.relative(path.join(workspace, '.verification'), reportPath);
if (reportRelative.startsWith('..') || path.isAbsolute(reportRelative)) throw new Error('Write the report inside .verification.');
const runId = new Date().toISOString().replace(/[-:TZ.]/g, '').slice(0, 14) + '-' + randomUUID().slice(0, 6);
const report = { runId, endpoint: endpoint.origin, startedAt: new Date().toISOString(), passed: false,
  checks: [], cleanup: [], testProfiles: [], gameInstallationPerformed: false, payloadBytesSent: 0, payloadBytesReceived: 0 };
const identities = [], clients = [], pendingExports = new Set();
let a, b, stranger, room;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (condition, message) => { if (!condition) throw new Error(message); };
const equal = (actual, expected, message) => check(JSON.stringify(actual) === JSON.stringify(expected), message);
const payloadBudget = () => check(report.payloadBytesSent + report.payloadBytesReceived < 256 * 1024, 'Application payload budget exceeded.');
function passed(label) { payloadBudget(); report.checks.push(label); console.log(`PASS ${label}`); }
function safeError(error) {
  let text = String(error?.message ?? error).slice(0, 2000);
  for (const identity of identities) for (const secret of [identity.token, ...(identity.friendReceipts ?? [])])
    if (secret) text = text.split(secret).join('[redacted]');
  return text.slice(0, 512);
}
function parseJson(text) { try { return JSON.parse(text); } catch { throw new Error('Relay returned invalid JSON.'); } }
const authHeaders = identity => ({ Authorization: `Bearer ${identity.token}`, 'X-User-Id': identity.userId });
async function request(route, { method = 'GET', identity, body, expect = 200, timeoutMs = 25_000, signal } = {}) {
  const bytes = body == null ? undefined : Buffer.isBuffer(body) ? body : Buffer.from(JSON.stringify(body));
  if (bytes) report.payloadBytesSent += bytes.length;
  payloadBudget();
  const response = await fetch(new URL(route, endpoint), {
    method, headers: { ...(identity ? authHeaders(identity) : {}), ...(body && !Buffer.isBuffer(body) ? { 'Content-Type': 'application/json' } : {}) },
    body: bytes, signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs),
  });
  let size = 0; const chunks = [];
  for await (const chunk of response.body) {
    size += chunk.length; report.payloadBytesReceived += chunk.length;
    check(size <= 64 * 1024, 'Relay HTTP response exceeded the live-test bound.'); chunks.push(Buffer.from(chunk));
  }
  payloadBudget(); check(expect == null || response.status === expect, `${method} ${route}: expected HTTP ${expect}, got ${response.status}.`);
  return { status: response.status, bytes: Buffer.concat(chunks), headers: response.headers };
}
async function waitForHealth() {
  const readiness = new AbortController(), started = Date.now(), deadline = started + 150_000;
  let attempts = 0, last = 'No health response.', delayMs = 2000;
  const cancel = () => readiness.abort();
  process.once('SIGINT', cancel); process.once('SIGTERM', cancel);
  try {
    while (Date.now() < deadline) {
      check(!readiness.signal.aborted, 'Relay readiness check cancelled.');
      attempts++;
      let response;
      try { response = await request('/health', { expect: null, timeoutMs: Math.min(20_000, deadline - Date.now()), signal: readiness.signal }); }
      catch (error) {
        check(!readiness.signal.aborted, 'Relay readiness check cancelled.');
        if (error.message.includes('payload budget') || error.message.includes('HTTP response exceeded')) throw error;
        if (error.cause?.code === 'ENOTFOUND') throw new Error('Relay hostname could not be resolved; check the endpoint URL.');
        last = `Health request failed: ${safeError(error)}`;
      }
      if (response) {
        if ([400, 401, 403, 404, 405, 410].includes(response.status))
          throw new Error(`GET /health returned HTTP ${response.status}; check the endpoint and deployed health route. This is not a cold-start response.`);
        let health;
        if (response.status === 200) { try { health = JSON.parse(response.bytes.toString()); } catch {} }
        if (health?.status === 'ok' && health.protocol === 1) {
          report.readiness = { attempts, elapsedMs: Date.now() - started }; return;
        }
        last = `HTTP ${response.status} ${response.headers.get('content-type') ?? 'unknown content type'} without protocol-1 healthy JSON.`;
        if (![200, 502, 503, 504].includes(response.status)) throw new Error(`Relay health unavailable: ${last}`);
      }
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      await new Promise((resolve, reject) => {
        const abort = () => { clearTimeout(timer); reject(new Error('Relay readiness check cancelled.')); };
        const timer = setTimeout(() => { readiness.signal.removeEventListener('abort', abort); resolve(); }, Math.min(delayMs, remaining));
        readiness.signal.addEventListener('abort', abort, { once: true });
        if (readiness.signal.aborted) abort();
      });
      delayMs = Math.min(delayMs * 2, 10_000);
    }
    throw new Error(`Relay did not become healthy within 150 seconds. ${last}`);
  } finally { process.removeListener('SIGINT', cancel); process.removeListener('SIGTERM', cancel); }
}
async function openClient(identity) {
  const wsUrl = new URL('/ws', endpoint); wsUrl.protocol = 'wss:';
  const socket = new WebSocket(wsUrl, { maxPayload: 64 * 1024, handshakeTimeout: 25_000, perMessageDeflate: false });
  const responses = new Map(), events = [], waiters = [];
  const api = {
    identity, socket, events,
    command(type, fields = {}, expectOk = true, timeoutMs = 15_000) {
      check(socket.readyState === WebSocket.OPEN, 'WebSocket is not connected.');
      const requestId = randomUUID(), payload = JSON.stringify({ type, requestId, ...fields });
      report.payloadBytesSent += Buffer.byteLength(payload); payloadBudget();
      const response = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => { responses.delete(requestId); reject(new Error(`Timed out waiting for ${type}.`)); }, timeoutMs);
        responses.set(requestId, { resolve, reject, timeout, type });
      }); socket.send(payload);
      return response.then(message => {
        check(message.ok === expectOk, `${type}: ${message.error ?? 'unexpected success'}`);
        return expectOk ? message.data : message.error;
      });
    },
    event(type, predicate = () => true, after = 0) {
      const seen = events.slice(after).find(message => message.type === type && predicate(message.data));
      if (seen) return Promise.resolve(seen.data);
      return new Promise((resolve, reject) => {
        const waiter = { type, predicate, resolve, reject, timeout: setTimeout(() => {
          waiters.splice(waiters.indexOf(waiter), 1); reject(new Error(`Timed out waiting for ${type}.`));
        }, 15_000) }; waiters.push(waiter);
      });
    },
    async close() {
      if (socket.readyState === WebSocket.CLOSED) return;
      await new Promise(resolve => {
        const timeout = setTimeout(() => { socket.terminate(); resolve(); }, 3000);
        socket.once('close', () => { clearTimeout(timeout); resolve(); }); socket.close();
      });
    },
  };
  clients.push(api);
  socket.on('message', raw => {
    report.payloadBytesReceived += raw.length;
    let message;
    try { message = parseJson(raw.toString()); } catch { socket.terminate(); return; }
    if (identity && (message.type === 'friends.changed' || message.type === 'response' && message.ok && Array.isArray(message.data?.friendReceipts)))
      identity.friendReceipts = [...message.data.friendReceipts];
    if (message.type === 'response') {
      const pending = responses.get(message.requestId);
      if (pending) { responses.delete(message.requestId); clearTimeout(pending.timeout); pending.resolve(message); }
    } else {
      events.push(message);
      for (const waiter of [...waiters]) if (waiter.type === message.type && waiter.predicate(message.data)) {
        waiters.splice(waiters.indexOf(waiter), 1); clearTimeout(waiter.timeout); waiter.resolve(message.data);
      }
    }
  });
  socket.on('error', () => {});
  socket.on('close', () => {
    for (const pending of responses.values()) { clearTimeout(pending.timeout); pending.reject(new Error(`WebSocket closed during ${pending.type}.`)); }
    responses.clear();
    for (const waiter of waiters.splice(0)) { clearTimeout(waiter.timeout); waiter.reject(new Error(`WebSocket closed while waiting for ${waiter.type}.`)); }
  });
  await new Promise((resolve, reject) => { socket.once('open', resolve); socket.once('error', () => reject(new Error('TLS WebSocket connection failed.'))); });
  if (identity) api.friends = await api.command('auth', { protocol: 1, userId: identity.userId, token: identity.token,
    displayName: identity.displayName, friendReceipts: identity.friendReceipts ?? [] });
  return api;
}
async function newIdentity(letter) {
  const registration = await request('/v1/register', { method: 'POST', body: { displayName: `Live Node ${runId} ${letter}` }, expect: 201 });
  const identity = parseJson(registration.bytes.toString());
  check(typeof identity.token === 'string' && identity.token.startsWith('d1.'), 'Registration did not return a signed device credential.');
  identities.push(identity); report.testProfiles.push({ userId: identity.userId, displayName: identity.displayName });
  return openClient(identity);
}
function project() {
  return { name: `Live Node ${runId}`, author: 'Live transport verification', gridSize: 8, variance: 20,
    cells: Array(64).fill(3), componentRoles: Buffer.alloc(64).toString('base64'), rampMask: Buffer.alloc(64).toString('base64'),
    cliffMask: Buffer.alloc(64).toString('base64'), elevationMask: Array(64).fill(127), manualResources: [], manualPlayerStarts: [] };
}
function delta(index, terrain, kind, slot) {
  const x = index % 8, y = Math.floor(index / 8), resource = { kind, x, y }, playerStart = { playerSlot: slot, x, y };
  return { gridSize: 8, indices: [index], cells: [terrain], componentRoles: 'AA==', rampMask: 'AA==', cliffMask: 'AA==', elevationMask: [127],
    manualResources: [resource], manualResourceChanges: [{ x, y, resource }], manualPlayerStarts: [playerStart], manualPlayerStartChanges: [{ playerSlot: slot, playerStart }] };
}
async function offer(bytes, kind = 'map', sha256 = digest(bytes)) {
  const result = await a.command('export.offer', { userId: b.identity.userId, kind, name: `Live ${kind} ${runId}`, size: bytes.length, sha256 });
  pendingExports.add(result.exportId); return result.exportId;
}
async function upload(id, bytes, expect = 200) { return request(`/v1/exports/${id}`, { method: 'PUT', identity: a.identity, body: bytes, expect }); }

try {
  await waitForHealth();
  passed('HTTPS health and protocol');
  a = await newIdentity('A'); b = await newIdentity('B'); stranger = await newIdentity('C');
  passed('Three isolated signed identities over WSS');
  await a.command('friends.request', { friendCode: b.identity.friendCode });
  const requested = await b.command('friends.list'); check(requested.incoming.some(friend => friend.userId === a.identity.userId), 'Friend request was not delivered.');
  check(requested.friends.length === 0, 'An unaccepted request authorized friendship.');
  await b.command('friends.accept', { userId: a.identity.userId });
  const accepted = await a.command('friends.list'); check(accepted.friends.length === 1 && accepted.friends[0].online, 'Accepted friendship or presence failed.');
  equal(a.identity.friendReceipts, b.identity.friendReceipts, 'Acceptance receipts differ.');
  check(a.identity.friendReceipts.length === 1, 'Acceptance receipt was not returned.');
  passed('Explicit friend acceptance, signed receipts and presence');
  const unauthenticated = await openClient(); await unauthenticated.command('friends.list', {}, false); await unauthenticated.close();
  room = await a.command('collab.create', { project: project() });
  await b.command('collab.join', { sessionId: room.sessionId }, false);
  await stranger.command('collab.sync', { sessionId: room.sessionId }, false);
  await stranger.command('collab.join', { sessionId: room.sessionId }, false);
  await a.command('collab.invite', { sessionId: room.sessionId, userId: stranger.identity.userId }, false);
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId });
  await b.event('collab.invite', data => data.sessionId === room.sessionId);
  check((await b.command('collab.join', { sessionId: room.sessionId })).members.length === 2, 'Invitation did not authorize joining.');
  passed('Unauthenticated and stranger ACL; invitation-only joining');
  const revisions = await Promise.all([
    a.command('collab.delta', { sessionId: room.sessionId, delta: delta(0, 5, 4, 1) }),
    b.command('collab.delta', { sessionId: room.sessionId, delta: delta(1, 7, 5, 2) }),
  ]);
  equal(revisions.map(value => value.revision).sort(), [1, 2], 'Concurrent changes were not ordered.');
  await a.event('collab.delta', data => data.sessionId === room.sessionId && data.revision === 2);
  await b.event('collab.delta', data => data.sessionId === room.sessionId && data.revision === 2);
  const synchronized = await a.command('collab.sync', { sessionId: room.sessionId });
  equal(synchronized.project.cells.slice(0, 2), [5, 7], 'Independent strokes did not merge.');
  check(synchronized.project.manualResources.length === 2 && synchronized.project.manualPlayerStarts.length === 2, 'Independent resource/start operations did not merge.');
  for (const peer of [a, b]) equal(peer.events.filter(event => event.type === 'collab.delta').map(event => event.data.revision), [1, 2], 'Peer observed out-of-order changes.');
  await a.command('collab.project', { sessionId: room.sessionId, project: project(), baseRevision: 0 }, false);
  await a.command('collab.delta', { sessionId: room.sessionId, delta: { ...delta(0, 3, 4, 1), elevationMask: [126] } }, false);
  const unchanged = await b.command('collab.sync', { sessionId: room.sessionId });
  equal(unchanged.project, synchronized.project, 'Invalid changes mutated the shared snapshot.'); check(unchanged.revision === 2, 'Rejected changes advanced the revision.');
  const replacement = structuredClone(synchronized.project); replacement.name = `Live replacement ${runId}`;
  await a.command('collab.project', { sessionId: room.sessionId, project: replacement, baseRevision: 2 });
  await b.event('collab.project', data => data.revision === 3);
  await b.command('collab.cursor', { sessionId: room.sessionId, x: 2, y: 3 });
  check((await a.event('collab.cursor')).userId === b.identity.userId, 'Cursor author was not authenticated.');
  await stranger.command('collab.cursor', { sessionId: room.sessionId, x: 1, y: 1 }, false);
  passed('Ordered concurrent edits, merge, atomic validation, revision conflict and cursor');
  const bytes = Buffer.from(`Live opaque map bytes ${runId}`), mapId = await offer(bytes);
  await request(`/v1/exports/${mapId}`, { expect: 401 });
  await request(`/v1/exports/${mapId}`, { identity: stranger.identity, expect: 403 });
  await request(`/v1/exports/${mapId}`, { identity: a.identity, expect: 403 });
  await request(`/v1/exports/${mapId}`, { identity: b.identity, expect: 409 });
  await request(`/v1/exports/${mapId}`, { method: 'PUT', identity: stranger.identity, body: bytes, expect: 403 });
  await upload(mapId, bytes); const offered = await b.event('export.offer', data => data.exportId === mapId);
  check(offered.sha256 === digest(bytes) && offered.kind === 'map', 'Verified export offer metadata differed.');
  const downloaded = await request(`/v1/exports/${mapId}`, { identity: b.identity });
  check(downloaded.bytes.equals(bytes) && digest(downloaded.bytes) === offered.sha256 && downloaded.headers.get('x-content-sha256') === offered.sha256, 'Downloaded bytes failed integrity.');
  await stranger.command('export.decide', { exportId: mapId, status: 'installed' }, false);
  await b.command('export.decide', { exportId: mapId, status: 'denied', message: 'Live test denial; no game installation requested.' });
  check((await a.event('export.result', data => data.exportId === mapId)).status === 'denied', 'Denial was not relayed.'); pendingExports.delete(mapId);
  await request(`/v1/exports/${mapId}`, { identity: b.identity, expect: 404 });
  passed('Map transport integrity, upload/download ACL and recipient denial');
  const civiBytes = Buffer.from(`Live opaque civilization bytes ${runId}`), civiId = await offer(civiBytes, 'civilization');
  await upload(civiId, civiBytes); await b.event('export.offer', data => data.exportId === civiId && data.kind === 'civilization');
  check((await request(`/v1/exports/${civiId}`, { identity: b.identity })).bytes.equals(civiBytes), 'Civilization transport bytes changed.');
  await b.command('export.decide', { exportId: civiId, status: 'installed', message: 'Live transport acknowledgement simulation; no game files changed.' });
  check((await a.event('export.result', data => data.exportId === civiId)).status === 'installed', 'Installed status was not relayed.'); pendingExports.delete(civiId);
  passed('Civilization byte transport and installed-status simulation; no game installation');
  const corruptId = await offer(bytes, 'map', digest(Buffer.from('intentional digest mismatch')));
  await upload(corruptId, bytes, 400);
  check((await a.event('export.result', data => data.exportId === corruptId)).status === 'failed', 'Hash failure did not fail the export.'); pendingExports.delete(corruptId);
  check(!b.events.some(event => event.type === 'export.offer' && event.data.exportId === corruptId), 'Corrupt export became available to the recipient.');
  await request(`/v1/exports/${corruptId}`, { identity: b.identity, expect: 404 });
  passed('SHA-256 mismatch rejection and temporary export removal');
  const withdrawn = await offer(bytes); await upload(withdrawn, bytes);
  await stranger.command('export.cancel', { exportId: withdrawn }, false);
  await a.command('export.cancel', { exportId: withdrawn });
  await b.event('export.cancelled', data => data.exportId === withdrawn);
  check((await a.event('export.result', data => data.exportId === withdrawn)).status === 'failed', 'Sender withdrawal did not fail the pending export.'); pendingExports.delete(withdrawn);
  await request(`/v1/exports/${withdrawn}`, { identity: b.identity, expect: 404 });
  passed('Sender-only withdrawal and recipient cancellation');
  const interrupted = await offer(bytes); await upload(interrupted, bytes);
  const presenceIndex = a.events.length, savedB = b.identity; await b.close();
  await a.event('friends.changed', data => data.friends.some(friend => friend.userId === savedB.userId && !friend.online), presenceIndex);
  check((await a.event('export.result', data => data.exportId === interrupted)).status === 'failed', 'Recipient disconnect did not revoke the export.'); pendingExports.delete(interrupted);
  await a.command('export.offer', { userId: savedB.userId, kind: 'map', name: 'Offline live test', size: bytes.length, sha256: digest(bytes) }, false);
  b = await openClient(savedB); check(b.friends.user.userId === savedB.userId && b.friends.user.friendCode === savedB.friendCode, 'Reconnect changed the signed identity.');
  check(b.friends.friends.some(friend => friend.userId === a.identity.userId && friend.online), 'Reconnect lost accepted friendship.');
  await request(`/v1/exports/${interrupted}`, { identity: b.identity, expect: 404 });
  await a.command('collab.invite', { sessionId: room.sessionId, userId: b.identity.userId }); await b.command('collab.join', { sessionId: room.sessionId });
  const endedIndex = b.events.length, savedA = a.identity; await a.close();
  await b.event('collab.ended', data => data.sessionId === room.sessionId, endedIndex);
  a = await openClient(savedA); check(a.friends.user.userId === savedA.userId && a.friends.user.friendCode === savedA.friendCode, 'Host reconnect changed identity.');
  await b.command('collab.sync', { sessionId: room.sessionId }, false); room = undefined;
  passed('Disconnect revocation, host closure and signed identity/receipt reconnect');
  report.passed = true;
} catch (error) { report.error = safeError(error); console.error(`LIVE FAIL ${report.error}`); }
finally {
  async function cleanup(label, action) { try { await action(); report.cleanup.push({ action: label, ok: true }); } catch (error) { report.cleanup.push({ action: label, ok: false, error: safeError(error) }); } }
  for (const exportId of pendingExports) if (a?.socket.readyState === WebSocket.OPEN)
    await cleanup('Withdraw remaining test export', () => a.command('export.cancel', { exportId }, true, 5000));
  for (const peer of [a, b]) if (peer?.socket.readyState === WebSocket.OPEN)
    await cleanup('Leave own collaboration session', () => peer.command('collab.leave', {}, true, 5000));
  if (a?.socket.readyState === WebSocket.OPEN && b?.identity)
    await cleanup('Remove only the two test profiles friendship', () => a.command('friends.remove', { userId: b.identity.userId }, true, 5000));
  else if (b?.socket.readyState === WebSocket.OPEN && a?.identity)
    await cleanup('Remove only the two test profiles friendship', () => b.command('friends.remove', { userId: a.identity.userId }, true, 5000));
  else if (a?.identity && b?.identity)
    report.cleanup.push({ action: 'Remove only the two test profiles friendship', ok: false, skipped: true,
      error: 'Both test peers are disconnected; friendship removal could not be confirmed.' });
  for (const peer of clients) await cleanup('Disconnect test socket', () => peer.close());
  report.finishedAt = new Date().toISOString();
  report.testProfilesLeftOffline = report.testProfiles.length;
  report.note = 'Only these uniquely named test profiles were used; the relay has no account-delete endpoint. Installed result was simulated; no game files were changed.';
  if (report.cleanup.some(item => !item.ok)) report.passed = false;
  if (report.payloadBytesSent + report.payloadBytesReceived >= 256 * 1024) { report.passed = false; report.error ??= 'Application payload budget exceeded.'; }
  await mkdir(path.dirname(reportPath), { recursive: true }); await writeFile(reportPath, JSON.stringify(report, null, 2), { mode: 0o600 });
  console.log(`LIVE ${report.passed ? 'PASS' : 'FAIL'}: ${report.checks.length} checks; ${report.payloadBytesSent + report.payloadBytesReceived} application bytes; ${report.testProfiles.length} test profiles left offline.`);
  console.log(`Report: ${reportPath}`); if (!report.passed) process.exitCode = 1;
}
