using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RDRecord.Core;
using RDRecord.Triggers;
using UnityEngine;

namespace RDRecord;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("Rhythm Doctor.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "rd.rdrecord";
    public const string PluginName = "RD Record";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log = null!;
    internal static PluginConfig Cfg = null!;
    internal static RecorderManager Recorder = null!;
    internal static Plugin Instance = null!;

    private Harmony? _harmony;
    private bool _inLevelLast;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Cfg = PluginConfig.Bind(Config);
        Recorder = new RecorderManager();

        _harmony = new Harmony(PluginGuid);
        TriggerPatches.TryPatchAll(_harmony);
        TriggerPatches.LogStatus();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. ffmpeg: {FfmpegBinary.DescribeStatus()}");
    }

    private void Update()
    {
        // manual hotkey toggle
        try
        {
            if (Cfg.Hotkey != KeyCode.None && Input.GetKeyDown(Cfg.Hotkey))
            {
                if (Recorder.IsRecording) Recorder.RequestEnd("hotkey");
                else Recorder.RequestBegin("hotkey");
            }
        }
        catch { /* input system edge cases */ }

        // zero-Harmony belt & suspenders: frame-precise pre-start detection.
        // gameState==PreStart && levelFinishedLoading == exactly the
        // "press space to begin" screen; works regardless of hook status
        // (guards against Mono JIT inlining of the tiny LoadingRoutine stub).
        if (Cfg.AutoTrigger.Value && !Recorder.IsRecording)
        {
            try
            {
                var g = scnGame.instance;
                if (g != null && !g.editorMode
                    && scnGame.levelToLoadSource != LevelSource.CutscenesPath
                    && g.levelFinishedLoading
                    && g.gameState == GameState.PreStart)
                {
                    Recorder.RequestBegin("poll(pre-start)");
                }
            }
            catch { /* instance torn down mid-frame */ }
        }

        // scene-level fallback: end when the level scene is gone
        if (Cfg.AutoTrigger.Value)
        {
            var g2 = scnGame.instance;
            bool inLevel = false;
            try { inLevel = g2 != null && !g2.editorMode; } catch { }
            if (!inLevel && _inLevelLast) Recorder.RequestEnd("poll(left-level)");
            _inLevelLast = inLevel;
        }

        Recorder.Tick();
    }

    private void OnApplicationQuit()
    {
        Recorder.Shutdown(5000);
    }
}
