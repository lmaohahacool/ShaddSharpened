import { BLOCK, ITEM, ITEM_INFO, isItem, isDoorBlock, isDoorOpen } from './config.js';
import {
  player, inventory, selectedIndex, setSelectedIndex, keys, gamemode, mobs, getIsNightTime, blockChanges
} from './core.js';
import {
  getTargetedBlock, startBreaking, setBreaking, placeBlockAt, isSolid
} from './mining.js';
import { getTargetedMob } from './mobs.js';
import { addToInventory, removeFromInventory, rebuildHotbar } from './inventory.js';
import { openCraftingTable, openInventory, closeInventory, isInventoryOpen, isCraftingOpen } from './crafting.js';
import { openFurnaceUI, isFurnaceOpen, closeFurnaceUI } from './furnace.js';
import { openChestUI, isChestOpen, closeChestUI } from './chest.js';
import { toggleMusic, playSound, soundHit, soundEat, soundPlace } from './audio.js';
import { setWorldTimeGlobal } from './daynight.js';
import { isChatOpen } from './commands.js';
import { spawnDroppedItem } from './items.js';
import { queueChunkRebuilds } from './chunks.js';
import { queryBlock } from './worldgen.js';

export let mouseLook=false;
export let leftMouseDown=false;
export let lastSpaceTime=0;

function anyPanelOpen(){
  return isInventoryOpen()||isCraftingOpen()||isFurnaceOpen()||isChestOpen()||
    document.getElementById('gamemode-menu').style.display==='block'||
    document.getElementById('settings-menu').style.display==='block';
}

function closeAllPanels(){
  if(isFurnaceOpen())closeFurnaceUI();
  if(isChestOpen())closeChestUI();
  if(isInventoryOpen()||isCraftingOpen())closeInventory();
}

function showEatFeedback(foodValue) {
  const bar = document.getElementById('hunger-bar');
  if (bar) {
    bar.classList.add('eating-flash');
    setTimeout(() => bar.classList.remove('eating-flash'), 300);
  }

  const popup = document.createElement('div');
  popup.className = 'eat-popup';
  popup.textContent = '🍗 +' + foodValue;
  document.body.appendChild(popup);
  setTimeout(() => popup.remove(), 800);
}

export function setupInput(){
  document.addEventListener('keydown',e=>{
    if(isChatOpen())return;
    if(document.activeElement && (document.activeElement.tagName==='INPUT'||document.activeElement.tagName==='TEXTAREA'))return;
    keys[e.code]=true;
    if(e.code==='Space'&&gamemode==='creative'){
      const now=performance.now();
      if(now-lastSpaceTime<300){player.flying=!player.flying;player.vel.y=0}
      lastSpaceTime=now;
    }
  });

  document.addEventListener('keyup',e=>{
    if(isChatOpen())return;
    keys[e.code]=false;
  });

  document.addEventListener('mouseup',e=>{
    window._swiping=false;
    if(window._swipedSlots)window._swipedSlots.clear();
    if(e.button===0){
      leftMouseDown=false;
      setBreaking(false);
      const mb=document.getElementById('mining-bar-container');
      if(mb)mb.style.display='none';
    }
  });

  document.addEventListener('pointerlockchange',()=>{
    mouseLook=document.pointerLockElement===document.querySelector('canvas');
    if(!mouseLook){
      if(!anyPanelOpen()&&document.getElementById('death-screen').style.display!=='flex'&&!isChatOpen()){
        document.getElementById('click-hint').style.display='block';
        document.getElementById('crosshair').style.display='none';
      }
    }else{
      document.getElementById('click-hint').style.display='none';
      document.getElementById('crosshair').style.display='block';
    }
  });

  document.addEventListener('mousemove',e=>{
    if(!mouseLook)return;
    if(isChatOpen())return;
    player.yaw-=e.movementX*.002;
    player.pitch-=e.movementY*.002;
    player.pitch=Math.max(-Math.PI/2,Math.min(Math.PI/2,player.pitch));
  });

  document.addEventListener('mousedown',e=>{
    if(anyPanelOpen())return;
    if(isChatOpen())return;
    if(!mouseLook)return;
    if(player.health<=0)return;

    if(e.button===0){
      const m=getTargetedMob();
      if(m){
        const killed=m.damage(2, player.pos.x, player.pos.z);
        playSound(soundHit);
        if(killed){
          const i=mobs.indexOf(m);
          if(i>=0)mobs.splice(i,1);
          const mobX = Math.floor(m.group.position.x);
          const mobY = Math.floor(m.group.position.y);
          const mobZ = Math.floor(m.group.position.z);
          if(gamemode === 'survival'){
            if(m.type==='zombie')spawnDroppedItem(mobX, mobY, mobZ, ITEM.ROTTEN_FLESH, 1+Math.floor(Math.random()*2));
            else spawnDroppedItem(mobX, mobY, mobZ, ITEM.BEEF, 1+Math.floor(Math.random()*2));
          }
        }
        return;
      }
      leftMouseDown=true;
      const t=getTargetedBlock();
      if(t)startBreaking(t);
    }
    else if(e.button===2){
      const t=getTargetedBlock();
      if(!t)return;
      const pb=t.block;
      if(pb===BLOCK.CRAFTING_TABLE){openCraftingTable(t.x,t.y,t.z);return}
      if(pb===BLOCK.FURNACE||pb===BLOCK.WORKING_FURNACE){openFurnaceUI(t.x,t.y,t.z);return}
      if(pb===BLOCK.CHEST){openChestUI(t.x,t.y,t.z);return}
      if(pb===BLOCK.BED_FOOT||pb===BLOCK.BED_HEAD){
        if(getIsNightTime()){
          import('./sleeping.js').then(s=>s.trySleep());
        }
        return;
      }

      // Doors — toggle open/closed
      if(isDoorBlock(pb)){
        const isOpen = isDoorOpen(pb);
        const isTop = pb===BLOCK.DOOR_TOP_CLOSED||pb===BLOCK.DOOR_TOP_OPEN;
        const otherY = isTop ? t.y - 1 : t.y + 1;
        const otherKey = `${t.x},${otherY},${t.z}`;
        const otherBlock = blockChanges.get(otherKey) || queryBlock(t.x, otherY, t.z);

        const newHere = isOpen
          ? (isTop ? BLOCK.DOOR_TOP_CLOSED   : BLOCK.DOOR_BOTTOM_CLOSED)
          : (isTop ? BLOCK.DOOR_TOP_OPEN     : BLOCK.DOOR_BOTTOM_OPEN);
        const newOther = isOpen
          ? (isTop ? BLOCK.DOOR_BOTTOM_CLOSED : BLOCK.DOOR_TOP_CLOSED)
          : (isTop ? BLOCK.DOOR_BOTTOM_OPEN   : BLOCK.DOOR_TOP_OPEN);

        blockChanges.set(`${t.x},${t.y},${t.z}`, newHere);
        if(isDoorBlock(otherBlock)){
          blockChanges.set(otherKey, newOther);
          queueChunkRebuilds(t.x, otherY, t.z);
        }
        queueChunkRebuilds(t.x, t.y, t.z);

        playSound(soundPlace);
        return;
      }

      const selectedItem=inventory[selectedIndex];
      const bid=selectedItem&&selectedItem.id!==null&&selectedItem.id!==undefined?selectedItem.id:null;
      if(bid===null)return;
      if(bid===ITEM.BEEF||bid===ITEM.STEAK||bid===ITEM.ROTTEN_FLESH){
        if(gamemode==='survival'){
          if(player.hunger<player.maxHunger){
            const foodValue = ITEM_INFO[bid].food;
            player.hunger=Math.min(player.maxHunger,player.hunger+foodValue);
            removeFromInventory(bid,1);
            playSound(soundEat);
            showEatFeedback(foodValue);
          }
        }
        return;
      }
      if(isItem(bid))return;
      placeBlockAt(t.placeX,t.placeY,t.placeZ,bid);
    }
  });

  document.addEventListener('contextmenu',e=>e.preventDefault());

  document.addEventListener('keydown',e=>{
    if(isChatOpen())return;
    if(document.activeElement && (document.activeElement.tagName==='INPUT'||document.activeElement.tagName==='TEXTAREA'))return;
    const num=parseInt(e.key);
    if(num>=1&&num<=9){setSelectedIndex(num-1);rebuildHotbar()}
    if(e.code==='KeyM')toggleMusic();
    if(e.code==='KeyU'){
      closeAllPanels();
      import('./ui.js').then(u=>u.openGamemodeMenu());
    }
    if(e.code==='KeyE'){
      if(anyPanelOpen())closeAllPanels();
      else openInventory();
    }
    if(e.code==='Escape'){
      if(document.getElementById('settings-menu').style.display==='block'){
        document.getElementById('settings-menu').style.display='none';
        document.getElementById('click-hint').style.display='block';
        return;
      }
      if(anyPanelOpen())closeAllPanels();
      else{
        document.getElementById('click-hint').style.display='block';
        document.getElementById('crosshair').style.display='none';
        if(document.pointerLockElement)document.exitPointerLock();
      }
    }
    if(e.code==='KeyN'){location.reload()}
  });

  document.addEventListener('wheel',e=>{
    if(!mouseLook||anyPanelOpen())return;
    if(isChatOpen())return;
    let idx=selectedIndex+(e.deltaY>0?1:-1);
    idx=(idx+9)%9;
    setSelectedIndex(idx);
    rebuildHotbar();
  });
}