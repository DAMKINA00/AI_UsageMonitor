"""작업 중 프레임: 짚짱은 홀로그램을 보며 타이핑, 클짱은 아날로그 수첩에 메모.
1번 프레임은 기본 디자인에서, 2·3번은 1번에서 손·눈만 바꿔 깜빡임 없이 이어지게 만든다."""
import subprocess, sys
from PIL import Image

KEEP = ("Keep exactly the same art style, scale, head size, body proportions (about three and a half heads tall) and position in the frame as the reference, "
        "with the feet at the same height near the bottom of the frame. ")
STYLE = ("Flat 2D doujin music video style: thin warm dark-brown line art, flat colors, no gradients, simple shapes. "
         "Full body visible, no background, no floor, no shadow, no logos, no text other than the outfit details.")
SAME = "The same girl as in the reference image, with identical hair, face, eye color, hair accessory, outfit and colors,"
TINY = ("Keep the entire image identical to the reference: same pose, same body, same props, same position, same colors and line art. "
        "Change only this small detail: ")

WORK1 = {
    "claude": "standing upright and writing memos in a small brown leather analog pocket notebook held open in her left hand, a pen in her right hand touching the page, looking down at the notebook with a focused calm expression.",
    "codex": "standing upright and typing with both hands on a holographic keyboard floating in front of her at waist height. "
             "The hologram is very pale and mostly see-through: drawn only with thin, faint, light aqua glowing outlines of the keys and a soft faint glow, "
             "with a transparent interior, never a solid colored slab. A small floating holographic screen beside her head is drawn the same way: "
             "a thin pale outline frame with a few faint lines of text and a transparent inside. She looks at the hologram with a confident grin. "
             "The hologram stays close to her body.",
}
VARIANTS = {
    "claude": {"work2": "her pen hand has moved to the right side of the notebook page, still writing.",
               "work3": "her eyes are closed in a blink and the pen is lifted slightly above the page."},
    "codex": {"work2": "her fingers are pressing different keys, with her hands spread a little wider on the holographic keyboard.",
              "work3": "her eyes are closed in a blink while she keeps typing."},
}

def wrap(desc, keep=True):
    return f"This is an RGBA format image with transparency. {desc} {KEEP if keep else ''}{STYLE} The image has an alpha channel and a transparent background."

def flatten(src, dst):
    im = Image.open(src).convert("RGBA")
    bg = Image.new("RGBA", im.size, (255, 255, 255, 255)); bg.alpha_composite(im); bg.convert("RGB").save(dst)

only = set(a for a in sys.argv[1:] if not a.startswith("seed="))
SEED = next((a[5:] for a in sys.argv[1:] if a.startswith("seed=")), "4242")
frame1_only = "frame1" in only
only.discard("frame1")
for ch in ["claude", "codex"]:
    if only and ch not in only: continue
    if "noframe1" not in only:
        subprocess.run(["python", "gen.py", "edit", f"poses/{ch}_work1.png", SEED, f"ref/{ch}.png", wrap(f"{SAME} {WORK1[ch]}")], check=True)
    flatten(f"poses/{ch}_work1.png", f"ref/{ch}_work1.png")
    if frame1_only: continue
    for i, (pose, change) in enumerate(VARIANTS[ch].items()):
        subprocess.run(["python", "gen.py", "edit", f"poses/{ch}_{pose}.png", str(4300 + i), f"ref/{ch}_work1.png", wrap(TINY + change, keep=False)], check=True)
