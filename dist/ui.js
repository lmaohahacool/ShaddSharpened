import { HOTBAR_SIZE, TOTAL_SLOTS } from './config.js';
import { gamemode, setGamemodeState, player, inventory, selectedIndex } from './core.js';
import { getIconHTML } from './textures.js';

export function setGamemode(mode){
  setGamemodeState(mode);
  player.flying=false;
  player.health=player.maxHealth;
  player.hunger=player.maxHunger;
  document.getElementById('mode-state').textContent=`Mode: ${mode==='creative'?'Creative':'Survival'}`;
  document.getElementById('btn-creative').classList.toggle('active',mode==='creative');
  document.getElementById('btn-survival').classList.toggle('active',mode==='survival');
  for(let i=0;i<TOTAL_SLOTS;i++)inventory[i]={id:null,count:0};

  // Rebuild hotbar cleanly
  const hotbarEl=document.getElementById('hotbar');
  if(hotbarEl){
    hotbarEl.innerHTML='';
    for(let i=0;i<HOTBAR_SIZE;i++){
      const s=document.createElement('div');
      s.className='slot'+(i===selectedIndex?' selected':'');
      s.innerHTML=`<div class="slot-label">${(i+1)%10}</div>`;
      hotbarEl.appendChild(s);
    }
  }

  updateHealthBar();
  updateHungerBar();

  document.getElementById('gamemode-menu').style.display='none';
  if(!document.pointerLockElement)document.querySelector('canvas').requestPointerLock().catch(()=>{});
}

export function openGamemodeMenu(){
  document.getElementById('gamemode-menu').style.display='block';
  if(document.pointerLockElement)document.exitPointerLock();
}

export function closeGamemodeMenu(){
  document.getElementById('gamemode-menu').style.display='none';
  document.querySelector('canvas').requestPointerLock().catch(()=>{});
}

export function updateHealthBar(){
  const bar=document.getElementById('health-bar');
  if(gamemode==='creative'){bar.style.display='none';return}
  bar.style.display='flex';bar.innerHTML='';
  const hearts=Math.ceil(player.maxHealth/2);
  for(let i=0;i<hearts;i++){
    const heart=document.createElement('div'),hp=player.health-i*2;
    heart.className='heart'+(hp<=0?' empty':'');
    bar.appendChild(heart);
  }
}

export function updateHungerBar(){
  const bar=document.getElementById('hunger-bar');
  if(gamemode==='creative'){bar.style.display='none';return}
  bar.style.display='flex';bar.innerHTML='';
  const total=Math.ceil(player.maxHunger/2);
  for(let i=0;i<total;i++){
    const d=document.createElement('div'),hv=player.hunger-i*2;
    d.className='drumstick'+(hv<=0?' empty':'');
    bar.appendChild(d);
  }
}

export function updateHunger(dt){
  if(gamemode!=='survival')return;
  if(player.health<=0)return;
  player.hungerTimer+=dt;
  if(player.hungerTimer>=8){
    player.hungerTimer=0;
    if(player.hunger>0){player.hunger=Math.max(0,player.hunger-1);updateHungerBar()}
  }
  if(player.hunger<=0){
    player.starveTimer+=dt;
    if(player.starveTimer>=2){
      player.starveTimer=0;
      player.health-=1;
      if(player.health<0)player.health=0;
      updateHealthBar();
      if(player.health<=0)die();
    }
  }else player.starveTimer=0;
}

export function damagePlayer(amount){
  if(gamemode==='creative')return;
  if(player.health<=0)return;
  player.health-=amount;
  if(player.health<0)player.health=0;
  updateHealthBar();
  if(player.health<=0)die();
}

export function die(){
  document.getElementById('death-screen').style.display='flex';
  if(document.pointerLockElement)document.exitPointerLock();
}

export function showDeathScreen(){
  document.getElementById('death-score').textContent='Score: 0';
  document.getElementById('death-screen').style.display='flex';
  if(document.pointerLockElement)document.exitPointerLock();
}

export function hideDeathScreen(){
  document.getElementById('death-screen').style.display='none';
  document.querySelector('canvas').requestPointerLock().catch(()=>{});
}