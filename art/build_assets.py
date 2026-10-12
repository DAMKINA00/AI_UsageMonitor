"""생성된 포즈 이미지를 다듬어 앱 리소스(Assets/Characters)로 복사한다.

동작이 바뀌어도 몸 크기·비례가 변하지 않도록, 한 캐릭터의 모든 포즈를
같은 영역(모든 포즈의 합집합 박스)으로 잘라 같은 크기 캔버스로 만든다.
어두운 창에서도 윤곽이 보이도록 흰 스티커 테두리를 두른다.
"""
import os
import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
DST = os.path.join(HERE, "..", "Assets", "Characters")
POSES = ["idle", "work1", "work2", "work3", "playing", "sleeping",
         "tired", "collapsed", "panic", "happy", "waiting"]
IDLE_H = 560  # 기본 자세 키 (결과 이미지 px)
OUTLINE = 6  # 결과 이미지 기준 px


def sticker(im):
    # 테두리는 캐릭터 몸에만: 반투명 픽셀과 아쿠아색 홀로그램(짚짱 작업 중)은 빼야
    # 속이 빈 홀로그램 화면이 흰 판으로 메워지지 않는다
    px = np.asarray(im).astype(np.int16)
    r, g, b, a = px[..., 0], px[..., 1], px[..., 2], px[..., 3]
    hologram = (g - r > 25) & (b - r > 25) & (a < 250)
    body = (a > 200) & ~hologram
    mask = Image.fromarray((body * 255).astype(np.uint8), "L")
    # 홀로그램 테두리 같은 얇은 조각은 지운다 (몸통은 굵어서 남는다)
    mask = mask.filter(ImageFilter.MinFilter(5)).filter(ImageFilter.MaxFilter(5))
    grown = mask.filter(ImageFilter.MaxFilter(OUTLINE * 2 + 1)).filter(ImageFilter.GaussianBlur(1.2))
    out = Image.new("RGBA", im.size, (255, 255, 255, 0))
    out.putalpha(grown)
    out.alpha_composite(im)
    return out


def source(ch, pose):
    return os.path.join(HERE, f"ref/{ch}_rgba.png" if pose == "idle" else f"poses/{ch}_{pose}.png")


os.makedirs(DST, exist_ok=True)
for ch in ["claude", "codex"]:
    images = {}
    for pose in POSES:
        src = source(ch, pose)
        if not os.path.exists(src):
            print("missing", src)
            continue
        images[pose] = Image.open(src).convert("RGBA")

    sizes = {im.size for im in images.values()}
    if len(sizes) > 1:
        print(f"warning: {ch} poses have different canvas sizes {sizes}")
    # 모든 포즈의 합집합 박스
    boxes = [im.getchannel("A").point(lambda a: 255 if a > 12 else 0).getbbox() for im in images.values()]
    boxes = [b for b in boxes if b]
    union = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
    w, h = union[2] - union[0], union[3] - union[1]
    # 두 캐릭터의 체형을 같게: 기본 자세(idle)의 키가 똑같은 픽셀 수가 되도록 배율을 정한다
    idle_box = images["idle"].getchannel("A").point(lambda a: 255 if a > 12 else 0).getbbox()
    scale = IDLE_H / (idle_box[3] - idle_box[1])
    size = (round(w * scale), round(h * scale))
    pad = OUTLINE + 4
    print(f"{ch}: union {union} -> {size} (+{pad}px pad)")

    for pose, im in images.items():
        im = im.crop(union).resize(size, Image.LANCZOS)
        canvas = Image.new("RGBA", (size[0] + pad * 2, size[1] + pad * 2), (0, 0, 0, 0))
        canvas.alpha_composite(im, (pad, pad))
        canvas = sticker(canvas)
        out = os.path.join(DST, f"{ch}_{pose}.png")
        canvas.save(out, optimize=True)
        print(f"  {ch}_{pose}.png {canvas.size} {os.path.getsize(out) // 1024}KB")

# 이전 버전에만 있던 포즈 파일 정리
for f in os.listdir(DST):
    name = os.path.splitext(f)[0]
    if "_" in name and name.split("_", 1)[1] not in POSES:
        os.remove(os.path.join(DST, f))
        print("removed old", f)
