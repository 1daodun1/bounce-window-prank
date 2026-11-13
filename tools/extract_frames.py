"""把项目根目录里的 mp4 拆成 25fps 帧序列（需要 ffmpeg）。

用法：python tools/extract_frames.py [视频路径] [输出目录]
默认：视频 = 项目根下任意 *.mp4，输出 = 项目根/视频帧
"""
import glob, os, subprocess, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def ffmpeg():
    try:
        from imageio_ffmpeg import get_ffmpeg_exe
        return get_ffmpeg_exe()
    except Exception:
        return "ffmpeg"


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else None
    if not src:
        cands = glob.glob(os.path.join(root, "*.mp4"))
        if not cands:
            print("项目根没有 mp4，请把视频放进来或显式传路径")
            return 1
        src = cands[0]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "视频帧")
    os.makedirs(out, exist_ok=True)
    for f in glob.glob(os.path.join(out, "*.jpg")):
        os.remove(f)
    cmd = [ffmpeg(), "-y", "-i", src, "-vf", "fps=25,scale=640:360", "-q:v", "7",
           os.path.join(out, "f%05d.jpg")]
    print(" ".join(cmd))
    return subprocess.call(cmd)


if __name__ == "__main__":
    sys.exit(main())
