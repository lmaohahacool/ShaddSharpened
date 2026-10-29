export const soundBreak=new Audio('sounds/break.mp3');
export const soundPlace=new Audio('sounds/place.mp3');
export const soundFootstep=new Audio('sounds/footstep.mp3');
export const soundHit=new Audio('sounds/hit.mp3');
export const soundEat=new Audio('sounds/eating.mp3');
export const music=new Audio('sounds/bg_music.mp3');
soundBreak.volume=.5;
soundPlace.volume=.5;
soundFootstep.volume=.2;
soundHit.volume=.6;
soundEat.volume=.6;
music.volume=.3;
music.loop=true;
export let musicEnabled=true;
export function playSound(s){try{s.currentTime=0;s.play().catch(()=>{})}catch(e){}}
export function toggleMusic(){musicEnabled=!musicEnabled;if(musicEnabled)music.play().catch(()=>{});else music.pause();const el=document.getElementById('music-state');if(el){el.textContent=musicEnabled?'♪ Music: ON':'♪ Music: OFF';el.className=musicEnabled?'music':'music off'}}
export function setMusicEnabled(v){musicEnabled=v}