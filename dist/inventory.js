import { HOTBAR_SIZE, TOTAL_SLOTS, INVENTORY_MAX_STACK, BLOCK, ITEM, ITEM_INFO, isItem, getInfo } from './config.js';
import { inventory as inv, selectedIndex, setSelectedIndex, cursorItem, setCursorItem, gamemode } from './core.js';
import { getIconHTML, getDurabilityHTML } from './textures.js';
import { refreshUI } from './crafting.js';

export function getInventory(){return inv}
export function getSelectedIndex(){return selectedIndex}
export function getCursorItem(){return cursorItem}

export function getSelectedItemId(){
  const item=inv[selectedIndex];
  return item&&item.id!==null&&item.id!==undefined?item.id:null;
}

export function rebuildHotbar(){
  const hotbarEl=document.getElementById('hotbar');
  if(!hotbarEl)return;
  hotbarEl.innerHTML='';
  for(let i=0;i<HOTBAR_SIZE;i++){
    const s=document.createElement('div');
    s.className='slot'+(i===selectedIndex?' selected':'');
    const item=inv[i];
    if(item&&item.id!==null&&item.count>0){
      s.innerHTML=`<div class="slot-label">${(i+1)%10}</div>${getIconHTML(item.id)}${getDurabilityHTML(item)}<div class="slot-count">${item.count}</div>`;
    }else{
      s.innerHTML=`<div class="slot-label">${(i+1)%10}</div>`;
    }
    hotbarEl.appendChild(s);
  }
}

export function updateHotbar(){
  rebuildHotbar();
  if(typeof window.updateHeldItem==='function')window.updateHeldItem();
}

export function addToInventory(blockId,count,durability){
  for(let i=0;i<TOTAL_SLOTS;i++){
    const item=inv[i];
    if(item.id===blockId&&item.count<INVENTORY_MAX_STACK&&(!isItem(blockId)||!ITEM_INFO[blockId].durability)){
      const space=INVENTORY_MAX_STACK-item.count,toAdd=Math.min(space,count);
      item.count+=toAdd;count-=toAdd;
      if(count<=0){rebuildHotbar();if(typeof refreshUI==='function')refreshUI();return}
    }
  }
  for(let i=0;i<TOTAL_SLOTS;i++){
    const item=inv[i];
    if(item.id===null||item.count===0){
      const toAdd=Math.min(INVENTORY_MAX_STACK,count);
      inv[i]={
        id:blockId,
        count:toAdd,
        durability:durability!==undefined?durability:(isItem(blockId)&&ITEM_INFO[blockId].durability?ITEM_INFO[blockId].durability:undefined)
      };
      count-=toAdd;
      if(count<=0){rebuildHotbar();if(typeof refreshUI==='function')refreshUI();return}
    }
  }
  rebuildHotbar();
  if(typeof refreshUI==='function')refreshUI();
}

export function removeFromInventory(blockId,count){
  let total=0;
  for(const item of inv)if(item.id===blockId)total+=item.count;
  if(total<count)return false;
  const order=[];
  for(let i=0;i<TOTAL_SLOTS;i++)if(i!==selectedIndex)order.push(i);
  order.unshift(selectedIndex);
  let remaining=count;
  for(const i of order){
    if(remaining<=0)break;
    const item=inv[i];
    if(item.id===blockId){
      const toRemove=Math.min(item.count,remaining);
      item.count-=toRemove;
      remaining-=toRemove;
      if(item.count<=0)inv[i]={id:null,count:0};
    }
  }
  rebuildHotbar();
  if(typeof refreshUI==='function')refreshUI();
  return true;
}

export function makeItem(id,count){
  if(isItem(id)&&ITEM_INFO[id]&&ITEM_INFO[id].durability)return{id,count,durability:ITEM_INFO[id].durability};
  return{id,count};
}

export function updateCursorDisplay(){
  const cursorEl=document.getElementById('cursor-item');
  if(!cursorEl)return;
  if(cursorItem&&cursorItem.id!==null&&cursorItem.count>0){
    cursorEl.style.display='block';
    cursorEl.innerHTML=`${getIconHTML(cursorItem.id)}<div class="slot-count">${cursorItem.count}</div>`;
  }else{
    cursorEl.style.display='none';
  }
}

export function clickSlot(slotGetter,slotSetter,rightClick,shiftClick){
  const cur=slotGetter();

  if(cursorItem===null||cursorItem.id===null){
    if(cur&&cur.id!==null&&cur.count>0){
      if(rightClick){
        if(shiftClick){
          slotSetter({id:null,count:0});
        }else{
          const half=Math.floor(cur.count/2);
          if(half>0){
            setCursorItem({id:cur.id,count:half,durability:cur.durability});
            cur.count-=half;
            if(cur.count<=0)slotSetter({id:null,count:0});
          }
        }
      }else{
        setCursorItem({id:cur.id,count:cur.count,durability:cur.durability});
        slotSetter({id:null,count:0});
      }
    }
    updateCursorDisplay();
    return;
  }

  if(cur===null||cur.id===null||cur.count<=0){
    if(rightClick){
      slotSetter({id:cursorItem.id,count:1,durability:cursorItem.durability});
      const c={...cursorItem};c.count--;
      setCursorItem(c.count<=0?null:c);
    }else{
      slotSetter({id:cursorItem.id,count:cursorItem.count,durability:cursorItem.durability});
      setCursorItem(null);
    }
  }
  else if(cur.id===cursorItem.id&&cur.count<INVENTORY_MAX_STACK&&!rightClick){
    const space=INVENTORY_MAX_STACK-cur.count,move=Math.min(space,cursorItem.count);
    cur.count+=move;
    const c={...cursorItem};c.count-=move;
    setCursorItem(c.count<=0?null:c);
  }
  else if(rightClick&&cur.id===cursorItem.id&&cur.count<INVENTORY_MAX_STACK){
    cur.count++;
    const c={...cursorItem};c.count--;
    setCursorItem(c.count<=0?null:c);
  }
  else{
    const tmp={id:cur.id,count:cur.count,durability:cur.durability};
    slotSetter({id:cursorItem.id,count:cursorItem.count,durability:cursorItem.durability});
    setCursorItem(tmp);
  }
  updateCursorDisplay();
}

export function swipePlace(slotGetter,slotSetter,slotKey){
  if(!window._swiping)return false;
  if(!window._swipedSlots)window._swipedSlots=new Set();
  if(window._swipedSlots.has(slotKey))return false;
  if(cursorItem===null||cursorItem.id===null||cursorItem.count<=0)return false;
  const cur=slotGetter();
  if(cur&&cur.id!==null&&cur.count>0){
    if(cur.id!==cursorItem.id||cur.count>=INVENTORY_MAX_STACK)return false;
    cur.count++;
  }else{
    slotSetter({id:cursorItem.id,count:1,durability:cursorItem.durability});
  }
  const c={...cursorItem};c.count--;
  setCursorItem(c.count<=0?null:c);
  window._swipedSlots.add(slotKey);
  updateCursorDisplay();
  return true;
}

document.addEventListener('mousemove',e=>{
  if(cursorItem&&cursorItem.id!==null){
    const cursorEl=document.getElementById('cursor-item');
    if(cursorEl){
      cursorEl.style.left=(e.clientX-20)+'px';
      cursorEl.style.top=(e.clientY-20)+'px';
    }
  }
});