import {
  BLOCK, CHUNK_SIZE, WORLD_BOTTOM,
  isWaterBlock, getWaterLevel, getWaterBlockFromLevel
} from './config.js';
import { blockChanges, player } from './core.js';
import { queryBlock } from './worldgen.js';
import { queueChunkRebuilds } from './chunks.js';

// -------------------- CONFIG --------------------
const PLAYER_RADIUS = 40;
const MAX_UPDATES_PER_TICK = 500;
const TICK_INTERVAL = 0.1;

// -------------------- STATE --------------------
const updateQueue = new Set();
let tickTimer = 0;

// -------------------- HELPERS --------------------

function queueUpdate(x, y, z){
  updateQueue.add(`${x},${y},${z}`);
}

function getBlock(x, y, z){
  return queryBlock(x, y, z);
}

function setBlock(x, y, z, id){
  const key = `${x},${y},${z}`;
  const current = blockChanges.get(key);
  if (current === id) return false;
  blockChanges.set(key, id);
  return true;
}

function setAndRebuild(x, y, z, id){
  if (setBlock(x, y, z, id)) {
    queueChunkRebuilds(x, y, z);
    return true;
  }
  return false;
}

function isWater(id){ return isWaterBlock(id); }
function isSource(id){ return id === BLOCK.WATER; }
function levelOf(id){ return getWaterLevel(id); }

// -------------------- PUBLIC API --------------------

// Queue a small cube around a changed position, so neighboring flows
// (including diagonal ones and flows one level up) get re-evaluated.
export function queueWaterUpdate(x, y, z){
  for (let dx = -2; dx <= 2; dx++) {
    for (let dy = -2; dy <= 3; dy++) {
      for (let dz = -2; dz <= 2; dz++) {
        queueUpdate(x + dx, y + dy, z + dz);
      }
    }
  }
}

export function updateWater(dt){
  tickTimer += dt;
  if (tickTimer < TICK_INTERVAL) return;
  tickTimer = 0;
  runTick();
}

// -------------------- TICK --------------------

function runTick(){
  if (updateQueue.size === 0) return;

  const px = Math.floor(player.pos.x);
  const pz = Math.floor(player.pos.z);

  const positions = [...updateQueue];
  updateQueue.clear();

  positions.sort((a, b) => {
    const [ax, , az] = a.split(',').map(Number);
    const [bx, , bz] = b.split(',').map(Number);
    const da = (ax - px) ** 2 + (az - pz) ** 2;
    const db = (bx - px) ** 2 + (bz - pz) ** 2;
    return da - db;
  });

  let processed = 0;
  for (const key of positions) {
    if (processed >= MAX_UPDATES_PER_TICK) break;
    const [x, y, z] = key.split(',').map(Number);
    const dx = x - px, dz = z - pz;
    if (dx * dx + dz * dz > PLAYER_RADIUS * PLAYER_RADIUS) continue;
    evaluate(x, y, z);
    processed++;
  }
}

// -------------------- EVALUATION --------------------

function evaluate(x, y, z){
  const block = getBlock(x, y, z);
  if (!isWater(block)) return;

  if (block === BLOCK.WATER_FALLING) {
    evaluateFalling(x, y, z);
    return;
  }

  if (block === BLOCK.WATER) {
    evaluateSource(x, y, z);
    return;
  }

  const level = levelOf(block);
  if (level >= 1 && level <= 6) {
    evaluateFlow(x, y, z, level);
  }
}

// -------- SOURCE --------

function evaluateSource(x, y, z){
  const below = getBlock(x, y - 1, z);

  if (below === BLOCK.AIR) {
    setAndRebuild(x, y - 1, z, BLOCK.WATER_FALLING);
    queueUpdate(x, y - 1, z);
    return;
  }

  const dirs = [[1, 0], [-1, 0], [0, 1], [0, -1]];
  for (const [dx, dz] of dirs) {
    const nx = x + dx, nz = z + dz;
    const neighbor = getBlock(nx, y, nz);

    if (neighbor === BLOCK.AIR) {
      setAndRebuild(nx, y, nz, BLOCK.WATER_FLOW_6);
      queueUpdate(nx, y, nz);
    } else if (isWater(neighbor)) {
      const nLevel = levelOf(neighbor);
      if (neighbor !== BLOCK.WATER_FALLING && nLevel > 0 && nLevel < 6) {
        setAndRebuild(nx, y, nz, BLOCK.WATER_FLOW_6);
        queueUpdate(nx, y, nz);
      }
    }
  }

  checkInfiniteSourceAt(x, y, z);
}

// -------- FLOW --------

function evaluateFlow(x, y, z, level){
  const block = getBlock(x, y, z);
  if (!isWater(block)) return;

  const below = getBlock(x, y - 1, z);
  if (below === BLOCK.AIR) {
    setAndRebuild(x, y - 1, z, BLOCK.WATER_FALLING);
    queueUpdate(x, y - 1, z);
    return;
  }

  const nextLevel = level - 1;
  if (nextLevel < 1) return;

  const nextBlock = getWaterBlockFromLevel(nextLevel);
  const dirs = [[1, 0], [-1, 0], [0, 1], [0, -1]];
  for (const [dx, dz] of dirs) {
    const nx = x + dx, nz = z + dz;
    const neighbor = getBlock(nx, y, nz);

    if (neighbor === BLOCK.AIR) {
      setAndRebuild(nx, y, nz, nextBlock);
      queueUpdate(nx, y, nz);
    } else if (isWater(neighbor)) {
      const nLevel = levelOf(neighbor);
      if (neighbor !== BLOCK.WATER_FALLING && nLevel > 0 && nLevel < nextLevel) {
        setAndRebuild(nx, y, nz, nextBlock);
        queueUpdate(nx, y, nz);
      }
    }
  }

  checkInfiniteSourceAt(x, y, z);
}

// -------- FALLING --------

function evaluateFalling(x, y, z){
  const block = getBlock(x, y, z);
  if (block !== BLOCK.WATER_FALLING) return;

  const below = getBlock(x, y - 1, z);

  if (below === BLOCK.AIR) {
    setAndRebuild(x, y - 1, z, BLOCK.WATER_FALLING);
    queueUpdate(x, y - 1, z);
    return;
  }

  spreadFromFalling(x, y, z);
}

function spreadFromFalling(x, y, z){
  const dirs = [[1, 0], [-1, 0], [0, 1], [0, -1]];
  for (const [dx, dz] of dirs) {
    const nx = x + dx, nz = z + dz;
    const neighbor = getBlock(nx, y, nz);

    if (neighbor === BLOCK.AIR) {
      setAndRebuild(nx, y, nz, BLOCK.WATER_FLOW_6);
      queueUpdate(nx, y, nz);
    } else if (isWater(neighbor) && neighbor !== BLOCK.WATER_FALLING) {
      const nLevel = levelOf(neighbor);
      if (nLevel > 0 && nLevel < 6) {
        setAndRebuild(nx, y, nz, BLOCK.WATER_FLOW_6);
        queueUpdate(nx, y, nz);
      }
    }
  }
}

// -------------------- INFINITE WATER --------------------

// Only triggers when two sources are directly OPPOSITE each other.
// Two sources on adjacent sides (or on the same side) don't count.
function checkInfiniteSourceAt(x, y, z){
  const me = getBlock(x, y, z);
  if (!isWater(me)) return;
  if (isSource(me)) return;
  if (me === BLOCK.WATER_FALLING) return;

  const nE = isSource(getBlock(x + 1, y, z));
  const nW = isSource(getBlock(x - 1, y, z));
  const nN = isSource(getBlock(x, y, z + 1));
  const nS = isSource(getBlock(x, y, z - 1));

  const opposite = (nE && nW) || (nN && nS);

  if (opposite) {
    setAndRebuild(x, y, z, BLOCK.WATER);
    queueUpdate(x + 1, y, z);
    queueUpdate(x - 1, y, z);
    queueUpdate(x, y, z + 1);
    queueUpdate(x, y, z - 1);
    queueUpdate(x, y - 1, z);
  }
}

// -------------------- SEED --------------------

export function seedChunkWater(cx, cz){
  const bx = cx * CHUNK_SIZE;
  const bz = cz * CHUNK_SIZE;
  for (let lx = 0; lx < CHUNK_SIZE; lx++) {
    for (let lz = 0; lz < CHUNK_SIZE; lz++) {
      const wx = bx + lx;
      const wz = bz + lz;
      for (let ly = -5; ly < 15; ly++) {
        const b = queryBlock(wx, ly, wz);
        if (isWater(b)) queueUpdate(wx, ly, wz);
      }
    }
  }
}