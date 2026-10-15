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
CHARACTERS = ["claude", "codex", "gemini"]
# 앱은 그림을 캔버스 아래에 맞춰 붙인다. 쓰러진 포즈가 캔버스 가운데에 그려져 있으면 떠 보이므로 바닥선으로 내린다.
GROUND_POSES = ["collapsed"]
# 서 있지 않은 포즈(점프·쓰러짐)는 바닥선 계산에서 뺀다
OFF_GROUND = {"happy", "collapsed"}
MIN_WIDTH = 400  # 포즈가 좁은 캐릭터도 다른 캐릭터와 비슷한 캔버스 폭이 되도록 좌우에 투명 여백을 더한다


def sticker(im):
    # 테두리는 캐릭터 몸에만: 반투명 픽셀과 아쿠아색 홀로그램(짚쨩 작업 중)은 빼야
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


def bbox(im):
    return im.getchannel("A").point(lambda a: 255 if a > 12 else 0).getbbox()


def to_ground(images, ch):
    """쓰러진 포즈의 아랫면을 서 있는 포즈의 발바닥 선(가장 낮은 아랫면)에 맞춘다."""
    standing = [bbox(im) for pose, im in images.items() if pose not in OFF_GROUND]
    standing = [b for b in standing if b]
    if not standing:
        return
    floor = max(b[3] for b in standing)
    for pose in GROUND_POSES:
        if pose not in images or not (b := bbox(images[pose])):
            continue
        dy = floor - b[3]
        if dy == 0:
            continue
        # 픽셀을 그대로 옮긴다 (마스크 없이 붙여 넣어 알파가 바뀌지 않게, 캔버스 밖은 잘림)
        moved = Image.new("RGBA", images[pose].size, (0, 0, 0, 0))
        moved.paste(images[pose], (0, dy))
        images[pose] = moved
        print(f"  {ch}_{pose}: moved {dy:+d}px to the floor line y={floor}")


os.makedirs(DST, exist_ok=True)
for ch in CHARACTERS:
    images = {}
    for pose in POSES:
        src = source(ch, pose)
        if not os.path.exists(src):
            print("missing", src)
            continue
        images[pose] = Image.open(src).convert("RGBA")
    if "idle" not in images:
        print(f"skip {ch}: no idle image")
        continue

    sizes = {im.size for im in images.values()}
    if len(sizes) > 1:
        print(f"warning: {ch} poses have different canvas sizes {sizes}")
    to_ground(images, ch)
    # 모든 포즈의 합집합 박스
    boxes = [bbox(im) for im in images.values()]
    boxes = [b for b in boxes if b]
    union = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
    w, h = union[2] - union[0], union[3] - union[1]
    # 캐릭터끼리 체형을 같게: 기본 자세(idle)의 키가 똑같은 픽셀 수가 되도록 배율을 정한다
    idle_box = bbox(images["idle"])
    scale = IDLE_H / (idle_box[3] - idle_box[1])
    size = (round(w * scale), round(h * scale))
    pad = OUTLINE + 4
    side = max(pad, (MIN_WIDTH - size[0] + 1) // 2)
    print(f"{ch}: union {union} -> {size} (+{pad}px pad, {side}px sides)")

    for pose, im in images.items():
        im = im.crop(union).resize(size, Image.LANCZOS)
        canvas = Image.new("RGBA", (size[0] + side * 2, size[1] + pad * 2), (0, 0, 0, 0))
        canvas.alpha_composite(im, (side, pad))
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
