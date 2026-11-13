# 桌面弹弹弹 · 使用说明与优化报告

最后更新：2026-11-12　当前成品：

| 文件 | 字节 | 说明 |
|---|---|---|
| `桌面弹弹弹.exe` | 191,167,014 | Python 外壳 + 主程序 + 素材 + NetGDI + 官方 .NET 4.8 离线包 |
| `备份-60MB无安装版.exe` | 60,465,664 | C# 自解压壳，不带 .NET 垫包，Win10/11 首选 |

两个包内容同源，差别只在外层壳和有没有 .NET 垫包。**换设备只拷一个 exe**，素材、TG 频道预览、NetGDI 都在里面。

---

## 一、这是什么

一个**没有界面**的桌面整蛊程序：双击 → 说明卡片（800×600，点「运行」）→ 开始。

开始后同时发生：图标 Q 弹、所有可见窗口 Q 弹、视频动态壁纸、按拍点弹原生对话框、按拍点拉起系统小工具、不定时弹出会一直换配色的 CMD 窗口、两个视频小窗、一个跟随鼠标的置顶窗、一直往焦点窗口打字 `78917891`、每拍抖屏/反色/残影/切条、每拍 55% 概率“哔”一声（音高从 do 到高 do 随机）、以及 NetGDI 的七种整屏特效。音乐放完进入收尾：关掉全部弹窗与外部程序 → 收尾音 → 收尾卡片 + 满屏飘字，再留 30 秒后退出（可按退出键提前结束）。

`Ctrl + Alt + Q` 或 `Ctrl + Shift + Q` 随时退出并复原。

---

## 二、双击之后发生了什么（309 MB 版）

1. 外层是 Python 打的壳（`src\launcher.py`，PyInstaller 单文件）。选它是因为主程序是 .NET，真缺框架的机器连报错窗都弹不出来，需要一个不依赖 .NET 的东西先跑。
2. 壳把 payload 解到 `%TEMP%\_MEI*`，查注册表 `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` 的 `Release`：
   - `≥ 528040` → 直接用，**不弹任何安装窗**（Win10/11 走这条）；
   - 不到 → 弹提示窗「你的电脑没有安装 .NET Framework 4.8 / Windows 10 以上一般自带；你的系统看起来低于 Win10，或者没有手动装过。我将会帮你安装，装完会自动启动程序（仍会弹一次管理员确认，请点“是”）」，用包里垫的微软官方离线包 `ndp48.exe`（121,346,568 字节，签名 Valid）以 `runas` + `/quiet /norestart` 静默安装，装完复查注册表再启动主程序。
3. payload 解到 `%LOCALAPPDATA%\朋友的酒`（按当前用户名走，不写死 `Admin`）；已存在且大小一致就跳过。
4. 启动主程序 → 说明卡片 → 点「运行」正式开始。

**空间要求**：`%TEMP%` 所在盘（默认 C 盘）309 MB 版约需 390 MB、126 MB 版约需 190 MB。

> 说明：真正缺 .NET 的机器（Win7/8.1）这条分支这台机器上验不了（自带 4.8），下面的修复是按 VM 报的现象做的。

### .NET 4.8 安装这一段（2026-11-13 重做）

VM 上的现象：外层能正常释放 `ndp48.exe`，点 UAC「是」之后仍然提示“安装没有成功”。原来的写法有三个盲点：不认返回码、不看安装日志、失败只给一句淡淡的话。现在改成：

| 改动 | 说明 |
|---|---|
| 认返回码 | `0` / `3010` / `1641` 都算安装程序跑完（后两个=需要重启，窗口会写明“重启后再运行”）；`1602`=被取消；`1603`=典型 Win7 缺 SP1 或 SHA-2 补丁 `KB4474419` |
| 两次尝试 | 先 `/quiet /norestart`（静默）；失败后窗口不关，留「重试静默安装」和「手动安装（显示进度）」两个按钮，手动那条走 `/passive`，微软自己的进度条会显示出来，卡在哪一步一眼能看到 |
| 日志可见 | 安装日志固定写 `%LOCALAPPDATA%\朋友的酒\dotnet_install.log`，路径直接印在窗口上，另有「打开安装日志」按钮 |
| 等待有上限 | `WaitForSingleObject` 从“无限等”改成 30 分钟；拿不到进程句柄时退化成固定等待再查注册表 |
| 不再藏窗口 | 管理员调用从 `SW_HIDE` 改成 `SW_SHOWNORMAL`，UAC 之后的报错框不会再被藏掉 |
| 启动留痕 | 每次启动先往 `launcher.log` 写一行 `Release=… 已安装=…`，VM 上先看这个数就知道是不是没装上 |

### Win7 SP0 直接拦掉

Win7 不装 SP1 的话，SHA-2 补丁和 .NET 4.8 都装不上，硬跑只会一路弹错。现在启动第一步先读注册表 `CurrentBuildNumber`：

- `7600`（或更低）= SP0 → 直接弹窗「当前 Win7 系统是 SP0（版本 7600），无法运行桌面弹弹弹.exe。请先升级到 Windows 7 SP1 再运行」，退出码 3，不碰任何安装流程；
- `7601` = SP1 → 继续走 SHA-2 → .NET → 主程序。
- 非 Win7 系统直接跳过这段判断（本机 Win11 build 22631 实测 `sp0=False`）。

弹窗里预留了一行开源致谢：`src\launcher.py` 顶部的 `GITHUB_REPO` 填上仓库地址后，会显示「该程序已开源，希望你能支持一下：<地址>」；地址留空时这一行不出现。

### Win7 的 SHA-2 补丁：包里带了，需要重启就自动续跑

Win7 缺 SHA-2 签名支持时，微软的 `ndp48.exe` 会自己退出来（表现就是“点了是还是没装上”）。现在把官方离线补丁 `windows6.1-kb4474419-v3-x64.msu`（55.8 MB，微软官方源）直接垫进包里：

1. 启动先判断是不是 Win7；是且还没装过 → 用 `wusa.exe /quiet /norestart` 静默安装，成功后在缓存目录写 `sha2.done` 记号，下次不再重复装。
2. 返回 `3010` / `1641`（装好了但要重启）→ 写一条 **RunOnce**（`HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce`，值名「朋友的酒组件安装续跑」），命令是「本 exe `--resume-install`」。RunOnce 由系统执行一次后自动删除，正是“一次性自启动”。
3. 重启后 RunOnce 拉起本程序 → 继续装 .NET 4.8 → 装完直接解 payload、起主程序、弹说明页，不需要用户再点任何东西。
4. .NET 自己返回 `3010` 时也走同一条续跑路径。

仍然要满足：Win7 必须是 SP1（SP0 装不上任何 4.8 相关补丁）。

---

## 三、怎么用

| 操作 | 说明 |
|---|---|
| 双击 `桌面弹弹弹.exe` | 立即开始，无需设置 |
| `Ctrl + Alt + Q` / `Ctrl + Shift + Q` | 退出并复原（图标 / 窗口 / 壁纸 / 屏幕）。全局热键 + 每帧 `GetAsyncKeyState` 双保险 |
| 任务管理器强杀 | 守护进程 `朋友的酒守护.exe` 存活，主进程一退它把桌面图标层显示出来并重绘 |
| 双击 `src\build.cmd` | 重编主程序 + NetGDI 宿主 → `%TEMP%\sfx_build\app_res.exe`、`ngdi.exe` |
| 双击 `src\build_sfx.cmd` | 重打 60 MB 自解压包 → `%TEMP%\sfx_build\sfx_out.exe` |
| 单独跑 `TG频道预览\TgPreview.exe` | 只演收尾那段（卡片 + 飘字），Esc 关闭 |

命令行参数（排障用）：

```
桌面弹弹弹.exe                          正常运行
桌面弹弹弹.exe --demo 11 --log a.txt     跑 11 秒自动退出并写日志
桌面弹弹弹.exe --bench                  内置基准，输出 CSV
桌面弹弹弹.exe --victim                 只演收尾卡片（Esc / 120 秒）
桌面弹弹弹.exe --frametest              只把 mp4 帧序列铺满屏幕 10 秒（验帧画面用）
```

**没有配置文件**。所有参数硬编码在 `src\JellyAll.cs` 的常量块里，ini 文件与代码里的 ini 引用已全部删除。

---

## 四、它同时在做这些事（全部一起开，没有时间排班）

1. **图标 Q 弹**：接管桌面图标层，原图标隐藏，屏上只有会弹的图标；整数像素落位、底边固定、标签按资源管理器规则断行。
2. **窗口 Q 弹**：所有可见窗口同一套公式（竖 ±振幅、横反向 0.72 倍）；运行中新开的窗口半秒内加入；最大化窗口被压回屏幕 70%。
3. **视频动态壁纸**：挂在图标下面，每 0.5 秒与音频对轴。
4. **按拍点弹原生对话框**：每个拍点 30% 概率（黄叹号 / 红报错 / 蓝问号 / 纯文字），标题正文都是「朋友的酒」，随机落位，上限 5 个，超了关最早的。
5. **按拍点拉起系统小工具**：35% 概率从白名单拉程序，**上限 4 个**；开着第 5 个时先把最早那个关掉再开新的（`PopWindows.Tick` 里 `while (open.Count >= max)` → 关最早 → Spawn），所以恒定 4 个。白名单从 50 项砍到 **19 项**（System32 里实际存在 16 个），只留一打开就有内容的：notepad、mspaint、calc、charmap、osk、magnify、dxdiag、msinfo32、winver、snippingtool、taskmgr、cleanmgr、resmon、regedit、write、control、mstsc、eventvwr、perfmon。之前那批「一排空白小窗」就是 `*.msc`、`mmc.exe`、`SystemProperties*.exe` 这类打开后空控制台/空属性页的程序，已清掉。
6. **变色 CMD 窗口**：`sheng` 一个 `col_loop.bat`，24 组配色循环（`color` 换前景+背景），每步打一行 `FRIENDS WINE [ 色号 ]`，约 1 秒一换；用 `CreateProcess` 起 `cmd.exe /c title 朋友的酒 & bat`，`SW_SHOWNOACTIVATE` 不抢焦点，520×300 随机位置。最多 3 个，**关掉任意一个 → 结束一个「自动打开应用」拉起的程序**（`PopWindows.KillOne()`：WM_CLOSE → CloseMainWindow → Kill）。实测：关窗后 3 秒 `cleanmgr.exe` 从 1 变 0。
7. **两个视频小窗 + 跟随鼠标的置顶窗**：小窗播 mp4，每两拍平滑换落点、始终夹在屏幕内；跟随窗标题「《朋友的酒》已入侵你的电脑」，内容也是视频。
8. **一直打字**：每 0.1 秒一个字符，`78917891` 循环，`keybd_event`。
9. **整屏 GDI 特效**：**每拍**同时抖 ±4px + 整屏反色 + 残影 + 切条，不再按拍号轮流；**常驻撕裂从第 1.2 秒就开始**（原来要等第 47 秒）。前台窗口那块只贴左右两边，弹窗本身不被撕碎。
10. **NetGDI 七种特效**（`NgrixLibs.NetGDI`）：反色 / 模糊 / 圆形隧道 / 阴影画刷 / 图案画刷 / 颜色滤镜 / 屏幕融化，全部 `Enabled = true`、`Interval = 70ms`。
11. **每拍“哔”一声**：55% 概率，音高从 523 / 587 / 659 / 698 / 784 / 880 / 988 / 1047 Hz（do re mi fa sol la si 高do）随机挑，时长 90–180 ms，后台线程播放不阻塞节拍。
12. **桌面帧画面（把 mp4 当 GUI 特效直接画在屏幕上）**：`41834646747-1-192.mp4` 用 ffmpeg 按 `fps=25, scale=640:360, -q:v 7` 拆成 **4683 张 jpg**（187.31 秒，正好对上 187.371 秒的音乐），放在项目根 `视频帧\`；打包版内嵌成 `frames.dat`（自定义容器：`FRMD` + 帧数 + 每帧「长度 + JPEG」）。运行时 `src\FrameStage.cs` 按音乐位置取帧：`idx = floor(position*25) % 4683`，节流 0.04 秒（即 25fps）。**画法**：整屏位图交给一层 `WS_EX_LAYERED + WS_EX_TRANSPARENT` 的穿透窗口（`UpdateLayeredWindow`），并插到壁纸 WorkerW 的正上方——压在壁纸之上、桌面图标与所有窗口之下，鼠标点击照样穿过去。**为什么不用 BitBlt 屏幕 DC**：桌面每帧都在重绘，直接往屏幕 DC 贴会被立刻擦掉（实测就是这样，`--frametest` 里能看见、整程序里全没了）。**为什么不跟主循环**：本机主循环被特效拖到 1 fps 时，一秒钟才画一帧，等于没有；所以帧序列自己跑一条 25fps 的线程，位置从主循环的 `PositionNow` 取，主循环再卡画面也在走。
13. **收尾**：音乐放完 → 关全部弹窗与外部程序（含 CMD、NetGDI 宿主）→ 放 `ending.wav` → 收尾卡片 + 满屏飘字（全部置顶压在卡片上）→ 3.4 秒后定格 30 秒（随时可按退出键）→ 退出。

---

## 五、节奏怎么保证严格跟音乐

- 音频 `reference-audio.wav`（44.1 kHz / 16 bit 立体声 / 187.371 秒），130 BPM，406 拍，拍长 0.4615 秒。
- **不用计时器推节奏**：每帧读「音频已播到第几个采样」算相位，机器卡一下下一刻立刻回到该在的位置。
- 图标、窗口、弹窗、小工具、CMD、屏幕特效、哔声共用这一个相位。
- 视频是唯一不共相位的（MP4 与 WAV 时长差 0.06 秒），只做周期性兜底对齐。

### 为什么撕裂不会被刷掉

撕裂开始只在开头拍一次快照，之后不再整屏还原；贴条时避开前台窗口那几行；退出时才贴回快照并整屏重画。

---

## 六、NetGDI 七种特效是怎么接进来的（含踩坑）

**坑**：把 `NetGDI.dll` 直接加载进主程序进程，`Enabled` 全设 true、日志写「全部开启」，抓帧却完全看不出效果——主程序自己的 GDI 绘制每帧都在把它压掉。

**现在的做法**：单独进程。

- `src\NgdiHost.cs` 编出 `NgdiRun.exe`（4 KB），loading `NetGDI.dll`，七项全开，主循环等父进程 PID 消失后自己 `Enabled = false`。
- 主程序 `src\NetGdiFx.cs`：`Enable(dir)` 用 `Process.Start(NgdiRun.exe, 当前PID)` 拉起；`Disable()` 直接 Kill 并等 1.5 秒。
- 主程序自己的 `ScreenFx` 照旧每拍叠一层，两套同时压屏。
- payload 里带 `NetGDI.dll` + `NgdiRun.exe`；解到缓存目录后主程序按同目录找。

**实测**：demo 第 5 秒抓帧，全屏反色块 + 系统图标平铺 + 横向切条错位 + 融化同时可见，`tasklist` 有 `NgdiRun.exe`；退出后桌面重绘正常。

---

## 七、自解压包里装了什么

根目录 `备份-126MB自解压版.exe`（C# 自解压壳）内嵌 9 份资源（不含 .NET 垫包与 SHA-2 补丁），解到 `%LOCALAPPDATA%\朋友的酒`：

| 内嵌资源名 | 解出来的文件名 | 字节 |
|---|---|---|
| `app.exe` | `桌面弹弹弹.exe` | 315,392 |
| `audio.wav` | `reference-audio.wav` | 33,052,266 |
| `video.mp4` | `41834646747-1-192.mp4` | 24,800,694 |
| `victim.jpg` | `victim.jpg` | 82,494 |
| `ending.wav` | `ending.wav` | 1,997,354 |
| `tgpv.exe` | `TG频道预览\TgPreview.exe` | 18,432 |
| `ngdi.dll` | `NetGDI.dll` | 18,944 |
| `ngdi.exe` | `NgdiRun.exe` | 4,096 |
| `frames.dat` | `frames.dat` | 65,580,882 |
| `windows6.1-kb4474419-v3-x64.msu` | `windows6.1-kb4474419-v3-x64.msu` | 55,853,327 |

解完还会把 `victim.jpg`、`ending.wav` 各拷一份进 `TG频道预览\`（那个小工具按自己文件夹找素材）。
309 MB 版 payload 共 11 项（比自解压版多 `NetGDI.dll`、`NgdiRun.exe`、`frames.dat`、`ndp48.exe`、`windows6.1-kb4474419-v3-x64.msu`）。

### 打包的坑（都踩过，别再踩）

1. **`csc` 读中文资源路径必挂**（`CS1566 读取资源文件…系统找不到指定的文件`）：中文路径经 cmd 传递后编码被打乱；而 `.cmd` 里的中文 `copy` 在 GBK 脚本里会静默失败。做法：**先用 python 把素材复制进 `%TEMP%\sfx_build\`（纯 ASCII 路径）**，再让 `csc` 按 `%WORK%` 里的 ASCII 名打包。
2. **`.cmd` 必须 GBK + CRLF**：写脚本时若用 `io.open()` 默认读法，CRLF 会被转成 LF，重写后整个批处理开始报 `'y' is not recognized`、`'opy' is not recognized` 这种碎裂错误，却还照样往下跑。修法：读写都用 `newline=""`，或统一 `replace('\n','\r\n')` 后以 `wb` 写 GBK。
3. **C 盘紧张时编译会假失败**：磁盘只剩 80 MB 时 `copy` 静默失败，`csc` 随后报 `CS1566`，看起来像路径问题。做法是把 `TEMP`/`TMP` 指到别的盘再编译（实测 `E:\tb`），并先清 `%TEMP%\_MEI*`（每个约 190 MB）。

### 外壳图标

`build_sfx.cmd` 第三步编 `Sfx.cs` 时必须带 `/win32icon:"app.ico"`，否则内层有图标、外层是默认图标。

---

## 八、磁盘空间：为什么以前会弹“内存不足”

旧版检查「载荷 + 200 MB 余量」= 260 MB，机器只剩 229 MB 时直接判定不够退出，跟 360、跟特效无关。

现在：余量降到 **40 MB**，载荷只统计真正还缺的部分（缓存里已存在且大小一致的不计入）。

实测：60 MB 版在 215 MB 空闲时可启动；191 MB 版在 481 MB 空闲时跑通，跑完 `_MEI` 自己清。

---

## 九、参数（硬编码在 `src\JellyAll.cs`）

| 常量 | 值 | 含义 |
|---|---|---|
| `bpm` | 130 | 每分钟拍数 |
| `beats` | 2 | 两拍一弹（周期 0.923 秒） |
| `iconAmount` | 0.38 | 图标弹幅 |
| `windowAmount` | 0.08 | 窗口弹幅 |
| `stagger` | true | 图标/窗口错拍 |
| `audioOn` / `windowsOn` / `videoOn` | true | 音乐 / 真窗口 / 视频壁纸 |
| `taskbarOn` | false | 任务栏不动（Win11 不可靠） |
| `popOn` / `popAt` / `popChance` / `popCap` | true / 0 / 0.3 / 5 | 原生对话框 |
| `typeOn` / `typeText` | true / `78917891` | 虚拟按键 |
| `sfxOn` / `sfxPx` | true / 4 | 整屏特效与抖动像素 |
| `appsOn` / `appsChance` / `appsCap` | true / 0.35 / 4 | 自动打开应用（满 4 个后每开一个先关最早的） |
| CMD 首弹 / 间隔 / 上限 | 0.6+rnd(0,2) 秒 / 16+rnd(0,22) 秒 / 3 | 变色 CMD |
| 图标窗首弹 / 间隔 / 上限 | 0.8+rnd(0,2) 秒 / 9+rnd(0,16) 秒 / 4 | 图标弹窗 |

---

## 十、优化报告

### 10.1 渲染层（1.6 那一轮的骨架，仍在用）

原版每帧清空整块分层表面、重画全部标签、逐个浮点缩放图标、整帧上传。改成「脏矩形增量 + 预渲染缓存」：标签只画一次存进背景位图；图标按像素尺寸缓存缩放；每帧只算「旧矩形 ∪ 新矩形」；尺寸未变直接返回。

同指纹 A/B（同机、同基准代码、81 合成图标、300 帧、x64 Release）：

| 指标 | 原版 1.5 | 1.6 | 变化 |
|---|---|---|---|
| 单帧绘制 `paint_mean` | 4.638 ms | 1.870 ms | −60% |
| 绘制中位数 / P95 | 4.461 / 5.819 ms | 1.535 / 4.766 ms | −66% / −18% |
| 单帧总耗时 `frame_mean` | 5.321 ms | 2.488 ms | −53% |
| 空转吞吐 `loop_fps` | 187.9 | 401.6 | +114% |
| GDI 句柄峰值 | 3→105 | 3→23 | −78% |
| 残影检查 | PASS（0 像素） | PASS（0 像素） | 一致 |

`cpu_single_core_pct` 口径不准，**已弃用**。

### 10.2 外观对齐

- 图标按真实字形（48 px）画，底边对齐字形底边；标签宽度按格宽（76 px），最多两行。
- 字体：拉丁 Segoe UI Variable Text，中文 Microsoft YaHei UI，缺失回退 Segoe UI → 微软雅黑 → 黑体；字号 12 px。
- 分层窗口用不了 ClearType，改 `AntiAlias` + 1 px 深色四向描边。
- GDI+ `DrawString` 会留内边距并丢掉末字，改成自己逐字测宽、按格宽断行。

### 10.3 打包与启动链

| 项 | 改动 | 证据 |
|---|---|---|
| TG 频道预览进包 | `Sfx.cs` 加解包目标 + `Directory.CreateDirectory` + 素材补拷 | 清空缓存后跑包，`TG频道预览\` 齐全 |
| 外壳图标 | `build_sfx.cmd` 补 `/win32icon:"app.ico"` | 从成品抽图标是动漫头像 |
| 空间预检 | 余量 200 → 40 MB，载荷只算真缺的 | 215 MB 空闲可启动；此前 229 MB 误报 |
| .NET 真装外壳 | `launcher.py` 查注册表 → 缺则静默装 → 解 payload → 起主程序 | Win11 三次启动不弹安装窗，主程序正常 |
| 删 ini | 参数硬编码、两条打包链与缓存/根目录的 ini 全删、代码里零引用 | `Get-ChildItem *.ini` 递归为 0 |
| 源码归档 | 30 个文件移入 `src\`，主程序 exe 留在项目根目录 | 根目录只剩 exe / 素材 / 文档 |
| NetGDI | 七种特效经独立宿主进程接入 | 第 5 秒抓帧全屏撕裂+反色+图标平铺 |
| 帧画面 | `FrameStage.cs` 把 4683 张 640×360 jpg 按 25fps 铺满屏幕；两条包都内嵌 `frames.dat` | `--frametest` 两张截图整屏都是 mp4 画面且帧号前进；`--demo` 日志出现「已加载 4683 帧」 |
| 安装链 | `launcher.py` 认返回码 / 失败可重试 / 可手动装 / 日志上屏；Win7 先装 SHA-2 补丁，需重启就写 RunOnce 续跑 | `py_compile` 通过；本机已装 4.8、非 Win7，缺框架与重启续跑分支未实机验 |
| 帧窗口 | `FrameStage` 改成穿透分层窗口（插在壁纸 WorkerW 之上）+ 独立 25fps 线程 | `--demo` 第 6 秒整屏都是 mp4 画面（此前整程序里完全看不到帧） |

### 10.4 已知限制（Windows 本身边界）

- 最大化窗口不参与（压回 70%）；最小化不参与；全屏独占游戏跳过。
- 改真实窗口尺寸必然触发重排，浏览器/聊天软件会边弹边闪边卡。
- 图标是分层窗口，用不了 ClearType，字比桌面略柔。
- 视频按 UniformToFill 铺满，片源非 16:10 会裁边；另有动态壁纸软件（如 Lively）会抢同一层。
- 309 MB 版每次启动要把 309 MB 解到 `%TEMP%`，首次慢十几秒。
- NetGDI 用全局 GDI 钩子，安全软件可能拦；被拦时只是少了这七种特效，程序本身照跑。
- 帧画面是 640×360 拉到全屏，放大后偏软；要更清晰得把帧尺寸提上去，包体随比例涨（62 MB → 约 2.3 倍）。
- 帧画面现在由自己的 25fps 线程推进，主循环卡不卡都不影响它；只有 CPU 真被占满时才会掉帧。
- 帧窗口在「壁纸之上、图标与窗口之下」，所以撕屏/反色那类整屏特效会短暂盖住它，下一帧补回。
- 未验证：多显示器 / 混合 DPI；连续跑数小时的内存曲线；Win7 真缺 .NET 的安装分支。

---

## 十一、出问题怎么办

| 现象 | 处理 |
|---|---|
| 弹「因内存不足而无法启动」 | 看 `%TEMP%` 所在盘空间，不是内存；清临时文件或换 60 MB 版 |
| 快捷键没反应 | 试 `Ctrl+Shift+Q`；不行就任务管理器结束进程，守护进程会把图标放回来 |
| 退出后桌面还留着特效残影 | 等 1～2 秒；仍不对就再跑一次并正常退出（会重绘桌面并重套壁纸） |
| 窗口卡在变形尺寸 | 再跑一次并按 `Ctrl+Alt+Q`，会重新记录尺寸并复位 |
| 打字没反应 | 虚拟按键送给焦点窗口，切到记事本再看 |
| 没有变色 CMD 窗口 | 看缓存目录有没有 `col_loop.bat`；安全软件拦 `cmd.exe` 也会没有 |
| 没有 NetGDI 那层特效 | 看缓存目录是否有 `NetGDI.dll` + `NgdiRun.exe`；被安全软件拦就只剩本地 GDI 特效 |
| 安全软件提示 | 新编 exe 常被短暂锁定，等十几秒；持续拦截需自行放行 |
| 屏幕没有视频画面 | 看缓存目录有没有 `frames.dat`（62 MB）；日志里应有「桌面帧画面：已加载 4683 帧」 |
| .NET 装不上 | 打开 `%LOCALAPPDATA%\朋友的酒\dotnet_install.log`；Win7 先确认 SP1 + `KB4474419`；窗口里点「手动安装（显示进度）」看卡在哪一步 |

---

## 十二、文件清单

```
桌面弹弹弹.exe                 308,946,312  A 版成品（Python 壳 + 主程序 + 素材 + NetGDI + 帧数据 + .NET 4.8 + SHA-2 补丁）
备份-126MB自解压版.exe          126,049,280  C# 自解压壳成品（无 Python 壳、无 .NET 垫包）
reference-audio.wav           33,052,266  音乐，唯一时钟
41834646747-1-192.mp4         24,800,694  壁纸 / 小窗视频
victim.jpg                        82,494  收尾卡片照片
ending.wav                     1,997,354  收尾音
视频帧\                        4,683 张 640×360 jpg（25fps 帧序列，共 62 MB）
frames.dat                    65,580,882  同一批帧打成单个容器，供两条打包链内嵌
redist\windows6.1-kb4474419-v3-x64.msu  55,853,327  Win7 的 SHA-2 补丁（微软官方离线包）
TG频道预览\                      独立预览工具
redist\ndp48.exe             121,346,568  微软官方 .NET 4.8 离线包（签名 Valid）

src\（30 个文件，全部源码与打包脚本）
  DesktopJelly.cs  JellyLayer.cs  JellyAll.cs  DesktopHost.cs  WpfVideo.cs
  VideoWindows.cs  CursorFollow.cs  IconWindow.cs  PopWindows.cs  CmdColor.cs
  MsgBoxPop.cs  KeySpam.cs  ScreenFx.cs  NetGdiFx.cs  NgdiHost.cs  VictimWindows.cs
  Intro.cs  WindowGuard.cs  SetupGate.cs（当前无调用）  Sfx.cs  FrameStage.cs
  WaveAudio.cs  LocalAudioPlayer.cs  PerformanceLog.cs  Benchmark.cs  JellyWindows.cs
  build.cmd  build_sfx.cmd  launcher.py  app.ico  DesktopJelly.manifest  NetGDI.dll
```

回滚：
- 主程序：`src\build.cmd` → `%TEMP%\sfx_build\app_res.exe`
- 自解压包：`src\build_sfx.cmd` → `%TEMP%\sfx_build\sfx_out.exe`（需先把 `frames.dat`、`ngdi.dll` 用 python 预置进 `%TEMP%\sfx_build`），手工拷到根目录
- 309 MB 包：先按 11 项拼 payload（app.exe/audio.wav/video.mp4/victim.jpg/ending.wav/tgpv.exe/NetGDI.dll/NgdiRun.exe/frames.dat/ndp48.exe/windows6.1-kb4474419-v3-x64.msu），再

```
python -m PyInstaller --noconfirm --clean --onefile --windowed --icon app.ico ^
  --name DesktopJellyBoot --add-data "<payload 目录>;payload" launcher.py
```

---

## 十二点五、崩溃兜底（2026-11-13 加）

- `JellyAll.Guard`：`AppDomain` 未处理异常统一接住，写 `error.log` 后先把 NetGDI、整屏特效、按键、任务栏、桌面图标全部复位，再让进程退——不会再出现“程序已停止工作”把桌面留在撕烂状态的情况。
- 每个 WPF 线程（收尾卡片、视频窗、跟随窗、图标窗、说明页）的 `Dispatcher` 都挂了 `UnhandledException`，Timer 回调里抛异常只记录、不结束进程。
- 主循环整段包了 `try / catch`：单帧异常只跳过这一帧并记一行日志，索引越界不会再把整场演出带走。
- 看门狗 `--restore` 读矩形文件时逐行 `try/catch`，半行损坏只跳过那一行；`--demo` 写错数字当没给。
- **蓝屏已删除**（`BlueScreen.cs` 与 `--bluetest` 一起移除）：收尾只剩卡片 + 飘字，定格 30 秒后正常退出，不会再出现任何蓝屏画面。
- 帧画面的内存 DC 与 GDI 位图都在 `FrameStage.Shutdown()` 里回收；解码失败只丢这一帧，不会让主循环抛异常。

---

## 十三、验证方式

```
桌面弹弹弹.exe --demo 11 --log 运行日志.txt
```

日志里能直接读到：BPM 与周期、视频路径、对话框/自动打开应用/变色 CMD 的策略、NetGDI 是否开启、图标/窗口数量、每秒 fps、窗口还原成功数，最后一行是汇总。

自测联动（关 CMD 杀一个程序）：运行后等 `cmd.exe` 出现，`taskkill /pid <pid>`，再看白名单程序的进程数是否少一个。

收尾单独验：双击 `TG频道预览\TgPreview.exe`，Esc 关闭。

帧画面单独验：

```
桌面弹弹弹.exe --frametest
```

10 秒内整屏应该是 mp4 的画面（不是壁纸），抓两张截图能看出帧号在前进；退出码 0 表示帧序列加载成功，4 表示没找到 `视频帧\` 或 `frames.dat`。
