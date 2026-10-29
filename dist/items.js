import * as THREE from 'three';
import { BLOCK, isItem, getInfo } from './config.js';
import { scene, player, camera } from './core.js';
import { itemTextures } from './textures.js';
import { addToInventory } from './inventory.js';
import { queryBlock } from './worldgen.js';

const PICKUP_RADIUS = 1.5;
const DESPAWN_TIME = 300;
const GRAVITY = 25;
const ITEM_SIZE_BLOCK = 0.25;
const ITEM_SIZE_ITEM = 0.4;

export const droppedItems = [];

let sharedCubeGeo = null;
let sharedPlaneGeo = null;

function getBlockGeometry() {
  if (!sharedCubeGeo) {
    sharedCubeGeo = new THREE.BoxGeometry(ITEM_SIZE_BLOCK, ITEM_SIZE_BLOCK, ITEM_SIZE_BLOCK);
  }
  return sharedCubeGeo;
}

function getItemGeometry() {
  if (!sharedPlaneGeo) {
    sharedPlaneGeo = new THREE.PlaneGeometry(ITEM_SIZE_ITEM, ITEM_SIZE_ITEM);
  }
  return sharedPlaneGeo;
}

// Remove every dropped item and dispose its mesh. Used on world load
// before restoring saved items, so we never stack two worlds together.
export function clearDroppedItems() {
  for (const d of droppedItems) {
    scene.remove(d.mesh);
    if (d.mesh.material.map) d.mesh.material.map.dispose();
    d.mesh.material.dispose();
  }
  droppedItems.length = 0;
}

export function spawnDroppedItem(x, y, z, itemId, count = 1, durability, opts) {
  const isBlock = !isItem(itemId);
  let mesh;

  if (isBlock) {
    const info = getInfo(itemId);
    const color = new THREE.Color(info && info.color ? info.color : '#888888');
    const mat = new THREE.MeshLambertMaterial({ color });
    mesh = new THREE.Mesh(getBlockGeometry(), mat);
  } else {
    const url = itemTextures[itemId];
    let mat;
    if (url) {
      const tex = new THREE.TextureLoader().load(url);
      tex.magFilter = THREE.NearestFilter;
      tex.minFilter = THREE.NearestFilter;
      mat = new THREE.MeshBasicMaterial({ map: tex, transparent: true, side: THREE.DoubleSide });
    } else {
      const info = getInfo(itemId);
      const color = (info && info.color) || '#888888';
      mat = new THREE.MeshBasicMaterial({ color, side: THREE.DoubleSide });
    }
    mesh = new THREE.Mesh(getItemGeometry(), mat);
  }

  // Default placement: centered in the block, slightly above its base.
  // Restored items override this with exactPosition below.
  mesh.position.set(x + 0.5, y + 0.5, z + 0.5);
  scene.add(mesh);

  const item = {
    mesh,
    id: itemId,
    count,
    durability,
    vx: (Math.random() - 0.5) * 2,
    vy: 2.5,
    vz: (Math.random() - 0.5) * 2,
    age: 0,
    onGround: false,
    spinAngle: Math.random() * Math.PI * 2,
    isItem: !isBlock,
    bobOffset: 0
  };

  // Allow callers (save loader) to override physics/state.
  if (opts) {
    if (opts.vx !== undefined) item.vx = opts.vx;
    if (opts.vy !== undefined) item.vy = opts.vy;
    if (opts.vz !== undefined) item.vz = opts.vz;
    if (opts.age !== undefined) item.age = opts.age;
    if (opts.onGround !== undefined) item.onGround = opts.onGround;
    if (opts.spinAngle !== undefined) item.spinAngle = opts.spinAngle;
    // Restored items must land exactly where they were saved, not at
    // the block-center spawn offset — otherwise they shift every reload.
    if (opts.exactPosition) {
      mesh.position.set(x, y, z);
    }
  }

  droppedItems.push(item);
  return item;
}

export function updateDroppedItems(dt) {
  for (let i = droppedItems.length - 1; i >= 0; i--) {
    const d = droppedItems[i];
    d.age += dt;

    if (d.age > DESPAWN_TIME) {
      scene.remove(d.mesh);
      if (d.mesh.material.map) d.mesh.material.map.dispose();
      d.mesh.material.dispose();
      droppedItems.splice(i, 1);
      continue;
    }

    if (!d.onGround) {
      d.vy -= GRAVITY * dt;
      d.mesh.position.x += d.vx * dt;
      d.mesh.position.y += d.vy * dt;
      d.mesh.position.z += d.vz * dt;

      const bx = Math.floor(d.mesh.position.x);
      const bz = Math.floor(d.mesh.position.z);
      const by = Math.floor(d.mesh.position.y - 0.5);

      const blockBelow = queryBlock(bx, by, bz);
      if (blockBelow !== BLOCK.AIR && d.mesh.position.y <= by + 1.5) {
        d.mesh.position.y = by + 1.5;
        d.vy = 0;
        d.vx = 0;
        d.vz = 0;
        d.onGround = true;
      }
    }

    d.spinAngle += dt * 1.5;

    if (d.onGround) {
      const targetBob = Math.sin(d.age * 3) * 0.1;
      d.mesh.position.y += (targetBob - d.bobOffset);
      d.bobOffset = targetBob;
    }

    if (d.isItem) {
      d.mesh.lookAt(camera.position);
    } else {
      d.mesh.rotation.y = d.spinAngle;
    }

    const dx = player.pos.x - d.mesh.position.x;
    const dy = (player.pos.y + 0.9) - d.mesh.position.y;
    const dz = player.pos.z - d.mesh.position.z;
    const distSq = dx * dx + dy * dy + dz * dz;

    if (distSq < PICKUP_RADIUS * PICKUP_RADIUS) {
      const dist = Math.sqrt(distSq) || 1;
      const speed = 8;
      d.mesh.position.x += (dx / dist) * speed * dt;
      d.mesh.position.y += (dy / dist) * speed * dt;
      d.mesh.position.z += (dz / dist) * speed * dt;

      if (distSq < 0.25) {
        addToInventory(d.id, d.count, d.durability);
        scene.remove(d.mesh);
        if (d.mesh.material.map) d.mesh.material.map.dispose();
        d.mesh.material.dispose();
        droppedItems.splice(i, 1);
      }
    }
  }
}