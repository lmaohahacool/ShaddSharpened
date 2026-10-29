#!/usr/bin/env python3
"""Regenerates the STAND-IN asset pack under repo/dist/.

WHY THIS EXISTS
---------------
The original web build's distribution folder (dist/: 34 terrain PNGs, 11 item
icons, 3 mob/model textures, 3 model JSONs + manifest, 6 MP3s) is the game's
content, owned by the original repository.  Workspace snapshots drop any
directory named "dist", so after a session boundary the folder can vanish from
this mirror even though every C# source file survives.  This script rebuilds a
complete, parseable stand-in so the C# port is runnable and self-testable from
this workspace alone.

The stand-in is NOT the original art.  To restore the real thing, copy the
dist/ folder from the original repository over repo/dist/ (the .csproj copies
whatever is at repo/dist into the output directory; AssetPaths prefers
<exe>/dist).  See notes/ASSETS.md.

Everything here matches the contracts the C# loaders expect:
  * PNG: RGBA8, non-interlaced.  Terrain sources are 96x96 because
    TextureSet.BlitNearest downsamples 96->16 by centre sampling; we render the
    16x16 pattern and nearest-upscale it by 6 so the downsample is exact.
  * models/*.json: {name, textureSize:[w,h], parts:[{id, pivot, position,
    rotation, boxes:[{origin, size, uv, inflate?}]}]}  (ModelLoader.AddPart).
  * models/manifest.json: { "<name>": {"model": rel, "texture": rel} }.
  * sounds/*.mp3: anything MP3Sharp can decode; effects are decoded once into
    OpenAL buffers, bg_music.mp3 is streamed.

Usage:  python3 tools/gen_placeholder_assets.py [--dist DIR] [--no-sounds]
Sounds need ffmpeg with libmp3lame on PATH; without it the script leaves WAVs
and says which MP3s are still missing (the game then reports missing sounds,
exactly like the browser build does).
"""
import json
import math
import os
import random
import struct
import subprocess
import sys
import wave
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(os.path.dirname(HERE), "dist")
WANT_SOUNDS = "--no-sounds" not in sys.argv
if "--dist" in sys.argv:
    DIST = sys.argv[sys.argv.index("--dist") + 1]

# Never clobber real art: if a genuine dist/ is present, refuse unless --force.
_probe = os.path.join(DIST, "textures", "grass_top.png")
if os.path.exists(_probe) and "--force" not in sys.argv:
    sys.exit(f"refusing to overwrite an existing {os.path.join(DIST, 'textures')}; "
             f"pass --force to replace it with placeholders")

R = random.Random(20261028)

# --------------------------------------------------------------------- helpers


def write_png(path, w, h, rgba):
    raw = b"".join(b"\x00" + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png)


def grid():
    return [[0, 0, 0, 0] for _ in range(256)]


def clamp(v):
    return max(0, min(255, int(v)))


def noise(p, rgb, spread, a=255):
    for i in range(256):
        d = R.randint(-spread, spread)
        p[i] = [clamp(rgb[0] + d), clamp(rgb[1] + d), clamp(rgb[2] + d), a]
    return p


def row(p, y, rgb, spread, a=255):
    for x in range(16):
        d = R.randint(-spread, spread)
        p[y * 16 + x] = [clamp(rgb[0] + d), clamp(rgb[1] + d), clamp(rgb[2] + d), a]
    return p


def rect(p, x0, y0, w, h, rgb, a=255):
    for y in range(y0, y0 + h):
        for x in range(x0, x0 + w):
            p[y * 16 + x] = [rgb[0], rgb[1], rgb[2], a]
    return p


def blotch(p, cx, cy, r, rgb, a=255):
    for y in range(16):
        for x in range(16):
            if (x - cx) ** 2 + (y - cy) ** 2 <= r * r + R.randint(0, 2):
                p[y * 16 + x] = [rgb[0], rgb[1], rgb[2], a]
    return p


def holes(p, chance):
    for i in range(256):
        if R.random() < chance:
            p[i] = [0, 0, 0, 0]
    return p


def save(name, p, n=6):
    w = 16 * n
    out = bytearray(w * w * 4)
    for y in range(w):
        for x in range(w):
            s = p[(y // n) * 16 + (x // n)]
            d = (y * w + x) * 4
            out[d:d + 4] = bytes(s)
    write_png(os.path.join(DIST, "textures", name), w, w, bytes(out))


# ---------------------------------------------------------------- terrain tiles
TERRAIN = {
    "grass_top.png": lambda: noise(grid(), (0x5A, 0x9F, 0x3C), 18),
    "dirt.png": lambda: noise(grid(), (0x86, 0x60, 0x3C), 16),
    "dirt_dark.png": lambda: noise(grid(), (0x6B, 0x4C, 0x30), 14),
    "stone.png": lambda: noise(grid(), (0x7F, 0x7F, 0x7F), 12),
    "sand.png": lambda: noise(grid(), (0xDB, 0xD3, 0xA0), 10),
    "snow.png": lambda: noise(grid(), (0xF2, 0xF6, 0xF7), 6),
    "cobblestone.png": lambda: blotch(blotch(blotch(blotch(noise(grid(), (0x77, 0x77, 0x77), 10),
                                                           3, 3, 2, (0x9A, 0x9A, 0x9A)),
                                                  10, 4, 2, (0x9A, 0x9A, 0x9A)),
                                         5, 10, 2, (0x60, 0x60, 0x60)), 12, 11, 2, (0x9A, 0x9A, 0x9A)),
    "planks.png": lambda: rect(rect(rect(rect(noise(grid(), (0xB0, 0x8A, 0x4E), 10),
                                               0, 3, 16, 1, (0x7A, 0x5C, 0x30)),
                                      0, 7, 16, 1, (0x7A, 0x5C, 0x30)),
                             0, 11, 16, 1, (0x7A, 0x5C, 0x30)), 0, 15, 16, 1, (0x7A, 0x5C, 0x30)),
    "wood_side.png": lambda: rect(rect(rect(rect(noise(grid(), (0x6E, 0x50, 0x2C), 10),
                                                  2, 0, 1, 16, (0x52, 0x3B, 0x1E)),
                                             6, 0, 1, 16, (0x52, 0x3B, 0x1E)),
                                        10, 0, 1, 16, (0x52, 0x3B, 0x1E)), 14, 0, 1, 16, (0x52, 0x3B, 0x1E)),
    "wood_top.png": lambda: rect(rect(noise(grid(), (0xB0, 0x8A, 0x4E), 8),
                                      4, 4, 8, 8, (0x8A, 0x68, 0x38)), 6, 6, 4, 4, (0x6E, 0x50, 0x2C)),
    "leaves.png": lambda: holes(noise(grid(), (0x2E, 0x6B, 0x1F), 22), 0.18),
    "short_grass.png": lambda: rect(rect(rect(rect(grid(), 3, 8, 1, 8, (0x4E, 0x8F, 0x34)),
                                               7, 6, 1, 10, (0x5A, 0x9F, 0x3C)),
                                          11, 9, 1, 7, (0x4E, 0x8F, 0x34)), 5, 10, 1, 6, (0x3E, 0x7B, 0x2A)),
    "cactus_top.png": lambda: rect(noise(grid(), (0x3E, 0x6B, 0x2A), 12), 4, 4, 8, 8, (0x88, 0xAA, 0x66)),
    "cactus_side.png": lambda: rect(rect(rect(noise(grid(), (0x3E, 0x6B, 0x2A), 12),
                                               0, 0, 1, 16, (0x2A, 0x4A, 0x1C)),
                                          15, 0, 1, 16, (0x2A, 0x4A, 0x1C)), 7, 2, 1, 12, (0x88, 0xAA, 0x66)),
    "cactus_bottom.png": lambda: noise(grid(), (0x33, 0x57, 0x22), 10),
    "CT_top.png": lambda: rect(rect(rect(rect(noise(grid(), (0x9A, 0x6E, 0x3E), 10),
                                              0, 0, 16, 1, (0x5E, 0x40, 0x24)),
                                         0, 15, 16, 1, (0x5E, 0x40, 0x24)),
                                    0, 0, 1, 16, (0x5E, 0x40, 0x24)), 15, 0, 1, 16, (0x5E, 0x40, 0x24)),
    "CT_side.png": lambda: rect(noise(grid(), (0x9A, 0x6E, 0x3E), 10), 0, 0, 16, 5, (0x5E, 0x40, 0x24)),
    "coal_ore.png": lambda: blotch(blotch(blotch(blotch(noise(grid(), (0x7F, 0x7F, 0x7F), 12),
                                                        4, 4, 1.6, (0x22, 0x22, 0x22)),
                                                   11, 6, 1.6, (0x22, 0x22, 0x22)),
                                          6, 11, 1.6, (0x22, 0x22, 0x22)), 13, 12, 1.6, (0x22, 0x22, 0x22)),
    "iron_ore.png": lambda: blotch(blotch(blotch(blotch(noise(grid(), (0x7F, 0x7F, 0x7F), 12),
                                                         4, 4, 1.6, (0xD8, 0xAF, 0x93)),
                                                    11, 6, 1.6, (0xD8, 0xAF, 0x93)),
                                           6, 11, 1.6, (0xD8, 0xAF, 0x93)), 13, 12, 1.6, (0xD8, 0xAF, 0x93)),
    "furnace_tbs.png": lambda: noise(grid(), (0x6E, 0x6E, 0x6E), 10),
    "furnace_front.png": lambda: rect(noise(grid(), (0x6E, 0x6E, 0x6E), 10), 4, 8, 8, 5, (0x2A, 0x2A, 0x2A)),
    "wfurnace_front.png": lambda: rect(rect(noise(grid(), (0x6E, 0x6E, 0x6E), 10),
                                            4, 8, 8, 5, (0x2A, 0x2A, 0x2A)), 5, 10, 6, 3, (0xE8, 0x8A, 0x20)),
    "chest_tbs.png": lambda: noise(grid(), (0x8A, 0x5C, 0x28), 10),
    "chest_front.png": lambda: rect(rect(noise(grid(), (0x8A, 0x5C, 0x28), 10),
                                         0, 6, 16, 2, (0x3E, 0x28, 0x10)), 7, 6, 2, 3, (0xD8, 0xC0, 0x60)),
    "grass_side.png": lambda: row(row(row(row(noise(grid(), (0x86, 0x60, 0x3C), 16),
                                               0, (0x5A, 0x9F, 0x3C), 16),
                                          1, (0x5A, 0x9F, 0x3C), 16),
                                     2, (0x4E, 0x8F, 0x34), 16), 3, (0x5A, 0x9F, 0x3C), 16),
    "grass_side_overlay.png": lambda: row(row(row(row(grid(), 0, (0x5A, 0x9F, 0x3C), 16),
                                                   1, (0x5A, 0x9F, 0x3C), 16),
                                              2, (0x4E, 0x8F, 0x34), 16), 3, (0x5A, 0x9F, 0x3C), 16),
    "bed_red.png": lambda: noise(grid(), (0xA0, 0x28, 0x28), 10),
    "bed_white.png": lambda: noise(grid(), (0xE8, 0xE8, 0xE8), 6),
    "torch.png": lambda: rect(rect(grid(), 7, 6, 2, 10, (0x6E, 0x50, 0x2C)), 7, 4, 2, 2, (0xFF, 0xD0, 0x40)),
    "ice.png": lambda: noise(grid(), (0x9B, 0xC8, 0xE8), 8, a=170),
    "water.png": lambda: row(row(noise(grid(), (0x30, 0x60, 0xD0), 12, a=160),
                                 3, (0x50, 0x80, 0xE0), 10, a=160), 9, (0x50, 0x80, 0xE0), 10, a=160),
    "door_top.png": lambda: rect(rect(rect(rect(noise(grid(), (0x9A, 0x6E, 0x3E), 8),
                                                0, 0, 16, 1, (0x5E, 0x40, 0x24)),
                                           0, 15, 16, 1, (0x5E, 0x40, 0x24)),
                                      0, 0, 1, 16, (0x5E, 0x40, 0x24)), 15, 0, 1, 16, (0x5E, 0x40, 0x24)),
    "door_bottom.png": lambda: rect(rect(rect(rect(noise(grid(), (0x9A, 0x6E, 0x3E), 8),
                                                   0, 0, 16, 1, (0x5E, 0x40, 0x24)),
                                              0, 15, 16, 1, (0x5E, 0x40, 0x24)),
                                         0, 0, 1, 16, (0x5E, 0x40, 0x24)), 7, 7, 2, 2, (0x5E, 0x40, 0x24)),
    "door_sides.png": lambda: rect(rect(rect(rect(noise(grid(), (0x9A, 0x6E, 0x3E), 8),
                                                  0, 0, 16, 1, (0x5E, 0x40, 0x24)),
                                             0, 5, 16, 1, (0x5E, 0x40, 0x24)),
                                        0, 10, 16, 1, (0x5E, 0x40, 0x24)), 0, 15, 16, 1, (0x5E, 0x40, 0x24)),
}
for name, fn in TERRAIN.items():
    save(name, fn())

# ------------------------------------------------------------------- item icons
ITEMS = {
    "sticks.png": [(7, 3, 2, 10, (0x6E, 0x50, 0x2C))],
    "wp.png": [(3, 3, 10, 2, (0xB5, 0x8A, 0x5A)), (7, 5, 2, 8, (0x6E, 0x50, 0x2C))],
    "sp.png": [(3, 3, 10, 2, (0x80, 0x80, 0x80)), (7, 5, 2, 8, (0x6E, 0x50, 0x2C))],
    "wa.png": [(4, 3, 6, 3, (0xB5, 0x8A, 0x5A)), (7, 6, 2, 7, (0x6E, 0x50, 0x2C))],
    "sa.png": [(4, 3, 6, 3, (0x80, 0x80, 0x80)), (7, 6, 2, 7, (0x6E, 0x50, 0x2C))],
    "beef.png": [(4, 5, 8, 6, (0xD8, 0x60, 0x60))],
    "steak.png": [(4, 5, 8, 6, (0x8A, 0x4A, 0x30))],
    "coal.png": [(5, 5, 6, 6, (0x22, 0x22, 0x22))],
    "raw_iron.png": [(5, 5, 6, 6, (0xD8, 0xAF, 0x93))],
    "iron_ingot.png": [(4, 6, 8, 4, (0xE8, 0xE8, 0xE8))],
    "rotten_flesh.png": [(5, 5, 6, 6, (0x6E, 0x50, 0x30))],
}
for name, rects in ITEMS.items():
    p = grid()
    for x0, y0, w, h, rgb in rects:
        rect(p, x0, y0, w, h, rgb)
    save(name, p)

# ----------------------------------------------------------------- model skins


def skin(path, w, h, base, patches):
    px = bytearray(w * h * 4)
    for i in range(w * h):
        d = R.randint(-8, 8)
        px[i * 4:i * 4 + 3] = bytes((clamp(base[0] + d), clamp(base[1] + d), clamp(base[2] + d)))
        px[i * 4 + 3] = 255
    for x0, y0, pw, ph, rgb in patches:
        for y in range(y0, y0 + ph):
            for x in range(x0, x0 + pw):
                o = (y * w + x) * 4
                px[o:o + 4] = bytes((rgb[0], rgb[1], rgb[2], 255))
    write_png(os.path.join(DIST, path), w, h, bytes(px))


skin("textures/cowtexture.png", 64, 32, (0x8A, 0x5C, 0x38),
     [(0, 0, 20, 12, (0xE8, 0xE8, 0xE8)), (40, 16, 14, 10, (0xE8, 0xE8, 0xE8))])
skin("textures/zombietexture.png", 64, 32, (0x3E, 0x6B, 0x2A),
     [(0, 16, 32, 16, (0x28, 0x40, 0x8A))])
skin("textures/torchtexture.png", 16, 16, (0x6E, 0x50, 0x2C),
     [(6, 0, 4, 4, (0xFF, 0xD0, 0x40))])

# -------------------------------------------------------------------- models


def box(origin, size, uv, inflate=0.0):
    b = {"origin": list(origin), "size": list(size), "uv": list(uv)}
    if inflate:
        b["inflate"] = inflate
    return b


def part(pid, pivot, position, boxes, rotation=(0, 0, 0)):
    return {"id": pid, "pivot": list(pivot), "position": list(position),
            "rotation": list(rotation), "boxes": boxes}


LEG = [box((-2, -8, -2), (4, 8, 4), (48, 0))]
cow = {"name": "cow", "textureSize": [64, 32], "parts": [
    part("body", (0, 0, 0), (0, 0, 0), [box((-5, -8, -10), (10, 8, 16), (0, 0))]),
    part("head", (0, 0, -10), (0, -8, -2), [box((-3, -6, -6), (6, 6, 6), (32, 0))]),
    part("leg1", (-3, 0, 6), (0, 0, 0), LEG),
    part("leg2", (3, 0, 6), (0, 0, 0), LEG),
    part("leg3", (-3, 0, -6), (0, 0, 0), LEG),
    part("leg4", (3, 0, -6), (0, 0, 0), LEG),
]}
zombie = {"name": "zombie", "textureSize": [64, 32], "parts": [
    part("body", (0, 0, 0), (0, 0, 0), [box((-4, -12, -2), (8, 12, 4), (16, 16))]),
    part("head", (0, 12, 0), (0, 0, 0), [box((-4, 0, -4), (8, 8, 8), (0, 0))]),
    part("armL", (-4, 10, 0), (0, 0, 0), [box((-4, -8, -2), (4, 12, 4), (40, 16))]),
    part("armR", (4, 10, 0), (0, 0, 0), [box((0, -8, -2), (4, 12, 4), (40, 16))]),
    part("legL", (-2, 0, 0), (0, 0, 0), [box((-2, -12, -2), (4, 12, 4), (0, 16))]),
    part("legR", (2, 0, 0), (0, 0, 0), [box((-2, -12, -2), (4, 12, 4), (0, 16))]),
]}
torch = {"name": "torch", "textureSize": [16, 16], "parts": [
    part("stick", (0, 0, 0), (0, 0, 0), [box((-1, -5, -1), (2, 10, 2), (0, 0))]),
]}
os.makedirs(os.path.join(DIST, "models"), exist_ok=True)
for fname, m in (("cow.json", cow), ("zombie.json", zombie), ("torch.json", torch)):
    with open(os.path.join(DIST, "models", fname), "w") as f:
        json.dump(m, f)
with open(os.path.join(DIST, "models", "manifest.json"), "w") as f:
    json.dump({
        "cow": {"model": "models/cow.json", "texture": "textures/cowtexture.png"},
        "zombie": {"model": "models/zombie.json", "texture": "textures/zombietexture.png"},
        "torch": {"model": "models/torch.json", "texture": "textures/torchtexture.png"},
    }, f, indent=2)

# -------------------------------------------------------------------- sounds
if WANT_SOUNDS:
    os.makedirs(os.path.join(DIST, "sounds"), exist_ok=True)
    SR = 44100

    def wr(name, seconds, fn, stereo=False):
        n = int(seconds * SR)
        frames = bytearray()
        for i in range(n):
            t = i / SR
            v = max(-1.0, min(1.0, fn(t)))
            s = int(v * 32000)
            frames += struct.pack("<hh", s, s) if stereo else struct.pack("<h", s)
        p = os.path.join(DIST, "sounds", name + ".wav")
        with wave.open(p, "wb") as w:
            w.setnchannels(2 if stereo else 1)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes(bytes(frames))
        return p

    rng2 = random.Random(7)
    wr("break", 0.22, lambda t: rng2.uniform(-1, 1) * math.exp(-t * 22))
    wr("place", 0.14, lambda t: rng2.uniform(-1, 1) * math.exp(-t * 30))
    wr("footstep", 0.10, lambda t: rng2.uniform(-1, 1) * math.exp(-t * 40))
    wr("hit", 0.18, lambda t: (0.6 * math.sin(2 * math.pi * 180 * t) +
                               0.4 * rng2.uniform(-1, 1)) * math.exp(-t * 18))
    wr("eating", 0.34, lambda t: rng2.uniform(-1, 1) * math.exp(-(t % 0.11) * 35) * 0.9)
    chords = [(0.0, 220.0), (4.0, 174.6), (8.0, 196.0), (12.0, 164.8), (16.0, 220.0), (20.0, 174.6)]
    wr("bg_music", 24.0, lambda t: sum(
        0.16 * math.sin(2 * math.pi * f * mult * t) * max(0.0, min(1.0, 2 - abs(t - c) / 3.0))
        for mult in (1.0, 1.5, 2.0) for (c, f) in chords if abs(t - c) < 6.0), stereo=True)

    missing = []
    for name in ("break", "place", "footstep", "hit", "eating", "bg_music"):
        wav = os.path.join(DIST, "sounds", name + ".wav")
        mp3 = os.path.join(DIST, "sounds", name + ".mp3")
        try:
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav,
                            "-codec:a", "libmp3lame", "-q:a", "2" if name == "bg_music" else "6",
                            mp3], check=True)
            os.remove(wav)
        except Exception as ex:
            missing.append(name)
    if missing:
        print("  ! ffmpeg/lame unavailable; left WAVs for:", ", ".join(missing))
        print("    install ffmpeg (libmp3lame) and re-run, or ship real MP3s from the original dist/")

print("placeholder assets written to", DIST)
