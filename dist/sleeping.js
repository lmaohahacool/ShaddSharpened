import { player } from './core.js';
import { setWorldTimeGlobal } from './daynight.js';
import { getIsNightTime } from './core.js';

export let isSleeping = false;
let sleepStartTime = 0;

const SLEEP_DURATION = 3000;  // ms before auto-wake
const FADE_IN_MS = 400;

export function trySleep() {
  if (isSleeping) return false;
  if (!getIsNightTime()) return false;

  isSleeping = true;
  sleepStartTime = performance.now();

  // Store original player state
  window._sleepSavedPitch = player.pitch;
  window._sleepSavedY = player.pos.y;
  window._sleepSavedYaw = player.yaw;

  // Lie down: tilt camera up, slightly lower
  player.pos.y -= 0.3;
  player.pitch = -1.2; // look up at the ceiling

  // Show fade + button
  const sleepUI = document.getElementById('sleep-ui');
  if (sleepUI) sleepUI.style.display = 'flex';

  return true;
}

export function wakeUp() {
  if (!isSleeping) return;
  isSleeping = false;

  // Restore player
  player.pitch = window._sleepSavedPitch || 0;
  player.pos.y = window._sleepSavedY || player.pos.y;

  // Hide UI
  const sleepUI = document.getElementById('sleep-ui');
  if (sleepUI) sleepUI.style.display = 'none';

  // Set time to dawn (a bit past sunrise)
  setWorldTimeGlobal(600 * 0.27);

  // Restore health/hunger like Minecraft
  player.health = player.maxHealth;
  player.hunger = Math.max(player.hunger, 10);
}

export function updateSleeping(dt) {
  if (!isSleeping) return;

  // Auto-wake after SLEEP_DURATION
  if (performance.now() - sleepStartTime > SLEEP_DURATION) {
    wakeUp();
  }
}