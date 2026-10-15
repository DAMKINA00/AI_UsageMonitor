"""참고 MV 그림체(단순한 플랫 채색, 얇은 갈색 선, 약 3.5등신 데포르메)로 클쨩·짚쨩 기본 디자인을 그린다."""
import subprocess, sys

STYLE = ("Flat 2D anime illustration in the style of a cute Japanese doujin music video: thin slightly wobbly warm dark-brown hand-drawn line art, "
         "completely flat fill colors with at most one soft shadow tone, no gradients, no glossy highlights, simple shapes and few details. "
         "Simple round face, simple eyes with a single white highlight, tiny mouth, pale pink blush ovals. "
         "Deformed chibi proportions about three and a half heads tall: big head, small body, short limbs, simple small hands. "
         "Full body visible from head to shoes, standing upright, centered, feet near the bottom, no background, no floor, no shadow, no text other than described.")
CLAUDE = ("A girl with long wavy light ash-brown hair reaching her waist, a black ribbon bow on one side of her head with a small gold eight-pointed star in its center, "
          "thin round glasses and gray-violet eyes. She wears a white blouse with a frilly jabot and a small red brooch, a black jacket with thin gold trim, "
          "a brown pleated skirt, black tights and brown boots. She stands facing the viewer with her hands gently clasped in front of her and a calm small smile.")
GPT = ("A girl with short silver-white bob hair, a cyan outlined-triangle hair clip on one side, bright blue eyes and a small fang. "
       "She wears an oversized white hooded jacket with thin cyan lines over a black high-collared top with tiny cyan letters \"GPT\" on the collar, "
       "black fingerless gloves, black shorts, black-and-white striped knee socks and black boots with cyan bands. "
       "She stands facing the viewer with a cheeky grin and her hands clasped behind her back.")

def wrap(d):
    return f"This is an RGBA format image with transparency. {d} {STYLE} The image has an alpha channel and a transparent background."

jobs = [("claude", s, CLAUDE) for s in (9101, 9202, 9303)] + [("codex", s, GPT) for s in (9401, 9502, 9603)]
for name, seed, desc in jobs:
    subprocess.run(["python", "gen.py", "t2i", f"out/{name}_v4_{seed}.png", str(seed), "832", "1248", wrap(desc)], check=True)
