import { SEED, setSeed, player, gamemode, selectedIndex, setSelectedIndex, setGamemodeState, inventory, blockChanges, blockFacings, mobs } from './core.js';
import { getWorldTime, setWorldTimeGlobal } from './daynight.js';
import { chestStates } from './chest.js';
import { furnaceStates } from './furnace.js';
import { droppedItems } from './items.js';

// -------------------- BINARY HELPERS --------------------

class ByteWriter {
  constructor() { this.parts = []; this.total = 0; }
  u8(v)  { const b = new Uint8Array(1); b[0] = v & 0xff; this.parts.push(b); this.total += 1; }
  u16(v) { const b = new Uint8Array(2); new DataView(b.buffer).setUint16(0, v & 0xffff, true); this.parts.push(b); this.total += 2; }
  u32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, v >>> 0, true); this.parts.push(b); this.total += 4; }
  i32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setInt32(0, v | 0, true); this.parts.push(b); this.total += 4; }
  f32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setFloat32(0, v, true); this.parts.push(b); this.total += 4; }
  toBytes() {
    const out = new Uint8Array(this.total);
    let o = 0;
    for (const p of this.parts) { out.set(p, o); o += p.length; }
    return out;
  }
}

class ByteReader {
  constructor(bytes) {
    this.bytes = bytes;
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    this.off = 0;
  }
  u8()  { const v = this.view.getUint8(this.off); this.off += 1; return v; }
  u16() { const v = this.view.getUint16(this.off, true); this.off += 2; return v; }
  u32() { const v = this.view.getUint32(this.off, true); this.off += 4; return v; }
  i32() { const v = this.view.getInt32(this.off, true); this.off += 4; return v; }
  f32() { const v = this.view.getFloat32(this.off, true); this.off += 4; return v; }
}

// -------------------- ITEM ID MAPPING --------------------

const ITEM_STRING_TO_ID = {
  'item_stick': 1000,
  'item_wp': 1001,
  'item_wa': 1002,
  'item_sp': 1003,
  'item_sa': 1004,
  'item_beef': 1005,
  'item_steak': 1006,
  'item_coal': 1007,
  'item_raw_iron': 1008,
  'item_iron_ingot': 1009,
  'item_rotten_flesh': 1010,
};
const ITEM_ID_TO_STRING = {};
for (const [k, v] of Object.entries(ITEM_STRING_TO_ID)) ITEM_ID_TO_STRING[v] = k;

export function itemIdToNumber(id) {
  if (id === null || id === undefined) return 0;
  if (typeof id === 'number') return id;
  return ITEM_STRING_TO_ID[id] || 0;
}
export function numberToItemId(n) {
  if (n === 0) return null;
  if (n < 1000) return n;
  return ITEM_ID_TO_STRING[n] || null;
}

// -------------------- BASE64 --------------------

function bytesToBase64(bytes) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}
function base64ToBytes(b64) {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

// -------------------- SAVE META --------------------

const META_KEY = 'shaft_meta_v1';
const SAVE_VERSION = 6;

export function getSavesMeta() {
  const raw = localStorage.getItem(META_KEY);
  if (!raw) return { saves: [] };
  try { return JSON.parse(raw); } catch (e) { return { saves: [] }; }
}
export function setSavesMeta(meta) {
  localStorage.setItem(META_KEY, JSON.stringify(meta));
}
export function nextFreeSlot() {
  const meta = getSavesMeta();
  const used = new Set(meta.saves.map(s => s.slot));
  for (let i = 0; i < 100; i++) if (!used.has(i)) return i;
  return -1;
}
export function updateMetaForSlot(slot, patch) {
  const meta = getSavesMeta();
  const entry = meta.saves.find(s => s.slot === slot);
  if (entry) Object.assign(entry, patch);
  else meta.saves.push({ slot, ...patch });
  setSavesMeta(meta);
}

function saveKey(slot) { return `shaft_save_v6_slot_${slot}`; }

// -------------------- MOB FILTERING --------------------

// We only persist tamed cows (persistent === true) and all zombies.
// Untamed cows wander off, die, and respawn naturally — no point saving them.
function getMobsToSave() {
  return mobs.filter(m => {
    if (m.type === 'zombie') return true;
    if (m.type === 'cow') return m.persistent === true;
    return false;
  });
}

// -------------------- SAVE GAME --------------------

export function saveGame(slot) {
  const w = new ByteWriter();

  // Header
  w.u16(SAVE_VERSION);
  w.u32(SEED >>> 0);
  w.f32(getWorldTime());
  w.u8(gamemode === 'creative' ? 0 : 1);

  // Player state
  w.f32(player.pos.x);
  w.f32(player.pos.y);
  w.f32(player.pos.z);
  w.f32(player.yaw);
  w.f32(player.pitch);
  w.f32(player.health);
  w.f32(player.hunger);
  w.u8(player.flying ? 1 : 0);
  w.u8(selectedIndex);
  w.u8(0);

  // --- Inventory ---
  w.u32(36);
  for (const slot of inventory) {
    if (!slot || slot.id === null || slot.id === undefined || slot.count <= 0) {
      w.u16(0); w.u8(0); w.u16(0);
    } else {
      w.u16(itemIdToNumber(slot.id));
      w.u8(Math.min(255, slot.count));
      w.u16(slot.durability || 0);
    }
  }

  // --- Block Changes ---
  w.u32(blockChanges.size);
  for (const [key, blockId] of blockChanges) {
    const [x, y, z] = key.split(',').map(Number);
    w.i32(x); w.i32(y); w.i32(z);
    w.u16(typeof blockId === 'number' ? blockId : itemIdToNumber(blockId));
  }

  // --- Block Facings ---
  w.u32(blockFacings.size);
  for (const [key, facing] of blockFacings) {
    const [x, y, z] = key.split(',').map(Number);
    w.i32(x); w.i32(y); w.i32(z);
    w.u8(facing);
  }

  // --- Chest States ---
  w.u32(chestStates.size);
  for (const [key, slots] of chestStates) {
    const [x, y, z] = key.split(',').map(Number);
    w.i32(x); w.i32(y); w.i32(z);
    w.u8(27);
    for (const slot of slots) {
      if (!slot || slot.id === null || slot.id === undefined || slot.count <= 0) {
        w.u16(0); w.u8(0); w.u16(0);
      } else {
        w.u16(itemIdToNumber(slot.id));
        w.u8(Math.min(255, slot.count));
        w.u16(slot.durability || 0);
      }
    }
  }

  // --- Furnace States ---
  w.u32(furnaceStates.size);
  for (const [key, st] of furnaceStates) {
    const [x, y, z] = key.split(',').map(Number);
    w.i32(x); w.i32(y); w.i32(z);
    w.u16(st.input && st.input.id ? itemIdToNumber(st.input.id) : 0);
    w.u8(st.input && st.input.count ? Math.min(255, st.input.count) : 0);
    w.u16(st.input && st.input.durability ? st.input.durability : 0);
    w.u16(st.fuel && st.fuel.id ? itemIdToNumber(st.fuel.id) : 0);
    w.u8(st.fuel && st.fuel.count ? Math.min(255, st.fuel.count) : 0);
    w.u16(st.fuel && st.fuel.durability ? st.fuel.durability : 0);
    w.u16(st.output && st.output.id ? itemIdToNumber(st.output.id) : 0);
    w.u8(st.output && st.output.count ? Math.min(255, st.output.count) : 0);
    w.u16(st.output && st.output.durability ? st.output.durability : 0);
    w.f32(st.fuelTime || 0);
    w.f32(st.progress || 0);
  }

  // --- Dropped Items ---
  w.u32(droppedItems.length);
  for (const d of droppedItems) {
    const p = d.mesh.position;
    w.f32(p.x); w.f32(p.y); w.f32(p.z);
    w.f32(d.vx || 0); w.f32(d.vy || 0); w.f32(d.vz || 0);
    w.u16(itemIdToNumber(d.id));
    w.u8(Math.min(255, d.count || 1));
    w.u16(d.durability || 0);
    w.f32(d.age || 0);
    w.u8(d.onGround ? 1 : 0);
    w.u8(d.isItem ? 1 : 0);
  }

  // --- Mobs ---
  // Only tamed cows + all zombies. See getMobsToSave() above.
  const mobsToSave = getMobsToSave();
  w.u32(mobsToSave.length);
  for (const m of mobsToSave) {
    const p = m.group.position;
    if (m.type === 'cow') {
      w.u8(0);
      w.f32(p.x); w.f32(p.y); w.f32(p.z);
      w.f32(m.vel.x); w.f32(m.vel.y); w.f32(m.vel.z);
      w.u8(Math.min(255, Math.max(0, m.health)));
      w.u8(m.onGround ? 1 : 0);
      w.f32(m.walkAnim);
      w.f32(m.idleTimer);
      // homePos is required because we only save persistent cows
      w.f32(m.homePos ? m.homePos.x : p.x);
      w.f32(m.homePos ? m.homePos.y : p.y);
      w.f32(m.homePos ? m.homePos.z : p.z);
    } else if (m.type === 'zombie') {
      w.u8(1);
      w.f32(p.x); w.f32(p.y); w.f32(p.z);
      w.f32(m.vel.x); w.f32(m.vel.y); w.f32(m.vel.z);
      w.u8(Math.min(255, Math.max(0, m.health)));
      w.u8(m.onGround ? 1 : 0);
      w.f32(m.walkAnim);
      w.u8(m.burning ? 1 : 0);
      w.f32(m.burnTimer || 0);
      w.f32(m.burnCheckTimer || 0);
    }
  }

  const bytes = w.toBytes();
  localStorage.setItem(saveKey(slot), bytesToBase64(bytes));

  const meta = getSavesMeta();
  const existing = meta.saves.find(s => s.slot === slot);
  const elapsed = existing && existing.lastPlayed
    ? Math.max(0, Math.floor((Date.now() - existing.lastPlayed) / 1000))
    : 0;
  updateMetaForSlot(slot, {
    seed: SEED,
    lastPlayed: Date.now(),
    playtime: (existing?.playtime || 0) + elapsed,
  });

  return true;
}

// -------------------- LOAD GAME --------------------

export function loadGame(slot) {
  const b64 = localStorage.getItem(saveKey(slot));
  if (!b64) return null;

  let bytes;
  try { bytes = base64ToBytes(b64); } catch (e) { return null; }

  const r = new ByteReader(bytes);

  const version = r.u16();
  if (version !== SAVE_VERSION) {
    console.error('Save version mismatch:', version, 'expected', SAVE_VERSION);
    return null;
  }

  const save = {};
  save.seed = r.u32();
  save.worldTime = r.f32();
  save.gamemode = r.u8() === 0 ? 'creative' : 'survival';
  save.playerX = r.f32();
  save.playerY = r.f32();
  save.playerZ = r.f32();
  save.playerYaw = r.f32();
  save.playerPitch = r.f32();
  save.playerHealth = r.f32();
  save.playerHunger = r.f32();
  save.playerFlying = r.u8() === 1;
  save.selectedIndex = r.u8();
  r.u8();

  // --- Inventory ---
  save.inventory = [];
  const slotCount = r.u32();
  for (let i = 0; i < slotCount; i++) {
    const idNum = r.u16();
    const count = r.u8();
    const dur = r.u16();
    if (idNum === 0 || count === 0) {
      save.inventory.push({ id: null, count: 0 });
    } else {
      save.inventory.push({
        id: numberToItemId(idNum),
        count,
        durability: (dur === 0) ? undefined : dur,
      });
    }
  }

  // --- Block Changes ---
  save.blockChanges = [];
  const bcCount = r.u32();
  for (let i = 0; i < bcCount; i++) {
    const x = r.i32();
    const y = r.i32();
    const z = r.i32();
    const idNum = r.u16();
    save.blockChanges.push({ x, y, z, id: idNum });
  }

  // --- Block Facings ---
  save.blockFacings = [];
  const bfCount = r.u32();
  for (let i = 0; i < bfCount; i++) {
    const x = r.i32();
    const y = r.i32();
    const z = r.i32();
    const facing = r.u8();
    save.blockFacings.push({ x, y, z, facing });
  }

  // --- Chest States ---
  save.chestStates = [];
  const csCount = r.u32();
  for (let i = 0; i < csCount; i++) {
    const x = r.i32();
    const y = r.i32();
    const z = r.i32();
    const slots = [];
    const n = r.u8();
    for (let j = 0; j < n; j++) {
      const idNum = r.u16();
      const count = r.u8();
      const dur = r.u16();
      if (idNum === 0 || count === 0) {
        slots.push({ id: null, count: 0 });
      } else {
        slots.push({
          id: numberToItemId(idNum),
          count,
          durability: (dur === 0) ? undefined : dur,
        });
      }
    }
    save.chestStates.push({ x, y, z, slots });
  }

  // --- Furnace States ---
  save.furnaceStates = [];
  const fsCount = r.u32();
  for (let i = 0; i < fsCount; i++) {
    const x = r.i32();
    const y = r.i32();
    const z = r.i32();
    const inputId = r.u16(); const inputCount = r.u8(); const inputDur = r.u16();
    const fuelId  = r.u16(); const fuelCount  = r.u8(); const fuelDur  = r.u16();
    const outId   = r.u16(); const outCount   = r.u8(); const outDur   = r.u16();
    const fuelTime = r.f32();
    const progress = r.f32();

    save.furnaceStates.push({
      x, y, z,
      input: inputId === 0 || inputCount === 0
        ? { id: null, count: 0 }
        : { id: numberToItemId(inputId), count: inputCount, durability: inputDur || undefined },
      fuel: fuelId === 0 || fuelCount === 0
        ? { id: null, count: 0 }
        : { id: numberToItemId(fuelId), count: fuelCount, durability: fuelDur || undefined },
      output: outId === 0 || outCount === 0
        ? { id: null, count: 0 }
        : { id: numberToItemId(outId), count: outCount, durability: outDur || undefined },
      fuelTime, progress
    });
  }

  // --- Dropped Items ---
  save.droppedItems = [];
  const diCount = r.u32();
  for (let i = 0; i < diCount; i++) {
    const x = r.f32(); const y = r.f32(); const z = r.f32();
    const vx = r.f32(); const vy = r.f32(); const vz = r.f32();
    const idNum = r.u16();
    const count = r.u8();
    const durability = r.u16();
    const age = r.f32();
    const onGround = r.u8() === 1;
    const isItemFlag = r.u8() === 1;
    save.droppedItems.push({
      x, y, z, vx, vy, vz,
      id: numberToItemId(idNum),
      count,
      durability: durability === 0 ? undefined : durability,
      age,
      onGround,
      isItem: isItemFlag,
    });
  }

  // --- Mobs ---
  save.mobs = [];
  const mobCount = r.u32();
  for (let i = 0; i < mobCount; i++) {
    const typeByte = r.u8();
    const mob = {
      type: typeByte === 1 ? 'zombie' : 'cow',
      x: r.f32(), y: r.f32(), z: r.f32(),
      vx: r.f32(), vy: r.f32(), vz: r.f32(),
      health: r.u8(),
      onGround: r.u8() === 1,
      walkAnim: r.f32(),
    };
    if (mob.type === 'cow') {
      mob.idleTimer = r.f32();
      mob.homeX = r.f32();
      mob.homeY = r.f32();
      mob.homeZ = r.f32();
    } else {
      mob.burning = r.u8() === 1;
      mob.burnTimer = r.f32();
      mob.burnCheckTimer = r.f32();
    }
    save.mobs.push(mob);
  }

  return save;
}

export function deleteSave(slot) {
  localStorage.removeItem(saveKey(slot));
  localStorage.removeItem(`shaft_save_v5_slot_${slot}`);
  localStorage.removeItem(`shaft_save_v4_slot_${slot}`);
  localStorage.removeItem(`shaft_save_v3_slot_${slot}`);
  localStorage.removeItem(`shaft_save_v2_slot_${slot}`);
  localStorage.removeItem(`shaft_save_v1_slot_${slot}`);
  const meta = getSavesMeta();
  meta.saves = meta.saves.filter(s => s.slot !== slot);
  setSavesMeta(meta);
}