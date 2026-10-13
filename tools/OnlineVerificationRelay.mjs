// Isolated local verification runner. Production uses relay/src/server.js.
import path from 'node:path';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { createRelay } from '../relay/src/server.js';

const controlDir = path.resolve(process.env.ONLINE_VERIFICATION_CONTROL_DIR ?? '');
if (!path.basename(controlDir).startsWith('online-') || path.basename(path.dirname(controlDir)) !== '.verification')
  throw new Error('Verification control directory must be an isolated .verification/online-* directory.');
const initialDataDir = path.resolve(process.env.RELAY_DATA_DIR ?? path.join(controlDir, 'data'));
if (initialDataDir !== path.join(controlDir, 'data')) throw new Error('Unexpected verification data directory.');
await mkdir(controlDir, { recursive: true });
const identitySecret = randomBytes(32).toString('base64');
const port = Number(process.env.PORT);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid local verification port.');
let generation = 0, lastRequest = '', stopping = false, checking = false;
let relay = await createRelay({ dataDir: initialDataDir, identitySecret });
await relay.listen(port, '127.0.0.1');
console.log('Isolated verification relay ready.');

const timer = setInterval(async () => {
  if (checking || stopping) return;
  checking = true;
  try {
    let request;
    try { request = (await readFile(path.join(controlDir, 'restart.request'), 'utf8')).trim(); }
    catch (error) { if (error.code === 'ENOENT') return; throw error; }
    if (!/^[0-9a-f-]{36}$/.test(request) || request === lastRequest) return;
    lastRequest = request;
    await relay.close();
    // A fresh directory models Render replacing its ephemeral filesystem.
    // Preserve earlier directories for diagnostics instead of deleting data.
    const dataDir = path.join(controlDir, `data-reset-${++generation}`);
    relay = await createRelay({ dataDir, identitySecret });
    await relay.listen(port, '127.0.0.1');
    await writeFile(path.join(controlDir, 'restart.done'), request, 'utf8');
    console.log(`Ephemeral filesystem replacement ${generation} complete.`);
  } catch (error) {
    console.error('Verification relay reset failed:', error.message);
    clearInterval(timer);
    process.exitCode = 1;
    await relay.close().catch(() => {});
  } finally { checking = false; }
}, 100);

async function stop() {
  if (stopping) return;
  stopping = true;
  clearInterval(timer);
  while (checking) await new Promise(resolve => setTimeout(resolve, 20));
  await relay.close();
  process.exit(0);
}
process.on('SIGTERM', stop);
process.on('SIGINT', stop);
