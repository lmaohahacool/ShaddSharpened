#!/usr/bin/env python3
"""Rasterise the game's real UI font (Jersey 15, per dist/style.css) into a texture
atlas + metrics JSON under dist/font/, so the C# port renders text with the same
typeface the browser build uses instead of a stand-in bitmap font.

Usage: python3 tools/gen_font_atlas.py [ttf-path] [render-size]
"""
import json
import os
import sys

from PIL import Image, ImageFont, ImageDraw

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TTF = sys.argv[1] if len(sys.argv) > 1 else "/tmp/Jersey15-Regular.ttf"
SIZE = int(sys.argv[2]) if len(sys.argv) > 2 else 64

CHARS = [chr(c) for c in range(32, 127)] + ["→", "·", "×", "—", "€"]
PAD = 2

font = ImageFont.truetype(TTF, SIZE)

# Measure every glyph's ink box and advance at the render size.
metrics = {}
for ch in CHARS:
    mask = font.getmask(ch, mode="L")
    w, h = mask.size                      # getmask returns the ink box, already cropped
    x0, y0, x1, y1 = font.getbbox(ch)     # ink box relative to the left-ascender anchor
    adv = font.getlength(ch)
    if w and h:
        metrics[ch] = {"ink": [w, h], "bearing": [x0, y0], "advance": adv}
    else:
        metrics[ch] = {"ink": [0, 0], "bearing": [0, 0], "advance": adv}

# Pack into rows of a 1024-wide atlas.
ATLAS_W = 1024
rows, cur_x, cur_y, row_h = [], 0, 0, 0
placements = {}
for ch in CHARS:
    iw, ih = metrics[ch]["ink"]
    gw, gh = iw + PAD * 2, ih + PAD * 2
    if cur_x + gw > ATLAS_W:
        cur_y += row_h + 1
        cur_x, row_h = 0, 0
    placements[ch] = (cur_x, cur_y)
    cur_x += gw + 1
    row_h = max(row_h, gh)
ATLAS_H = cur_y + row_h + 1

img = Image.new("L", (ATLAS_W, ATLAS_H), 0)
draw = ImageDraw.Draw(img)
glyphs = {}
for ch in CHARS:
    iw, ih = metrics[ch]["ink"]
    if iw == 0 or ih == 0:
        glyphs[str(ord(ch))] = [0, 0, 0, 0, 0, 0, metrics[ch]["advance"]]
        continue
    mask = font.getmask(ch, mode="L")
    bx, by = metrics[ch]["bearing"]
    px, py = placements[ch]
    # Paste the ink box at (px+PAD, py+PAD); bearing ties it back to the pen position.
    ink = Image.frombytes("L", (iw, ih), bytes(mask))
    img.paste(ink, (px + PAD, py + PAD))
    glyphs[str(ord(ch))] = [px, py, iw + PAD * 2, ih + PAD * 2, bx - PAD, by - PAD,
                            metrics[ch]["advance"]]

out_dir = os.path.join(REPO, "dist", "font")
os.makedirs(out_dir, exist_ok=True)
img.save(os.path.join(out_dir, "jersey15.png"))
with open(os.path.join(out_dir, "jersey15.json"), "w") as f:
    json.dump({"size": SIZE, "atlas": "jersey15.png", "lineHeight": font.getmetrics()[0],
               "ascender": font.getmetrics()[0], "glyphs": glyphs}, f)
print(f"atlas {ATLAS_W}x{ATLAS_H}, {len(glyphs)} glyphs -> dist/font/")
