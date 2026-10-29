import * as THREE from 'three';
import { BLOCK, ITEM, BLOCK_INFO, ITEM_INFO, TOOL_TIER, TOOL_REQUIRED_LEVEL, WORLD_BOTTOM, SEA_LEVEL, isWaterBlock, isDoorBlock, isDoorOpen, CHUNK_SIZE } from './config.js';
import { player, blockChanges, blockFacings, inventory, selectedIndex, gamemode, scene, camera } from './core.js';
import { queryBlock, getPlayerBlock } from './worldgen.js';
import { queueChunkRebuilds, forceRebuildChunkAt } from './chunks.js';
import { addToInventory, removeFromInventory, getSelectedItemId, rebuildHotbar } from './inventory.js';
import { furnaceStates } from './furnace.js';
import { chestStates } from './chest.js';
import { playSound, soundBreak, soundPlace } from './audio.js';
import { queueWaterUpdate } from './water.js';
import { addLightAt, removeLightAt, getAffectedChunks, TORCH_LIGHT } from './lighting.js';
import { spawnDroppedItem } from './items.js';

export let breaking=false, breakTarget=null, breakProgress=0;
export function setBreaking(v){breaking=v}

export const highlightGeo=new THREE.BoxGeometry(1.001,1.001,1.001);
export const highlightEdges=new THREE.EdgesGeometry(highlightGeo);
export const highlightMat=new THREE.LineBasicMaterial({color:0x000000,linewidth:2});
export const highlightBox=new THREE.LineSegments(highlightEdges,highlightMat);
highlightBox.visible=false;scene.add(highlightBox);

const particles=[];
const particleGeo=new THREE.BoxGeometry(.15,.15,.15);

export function spawnBreakParticles(x,y,z,blockId){
  const color=new THREE.Color(BLOCK_INFO[blockId].color);
  for(let i=0;i<8;i++){
    const mesh=new THREE.Mesh(particleGeo,new THREE.MeshLambertMaterial({color:color}));
    mesh.position.set(x+.5+(Math.random()-.5)*.8,y+.5+(Math.random()-.5)*.8,z+.5+(Math.random()-.5)*.8);
    scene.add(mesh);
    particles.push({mesh,vel:new THREE.Vector3((Math.random()-.5)*3,Math.random()*4+1,(Math.random()-.5)*3),life:1});
  }
}

export function updateParticles(dt){
  for(let i=particles.length-1;i>=0;i--){
    const p=particles[i];
    p.vel.y-=20*dt;
    p.mesh.position.addScaledVector(p.vel,dt);
    const bx=Math.floor(p.mesh.position.x),by=Math.floor(p.mesh.position.y),bz=Math.floor(p.mesh.position.z);
    if(isSolid(bx,by,bz)){p.vel.set(0,0,0);p.mesh.position.y=by+1.08}
    p.life-=dt*1.2;
    p.mesh.scale.setScalar(Math.max(.01,p.life));
    p.mesh.rotation.x+=dt*5;p.mesh.rotation.y+=dt*4;
    if(p.life<=0){scene.remove(p.mesh);p.mesh.material.dispose();particles.splice(i,1)}
  }
}

export function startBreaking(target){
  if(!target)return;
  if(gamemode==='creative'){breakBlockInstant(target);return}
  breaking=true;
  breakTarget={x:target.x,y:target.y,z:target.z,block:target.block};
  breakProgress=0;
}

export function breakBlockInstant(target){
  const blockId=target.block;

  if (blockId === BLOCK.SHORT_GRASS) {
    blockChanges.set(`${target.x},${target.y},${target.z}`, BLOCK.AIR);
    queueChunkRebuilds(target.x, target.y, target.z);
    document.getElementById('mining-bar-container').style.display='none';
    return;
  }

  blockChanges.set(`${target.x},${target.y},${target.z}`,BLOCK.AIR);
  blockFacings.delete(`${target.x},${target.y},${target.z}`);
  furnaceStates.delete(`${target.x},${target.y},${target.z}`);
  const chestKey=`${target.x},${target.y},${target.z}`;
  const chestSt=chestStates.get(chestKey);
  if(chestSt){
    if (gamemode === 'survival') {
      for(const it of chestSt){if(it&&it.id!==null&&it.count>0)spawnDroppedItem(target.x,target.y,target.z,it.id,it.count,it.durability)}
    }
    chestStates.delete(chestKey);
  }

  if(blockId===BLOCK.BED_FOOT){
    const dirs=[[0,1],[0,-1],[1,0],[-1,0]];
    for(const[dx,dz]of dirs){
      const k=`${target.x+dx},${target.y},${target.z+dz}`;
      if(blockChanges.get(k)===BLOCK.BED_HEAD){
        blockChanges.set(k,BLOCK.AIR);
        queueChunkRebuilds(target.x+dx,target.y,target.z+dz);
      }
    }
  }
  if(blockId===BLOCK.BED_HEAD){
    const dirs=[[0,1],[0,-1],[1,0],[-1,0]];
    for(const[dx,dz]of dirs){
      const k=`${target.x+dx},${target.y},${target.z+dz}`;
      if(blockChanges.get(k)===BLOCK.BED_FOOT){
        blockChanges.set(k,BLOCK.AIR);
        queueChunkRebuilds(target.x+dx,target.y,target.z+dz);
      }
    }
  }

  // Doors: breaking either half removes both, drops one door item
  if (isDoorBlock(blockId)) {
    const isTop = blockId === BLOCK.DOOR_TOP_CLOSED || blockId === BLOCK.DOOR_TOP_OPEN;
    const otherY = isTop ? target.y - 1 : target.y + 1;
    const otherKey = `${target.x},${otherY},${target.z}`;
    const otherBlock = blockChanges.get(otherKey) || queryBlock(target.x, otherY, target.z);

    if (isDoorBlock(otherBlock)) {
      blockChanges.set(otherKey, BLOCK.AIR);
      blockFacings.delete(otherKey);
      queueChunkRebuilds(target.x, otherY, target.z);
    }

    if (gamemode === 'survival') {
      const dropY = isTop ? target.y - 1 : target.y;
      spawnDroppedItem(target.x, dropY, target.z, BLOCK.DOOR_BOTTOM_CLOSED, 1);
    }

    queueChunkRebuilds(target.x, target.y, target.z);
    playSound(soundBreak);
    spawnBreakParticles(target.x, target.y, target.z, blockId);
    document.getElementById('mining-bar-container').style.display='none';
    return;
  }

  queueChunkRebuilds(target.x,target.y,target.z);
  playSound(soundBreak);
  spawnBreakParticles(target.x,target.y,target.z,blockId);
  const heldId=getSelectedItemId();
  const requiredLevel=TOOL_REQUIRED_LEVEL[blockId];
  let canDrop=true;
  if(requiredLevel!==undefined){
    const tier=(heldId&&typeof heldId==='string'&&TOOL_TIER.pickaxe&&TOOL_TIER.pickaxe[heldId])||0;
    canDrop=tier>=requiredLevel;
  }
  let dropItem=blockId,dropCount=1;
  if(blockId===BLOCK.STONE)dropItem=BLOCK.COBBLESTONE;
  if(blockId===BLOCK.COAL_ORE){dropItem=ITEM.COAL;dropCount=1+Math.floor(Math.random()*2)}
  if(blockId===BLOCK.IRON_ORE){dropItem=ITEM.RAW_IRON;dropCount=1}
  if(blockId===BLOCK.FURNACE||blockId===BLOCK.WORKING_FURNACE)dropItem=BLOCK.FURNACE;
  if(blockId===BLOCK.CHEST)dropItem=BLOCK.CHEST;
  if(blockId===BLOCK.BED_FOOT||blockId===BLOCK.BED_HEAD)dropItem=BLOCK.BED_FOOT;
  if(blockId===BLOCK.ICE)dropItem=BLOCK.ICE;

  if(gamemode==='survival'&&canDrop) spawnDroppedItem(target.x, target.y, target.z, dropItem, dropCount);

  if(gamemode==='survival'){
    const heldItem=inventory[selectedIndex];
    if(heldItem&&typeof heldItem.id==='string'&&ITEM_INFO[heldItem.id]&&ITEM_INFO[heldItem.id].durability){
      if(heldItem.durability===undefined)heldItem.durability=ITEM_INFO[heldItem.id].durability;
      heldItem.durability-=1;
      if(heldItem.durability<=0){
        inventory[selectedIndex]={id:null,count:0};
        playSound(soundBreak);
      }
      rebuildHotbar();
      if(typeof window.updateHeldItem==='function')window.updateHeldItem();
    }
  }

  queueWaterUpdate(target.x, target.y, target.z);

  if (blockId === BLOCK.TORCH) {
    removeLightAt(target.x, target.y, target.z);
    for (const [ccx, ccz] of getAffectedChunks(target.x, target.y, target.z)) {
      const wx = ccx * CHUNK_SIZE + 8;
      const wz = ccz * CHUNK_SIZE + 8;
      forceRebuildChunkAt(wx, target.y, wz);
    }
  }

  document.getElementById('mining-bar-container').style.display='none';
}

export function placeBlockAt(x,y,z,blockId){
  const px=Math.floor(player.pos.x),py=Math.floor(player.pos.y),pz=Math.floor(player.pos.z);
  if(x===px&&z===pz&&(y===py||y===py+1))return;
  if(gamemode==='survival'){if(!removeFromInventory(blockId,1))return}

  if(blockId===BLOCK.DOOR_BOTTOM_CLOSED){
    const above = queryBlock(x, y + 1, z);
    if (above !== BLOCK.AIR) return;

    const dir=new THREE.Vector3(-Math.sin(player.yaw),0,-Math.cos(player.yaw));
    let facing;
    if(Math.abs(dir.x)>Math.abs(dir.z))facing=dir.x>0?2:3;else facing=dir.z>0?0:1;

    blockChanges.set(`${x},${y},${z}`, BLOCK.DOOR_BOTTOM_CLOSED);
    blockChanges.set(`${x},${y + 1},${z}`, BLOCK.DOOR_TOP_CLOSED);
    blockFacings.set(`${x},${y},${z}`, facing);
    blockFacings.set(`${x},${y + 1},${z}`, facing);

    queueChunkRebuilds(x, y, z);
    queueChunkRebuilds(x, y + 1, z);
    playSound(soundPlace);
    return;
  }

  if(blockId===BLOCK.BED_FOOT){
    const dir=new THREE.Vector3(-Math.sin(player.yaw),0,-Math.cos(player.yaw));
    let hx=x,hz=z;
    if(Math.abs(dir.x)>Math.abs(dir.z)){hx+=dir.x>0?1:-1}else{hz+=dir.z>0?1:-1}
    if(queryBlock(hx,y,hz)!==BLOCK.AIR)return;
    blockChanges.set(`${x},${y},${z}`,BLOCK.BED_FOOT);
    blockChanges.set(`${hx},${y},${hz}`,BLOCK.BED_HEAD);
    queueChunkRebuilds(x,y,z);
    queueChunkRebuilds(hx,y,hz);
    playSound(soundPlace);
    return;
  }
  blockChanges.set(`${x},${y},${z}`,blockId);
  if(blockId===BLOCK.FURNACE||blockId===BLOCK.WORKING_FURNACE||blockId===BLOCK.CHEST){
    const dir=new THREE.Vector3(-Math.sin(player.yaw),0,-Math.cos(player.yaw));
    let facing;
    if(Math.abs(dir.x)>Math.abs(dir.z))facing=dir.x>0?2:3;else facing=dir.z>0?0:1;
    blockFacings.set(`${x},${y},${z}`,facing);
  }
  queueChunkRebuilds(x,y,z);
  playSound(soundPlace);

  queueWaterUpdate(x, y, z);

  if (blockId === BLOCK.TORCH) {
    addLightAt(x, y, z, TORCH_LIGHT);
    for (const [ccx, ccz] of getAffectedChunks(x, y, z)) {
      const wx = ccx * CHUNK_SIZE + 8;
      const wz = ccz * CHUNK_SIZE + 8;
      forceRebuildChunkAt(wx, y, wz);
    }
  }
}

export function isSolid(x,y,z){
  const bx=Math.floor(x),by=Math.floor(y),bz=Math.floor(z);
  if(by<WORLD_BOTTOM-5)return true;
  const b=queryBlock(bx,by,bz);
  if(b===BLOCK.AIR)return false;
  if(isWaterBlock(b))return false;
  if(b===BLOCK.LEAVES||b===BLOCK.TORCH||b===BLOCK.BED_FOOT||b===BLOCK.BED_HEAD)return false;
  if(b===BLOCK.SHORT_GRASS)return false;
  if(isDoorBlock(b)) return !isDoorOpen(b);
  return true;
}

export function updateMining(dt){
  if(gamemode!=='survival')return;
  if(!breaking||!breakTarget)return;
  const current=getTargetedBlock();
  if(!current||current.x!==breakTarget.x||current.y!==breakTarget.y||current.z!==breakTarget.z){
    breaking=false;breakTarget=null;breakProgress=0;
    document.getElementById('mining-bar-container').style.display='none';
    return;
  }
  const info=BLOCK_INFO[breakTarget.block];
  const hardness=info.hardness||.5;
  const heldId=getSelectedItemId();
  let hardness2=hardness;
  const requiredLevel=TOOL_REQUIRED_LEVEL[breakTarget.block];
  if(requiredLevel!==undefined){
    const tier=(heldId&&typeof heldId==='string'&&TOOL_TIER.pickaxe&&TOOL_TIER.pickaxe[heldId])||0;
    if(tier<requiredLevel)hardness2*=8;
  }
  breakProgress+=dt/hardness2;
  const miningBar=document.getElementById('mining-bar-container');
  const miningFill=document.getElementById('mining-bar-fill');
  const miningLabel=document.getElementById('mining-bar-label');
  miningBar.style.display='block';
  miningFill.style.width=(Math.min(1,breakProgress)*100)+'%';
  miningLabel.textContent=BLOCK_INFO[breakTarget.block].name;
  if(breakProgress>=1){
    breakBlockInstant(breakTarget);
    breaking=false;breakTarget=null;breakProgress=0;
    document.getElementById('mining-bar-container').style.display='none';
  }
}

const raycaster=new THREE.Raycaster();raycaster.far=6;

export function getTargetedBlock(){
  const origin=new THREE.Vector3(player.pos.x,player.pos.y+1.6,player.pos.z);
  const dir=new THREE.Vector3();
  camera.getWorldDirection(dir);
  raycaster.set(origin,dir);
  const step=.05;let lastAir=null;
  for(let t=0;t<6;t+=step){
    const px=Math.floor(origin.x+dir.x*t),py=Math.floor(origin.y+dir.y*t),pz=Math.floor(origin.z+dir.z*t);
    const block=queryBlock(px,py,pz);
    if(block!==BLOCK.AIR&&!isWaterBlock(block)){
      return{x:px,y:py,z:pz,block:block,placeX:lastAir?lastAir.x:px,placeY:lastAir?lastAir.y:py,placeZ:lastAir?lastAir.z:pz};
    }
    lastAir={x:px,y:py,z:pz};
  }
  return null;
}

export function updateFallDamage(dt){
  if(gamemode==='creative')return;
  if(player.health<=0)return;
  if(player.flying)return;
  if(!player.onGround){
    if(player.fallStartY===null)player.fallStartY=player.pos.y;
    else if(player.pos.y>player.fallStartY)player.fallStartY=player.pos.y;
  }else{
    if(player.fallStartY!==null){
      const fd=player.fallStartY-player.pos.y;
      if(fd>3){
        const dmg=Math.floor(fd-3);
        player.health-=dmg;
        if(player.health<0)player.health=0;
        const bar=document.getElementById('health-bar');
        if(bar&&gamemode!=='creative'){
          bar.style.display='flex';bar.innerHTML='';
          const hearts=Math.ceil(player.maxHealth/2);
          for(let i=0;i<hearts;i++){
            const heart=document.createElement('div'),hp=player.health-i*2;
            heart.className='heart'+(hp<=0?' empty':'');
            bar.appendChild(heart);
          }
        }
        if(player.health<=0){
          document.getElementById('death-screen').style.display='flex';
          if(document.pointerLockElement)document.exitPointerLock();
        }
      }
      player.fallStartY=null;
    }
  }
}