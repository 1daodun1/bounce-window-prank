# 上传与发布说明

这个文件夹就是可以直接推到 GitHub 的仓库内容（42 个文件，约 2 MB）。成品 exe 太大（309 MB），**不进仓库**，走 Releases。

## 一、推仓库

```bash
cd DesktopJelly-repo
git init
git add .
git commit -m "桌面弹弹弹 · 朋友的酒：开源首版"
git branch -M main
git remote add origin https://github.com/1daodun1/bounce-window-prank.git
git push -u origin main
```

> 仓库里**没有**音频 / 视频 / 帧数据 / exe，`.gitignore` 已经挡住。别人 clone 后按 README 的「重新生成帧序列」自己放素材即可。
> 想让仓库自带素材，请用 Git LFS：`git lfs track "*.wav" "*.mp4" "*.dat"`。

## 二、上传 Release

1. 仓库首页 → **Releases** → **Draft a new release**
2. Tag 填 `v1.0.0`，标题填「桌面弹弹弹 · 朋友的酒 v1.0.0」
3. 附件上传两个成品（Release 单文件上限 2 GB，够用）：

| 附件 | 字节 | SHA-256 |
|---|---|---|
| `桌面弹弹弹.exe` | 308,947,235 | `bd09b7ac97cc9906510f53368e73891e442738f88a2d9ffc5636aa9d9dbe1c4a` |
| `备份-126MB自解压版.exe` | 126,051,840 | `34721b8af32735c5a179dca2483d4d139274330d22be07eb3124295265e9f3fd` |

4. 正文直接粘下面这段：

```markdown
## 桌面弹弹弹 · 朋友的酒 v1.0.0

双击一个 exe，桌面图标和窗口跟着 130 BPM 的音乐一起 Q 弹，整屏撕裂 / 反色 / 融化，弹对话框、变色 CMD、视频小窗，最后一段收尾动画。

**下载**
- `桌面弹弹弹.exe`（309 MB，推荐）：单文件，Win7 SP1/8.1 缺 .NET 4.8 会自己装；Win7 还会先补 SHA-2 补丁，需要重启就一次性自启动续跑。
- `备份-126MB自解压版.exe`（126 MB）：轻量版，目标机必须已装 .NET 4.8。

**要求**：Windows 7 SP1 / 8.1 / 10 / 11，64 位；`%TEMP%` 留约 390 MB。
**退出**：`Ctrl + Alt + Q`。
**注意**：玩笑程序，自解压结构会被 360 误报，加白名单即可；不联网、不改系统文件。

SHA-256:
- 桌面弹弹弹.exe `bd09b7ac…dbe1c4a`
- 备份-126MB自解压版.exe `34721b8a…5e9f3fd`
```

5. 发布后把 README 里的 `https://github.com/1daodun1/bounce-window-prank` 换成实际链接（在 `README.md` 的「开源」一节，以及 `src/launcher.py` 的 `GITHUB_REPO` 常量，两处都改）。

## 三、改完地址要重打一次包

`GITHUB_REPO` 只影响 Win7 SP0 那条提示末尾的一行字，改完重新跑一次 PyInstaller 即可（命令见 README「从源码构建」）。

## 四、版本号命名建议

| Tag | 说明 |
|---|---|
| `v1.0.0` | 首版：帧画面 + 窗口上限 4 + Win7 SP0 拦截 + SHA-2 自动补丁 |
| `v1.1.0` | 后续加功能（新特效 / 新弹窗样式） |
| `v1.0.1` | 只修 bug（安装链、卡顿、索引越界） |
