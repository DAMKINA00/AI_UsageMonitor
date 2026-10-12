"""usage: python darksheet.py out.png h files... — 앱 배경색 위에 합성한 미리보기"""
import sys
from PIL import Image
out, h, files = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
tiles = []
for f in files:
    im = Image.open(f).convert("RGBA")
    t = im.resize((int(im.width * h / im.height), h), Image.LANCZOS)
    bg = Image.new("RGBA", t.size, (27, 28, 33, 255)); bg.alpha_composite(t); tiles.append(bg)
sheet = Image.new("RGB", (sum(t.width for t in tiles) + 8 * (len(tiles) - 1), h), (70, 70, 70)); x = 0
for t in tiles:
    sheet.paste(t.convert("RGB"), (x, 0)); x += t.width + 8
sheet.save(out)
