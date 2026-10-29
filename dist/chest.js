import { HOTBAR_SIZE } from './config.js';
import { inventory as inv, selectedIndex } from './core.js';
import { getIconHTML, getDurabilityHTML } from './textures.js';
import { clickSlot } from './inventory.js';
import { makeSlotEl } from './crafting.js';

export const chestStates = new Map();
export let chestOpen = false;
export let openChestKey = null;

export function setChestOpen(v) { chestOpen = v; }
export function isChestOpen() { return chestOpen; }
export function getOpenChestKey() { return openChestKey; }

export function getChestState(key) {
  if (!chestStates.has(key)) {
    chestStates.set(key, new Array(27).fill(null).map(() => ({ id: null, count: 0 })));
  }
  return chestStates.get(key);
}

export function openChestUI(x, y, z) {
  chestOpen = true;
  openChestKey = `${x},${y},${z}`;
  getChestState(openChestKey);
  document.getElementById('chest-ui').style.display = 'flex';
  renderChest();
  if (document.pointerLockElement) document.exitPointerLock();
}

export function closeChestUI() {
  if (!chestOpen) return;
  chestOpen = false;
  openChestKey = null;
  document.getElementById('chest-ui').style.display = 'none';
  document.getElementById('click-hint').style.display = 'none';
  document.getElementById('crosshair').style.display = 'block';
  if (!document.pointerLockElement) document.querySelector('canvas').requestPointerLock().catch(() => {});
}

export function makeChestSlot(slotArray, idx) {
  const s = document.createElement('div');
  s.className = 'inv-slot';
  const item = slotArray[idx];
  if (item && item.id !== null && item.count > 0) {
    s.innerHTML = `${getIconHTML(item.id)}${getDurabilityHTML(item)}<div class="slot-count">${item.count}</div>`;
  }
  s.addEventListener('mousedown', e => {
    e.preventDefault();
    e.stopPropagation();
    clickSlot(() => slotArray[idx], v => slotArray[idx] = v, e.button === 2, e.shiftKey);
    renderChest();
  });
  s.addEventListener('contextmenu', e => e.preventDefault());
  return s;
}

export function renderChest() {
  if (!openChestKey) return;
  const st = getChestState(openChestKey);
  const ig = document.getElementById('chest-inv-grid');
  const ihg = document.getElementById('chest-hotbar-grid');
  ig.innerHTML = '';
  ihg.innerHTML = '';
  for (let i = 0; i < 27; i++) ig.appendChild(makeChestSlot(st, i));
  for (let i = 0; i < HOTBAR_SIZE; i++) ihg.appendChild(makeSlotEl(inv, i, i === selectedIndex));
}