import {
  TOTAL_SLOTS, HOTBAR_SIZE, BLOCK, SMELT_RECIPES, FUEL_VALUES,
  INVENTORY_MAX_STACK
} from './config.js';
import {
  inventory as inv, selectedIndex, blockChanges
} from './core.js';
import { getIconHTML } from './textures.js';
import { clickSlot, addToInventory } from './inventory.js';
import { makeSlotEl } from './crafting.js';
import { getPlayerBlock } from './worldgen.js';
import { queueChunkRebuilds } from './chunks.js';

export const furnaceStates = new Map();
export let furnaceOpen = false;
export let openFurnaceKey = null;

export function setFurnaceOpen(v) { furnaceOpen = v; }
export function isFurnaceOpen() { return furnaceOpen; }
export function getOpenFurnaceKey() { return openFurnaceKey; }

export function getFurnaceState(key) {
  if (!furnaceStates.has(key)) {
    furnaceStates.set(key, {
      input: { id: null, count: 0 },
      fuel: { id: null, count: 0 },
      output: { id: null, count: 0 },
      fuelTime: 0,
      progress: 0
    });
  }
  return furnaceStates.get(key);
}

export function openFurnaceUI(x, y, z) {
  furnaceOpen = true;
  openFurnaceKey = `${x},${y},${z}`;
  getFurnaceState(openFurnaceKey);
  document.getElementById('furnace-ui').style.display = 'flex';
  renderFurnace();
  if (document.pointerLockElement) document.exitPointerLock();
}

export function closeFurnaceUI() {
  if (!furnaceOpen) return;
  if (openFurnaceKey) {
    const st = getFurnaceState(openFurnaceKey);
    if (st.input.id !== null && st.input.count > 0) {
      addToInventory(st.input.id, st.input.count, st.input.durability);
      st.input = { id: null, count: 0 };
    }
    if (st.fuel.id !== null && st.fuel.count > 0) {
      addToInventory(st.fuel.id, st.fuel.count, st.fuel.durability);
      st.fuel = { id: null, count: 0 };
    }
    if (st.output.id !== null && st.output.count > 0) {
      addToInventory(st.output.id, st.output.count, st.output.durability);
      st.output = { id: null, count: 0 };
    }
  }
  furnaceOpen = false;
  openFurnaceKey = null;
  document.getElementById('furnace-ui').style.display = 'none';
  document.getElementById('click-hint').style.display = 'none';
  document.getElementById('crosshair').style.display = 'block';
  if (!document.pointerLockElement) document.querySelector('canvas').requestPointerLock().catch(() => {});
}

export function renderFurnace() {
  if (!openFurnaceKey) return;
  const st = getFurnaceState(openFurnaceKey);

  const mkSlot = (slotName, el) => {
    el.innerHTML = '';
    const item = st[slotName];
    if (item && item.id !== null && item.count > 0) {
      el.innerHTML = `${getIconHTML(item.id)}<div class="slot-count">${item.count}</div>`;
    }
    el.onmousedown = e => {
      e.preventDefault();
      e.stopPropagation();
      clickSlot(() => st[slotName], v => st[slotName] = v, e.button === 2, e.shiftKey);
      renderFurnace();
    };
    el.oncontextmenu = e => e.preventDefault();
  };

  mkSlot('input', document.getElementById('furnace-input-slot'));
  mkSlot('fuel', document.getElementById('furnace-fuel-slot'));
  mkSlot('output', document.getElementById('furnace-output-slot'));

  const inputOK = st.input.id !== null && SMELT_RECIPES[st.input.id] !== undefined;
  const totalTime = inputOK ? SMELT_RECIPES[st.input.id].time : 10;
  document.getElementById('furnace-progress-fill').style.width = (st.progress / totalTime * 100) + '%';
  document.getElementById('furnace-fuel-fill').style.height = Math.min(100, (st.fuelTime / 20) * 100) + '%';

  const flamesEl = document.getElementById('furnace-flames');
  if (st.fuelTime > 0) flamesEl.classList.add('active');
  else flamesEl.classList.remove('active');

  const ig = document.getElementById('furnace-inv-grid');
  const ihg = document.getElementById('furnace-hotbar-grid');
  ig.innerHTML = '';
  ihg.innerHTML = '';
  for (let i = HOTBAR_SIZE; i < TOTAL_SLOTS; i++) ig.appendChild(makeSlotEl(inv, i, false));
  for (let i = 0; i < HOTBAR_SIZE; i++) ihg.appendChild(makeSlotEl(inv, i, i === selectedIndex));
}

export function updateFurnaces(dt) {
  if (furnaceStates.size === 0) return;

  for (const [key, st] of furnaceStates) {
    const inputOK = st.input.id !== null && SMELT_RECIPES[st.input.id] !== undefined;
    let active = false;

    if (inputOK) {
      if (st.fuelTime <= 0) {
        if (st.fuel.id !== null && FUEL_VALUES[st.fuel.id] !== undefined) {
          const fuelPts = FUEL_VALUES[st.fuel.id];
          st.fuelTime += fuelPts / 20;
          st.fuel.count--;
          if (st.fuel.count <= 0) st.fuel = { id: null, count: 0 };
        }
      }
      if (st.fuelTime > 0) {
        const recipe = SMELT_RECIPES[st.input.id];
        const canOutput = (st.output.id === null || st.output.id === recipe.output) && st.output.count < INVENTORY_MAX_STACK;
        if (canOutput) {
          active = true;
          st.fuelTime -= dt;
          st.progress += dt;
          if (st.progress >= recipe.time) {
            st.output = { id: recipe.output, count: st.output.count + 1 };
            st.input.count--;
            if (st.input.count <= 0) st.input = { id: null, count: 0 };
            st.progress = 0;
          }
        }
      }
    }

    const [fx, fy, fz] = key.split(',').map(Number);
    const curBlock = getPlayerBlock(fx, fy, fz);
    if (active && curBlock === BLOCK.FURNACE) {
      blockChanges.set(key, BLOCK.WORKING_FURNACE);
      queueChunkRebuilds(fx, fy, fz);
    } else if (!active && curBlock === BLOCK.WORKING_FURNACE) {
      blockChanges.set(key, BLOCK.FURNACE);
      queueChunkRebuilds(fx, fy, fz);
    }
  }

  if (furnaceOpen) renderFurnace();
}