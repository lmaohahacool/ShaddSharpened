import * as THREE from 'three';
import { DAY_LENGTH } from './config.js';
import { scene, sunDirection, daySky, nightSky, duskSky, sunLight, ambientLight, hemiLight, camera, setIsNightTime } from './core.js';
import { smoothstep } from './math.js';

let worldTime=DAY_LENGTH*.25;
export function getWorldTime(){return worldTime}
export function setWorldTimeGlobal(v){worldTime=v}

let sunSprite=null,moonSprite=null;

function buildSunMoon(){
  const sunCanvas=document.createElement('canvas');sunCanvas.width=128;sunCanvas.height=128;
  const sunCtx=sunCanvas.getContext('2d');
  const sunGrad=sunCtx.createRadialGradient(64,64,8,64,64,64);
  sunGrad.addColorStop(0,'rgba(255,255,240,1)');
  sunGrad.addColorStop(.5,'rgba(255,255,200,1)');
  sunGrad.addColorStop(.8,'rgba(255,250,180,.5)');
  sunGrad.addColorStop(1,'rgba(255,250,180,0)');
  sunCtx.fillStyle=sunGrad;sunCtx.fillRect(0,0,128,128);
  sunSprite=new THREE.Sprite(new THREE.SpriteMaterial({map:new THREE.CanvasTexture(sunCanvas),transparent:true,depthWrite:false,fog:false}));
  sunSprite.scale.set(100,100,1);
  sunSprite.renderOrder=-1;
  scene.add(sunSprite);

  const moonCanvas=document.createElement('canvas');moonCanvas.width=128;moonCanvas.height=128;
  const moonCtx=moonCanvas.getContext('2d');
  const moonGrad=moonCtx.createRadialGradient(64,64,8,64,64,64);
  moonGrad.addColorStop(0,'rgba(240,240,255,1)');
  moonGrad.addColorStop(.6,'rgba(200,200,220,.7)');
  moonGrad.addColorStop(1,'rgba(200,200,220,0)');
  moonCtx.fillStyle=moonGrad;
  moonCtx.beginPath();moonCtx.arc(64,64,50,0,Math.PI*2);moonCtx.fill();
  moonSprite=new THREE.Sprite(new THREE.SpriteMaterial({map:new THREE.CanvasTexture(moonCanvas),transparent:true,depthWrite:false,fog:false}));
  moonSprite.scale.set(80,80,1);
  moonSprite.renderOrder=-1;
  scene.add(moonSprite);
}
buildSunMoon();

export function updateSun(){
  if(!sunSprite||!moonSprite)return;
  sunSprite.position.copy(camera.position).addScaledVector(sunDirection,500);
  moonSprite.position.copy(camera.position).addScaledVector(sunDirection,-500);
}

export function updateDayNight(dt){
  worldTime=(worldTime+dt)%DAY_LENGTH;
  const t=worldTime/DAY_LENGTH,angle=t*Math.PI*2-Math.PI/2;
  sunDirection.set(Math.cos(angle)*.4,Math.sin(angle),Math.cos(angle)*.5).normalize();
  const sh=Math.max(0,sunDirection.y),isNight=sunDirection.y<-.1;
  setIsNightTime(isNight);
  let sky;
  if(sunDirection.y<-.1)sky=nightSky.clone();
  else if(sunDirection.y<.15)sky=nightSky.clone().lerp(duskSky,smoothstep((sunDirection.y+.1)/.25));
  else if(sunDirection.y<.4)sky=duskSky.clone().lerp(daySky,smoothstep((sunDirection.y-.15)/.25));
  else sky=daySky.clone();
  scene.background=sky;
  if(scene.fog)scene.fog.color=sky;
  sunLight.intensity=Math.max(.05,sh*2.5);
  ambientLight.intensity=.15+sh*.9;
  hemiLight.intensity=.1+sh*.6;
  const el=document.getElementById('time-state');
  if(el)el.textContent=isNight?'Time: Night':sunDirection.y<.4?'Time: Dusk/Dawn':'Time: Day';
  if(sunSprite){sunSprite.visible=sunDirection.y>-.15;sunSprite.material.opacity=Math.min(1,Math.max(0,sunDirection.y*4+.3))}
  if(moonSprite){moonSprite.visible=sunDirection.y<.15;moonSprite.material.opacity=Math.min(1,Math.max(0,-sunDirection.y*4+.3))}
  updateSun();
}