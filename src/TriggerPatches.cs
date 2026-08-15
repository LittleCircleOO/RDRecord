using System;
using System.Collections;
using RDRecord.Core;

namespace RDRecord.Triggers;

/// <summary>Harmony hooks, each patched independently (Trainer pattern):
/// a single signature drift degrades only that signal, never the plugin.</summary>
internal static class TriggerPatches
{
    internal static bool LoadingHookOk;   // begin: pre-start screen
    internal static bool SceneHookOk;     // end: left level scene / retry re-segment
    internal static bool RankHookOk;      // metadata: rank + mistakes at save time

    public static void TryPatchAll(HarmonyLib.Harmony h)
    {
        LoadingHookOk = SafePatch(h, typeof(LoadingRoutinePatch), nameof(LoadingRoutinePatch), "begin (pre-start screen)");
        SceneHookOk = SafePatch(h, typeof(SceneStartPatch), nameof(SceneStartPatch), "end (scene switch)");
        RankHookOk = SafePatch(h, typeof(RankSavePatch), nameof(RankSavePatch), "rank metadata");
    }

    private static bool SafePatch(HarmonyLib.Harmony h, Type type, string name, string what)
    {
        try { h.CreateClassProcessor(type).Patch(); return true; }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"hook unavailable ({what}): {e.Message}");
            return false;
        }
    }

    public static void LogStatus()
    {
        Plugin.Log.LogInfo($"hooks: begin={(LoadingHookOk ? "ok" : "FALLBACK-POLL")} end={(SceneHookOk ? "ok" : "FALLBACK-POLL")} rank={(RankHookOk ? "ok" : "off")}");
    }

    // ---------------------------------------------------------------- begin --

    /// <summary>Level data loaded &amp; pre-start screen visible, player has NOT
    /// pressed space yet. Pass-through postfix semantics are officially
    /// supported since Harmony 2018 (verified in HarmonyX 2.9.0 source), but
    /// the target here is a ~15-byte iterator stub that Mono may inline into
    /// its caller at caller-JIT time, bypassing the detour on Unity 6000.x.
    /// Empirically it did not fire. Kept as fast path; the authoritative
    /// fallback is Plugin.Update()'s gameState==PreStart poll (frame-precise,
    /// zero Harmony dependency). Current form uses plain `ref __result`
    /// replacement - stable documented semantics on any Harmony 2.x.</summary>
    [HarmonyLib.HarmonyPatch(typeof(scnGame), "LoadingRoutine")]
    private static class LoadingRoutinePatch
    {
        [HarmonyLib.HarmonyPostfix]
        public static void Postfix(ref IEnumerator __result, scnGame __instance)
        {
            __result = Wrapper(__result, __instance);
        }

        private static IEnumerator Wrapper(IEnumerator inner, scnGame inst)
        {
            // run the original loading coroutine to completion
            while (inner.MoveNext()) yield return inner.Current;

            bool editorMode, stc;
            LevelSource src;
            try
            {
                editorMode = inst.editorMode;
                stc = inst.startTheGameCalled;
                src = scnGame.levelToLoadSource;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"begin-hook state read failed: {e.Message}");
                yield break;
            }
            Plugin.Log.LogInfo(
                $"LoadingRoutine done: editorMode={editorMode} startTheGameCalled={stc} source={src}");

            if (Plugin.Cfg.AutoTrigger.Value
                && !editorMode
                && src != LevelSource.CutscenesPath
                && !stc)
            {
                Plugin.Recorder?.RequestBegin("pre-start");
            }
        }
    }

    // ------------------------------------------------------------------ end --

    /// <summary>Any scene start while recording ends the take (CurrentLevelInfo-
    /// verified anchor): leaving to menu = "after rank confirm"; a fresh scnGame
    /// (retry) = clean re-segment, the new take begins via LoadingRoutine.</summary>
    [HarmonyLib.HarmonyPatch(typeof(scnBase), "Start")]
    private static class SceneStartPatch
    {
        private static void Postfix(scnBase __instance)
        {
            // diagnostic: one line per scene load, proves this hook fires
            Plugin.Log.LogInfo($"scene start: {__instance.GetType().Name}");
            var rec = Plugin.Recorder;
            if (rec == null || !rec.IsRecording) return;
            try
            {
                if (__instance is scnGame)
                    rec.RequestEnd("re-segment(new scnGame)");   // retry / next level
                else
                    rec.RequestEnd("left-level-scene");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"end-hook failed: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------ rank meta --

    /// <summary>Rank + mistakes are final at save time (Archipelago-verified).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Rankscreen), nameof(Rankscreen.ShowAndSaveRank))]
    private static class RankSavePatch
    {
        private static void Prefix()
        {
            try
            {
                var g = scnGame.instance;
                if (g == null) return;
                var rank = g.currentLevel.GetRankFromMistakes();
                int mistakes = (int)System.Math.Round(g.mistakesManager.mistakes);
                Plugin.Recorder?.OnRankSaved(rank.ToString(), mistakes);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"rank-hook failed: {e.Message}"); }
        }
    }
}
