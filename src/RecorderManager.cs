using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;

namespace RDRecord.Core;

/// <summary>Main-thread state machine. Requests come from Harmony hooks,
/// hotkey, polling fallback or capture callbacks; Tick() serializes them.</summary>
internal sealed class RecorderManager
{
    private TakeController? _take;
    private VideoCaptureBehaviour? _video;
    private AudioCaptureBehaviour? _audio;
    private readonly List<Thread> _finalizers = new();
    private int _seq;
    private string _pendingBegin = "";
    private string _pendingEnd = "";
    private float _audioAttachRetryUntil;

    internal bool IsRecording => _take != null;

    internal string OutputDir
    {
        get
        {
            var dir = Plugin.Cfg.OutputDir.Value;
            if (dir.Length == 0) dir = Path.Combine(Paths.PluginPath, "RDRecord", "recordings");
            return dir;
        }
    }

    // -------- requests (Unity main thread only) --------

    public void RequestBegin(string reason)
    {
        if (_take == null && _pendingBegin.Length == 0) _pendingBegin = reason;
    }

    public void RequestEnd(string reason)
    {
        if (_take != null && _pendingEnd.Length == 0) _pendingEnd = reason;
    }

    public void OnRankSaved(string rank, int mistakes)
    {
        var t = _take;
        if (t == null) return;
        t.Rank = rank;
        t.Mistakes = mistakes;
        t.RankSavedAt = UnityEngine.Time.unscaledTime;
        Plugin.Log.LogInfo($"rank captured: {rank} ({mistakes} mistakes)");
    }

    // -------- main-thread tick --------

    public void Tick()
    {
        if (_take == null)
        {
            if (_pendingBegin.Length > 0)
            {
                var r = _pendingBegin; _pendingBegin = "";
                DoBegin(r);
            }
            return;
        }

        var take = _take;

        // audio attach retry (listener may appear a few frames late)
        if (_audio == null && UnityEngine.Time.unscaledTime < _audioAttachRetryUntil)
            TryAttachAudio(take);

        // safety net: rank saved but scene switch never arrived within 10s
        if (take.Rank != null && UnityEngine.Time.unscaledTime - take.RankSavedAt > 10f)
            RequestEnd("safety-deadline(rank+10s)");

        // re-segment (resolution changed): end then resume immediately (same level, meta still valid)
        if (take.ResegmentRequested && _pendingEnd.Length == 0)
        {
            RequestEnd("resolution-changed");
            if (Plugin.Cfg.AutoTrigger.Value) _pendingBegin = "auto-resume";
        }

        if (_pendingEnd.Length > 0)
        {
            var r = _pendingEnd; _pendingEnd = "";
            DoEnd(r);

            // delayed resume (e.g. resolution change): next tick will pick it up
            // note: retry/next-level re-segments do NOT auto-resume - the new
            // take begins from its own LoadingRoutine completion (pre-start screen)
        }
        else if (_pendingBegin.Length > 0 && Plugin.Cfg.AutoTrigger.Value)
        {
            _pendingBegin = ""; // shouldn't happen while recording; consume
        }
    }

    /// <summary>Synchronous shutdown for OnApplicationQuit.</summary>
    public void Shutdown(int waitMs)
    {
        if (_pendingEnd.Length > 0 || _take != null)
        {
            var r = _pendingEnd.Length > 0 ? _pendingEnd : "app-quit";
            _pendingEnd = "";
            DoEnd(r);
        }
        WaitFinalizers(waitMs);
    }

    // -------- begin / end (main thread) --------

    private void DoBegin(string reason)
    {
        try
        {
            FfmpegBinary.Initialize(Plugin.Cfg);
            if (FfmpegBinary.CurrentPath == null)
            {
                Plugin.Log.LogError($"cannot start take ({reason}): no ffmpeg available.");
                return;
            }

            Directory.CreateDirectory(OutputDir);

            int fps = Plugin.Cfg.Fps.Value;
            int crf = Plugin.Cfg.Crf.Value;
            bool h265 = Plugin.Cfg.Codec.Value.Equals("H265", StringComparison.OrdinalIgnoreCase);
            var encoder = ResolveEncoder(h265);
            int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            if (w <= 0 || h <= 0) { Plugin.Log.LogWarning("begin skipped: invalid screen size"); return; }

            var take = new TakeController(w, h, fps, crf, encoder)
            {
                TmpDir = Path.Combine(OutputDir, ".tmp", $"{DateTime.Now:yyyyMMdd-HHmmss}-{_seq++}")
            };
            Directory.CreateDirectory(take.TmpDir);

            LevelMeta.FillAtBegin(take, scnGame.instance);
            LevelMeta.FillPlayerConfig(take, scnGame.instance);

            _video = Plugin.Instance.gameObject.AddComponent<VideoCaptureBehaviour>();
            _video.Begin(take);
            _audio = null;
            _audioAttachRetryUntil = UnityEngine.Time.unscaledTime + 5f;
            TryAttachAudio(take);

            take.StartPumps();
            _take = take;
            Plugin.Log.LogInfo($"REC start ({reason}): {w}x{h}@{fps} {take.Encoder} crf{crf} song='{take.Song}'");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"begin failed: {e}");
            CleanupComponents();
            _take = null;
        }
    }

    /// <summary>Encoder=Auto probes NVENC → AMF → QSV with a real init test
    /// (256x256 lavfi encode; -encoders presence alone is not enough) and
    /// falls back to software. Explicit selections fail hard if unusable,
    /// except Auto-style downgrade from forced hw when init fails at spawn.</summary>
    private static string ResolveEncoder(bool h265)
    {
        string sw = h265 ? "libx265" : "libx264";
        string choice = Plugin.Cfg.Encoder.Value;
        if (choice.Equals("Software", StringComparison.OrdinalIgnoreCase)) return sw;

        string[] hwCandidates = choice.ToUpperInvariant() switch
        {
            "NVENC" => new[] { h265 ? "hevc_nvenc" : "h264_nvenc" },
            "AMF" => new[] { h265 ? "hevc_amf" : "h264_amf" },
            "QSV" => new[] { h265 ? "hevc_qsv" : "h264_qsv" },
            _ => new[] { h265 ? "hevc_nvenc" : "h264_nvenc", h265 ? "hevc_amf" : "h264_amf", h265 ? "hevc_qsv" : "h264_qsv" },
        };

        foreach (var enc in hwCandidates)
        {
            if (!FfmpegBinary.HasEncoder(enc)) continue;
            if (!FfmpegBinary.EncoderInitializes(enc)) continue;
            Plugin.Log.LogInfo($"encoder selected: {enc}");
            return enc;
        }

        if (choice.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.LogInfo($"no hardware encoder initialized - using {sw}");
            return sw;
        }
        Plugin.Log.LogWarning($"Encoder={choice} unavailable on this machine - falling back to {sw}");
        return sw;
    }

    private void TryAttachAudio(TakeController take)
    {
        try
        {
            var listener = UnityEngine.Object.FindObjectOfType<UnityEngine.AudioListener>();
            if (listener == null) return;
            _audio = listener.gameObject.AddComponent<AudioCaptureBehaviour>();
            _audio.Bind(take);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"audio attach failed: {e.Message}"); }
    }

    private void DoEnd(string reason)
    {
        var take = _take;
        if (take == null) return;
        _take = null;

        // 1. stop producers (coroutine destroyed first -> no new readback requests)
        CleanupComponents();
        // 2. seal inputs; in-flight callbacks may still land within the drain grace
        take.CloseVideoInput();
        take.CloseAudioInput();

        bool failed = take.Rank == null;
        if (failed && !Plugin.Cfg.KeepFailedTakes.Value)
        {
            Plugin.Log.LogInfo($"REC end ({reason}): take without rank discarded. " +
                $"frames={take.FramesQueued} dropped={take.FramesDropped}");
            take.Stopping = true;
            var t = new Thread(() =>
            {
                take.WaitPumps(3000);
                take.Discard();
            }) { Name = "RDRecord-discard", IsBackground = true };
            t.Start(); _finalizers.Add(t);
            return;
        }

        Plugin.Log.LogInfo($"REC end ({reason}): finalizing... frames={take.FramesQueued} dropped={take.FramesDropped}");

        var finalName = LevelMeta.BuildFileName(Plugin.Cfg, take);
        string finalPath = UniquePath(Path.Combine(OutputDir, finalName));

        var t2 = new Thread(() =>
        {
            try
            {
                take.WaitPumps(5000);
                take.FinalizeAndMux(finalPath);
            }
            catch (Exception e) { Plugin.Log.LogError($"finalize failed: {e}"); }
        })
        { Name = "RDRecord-finalize", IsBackground = true };
        t2.Start();
        _finalizers.Add(t2);
    }

    private void CleanupComponents()
    {
        if (_video != null) { _video.End(); UnityEngine.Object.Destroy(_video); _video = null; }
        if (_audio != null) { UnityEngine.Object.Destroy(_audio); _audio = null; }
    }

    private static string UniquePath(string p)
    {
        if (!File.Exists(p)) return p;
        var dir = Path.GetDirectoryName(p)!;
        var name = Path.GetFileNameWithoutExtension(p);
        var ext = Path.GetExtension(p);
        for (int i = 2; i < 100; i++)
        {
            var cand = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(cand)) return cand;
        }
        return Path.Combine(dir, $"{name}-{Guid.NewGuid():N}{ext}");
    }

    public void WaitFinalizers(int ms)
    {
        foreach (var t in _finalizers) t.Join(ms);
        _finalizers.Clear();
    }
}
