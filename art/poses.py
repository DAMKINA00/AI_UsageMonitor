"""참조 디자인(ref/claude.png=클쨩, ref/codex.png=짚쨩)으로 상태별 포즈를 만든다. 동작은 참고 MV의 손짓 연출을 따른다.
python poses.py [pose|name ...]"""
import subprocess, sys

KEEP = ("Keep exactly the same art style, scale, head size, body proportions (about three and a half heads tall) and position in the frame as the reference, "
        "with the feet at the same height near the bottom of the frame. ")
STYLE = ("Flat 2D doujin music video style: thin warm dark-brown line art, flat colors, no gradients, simple shapes. "
         "Full body visible, no background, no floor, no shadow, no logos, no text other than the outfit details.")
SAME = "The same girl as in the reference image, with identical hair, face, eye color, hair accessory, outfit and colors,"

POSES = {
    "playing": {"claude": "standing and reading an open book held in one hand while holding a steaming teacup in the other, relaxed content smile.",
                "codex": "standing and playing a handheld game console held in both hands, excited gleeful grin."},
    "sleeping": {"claude": "standing while dozing off peacefully, head tilted to one side, eyes closed, arms hanging loosely, calm sleeping face with no snot bubble and no drool.",
                 "codex": "standing while dozing off, head drooping forward, eyes closed, arms hanging loosely, calm sleeping face with no snot bubble and no drool."},
    "tired": {"claude": "standing slumped with drooping shoulders and limp arms, half-closed tired eyes behind crooked glasses, gloomy blue vertical lines on her face, a big sweat drop.",
              "codex": "standing slumped with drooping shoulders and limp arms, half-closed tired eyes, gloomy blue vertical lines on her face, a big sweat drop."},
    "collapsed": {"claude": "collapsed lying face-down flat on the ground in despair with her arms stretched forward, a tiny white ghost-like soul floating up from her back.",
                  "codex": "collapsed lying face-down flat on the ground in despair with her arms stretched forward, a tiny white ghost-like soul floating up from her back."},
    "panic": {"claude": "standing with both fists pressed to her cheeks, eyes squeezed shut like >_< with comical streams of tears, mouth wide open crying.",
              "codex": "standing with both hands gripping her head, eyes squeezed shut like >_< with comical streams of tears, mouth wide open screaming."},
    "happy": {"claude": "jumping in the air with both arms raised high, eyes closed in a big happy smile.",
              "codex": "jumping in the air with both arms raised high, eyes closed in a big happy grin showing her fang."},
    "waiting": {"claude": "standing and pointing her index finger straight at the viewer, leaning forward slightly with a polite asking expression.",
                "codex": "standing and pointing her index finger straight at the viewer, leaning forward with an impatient pout."},
}

only = set(sys.argv[1:])
for n, (pose, by) in enumerate(POSES.items()):
    for ch, desc in by.items():
        key = f"{ch}_{pose}"
        if only and key not in only and pose not in only and ch not in only:
            continue
        prompt = f"This is an RGBA format image with transparency. {SAME} {desc} {KEEP}{STYLE} The image has an alpha channel and a transparent background."
        subprocess.run(["python", "gen.py", "edit", f"poses/{key}.png", str(1000 + n * 17 + len(ch)), f"ref/{ch}.png", prompt], check=True)
