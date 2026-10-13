import { createHmac, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const code = /^[A-F0-9]{12}$/;
function requireValid(condition) { if (!condition) throw new Error('Invalid signed identity or friend receipt.'); }
export async function loadIdentitySecret(dataDir, options) {
  let secret = options.identitySecret ?? process.env.IDENTITY_SECRET;
  if (!secret) {
    if (process.env.RENDER || options.requireIdentitySecret) throw new Error('IDENTITY_SECRET is required for the Render relay.');
    const file = path.join(dataDir, 'identity-secret');
    try { secret = (await readFile(file, 'utf8')).trim(); }
    catch (error) {
      if (error.code !== 'ENOENT') throw error;
      secret = randomBytes(32).toString('base64url');
      try { await writeFile(file, secret, { flag: 'wx', mode: 0o600 }); }
      catch (writeError) {
        if (writeError.code !== 'EEXIST') throw writeError;
        secret = (await readFile(file, 'utf8')).trim();
      }
    }
  }
  if (typeof secret !== 'string' || secret.length < 32 || secret.length > 1024)
    throw new Error('IDENTITY_SECRET must contain 32-1024 characters.');
  return secret;
}

export function identitySigner(secret) {
  const sign = (prefix, value) => {
    const payload = Buffer.from(JSON.stringify(value)).toString('base64url');
    const header = `${prefix}.${payload}`;
    return `${header}.${createHmac('sha256', secret).update(header).digest('base64url')}`;
  };
  function verify(prefix, value, maximum) {
    requireValid(typeof value === 'string' && value.length <= maximum);
    const pieces = value.split('.');
    requireValid(pieces.length === 3 && pieces[0] === prefix && /^[A-Za-z0-9_-]+$/.test(pieces[1]) && /^[A-Za-z0-9_-]{43}$/.test(pieces[2]));
    const expected = createHmac('sha256', secret).update(`${pieces[0]}.${pieces[1]}`).digest();
    const actual = Buffer.from(pieces[2], 'base64url');
    requireValid(actual.length === expected.length && actual.toString('base64url') === pieces[2] && timingSafeEqual(actual, expected));
    const decoded = Buffer.from(pieces[1], 'base64url');
    requireValid(decoded.toString('base64url') === pieces[1]);
    let payload;
    try { payload = JSON.parse(decoded.toString('utf8')); } catch { throw new Error('Invalid signed identity or friend receipt.'); }
    requireValid(payload && typeof payload === 'object' && !Array.isArray(payload));
    return payload;
  }
  function publicProfile(value) {
    requireValid(value && typeof value === 'object' && uuid.test(value.userId) && code.test(value.friendCode) &&
      typeof value.displayName === 'string' && value.displayName.trim().length > 0 && value.displayName.length <= 64 && !/[\x00-\x1f\x7f]/.test(value.displayName));
    return { userId: value.userId, friendCode: value.friendCode, displayName: value.displayName };
  }
  return {
    device(userId, friendCode) { return sign('d1', { userId, friendCode, nonce: randomUUID() }); },
    verifyDevice(value) {
      const payload = verify('d1', value, 512);
      requireValid(uuid.test(payload.userId) && code.test(payload.friendCode) && uuid.test(payload.nonce));
      return { userId: payload.userId, friendCode: payload.friendCode };
    },
    receipt(a, b) { return sign('f1', { version: 1, nonce: randomUUID(), users: [publicProfile(a), publicProfile(b)] }); },
    verifyReceipt(value) {
      const payload = verify('f1', value, 2048);
      requireValid(payload.version === 1 && uuid.test(payload.nonce) && Array.isArray(payload.users) && payload.users.length === 2);
      const users = payload.users.map(publicProfile);
      requireValid(users[0].userId !== users[1].userId && users[0].friendCode !== users[1].friendCode);
      return { users, nonce: payload.nonce };
    },
  };
}
