import * as THREE from 'three';
import { fbm, fbm3D, hash3, smoothstep } from './math.js';
import {
  BIOME, GRASS_COLORS, CHUNK_SIZE, SEA_LEVEL, WORLD_BOTTOM,
  BLOCK, TREE_GRID, CACTUS_GRID, CACTUS_CHANCE
} from './config.js';
import {
  SEED, SPAWN_BIOME, BIOME_BLEND, blockChanges, blockFacings,
  caveCache, oreCache, treeCache, cactusCache
} from './core.js';

export function getPlayerBlock(x, y, z) {
  const k = `${x},${y},${z}`;
  return blockChanges.has(k) ? blockChanges.get(k) : null;
}

export function getBlockFacing(x, y, z) {
  const k = `${x},${y},${z}`;
  return blockFacings.has(k) ? blockFacings.get(k) : 0;
}

export function getBiomeRaw(x, z) {
  if (SPAWN_BIOME !== undefined && Math.abs(x) < 200 && Math.abs(z) < 200) return SPAWN_BIOME;
  const bn = fbm(x * .0025, z * .0025, SEED, 3);
  let b;
  if (bn < .32) b = BIOME.DESERT;
  else if (bn > .62) b = BIOME.FOREST;
  else if (bn < .45) b = BIOME.SNOWY;
  else b = BIOME.PLAINS;
  const rh = fbm(x * .008, z * .008, SEED, 5) * 30 - 10;
  if (rh >= 22) b = BIOME.MOUNTAIN;
  const og = fbm(x * .0018 + 500, z * .0018 + 500, SEED, 3);
  if (b === BIOME.FOREST && og > .68) b = BIOME.OLD_GROWTH;
  return b;
}

export function getBiome(x, z) {
  return getBiomeRaw(x, z);
}

const _c1 = new THREE.Color();
const _c2 = new THREE.Color();

export function getGrassColorAt(x, z) {
  const raw = getBiomeRaw(x, z);
  if (BIOME_BLEND <= 0) return GRASS_COLORS[raw] || GRASS_COLORS[BIOME.PLAINS];
  const wx = Math.floor(x / BIOME_BLEND), wz = Math.floor(z / BIOME_BLEND);
  const fx = (x - wx * BIOME_BLEND) / BIOME_BLEND;
  const fz = (z - wz * BIOME_BLEND) / BIOME_BLEND;
  const b00 = getBiomeRaw(wx * BIOME_BLEND, wz * BIOME_BLEND);
  const b10 = getBiomeRaw((wx + 1) * BIOME_BLEND, wz * BIOME_BLEND);
  const b01 = getBiomeRaw(wx * BIOME_BLEND, (wz + 1) * BIOME_BLEND);
  const b11 = getBiomeRaw((wx + 1) * BIOME_BLEND, (wz + 1) * BIOME_BLEND);
  _c1.setHex(GRASS_COLORS[b00] || 0x91bd59);
  _c2.setHex(GRASS_COLORS[b10] || 0x91bd59);
  _c1.lerp(_c2, fx);
  _c2.setHex(GRASS_COLORS[b01] || 0x91bd59);
  _c2.lerp(new THREE.Color(GRASS_COLORS[b11] || 0x91bd59), fx);
  _c1.lerp(_c2, fz);
  return _c1.getHex();
}

export function getHeight(x, z) {
  const bn = fbm(x * .0025, z * .0025, SEED, 3);
  const base = fbm(x * .008, z * .008, SEED, 5);
  const ridge = fbm(x * .02, z * .02, SEED, 3);
  const rp = Math.pow(ridge, 2.5);
  const nh = base * 30 + rp * 25 - 12;
  const dh = 3 + base * 3;
  const fh = nh * .85;
  let h;
  if (bn < .25) h = dh;
  else if (bn < .4) { const t = smoothstep((bn - .25) / .15); h = dh * (1 - t) + nh * t; }
  else if (bn < .55) h = nh;
  else if (bn < .7) { const t = smoothstep((bn - .55) / .15); h = nh * (1 - t) + fh * t; }
  else h = fh;
  return Math.floor(h);
}

function chunkKey(cx, cz) { return `${cx},${cz}`; }

function buildCaveMap(cx, cz) {
  const caves = new Set();
  const bX = cx * CHUNK_SIZE, bZ = cz * CHUNK_SIZE;
  for (let lx = 0; lx < CHUNK_SIZE; lx++) for (let lz = 0; lz < CHUNK_SIZE; lz++) {
    const wx = bX + lx, wz = bZ + lz;
    const gh = getHeight(wx, wz);
    for (let y = WORLD_BOTTOM + 2; y < gh - 2; y++) {
      const depthFactor = Math.min(1, Math.max(0, (2 - Math.abs(y + 25) / 15)));
      if (depthFactor <= 0) continue;
      const n = fbm3D(wx * .08, y * .08, wz * .08, SEED, 3);
      const threshold = 0.68 - 0.10 * depthFactor;
      if (n > threshold) caves.add(`${lx},${y},${lz}`);
    }
  }
  return caves;
}

function getCachedCaveMap(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!caveCache.has(k)) caveCache.set(k, buildCaveMap(cx, cz));
  return caveCache.get(k);
}

export function isCave(x, y, z) {
  if (y <= WORLD_BOTTOM + 1) return false;
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const lx = x - cx * CHUNK_SIZE, lz = z - cz * CHUNK_SIZE;
  return getCachedCaveMap(cx, cz).has(`${lx},${y},${lz}`);
}

function buildOreBlocks(cx, cz) {
  const blocks = new Map();
  const bX = cx * CHUNK_SIZE, bZ = cz * CHUNK_SIZE;
  const coalCount = 4 + Math.floor(hash3(cx, cz, 101, SEED) * 3);
  for (let v = 0; v < coalCount; v++) {
    const sx = 4 + Math.floor(hash3(cx, cz, 200 + v, SEED) * 8);
    const sz = 4 + Math.floor(hash3(cx, cz, 300 + v, SEED) * 8);
    const sy = -25 + Math.floor(hash3(cx, cz, 400 + v, SEED) * 33);
    const size = 4 + Math.floor(hash3(cx, cz, 500 + v, SEED) * 9);
    growVein(blocks, bX, bZ, sx, sy, sz, size, BLOCK.COAL_ORE, cx, cz, 600 + v);
  }
  const ironCount = 4 + Math.floor(hash3(cx, cz, 102, SEED) * 4);
  for (let v = 0; v < ironCount; v++) {
    const sx = 4 + Math.floor(hash3(cx, cz, 700 + v, SEED) * 8);
    const sz = 4 + Math.floor(hash3(cx, cz, 800 + v, SEED) * 8);
    const sy = -25 + Math.floor(hash3(cx, cz, 900 + v, SEED) * 40);
    const size = 4 + Math.floor(hash3(cx, cz, 1000 + v, SEED) * 6);
    growVein(blocks, bX, bZ, sx, sy, sz, size, BLOCK.IRON_ORE, cx, cz, 1100 + v);
  }
  return blocks;
}

function growVein(blocks, bX, bZ, sx, sy, sz, size, oreId, cx, cz, seed) {
  let x = sx, y = sy, z = sz;
  for (let i = 0; i < size; i++) {
    if (x >= 0 && x < CHUNK_SIZE && z >= 0 && z < CHUNK_SIZE && y > WORLD_BOTTOM && y < 64) {
      const wx = bX + x, wz = bZ + z, gh = getHeight(wx, wz);
      if (y < gh - 3 && y < gh - 1) blocks.set(`${x},${y},${z}`, oreId);
    }
    const d = hash3(cx * 100 + i, cz * 100 + i, seed + i, SEED) * 6, dir = Math.floor(d);
    if (dir === 0) x++;
    else if (dir === 1) x--;
    else if (dir === 2) y++;
    else if (dir === 3) y--;
    else if (dir === 4) z++;
    else z--;
  }
}

function getCachedOreBlocks(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!oreCache.has(k)) oreCache.set(k, buildOreBlocks(cx, cz));
  return oreCache.get(k);
}

export function getOreAt(x, y, z) {
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const b = getCachedOreBlocks(cx, cz);
  const lx = x - cx * CHUNK_SIZE, lz = z - cz * CHUNK_SIZE;
  const v = b.get(`${lx},${y},${lz}`);
  return v !== undefined ? v : null;
}

export function getBaseBlock(x, y, z) {
  const height = getHeight(x, z), biome = getBiome(x, z);

  if (y > height) {
    if (y <= SEA_LEVEL) {
      if (biome === BIOME.SNOWY && y === SEA_LEVEL) return BLOCK.ICE;
      return BLOCK.WATER;
    }
    return BLOCK.AIR;
  }

  if (y < height - 2 && isCave(x, y, z)) return BLOCK.AIR;
  if (y < height - 3) {
    const ore = getOreAt(x, y, z);
    if (ore !== null) return ore;
  }
  const isOld = biome === BIOME.OLD_GROWTH;
  if (y === height) {
    if (biome === BIOME.SNOWY) {
      if (height < SEA_LEVEL) return BLOCK.ICE;
      return BLOCK.SNOW;
    }
    if (height < SEA_LEVEL && biome !== BIOME.SNOWY) return BLOCK.SAND;
    if (biome === BIOME.DESERT) return BLOCK.SAND;
    if (biome === BIOME.FOREST) { if (height >= 24) return BLOCK.STONE; return BLOCK.GRASS; }
    if (isOld) { if (height >= 24) return BLOCK.STONE; return BLOCK.GRASS; }
    if (biome === BIOME.MOUNTAIN) { if (height >= 28) return BLOCK.SNOW; return BLOCK.STONE; }
    const nw = (biome !== BIOME.SNOWY) && (getHeight(x + 3, z) <= SEA_LEVEL || getHeight(x - 3, z) <= SEA_LEVEL || getHeight(x, z + 3) <= SEA_LEVEL || getHeight(x, z - 3) <= SEA_LEVEL);
    if (biome !== BIOME.SNOWY) {
      if (height <= SEA_LEVEL + 1 || nw) return BLOCK.SAND;
      if (height >= 28) return BLOCK.SNOW;
      if (height >= 20) return BLOCK.STONE;
    }
    return BLOCK.GRASS;
  }
  if (y >= height - 3) {
    if (biome === BIOME.SNOWY) return BLOCK.DIRT;
    if (isOld) return BLOCK.DIRT_DARK;
    if (height < SEA_LEVEL && biome !== BIOME.SNOWY) return BLOCK.SAND;
    if (biome === BIOME.DESERT) return BLOCK.SAND;
    if (biome === BIOME.MOUNTAIN) return BLOCK.STONE;
    if (height >= 20) return BLOCK.STONE;
    return BLOCK.DIRT;
  }
  return BLOCK.STONE;
}

// -------------------- SHORT GRASS SPAWN (patch-based) --------------------

export function shouldSpawnShortGrass(x, z) {
  const biome = getBiomeRaw(x, z);
  const h = getHeight(x, z);
  if (h <= SEA_LEVEL) return false;
  const surface = getBaseBlock(x, h, z);
  if (surface !== BLOCK.GRASS) return false;

  let chance = 0;
  if (biome === BIOME.PLAINS) chance = 0.3;
  else if (biome === BIOME.FOREST) chance = 0.35;
  else if (biome === BIOME.OLD_GROWTH) chance = 0.25;
  else return false;

  // Patch noise: grass grows in clusters
  const patch = fbm(x * 0.15, z * 0.15, SEED + 9001, 2);
  if (patch < 0.4) return false;

  const r = hash3(x, 0, z, SEED + 7777);
  return r < chance;
}

// -------------------- TREES --------------------

function getTreeDensity(b) {
  if (b === BIOME.FOREST) return .9;
  if (b === BIOME.OLD_GROWTH) return 1.4;
  if (b === BIOME.PLAINS) return .4;
  if (b === BIOME.MOUNTAIN) return .05;
  if (b === BIOME.DESERT) return 0;
  if (b === BIOME.SNOWY) return 0;
  return .3;
}

function getTreeForCell(cx, cz) {
  const sx = cx * TREE_GRID + TREE_GRID / 2, sz = cz * TREE_GRID + TREE_GRID / 2;
  const biome = getBiome(sx, sz);
  if (hash3(cx, cz, 42, SEED) > getTreeDensity(biome)) return null;
  const ox = Math.floor(hash3(cx, cz, 101, SEED) * TREE_GRID);
  const oz = Math.floor(hash3(cx, cz, 102, SEED) * TREE_GRID);
  const tx = cx * TREE_GRID + ox, tz = cz * TREE_GRID + oz;
  const gy = getHeight(tx, tz);
  if (gy <= SEA_LEVEL) return null;
  if (getBaseBlock(tx, gy, tz) !== BLOCK.GRASS) return null;
  if (isCave(tx, gy, tz) || isCave(tx, gy + 1, tz)) return null;
  let th, cr;
  if (biome === BIOME.OLD_GROWTH) { th = 9 + Math.floor(hash3(tx, tz, 1, SEED) * 5); cr = 3 + Math.floor(hash3(tx, tz, 3, SEED) * 3); }
  else if (biome === BIOME.FOREST) { th = 5 + Math.floor(hash3(tx, tz, 1, SEED) * 4); cr = 2 + Math.floor(hash3(tx, tz, 3, SEED) * 2); }
  else { th = 4 + Math.floor(hash3(tx, tz, 1, SEED) * 3); cr = 2 + Math.floor(hash3(tx, tz, 3, SEED) * 2); }
  return { treeX: tx, treeZ: tz, groundY: gy, trunkHeight: th, canopyRadius: cr, isOld: biome === BIOME.OLD_GROWTH };
}

export function buildTreeBlocks(cx, cz) {
  const blocks = new Map(), bX = cx * CHUNK_SIZE, bZ = cz * CHUNK_SIZE;
  const mcX0 = Math.floor((bX - 8) / TREE_GRID), mcX1 = Math.floor((bX + CHUNK_SIZE + 8) / TREE_GRID);
  const mcZ0 = Math.floor((bZ - 8) / TREE_GRID), mcZ1 = Math.floor((bZ + CHUNK_SIZE + 8) / TREE_GRID);
  for (let cx2 = mcX0; cx2 <= mcX1; cx2++) for (let cz2 = mcZ0; cz2 <= mcZ1; cz2++) {
    const t = getTreeForCell(cx2, cz2);
    if (!t) continue;
    const { treeX, treeZ, groundY, trunkHeight, canopyRadius } = t;
    for (let ty = 1; ty <= trunkHeight; ty++) {
      const lx = treeX - bX, lz = treeZ - bZ, ly = groundY + ty;
      if (lx >= 0 && lx < CHUNK_SIZE && lz >= 0 && lz < CHUNK_SIZE) blocks.set(`${lx},${ly},${lz}`, BLOCK.LOG);
    }
    const ccY = groundY + trunkHeight - 1;
    for (let ly = ccY - 1; ly <= ccY + 2; ly++) for (let dx = -canopyRadius; dx <= canopyRadius; dx++) for (let dz = -canopyRadius; dz <= canopyRadius; dz++) {
      const lx = treeX - bX + dx, lz = treeZ - bZ + dz;
      if (lx < 0 || lx >= CHUNK_SIZE || lz < 0 || lz >= CHUNK_SIZE) continue;
      if (blocks.get(`${lx},${ly},${lz}`) === BLOCK.LOG) continue;
      const dSq = dx * dx + dz * dz + (ly - ccY) * (ly - ccY);
      if (dSq <= canopyRadius * canopyRadius + 2 && hash3(lx + bX, ly, lz + bZ, SEED) < .9 && !blocks.has(`${lx},${ly},${lz}`)) blocks.set(`${lx},${ly},${lz}`, BLOCK.LEAVES);
    }
  }
  return blocks;
}

function getCachedTreeBlocks(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!treeCache.has(k)) treeCache.set(k, buildTreeBlocks(cx, cz));
  return treeCache.get(k);
}

export function getTreeBlock(x, y, z) {
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const b = getCachedTreeBlocks(cx, cz);
  const lx = x - cx * CHUNK_SIZE, lz = z - cz * CHUNK_SIZE;
  const v = b.get(`${lx},${y},${lz}`);
  return v !== undefined ? v : null;
}

function getCactusForCell(cx, cz) {
  const sx = cx * CACTUS_GRID + CACTUS_GRID / 2, sz = cz * CACTUS_GRID + CACTUS_GRID / 2;
  if (getBiome(sx, sz) !== BIOME.DESERT) return null;
  if (hash3(cx, cz, 77, SEED) > CACTUS_CHANCE) return null;
  const ox = Math.floor(hash3(cx, cz, 201, SEED) * CACTUS_GRID);
  const oz = Math.floor(hash3(cx, cz, 202, SEED) * CACTUS_GRID);
  const ctx = cx * CACTUS_GRID + ox, ctz = cz * CACTUS_GRID + oz;
  const gy = getHeight(ctx, ctz);
  if (gy <= SEA_LEVEL) return null;
  if (getBaseBlock(ctx, gy, ctz) !== BLOCK.SAND) return null;
  if (isCave(ctx, gy, ctz) || isCave(ctx, gy + 1, ctz)) return null;
  const h = 1 + Math.floor(hash3(ctx, ctz, 88, SEED) * 3);
  return { cactusX: ctx, cactusZ: ctz, groundY: gy, height: h };
}

export function buildCactusBlocks(cx, cz) {
  const blocks = new Map(), bX = cx * CHUNK_SIZE, bZ = cz * CHUNK_SIZE;
  const mcX0 = Math.floor((bX - 6) / CACTUS_GRID), mcX1 = Math.floor((bX + CHUNK_SIZE + 6) / CACTUS_GRID);
  const mcZ0 = Math.floor((bZ - 6) / CACTUS_GRID), mcZ1 = Math.floor((bZ + CHUNK_SIZE + 6) / CACTUS_GRID);
  for (let cx2 = mcX0; cx2 <= mcX1; cx2++) for (let cz2 = mcZ0; cz2 <= mcZ1; cz2++) {
    const c = getCactusForCell(cx2, cz2);
    if (!c) continue;
    const { cactusX, cactusZ, groundY, height } = c;
    for (let ty = 1; ty <= height; ty++) {
      const lx = cactusX - bX, lz = cactusZ - bZ, ly = groundY + ty;
      if (lx >= 0 && lx < CHUNK_SIZE && lz >= 0 && lz < CHUNK_SIZE) blocks.set(`${lx},${ly},${lz}`, BLOCK.CACTUS);
    }
  }
  return blocks;
}

function getCachedCactusBlocks(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!cactusCache.has(k)) cactusCache.set(k, buildCactusBlocks(cx, cz));
  return cactusCache.get(k);
}

export function getCactusBlock(x, y, z) {
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const b = getCachedCactusBlocks(cx, cz);
  const lx = x - cx * CHUNK_SIZE, lz = z - cz * CHUNK_SIZE;
  const v = b.get(`${lx},${y},${lz}`);
  return v !== undefined ? v : null;
}

export function queryBlock(x, y, z) {
  const pb = getPlayerBlock(x, y, z);
  if (pb !== null) return pb;
  const cb = getCactusBlock(x, y, z);
  if (cb !== null) return cb;
  const tb = getTreeBlock(x, y, z);
  if (tb !== null) return tb;
  return getBaseBlock(x, y, z);
}