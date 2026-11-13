"""把 视频帧/ 里的 jpg 打成 frames.dat（FRMD 容器：magic + 帧数 + 每帧「长度 + JPEG」）。

用法：python tools/build_frames_dat.py
产物：项目根/frames.dat
"""
import os, struct

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
folder = os.path.join(root, "视频帧")
files = sorted(f for f in os.listdir(folder) if f.lower().endswith(".jpg"))
out_path = os.path.join(root, "frames.dat")

with open(out_path, "wb") as out:
    out.write(b"FRMD")
    out.write(struct.pack("<i", len(files)))
    for f in files:
        blob = open(os.path.join(folder, f), "rb").read()
        out.write(struct.pack("<i", len(blob)))
        out.write(blob)

print("frames=%d dat=%.1f MB" % (len(files), os.path.getsize(out_path) / 1048576.0))
