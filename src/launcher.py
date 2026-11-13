# -*- coding: utf-8 -*-
"""朋友的酒 · 不依赖 .NET 的启动外壳。

流程：
  1. 查注册表确认有没有 .NET Framework 4.8
  2. 没有：弹出进度窗，用包里垫好的官方离线安装包静默安装（会走一次 UAC）
  3. 有：直接往下走，什么都不弹
  4. 把主程序和素材解到 %LOCALAPPDATA%\\朋友的酒，再启动主程序

用 PyInstaller 打包成单文件 exe 后，目标机不需要装 Python。
"""

import ctypes
import io
import os
import shutil
import subprocess
import sys
import threading
import time

import winreg

PATCH_MSU = "windows6.1-kb4474419-v3-x64.msu"   # Win7 的 SHA-2 签名补丁（微软官方离线包）
GITHUB_REPO = ""   # 开源仓库地址，用户给了就填这里；空着就不显示这一行
CACHE_NAME = "朋友的酒"
PAYLOAD_DIR = "payload"

# (包内文件名, 解出来的相对路径)
PAYLOAD = [
    ("app.exe", "桌面弹弹弹.exe"),
    ("audio.wav", "reference-audio.wav"),
    ("video.mp4", "41834646747-1-192.mp4"),
    ("victim.jpg", "victim.jpg"),
    ("ending.wav", "ending.wav"),
    ("tgpv.exe", os.path.join("TG频道预览", "TgPreview.exe")),
    ("NetGDI.dll", "NetGDI.dll"),
    ("NgdiRun.exe", "NgdiRun.exe"),
    ("frames.dat", "frames.dat"),
    (PATCH_MSU, PATCH_MSU),
]

INSTALLER = "ndp48.exe"
RUNONCE_KEY = r"Software\Microsoft\Windows\CurrentVersion\RunOnce"
RUNONCE_NAME = "朋友的酒组件安装续跑"
NET48_RELEASE = 528040          # .NET Framework 4.8 门槛（Win10 528040，Win7/8.1 528049）
DOWNLOAD_PAGE = "https://dotnet.microsoft.com/download/dotnet-framework/net48"

MB_ICONERROR = 0x00000010
MB_ICONINFORMATION = 0x00000040
MB_OK = 0x00000000
MB_SETFOREGROUND = 0x00010000


def base_dir():
    """PyInstaller 单文件模式下资源在本体解包出的临时目录里。"""
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def cache_dir():
    return os.path.join(os.environ.get("LOCALAPPDATA", ""), CACHE_NAME)


def message(text, title=None):
    ctypes.windll.user32.MessageBoxW(
        None, text, title or text, MB_ICONERROR | MB_OK | MB_SETFOREGROUND)


def net48_installed():
    for flag in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
        try:
            with winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                0,
                winreg.KEY_READ | flag,
            ) as key:
                release, _ = winreg.QueryValueEx(key, "Release")
                if int(release) >= NET48_RELEASE:
                    return True
        except OSError:
            continue
    return False


class SHELLEXECUTEINFOW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint),
        ("fMask", ctypes.c_uint),
        ("hwnd", ctypes.c_void_p),
        ("lpVerb", ctypes.c_wchar_p),
        ("lpFile", ctypes.c_wchar_p),
        ("lpParameters", ctypes.c_wchar_p),
        ("lpDirectory", ctypes.c_wchar_p),
        ("nShow", ctypes.c_int),
        ("hInstApp", ctypes.c_void_p),
        ("lpIDList", ctypes.c_void_p),
        ("lpClass", ctypes.c_wchar_p),
        ("hkeyClass", ctypes.c_void_p),
        ("dwHotKey", ctypes.c_uint),
        ("hIcon", ctypes.c_void_p),
        ("hProcess", ctypes.c_void_p),
    ]


def win7_build():
    """Win7 的版本号：7600 = SP0，7601 = SP1。"""
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                            0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            build = winreg.QueryValueEx(key, "CurrentBuildNumber")[0]
            return int(build)
    except Exception:
        try:
            return int(sys.getwindowsversion().build)
        except Exception:
            return 0


def is_win7_sp0():
    try:
        v = sys.getwindowsversion()
        if v.major != 6 or v.minor != 1:
            return False
    except Exception:
        return False
    return win7_build() <= 7600


def open_source_note():
    """开源致谢那一行；仓库地址还没填时不出现。"""
    if not GITHUB_REPO:
        return ""
    return "\n\n该程序已开源，希望你能支持一下：\n" + GITHUB_REPO


def is_win7():
    try:
        v = sys.getwindowsversion()
        return v.major == 6 and v.minor == 1
    except Exception:
        return False


def sha2_done():
    return os.path.exists(os.path.join(cache_dir(), "sha2.done"))


def mark_sha2():
    try:
        os.makedirs(cache_dir(), exist_ok=True)
        with io.open(os.path.join(cache_dir(), "sha2.done"), "w", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S"))
    except Exception:
        pass


def schedule_resume():
    """注册一次性自启动：重启后自动再跑一遍这个 exe，装完再拉起主程序。

    用 RunOnce 而不是 Run，是因为它执行一次就会被系统自己删掉。
    """
    try:
        exe = sys.executable if getattr(sys, "frozen", False) else os.path.abspath(__file__)
        cmd = '"%s" --resume-install' % exe
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUNONCE_KEY, 0, winreg.KEY_SET_VALUE) as key:
            winreg.SetValueEx(key, RUNONCE_NAME, 0, winreg.REG_SZ, cmd)
        log("已写入一次性自启动：%s" % cmd)
        return True
    except Exception as exc:
        log("写一次性自启动失败：%s" % exc)
        return False


def install_sha2_patch():
    """Win7 先补 SHA-2 签名支持，.NET 4.8 的安装包才肯跑。

    返回 "ok" / "reboot" / "skip" / "fail"
    """
    if not is_win7():
        return "skip"
    if sha2_done():
        return "ok"
    msu = os.path.join(base_dir(), PAYLOAD_DIR, PATCH_MSU)
    if not os.path.isfile(msu):
        log("没带补丁包：%s" % msu)
        return "fail"

    log("开始安装 KB4474419")
    try:
        code, timedout = run_elevated("wusa.exe", '"%s" /quiet /norestart' % msu, os.path.dirname(msu))
    except Exception as exc:
        log("补丁安装调用失败：%s" % exc)
        return "fail"
    log("KB4474419 返回码=%s 超时=%s" % (code, timedout))

    # 0=成功；3010/1641=成功但需要重启；2359302(0x240002)/2149842967 等=已经装过
    if code in (0, 2359302, -2146498530):
        mark_sha2()
        return "ok"
    if code in (3010, 1641):
        mark_sha2()
        return "reboot"
    return "fail"


def read_release():
    """返回注册表里 .NET 4.x 的 Release 值（读不到就是 0）。"""
    for flag in (0, winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                                r"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                                0, winreg.KEY_READ | flag) as key:
                release, _ = winreg.QueryValueEx(key, "Release")
                return int(release)
        except OSError:
            continue
    return 0


def run_elevated(exe, params, cwd, timeout_ms=1800000):
    """以管理员身份启动并等它结束；返回 (退出码, 是否超时)。用户在 UAC 点“否”会抛异常。"""
    info = SHELLEXECUTEINFOW()
    info.cbSize = ctypes.sizeof(info)
    info.fMask = 0x00000040          # SEE_MASK_NOCLOSEPROCESS
    info.lpVerb = "runas"
    info.lpFile = exe
    info.lpParameters = params
    info.lpDirectory = cwd
    info.nShow = 1                   # 安装程序自己的界面归它自己管
    ok = ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(info))
    if not ok:
        raise ctypes.WinError()

    if not info.hProcess:
        # 极少数系统拿不到进程句柄，只能给一个保守的等待，装完再查注册表
        log("拿不到安装进程句柄，退化为固定等待")
        time.sleep(120)
        return (-1, False)

    waited = ctypes.windll.kernel32.WaitForSingleObject(info.hProcess, timeout_ms)
    code = ctypes.c_ulong()
    ctypes.windll.kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
    ctypes.windll.kernel32.CloseHandle(info.hProcess)
    return (code.value, waited == 0x102)   # WAIT_TIMEOUT


def _run_install(installer, log_path, quiet):
    params = ("/quiet /norestart" if quiet else "/passive /norestart")
    params += ' /log "%s"' % log_path
    code, timedout = run_elevated(installer, params, os.path.dirname(installer))
    log("安装尝试 quiet=%s 返回码=%s 超时=%s" % (quiet, code, timedout))
    return code, timedout


def install_framework():
    """弹进度窗 + 静默装 .NET 4.8；失败会保留窗口并允许重试/手动安装。"""
    installer = os.path.join(base_dir(), PAYLOAD_DIR, INSTALLER)
    if not os.path.isfile(installer):
        log("找不到安装包：%s" % installer)
        return False

    log_path = os.path.join(cache_dir(), "dotnet_install.log")
    try:
        os.makedirs(cache_dir(), exist_ok=True)
    except Exception:
        pass

    try:
        import tkinter as tk
        from tkinter import ttk
    except Exception:
        _run_install(installer, log_path, True)
        return net48_installed()

    root = tk.Tk()
    root.title("朋友的酒 · 运行组件")
    root.geometry("600x300")
    root.configure(bg="#0a1020")
    root.resizable(False, False)

    tk.Label(root, text="你的电脑没有安装 .NET Framework 4.8",
             bg="#0a1020", fg="#ffffff",
             font=("Microsoft YaHei UI", 16, "bold")).pack(pady=(24, 6))
    tk.Label(root, text="Windows 10 以上一般自带；你的系统看起来低于 Win10，或者没有手动装过。\n"
                        "我会帮你安装（会弹一次管理员确认，请点“是”）。Win7 请确认已装 SP1。",
             bg="#0a1020", fg="#9fb2d0",
             font=("Microsoft YaHei UI", 10), justify="center").pack(pady=(0, 8))

    bar = ttk.Progressbar(root, mode="indeterminate", length=470)
    bar.pack(pady=4)
    bar.start(14)

    status = tk.Label(root, text="正在准备安装…", bg="#0a1020", fg="#6ea8ff",
                      font=("Microsoft YaHei UI", 10), wraplength=540, justify="center")
    status.pack(pady=6)

    buttons = tk.Frame(root, bg="#0a1020")
    buttons.pack(pady=4)

    state = {"busy": True, "ok": False, "attempt": 0, "note": ""}

    def do_install(quiet):
        if state["busy"] and state["attempt"] > 0:
            return
        state["busy"] = True
        state["attempt"] += 1
        status.configure(text="正在安装 .NET Framework 4.8（第 %d 次）… 请不要关闭窗口" % state["attempt"])
        for b in buttons.winfo_children():
            b.configure(state="disabled")
        bar.start(14)

        def worker():
            try:
                code, timedout = _run_install(installer, log_path, quiet)
                state["ok"] = net48_installed()
                if state["ok"]:
                    state["note"] = "安装完成，Release=%d" % read_release()
                elif code in (0, 3010, 1641):
                    state["note"] = ("安装程序返回 %d，但注册表里还没出现 4.8。"
                                     "3010/1641 表示需要重启，重启后再运行本程序。" % code)
                elif code == 1602:
                    state["note"] = "安装被取消（可能是在 UAC 里点了“否”）"
                elif code == 1603:
                    state["note"] = "安装失败（1603）：Win7 常因缺 SP1 或缺少 SHA-2 补丁 KB4474419"
                else:
                    state["note"] = "安装程序返回 %s%s" % (code, "（等待超时）" if timedout else "")
            except Exception as exc:
                state["note"] = str(exc)
            state["busy"] = False

        threading.Thread(target=worker, daemon=True).start()

    def poll():
        if state["busy"]:
            root.after(400, poll)
            return
        bar.stop()
        if state["ok"]:
            status.configure(text="组件安装完成，正在启动…")
            root.after(1500, root.destroy)
            return
        status.configure(text="安装没有成功：%s\n日志：%s" % (state.get("note", ""), log_path))
        for b in buttons.winfo_children():
            b.configure(state="normal")
        root.after(400, poll)

    def retry():
        do_install(True)

    def manual():
        do_install(False)

    def browse_log():
        try:
            os.startfile(log_path)
        except Exception:
            pass

    tk.Button(buttons, text="重试静默安装", command=retry, bg="#1b2e4d", fg="#ffffff",
              activebackground="#26406a", activeforeground="#ffffff", relief="flat",
              font=("Microsoft YaHei UI", 10), padx=14, pady=4).pack(side="left", padx=6)
    tk.Button(buttons, text="手动安装（显示进度）", command=manual, bg="#1b2e4d", fg="#ffffff",
              activebackground="#26406a", activeforeground="#ffffff", relief="flat",
              font=("Microsoft YaHei UI", 10), padx=14, pady=4).pack(side="left", padx=6)
    tk.Button(buttons, text="打开安装日志", command=browse_log, bg="#16223a", fg="#9fb2d0",
              relief="flat", font=("Microsoft YaHei UI", 10), padx=12, pady=4).pack(side="left", padx=6)

    state["busy"] = False
    root.after(200, lambda: do_install(True))
    root.after(400, poll)
    root.attributes("-topmost", True)
    root.mainloop()

    if not state["ok"]:
        message("缺少 .NET Framework 4.8，而且这次没有装成功。\n"
                "Win7 需要 SP1 + KB4474419；Win8.1 直接装即可。\n"
                "也可以手动下载安装：%s\n安装日志：%s" % (DOWNLOAD_PAGE, log_path))
    return state["ok"]


def ensure_payload():
    target = cache_dir()
    os.makedirs(target, exist_ok=True)
    for res_name, rel in PAYLOAD:
        src = os.path.join(base_dir(), PAYLOAD_DIR, res_name)
        dst = os.path.join(target, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if os.path.isfile(dst) and os.path.getsize(dst) == os.path.getsize(src):
            continue
        shutil.copyfile(src, dst)

    tg_dir = os.path.join(target, "TG频道预览")
    for name in ("victim.jpg", "ending.wav"):
        src = os.path.join(target, name)
        dst = os.path.join(tg_dir, name)
        if os.path.isfile(src) and not os.path.isfile(dst):
            shutil.copyfile(src, dst)


def log(msg):
    """留在缓存目录里的启动外壳日志，出问题能看出走到哪一步。"""
    try:
        with io.open(os.path.join(cache_dir(), "launcher.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    except Exception:
        pass


def restore_desktop():
    """主程序退出后（包括在任务管理器里被强制结束）把桌面图标层放回原样。

    主程序是靠 ShowWindow(SW_HIDE) 藏起 Explorer 的图标窗口来制造“桌面图标没了”的，
    进程一旦被强杀，它自己的收尾根本跑不到，图标就永远不回来。外壳等它退出后补这一步。
    """
    u = ctypes.windll.user32
    targets = []
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(hwnd, _):
        buf = ctypes.create_unicode_buffer(96)
        try:
            u.GetClassNameW(ctypes.c_void_p(hwnd), buf, 96)
        except Exception:
            return True
        if buf.value in ("Progman", "WorkerW"):
            targets.append(hwnd)                         # 整块桌面宿主本身
            view = u.FindWindowExW(ctypes.c_void_p(hwnd), None, "SHELLDLL_DefView", None)
            if view:
                targets.append(view)
                lst = u.FindWindowExW(ctypes.c_void_p(view), None, "SysListView32", None)
                if lst:
                    targets.append(lst)
        elif buf.value in ("SHELLDLL_DefView", "SysListView32"):
            targets.append(hwnd)
        return True

    u.EnumWindows(EnumProc(cb), None)
    for h in targets:
        u.ShowWindow(ctypes.c_void_p(h), 5)              # SW_SHOW
        u.InvalidateRect(ctypes.c_void_p(h), None, True)
        u.UpdateWindow(ctypes.c_void_p(h))

    tray = u.FindWindowW("Shell_TrayWnd", None)          # 任务栏也叫回来
    if tray:
        u.ShowWindow(ctypes.c_void_p(tray), 5)
    try:                                                 # 触发一次桌面重画
        SPI_SETDESKWALLPAPER = 0x0014
        buf = ctypes.create_unicode_buffer(260)
        u.SystemParametersInfoW(0x0073, 260, buf, 0)     # SPI_GETDESKWALLPAPER
        if buf.value:
            u.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, buf.value, 0x01 | 0x02)
    except Exception:
        pass


def main():
    log("启动：%s" % sys.version.replace("\n", " "))
    try:
        log("Release=%d 已安装=%s" % (read_release(), net48_installed()))
    except Exception as exc:
        log("读 Release 失败：%s" % exc)
    resume = "--resume-install" in sys.argv

    if is_win7_sp0():
        log("Win7 SP0，直接退出")
        message("当前 Win7 系统是 SP0（版本 7600），无法运行桌面弹弹弹.exe。\n"
                "请先升级到 Windows 7 SP1 再运行。"
                + open_source_note(), "朋友的酒 · 系统版本不支持")
        return 3

    if is_win7() and not sha2_done():
        patch = install_sha2_patch()
        if patch == "reboot":
            if schedule_resume():
                message("SHA-2 补丁装好了，但需要重启一次才能继续。\n"
                        "已经登记了一次性自启动：重启后会自动继续安装 .NET 4.8 并启动程序。", "朋友的酒 · 继续安装")
                return 0
            message("补丁装好了但需要重启，自动续跑没登记成功。\n重启后请手动再双击一次本程序。", "朋友的酒 · 需要重启")
            return 0
        if patch == "fail":
            message("Win7 需要先装 SHA-2 签名补丁（KB4474419），这次没装上。\n"
                    "程序里的补丁包是 %s，也可以手动双击它安装。\n日志：%s"
                    % (PATCH_MSU, os.path.join(cache_dir(), "launcher.log")), "朋友的酒 · 组件缺失")
            return 1

    if not net48_installed():
        if not install_framework():
            message("缺少 .NET Framework 4.8，组件没有安装成功，程序无法启动。\n"
                    "可以到 %s 手动下载安装。" % DOWNLOAD_PAGE)
            return 1

    try:
        ensure_payload()
    except Exception as exc:
        message("准备文件失败：%s" % exc)
        return 2

    exe = os.path.join(cache_dir(), "桌面弹弹弹.exe")
    try:
        proc = subprocess.Popen([exe] + sys.argv[1:], cwd=cache_dir(), close_fds=True)
    except Exception as exc:
        message("启动失败：%s" % exc)
        return 3

    # 陪着它，等它自己结束或者被人从任务管理器里强制结束，然后把桌面收回来
    log("主程序已启动 pid=%d" % proc.pid)
    try:
        proc.wait()
    except Exception:
        pass
    log("主程序已退出，开始恢复桌面")
    for _ in range(3):
        try:
            restore_desktop()
        except Exception as exc:
            log("恢复桌面出错：" + str(exc))
        time.sleep(1.0)
    log("恢复桌面完成")
    return 0


if __name__ == "__main__":
    sys.exit(main())
