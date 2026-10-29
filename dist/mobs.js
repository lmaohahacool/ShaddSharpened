import * as THREE from 'three';
import { BLOCK, BIOME, WORLD_BOTTOM, MOB_GRAVITY, SEA_LEVEL } from './config.js';
import { mobs, player, scene, camera, getIsNightTime, gamemode } from './core.js';
import { queryBlock, getHeight, getBiome } from './worldgen.js';
import { playSound, soundHit } from './audio.js';

export const models = {};
export const modelManifest = {};

function mobSolid(x, y, z) {
  const bx = Math.floor(x), by = Math.floor(y), bz = Math.floor(z);
  if (by < WORLD_BOTTOM - 5) return true;
  const b = queryBlock(bx, by, bz);
  return b !== BLOCK.AIR && b !== BLOCK.WATER && b !== BLOCK.LEAVES;
}

const PIXELS_PER_BLOCK = 16;

function buildBoxMesh(box, texture, texW, texH) {
  const [w, h, d] = box.size;
  const [u, v] = box.uv;
  const pad = box.inflate || 0;

  const bw = (w + pad * 2) / PIXELS_PER_BLOCK;
  const bh = (h + pad * 2) / PIXELS_PER_BLOCK;
  const bd = (d + pad * 2) / PIXELS_PER_BLOCK;

  const geom = new THREE.BoxGeometry(bw, bh, bd);

  const U = (px) => px / texW;
  const V = (py) => 1 - py / texH;

  const uRight0 = u,               uRight1 = u + d;
  const uFront0 = u + d,           uFront1 = u + d + w;
  const uLeft0  = u + d + w,       uLeft1  = u + d + w + d;
  const uBack0  = u + d + w + d,   uBack1  = u + d + w + d + w;
  const uTop0 = u + d,             uTop1 = u + d + w;
  const uBot0 = u + d + w,         uBot1 = u + d + 2 * w;

  const vTop = v;
  const vRow = v + d;

  const regions = [
    [U(uRight0), U(uRight1), V(vRow + h), V(vRow)],
    [U(uLeft0),  U(uLeft1),  V(vRow + h), V(vRow)],
    [U(uTop0),   U(uTop1),   V(vRow),     V(vTop)],
    [U(uBot0),   U(uBot1),   V(vRow),     V(vTop)],
    [U(uFront0), U(uFront1), V(vRow + h), V(vRow)],
    [U(uBack0),  U(uBack1),  V(vRow + h), V(vRow)]
  ];

  const uvAttr = geom.attributes.uv;
  for (let f = 0; f < 6; f++) {
    const r = regions[f];
    const u0 = r[0], u1 = r[1], vTopRegion = r[2], vBotRegion = r[3];
    for (let i = 0; i < 4; i++) {
      const idx = f * 4 + i;
      const nu = uvAttr.getX(idx);
      const nv = uvAttr.getY(idx);
      uvAttr.setXY(idx, u0 + nu * (u1 - u0), vBotRegion + nv * (vTopRegion - vBotRegion));
    }
  }
  uvAttr.needsUpdate = true;

  const mat = new THREE.MeshLambertMaterial({
    map: texture,
    side: THREE.FrontSide,
    transparent: false,
    alphaTest: 0,
    depthWrite: true,
    depthTest: true
  });

  return new THREE.Mesh(geom, mat);
}

export function buildModelNew(modelData, texture) {
  const group = new THREE.Group();
  group.name = modelData.name || 'model';
  const [texW, texH] = modelData.textureSize;

  for (const part of modelData.parts) {
    const partGroup = new THREE.Group();
    partGroup.name = part.id;

    const [px, py, pz] = part.pivot;
    const [rx, ry, rz] = part.rotation || [0, 0, 0];
    const [ox, oy, oz] = part.position || [0, 0, 0];

    partGroup.position.set(
      (px + ox) / PIXELS_PER_BLOCK,
      -(py + oy) / PIXELS_PER_BLOCK,
      (pz + oz) / PIXELS_PER_BLOCK
    );

    partGroup.rotation.set(
      THREE.MathUtils.degToRad(rx),
      THREE.MathUtils.degToRad(ry),
      THREE.MathUtils.degToRad(rz)
    );

    for (const box of part.boxes) {
      const mesh = buildBoxMesh(box, texture, texW, texH);
      const [bx, by, bz] = box.origin;
      const [sw, sh, sd] = box.size;

      mesh.position.set(
        (bx + sw / 2 - px) / PIXELS_PER_BLOCK,
        -(by + sh / 2 - py) / PIXELS_PER_BLOCK,
        (bz + sd / 2 - pz) / PIXELS_PER_BLOCK
      );

      partGroup.add(mesh);
    }

    group.add(partGroup);
  }

  const legParts = group.children.filter(c => c.name && c.name.includes('leg'));
  group.userData.legs = legParts;

  return group;
}

function convertToNewFormat(oldData) {
  if (oldData.parts) return oldData;
  if (!oldData.models) return oldData;

  const newParts = [];
  for (const oldPart of oldData.models) {
    const partId = oldPart.part || 'part';
    const boxes = [];
    for (const oldBox of (oldPart.boxes || [])) {
      const c = oldBox.coordinates || [0, 0, 0, 8, 8, 8];
      const t = oldBox.textureOffset || [0, 0];
      boxes.push({
        origin: [c[0], c[1], c[2]],
        size: [c[3], c[4], c[5]],
        uv: [t[0], t[1]],
        inflate: 0
      });
    }
    newParts.push({
      id: partId,
      name: partId.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase()),
      pivot: [0, 0, 0],
      position: [0, 0, 0],
      rotation: [0, 0, 0],
      boxes: boxes
    });
  }
  return {
    name: oldData.name || 'model',
    textureSize: oldData.textureSize || [64, 64],
    parts: newParts
  };
}

export async function loadAllModels() {
  const res = await fetch('models/manifest.json?v=' + Date.now());
  if (!res.ok) { console.error('models/manifest.json not found'); return; }
  const manifest = await res.json();

  const modelsList = manifest.models || manifest;
  Object.assign(modelManifest, modelsList);

  const loader = new THREE.TextureLoader();

  const promises = Object.entries(modelsList).map(([name, info]) => {
    if (name === 'version') return Promise.resolve();

    return Promise.all([
      fetch(info.model + '?v=' + Date.now()).then(r => r.json()),
      info.texture
        ? new Promise((resolve) => {
            loader.load(info.texture + '?v=' + Date.now(), tex => {
              tex.magFilter = THREE.NearestFilter;
              tex.minFilter = THREE.NearestFilter;
              tex.colorSpace = THREE.SRGBColorSpace;
              resolve(tex);
            }, undefined, () => {
              console.warn('missing texture: ' + info.texture);
              resolve(null);
            });
          })
        : Promise.resolve(null)
    ]).then(([data, tex]) => {
      const color = new THREE.Color(info.color || '#888888');
      const newData = convertToNewFormat(data);

      if (newData.parts) {
        models[name] = {
          data: newData,
          texture: tex,
          color,
          format: 'new',
          create: () => buildModelNew(newData, tex)
        };
        console.log('loaded model (new): ' + name);
      } else {
        models[name] = {
          data,
          texture: tex,
          color,
          format: 'legacy',
          create: () => buildModelLegacy(data, tex, color)
        };
        console.log('loaded model (legacy): ' + name);
      }
    });
  });

  await Promise.all(promises);
}

function buildBoxLegacy(w, h, d, u, v, texW, texH, texture, color) {
  const geom = new THREE.BoxGeometry(w / 16, h / 16, d / 16);
  const U = (px) => px / texW;
  const V = (py) => 1 - py / texH;

  const uRight0 = u, uRight1 = u + d;
  const uFront0 = u + d, uFront1 = u + d + w;
  const uLeft0 = u + d + w, uLeft1 = u + d + w + d;
  const uBack0 = u + d + w + d, uBack1 = u + d + w + d + w;
  const uTop0 = u + d, uTop1 = u + d + w;
  const uBot0 = u + d + w, uBot1 = u + d + 2 * w;

  const vTop = v, vRow = v + d;

  const regions = [
    [U(uRight0), U(uRight1), V(vRow + h), V(vRow)],
    [U(uLeft0), U(uLeft1), V(vRow + h), V(vRow)],
    [U(uTop0), U(uTop1), V(vRow), V(vTop)],
    [U(uBot0), U(uBot1), V(vRow), V(vTop)],
    [U(uFront0), U(uFront1), V(vRow + h), V(vRow)],
    [U(uBack0), U(uBack1), V(vRow + h), V(vRow)]
  ];

  const uvAttr = geom.attributes.uv;
  for (let f = 0; f < 6; f++) {
    const r = regions[f];
    for (let i = 0; i < 4; i++) {
      const idx = f * 4 + i;
      const nu = uvAttr.getX(idx);
      const nv = uvAttr.getY(idx);
      uvAttr.setXY(idx, r[0] + nu * (r[1] - r[0]), r[3] + nv * (r[2] - r[3]));
    }
  }
  uvAttr.needsUpdate = true;

  const mat = texture
    ? new THREE.MeshLambertMaterial({ map: texture, side: THREE.FrontSide })
    : new THREE.MeshLambertMaterial({ color: color, side: THREE.FrontSide });

  return new THREE.Mesh(geom, mat);
}

export function buildModelLegacy(modelData, texture, color) {
  const group = new THREE.Group();
  const texW = modelData.textureSize[0];
  const texH = modelData.textureSize[1] || texW;

  for (const part of modelData.models) {
    const partGroup = new THREE.Group();
    for (const box of part.boxes) {
      const [x, y, z, w, h, d] = box.coordinates;
      const [u, v] = box.textureOffset;
      const mesh = buildBoxLegacy(w, h, d, u, v, texW, texH, texture, color);
      mesh.position.set(x / 16 + w / 32, -y / 16 - h / 32, z / 16 + d / 32);
      partGroup.add(mesh);
    }
    partGroup.name = part.part;
    group.add(partGroup);
  }

  const legParts = group.children.filter(c => c.name && c.name.includes('leg'));
  group.userData.legs = legParts;

  return group;
}

export function getModel(name) {
  return models[name] ? models[name].create() : null;
}

function collectMaterials(group) {
  const mats = [];
  group.traverse(obj => {
    if (obj.isMesh && obj.material) mats.push(obj.material);
  });
  return mats;
}

function tryJumpOver(mob, dirX, dirZ) {
  if (!mob.onGround) return false;
  const p = mob.group.position;
  const frontX = p.x + dirX * 0.5;
  const frontZ = p.z + dirZ * 0.5;

  const blockedAtFeet = mobSolid(frontX, p.y, frontZ);
  const blockedAtHead = mobSolid(frontX, p.y + mob.height - 0.2, frontZ);
  const clearAbove = !mobSolid(frontX, p.y + mob.height + 0.2, frontZ);

  if (blockedAtFeet && !blockedAtHead && clearAbove) {
    mob.vel.y = 7.5;
    mob.onGround = false;
    return true;
  }
  return false;
}

// -------------------- PERSISTENT LEASH --------------------
//
// If a mob is flagged `persistent`, it remembers its save position and
// won't wander more than LEASH_RADIUS blocks away from it. When it hits
// the boundary, it turns around and wanders back toward home.
//
// This is what makes a cow pen work without a fence block: tamed cows
// stay near where you tamed them.
const LEASH_RADIUS = 8;

function applyLeash(mob) {
  if (!mob.persistent || !mob.homePos) return false;

  const p = mob.group.position;
  const dx = p.x - mob.homePos.x;
  const dz = p.z - mob.homePos.z;
  const distSq = dx * dx + dz * dz;

  if (distSq <= LEASH_RADIUS * LEASH_RADIUS) return false;

  // Out of bounds — turn around and head home
  const dist = Math.sqrt(distSq) || 1;
  mob.walkDir.set(-dx / dist, 0, -dz / dist);
  return true;
}

// -------------------- MOB (cow) --------------------

export class Mob {
  constructor(x, y, z, modelName = 'cow') {
    this.group = getModel(modelName) || new THREE.Group();
    this.group.position.set(x, y, z);
    scene.add(this.group);
    this.vel = new THREE.Vector3();
    this.health = 10;
    this.maxHealth = 10;
    this.onGround = false;
    this.width = 0.8;
    this.height = 1.2;
    this.walkDir = new THREE.Vector3(0, 0, 0);
    this.idleTimer = 0;
    this.walkAnim = 0;
    this.type = modelName;
    this.flashTimer = 0;
    this.materials = collectMaterials(this.group);
    this.hurtTimer = 0;
    this.knockbackResist = 1.0;
    this.fleeTimer = 0;
    this.fleeDir = new THREE.Vector3();

    // --- Stage 6: persistence ---
    this.persistent = false;
    this.homePos = null;    // set when tamed
    this.tameIndicator = null;
  }

  // Called when the player right-clicks this cow.
  // Toggles persistence. When becoming persistent, records the current
  // position as "home" so the leash has a center.
  togglePersistent() {
    this.persistent = !this.persistent;
    if (this.persistent) {
      this.homePos = this.group.position.clone();
      // visual: a small floating marker above the cow
      if (!this.tameIndicator) {
        const geo = new THREE.SphereGeometry(0.12, 6, 4);
        const mat = new THREE.MeshBasicMaterial({ color: 0xffcc44 });
        this.tameIndicator = new THREE.Mesh(geo, mat);
        this.tameIndicator.position.set(0, this.height + 0.35, 0);
        this.group.add(this.tameIndicator);
      }
      this.tameIndicator.visible = true;
    } else {
      this.homePos = null;
      if (this.tameIndicator) this.tameIndicator.visible = false;
    }
    return this.persistent;
  }

  update(dt) {
    this.vel.y -= MOB_GRAVITY * dt;
    this.vel.x *= Math.pow(0.15, dt);
    this.vel.z *= Math.pow(0.15, dt);

    if (this.flashTimer > 0) {
      this.flashTimer -= dt;
      const intensity = Math.max(0, this.flashTimer / 0.15);
      for (const m of this.materials) m.emissive.setRGB(intensity, 0, 0);
    }

    this.hurtTimer -= dt;
    const skipAI = this.hurtTimer > 0;

    let currentSpeed = 1.2;
    if (this.fleeTimer > 0) {
      this.fleeTimer -= dt;
      this.walkDir.copy(this.fleeDir);
      currentSpeed = 4.5;
    } else if (!skipAI) {
      this.idleTimer -= dt;
      if (this.idleTimer <= 0) {
        this.idleTimer = 2 + Math.random() * 3;
        if (Math.random() < 0.6) {
          const a = Math.random() * Math.PI * 2;
          this.walkDir.set(Math.cos(a), 0, Math.sin(a));
        } else {
          this.walkDir.set(0, 0, 0);
        }
      }

      // Leash takes priority over random wandering.
      // If a persistent cow wandered too far from home, redirect it.
      applyLeash(this);
    }

    const p = this.group.position;

    if (this.walkDir.length() > 0) {
      const nx = p.x + this.walkDir.x * currentSpeed * dt;
      const nz = p.z + this.walkDir.z * currentSpeed * dt;

      let blockedX = false, blockedZ = false;

      if (!mobSolid(nx, p.y, p.z) && !mobSolid(nx, p.y + this.height - 0.2, p.z)) {
        p.x = nx;
      } else blockedX = true;

      if (!mobSolid(p.x, p.y, nz) && !mobSolid(p.x, p.y + this.height - 0.2, nz)) {
        p.z = nz;
      } else blockedZ = true;

      if ((blockedX || blockedZ) && this.onGround) {
        if (!tryJumpOver(this, this.walkDir.x, this.walkDir.z)) {
          if (blockedX) this.walkDir.x *= -1;
          if (blockedZ) this.walkDir.z *= -1;
        }
      }
    }

    const kx = p.x + this.vel.x * dt;
    const kz = p.z + this.vel.z * dt;
    if (!mobSolid(kx, p.y, p.z) && !mobSolid(kx, p.y + this.height - 0.2, p.z)) p.x = kx;
    else this.vel.x = 0;
    if (!mobSolid(p.x, p.y, kz) && !mobSolid(p.x, p.y + this.height - 0.2, kz)) p.z = kz;
    else this.vel.z = 0;

    let ny = p.y + this.vel.y * dt;
    this.onGround = false;
    if (this.vel.y <= 0) {
      if (mobSolid(p.x, ny, p.z)) { p.y = Math.floor(ny) + 1; this.vel.y = 0; this.onGround = true; }
      else p.y = ny;
    } else {
      if (mobSolid(p.x, ny + this.height, p.z)) this.vel.y = 0;
      else p.y = ny;
    }

    if (this.walkDir.length() > 0) {
      this.walkAnim += dt * (this.fleeTimer > 0 ? 12 : 8);
      this.group.rotation.y = Math.atan2(this.walkDir.x, this.walkDir.z) + Math.PI;
    }

    const swing = Math.sin(this.walkAnim) * 0.4;
    const legs = this.group.userData.legs;
    if (legs) {
      for (let i = 0; i < legs.length; i++) {
        legs[i].rotation.x = (i % 2 === 0 ? swing : -swing);
      }
    }
  }
  damage(a, sourceX, sourceZ) {
    this.health -= a;
    this.flashTimer = 0.15;
    this.hurtTimer = 0.3;
    if (sourceX !== undefined && sourceZ !== undefined) {
      const dx = this.group.position.x - sourceX;
      const dz = this.group.position.z - sourceZ;
      const dist = Math.hypot(dx, dz) || 1;
      const strength = 7 * this.knockbackResist;
      this.vel.x += (dx / dist) * strength;
      this.vel.z += (dz / dist) * strength;
      this.vel.y = 4;
      this.fleeTimer = 5;
      this.fleeDir.set(dx / dist, 0, dz / dist);
    }
    if (this.health <= 0) { scene.remove(this.group); return true; }
    return false;
  }
}

// -------------------- ZOMBIE with burning --------------------

export class Zombie {
  constructor(x, y, z) {
    this.group = getModel('zombie') || new THREE.Group();
    this.group.position.set(x, y, z);
    scene.add(this.group);
    this.vel = new THREE.Vector3();
    this.health = 20;
    this.maxHealth = 20;
    this.onGround = false;
    this.width = 0.6;
    this.height = 1.8;
    this.walkDir = new THREE.Vector3(0, 0, 0);
    this.walkAnim = 0;
    this.attackCooldown = 0;
    this.type = 'zombie';
    this.flashTimer = 0;
    this.materials = collectMaterials(this.group);
    this.hurtTimer = 0;
    this.knockbackResist = 0.6;
    this.idleTimer = 0;

    // Burning
    this.burning = false;
    this.burnTimer = 0;
    this.burnCheckTimer = 0;
    this.burnSoundTimer = 0;

    // Zombies are always persistent (they're hostile creatures).
    // They don't have a leash — they roam freely. They don't have a
    // home position either, because they're not "tamed."
    this.persistent = true;
    this.homePos = null;
    this.tameIndicator = null;
  }

  hasSkyAbove() {
    const p = this.group.position;
    const x = Math.floor(p.x);
    const z = Math.floor(p.z);
    const y = Math.floor(p.y + this.height);

    for (let checkY = y; checkY < 128; checkY++) {
      const b = queryBlock(x, checkY, z);
      if (b !== BLOCK.AIR) return false;
    }
    return true;
  }

  updateBurning(dt) {
    this.burnCheckTimer -= dt;
    if (this.burnCheckTimer <= 0) {
      this.burnCheckTimer = 0.5;

      const shouldBurn = !getIsNightTime() && this.hasSkyAbove();

      if (shouldBurn && !this.burning) {
        this.burning = true;
        for (const m of this.materials) {
          if (m.color) m.color.setRGB(1.0, 0.55, 0.3);
        }
      } else if (!shouldBurn && this.burning) {
        this.burning = false;
        for (const m of this.materials) {
          if (m.color) m.color.setRGB(1.0, 1.0, 1.0);
        }
      }
    }

    if (this.burning) {
      this.burnTimer -= dt;
      if (this.burnTimer <= 0) {
        this.burnTimer = 1.5;
        const killed = this.damage(1);
        if (killed) return true;
      }
    }

    return false;
  }

  update(dt) {
    if (this.updateBurning(dt)) return;

    this.vel.y -= MOB_GRAVITY * dt;
    this.vel.x *= Math.pow(0.15, dt);
    this.vel.z *= Math.pow(0.15, dt);
    this.attackCooldown -= dt;

    if (this.flashTimer > 0) {
      this.flashTimer -= dt;
      const intensity = Math.max(0, this.flashTimer / 0.15);
      for (const m of this.materials) {
        if (m.emissive) m.emissive.setRGB(intensity, 0, 0);
      }
    }

    this.hurtTimer -= dt;
    const skipAI = this.hurtTimer > 0;
    const p = this.group.position;
    const dx = player.pos.x - p.x, dz = player.pos.z - p.z;
    const dist = Math.hypot(dx, dz);

    if (!skipAI) {
      if (gamemode === 'survival') {
        if (dist < 16 && dist > 0) {
          this.walkDir.set(dx / dist, 0, dz / dist);
        } else {
          this.idleTimer -= dt;
          if (this.idleTimer <= 0) {
            this.idleTimer = 2 + Math.random() * 3;
            if (Math.random() < 0.5) {
              const a = Math.random() * Math.PI * 2;
              this.walkDir.set(Math.cos(a), 0, Math.sin(a));
            } else {
              this.walkDir.set(0, 0, 0);
            }
          }
        }
      } else {
        this.idleTimer -= dt;
        if (this.idleTimer <= 0) {
          this.idleTimer = 3 + Math.random() * 4;
          if (Math.random() < 0.5) {
            const a = Math.random() * Math.PI * 2;
            this.walkDir.set(Math.cos(a), 0, Math.sin(a));
          } else {
            this.walkDir.set(0, 0, 0);
          }
        }
      }

      if (this.walkDir.length() > 0) {
        this.group.rotation.y = Math.atan2(this.walkDir.x, this.walkDir.z);
      }

      const sp = gamemode === 'survival' ? 1.8 : 0.6;
      const nx = p.x + this.walkDir.x * sp * dt;
      const nz = p.z + this.walkDir.z * sp * dt;

      let blockedX = false, blockedZ = false;

      if (!mobSolid(nx, p.y, p.z) && !mobSolid(nx, p.y + this.height - 0.2, p.z)) {
        p.x = nx;
      } else blockedX = true;

      if (!mobSolid(p.x, p.y, nz) && !mobSolid(p.x, p.y + this.height - 0.2, nz)) {
        p.z = nz;
      } else blockedZ = true;

      if ((blockedX || blockedZ) && this.onGround) {
        if (!tryJumpOver(this, this.walkDir.x, this.walkDir.z)) {
          if (blockedX) this.walkDir.x *= -1;
          if (blockedZ) this.walkDir.z *= -1;
        }
      }
    }

    const kx = p.x + this.vel.x * dt;
    const kz = p.z + this.vel.z * dt;
    if (!mobSolid(kx, p.y, p.z) && !mobSolid(kx, p.y + this.height - 0.2, p.z)) p.x = kx;
    else this.vel.x = 0;
    if (!mobSolid(p.x, p.y, kz) && !mobSolid(p.x, p.y + this.height - 0.2, kz)) p.z = kz;
    else this.vel.z = 0;

    let ny = p.y + this.vel.y * dt;
    this.onGround = false;
    if (this.vel.y <= 0) {
      if (mobSolid(p.x, ny, p.z)) { p.y = Math.floor(ny) + 1; this.vel.y = 0; this.onGround = true; }
      else p.y = ny;
    } else {
      if (mobSolid(p.x, ny + this.height, p.z)) this.vel.y = 0;
      else p.y = ny;
    }

    if (!skipAI && gamemode === 'survival' && dist < 1.2 && this.attackCooldown <= 0 && Math.abs(p.y - player.pos.y) < 1.5) {
      player.health -= 3;
      if (player.health < 0) player.health = 0;
      const bar = document.getElementById('health-bar');
      if (bar) {
        bar.style.display = 'flex';
        bar.innerHTML = '';
        const hearts = Math.ceil(player.maxHealth / 2);
        for (let i = 0; i < hearts; i++) {
          const heart = document.createElement('div'), hp = player.health - i * 2;
          heart.className = 'heart' + (hp <= 0 ? ' empty' : '');
          bar.appendChild(heart);
        }
      }
      if (player.health <= 0) {
        document.getElementById('death-screen').style.display = 'flex';
        if (document.pointerLockElement) document.exitPointerLock();
      }
      this.attackCooldown = 1;
    }

    this.walkAnim += dt * 6;
    const swing = Math.sin(this.walkAnim) * 0.5;
    const legs = this.group.userData.legs;
    if (legs) {
      legs[0].rotation.x = swing;
      if (legs[1]) legs[1].rotation.x = -swing;
    }
  }

  damage(a, sourceX, sourceZ) {
    this.health -= a;
    this.flashTimer = 0.15;
    this.hurtTimer = 0.3;
    if (sourceX !== undefined && sourceZ !== undefined) {
      const dx = this.group.position.x - sourceX;
      const dz = this.group.position.z - sourceZ;
      const dist = Math.hypot(dx, dz) || 1;
      const strength = 7 * this.knockbackResist;
      this.vel.x += (dx / dist) * strength;
      this.vel.z += (dz / dist) * strength;
      this.vel.y = 4;
    }
    if (this.health <= 0) { scene.remove(this.group); return true; }
    return false;
  }
}

export function spawnCow(x, z) {
  const y = getHeight(x, z) + 1;
  const cow = new Mob(x, y, z, 'cow');
  mobs.push(cow);
  return cow;
}

export function spawnZombie(x, z) {
  const y = getHeight(x, z) + 1;
  const zz = new Zombie(x, y, z);
  mobs.push(zz);
  return zz;
}

export function trySpawnCows(px, pz) {
  for (let i = 0; i < 20; i++) {
    const a = Math.random() * Math.PI * 2, d = 10 + Math.random() * 30;
    const cx = Math.floor(px + Math.cos(a) * d), cz = Math.floor(pz + Math.sin(a) * d);
    const biome = getBiome(cx, cz);
    if (biome !== BIOME.PLAINS && biome !== BIOME.FOREST && biome !== BIOME.OLD_GROWTH) continue;
    const h = getHeight(cx, cz);
    if (h <= SEA_LEVEL) continue;
    if (mobs.some(m => Math.abs(m.group.position.x - cx) < 3 && Math.abs(m.group.position.z - cz) < 3)) continue;
    if (Math.random() < 0.5) spawnCow(cx + 0.5, cz + 0.5);
  }
}

export function trySpawnZombies(px, pz) {
  if (!getIsNightTime()) return;
  const zombieCount = mobs.filter(m => m.type === 'zombie').length;
  if (zombieCount >= 8) return;
  for (let i = 0; i < 6; i++) {
    const a = Math.random() * Math.PI * 2, d = 14 + Math.random() * 20;
    const cx = Math.floor(px + Math.cos(a) * d), cz = Math.floor(pz + Math.sin(a) * d);
    const h = getHeight(cx, cz);
    if (h <= SEA_LEVEL) continue;
    if (mobs.some(m => Math.abs(m.group.position.x - cx) < 2 && Math.abs(m.group.position.z - cz) < 2)) continue;
    if (Math.random() < 0.4) spawnZombie(cx + 0.5, cz + 0.5);
  }
}

let cowSpawnTimer = 0, zombieSpawnTimer = 0;
export function updateMobs(dt) {
  for (let i = mobs.length - 1; i >= 0; i--) {
    const m = mobs[i];
    m.update(dt);
    // Stage 6 fix: remove dead cows too, not just zombies.
    // Otherwise dead cows stay in the array forever and get saved.
    if (m.health <= 0) mobs.splice(i, 1);
  }

  cowSpawnTimer += dt;
  if (cowSpawnTimer > 10) {
    cowSpawnTimer = 0;
    if (mobs.filter(m => m.type === 'cow').length < 15) trySpawnCows(player.pos.x, player.pos.z);
  }
  zombieSpawnTimer += dt;
  if (zombieSpawnTimer > 3) {
    zombieSpawnTimer = 0;
    trySpawnZombies(player.pos.x, player.pos.z);
  }
}

export function getTargetedMob() {
  const origin = new THREE.Vector3(player.pos.x, player.pos.y + 1.6, player.pos.z);
  const dir = new THREE.Vector3();
  camera.getWorldDirection(dir);
  let closest = null, closestT = 5;
  for (const m of mobs) {
    const dx = m.group.position.x - origin.x;
    const dy = m.group.position.y + 0.3 - origin.y;
    const dz = m.group.position.z - origin.z;
    const t = dx * dir.x + dy * dir.y + dz * dir.z;
    if (t < 0 || t > 5) continue;
    const px = origin.x + dir.x * t, py = origin.y + dir.y * t, pz = origin.z + dir.z * t;
    const d = Math.sqrt(
      (px - m.group.position.x) ** 2 +
      (py - m.group.position.y - 0.3) ** 2 +
      (pz - m.group.position.z) ** 2
    );
    if (d < 0.8 && t < closestT) { closest = m; closestT = t; }
  }
  return closest;
}

// -------------------- STAGE 6: SAVE/LOAD --------------------

// Restore a tamed cow from saved data. Uses the constructor to get
// default state, then overwrites saved fields. Re-creates the tame
// indicator mesh (which is normally added on togglePersistent).
export function restoreCow(save) {
  const cow = new Mob(save.x, save.y, save.z, 'cow');
  cow.vel.set(save.vx, save.vy, save.vz);
  cow.health = save.health;
  cow.onGround = save.onGround;
  cow.walkAnim = save.walkAnim;
  cow.idleTimer = save.idleTimer;
  cow.persistent = true;
  cow.homePos = new THREE.Vector3(save.homeX, save.homeY, save.homeZ);
  // Rebuild the tame indicator mesh
  if (!cow.tameIndicator) {
    const geo = new THREE.SphereGeometry(0.12, 6, 4);
    const mat = new THREE.MeshBasicMaterial({ color: 0xffcc44 });
    cow.tameIndicator = new THREE.Mesh(geo, mat);
    cow.tameIndicator.position.set(0, cow.height + 0.35, 0);
    cow.group.add(cow.tameIndicator);
  }
  cow.tameIndicator.visible = true;
  mobs.push(cow);
  return cow;
}

// Restore a zombie from saved data. If it was burning when saved,
// restore the color tint. Otherwise the next updateBurning tick will
// re-evaluate whether it should be burning.
export function restoreZombie(save) {
  const z = new Zombie(save.x, save.y, save.z);
  z.vel.set(save.vx, save.vy, save.vz);
  z.health = save.health;
  z.onGround = save.onGround;
  z.walkAnim = save.walkAnim;
  z.burning = save.burning;
  z.burnTimer = save.burnTimer;
  z.burnCheckTimer = save.burnCheckTimer;
  if (z.burning) {
    for (const m of z.materials) {
      if (m.color) m.color.setRGB(1.0, 0.55, 0.3);
    }
  }
  mobs.push(z);
  return z;
}