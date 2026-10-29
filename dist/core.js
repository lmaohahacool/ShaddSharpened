import * as THREE from 'three';
import { BIOME } from './config.js';

export const urlParams=new URLSearchParams(window.location.search);
export const VERSION='Late Alpha 2.0.2';
export let SEED=parseInt(urlParams.get('seed'))||Math.floor(Math.random()*1000000);
export function setSeed(v){SEED=v}
export let BIOME_BLEND=parseInt(urlParams.get('blend'))||4;
export function setBiomeBlend(v){BIOME_BLEND=v}
export const SPAWN_BIOME_NAME=(urlParams.get('spawn')||'').toLowerCase();
export const SPAWN_BIOME=({forest:BIOME.FOREST,desert:BIOME.DESERT,mountain:BIOME.MOUNTAIN,plains:BIOME.PLAINS,snowy:BIOME.SNOWY,snow:BIOME.SNOWY,oldgrowth:BIOME.OLD_GROWTH,old_growth:BIOME.OLD_GROWTH,ogg:BIOME.OLD_GROWTH})[SPAWN_BIOME_NAME];

export let gamemode='creative';
export function setGamemodeState(v){gamemode=v}
export const blockChanges=new Map();
export const blockFacings=new Map();

export const scene=new THREE.Scene();
scene.background=new THREE.Color(0x87ceeb);
scene.fog=new THREE.Fog(0x87ceeb,48,72);
export const camera=new THREE.PerspectiveCamera(75,window.innerWidth/window.innerHeight,.1,1000);
export const renderer=new THREE.WebGLRenderer({antialias:true});
renderer.setSize(window.innerWidth,window.innerHeight);
renderer.setPixelRatio(Math.min(window.devicePixelRatio,2));
document.body.appendChild(renderer.domElement);

export const sunLight=new THREE.DirectionalLight(0xfff5d0,2);sunLight.position.set(50,100,30);scene.add(sunLight);
export const ambientLight=new THREE.AmbientLight(0xffffff,.9);scene.add(ambientLight);
export const hemiLight=new THREE.HemisphereLight(0x87ceeb,0x4caf50,.6);scene.add(hemiLight);

export const player={pos:new THREE.Vector3(0,50,0),vel:new THREE.Vector3(),yaw:0,pitch:0,onGround:false,health:20,maxHealth:20,hunger:20,maxHunger:20,hungerTimer:0,starveTimer:0,fallStartY:null,sneaking:false,sprinting:false,flying:false};
export const mobs=[];
export const particles=[];
export const torches=[];
export const chunks=new Map();
export const keys={};

export const inventory=new Array(36).fill(null).map(()=>({id:null,count:0}));
export let selectedIndex=0;
export function setSelectedIndex(v){selectedIndex=v}
export let cursorItem=null;
export function setCursorItem(v){cursorItem=v}

export const sunDirection=new THREE.Vector3(.4,.6,.5).normalize();
export const daySky=new THREE.Color(0x87ceeb);
export const nightSky=new THREE.Color(0x0a0a1a);
export const duskSky=new THREE.Color(0xff8844);

export let worldTime=600*.25;
export function setWorldTime(v){worldTime=v}

let isNightTime=false;
export function setIsNightTime(v){isNightTime=v}
export function getIsNightTime(){return isNightTime}

export const chunkBuildQueue=[];
export const queuedChunks=new Set();
export const chunkRebuildQueue=[];
export const queuedRebuilds=new Set();

export const caveCache=new Map();
export const oreCache=new Map();
export const treeCache=new Map();
export const cactusCache=new Map();

window.addEventListener('resize',()=>{camera.aspect=window.innerWidth/window.innerHeight;camera.updateProjectionMatrix();renderer.setSize(window.innerWidth,window.innerHeight)});