import { HOTBAR_SIZE, TOTAL_SLOTS, BLOCK, ITEM, CRAFT_RECIPES, isItem, getInfo, INVENTORY_MAX_STACK } from './config.js';
import { inventory as inv, selectedIndex, cursorItem, setCursorItem, gamemode } from './core.js';
import { getIconHTML, getDurabilityHTML } from './textures.js';
import { clickSlot, swipePlace, rebuildHotbar, addToInventory, makeItem, updateCursorDisplay } from './inventory.js';

export let craftingGridSlots=new Array(4).fill(null).map(()=>({id:null,count:0}));
export function setCraftingGridSlots(v){craftingGridSlots=v}

export let craftingOpen=false;
export let inventoryOpen=false;
export function setCraftingOpen(v){craftingOpen=v}
export function setInventoryOpen(v){inventoryOpen=v}
export function isCraftingOpen(){return craftingOpen}
export function isInventoryOpen(){return inventoryOpen}

export function matchRecipe(grid,size){
  for(const r of CRAFT_RECIPES){
    const ph=r.pattern.length,pw=r.pattern[0].length;
    if(ph>size||pw>size)continue;
    for(let oy=0;oy<=size-ph;oy++)for(let ox=0;ox<=size-pw;ox++){
      let ok=true;
      for(let y=0;y<ph&&ok;y++)for(let x=0;x<pw;x++){
        const want=r.pattern[y][x],have=grid[(oy+y)*size+(ox+x)];
        if(want===null){if(have!==null){ok=false;break}}
        else{if(want!==have){ok=false;break}}
      }
      if(!ok)continue;
      let clean=true;
      for(let y=0;y<size&&clean;y++)for(let x=0;x<size;x++){
        const inside=(y>=oy&&y<oy+ph&&x>=ox&&x<ox+pw);
        if(inside)continue;
        if(grid[y*size+x]!==null){clean=false;break}
      }
      if(clean)return r.result;
    }
  }
  return null;
}

export function slotHTMLContent(item){
  if(!item||item.id===null||item.count<=0)return'';
  return`${getIconHTML(item.id)}${getDurabilityHTML(item)}<div class="slot-count">${item.count}</div>`;
}

export function makeSlotEl(slotArray,idx,selected){
  const s=document.createElement('div');
  s.className='inv-slot'+(selected?' selected-slot':'');
  const item=slotArray[idx];
  s.innerHTML=slotHTMLContent(item);
  s.addEventListener('mousedown',e=>{
    e.preventDefault();e.stopPropagation();
    clickSlot(()=>slotArray[idx],v=>slotArray[idx]=v,e.button===2,e.shiftKey);
    refreshUI();
  });
  s.addEventListener('contextmenu',e=>e.preventDefault());
  return s;
}

export function makeCraftingSlotEl(slotArray,idx,size){
  const s=document.createElement('div');
  s.className='inv-slot';
  const item=slotArray[idx];
  s.innerHTML=slotHTMLContent(item);
  s.addEventListener('mousedown',e=>{
    e.preventDefault();e.stopPropagation();
    if(e.button===2){
      clickSlot(()=>slotArray[idx],v=>slotArray[idx]=v,true,e.shiftKey);
      refreshUI();
      return;
    }
    window._swiping=true;
    window._swipedSlots=new Set();
    swipePlace(()=>slotArray[idx],v=>slotArray[idx]=v,`c${idx}`);
    refreshUI();
  });
  s.addEventListener('mouseenter',()=>{
    if(window._swiping){
      swipePlace(()=>slotArray[idx],v=>slotArray[idx]=v,`c${idx}`);
      refreshUI();
    }
  });
  s.addEventListener('contextmenu',e=>e.preventDefault());
  return s;
}

export function renderCraftingGrid(container,size,grid){
  container.innerHTML='';
  for(let i=0;i<size*size;i++)container.appendChild(makeCraftingSlotEl(grid,i,size));
}

export function renderOutput(container,size,grid){
  container.innerHTML='';
  const g=[];
  for(let i=0;i<size*size;i++){const it=grid[i];g.push(it&&it.id!==null?it.id:null)}
  const r=matchRecipe(g,size);
  if(r)container.innerHTML=`${getIconHTML(r.id)}<div class="slot-count">${r.count}</div>`;
  container.onmousedown=e=>{
    e.preventDefault();e.stopPropagation();
    if(cursorItem!==null&&cursorItem.id!==null)return;
    const g2=[];
    for(let i=0;i<size*size;i++){const it=grid[i];g2.push(it&&it.id!==null?it.id:null)}
    const rec=matchRecipe(g2,size);
    if(!rec)return;
    if(e.button===2){setCursorItem(makeItem(rec.id,1))}
    else{setCursorItem(makeItem(rec.id,rec.count))}
    for(let i=0;i<size*size;i++){const it=grid[i];if(it&&it.id!==null){it.count--;if(it.count<=0)grid[i]={id:null,count:0}}}
    updateCursorDisplay();
    refreshUI();
  };
}

export function renderInventory(){
  document.getElementById('inv-title').textContent='Inventory';
  const ca=document.getElementById('crafting-area');
  ca.innerHTML='<div id="crafting-grid-2x2"></div><div id="crafting-arrow">→</div><div id="crafting-output"></div>';
  if(craftingGridSlots.length!==4)craftingGridSlots=new Array(4).fill(null).map(()=>({id:null,count:0}));
  renderCraftingGrid(document.getElementById('crafting-grid-2x2'),2,craftingGridSlots);
  renderOutput(document.getElementById('crafting-output'),2,craftingGridSlots);
  const ig=document.getElementById('inv-grid'),ihg=document.getElementById('inv-hotbar-grid');
  ig.innerHTML='';ihg.innerHTML='';
  for(let i=HOTBAR_SIZE;i<TOTAL_SLOTS;i++)ig.appendChild(makeSlotEl(inv,i,false));
  for(let i=0;i<HOTBAR_SIZE;i++)ihg.appendChild(makeSlotEl(inv,i,i===selectedIndex));
  if(typeof window._renderCreativePanel==='function')window._renderCreativePanel();
}

export function renderCraftingTable(){
  document.getElementById('inv-title').textContent='Crafting Table';
  const ca=document.getElementById('crafting-area');
  ca.innerHTML='<div id="crafting-grid-3x3"></div><div id="crafting-arrow">→</div><div id="crafting-output"></div>';
  if(craftingGridSlots.length!==9)craftingGridSlots=new Array(9).fill(null).map(()=>({id:null,count:0}));
  renderCraftingGrid(document.getElementById('crafting-grid-3x3'),3,craftingGridSlots);
  renderOutput(document.getElementById('crafting-output'),3,craftingGridSlots);
  const ig=document.getElementById('inv-grid'),ihg=document.getElementById('inv-hotbar-grid');
  ig.innerHTML='';ihg.innerHTML='';
  for(let i=HOTBAR_SIZE;i<TOTAL_SLOTS;i++)ig.appendChild(makeSlotEl(inv,i,false));
  for(let i=0;i<HOTBAR_SIZE;i++)ihg.appendChild(makeSlotEl(inv,i,i===selectedIndex));
  if(typeof window._renderCreativePanel==='function')window._renderCreativePanel();
}

export function openInventory(){
  inventoryOpen=true;craftingOpen=false;
  craftingGridSlots=new Array(4).fill(null).map(()=>({id:null,count:0}));
  document.getElementById('inv-screen').style.display='flex';
  renderInventory();
  if(document.pointerLockElement)document.exitPointerLock();
}

export function openCraftingTable(){
  inventoryOpen=true;craftingOpen=true;
  craftingGridSlots=new Array(9).fill(null).map(()=>({id:null,count:0}));
  document.getElementById('inv-screen').style.display='flex';
  renderCraftingTable();
  if(document.pointerLockElement)document.exitPointerLock();
}

export function closeInventory(){
  if(cursorItem&&cursorItem.id!==null){
    addToInventory(cursorItem.id,cursorItem.count,cursorItem.durability);
    setCursorItem(null);
    updateCursorDisplay();
  }
  // Return crafting grid items
  for(let i=0;i<craftingGridSlots.length;i++){
    const it=craftingGridSlots[i];
    if(it&&it.id!==null&&it.count>0){addToInventory(it.id,it.count,it.durability);craftingGridSlots[i]={id:null,count:0}}
  }
  window._swiping=false;
  if(window._swipedSlots)window._swipedSlots.clear();
  inventoryOpen=false;craftingOpen=false;
  document.getElementById('inv-screen').style.display='none';
  rebuildHotbar();
  document.getElementById('click-hint').style.display='none';
  document.getElementById('crosshair').style.display='block';
  if(!document.pointerLockElement)document.querySelector('canvas').requestPointerLock().catch(()=>{});
}

export function refreshUI(){
  if(typeof window.refreshUI==='function'){window.refreshUI();return}
  if(craftingOpen)renderCraftingTable();
  else renderInventory();
  rebuildHotbar();
}