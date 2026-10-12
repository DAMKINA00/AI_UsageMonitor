"""Qwen Image 2.1 로 캐릭터 이미지를 만드는 스크립트 (로컬 ComfyUI API 직접 호출).

python gen.py t2i  <out.png> <seed> <w> <h> "<prompt>"
python gen.py edit <out.png> <seed> <ref.png> "<prompt>" [ref2.png]
"""
import json
import sys
import time
import urllib.parse
import urllib.request
import uuid

HOST = "http://127.0.0.1:8188"
UNET = "qwen_image_2.1_int8_convrot.safetensors"
CLIP = "qwen3vl_8b_int8_convrot.safetensors"
VAE = "qwen_image_2.1_vae_bf16.safetensors"
STEPS = 22


def post(path, data, headers=None):
    req = urllib.request.Request(HOST + path, data=data, headers=headers or {})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())


def upload(path):
    boundary = uuid.uuid4().hex
    name = f"aum_{uuid.uuid4().hex[:8]}.png"
    body = (
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{name}\"\r\n"
        f"Content-Type: image/png\r\n\r\n"
    ).encode() + open(path, "rb").read() + (
        f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n--{boundary}--\r\n"
    ).encode()
    r = post("/upload/image", body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})
    return r["name"]


def base_graph(prompt, seed):
    return {
        "1": {"class_type": "UNETLoader", "inputs": {"unet_name": UNET, "weight_dtype": "default"}},
        "2": {"class_type": "QwenImage21Cache", "inputs": {"model": ["1", 0], "device": "auto", "dtype": "default"}},
        "3": {"class_type": "CLIPLoader", "inputs": {"clip_name": CLIP, "type": "qwen_image", "device": "default"}},
        "4": {"class_type": "VAELoader", "inputs": {"vae_name": VAE}},
        "5": {"class_type": "TextEncodeQwenImage21",
              "inputs": {"clip": ["3", 0], "prompt": prompt, "negative_prompt": "", "resolution": 1024}},
        "7": {"class_type": "KSampler",
              "inputs": {"model": ["2", 0], "positive": ["5", 0], "negative": ["5", 1], "latent_image": None,
                         "seed": seed, "steps": STEPS, "cfg": 1.0, "sampler_name": "euler", "scheduler": "simple",
                         "denoise": 1.0}},
        "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["4", 0]}},
        "9": {"class_type": "SaveImageAdvanced",
              "inputs": {"images": ["8", 0], "filename_prefix": "aum/gen", "format": "png",
                         "format.bit_depth": "8-bit", "format.input_color_space": "sRGB"}},
    }


def run(graph, out):
    pid = post("/prompt", json.dumps({"prompt": graph, "client_id": uuid.uuid4().hex}).encode(),
               {"Content-Type": "application/json"})["prompt_id"]
    while True:
        time.sleep(2)
        with urllib.request.urlopen(f"{HOST}/history/{pid}") as r:
            h = json.loads(r.read())
        if pid not in h:
            continue
        st = h[pid].get("status", {})
        if st.get("status_str") == "error":
            raise SystemExit(json.dumps(st, indent=1)[:3000])
        imgs = [i for o in h[pid]["outputs"].values() for i in o.get("images", [])]
        if imgs:
            i = imgs[0]
            q = urllib.parse.urlencode({"filename": i["filename"], "subfolder": i["subfolder"], "type": i["type"]})
            with urllib.request.urlopen(f"{HOST}/view?{q}") as r:
                open(out, "wb").write(r.read())
            print(out)
            return
        if st.get("completed"):
            raise SystemExit("no image output")


def main():
    mode, out, seed = sys.argv[1], sys.argv[2], int(sys.argv[3])
    if mode == "t2i":
        w, h, prompt = int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
        g = base_graph(prompt, seed)
        g["6"] = {"class_type": "EmptyLatentImage", "inputs": {"width": w, "height": h, "batch_size": 1}}
        g["7"]["inputs"]["latent_image"] = ["6", 0]
    else:
        ref, prompt = sys.argv[4], sys.argv[5]
        g = base_graph(prompt, seed)
        refs = [ref] + sys.argv[6:]
        for n, path in enumerate(refs, start=1):
            g[f"20{n}"] = {"class_type": "LoadImage", "inputs": {"image": upload(path)}}
            g["5"]["inputs"][f"images.image_{n}"] = [f"20{n}", 0]
        g["5"]["inputs"]["vae"] = ["4", 0]
        g["7"]["inputs"]["latent_image"] = ["5", 2]
    run(g, out)


if __name__ == "__main__":
    main()
