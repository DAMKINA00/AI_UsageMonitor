"""Claude Code statusLine helper for AI Usage Monitor.

Claude Code pipes session JSON (including rate_limits) to this script on stdin.
We persist the useful bits to %LOCALAPPDATA%\\AIUsageMonitor\\claude\\ so the
monitor app can read them, then print a short status line.
"""
import json
import os
import sys
import time

OUT_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "AIUsageMonitor", "claude")


def atomic_write(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    os.replace(tmp, path)


def main():
    try:
        raw = sys.stdin.buffer.read().decode("utf-8", errors="replace")
        data = json.loads(raw) if raw.strip() else {}
    except Exception:
        data = {}

    record = {
        "captured_at": time.time(),
        "session_id": data.get("session_id"),
        "cwd": data.get("cwd") or (data.get("workspace") or {}).get("current_dir"),
        "model": (data.get("model") or {}).get("display_name"),
        "version": data.get("version"),
        "rate_limits": data.get("rate_limits"),
        "cost": data.get("cost"),
        "context_window": {
            k: (data.get("context_window") or {}).get(k)
            for k in ("used_percentage", "context_window_size")
        },
    }

    try:
        os.makedirs(OUT_DIR, exist_ok=True)
        if record["session_id"]:
            atomic_write(os.path.join(OUT_DIR, f"session_{record['session_id']}.json"), record)
        if record["rate_limits"]:
            atomic_write(os.path.join(OUT_DIR, "rate_limits.json"), record)
    except Exception:
        pass

    parts = []
    rl = record["rate_limits"] or {}
    for key, label in (("five_hour", "5h"), ("seven_day", "7d")):
        pct = (rl.get(key) or {}).get("used_percentage")
        if pct is not None:
            parts.append(f"{label} {pct:.0f}%")
    if record["model"]:
        parts.insert(0, record["model"])
    print(" | ".join(parts))


if __name__ == "__main__":
    main()
