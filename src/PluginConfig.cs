using BepInEx.Configuration;

namespace RDRecord;

internal sealed class PluginConfig
{
    public ConfigEntry<string> Codec = null!;
    public ConfigEntry<string> Encoder = null!;
    public ConfigEntry<int> Fps = null!;
    public ConfigEntry<int> Crf = null!;
    public ConfigEntry<string> OutputDir = null!;
    public ConfigEntry<string> HotkeyName = null!;
    public ConfigEntry<bool> AutoTrigger = null!;
    public ConfigEntry<bool> KeepFailedTakes = null!;
    public ConfigEntry<string> FileNameTemplate = null!;
    public ConfigEntry<bool> LocalizeNames = null!;
    public ConfigEntry<string> FFmpegPath = null!;
    public ConfigEntry<bool> AutoDownloadFFmpeg = null!;
    public ConfigEntry<bool> DumpFirstFrame = null!;

    public UnityEngine.KeyCode Hotkey { get; private set; }

    /// <summary>Resolved at runtime (RD.ChartRendering RenderPaths-style): the user's
    /// Videos/RDRecord folder; falls back to the plugin dir when the OS reports no
    /// Videos folder (e.g. headless Linux without xdg-user-dirs).</summary>
    internal static string ResolveDefaultOutputDir()
    {
        string videos = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos);
        return string.IsNullOrWhiteSpace(videos)
            ? System.IO.Path.Combine(BepInEx.Paths.PluginPath, "RDRecord", "recordings")
            : System.IO.Path.Combine(videos, "RDRecord");
    }

    public static PluginConfig Bind(ConfigFile cfg)
    {
        var c = new PluginConfig
        {
            Codec = cfg.Bind("Recording", "Codec", "H264",
                new ConfigDescription("Video codec. H264: best compatibility for sharing. H265: ~20% smaller on this content, heavier CPU (~2-3x), weaker browser/player support.",
                    new AcceptableValueList<string>("H264", "H265"))),
            Encoder = cfg.Bind("Recording", "Encoder", "Software",
                new ConfigDescription("Encoder backend. Auto: probe NVENC->AMF->QSV, fall back to software (x264/x265) if none initializes. Software: always libx264/libx265 (best compression per bitrate). NVENC/AMF/QSV: force a specific hardware encoder (fails the take if unavailable).",
                    new AcceptableValueList<string>("Auto", "Software", "NVENC", "AMF", "QSV"))),
            Fps = cfg.Bind("Recording", "Fps", 60,
                new ConfigDescription("Target capture framerate preset (30 or 60).",
                    new AcceptableValueList<int>(30, 60))),
            Crf = cfg.Bind("Recording", "Crf", 23,
                new ConfigDescription("x264 CRF quality. Lower = better quality / larger file (18-28).",
                    new AcceptableValueRange<int>(18, 28))),
            OutputDir = cfg.Bind("Recording", "OutputDir", "",
                "Output directory for finished recordings. Empty = user's Videos/RDRecord folder (plugin folder fallback when the OS has none). Environment variables are expanded."),
            HotkeyName = cfg.Bind("Recording", "Hotkey", "F9",
                "Manual start/stop hotkey (UnityEngine.KeyCode name, empty to disable)"),
            AutoTrigger = cfg.Bind("Trigger", "AutoTrigger", true,
                "Automatically start/stop recording from game events (pre-start screen -> left level scene)."),
            KeepFailedTakes = cfg.Bind("Trigger", "KeepFailedTakes", true,
                "Keep takes that never reached the rank screen (abandoned/failed runs)."),
            FileNameTemplate = cfg.Bind("Trigger", "FileNameTemplate", "{song}_{difficulty}_{player}_{defib}_{rank}_{date}",
                "Output name template. Tokens: {player} {song} {artist} {author} {difficulty} {defib} {defib1} {defib2} {rank} {mistakes} {id} {date}. " +
                "{player} = Steam persona name (\"-\" when Steam is absent); {difficulty} = chart's own label; {defib} = player judgement config (single-player: P1 value, two-player: P1+P2)."),
            LocalizeNames = cfg.Bind("Trigger", "LocalizeNames", true,
                "Render {difficulty} and {defib} tokens as localized display names (game language, e.g. \"普通\") when true; as raw enum values (e.g. \"Normal\") when false."),
            FFmpegPath = cfg.Bind("FFmpeg", "FFmpegPath", "",
                "Optional explicit ffmpeg path. Search order: this path -> BepInEx/plugins/RDRecord/bin -> system PATH -> download (if enabled)."),
            AutoDownloadFFmpeg = cfg.Bind("FFmpeg", "AutoDownloadFFmpeg", true,
                "Download ffmpeg automatically when no local copy is found (Windows: BtbN GPL zip; Linux: johnvansickle tar.xz, needs tar+xz-utils; macOS: evermeet.cx zip)."),
            DumpFirstFrame = cfg.Bind("Diagnostics", "DumpFirstFrame", false,
                "Write the first captured frame (pre-flip raw readback bytes, Unity texture semantics) as a dot-prefixed PNG next to the recordings, to diagnose flipping/color issues.")
        };
        c.Hotkey = System.Enum.TryParse<UnityEngine.KeyCode>(c.HotkeyName.Value, true, out var kc) ? kc : UnityEngine.KeyCode.None;
        c.HotkeyName.SettingChanged += (_, _) =>
        {
            c.Hotkey = System.Enum.TryParse<UnityEngine.KeyCode>(c.HotkeyName.Value, true, out var k2) ? k2 : UnityEngine.KeyCode.None;
        };
        return c;
    }
}
