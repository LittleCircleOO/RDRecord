# RDRecord

[English](README.md) | [中文](README_zh.md)

Low-overhead in-game video recorder for **Rhythm Doctor** (BepInEx 5 plugin). Recording starts automatically when the pre-start screen ("press space to begin") appears, and stops after you confirm your rank and leave the level. Video and audio are encoded on-the-fly through dual ffmpeg pipes; final muxing is a lossless `-c copy` that finishes in seconds.

Full design document (Chinese): [`RD内录系统实现方案.md`](doc/RD内录系统实现方案.md).

## Highlights

- **Frame-precise auto triggers** - begins at the pre-start screen, ends after leaving the level scene; retries re-segment automatically
- **Rigid A/V sync** - CFR frame shaping fills capture gaps with repeat frames (skip macroblocks at ~zero bitrate), so the timeline never drifts against audio
- **Crash-safe output** - fragmented MP4 + ADTS; leftovers in `.tmp/` remain playable if the game dies mid-take
- **Smart filename** - `{song}_{difficulty}_{rank}_{date}` by default, rank included automatically
- **Near-zero CPU with hardware encoders** - NVENC / AMF / QSV auto-probed, software x264/x265 as fallback
- **Small files** - mirrors the encoding profile of a known 3.3h/1.04GiB reference recording (no B-frames, 10s GOP ceiling, AAC-LC mono 32kHz)

## Installation

### Option A: build from source

1. Prerequisites: .NET SDK 8 or 10, game with BepInEx 5 (Mono) installed.
2. Copy `Directory.Build.props.example` to `Directory.Build.props` and point `GameExePath` at your game executable (this file is gitignored - machine-local).
3. Build:

   ```
   dotnet build -c Release
   ```

   The build auto-deploys `RDRecord.dll` to `<game>/BepInEx/plugins/RDRecord/`.

### Option B: prebuilt DLL

Copy `RDRecord.dll` into `<game>/BepInEx/plugins/RDRecord/` (any subfolder of `plugins/` works).

### ffmpeg (required at first recording)

No manual setup needed in most cases - the plugin resolves ffmpeg through this chain:

1. Configured `FFmpegPath` (if set)
2. `BepInEx/plugins/RDRecord/bin/ffmpeg[.exe]`
3. System `PATH`
4. Automatic download (`AutoDownloadFFmpeg = true`, default on)
   - Windows: BtbN GPL build (zip)
   - Linux: johnvansickle static build (tar.xz; needs system `tar` + `xz-utils`; auto `chmod +x`)
   - macOS: evermeet.cx build (zip; x86_64, runs under Rosetta on Apple Silicon)

The resolved source and version are logged (`ffmpeg resolved [plugin-bin]: ...`).

## Configuration

File: `BepInEx/config/rd.rdrecord.cfg` (generated on first launch; edit while the game is closed, or use BepInEx Configuration Manager).

### [Recording]

| Key | Default | Values | Notes |
|---|---|---|---|
| `Codec` | `H264` | `H264` / `H265` | H265 ≈ 20% smaller on this content, weaker player/browser support |
| `Encoder` | `Auto` | `Auto` / `Software` / `NVENC` / `AMF` / `QSV` | Auto probes NVENC→AMF→QSV with a real init test, falls back to software |
| `Fps` | `60` | `30` / `60` | 30 matches the reference recording's profile and halves the size |
| `Crf` | `23` | 18–28 | Quality; scales are auto-mapped per encoder (x265 +5, NVENC +9/+12, ...) |
| `OutputDir` | `<plugins>/RDRecord/recordings` | path | Empty falls back to the default |
| `Hotkey` | `F9` | KeyCode | Manual start/stop; empty disables |

### [Trigger]

| Key | Default | Notes |
|---|---|---|
| `AutoTrigger` | `true` | Automatic start/stop from game events |
| `KeepFailedTakes` | `true` | Keep takes that never reached the rank screen (abandoned/failed runs) |
| `FileNameTemplate` | `{song}_{difficulty}_{rank}_{date}` | Tokens: `{song} {artist} {author} {difficulty} {rank} {mistakes} {id} {date}` |

### [FFmpeg]

| Key | Default | Notes |
|---|---|---|
| `FFmpegPath` | *(empty)* | Optional explicit path; searched first when set |
| `AutoDownloadFFmpeg` | `true` | Download ffmpeg when no local copy is found |

### Recommended profiles

| Goal | Settings |
|---|---|
| Smallest files (archive) | `Encoder = Software`, `Codec = H264`, `Fps = 30`, `Crf = 28` |
| Best quality | `Encoder = Software`, `Codec = H264`, `Fps = 60`, `Crf = 18–23` |
| Lowest CPU while playing | `Encoder = NVENC` (or `Auto`), `Fps = 60` |
| Balanced | defaults + `Crf = 26` |

## Recording behavior

| Game event | Action |
|---|---|
| Level loaded, pre-start screen shown (space not pressed) | Start recording (song/difficulty captured for the filename) |
| Rank screen saved | Capture rank/mistakes for the filename |
| Player confirms rank, scene switches out (menu/select/retry) | Stop, mux to disk |
| Retry (fresh scnGame scene) | Segment: close previous take, new take starts at its own pre-start screen |
| Window resolution changed mid-take | Segment, then auto-resume |
| Rank saved but scene never switches (10s) | Safety-net forced stop |
| Hotkey (default F9) | Manual start/stop |

## Output details

- H.264 High (or H.265), `yuv420p`, no B-frames, GOP ceiling 10s, AAC-LC mono 32kHz 126kbps
- CFR output at the configured fps - timeline-accurate against audio; capture hiccups appear as frozen frames, never as drift
- Benchmarks (RTX 3060 Laptop, 1080p, real gameplay content): x264 ≈ 86fps encode / NVENC ≈ 248–290fps at ~zero CPU; NVENC at size parity is slightly softer on moving text edges than x264
- Measured size at defaults (Software/H264/30fps/crf23): ~10 MiB/min on effect-heavy charts, ~1.3 Mbps; quiet charts land lower

## Known limits

- 60fps capture assumes the game itself renders faster than 60fps; below that, repeat-frame shaping keeps sync but motion smoothness follows the game
- Linux auto-download needs `tar`/`xz-utils`; otherwise place ffmpeg manually
- Concurrent hardware-encoder sessions are driver-limited (consumer NVIDIA: 8); if a session cannot start, the take logs the error and keeps crash-safe partials

## License

Plugin code: MIT. ffmpeg is a separate process distributed under its own licenses (GPL builds linked above).
