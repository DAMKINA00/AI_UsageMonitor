"""AI활용갤 밈 캐릭터(클짱=Claude, 짚짱=GPT/Codex)를 일반 소녀 비례 + 치비 스티커풍으로 그린다."""
import subprocess, sys

STYLE = ("Cute chibi-sticker anime art style: bold clean dark outlines, bright flat cel shading, soft rounded face, large glossy eyes, "
         "small nose and mouth, rosy blush, simple shapes with minimal clutter. "
         "Normal anime teenage girl body proportions, about six heads tall, slim arms and long legs, NOT super-deformed, NOT a big-head SD character. "
         "Full body visible from head to shoes, centered, no background, no floor, no text other than what is described.")
CLAUDE = ("A full-body anime girl, an elegant bookish girl. She has long wavy light ash-brown hair reaching her waist, "
          "a black ribbon bow on one side of her head with a small gold eight-pointed starburst ornament in its center, "
          "thin round gold-rimmed glasses and calm gray-violet eyes. She wears a white high-collared blouse with a frilly jabot and a small red brooch, "
          "a fitted black jacket-vest with gold trim and buttons, long black sleeves with white cuffs, a dark brown pleated skirt, "
          "black tights and brown lace-up boots. She stands facing the viewer with a gentle composed smile, one hand lightly raised.")
GPT = ("A full-body anime girl, a cheeky energetic tech girl. She has short silver-white bob hair with a faint lavender sheen and longer side locks, "
       "large bright blue eyes, a small fang, and a black-and-cyan outlined-triangle hair clip on one side. "
       "She wears an oversized white hooded jacket with black panels and glowing cyan accent lines and a small square cyan patch on the sleeve, "
       "over a black high-collared bodysuit top with small cyan letters \"GPT\" on the collar and a small cyan triangle on the chest, "
       "black fingerless gloves with cyan trim, black shorts, black-and-white striped thigh-high socks and chunky black boots with cyan bands. "
       "She stands facing the viewer with a smug playful grin, one hand on her hip.")

def wrap(d):
    return f"This is an RGBA format image with transparency. {d} {STYLE} The image has an alpha channel and a transparent background."

jobs = [("claude", s, CLAUDE) for s in (7101, 7202, 7303)] + [("codex", s, GPT) for s in (8101, 8202, 8303)]
only = set(sys.argv[1:])
for name, seed, desc in jobs:
    if only and str(seed) not in only: continue
    subprocess.run(["python", "gen.py", "t2i", f"out/{name}_v3_{seed}.png", str(seed), "832", "1248", wrap(desc)], check=True)
