using System;
using System.IO;
using System.Linq;

namespace RDRecord.Core;

/// <summary>Level metadata -> filename tokens (CurrentLevelInfo-verified reads).</summary>
internal static class LevelMeta
{
    public static void FillAtBegin(TakeController take, scnGame? game)
    {
        // Steam persona name works everywhere (own guard inside: "" when not initialized)
        try { take.PlayerName = Sanitize(SteamIntegration.GetPlayersName() ?? ""); }
        catch { take.PlayerName = ""; }

        if (game == null) return;   // hotkey-begin outside a level: fall back to timestamp naming
        try
        {
            take.LevelId = Sanitize(game.levelIdentifier ?? "");
            switch (scnGame.levelToLoadSource)
            {
                case LevelSource.InternalPath:
                    take.Song = TryLocalized("levelSelect." + scnGame.internalIdentifier)
                                ?? Sanitize(scnGame.internalIdentifier ?? "");
                    break;
                case LevelSource.ExternalPath:
                    // RDLevelSettings is a struct: ?. yields Nullable<T>, keep using ?. to unwrap
                    var settings = game.currentLevel?.data?.settings;
                    if (settings != null)
                    {
                        take.Song = Sanitize(settings?.song ?? "");
                        take.Artist = Sanitize(settings?.artist ?? "");
                        take.Author = Sanitize(settings?.author ?? "");
                        take.Difficulty = Sanitize(settings?.difficulty.ToString() ?? "");
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"level meta read failed (naming falls back): {e.Message}");
        }
    }

    /// <summary>Player judgement config (defibrillator difficulty), split P1/P2.
    /// Single-player resolves to P1 only (verified: scrHandController.Awake dresses the
    /// player-operated right arm with p1Skin unconditionally; leftArm becomes P2 only
    /// under GC.twoPlayerMode; GetHitMargin branches P1->p1DefibMode / P2->p2DefibMode).
    /// In-level: read the live scnGame statics; otherwise Persistence (saved config).</summary>
    public static void FillPlayerConfig(TakeController take, scnGame? game)
    {
        try
        {
            take.Defib1 = LocalizeDefib(ReadDefib(game, p1: true));
            bool twoPlayer = false;
            try { twoPlayer = GC.twoPlayerMode; } catch { }
            take.Defib2 = twoPlayer ? LocalizeDefib(ReadDefib(game, p1: false)) : "";
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"defib mode read failed (naming falls back): {e.Message}");
        }
    }

    private static object? ReadDefib(scnGame? game, bool p1)
    {
        // live in-level value first (pause-menu changes apply here;
        // initialized by scnGame.Awake from Persistence each level start)
        try
        {
            return p1 ? scnGame.p1DefibMode : scnGame.p2DefibMode;
        }
        catch { }
        // saved config works everywhere (hotkey take outside a level)
        try
        {
            return p1 ? Persistence.GetDefibrillatorP1() : Persistence.GetDefibrillatorP2();
        }
        catch { return null; }
    }

    private static string LocalizeDefib(object? mode)
    {
        if (mode == null) return "";
        var s = mode.ToString() ?? "";
        if (s.Length == 0) return "";
        // pause menu localization key pattern ("enum.DefibMode." + value)
        return TryLocalized("enum.DefibMode." + s) ?? Sanitize(s);
    }

    private static string? TryLocalized(string key)
    {
        try { return Sanitize(RDString.Get(key)); }
        catch { return null; }
    }

    public static string BuildFileName(PluginConfig cfg, TakeController take)
    {
        string date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string name = cfg.FileNameTemplate.Value
            .Replace("{song}", OrDash(take.Song))
            .Replace("{artist}", OrDash(take.Artist))
            .Replace("{author}", OrDash(take.Author))
            .Replace("{difficulty}", OrDash(take.Difficulty))
            .Replace("{player}", OrDash(take.PlayerName))
            .Replace("{defib}", OrDash(DefibSmart(take)))
            .Replace("{defib1}", OrDash(take.Defib1))
            .Replace("{defib2}", OrDash(take.Defib2))
            .Replace("{rank}", take.Rank != null ? Sanitize(take.Rank) : "NR")
            .Replace("{mistakes}", take.Mistakes >= 0 ? take.Mistakes.ToString() : "-")
            .Replace("{id}", OrDash(take.LevelId))
            .Replace("{date}", date);
        name = Sanitize(name);
        if (name.Length == 0) name = date;
        return name + ".mp4";
    }

    private static string OrDash(string s) => s.Length == 0 ? "-" : s;

    /// <summary>{defib}: single-player = P1 value; two-player = "P1val+P2val".</summary>
    private static string DefibSmart(TakeController take)
    {
        if (take.Defib2.Length == 0) return take.Defib1;
        return take.Defib1.Length == 0 ? take.Defib2 : $"{take.Defib1}+{take.Defib2}";
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '{' || c == '}') continue;
            sb.Append(char.IsWhiteSpace(c) || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }
        var r = sb.ToString().Trim('.');
        return r.Length > 80 ? r[..80] : r;
    }
}
