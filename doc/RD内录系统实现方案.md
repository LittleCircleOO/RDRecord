# Rhythm Doctor 内录系统实现方案

基于 BepInEx 插件 + Unity 进程内钩子 + ffmpeg 子进程编码的低占用内录系统设计文档。

设计目标:在玩家正常游玩(实时、有声、不掉帧)的前提下,产出与参考录像
(2160x1080@30fps, 3h18m, 1.04GiB, ~751kbps 总码率)
同压缩特征的低体积带音频录像。

---

## 目录

1. [架构总览](#1-架构总览)
2. [视频帧捕获(方案乙)](#2-视频帧捕获方案-乙)
3. [音频捕获](#3-音频捕获)
4. [ffmpeg 拓扑与编码参数](#4-ffmpeg-拓扑与编码参数)
5. [L1 音画同步](#5-l1-音画同步)
6. [BepInEx 集成与自动触发](#6-bepinex-集成与自动触发)
7. [ffmpeg 二进制获取策略](#7-ffmpeg-二进制获取策略)
8. [生命周期与边界情况](#8-生命周期与边界情况)
9. [配置项参考](#9-配置项参考)
10. [验证清单](#10-验证清单)
11. [演进路线](#11-演进路线)

---

## 1. 架构总览

```
┌────────────────── Rhythm Doctor 进程 (BepInEx 5 注入) ──────────────────┐
│                                                                         │
│ [Harmony 自动触发] LoadingRoutine 完成且 ──开录──▶ scnBase.Start 且        │
│        │            未按空格(预备屏)          非 scnGame(已退出)          │
│        ▼                                                                  │
│ [捕获组件] (运行时 AddComponent, 不改游戏文件)                           │
│                                                                         │
│   VideoCaptureBehaviour (协程, WaitForEndOfFrame):                      │
│     backbuffer → RT 拷贝 → (30fps 时按时间轴抽帧)                       │
│     → AsyncGPUReadback(RGBA) ─→ 有界队列(8帧) ──┐                      │
│                                                 │                      │
│   AudioCaptureBehaviour (挂 AudioListener):     ▼                      │
│     OnAudioFilterRead → 预分配环形缓冲(4MB) → 写管线线程               │
│                                                                         │
└────────────┬──────────────────────────────────┬────────────────────────┘
             │ stdin: rawvideo RGBA             │ stdin: f32le PCM
             ▼                                  ▼
   ┌──────────────────────┐        ┌──────────────────────┐
│ ffmpeg #1 (视频,常驻) │        │ ffmpeg #2 (音频,常驻) │
│ x264 veryfast        │        │ aac 126k mono 32k    │
│ bf=0 g=300 crf=23    │        │ -f adts              │
│ → video.tmp.mp4 (fMP4)│        │ → audio.tmp.aac      │
│ └──────────┬───────────┘        └──────────┬───────────┘              │
              └──────────────┬────────────────┘
                             ▼ 停录时 (秒级, 无重编码)
              ffmpeg #3: -c copy 混流 + itsoffset 对轨
                             ▼
              {曲目}_{时间戳}.mp4  (≈751kbps 级别, 带音轨)
```

核心原则:

- 游戏进程只做"搬运"(拷贝 + 回读 + 管道写入),压缩计算 100% 在 ffmpeg 进程内
- 全程不在游戏渲染呈现链路插入任何等待点,输入延迟零增加
- 所有中间产物崩溃安全(fMP4 碎片 + ADTS 裸流)
- 编码参数逐项对齐参考文件的码流特征

双 ffmpeg 进程的原因:.NET `Process` 只暴露一条 `StandardInput` 管道,无法向同一
ffmpeg 进程喂双 stdin;视频音频各一个进程、停录时 `-c copy` 秒级混流,比参考项目
"录完 WAV 再统一编码"更快,且无中间大文件。

---

## 2. 视频帧捕获(方案: 乙)

### 2.1 选型结论

采用 **`WaitForEndOfFrame` 协程 + `ScreenCapture.CaptureScreenshotIntoRenderTexture`**。

| 对比项 | 甲: 相机 OnRenderImage | **乙: WaitForEndOfFrame(选用)** |
|---|---|---|
| 拿到的内容 | 仅该相机渲染结果 | **最终合成画面**(全部相机 + Overlay UI + OnGUI) |
| 判定文字/Combo 等 Screen Space Overlay UI | **缺失**(引擎直接画进 backbuffer) | 完整包含 |
| 侵入性 | 改变相机渲染路径,可能与同类模组冲突 | 零侵入,呈现之后才动手 |
| 管线相关性 | 仅内置管线 | 管线无关 |
| 时序 | 帧内(Present 前) | 帧尾(Present 后),天然"每呈现一帧回调一次" |

对音游录像而言判定信息是画面核心,甲方案拿不到 Overlay UI,一票否决。
甲方案保留为配置项兜底(`CaptureMode=CameraFallback`),防游戏更新引入兼容问题。

### 2.2 捕获协程逻辑

```csharp
// 伪代码骨架
IEnumerator CaptureLoop() {
    var rt = new RenderTexture(Screen.width, Screen.height, 0,
                               RenderTextureFormat.ARGB32);
    var wait = new WaitForEndOfFrame();
    double acc = 0.0;                 // 30fps 模式的抽帧累计器
    double lastDsp = AudioSettings.dspTime;

    while (recording) {
        yield return wait;            // 每个呈现帧唤醒一次(游戏通常 60fps)

        // ── 帧率策略 ──
        if (targetFps == 30) {
            double now = AudioSettings.dspTime;
            acc += now - lastDsp; lastDsp = now;
            if (acc < 1.0/30.0) continue;   // 未满 1/30s, 跳过本帧, 零开销
            acc -= 1.0/30.0;
        }
        // 60fps 模式: 不抽帧, 每个呈现帧都捕获(见 2.3)

        ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);  // backbuffer→RT
        var req = AsyncGPUReadback.Request(rt, 0, OnReadback); // 异步回读
        // 立即返回, 主线程继续跑下一帧游戏逻辑
    }
}
```

### 2.3 帧率预设: 30 / 60 fps(用户可选)

| | **30fps(默认)** | **60fps** |
|---|---|---|
| 捕获策略 | 时间轴抽帧:累计 dspTime 达 1/30s 才拷贝+回读,其余帧协程直接 continue | 每个呈现帧都拷贝+回读(要求游戏本身跑满 60) |
| GPU 额外开销 | ~0.2-0.5ms/捕获帧,且一半的呈现帧零开销 | 同量级但每帧发生,GPU 余量小的机器注意 |
| 回读带宽(RGBA) | 2160x1080 ≈ 280MB/s | ≈ 560MB/s(PCIe 3.0 x16 仍 <4%) |
| 编码参数差异 | `g=300`(GOP 上限 10s) | `g=600`(同为 10s 上限,场景切换检测照常自动插 I) |
| 码率上限 | `-maxrate 2M -bufsize 4M` | `-maxrate 4M -bufsize 8M` |
| 预期体积 | 与参考文件同量级(~751kbps) | 约 1.3~1.6 倍 |
| 适用场景 | 谱面复盘、存档(推荐,和参考文件一致) | 需要逐帧细看判定/特效时 |

注意事项:

- 60fps 模式下若游戏实际帧率不稳(55~60 波动),CFR 整形以重复帧填补,
  成片帧率恒定,掉帧段表现为静帧,不产生音画漂移
- 抽帧累计器使用 `AudioSettings.dspTime`(硬件时钟,单调稳定),不用
  `Time.unscaledTime`(可被参考项目那类时间补丁改写)
- 回读完成回调滞后 1~3 帧,属正常;按请求序号保序入队,迟到的直接丢弃并计数

### 2.4 队列与背压

- `ConcurrentQueue<byte[]>`,容量 8 帧(2160x1080 RGBA ≈ 8.9MB/帧,总驻留 <75MB;
  1080p 下 <19MB)
- 预分配帧对象池,回读复用,**录制路径零 GC 分配**
- 队列满 → 丢最旧帧 + 计数告警(原则:保游戏不保录像)
- 写管线线程 `stdin.Write` 阻塞时同样触发上述背压,不会反压到主线程

---

## 3. 音频捕获

在 `AudioListener` 所在 GameObject 运行时 `AddComponent` 挂捕获组件:

```csharp
void OnAudioFilterRead(float[] data, int channels) {
    // data = 最终混音(BGM+key音+SE, 音量已应用), 交错 float[-1,1],
    //        采样率 = AudioSettings.outputSampleRate(通常 48kHz)
    // 只做: Buffer.BlockCopy 到预分配环形缓冲 → 唤醒写线程
    // 禁止: 分配、锁等待、调用 Unity 主线程 API
}
```

关键性质:

- **串联抽头,数据过手不改**——扬声器输出与没有插件时逐比特一致,玩家正常听音。
  参考项目 RD.ChartRendering 的"录制时无声"是因为它用的独占式
  `AudioRenderer` API(启动即静音引擎输出),本方案不用该 API,问题不存在
- 玩家用系统混音器静音游戏时,此链**照常收到数据**(取自 DSP 图而非系统输出)
- 环形缓冲 4MB(48kHz 立体声 float ≈ 21 秒余量),写线程跟不上丢最旧并计数
- 旁路录制**不需要**参考项目里 PauseGuard/AutoplayGuard/时间重写/输入模拟
  那一整套补丁——那是"驱动游戏"的离线渲染器才需要的东西

---

## 4. ffmpeg 拓扑与编码参数

### 4.1 视频进程(常驻 #1)

```
ffmpeg -hide_banner -loglevel error
  -f rawvideo -pix_fmt rgba -s {W}x{H} -r {FPS}
  -i pipe:0
  -vf format=yuv420p
  -c:v libx264 -preset veryfast -profile:v high
  -bf 0 -g {300 或 600}
  -crf 23 -maxrate {2M 或 4M} -bufsize {4M 或 8M}
  -fps_mode passthrough
  -movflags +frag_keyframe+empty_moov+default_base_moof
  -y video.tmp.mp4
```

**重要实测修正(2026-08)**:rawvideo demuxer 会自行按 `-r` 生成递增 PTS,
`-use_wallclock_as_timestamps` 仅在 PTS 为 NOPTS 时生效、对此输入**永远不参与**
(实测 1140 包 PTS 间隔全部精确等于 1/r)。因此管道必须接收严格恒定节奏的帧流,
恒速职责由插件侧 **CFR 帧率整形**承担(见 5.1)。

参数与参考文件码流特征的对应关系:

| 参数 | 参考文件实测特征 | 作用 |
|---|---|---|
| `-bf 0` | has_b_frames=0 | 无 B 帧,编码/解码低延迟,实时录制标准做法 |
| `-g 300/600`(scenecut 默认开) | GOP avg≈221 / max=300 / min=4 | 最长 10 秒 I 帧间隔;场景切换自动补 I 即 min=4 的来源 |
| `-preset veryfast` | SEI: me=hex subme=2, CABAC=1, 8x8dct=1, trellis=1 | 快速运动估计 + 熵编码端优化全开,与原编码器同档 |
| `-profile:v high` | H.264 High@L5.0 | 启用 8x8dct 等 High Profile 工具 |
| `-crf 23 -maxrate …` | 静止段总码率 ~500-800kbps,P 帧中位数 1KB | 静止画面自动塌缩为 skip 宏块(近零码率),突变段上限兜底 |
| `-vf format=yuv420p` | yuv420p(4:2:0) | 色度子采样,RGB→YUV 转换在 ffmpeg 进程内完成 |
| `-fps_mode passthrough` | — | 透传 demuxer 的时间轴(CFR,见上方修正说明) |
| fMP4 movflags | — | 崩溃/强退也有可播放的碎片文件 |

说明:

- v1 直接回读 RGBA,游戏端零像素格式转换代码;RGB→YUV 由 ffmpeg 做,代价是
  ffmpeg 进程内一次 CPU 转换,可接受
- v2 优化项(GPU shader 转 I420 全范围再回读,输入改 `-pix_fmt yuvj420p`,
  回读带宽 -62%,色彩与参考文件 yuvj420p/full-range 完全一致)见第 11 节

### 4.2 音频进程(常驻 #2)

```
ffmpeg -hide_banner -loglevel error
  -f f32le -ar 48000 -ac 2 -i pipe:0
  -c:a aac -profile:a aac_low -ac 1 -ar 32000 -b:a 126k
  -f adts -y audio.tmp.aac
```

对齐参考文件音频特征:AAC-LC、单声道、32kHz、126kbps。
下混与重采样由 ffmpeg 完成,游戏端只搬运 float PCM。
ADTS 裸流逐帧自包含,天然抗崩溃。

### 4.3 混流进程(停录时 #3,纯 copy)

```
ffmpeg -i video.tmp.mp4 -itsoffset {t0a - t0v} -i audio.tmp.aac
  -c copy -movflags +faststart
  -y {输出目录}/{曲目}_{yyyyMMdd-HHmmss}.mp4
```

`OFFSET = t0a - t0v`(见第 5 节),通常在 ±20ms 内。秒级完成,零重编码。

---

## 5. L1 音画同步

方案 = **CFR 帧率整形 + 首帧锚点混流**,不写 sidecar、不烧录任何隐藏信息。

### 5.1 机制(2026-08 实测修订版)

```
开录顺序:
  1. 启动 ffmpeg #1(视频)、#2(音频)
  2. 每个捕获帧携带采集时刻 t(rawvideo 管道无时间戳通道,时序由插件维护)
  3. 视频写线程 CFR 整形:
       idx = round((t - t0v) * fps)     ← t0v = 首帧采集时刻
       idx 跳跃(游戏呈现率不足/丢帧产生的空洞) → 用重复帧填补
       x264 将重复帧编码为 skip 宏块,体积成本≈0,
       掉帧段在成片中表现为静帧(诚实记录),时间轴刚性恒定
  4. 音频环形缓冲连续消费,首字节写入时记 t0a

停录混流:
  -itsoffset (t0a - t0v): 音频首样本(真实时刻 t0a)对齐到
  视频首帧(PTS=0, 真实时刻 t0v)的轴上
  误差来源 = 首帧采集→写入管道的延迟 vs 首样本写入时刻的差 → 数十毫秒级
```

背景:原设计依赖 `-use_wallclock_as_timestamps` 实现 VFR 直录,实测发现
rawvideo demuxer 自行按 `-r` 生成递增 PTS,该选项永远不生效;当游戏实际
呈现率 < 2×目标帧率时,抽帧产出率低于声明帧率(如 50fps 呈现 → 25fps 采集),
CFR 打点导致视频整体加速、音画线性漂移(实测 45s 录制漂移 7s)。
CFR 整形从机制上根治:管道节奏恒定 = 声明帧率,音画同步不再依赖任何
ffmpeg 侧时间戳行为。

### 5.2 边界与修正

- 成片为 **CFR**(帧率恒定=配置值),Premiere 等剪辑软件原生兼容,无需转换
- 若个别机器出现可感知偏差(如 key 音偏 40ms),事后修正零成本:
  `ffmpeg -itsoffset 0.040 -i out.mp4 -c copy out_fixed.mp4`
- 帧号→时刻映射由 CFR 整形的 idx 计算天然保证,无需额外记录

---

## 6. BepInEx 集成与自动触发

### 6.1 插件形态

- BepInEx 5 标准 `BaseUnityPlugin`(RD 为 Mono 运行时,与参考项目同生态)
- 游戏文件零改动:捕获组件运行时 `AddComponent`,钩子 Harmony 运行时挂
- 依赖仅 Harmony(BepInEx 自带),无其他运行时依赖

### 6.2 触发钩子

钩子点调研来源:对 GitHub 上五个 RD BepInEx 插件的 Harmony 补丁面交叉验证——
RhythmDoctor.Archipelago(联网多世界,关卡开始/完成语义最苛刻)、
RhythmDoctorOnline(多人同步)、CurrentLevelInfo(关卡信息导出,与录制命名同构)、
RhythmDoctorTrainer、RDGameplayPatches。

#### 6.2.1 开录(主钩子): "伸手/按空格继续"预备画面出现时刻

证据链来源:`raf13lol/RDMultiplayer` 的 `patches/PreStartScreen.cs`(多人模组,
必须在预备屏上同步全体玩家的开始输入,对这些状态的语义依赖最严格):

- 预备屏状态判据 = `scnGame.instance 存在 && !startTheGameCalled`
  (该项目全部预备屏 UI 逻辑以 `!game.startTheGameCalled` 为门;
  `SharedPackets.cs` 亦用该字段防重复开始——它是"是否已按空格"的官方标志位)
- `scnGame.LoadingRoutine` 协程完成 = 加载结束、预备屏就绪
  (RDMultiplayer 用完全相同的后缀包装手法,在加载完成后立即显示预备屏帮助文本)
- 右边界 `scnGame.StartTheGame(float speed)` = 玩家按下空格、谱面真正开始
  (MyseIfRDPatches SpeedChange 与 RDMultiplayer 均挂此点)
- 预备屏提示文本 = scnGame 私有字段 `beginLevel`(RDMultiplayer 经 AccessTools
  读取该字段恢复提示文案);过滤器 `!editorMode && != CutscenesPath` 为该项目
  `scnGame.Awake` 补丁的原版写法

```csharp
// 开录: 加载完成、进入预备屏(尚未按空格) —— RDMultiplayer 验证的包装手法
[HarmonyPatch(typeof(scnGame), "LoadingRoutine")]
static IEnumerator Postfix(IEnumerator __result, scnGame __instance) {
    while (__result.MoveNext()) yield return __result.Current;   // 等原协程跑完
    if (__instance.editorMode) yield break;                      // 编辑器试玩不录
    if (scnGame.levelToLoadSource == LevelSource.CutscenesPath)  // 过场不录
        yield break;
    if (!__instance.startTheGameCalled)      // 玩家尚未按空格 → 正处预备屏
        Recorder.BeginTake();                // 伸手/提示画面自此入镜(静帧=skip宏块,白送)
}
```

明确不采用(证据存在但时序未钉死,不做主钩子,如实记录):

| 候选 | 状态 |
|---|---|
| `scrHandController.SlideIn` / `ShowHandButtons` | RDMultiplayer patch 过,确属预备屏流程,但相对提示文本出现的精确时序无项目依据 |
| `EnsureSwitchPlayersState` | CurrentLevelInfo 用于读谱面元数据;在加载流程中的精确位置无依据,仅保留做命名信息读取 |
| `scnGame.Start` / `Awake` | 场景加载起点,早于加载完成,画面尚未就绪 |

### 6.2.2 停录(主钩子): 玩家确认评级、画面退出到谱面外之后

证据:CurrentLevelInfo 在 `scnBase.Start` 且非 scnGame 时写 "Not in a level"——
即**到达关卡外场景(选曲/菜单)的时刻**,与"推后一个状态"严格一致。
失败/中途退出/重试所有路径最终都收敛到场景切换,无需逐路径挂停录钩子。

```csharp
// 停录(主信号): 已离开关卡场景
[HarmonyPatch(typeof(scnBase), "Start")]
static void Postfix(scnBase __instance) {
    if (__instance is not scnGame || __instance.editor != null)
        Recorder.EndTake();      // 评级展示+玩家浏览+退出过渡全程入镜
}
```

配套机制:

- **重试分段**: 重试会重载 scnGame 场景,新场景 `is scnGame` 不触发停录;
  由 6.2.5 的 BeginTake 幂等处理(先停旧段正常混流,再开新段)
- **元数据捕获(不承担停录)**: rank/mistakes 在
  `Rankscreen.ShowAndSaveRank` Prefix 抓取(Archipelago 验证),供文件命名;
  注意类名漂移——MyseIfRDPatches 挂的是 `HUD.ShowAndSaveRank`/`HUD.AdvanceGameover`,
  Archipelago/ChartRendering 挂 `Rankscreen.*`,实现时两者都试,兼容不同游戏版本
- **安全网**: `ShowAndSaveRank` 后 10s 仍未收到场景切换(异常路径)→ 强制停录
- 原信号 B/C/D(`EndLevel`/`FailLevel`/`Quit`)降级为日志观测点,不再承担停录
  (它们都早于结算屏结束,与"推后一个状态"的需求冲突;其后续必然走到场景切换)

#### 6.2.3 零 Harmony 兜底层(抗游戏更新/抗 JIT 内联)

主线程每帧轮询,两路信号:

```
开录(帧级精确): scnGame.instance != null
  && !editorMode && levelToLoadSource != CutscenesPath
  && levelFinishedLoading && gameState == GameState.PreStart   ← 正是预备屏
停录: scnGame.instance null 化状态迁移(与主停录钩子语义等价)
```

实测注记(2026-08):Harmony 协程包装写法(pass-through postfix,官方语义,
HarmonyX 2.9.0 源码确认存在;RDMultiplayer 在旧 Unity 上有效)在 Unity 6000.3 +
BepInEx 5.4.23 环境下对该目标静默无效——LoadingRoutine 迭代器 stub 仅约 15 字节
IL,Mono JIT 编译调用方 scnGame.Start 时将其内联,绕过 detour(同环境 scnBase.Start
等大方法补丁正常)。故 gameState 轮询并非"降级兜底"而是**与钩子并列的主路径**,
两路幂等竞争,先到先得;LoadingRoutine 包装保留为快路径。

所有 Harmony 补丁逐个独立 try/catch 挂载(Trainer 的做法):单个方法位移
只损失对应信号,轮询层保证基本功能永不失效。

#### 6.2.4 曲目命名信息(开录/停录时读取)

CurrentLevelInfo 项目给出的完整读取姿势:

| 来源 | 字段 | 文件名素材 |
|---|---|---|
| 通用 | `scnGame.instance.levelIdentifier` | 自定义关 ID(如 `samurai-technz`) |
| 主线 | `scnGame.internalIdentifier` + `RDString.Get("levelSelect." + id)` | 本地化曲名 |
| 自定义 | `currentLevel.data.settings` 的 `.song/.artist/.author/.difficulty` | 曲名/作者/难度 |
| 成绩(结算时,见 6.2.2 元数据捕获) | `GetRankFromMistakes()` + `mistakesManager.mistakes` | rank/错误数 |

成品命名建议:`{曲名}_{难度}_{Rank}_{yyyyMMdd-HHmmss}.mp4`
(活动/赛事投稿场景 Rank 直接进文件名;取不到的字段逐级降级,最终退回纯时间戳)。

#### 6.2.5 分段

二次进入开录钩子 = 换曲/重试(RD 重开关卡会重载场景,分辨率可能变化)。
BeginTake 幂等:已在录 → 先停当前段(正常收尾混流)再开新段。

### 6.3 健壮性

- 全部 Harmony 补丁独立挂载,单个失败仅损失对应信号(见 6.2.3)
- 手动热键兜底:默认 `F9` 开始/停止,可在配置改键
- 与参考项目那类"离线渲染器"模组共存:本插件为纯被动旁路,不劫持输入、
  不改时间流、不拦截暂停,理论冲突面接近零

---

## 7. ffmpeg 二进制获取策略

按"用户自装为主、插件可自动拉取"设计:

### 7.1 固定位置

```
BepInEx/plugins/RD.Recorder/bin/ffmpeg.exe      (Windows)
BepInEx/plugins/RD.Recorder/bin/ffmpeg          (Linux/macOS)
```

配置项 `FFmpegPath` 可覆盖。

### 7.2 启动自检

- 文件不存在,或 `ffmpeg -version` 输出的版本号与锁定版本不符 → 触发获取流程
- 校验 sha256(版本清单内置于插件),失败拒绝使用并报错

### 7.3 自动拉取(需用户在配置中显式同意 `AutoDownloadFFmpeg=true`)

| 平台 | 来源 | 处理 |
|---|---|---|
| Windows x64 | BtbN/FFmpeg-Builds(GPL release, 固定 tag) | 下载 zip → 抽取 ffmpeg.exe → 删包 |
| Linux x64 | johnvansickle.com static | tar.xz 解包 |
| macOS | evermeet.cx | zip 解包 |

- 单文件直链 + 固定版本 + sha256 清单,下载进度写日志
- 首次下载失败 → 热键开录时明确报错并提示手动放置路径,不静默失败

### 7.4 合规

ffmpeg 以独立进程运行,与插件无链接关系(管道字节流交互),GPL 二进制独立分发,
闭源/混合许可无传染问题。

---

## 8. 生命周期与边界情况

| 场景 | 处理 |
|---|---|
| 正常停录 | 关视频管道 → 关音频管道 → `WaitForExit`(各自 ≤5s) → 混流 → 删临时文件 → 日志输出成品路径+体积+码率 |
| 录制中退出游戏 | `OnApplicationQuit` 内同步走停录;fMP4+ADTS 碎片文件可直接播放,文档附手工混流命令 |
| 分辨率/窗口变化 | `Screen.width/height` 与当前 ffmpeg 进程参数不符 → 自动分段(旧段正常收尾,新段起新进程) |
| 游戏内暂停 | 继续录:静态帧全部成为 skip 宏块,码率≈0,暂停段被如实记录 |
| 视频队列满 | 丢最旧帧+计数(保游戏不保录像),日志汇总丢帧数 |
| 音频环形缓冲满 | 丢最旧+计数;DSP 回调内绝不做阻塞操作 |
| 磁盘不足 | 开录前检查剩余空间(按码率上限估算);混流前二次检查 |
| 曲名获取失败 | 按 6.2.4 的字段链逐级降级,最终退回 `{yyyyMMdd-HHmmss}.mp4` 时间戳命名 |
| 游戏帧率 < 目标帧率 | CFR 整形自动补重复帧,时间轴恒定,掉帧段为静帧,音画不漂移 |
| IL2CPP 版 RD(若未来出现) | 本方案基于 Mono/BepInEx 5;届时需 BepInEx 6 + Il2CppInterop,架构不变、绑定层重写 |

---

## 9. 配置项参考

`BepInEx/config/RD.Recorder.cfg`:

```ini
[Recording]
; 帧率预设: 30 或 60
Fps = 30
; 质量: CRF 值, 越小画质越高体积越大(18~28)
Crf = 23
; 输出目录(空 = BepInEx/plugins/RD.Recorder/recordings)
OutputDir =
; 手动热键(默认 F9)
Hotkey = F9

[Trigger]
; 跟随游戏事件自动开停录(见 6.2)
AutoTrigger = true
; 失败/中途退出的段是否保留成品文件(false = 删临时文件不上传混流)
KeepFailedTakes = true
; 成品文件名模板, 可用 {song}{artist}{difficulty}{rank}{mistakes}{date}
FileNameTemplate = {song}_{difficulty}_{rank}_{date}

[FFmpeg]
; ffmpeg 路径(空 = 插件 bin 目录下查找)
FFmpegPath =
; 允许插件联网自动下载 ffmpeg 二进制
AutoDownloadFFmpeg = false

[Advanced]
; 捕获模式: WaitForEndOfFrame(默认) / CameraFallback
CaptureMode = WaitForEndOfFrame
; 视频帧队列上限(帧)
VideoQueueSize = 8
```

---

## 10. 验证清单

录制约 10 分钟素材后,以下指标应与参考文件特征一致:

```bash
ffprobe -v error -show_format -show_streams out.mp4
```

| 检查项 | 期望值 |
|---|---|
| 视频编解码器 / Profile | h264 High |
| has_b_frames | 0 |
| GOP(avg/max) | ≈220/300(30fps)或 ≈440/600(60fps),min 因 scenecut 可远小于均值 |
| 像素格式 | yuv420p(v2 优化后为 yuvj420p) |
| 帧率模式 | CFR(恒定=配置值,重复帧填洞) |
| 音频 | aac LC, mono, 32000 Hz, ~126kbps |
| 总码率(静态谱面段) | 500~800 kbps(30fps) |
| P 帧大小中位数(抽样) | 1~2 KB(`ffprobe -show_packets` 抽查) |
| 音画同步 | 人工核对:首个 key 音与判定闪光对齐,误差不可感知(<1 帧级) |
| 崩溃安全 | 录制中 kill 游戏进程,video.tmp.mp4 仍可播放且音画完整 |

---

## 11. 演进路线

| 版本 | 内容 |
|---|---|
| v1 | 本文档全部内容:RGBA 回读 + 双进程 ffmpeg + L1 同步 + 自动触发 |
| v2 | GPU 像素格式转换:shader RGBA→I420(full range)后回读,ffmpeg 输入改 `-pix_fmt yuvj420p`;回读带宽 -62%,色彩与参考文件完全一致 |
| v2.x | 硬编探测:启动时 `ffmpeg -encoders` 探测 nvenc/amf/qsv,配置项可选,低配机 CPU 减负 |
| v3(可选) | L2 精校:sidecar CSV 记录 (frame_idx, dsp_time, audio_samples),毫秒级事后修正;MKV attachment 挂录制元数据 |

---

## 附:成品对照

| | 参考文件 | 本方案预期(30fps) |
|---|---|---|
| 分辨率 | 2160x1080(游戏窗口原生) | 同游戏窗口 |
| 帧率 | 30 | 30(或 60) |
| 总码率 | 751 kbps | 同量级(500~800kbps 随内容) |
| GOP | avg 221 / max 300 | 30fps: 同参数复刻;60fps: g=600 等比 |
| B 帧 | 无 | 无 |
| 音频 | AAC-LC mono 32k 126k | 完全一致 |
| 音画同步 | 录制端一次成型 | L1 墙钟 PTS + itsoffset 混流 |
| 3.3 小时体积 | 1.04 GiB | ≈ 同量级 |
