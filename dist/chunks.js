import * as THREE from 'three';
import {
  CHUNK_SIZE, RENDER_DISTANCE, SEA_LEVEL, WORLD_BOTTOM,
  BLOCK, BLOCK_TEXTURE_MAP, CACTUS_WIDTH, UV_INSET,
  ATLAS_COLS, ATLAS_SIZE,
  isWaterBlock, getWaterHeight, getWaterLevel,
  isDoorBlock, isDoorOpen
} from './config.js';
import {
  chunks, chunkBuildQueue, queuedChunks, chunkRebuildQueue, queuedRebuilds,
  treeCache, cactusCache, oreCache, caveCache,
  player, scene
} from './core.js';
import {
  getHeight, getGrassColorAt, getBaseBlock, getPlayerBlock, getBlockFacing,
  buildTreeBlocks, buildCactusBlocks, shouldSpawnShortGrass
} from './worldgen.js';
import { material, overlayMaterial, plantMaterial } from './textures.js';
import { seedChunkWater } from './water.js';
import { getLight } from './lighting.js';
import { spawnTorchesForChunk, removeTorchesForChunk } from './torches.js';

export function chunkKey(cx, cz) { return `${cx},${cz}`; }

function getTreeBlocksForChunk(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!treeCache.has(k)) treeCache.set(k, buildTreeBlocks(cx, cz));
  return treeCache.get(k);
}

function getCactusBlocksForChunk(cx, cz) {
  const k = chunkKey(cx, cz);
  if (!cactusCache.has(k)) cactusCache.set(k, buildCactusBlocks(cx, cz));
  return cactusCache.get(k);
}

function lightToColor(level){
  const t = level / 15;
  return 0.5 + 0.5 * t;
}

export function buildChunkGeometry(cx, cz) {
  const positions = [], uvs = [], normals = [], indices = [], colors = [];
  const oPositions = [], oUvs = [], oNormals = [], oIndices = [], oColors = [];
  const pPositions = [], pUvs = [], pNormals = [], pIndices = [], pColors = [];
  const bX = cx * CHUNK_SIZE, bZ = cz * CHUNK_SIZE;
  const treeBlocks = getTreeBlocksForChunk(cx, cz);
  const cactusBlocks = getCactusBlocksForChunk(cx, cz);

  const getBlock = (lx, ly, lz) => {
    const wx = bX + lx, wz = bZ + lz;
    const pb = getPlayerBlock(wx, ly, wz);
    if (pb !== null) return pb;
    const k = `${lx},${ly},${lz}`;
    if (treeBlocks.has(k)) return treeBlocks.get(k);
    if (cactusBlocks.has(k)) return cactusBlocks.get(k);
    return getBaseBlock(wx, ly, wz);
  };

  // Generic axis-aligned cube face emitter (used for normal blocks)
  const addFace = (lx, ly, lz, bt, fd, hs = 1, shrink = 0, overlay = false, overlayTop = false, tint = null) => {
    const x = bX + lx, y = ly, z = bZ + lz, s = hs, pad = shrink;
    const x0 = x + pad, x1 = x + 1 - pad, y0 = y, y1 = y + s, z0 = z + pad, z1 = z + 1 - pad;
    const texDef = BLOCK_TEXTURE_MAP[bt];
    let ti;
    if (overlay) { ti = overlayTop ? texDef.top : texDef.overlay; }
    else {
      if (fd === 0) ti = texDef.top;
      else if (fd === 1) ti = texDef.bottom;
      else {
        let facing = 0;
        if (texDef.front !== undefined) {
          const wx = bX + lx, wz = bZ + lz, f = getBlockFacing(wx, ly, wz), frontFaceIdx = [3, 2, 5, 4][f];
          if (fd === frontFaceIdx) ti = texDef.front; else ti = texDef.side;
        } else ti = texDef.side;
      }
    }
    const u0 = ti / ATLAS_COLS + UV_INSET / ATLAS_COLS;
    const u1 = (ti + 1) / ATLAS_COLS - UV_INSET / ATLAS_COLS;
    const v0 = UV_INSET, v1 = 1 - UV_INSET;
    const P = overlay ? oPositions : positions;
    const U = overlay ? oUvs : uvs;
    const N = overlay ? oNormals : normals;
    const I = overlay ? oIndices : indices;
    const C = overlay ? oColors : colors;
    const baseIdx = P.length / 3;

    let sampleLx = lx, sampleLy = ly, sampleLz = lz;
    if (fd === 0) sampleLy += 1;
    else if (fd === 1) sampleLy -= 1;
    else if (fd === 2) sampleLz += 1;
    else if (fd === 3) sampleLz -= 1;
    else if (fd === 4) sampleLx += 1;
    else if (fd === 5) sampleLx -= 1;

    const lightLevel = getLight(bX + sampleLx, sampleLy, bZ + sampleLz);
    const lightMul = lightToColor(lightLevel);
    const tr = tint ? tint[0] : 1;
    const tg = tint ? tint[1] : 1;
    const tb = tint ? tint[2] : 1;
    const cr = lightMul * tr, cg = lightMul * tg, cb = lightMul * tb;

    const quad = (a, b, c_, d, ua, ub, uc, ud, nx, ny, nz) => {
      P.push(...a, ...b, ...c_, ...d);
      U.push(...ua, ...ub, ...uc, ...ud);
      for (let i = 0; i < 4; i++) N.push(nx, ny, nz);
      I.push(baseIdx, baseIdx + 1, baseIdx + 2, baseIdx, baseIdx + 2, baseIdx + 3);
      for (let i = 0; i < 4; i++) C.push(cr, cg, cb);
    };
    const uvTL = [u0, v1], uvTR = [u1, v1], uvBR = [u1, v0], uvBL = [u0, v0];
    switch (fd) {
      case 0: quad([x0, y1, z1], [x1, y1, z1], [x1, y1, z0], [x0, y1, z0], uvBL, uvBR, uvTR, uvTL, 0, 1, 0); break;
      case 1: quad([x0, y0, z0], [x1, y0, z0], [x1, y0, z1], [x0, y0, z1], uvTL, uvTR, uvBR, uvBL, 0, -1, 0); break;
      case 2: quad([x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1], uvBL, uvBR, uvTR, uvTL, 0, 0, 1); break;
      case 3: quad([x1, y0, z0], [x0, y0, z0], [x0, y1, z0], [x1, y1, z0], uvBL, uvBR, uvTR, uvTL, 0, 0, -1); break;
      case 4: quad([x1, y0, z1], [x1, y0, z0], [x1, y1, z0], [x1, y1, z1], uvBL, uvBR, uvTR, uvTL, 1, 0, 0); break;
      case 5: quad([x0, y0, z0], [x0, y0, z1], [x0, y1, z1], [x0, y1, z0], uvBL, uvBR, uvTR, uvTL, -1, 0, 0); break;
    }
  };

  // ------ Door slab emitter ------
  const addDoorBlock = (lx, ly, lz, blockId, facing, isOpen) => {
    const x = bX + lx, y = ly, z = bZ + lz;
    const texDef = BLOCK_TEXTURE_MAP[blockId];
    const panelSlot = texDef.front;
    const edgeSlot  = texDef.side;

    const T = 0.2;
    let x0 = 0, x1 = 1, z0 = 0, z1 = 1;
    let panelAxis = 'z';

    if (!isOpen) {
      if (facing === 0) { z0 = 1 - T; z1 = 1; }
      else if (facing === 1) { z0 = 0; z1 = T; }
      else if (facing === 2) { x0 = 1 - T; x1 = 1; }
      else { x0 = 0; x1 = T; }
      panelAxis = (facing === 0 || facing === 1) ? 'z' : 'x';
    } else {
      if (facing === 0) { x0 = 0; x1 = T; }
      else if (facing === 1) { x0 = 1 - T; x1 = 1; }
      else if (facing === 2) { z0 = 1 - T; z1 = 1; }
      else { z0 = 0; z1 = T; }
      panelAxis = (facing === 0 || facing === 1) ? 'x' : 'z';
    }

    const wx0 = x + x0, wx1 = x + x1;
    const wy0 = y,       wy1 = y + 1;
    const wz0 = z + z0, wz1 = z + z1;

    const lightLevel = getLight(x, y, z);
    const lightMul = lightToColor(lightLevel);
    const cr = lightMul, cg = lightMul, cb = lightMul;

    // Fixed pushQuad — no shadowing of `uvs`
    const pushQuad = (vArr, uvArr, normal) => {
      const baseIdx = positions.length / 3;
      for (const v of vArr) positions.push(v[0], v[1], v[2]);
      for (const u of uvArr) uvs.push(u[0], u[1]);
      for (let i = 0; i < 4; i++) normals.push(normal[0], normal[1], normal[2]);
      indices.push(baseIdx, baseIdx + 1, baseIdx + 2, baseIdx, baseIdx + 2, baseIdx + 3);
      for (let i = 0; i < 4; i++) colors.push(cr, cg, cb);
    };

    const slotUV = (slot) => {
      const u0 = slot / ATLAS_COLS + UV_INSET / ATLAS_COLS;
      const u1 = (slot + 1) / ATLAS_COLS - UV_INSET / ATLAS_COLS;
      return { u0, u1, v0: UV_INSET, v1: 1 - UV_INSET };
    };

    const panelUV = slotUV(panelSlot);
    const edgeUV  = slotUV(edgeSlot);

    // +X face
    {
      const isPanel = (panelAxis === 'x');
      const uv = isPanel ? panelUV : edgeUV;
      pushQuad(
        [[wx1, wy0, wz1], [wx1, wy0, wz0], [wx1, wy1, wz0], [wx1, wy1, wz1]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [1, 0, 0]
      );
    }
    // -X face
    {
      const isPanel = (panelAxis === 'x');
      const uv = isPanel ? panelUV : edgeUV;
      pushQuad(
        [[wx0, wy0, wz0], [wx0, wy0, wz1], [wx0, wy1, wz1], [wx0, wy1, wz0]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [-1, 0, 0]
      );
    }
    // +Z face
    {
      const isPanel = (panelAxis === 'z');
      const uv = isPanel ? panelUV : edgeUV;
      pushQuad(
        [[wx0, wy0, wz1], [wx1, wy0, wz1], [wx1, wy1, wz1], [wx0, wy1, wz1]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [0, 0, 1]
      );
    }
    // -Z face
    {
      const isPanel = (panelAxis === 'z');
      const uv = isPanel ? panelUV : edgeUV;
      pushQuad(
        [[wx1, wy0, wz0], [wx0, wy0, wz0], [wx0, wy1, wz0], [wx1, wy1, wz0]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [0, 0, -1]
      );
    }
    // +Y face (top edge)
    {
      const uv = edgeUV;
      pushQuad(
        [[wx0, wy1, wz1], [wx1, wy1, wz1], [wx1, wy1, wz0], [wx0, wy1, wz0]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [0, 1, 0]
      );
    }
    // -Y face (bottom edge)
    {
      const uv = edgeUV;
      pushQuad(
        [[wx0, wy0, wz0], [wx1, wy0, wz0], [wx1, wy0, wz1], [wx0, wy0, wz1]],
        [[uv.u0, uv.v0], [uv.u1, uv.v0], [uv.u1, uv.v1], [uv.u0, uv.v1]],
        [0, -1, 0]
      );
    }
  };

  const isOpaque = b =>
    b !== BLOCK.AIR
    && !isWaterBlock(b)
    && !isDoorBlock(b)
    && b !== BLOCK.LEAVES
    && b !== BLOCK.CACTUS
    && b !== BLOCK.TORCH
    && b !== BLOCK.BED_FOOT
    && b !== BLOCK.BED_HEAD
    && b !== BLOCK.SHORT_GRASS;

  const TMP_COLOR = new THREE.Color();

  for (let lx = 0; lx < CHUNK_SIZE; lx++) for (let lz = 0; lz < CHUNK_SIZE; lz++) {
    const h = getHeight(bX + lx, bZ + lz);
    const maxY = Math.max(h + 30, SEA_LEVEL + 30);
    TMP_COLOR.setHex(getGrassColorAt(bX + lx + 0.5, bZ + lz + 0.5));
    const grassTint = [TMP_COLOR.r, TMP_COLOR.g, TMP_COLOR.b];

    for (let ly = WORLD_BOTTOM; ly <= maxY; ly++) {
      const block = getBlock(lx, ly, lz);
      if (block === BLOCK.AIR) continue;
      if (block === BLOCK.TORCH) continue;
      if (block === BLOCK.SHORT_GRASS) continue;

      // Doors use dedicated mesher
      if (isDoorBlock(block)) {
        const wx = bX + lx, wz = bZ + lz;
        const facing = getBlockFacing(wx, ly, wz);
        const isOpen = isDoorOpen(block);
        addDoorBlock(lx, ly, lz, block, facing, isOpen);
        continue;
      }

      const isW = isWaterBlock(block);
      const isFalling = block === BLOCK.WATER_FALLING;
      const isL = block === BLOCK.LEAVES;
      const isC = block === BLOCK.CACTUS;
      const isG = block === BLOCK.GRASS;
      const isTorch = block === BLOCK.TORCH;
      const isBed = block === BLOCK.BED_FOOT || block === BLOCK.BED_HEAD;
      const pad = isC ? (1 - CACTUS_WIDTH) / 2 : 0;

      const above = getBlock(lx, ly + 1, lz);
      const below = getBlock(lx, ly - 1, lz);

      const waterH = isW ? getWaterHeight(block) : 1;

      let sT;
      if (isW) {
        sT = !isWaterBlock(above);
      } else if (isC) sT = above !== BLOCK.CACTUS;
      else if (isL) sT = above !== BLOCK.LEAVES;
      else if (isTorch) sT = true;
      else sT = !isOpaque(above);

      if (sT) {
        if (isW) {
          addFace(lx, ly, lz, block, 0, waterH, pad);
        } else if (isG) {
          addFace(lx, ly, lz, block, 0, 1, pad, true, true, grassTint);
        } else {
          let hs2 = 1;
          if (isBed) hs2 = .5625;
          if (isTorch) hs2 = .625;
          addFace(lx, ly, lz, block, 0, hs2, pad);
        }
      }

      let sB;
      if (isW) {
        sB = isFalling && !isWaterBlock(below) && !isOpaque(below);
      } else if (isC) sB = below !== BLOCK.CACTUS;
      else if (isL) sB = below !== BLOCK.LEAVES;
      else if (isTorch) sB = true;
      else sB = !isOpaque(below);

      if (sB) {
        addFace(lx, ly, lz, block, 1, 1, pad);
      }

      if (isW) {
        const nF = getBlock(lx, ly, lz + 1);
        const nB = getBlock(lx, ly, lz - 1);
        const nR = getBlock(lx + 1, ly, lz);
        const nL = getBlock(lx - 1, ly, lz);

        const shouldDrawSide = (neighbor) => {
          if (isWaterBlock(neighbor)) {
            return getWaterHeight(neighbor) < waterH;
          }
          return !isOpaque(neighbor);
        };

        if (shouldDrawSide(nF)) addFace(lx, ly, lz, block, 2, waterH, pad);
        if (shouldDrawSide(nB)) addFace(lx, ly, lz, block, 3, waterH, pad);
        if (shouldDrawSide(nR)) addFace(lx, ly, lz, block, 4, waterH, pad);
        if (shouldDrawSide(nL)) addFace(lx, ly, lz, block, 5, waterH, pad);
        continue;
      }

      const nF = getBlock(lx, ly, lz + 1);
      const nB = getBlock(lx, ly, lz - 1);
      const nR = getBlock(lx + 1, ly, lz);
      const nL = getBlock(lx - 1, ly, lz);

      const sideFaces = [
        isC ? [2, nF !== BLOCK.CACTUS] : isL ? [2, nF !== BLOCK.LEAVES] : isTorch ? [2, true] : [2, !isOpaque(nF)],
        isC ? [3, nB !== BLOCK.CACTUS] : isL ? [3, nB !== BLOCK.LEAVES] : isTorch ? [3, true] : [3, !isOpaque(nB)],
        isC ? [4, nR !== BLOCK.CACTUS] : isL ? [4, nR !== BLOCK.LEAVES] : isTorch ? [4, true] : [4, !isOpaque(nR)],
        isC ? [5, nL !== BLOCK.CACTUS] : isL ? [5, nL !== BLOCK.LEAVES] : isTorch ? [5, true] : [5, !isOpaque(nL)]
      ];

      for (const [fd, shouldDraw] of sideFaces) {
        if (!shouldDraw) continue;
        let shrink2 = isC ? pad : 0;
        let hs3 = 1;
        if (isBed) hs3 = .5625;
        if (isTorch) { hs3 = .625; shrink2 = .375; }
        addFace(lx, ly, lz, block, fd, hs3, shrink2);
        if (isG) {
          addFace(lx, ly, lz, block, fd, 1, 0, true, false, grassTint);
        }
      }

      if (block === BLOCK.GRASS && !isOpaque(above)) {
        if (shouldSpawnShortGrass(bX + lx, bZ + lz)) {
          const s = 0.5;
          const px = bX + lx + 0.5, py = ly + 1, pz = bZ + lz + 0.5;
          const texDef = BLOCK_TEXTURE_MAP[BLOCK.SHORT_GRASS];
          const ti = texDef.side;
          const u0 = ti / ATLAS_COLS + UV_INSET / ATLAS_COLS;
          const u1 = (ti + 1) / ATLAS_COLS - UV_INSET / ATLAS_COLS;
          const v0 = UV_INSET, v1 = 1 - UV_INSET;
          const lightLevel = getLight(bX + lx, py, bZ + lz);
          const lightMul = lightToColor(lightLevel);
          const cr = lightMul * grassTint[0], cg = lightMul * grassTint[1], cb = lightMul * grassTint[2];
          const addQuad = (ax, ay, az, bx2, by, bz2, cx2, cy, cz2, dx2, dy, dz2) => {
            const baseIdx = pPositions.length / 3;
            pPositions.push(ax, ay, az, bx2, by, bz2, cx2, cy, cz2, dx2, dy, dz2);
            pUvs.push(u0, v0, u1, v0, u1, v1, u0, v1);
            for (let i = 0; i < 4; i++) pNormals.push(0, 1, 0);
            pColors.push(cr, cg, cb, cr, cg, cb, cr, cg, cb, cr, cg, cb);
            pIndices.push(baseIdx, baseIdx + 1, baseIdx + 2, baseIdx, baseIdx + 2, baseIdx + 3);
          };
          const h2 = 0.7;
          addQuad(px - s, py, pz - s, px + s, py, pz + s, px + s, py + h2, pz + s, px - s, py + h2, pz - s);
          addQuad(px + s, py, pz - s, px - s, py, pz + s, px - s, py + h2, pz + s, px + s, py + h2, pz - s);
        }
      }
    }
  }

  const geom = new THREE.BufferGeometry();
  geom.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geom.setAttribute('uv', new THREE.Float32BufferAttribute(uvs, 2));
  geom.setAttribute('normal', new THREE.Float32BufferAttribute(normals, 3));
  geom.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
  geom.setIndex(indices);

  const oGeom = new THREE.BufferGeometry();
  oGeom.setAttribute('position', new THREE.Float32BufferAttribute(oPositions, 3));
  oGeom.setAttribute('uv', new THREE.Float32BufferAttribute(oUvs, 2));
  oGeom.setAttribute('normal', new THREE.Float32BufferAttribute(oNormals, 3));
  oGeom.setAttribute('color', new THREE.Float32BufferAttribute(oColors, 3));
  oGeom.setIndex(oIndices);

  const pGeom = new THREE.BufferGeometry();
  pGeom.setAttribute('position', new THREE.Float32BufferAttribute(pPositions, 3));
  pGeom.setAttribute('uv', new THREE.Float32BufferAttribute(pUvs, 2));
  pGeom.setAttribute('normal', new THREE.Float32BufferAttribute(pNormals, 3));
  pGeom.setAttribute('color', new THREE.Float32BufferAttribute(pColors, 3));
  pGeom.setIndex(pIndices);

  return { geom, oGeom, pGeom };
}

export function queueChunkForBuild(cx, cz, priority) {
  const k = chunkKey(cx, cz);
  if (chunks.has(k)) return;
  if (queuedChunks.has(k)) return;
  queuedChunks.add(k);
  chunkBuildQueue.push({ cx, cz, priority });
}

export function queueChunkRebuilds(x, y, z) {
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const lx = x - cx * CHUNK_SIZE, lz = z - cz * CHUNK_SIZE;
  const addRebuild = (ccx, ccz) => {
    const nk = chunkKey(ccx, ccz);
    if (!chunks.has(nk)) return;
    if (queuedRebuilds.has(nk)) return;
    queuedRebuilds.add(nk);
    chunkRebuildQueue.push({ cx: ccx, cz: ccz });
  };
  addRebuild(cx, cz);
  const w = lx === 0, e = lx === CHUNK_SIZE - 1, n = lz === 0, s = lz === CHUNK_SIZE - 1;
  if (w) addRebuild(cx - 1, cz);
  if (e) addRebuild(cx + 1, cz);
  if (n) addRebuild(cx, cz - 1);
  if (s) addRebuild(cx, cz + 1);
  if (w && n) addRebuild(cx - 1, cz - 1);
  if (w && s) addRebuild(cx - 1, cz + 1);
  if (e && n) addRebuild(cx + 1, cz - 1);
  if (e && s) addRebuild(cx + 1, cz + 1);
}

export function forceRebuildChunkAt(x, y, z) {
  const cx = Math.floor(x / CHUNK_SIZE), cz = Math.floor(z / CHUNK_SIZE);
  const nk = chunkKey(cx, cz);
  if (!chunks.has(nk)) return;
  if (queuedRebuilds.has(nk)) return;
  queuedRebuilds.add(nk);
  chunkRebuildQueue.push({ cx, cz });
}

export function processChunkQueues() {
  const startTime = performance.now(), BUDGET = 8;
  let chunksThisFrame = 0;
  const MAX_CHUNKS_PER_FRAME = 2;

  while (chunkRebuildQueue.length > 0 && performance.now() - startTime < BUDGET && chunksThisFrame < MAX_CHUNKS_PER_FRAME) {
    const { cx, cz } = chunkRebuildQueue.shift();
    const k = chunkKey(cx, cz);
    queuedRebuilds.delete(k);
    if (!chunks.has(k)) continue;
    const { geom, oGeom, pGeom } = buildChunkGeometry(cx, cz);
    const newMesh = new THREE.Mesh(geom, material);
    scene.add(newMesh);
    const newOverlay = new THREE.Mesh(oGeom, overlayMaterial);
    scene.add(newOverlay);
    const newPlant = new THREE.Mesh(pGeom, plantMaterial);
    scene.add(newPlant);
    const old = chunks.get(k);
    scene.remove(old.solid); old.solid.geometry.dispose();
    scene.remove(old.overlay); old.overlay.geometry.dispose();
    scene.remove(old.plant); old.plant.geometry.dispose();
    chunks.set(k, { solid: newMesh, overlay: newOverlay, plant: newPlant });
    removeTorchesForChunk(cx, cz);
    spawnTorchesForChunk(cx, cz);
    chunksThisFrame++;
  }

  chunksThisFrame = 0;
  while (chunkBuildQueue.length > 0 && performance.now() - startTime < BUDGET && chunksThisFrame < MAX_CHUNKS_PER_FRAME) {
    const { cx, cz } = chunkBuildQueue.shift();
    const k = chunkKey(cx, cz);
    queuedChunks.delete(k);
    if (chunks.has(k)) continue;
    const { geom, oGeom, pGeom } = buildChunkGeometry(cx, cz);
    const mesh = new THREE.Mesh(geom, material);
    scene.add(mesh);
    const overlay = new THREE.Mesh(oGeom, overlayMaterial);
    scene.add(overlay);
    const plant = new THREE.Mesh(pGeom, plantMaterial);
    scene.add(plant);
    chunks.set(k, { solid: mesh, overlay: overlay, plant: plant });
    seedChunkWater(cx, cz);
    spawnTorchesForChunk(cx, cz);
    chunksThisFrame++;
  }
}

export function ensureChunksAround(px, pz) {
  const pcx = Math.floor(px / CHUNK_SIZE), pcz = Math.floor(pz / CHUNK_SIZE);
  const needed = new Set();
  for (let dx = -RENDER_DISTANCE; dx <= RENDER_DISTANCE; dx++) for (let dz = -RENDER_DISTANCE; dz <= RENDER_DISTANCE; dz++) {
    const cx = pcx + dx, cz = pcz + dz, dist = Math.sqrt(dx * dx + dz * dz);
    if (dist > RENDER_DISTANCE + .5) continue;
    const k = chunkKey(cx, cz);
    needed.add(k);
    if (!chunks.has(k)) queueChunkForBuild(cx, cz, dist);
  }
  for (const [k, chunk] of chunks) {
    if (!needed.has(k)) {
      const [cx, cz] = k.split(',').map(Number);
      scene.remove(chunk.solid); chunk.solid.geometry.dispose();
      scene.remove(chunk.overlay); chunk.overlay.geometry.dispose();
      scene.remove(chunk.plant); chunk.plant.geometry.dispose();
      chunks.delete(k);
      treeCache.delete(k); cactusCache.delete(k); oreCache.delete(k); caveCache.delete(k);
      removeTorchesForChunk(cx, cz);
    }
  }
}

export function rebuildAllChunks() {
  for (const [k, chunk] of chunks) {
    const [cx, cz] = k.split(',').map(Number);
    scene.remove(chunk.solid); chunk.solid.geometry.dispose();
    scene.remove(chunk.overlay); chunk.overlay.geometry.dispose();
    scene.remove(chunk.plant); chunk.plant.geometry.dispose();
    removeTorchesForChunk(cx, cz);
  }
  chunks.clear();
  ensureChunksAround(player.pos.x, player.pos.z);
}