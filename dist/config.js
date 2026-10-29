export const BIOME={PLAINS:0,FOREST:1,MOUNTAIN:2,DESERT:3,SNOWY:4,OLD_GROWTH:5};
export const GRASS_COLORS={[BIOME.PLAINS]:0x91bd59,[BIOME.FOREST]:0x79c05a,[BIOME.MOUNTAIN]:0x8ab689,[BIOME.DESERT]:0xbfb755,[BIOME.SNOWY]:0x80b497,[BIOME.OLD_GROWTH]:0x4f7d33};
export const CHUNK_SIZE=16,RENDER_DISTANCE=5,SEA_LEVEL=0,WORLD_BOTTOM=-30;
export const BLOCK={
  AIR:0,GRASS:1,DIRT:2,STONE:3,SAND:4,WATER:5,SNOW:6,LOG:7,LEAVES:8,CACTUS:9,
  PLANKS:10,CRAFTING_TABLE:11,COBBLESTONE:12,COAL_ORE:13,IRON_ORE:14,FURNACE:15,
  WORKING_FURNACE:16,CHEST:17,DIRT_DARK:18,BED_FOOT:19,BED_HEAD:20,TORCH:21,ICE:22,
  WATER_FLOW_6:23,WATER_FLOW_5:24,WATER_FLOW_4:25,WATER_FLOW_3:26,WATER_FLOW_2:27,
  WATER_FLOW_1:28,WATER_FALLING:29,
  SHORT_GRASS:30,
  DOOR_BOTTOM_CLOSED:31,
  DOOR_TOP_CLOSED:32,
  DOOR_BOTTOM_OPEN:33,
  DOOR_TOP_OPEN:34
};
export const ITEM={STICK:'item_stick',WP:'item_wp',WA:'item_wa',SP:'item_sp',SA:'item_sa',BEEF:'item_beef',STEAK:'item_steak',COAL:'item_coal',RAW_IRON:'item_raw_iron',IRON_INGOT:'item_iron_ingot',ROTTEN_FLESH:'item_rotten_flesh'};
export const ATLAS_COLS=64,ATLAS_SIZE=16,ATLAS_WIDTH=ATLAS_COLS*ATLAS_SIZE,ATLAS_HEIGHT=ATLAS_SIZE;
export const BLOCK_TEXTURE_MAP={
  [BLOCK.GRASS]:{top:0,side:1,bottom:2,overlay:24},
  [BLOCK.DIRT]:{top:2,side:2,bottom:2},
  [BLOCK.DIRT_DARK]:{top:25,side:25,bottom:25},
  [BLOCK.STONE]:{top:3,side:3,bottom:3},
  [BLOCK.SAND]:{top:4,side:4,bottom:4},
  [BLOCK.WATER]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_6]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_5]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_4]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_3]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_2]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FLOW_1]:{top:5,side:5,bottom:5},
  [BLOCK.WATER_FALLING]:{top:5,side:5,bottom:5},
  [BLOCK.SNOW]:{top:6,side:6,bottom:6},
  [BLOCK.LOG]:{top:8,side:7,bottom:8},
  [BLOCK.LEAVES]:{top:9,side:9,bottom:9},
  [BLOCK.CACTUS]:{top:10,side:11,bottom:12},
  [BLOCK.PLANKS]:{top:13,side:13,bottom:13},
  [BLOCK.CRAFTING_TABLE]:{top:14,side:15,bottom:13},
  [BLOCK.COBBLESTONE]:{top:16,side:16,bottom:16},
  [BLOCK.COAL_ORE]:{top:17,side:17,bottom:17},
  [BLOCK.IRON_ORE]:{top:18,side:18,bottom:18},
  [BLOCK.FURNACE]:{top:19,side:19,bottom:19,front:20},
  [BLOCK.WORKING_FURNACE]:{top:19,side:19,bottom:19,front:21},
  [BLOCK.CHEST]:{top:22,side:22,bottom:22,front:23},
  [BLOCK.BED_FOOT]:{top:26,side:26,bottom:26},
  [BLOCK.BED_HEAD]:{top:27,side:27,bottom:27},
  [BLOCK.TORCH]:{top:28,side:28,bottom:28},
  [BLOCK.ICE]:{top:29,side:29,bottom:29},
  [BLOCK.SHORT_GRASS]:{top:30,side:30,bottom:30},
  // Doors — top/bottom textures for front/back faces, sides for thin edges
  [BLOCK.DOOR_BOTTOM_CLOSED]:{top:33,side:33,bottom:33,front:32},
  [BLOCK.DOOR_TOP_CLOSED]:{top:33,side:33,bottom:33,front:31},
  [BLOCK.DOOR_BOTTOM_OPEN]:{top:33,side:33,bottom:33,front:32},
  [BLOCK.DOOR_TOP_OPEN]:{top:33,side:33,bottom:33,front:31}
};
export const CACTUS_WIDTH=.8;

export const WATER_HEIGHTS={
  [BLOCK.WATER]:0.8,
  [BLOCK.WATER_FLOW_6]:0.7,
  [BLOCK.WATER_FLOW_5]:0.6,
  [BLOCK.WATER_FLOW_4]:0.5,
  [BLOCK.WATER_FLOW_3]:0.4,
  [BLOCK.WATER_FLOW_2]:0.3,
  [BLOCK.WATER_FLOW_1]:0.2,
  [BLOCK.WATER_FALLING]:1.0
};

export function isWaterBlock(id){
  return id===BLOCK.WATER
    || id===BLOCK.WATER_FLOW_6
    || id===BLOCK.WATER_FLOW_5
    || id===BLOCK.WATER_FLOW_4
    || id===BLOCK.WATER_FLOW_3
    || id===BLOCK.WATER_FLOW_2
    || id===BLOCK.WATER_FLOW_1
    || id===BLOCK.WATER_FALLING;
}

export function isDoorBlock(id){
  return id===BLOCK.DOOR_BOTTOM_CLOSED
    || id===BLOCK.DOOR_TOP_CLOSED
    || id===BLOCK.DOOR_BOTTOM_OPEN
    || id===BLOCK.DOOR_TOP_OPEN;
}

export function isDoorOpen(id){
  return id===BLOCK.DOOR_BOTTOM_OPEN || id===BLOCK.DOOR_TOP_OPEN;
}

export function getWaterHeight(id){
  return WATER_HEIGHTS[id]!==undefined?WATER_HEIGHTS[id]:1;
}

export function getWaterLevel(id){
  if(id===BLOCK.WATER)return 7;
  if(id===BLOCK.WATER_FLOW_6)return 6;
  if(id===BLOCK.WATER_FLOW_5)return 5;
  if(id===BLOCK.WATER_FLOW_4)return 4;
  if(id===BLOCK.WATER_FLOW_3)return 3;
  if(id===BLOCK.WATER_FLOW_2)return 2;
  if(id===BLOCK.WATER_FLOW_1)return 1;
  if(id===BLOCK.WATER_FALLING)return 8;
  return 0;
}

export function getWaterBlockFromLevel(level){
  if(level>=7)return BLOCK.WATER;
  if(level===6)return BLOCK.WATER_FLOW_6;
  if(level===5)return BLOCK.WATER_FLOW_5;
  if(level===4)return BLOCK.WATER_FLOW_4;
  if(level===3)return BLOCK.WATER_FLOW_3;
  if(level===2)return BLOCK.WATER_FLOW_2;
  if(level===1)return BLOCK.WATER_FLOW_1;
  return BLOCK.AIR;
}

export const BLOCK_INFO={
  [BLOCK.AIR]:{name:"Air",color:"#000000",hardness:0},
  [BLOCK.GRASS]:{name:"Grass",color:"#4caf50",hardness:.6},
  [BLOCK.DIRT]:{name:"Dirt",color:"#8b5a2b",hardness:.5},
  [BLOCK.DIRT_DARK]:{name:"Dark Dirt",color:"#4a2c15",hardness:.5},
  [BLOCK.STONE]:{name:"Stone",color:"#808080",hardness:1.5},
  [BLOCK.SAND]:{name:"Sand",color:"#f4e4a0",hardness:.5},
  [BLOCK.WATER]:{name:"Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_6]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_5]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_4]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_3]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_2]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FLOW_1]:{name:"Flowing Water",color:"#2a5db0",hardness:999},
  [BLOCK.WATER_FALLING]:{name:"Falling Water",color:"#2a5db0",hardness:999},
  [BLOCK.SNOW]:{name:"Snow",color:"#ffffff",hardness:.3},
  [BLOCK.LOG]:{name:"Wood",color:"#6b4423",hardness:1},
  [BLOCK.LEAVES]:{name:"Leaves",color:"#3a7d3a",hardness:.2},
  [BLOCK.CACTUS]:{name:"Cactus",color:"#2d7a2d",hardness:.4},
  [BLOCK.PLANKS]:{name:"Planks",color:"#b58a5a",hardness:.8},
  [BLOCK.CRAFTING_TABLE]:{name:"Crafting Table",color:"#8b5a2b",hardness:1.2},
  [BLOCK.COBBLESTONE]:{name:"Cobblestone",color:"#7a7a7a",hardness:2},
  [BLOCK.COAL_ORE]:{name:"Coal Ore",color:"#2a2a2a",hardness:2},
  [BLOCK.IRON_ORE]:{name:"Iron Ore",color:"#c8a890",hardness:2.5},
  [BLOCK.FURNACE]:{name:"Furnace",color:"#666666",hardness:2.5},
  [BLOCK.WORKING_FURNACE]:{name:"Furnace",color:"#666666",hardness:2.5},
  [BLOCK.CHEST]:{name:"Chest",color:"#8b5a2b",hardness:2.5},
  [BLOCK.BED_FOOT]:{name:"Bed",color:"#a02a2a",hardness:.4},
  [BLOCK.BED_HEAD]:{name:"Bed",color:"#ffffff",hardness:.4},
  [BLOCK.TORCH]:{name:"Torch",color:"#ffcc44",hardness:.2},
  [BLOCK.ICE]:{name:"Ice",color:"#7fc8e8",hardness:.5},
  [BLOCK.SHORT_GRASS]:{name:"Short Grass",color:"#4caf50",hardness:0},
  [BLOCK.DOOR_BOTTOM_CLOSED]:{name:"Door",color:"#8b5a2b",hardness:.8},
  [BLOCK.DOOR_TOP_CLOSED]:{name:"Door",color:"#8b5a2b",hardness:.8},
  [BLOCK.DOOR_BOTTOM_OPEN]:{name:"Door",color:"#8b5a2b",hardness:.8},
  [BLOCK.DOOR_TOP_OPEN]:{name:"Door",color:"#8b5a2b",hardness:.8}
};
export const ITEM_INFO={[ITEM.STICK]:{name:"Stick",color:"#8b5a2b"},[ITEM.WP]:{name:"Wooden Pickaxe",color:"#b58a5a",durability:60,tool:'pickaxe'},[ITEM.WA]:{name:"Wooden Axe",color:"#b58a5a",durability:60,tool:'axe'},[ITEM.SP]:{name:"Stone Pickaxe",color:"#808080",durability:130,tool:'pickaxe'},[ITEM.SA]:{name:"Stone Axe",color:"#808080",durability:130,tool:'axe'},[ITEM.BEEF]:{name:"Raw Beef",color:"#cc4444",food:4},[ITEM.STEAK]:{name:"Steak",color:"#8b3a1a",food:8},[ITEM.COAL]:{name:"Coal",color:"#1a1a1a",fuel:400},[ITEM.RAW_IRON]:{name:"Raw Iron",color:"#c8a890"},[ITEM.IRON_INGOT]:{name:"Iron Ingot",color:"#d0d0d0"},[ITEM.ROTTEN_FLESH]:{name:"Rotten Flesh",color:"#6b7a2e",food:2}};
export const TOOL_EFFECT={pickaxe:[BLOCK.STONE,BLOCK.COBBLESTONE,BLOCK.CRAFTING_TABLE,BLOCK.COAL_ORE,BLOCK.IRON_ORE,BLOCK.FURNACE,BLOCK.WORKING_FURNACE,BLOCK.CHEST,BLOCK.ICE],axe:[BLOCK.LOG,BLOCK.PLANKS,BLOCK.DOOR_BOTTOM_CLOSED,BLOCK.DOOR_TOP_CLOSED,BLOCK.DOOR_BOTTOM_OPEN,BLOCK.DOOR_TOP_OPEN]};
export const TOOL_REQUIRED_LEVEL={[BLOCK.STONE]:1,[BLOCK.COBBLESTONE]:1,[BLOCK.COAL_ORE]:1,[BLOCK.IRON_ORE]:2,[BLOCK.FURNACE]:1,[BLOCK.WORKING_FURNACE]:1,[BLOCK.CHEST]:1};
export const TOOL_TIER={pickaxe:{[ITEM.WP]:1,[ITEM.SP]:2},axe:{[ITEM.WA]:1,[ITEM.SA]:2}};
export const FUEL_VALUES={[ITEM.COAL]:400,[BLOCK.PLANKS]:300,[BLOCK.LOG]:250};
export const SMELT_RECIPES={[ITEM.RAW_IRON]:{output:ITEM.IRON_INGOT,time:10},[ITEM.BEEF]:{output:ITEM.STEAK,time:8}};
export const HOTBAR_SIZE=9,STORAGE_SIZE=27,TOTAL_SLOTS=HOTBAR_SIZE+STORAGE_SIZE,INVENTORY_MAX_STACK=64;
export const DAY_LENGTH=600;
export const PLAYER_HEIGHT=1.8,PLAYER_EYE=1.6,WALK_SPEED=6,SPRINT_SPEED=9,SNEAK_SPEED=2.5,GRAVITY=25,JUMP_VELOCITY=9,FLY_SPEED=12;
export const MOB_GRAVITY=25;
export const TREE_GRID=12;
export const CACTUS_GRID=14,CACTUS_CHANCE=.35;
export const UV_INSET=.5/ATLAS_SIZE/ATLAS_COLS;
export const textureFiles=[
  'textures/grass_top.png','textures/grass_side.png','textures/dirt.png','textures/stone.png',
  'textures/sand.png','textures/water.png','textures/snow.png','textures/wood_side.png',
  'textures/wood_top.png','textures/leaves.png','textures/cactus_top.png','textures/cactus_side.png',
  'textures/cactus_bottom.png','textures/planks.png','textures/ct_top.png','textures/ct_side.png',
  'textures/cobblestone.png','textures/coal_ore.png','textures/iron_ore.png','textures/furnace_tbs.png',
  'textures/furnace_front.png','textures/wfurnace_front.png','textures/chest_tbs.png','textures/chest_front.png',
  'textures/grass_side_overlay.png','textures/dirt_dark.png','textures/bed_red.png','textures/bed_white.png',
  'textures/torch.png','textures/ice.png',
  'textures/short_grass.png',
  'textures/door_top.png','textures/door_bottom.png','textures/door_sides.png'
];
export function isItem(id){return typeof id==='string'}
export function getInfo(id){return isItem(id)?ITEM_INFO[id]:BLOCK_INFO[id]}
export function requiresTool(blockId){return TOOL_REQUIRED_LEVEL[blockId]!==undefined}
export function getMiningSpeed(blockId,itemId){const info=itemId&&isItem(itemId)?ITEM_INFO[itemId]:null;if(!info||!info.tool)return 1;const list=TOOL_EFFECT[info.tool]||[];if(!list.includes(blockId))return 1;if(info.tool==='pickaxe')return 6;if(info.tool==='axe')return 3;return 1}
export const CRAFT_RECIPES=[
  {pattern:[[BLOCK.LOG]],result:{id:BLOCK.PLANKS,count:4}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,BLOCK.PLANKS]],result:{id:BLOCK.CRAFTING_TABLE,count:1}},
  {pattern:[[BLOCK.PLANKS],[BLOCK.PLANKS]],result:{id:ITEM.STICK,count:2}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS,BLOCK.PLANKS],[null,ITEM.STICK,null],[null,ITEM.STICK,null]],result:{id:ITEM.WP,count:1}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,ITEM.STICK],[null,ITEM.STICK]],result:{id:ITEM.WA,count:1}},
  {pattern:[[BLOCK.COBBLESTONE,BLOCK.COBBLESTONE,BLOCK.COBBLESTONE],[null,ITEM.STICK,null],[null,ITEM.STICK,null]],result:{id:ITEM.SP,count:1}},
  {pattern:[[BLOCK.COBBLESTONE,BLOCK.COBBLESTONE],[BLOCK.COBBLESTONE,ITEM.STICK],[null,ITEM.STICK]],result:{id:ITEM.SA,count:1}},
  {pattern:[[BLOCK.COBBLESTONE,BLOCK.COBBLESTONE,BLOCK.COBBLESTONE],[BLOCK.COBBLESTONE,null,BLOCK.COBBLESTONE],[BLOCK.COBBLESTONE,BLOCK.COBBLESTONE,BLOCK.COBBLESTONE]],result:{id:BLOCK.FURNACE,count:1}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,null,BLOCK.PLANKS],[BLOCK.PLANKS,BLOCK.PLANKS,BLOCK.PLANKS]],result:{id:BLOCK.CHEST,count:1}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,BLOCK.PLANKS]],result:{id:BLOCK.BED_FOOT,count:1}},
  {pattern:[[ITEM.COAL],[ITEM.STICK]],result:{id:BLOCK.TORCH,count:4}},
  {pattern:[[BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,BLOCK.PLANKS],[BLOCK.PLANKS,BLOCK.PLANKS]],result:{id:BLOCK.DOOR_BOTTOM_CLOSED,count:1}}
];