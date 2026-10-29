import * as THREE from 'three';
import { ATLAS_COLS, ATLAS_SIZE, ATLAS_WIDTH, ATLAS_HEIGHT, UV_INSET, BLOCK_TEXTURE_MAP, textureFiles, BLOCK_INFO, ITEM_INFO, isItem, getInfo } from './config.js';

export const atlasCanvas=document.createElement('canvas');
atlasCanvas.width=ATLAS_WIDTH;atlasCanvas.height=ATLAS_HEIGHT;
export const atlasCtx=atlasCanvas.getContext('2d');
atlasCtx.imageSmoothingEnabled=false;atlasCtx.clearRect(0,0,ATLAS_WIDTH,ATLAS_HEIGHT);

export const material=new THREE.MeshLambertMaterial({side:THREE.FrontSide,transparent:false,alphaTest:.5,depthWrite:true,vertexColors:true});
export const overlayMaterial=new THREE.MeshLambertMaterial({side:THREE.FrontSide,transparent:true,alphaTest:.5,depthWrite:true,vertexColors:true});
export const plantMaterial=new THREE.MeshLambertMaterial({side:THREE.DoubleSide,transparent:true,alphaTest:.5,depthWrite:true,vertexColors:true});

export const itemTextures={};
export let iconAtlasURL='';
export function getIconAtlasURL(){return iconAtlasURL}
export function buildIconAtlas(){const SCALE=3,c=document.createElement('canvas');c.width=ATLAS_WIDTH*SCALE;c.height=ATLAS_HEIGHT*SCALE;const ctx=c.getContext('2d');ctx.imageSmoothingEnabled=false;ctx.drawImage(atlasCanvas,0,0,c.width,c.height);iconAtlasURL=c.toDataURL()}

export function blockIconStyle(blockId){const T=BLOCK_TEXTURE_MAP[blockId];if(!T)return'';const ti=T.side,SCALE=3,cellX=ti*ATLAS_SIZE*SCALE,url=`url('${iconAtlasURL}')`,size=ATLAS_WIDTH*SCALE;return`background-image:${url};background-size:${size}px ${ATLAS_HEIGHT*SCALE}px;background-position:-${cellX}px 0;background-repeat:no-repeat;width:36px;height:36px`}
export function getIconHTML(id){if(isItem(id)){const url=itemTextures[id];if(url)return`<div class="item-icon" style="background-image:url('${url}')"></div>`;return`<div class="item-icon" style="background-color:${getInfo(id).color}"></div>`}const style=blockIconStyle(id);if(style)return`<div class="slot-icon" style="${style}"></div>`;return`<div class="slot-icon" style="background-color:${getInfo(id).color};border-radius:2px"></div>`}
export function getDurabilityHTML(item){if(!item||!isItem(item.id))return'';const info=ITEM_INFO[item.id];if(!info||!info.durability)return'';const cur=item.durability!==undefined?item.durability:info.durability,pct=Math.max(0,Math.min(1,cur/info.durability));return`<div class="durability"><div class="durability-fill" style="width:${pct*100}%;background:${pct>.5?'#4caf50':pct>.25?'#ffaa00':'#ff3333'}"></div></div>`}

let loadedCount=0;
const totalTextures=textureFiles.length;

function loadItemTextures(cb){const list=[{key:'item_stick',path:'textures/sticks.png'},{key:'item_wp',path:'textures/wp.png'},{key:'item_wa',path:'textures/wa.png'},{key:'item_sp',path:'textures/sp.png'},{key:'item_sa',path:'textures/sa.png'},{key:'item_beef',path:'textures/beef.png'},{key:'item_steak',path:'textures/steak.png'},{key:'item_coal',path:'textures/coal.png'},{key:'item_raw_iron',path:'textures/raw_iron.png'},{key:'item_iron_ingot',path:'textures/iron_ingot.png'},{key:'item_rotten_flesh',path:'textures/rotten_flesh.png'}];let remaining=list.length;if(remaining===0){cb();return}list.forEach(t=>{const img=new Image();img.onload=()=>{itemTextures[t.key]=img.src;remaining--;if(remaining<=0)cb()};img.onerror=()=>{console.warn('missing texture: '+t.path);remaining--;if(remaining<=0)cb()};img.src=t.path+'?v='+Date.now()})}

function tryFinish(cb){loadedCount++;if(loadedCount>=totalTextures){const tex=new THREE.CanvasTexture(atlasCanvas);tex.magFilter=THREE.NearestFilter;tex.minFilter=THREE.NearestFilter;tex.colorSpace=THREE.SRGBColorSpace;tex.needsUpdate=true;material.map=tex;material.needsUpdate=true;overlayMaterial.map=tex;overlayMaterial.needsUpdate=true;plantMaterial.map=tex;plantMaterial.needsUpdate=true;buildIconAtlas();loadItemTextures(cb)}}

export function loadAllTextures(cb){
  textureFiles.forEach((path,i)=>{const img=new Image();img.crossOrigin='anonymous';img.onload=()=>{atlasCtx.drawImage(img,i*ATLAS_SIZE,0,ATLAS_SIZE,ATLAS_SIZE);tryFinish(cb)};img.onerror=()=>{console.error('FAILED: '+path);tryFinish(cb)};img.src=path+'?v='+Date.now()});
}