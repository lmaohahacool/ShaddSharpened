import { BLOCK, CHUNK_SIZE, WORLD_BOTTOM } from './config.js';
import { scene } from './core.js';
import { queryBlock } from './worldgen.js';
import { getModel } from './mobs.js';

let torchTemplate = null;
const torchInstances = [];

function ensureTemplate() {
  if (torchTemplate) return torchTemplate;
  const m = getModel('torch');
  if (!m) return null;
  torchTemplate = m;
  return torchTemplate;
}

export function spawnTorchesForChunk(cx, cz) {
  const tpl = ensureTemplate();
  if (!tpl) return;

  const bx = cx * CHUNK_SIZE;
  const bz = cz * CHUNK_SIZE;

  for (let lx = 0; lx < CHUNK_SIZE; lx++) {
    for (let lz = 0; lz < CHUNK_SIZE; lz++) {
      for (let ly = WORLD_BOTTOM; ly < 64; ly++) {
        const wx = bx + lx;
        const wz = bz + lz;
        const b = queryBlock(wx, ly, wz);
        if (b !== BLOCK.TORCH) continue;

        const instance = tpl.clone(true);
        instance.position.set(wx + 0.5, ly, wz + 0.5);
        scene.add(instance);

        torchInstances.push({ mesh: instance, x: wx, y: ly, z: wz });
      }
    }
  }
}

export function removeTorchesForChunk(cx, cz) {
  const bx = cx * CHUNK_SIZE;
  const bz = cz * CHUNK_SIZE;
  const bx2 = bx + CHUNK_SIZE;
  const bz2 = bz + CHUNK_SIZE;

  for (let i = torchInstances.length - 1; i >= 0; i--) {
    const t = torchInstances[i];
    if (t.x >= bx && t.x < bx2 && t.z >= bz && t.z < bz2) {
      scene.remove(t.mesh);
      torchInstances.splice(i, 1);
    }
  }
}

export function updateTorches(dt) {
  // Placeholder for future animation
}