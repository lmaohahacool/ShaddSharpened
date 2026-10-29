# Shaft Additionality — JS→C# Migration Brief
Prepared from a direct read of `dist/` at commit a426609. All numbers below were
measured by executing the real `math.js` functions, not estimated.

## 1. Source architecture (as found)

| File | Lines | Role |
|---|---|---|
| main.js | 557 | rAF loop (line 228), orchestrator |
| chunks.js | 528 | Mesher: per-face, 3 geometries/chunk (opaque, overlay, plants) |
| worldgen.js | 355 | `getHeight`, `getBiomeRaw`, caves/ores/trees/cactus |
| water.js | 266 | Level-based cellular automaton, 10 Hz, 500 updates/tick |
| config.js | 195 | BLOCK/ITEM ids, atlas, recipes, tuning |
| math.js | 6 | hash2, hash3, noise2D/3D, fbm, fbm3D, smoothstep |
| core.js | 65 | Shared mutable singletons, THREE scene/renderer/camera |

Constants: `CHUNK_SIZE=16`, `RENDER_DISTANCE=5`, `SEA_LEVEL=0`,
`WORLD_BOTTOM=-30`, `DAY_LENGTH=600`.

### Structural facts that drive the C# design
1. **There is no voxel array.** `queryBlock(x,y,z)` *recomputes* the block from
   procedural noise on every call, memoised through four **unbounded** `Map`
   caches (`caveCache`, `oreCache`, `treeCache`, `cactusCache` in core.js) that
   are never evicted. All keys are template strings `` `${x},${y},${z}` ``.
2. **Player edits are a string-keyed sparse delta map** (`blockChanges`).
3. **Everything is single-threaded.** No Web Workers anywhere in `dist/`.
4. **Water is not a heightfield and not particles.** It is a discrete
   Minecraft-style flood fill over block levels 1–7 plus `WATER_FALLING`, with a
   `Set` of string keys, `.split(',')`-parsed and distance-sorted *every tick*
   (water.js `runTick`). `MAX_UPDATES_PER_TICK=500`, `TICK_INTERVAL=0.1`,
   `PLAYER_RADIUS=40`.
5. `getWaterHeight()` gives per-level surface heights (0.2–1.0) — the mesh is
   already non-cubic for water, which helps the SPH handoff.

## 2. The hash bug — verified, and it is worse than described

The brief said: *"a floating-point precision bug in the hash3 function."*
Correct diagnosis, **wrong function**. The catastrophic instance is in `hash2`.

Both functions contain the literal `1442695040888963407`, which is **160× larger
than `Number.MAX_SAFE_INTEGER` (2^53)**. It parses to `1442695040888963328`
(drift **−79** from intent). Its role differs per function:

- `hash2`: `x*374761393 + y*668265263 + SEED*1442695040888963407`
  → x,y terms are safe integers. **The SEED term is lossy.**
- `hash3`: `x*… + y*… + z*1442695040888963407 + SEED*998244353`
  → **the z term is lossy.** The SEED term is *exact* (`999999*998244353 ≈ 9.98e14
  < 2^53`). So fixing hash3 alone does **not** restore seed diversity.

### Measured seed collapse (the headline)
`core.js` generates seeds via `Math.floor(Math.random()*1000000)`.

```
seeds 0..999      ->   1000 distinct int32 values  (100.00% unique)
seeds 0..9999     ->   6002 distinct int32 values  ( 60.02% unique)
seeds 0..99999    ->   6002 distinct int32 values  (  6.00% unique)
seeds 0..999999   ->   6002 distinct int32 values  (  0.60% unique)
```

**Only ~6002 distinct worlds exist across the entire 1,000,000-seed space.**
Cause: as `seed` grows, `seed*C` grows and IEEE-754 double spacing widens,
destroying low-order bits before `|0` is applied —

```
seed=      1  double spacing 2^8   -> low  8 bits destroyed
seed=   1000  double spacing 2^18  -> low 18 bits destroyed
seed= 100000  double spacing 2^24  -> low 24 bits destroyed
seed=1000000  double spacing 2^28  -> low 28 bits destroyed
```

End-to-end confirmation: replicating the real `fbm(...,5)` height pipeline over a
16-sample fingerprint, **20,000 seeds produced only 8,941 distinct terrain shapes
(44.7%)**. Some shapes repeat 8×.

### hash3's z axis degrades the same way
```
z=    1  trailing zero bits= 8  => 24 useful bits
z=  100  trailing zero bits=15  => 17 useful bits
z= 1000  trailing zero bits=20  => 12 useful bits
z=10000  trailing zero bits=21  => 11 useful bits
```
Exact linearity in z holds only for z=0,1,2 and powers of two (first break at
z=3), so the result is neither a clean lattice nor well distributed — it is a
magnitude-dependent double-rounding artefact. This feeds `fbm3D` (caves) and the
ore/tree/cactus placement hashes.

Avalanche quality (1 input bit flipped → output bits changed, ideal 16.00):
```
hash3 x-axis : 15.43     hash3 z-axis : 12.79
hash2 x-axis : 13.71     hash2 SEED   : 13.07
```
The two lossy axes are the two worst scores.

## 3. Consequence: "make it mathematically perfect" invalidates all saves

An exact-int64 reimplementation of hash3 was compared against the live JS
version: **1999 of 2000 samples differ (100.0%)**. A correct fix does not
*repair* worldgen, it **replaces** it.

`save.js` stores `SAVE_VERSION=6`, the `seed` (u32), and `blockChanges` as a
sparse delta list (lines 160–161, 323–330). Terrain itself is *not* saved — it is
regenerated from seed on load, then deltas are applied. Version mismatch is hard-
rejected (lines 284–285).

So a fixed hash means every existing save's player edits land on **different base
terrain**. This is a product decision, not a coding detail, and it must be made
deliberately.

## 4. C# porting traps identified

- **`(int)someDouble` in C# is NOT JavaScript `|0`.** JS `ToInt32` is defined
  modularly for every double. In C#, casting an out-of-range double to `int` in an
  unchecked context is *unspecified*; on x86/x64 `cvttsd2si` yields `0x80000000`.
  A literal transliteration of `(…)|0` silently collapses to a constant. `ToInt32`
  must be implemented explicitly.
- **`Math.imul` → unchecked `(int)a * (int)b`** (wrapping). Fine, but the project
  must be built with `unchecked` semantics in mind; `/checked` builds would throw.
- **int64 is correct for the hash only.** It must NOT be generalised to particle
  state. SPH wants `float`/`System.Numerics` SIMD; int64 positions would cost
  ~2–4× throughput and kill vectorisation.
- **No .NET SDK in this sandbox** (not apt-installable). C# I write here cannot be
  compiled or executed by me. Numerics will be validated in Python/Node against a
  reference and the C# must be built locally.

## 5. SPH feasibility — an honest correction

The stated rationale was *"JavaScript (WebGL) is too slow for this."* That is true
of the **CPU**, but SPH is not CPU-bound by necessity:

- Real SPH at 50k–200k particles runs in browsers today on **WebGPU compute
  shaders**, and WebGL2 can do it via GPGPU ping-pong / transform feedback.
  Leaving JS does not *unlock* SPH.
- A GPU compute path (Vulkan/GL 4.3+ compute via Silk.NET) will beat 10 CPU cores
  by roughly 1–2 orders of magnitude at the same particle count.
- CPU SPH with TPL is genuinely viable for ~50–100k particles *if* done right, and
  C# is a good host for it (structs, `Span<T>`, `Vector256<T>`, no DOM). So C#
  remains a defensible choice — just not for the reason given.

If CPU/TPL is the chosen path, note that `Parallel.For` is the wrong primitive for
the hot loop. SPH needs: uniform-grid spatial hash → density pass → force pass →
integrate, with persistent workers and struct-of-arrays layout, parallelised over
grid cells with conflict-free writes. TPL's per-frame task scheduling overhead
shows up at 60 Hz.

**Hard prerequisite for SPH, independent of solver choice:** SPH particles need a
solid boundary to collide against. The current engine has *no voxel storage* — it
recomputes blocks from noise per query (§1.1). A real chunked voxel array with
per-cell occupancy (or a boundary SDF) must exist before any particle solver can
be coupled to terrain. This is a larger blocker than the SPH mathematics.

Foam should be planned as a **secondary emitter** (spawned from high-vorticity /
high-strain regions or a breaking-surface criterion), not as part of the pressure
solve.

## 6. Open decisions blocking implementation

1. Hash fix vs. save compatibility (§3) — the two are mutually exclusive.
2. CPU/TPL vs. GPU compute for SPH (§5).
3. Silk.NET vs. MonoGame. MonoGame exposes no compute shaders and would foreclose
   option 2's GPU path; Silk.NET (raw GL/Vulkan) keeps it open. For SPH, Silk.NET.
4. Does SPH *replace* the block water automaton, or coexist (rivers/lakes as
   blocks, splashes/waterfalls as particles)?
5. Target particle count and target frame budget — these determine 1 vs 2 outright.

---

# DECISIONS LOCKED (confirmed with Sasha)

| # | Decision | Choice |
|---|---|---|
| 1 | Hash fix vs. saves | **Fix properly, bump `SAVE_VERSION` 6 → 7.** Old saves become explicitly incompatible. |
| 2 | SPH backend | **GPU compute shaders.** (Supersedes the original "TPL only" rule — see note below.) |
| 3 | Graphics stack | **Silk.NET** (raw OpenGL/Vulkan). Compute shaders available; no scene-graph overhead. |
| 4 | Water scope | **Coexist.** Static seas/lakes stay `WATER` blocks; SPH drives splashes, waterfalls, shore-breaking waves, player interaction. Two-way coupling at the boundary. |

**Note on decision 2 vs. the original brief.** The stated rule was "native C# TPL
for the SPH multi-threading." Moving the *solver* to GPU compute does not remove
TPL from the project — it relocates it. CPU/TPL remains the right home for:
chunk meshing and buffer upload, procedural worldgen, spatial-grid rebuilds,
frustum/occlusion culling, particle readback and foam-emitter bookkeeping, save
serialisation, and the block↔particle coupling layer. TPL is no longer on the
SPH critical path. This is consistent with the "no fake water" rule: GPU SPH is
*more* physically real than a CPU solve at the same budget, not less.

**Recommendation on decision 3 sub-choice (GL vs. Vulkan).** Start on
**OpenGL 4.5/4.6** compute (`GL_SHADER_STORAGE_BUFFER` + `glDispatchCompute`).
It gives full SSBO-based SPH with far less ceremony than Vulkan, and Silk.NET
lets a Vulkan backend be added later behind the same abstraction. Going straight
to Vulkan would put explicit synchronisation and render-pass work on the critical
path of a migration that already has to reproduce an entire engine.

---

# TOOLCHAIN STATUS (verified in this sandbox)

- **.NET SDK 8.0.404 installed and working.** It is *not* apt-installable here and
  `curl`/`wget` are absent; it was fetched via Python `urllib` and extracted to
  `/tmp/.dotnet` — deliberately **outside** the repo so the ~200 MB SDK stays out
  of the Git snapshot.
- Required environment (the SDK throws a `TimeZoneInfo` stack trace without it):
  ```
  export DOTNET_ROOT=/tmp/.dotnet
  export PATH=$PATH:/tmp/.dotnet
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
  export TZ=UTC
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  ```
- **Consequence: I can compile and run C# here.** Numerics can be verified by
  execution rather than by inspection, and cross-checked against the live JS.
- No GPU is available in this sandbox, so compute-shader code can be *written and
  compiled* but not *executed* here. GPU paths will need local validation on
  Sasha's machine; CPU-side logic (grids, coupling, hashing, save I/O) is fully
  testable here.

---

# VERIFICATION: the cast trap is proven, and a bit-exact C# reference now exists

## A. `(int)hugeDouble` ≠ JS `|0` — measured, not assumed
A `dotnet` probe compiled and ran in this sandbox:

```
value=1.442695E+018   C# unchecked (int)cast = -2147483648   JS ToInt32 =  -144211712   *** MISMATCH ***
value=2.885390E+018   C# unchecked (int)cast = -2147483648   JS ToInt32 =  -288423424   *** MISMATCH ***
value=4.328085E+018   C# unchecked (int)cast = -2147483648   JS ToInt32 =  -432634880   *** MISMATCH ***
value=1.442695E+021   C# unchecked (int)cast = -2147483648   JS ToInt32 = 1817182208   *** MISMATCH ***
value=1.442694E+024   C# unchecked (int)cast = -2147483648   JS ToInt32 =  536870912   *** MISMATCH ***
```

Every out-of-range cast saturates to `int.MinValue` (the x86/x64 `cvttsd2si`
overflow sentinel). A literal transliteration of `(...)|0` would therefore collapse
the hash to a **constant**, producing flat, featureless terrain — and it would
compile cleanly and fail silently. `ToInt32` must be implemented explicitly
(modulo 2^32 with a sign correction), exactly as spec'd in ECMA-262 §7.1.8.

Also confirmed: the C# `long` literal `1442695040888963407L` is **exact**, while
the same literal as a JS double is `1442695040888963328` — **drift of 79**.

## B. A bit-exact C# reproduction of the live JS hash was achieved
The C# `Hash3_JsSemantics` (explicit `ToInt32` + `Math.imul` equivalent) was run
against the **unmodified** `dist/math.js` under Node:

| z | live JS `hash3(3,7,z,12345)` | C# `Hash3_JsSemantics` | |
|---|---|---|---|
| 0 | 0.1628010249696672 | 0.1628010249696672 | match |
| 1 | 0.3059377658646554 | 0.3059377658646554 | match |
| 2 | 0.13376637361943722 | 0.13376637361943722 | match |
| 3 | 0.8985299551859498 | 0.8985299551859498 | match |
| 100 | 0.26213578856550157 | 0.26213578856550157 | match |
| 1000 | 0.9623269725125283 | 0.9623269725125283 | match |

Exact-int64 `Hash3_ExactInt64` on the same inputs gives entirely different values
(`0.04169660108163953` at z=1, etc.), reconfirming §3: **1999/2000 samples
differ**.

## C. Why this changes the migration sequencing
Because a *bit-exact legacy* implementation now exists and is proven against the
real JS, the migration can be de-risked instead of changing two things at once:

- **Phase A — parity.** Port worldgen/chunking/meshing to C# using
  `Hash*_JsSemantics`. Assert bit-exact equality against Node-generated golden
  values for thousands of coordinates. Any divergence is unambiguously a porting
  bug, because the terrain is *known* to be identical by construction.
- **Phase B — the fix.** Swap in `Hash*_ExactInt64`, regenerate goldens, bump
  `SAVE_VERSION` to 7, and land the terrain change as its own isolated, reviewable
  commit.

Changing the physics backend, the renderer, the language, *and* the worldgen hash
simultaneously would make every visual defect undiagnosable. Phase A buys a
reference point. **Recommendation: adopt the two-phase order even though decision
1 is "fix it properly" — the fix still lands, just not in the same commit as the
port.**

## D. Reproduction
Analysis scripts are in `analysis/` (`hashtest.js`, `hash2.js`, `hash3.js`,
`seed_entropy.js`); the C# probe is at `/tmp/probe/HashProbe/` (outside the repo).
