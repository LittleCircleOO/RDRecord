# RDRecord

[English](README.md) | [中文](README_zh.md)

Rhythm Doctor 低占用内录 BepInEx 5 插件：预备屏（"按空格继续"）出现自动开录，确认评级、退出关卡场景后自动停录。音视频经双 ffmpeg 管道实时编码，停录以无损 `-c copy` 秒级混流。

完整设计文档：[`RD内录系统实现方案.md`](doc/RD内录系统实现方案.md)。

## 特性

- **帧级精确自动触发**——预备屏开录、退出关卡场景停录；重试自动分段
- **刚性音画同步**——CFR 帧率整形以重复帧填补采集空洞（skip 宏块近零码率），时间轴绝不漂移
- **崩溃安全**——fMP4 分片 + ADTS；游戏中途崩溃时 `.tmp/` 残留文件仍可播放
- **智能命名**——默认 `{song}_{difficulty}_{rank}_{date}`，Rank 自动写入文件名
- **硬件编码器 CPU 近零**——NVENC / AMF / QSV 自动探测，软件 x264/x265 兜底
- **小体积**——复刻一份 3.3 小时 1.04GiB 参考录像的编码特征（无 B 帧、10 秒 GOP 上限、AAC-LC 单声道 32kHz）

## 安装

### 方式 A：源码构建

1. 前置：.NET SDK 8 或 10，游戏已装 BepInEx 5（Mono）。
2. 复制 `Directory.Build.props.example` 为 `Directory.Build.props`，将 `GameExePath` 指向你的游戏 exe（该文件已 gitignore，仅存于本机）。
3. 构建：

   ```
   dotnet build -c Release
   ```

   编译产物自动部署到 `<游戏目录>\BepInEx\plugins\RDRecord\`。

### 方式 B：直接放置 DLL

把 `RDRecord.dll` 放入 `<游戏目录>\BepInEx\plugins\RDRecord\`（`plugins/` 下任意子目录均可）。

### ffmpeg（首次录制时需要）

多数情况无需手动配置——插件按以下链路解析：

1. 配置的 `FFmpegPath`（可选，显式指定）
2. `BepInEx/plugins/RDRecord/bin/ffmpeg[.exe]`
3. 系统 `PATH`
4. 自动下载（`AutoDownloadFFmpeg = true`，默认开启）
   - Windows：BtbN GPL 构建（zip）
   - Linux：johnvansickle 静态构建（tar.xz；需系统 `tar` + `xz-utils`；自动 `chmod +x`）
   - macOS：evermeet.cx 构建（zip；x86_64，Apple Silicon 下经 Rosetta 运行）

实际命中的来源与版本写入日志（`ffmpeg resolved [plugin-bin]: ...`）。

## 配置

文件：`BepInEx/config/rd.rdrecord.cfg`（首次启动生成；关游戏后编辑，或用 BepInEx Configuration Manager）。

### [Recording]

| 键 | 默认值 | 可选值 | 说明 |
|---|---|---|---|
| `Codec` | `H264` | `H264` / `H265` | H265 在本内容上省约 20% 体积，播放器/浏览器兼容性弱 |
| `Encoder` | `Auto` | `Auto` / `Software` / `NVENC` / `AMF` / `QSV` | Auto 按真实初始化探测 NVENC→AMF→QSV，全失败回退软件 |
| `Fps` | `60` | `30` / `60` | 30 与参考录像参数一致，体积减半 |
| `Crf` | `23` | 18–28 | 质量基准；各编码器刻度自动映射（x265 +5、NVENC +9/+12 等） |
| `OutputDir` | `<plugins>/RDRecord/recordings` | 路径 | 留空回退默认 |
| `Hotkey` | `F9` | KeyCode | 手动开/停；留空禁用 |

### [Trigger]

| 键 | 默认值 | 说明 |
|---|---|---|
| `AutoTrigger` | `true` | 跟随游戏事件自动开停录 |
| `KeepFailedTakes` | `true` | 保留未到结算屏的段（放弃/失败的对局） |
| `FileNameTemplate` | `{song}_{difficulty}_{rank}_{date}` | 可用 token：`{song} {artist} {author} {difficulty} {rank} {mistakes} {id} {date}` |

### [FFmpeg]

| 键 | 默认值 | 说明 |
|---|---|---|
| `FFmpegPath` | *(空)* | 可选显式路径；设置后最优先探测 |
| `AutoDownloadFFmpeg` | `true` | 本地未找到 ffmpeg 时自动下载 |

### 推荐组合

| 目标 | 设置 |
|---|---|
| 最小体积（归档） | `Encoder = Software`，`Codec = H264`，`Fps = 30`，`Crf = 28` |
| 最高画质 | `Encoder = Software`，`Codec = H264`，`Fps = 60`，`Crf = 18–23` |
| 游玩时 CPU 最低 | `Encoder = NVENC`（或 `Auto`），`Fps = 60` |
| 均衡 | 默认 + `Crf = 26` |

## 录制行为

| 游戏事件 | 动作 |
|---|---|
| 谱面加载完成、预备屏出现（未按空格） | 开录（曲名/难度写入文件名） |
| 结算屏保存成绩 | 记录 Rank/mistakes（进文件名） |
| 玩家确认评级、场景切出（菜单/选曲/重试） | 停录、混流落盘 |
| 重试（新 scnGame 场景） | 分段：旧段收尾，新段由其自身预备屏开录 |
| 窗口分辨率变化 | 分段后自动续录 |
| 成绩保存 10s 仍未切场景 | 安全网强制停录 |
| 热键（默认 F9） | 手动开/停 |

## 输出细节

- H.264 High（或 H.265）、`yuv420p`、无 B 帧、GOP 上限 10 秒、AAC-LC 单声道 32kHz 126kbps
- CFR 恒定帧率输出——与音频时间轴严格对齐；采集卡顿表现为静帧，绝不漂移
- 基准（RTX 3060 Laptop，1080p 真实对局内容）：x264 约 86fps 编码 / NVENC 248–290fps 且 CPU 近零；同体积下 NVENC 动态文字边缘略逊 x264
- 默认档实测体积（Software/H264/30fps/crf23）：特效密集谱面约 10 MiB/min（~1.3 Mbps）；安静谱面更低

## 已知边界

- 60fps 采集假定游戏本身渲染高于 60fps；不足时重复帧整形保持同步，但流畅度跟随游戏
- Linux 自动下载依赖系统 `tar`/`xz-utils`；缺失时手动放置 ffmpeg
- 硬件编码器并发会话受驱动限制（消费级 NVIDIA 为 8 路）；会话启动失败时记录错误并保留崩溃安全的部分文件

## 许可

插件代码：MIT。ffmpeg 为独立进程，遵循其自身许可（上文链接均为 GPL 构建）。
