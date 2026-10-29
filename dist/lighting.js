import { CHUNK_SIZE, BLOCK, isWaterBlock } from './config.js';
import { queryBlock } from './worldgen.js';

// -------------------- CONFIG --------------------
export const MAX_LIGHT = 15;
export const TORCH_LIGHT = 14;

const HEIGHT = 128;
const BOTTOM = -32;

function chunkKey(cx, cz){ return `${cx},${cz}`; }

// -------------------- STORAGE --------------------
const lightMaps = new Map();

// -------------------- INDEXING --------------------
function localIndex(lx, ly, lz){
  const y = ly - BOTTOM;
  return (y * CHUNK_SIZE + lz) * CHUNK_SIZE + lx;
}

// -------------------- MAP LIFECYCLE --------------------

export function createLightMap(cx, cz){
  const key = chunkKey(cx, cz);
  let map = lightMaps.get(key);
  if (!map) {
    map = new Uint8Array(CHUNK_SIZE * HEIGHT * CHUNK_SIZE);
    lightMaps.set(key, map);
  }
  return map;
}

export function getLightMap(cx, cz){
  return lightMaps.get(chunkKey(cx, cz)) || null;
}

export function deleteLightMap(cx, cz){
  lightMaps.delete(chunkKey(cx, cz));
}

// -------------------- WORLD-COORD ACCESS --------------------

export function getLight(x, y, z){
  if (y < BOTTOM || y >= BOTTOM + HEIGHT) return 0;

  const cx = Math.floor(x / CHUNK_SIZE);
  const cz = Math.floor(z / CHUNK_SIZE);
  const map = lightMaps.get(chunkKey(cx, cz));
  if (!map) return 0;

  const lx = x - cx * CHUNK_SIZE;
  const lz = z - cz * CHUNK_SIZE;
  return map[localIndex(lx, y, lz)];
}

export function setLight(x, y, z, value){
  if (y < BOTTOM || y >= BOTTOM + HEIGHT) return;

  const cx = Math.floor(x / CHUNK_SIZE);
  const cz = Math.floor(z / CHUNK_SIZE);

  let map = lightMaps.get(chunkKey(cx, cz));
  if (!map) {
    map = new Uint8Array(CHUNK_SIZE * HEIGHT * CHUNK_SIZE);
    lightMaps.set(chunkKey(cx, cz), map);
  }

  const lx = x - cx * CHUNK_SIZE;
  const lz = z - cz * CHUNK_SIZE;
  map[localIndex(lx, y, lz)] = value;
}

// -------------------- TRANSPARENCY CHECK --------------------

function isTransparent(blockId){
  if (blockId === BLOCK.AIR) return true;
  if (isWaterBlock(blockId)) return true;
  if (blockId === BLOCK.LEAVES) return true;
  if (blockId === BLOCK.TORCH) return true;
  if (blockId === BLOCK.BED_FOOT) return true;
  if (blockId === BLOCK.BED_HEAD) return true;
  return false;
}

// -------------------- FLOOD FILL: ADD --------------------

const DIRS = [[1,0,0],[-1,0,0],[0,0,1],[0,0,-1],[0,1,0],[0,-1,0]];

export function addLightAt(x, y, z, level = TORCH_LIGHT){
  const existing = getLight(x, y, z);
  if (existing >= level) return;

  setLight(x, y, z, level);

  const queue = [[x, y, z, level]];
  let head = 0;

  while (head < queue.length) {
    const [cx, cy, cz, current] = queue[head++];
    if (current <= 1) continue;
    const next = current - 1;

    for (const [dx, dy, dz] of DIRS) {
      const nx = cx + dx, ny = cy + dy, nz = cz + dz;
      if (ny < BOTTOM || ny >= BOTTOM + HEIGHT) continue;

      const neighborLight = getLight(nx, ny, nz);
      if (neighborLight >= next) continue;

      const blockId = queryBlock(nx, ny, nz);
      if (!isTransparent(blockId)) continue;

      setLight(nx, ny, nz, next);
      queue.push([nx, ny, nz, next]);
    }
  }
}

// -------------------- FLOOD FILL: REMOVE --------------------

export function removeLightAt(x, y, z){
  const startLevel = getLight(x, y, z);
  if (startLevel === 0) return;

  setLight(x, y, z, 0);

  const removeQueue = [[x, y, z, startLevel]];
  const readdQueue = [];
  let head = 0;

  while (head < removeQueue.length) {
    const [cx, cy, cz, current] = removeQueue[head++];

    for (const [dx, dy, dz] of DIRS) {
      const nx = cx + dx, ny = cy + dy, nz = cz + dz;
      if (ny < BOTTOM || ny >= BOTTOM + HEIGHT) continue;

      const nLight = getLight(nx, ny, nz);
      if (nLight === 0) continue;

      if (nLight < current) {
        setLight(nx, ny, nz, 0);
        removeQueue.push([nx, ny, nz, nLight]);
      } else {
        readdQueue.push([nx, ny, nz, nLight]);
      }
    }
  }

  for (const [rx, ry, rz, rLevel] of readdQueue) {
    addLightAt(rx, ry, rz, rLevel);
  }
}

// -------------------- CHUNK-AFFECTED TRACKING --------------------

// Returns the 5x5 chunk area around a change. That's 80x80 blocks —
// enough to cover any light change (torch reaches 14 blocks).
export function getAffectedChunks(x, y, z){
  const cx = Math.floor(x / CHUNK_SIZE);
  const cz = Math.floor(z / CHUNK_SIZE);
  const result = [];
  for (let dx = -2; dx <= 2; dx++) {
    for (let dz = -2; dz <= 2; dz++) {
      result.push([cx + dx, cz + dz]);
    }
  }
  return result;
}