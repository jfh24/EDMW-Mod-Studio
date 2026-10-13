// This follows MapProject.Validate and MapDelta.ApplyTo in the desktop app.
// Byte arrays are base64 in System.Text.Json; signed elevations are numeric arrays.
const resourceKinds = new Set([0, 1, 3, 4, 5, ...Array.from({ length: 16 }, (_, i) => i + 16)]);
const waterResource = kind => [3, 21, 29].includes(kind);
const safeStart = terrain => [2, 3, 5, 7].includes(terrain);
const integer = (value, min, max) => Number.isInteger(value) && value >= min && value <= max;
function requireValue(condition, message) { if (!condition) throw new Error(message); }
function object(value, name) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value), `${name} must be an object.`);
  return value;
}
function text(value, name, fallback = '') {
  value ??= fallback;
  requireValue(typeof value === 'string' && value.length <= 512, `${name} is too long or invalid.`);
  return value;
}
function bytes(value, count, name, defaults = false) {
  if (defaults && (value == null || value === '')) return Buffer.alloc(count);
  requireValue(typeof value === 'string' && value.length <= Math.ceil(count / 3) * 4 &&
    /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value), `${name} must be base64.`);
  const decoded = Buffer.from(value, 'base64');
  requireValue(decoded.length === count && decoded.toString('base64') === value, `${name} has the wrong length.`);
  return decoded;
}
function array(value, maximum, name) {
  requireValue(Array.isArray(value) && value.length <= maximum, `${name} is invalid or too large.`);
  return value;
}
function coordinate(value, size, name) { requireValue(integer(value, 0, size - 1), `${name} is outside the map.`); }
function resource(value, size, cells) {
  object(value, 'Resource');
  coordinate(value.x, size, 'Resource x'); coordinate(value.y, size, 'Resource y');
  requireValue(resourceKinds.has(value.kind), 'Unknown resource kind.');
  requireValue(waterResource(value.kind) === (cells[value.y * size + value.x] <= 1), 'Resource terrain is incompatible.');
  return { kind: value.kind, x: value.x, y: value.y };
}
function playerStart(value, size, cells) {
  object(value, 'Player start');
  requireValue(integer(value.playerSlot, 1, 8), 'Player slot must be 1-8.');
  coordinate(value.x, size, 'Player x'); coordinate(value.y, size, 'Player y');
  requireValue(safeStart(cells[value.y * size + value.x]), 'Player start terrain is unsafe.');
  return { playerSlot: value.playerSlot, x: value.x, y: value.y };
}

export function validateProject(value) {
  object(value, 'Project');
  const size = value.gridSize;
  requireValue(integer(size, 8, 512), 'Grid size must be 8-512.');
  const count = size * size;
  const cells = array(value.cells, count, 'Cells').slice();
  requireValue(cells.length === count && cells.every(cell => integer(cell, 0, 7)), 'Invalid terrain cells.');
  const roles = bytes(value.componentRoles, count, 'Component roles', true);
  const ramps = bytes(value.rampMask, count, 'Ramp mask', true);
  const cliffs = bytes(value.cliffMask, count, 'Cliff mask', true);
  const elevations = value.elevationMask == null || Array.isArray(value.elevationMask) && value.elevationMask.length === 0
    ? Array(count).fill(127) : array(value.elevationMask, count, 'Elevation mask').slice();
  requireValue(elevations.length === count && elevations.every(v => integer(v, -100, 100) || v === 126 || v === 127), 'Invalid elevations.');
  requireValue(roles.every(v => v <= 4) && ramps.every(v => v <= 1) && cliffs.every(v => v <= 1), 'Invalid role or mask value.');
  for (let i = 0; i < count; i++) {
    if (elevations[i] === 127) cliffs[i] = 0;
    requireValue(elevations[i] !== 126 || cells[i] === 6, 'Cliffs must use mountain terrain.');
  }
  const variance = value.variance ?? 20;
  requireValue(integer(variance, -2147483648, 2147483647), 'Invalid variance.');
  requireValue(value.generationSeed == null || integer(value.generationSeed, -2147483648, 2147483647), 'Invalid generation seed.');
  requireValue(value.preserveCoastline == null || typeof value.preserveCoastline === 'boolean', 'Invalid coastline flag.');
  const resources = array(value.manualResources ?? [], count * 4, 'Resources').map(v => resource(v, size, cells));
  const starts = array(value.manualPlayerStarts ?? [], 8, 'Player starts').map(v => playerStart(v, size, cells));
  requireValue(new Set(starts.map(v => v.playerSlot)).size === starts.length, 'Duplicate player slots.');
  return {
    name: text(value.name, 'Map name', 'My Custom Map'), author: text(value.author, 'Author'),
    gridSize: size, variance: Math.max(0, Math.min(50, variance)), generationSeed: value.generationSeed ?? null,
    preserveCoastline: value.preserveCoastline ?? false,
    cells, componentRoles: roles.toString('base64'), rampMask: ramps.toString('base64'),
    cliffMask: cliffs.toString('base64'), elevationMask: elevations,
    manualResources: resources, manualPlayerStarts: starts,
  };
}

export function applyDelta(project, value) {
  object(value, 'Delta');
  const size = project.gridSize, count = size * size;
  requireValue(value.gridSize === size, 'Delta grid differs; synchronize first.');
  const indices = array(value.indices, count, 'Indices');
  const cells = array(value.cells, indices.length, 'Cells');
  requireValue(cells.length === indices.length && cells.every(v => integer(v, 0, 7)), 'Invalid changed terrain.');
  const roles = bytes(value.componentRoles, indices.length, 'Component roles');
  const ramps = bytes(value.rampMask, indices.length, 'Ramp mask');
  requireValue(value.cliffMask !== null, 'Cliff mask must be base64 when present.');
  const cliffs = value.cliffMask === undefined || value.cliffMask === '' ? null : bytes(value.cliffMask, indices.length, 'Cliff mask');
  const elevations = array(value.elevationMask, indices.length, 'Elevation mask');
  requireValue(elevations.length === indices.length, 'Delta masks differ in length.');
  requireValue(indices.every(v => integer(v, 0, count - 1)), 'Change is outside the map.');
  const candidate = structuredClone(project);
  const candidateRoles = Buffer.from(candidate.componentRoles, 'base64');
  const candidateRamps = Buffer.from(candidate.rampMask, 'base64');
  const candidateCliffs = Buffer.from(candidate.cliffMask, 'base64');
  for (let i = 0; i < indices.length; i++) {
    const index = indices[i], x = index % size, y = Math.floor(index / size);
    candidate.cells[index] = cells[i]; candidateRoles[index] = roles[i];
    candidateRamps[index] = ramps[i]; if (cliffs) candidateCliffs[index] = cliffs[i];
    candidate.elevationMask[index] = elevations[i];
    candidate.manualResources = candidate.manualResources.filter(r => r.x !== x || r.y !== y || waterResource(r.kind) === (cells[i] <= 1));
    if (!safeStart(cells[i])) candidate.manualPlayerStarts = candidate.manualPlayerStarts.filter(s => s.x !== x || s.y !== y);
  }
  candidate.componentRoles = candidateRoles.toString('base64'); candidate.rampMask = candidateRamps.toString('base64');
  candidate.cliffMask = candidateCliffs.toString('base64');
  if (value.manualResourceChanges != null) {
    const changes = array(value.manualResourceChanges, count, 'Resource changes'), seen = new Set();
    for (const change of changes) {
      object(change, 'Resource change'); coordinate(change.x, size, 'Resource x'); coordinate(change.y, size, 'Resource y');
      const key = `${change.x},${change.y}`;
      requireValue(!seen.has(key), 'Duplicate resource changes.'); seen.add(key);
      requireValue(change.resource == null || (change.resource.x === change.x && change.resource.y === change.y), 'Resource change location differs.');
      candidate.manualResources = candidate.manualResources.filter(r => r.x !== change.x || r.y !== change.y);
      if (change.resource != null) candidate.manualResources.push(change.resource);
    }
    candidate.manualResources.sort((a, b) => a.y - b.y || a.x - b.x || a.kind - b.kind);
  } else if (value.manualResources != null) candidate.manualResources = value.manualResources;
  if (value.manualPlayerStartChanges != null) {
    const changes = array(value.manualPlayerStartChanges, 8, 'Player changes'), seen = new Set();
    for (const change of changes) {
      object(change, 'Player change'); requireValue(integer(change.playerSlot, 1, 8) && !seen.has(change.playerSlot), 'Invalid or duplicate player change.');
      seen.add(change.playerSlot);
      requireValue(change.playerStart == null || change.playerStart.playerSlot === change.playerSlot, 'Player change slot differs.');
      candidate.manualPlayerStarts = candidate.manualPlayerStarts.filter(s => s.playerSlot !== change.playerSlot);
      if (change.playerStart != null) candidate.manualPlayerStarts.push(change.playerStart);
    }
    candidate.manualPlayerStarts.sort((a, b) => a.playerSlot - b.playerSlot);
  } else if (value.manualPlayerStarts != null) candidate.manualPlayerStarts = value.manualPlayerStarts;
  for (const field of ['name', 'author', 'variance', 'preserveCoastline']) if (value[field] != null) candidate[field] = value[field];
  requireValue(value.generationSeedChanged == null || typeof value.generationSeedChanged === 'boolean', 'Invalid seed change flag.');
  if (value.generationSeedChanged) candidate.generationSeed = value.generationSeed ?? null;
  // Validate the resulting clone before touching the authoritative room snapshot.
  const result = validateProject(candidate);
  return result;
}
