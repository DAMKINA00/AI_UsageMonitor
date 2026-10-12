"""usage: python sheet.py out.png h file1 file2 ... — 노란 배경에 합성한 미리보기 시트"""
import sys
from PIL import Image
out, h, files = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
thumbs = []
for f in files:
    im = Image.open(f).convert("RGBA")
    w = int(im.width * h / im.height)
    t = im.resize((w, h), Image.LANCZOS)
    bg = Image.new("RGBA", t.size, (247, 201, 75, 255))
    bg.alpha_composite(t)
    thumbs.append(bg)
sheet = Image.new("RGB", (sum(t.width for t in thumbs) + 8 * (len(thumbs) - 1), h), (30, 30, 30))
x = 0
for t in thumbs:
    sheet.paste(t.convert("RGB"), (x, 0)); x += t.width + 8
sheet.save(out)
print(Image.open(files[0]).mode, Image.open(files[0]).size)
