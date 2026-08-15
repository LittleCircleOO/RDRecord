using System;
using System.IO;
using System.Linq;

namespace RDRecord.Core;

/// <summary>Level metadata -> filename tokens (CurrentLevelInfo-verified reads).</summary>
internal static class LevelMeta
{
    public static void FillAtBegin(TakeController take, scnGame? game)
    {
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
            .Replace("{rank}", take.Rank != null ? Sanitize(take.Rank) : "NR")
            .Replace("{mistakes}", take.Mistakes >= 0 ? take.Mistakes.ToString() : "-")
            .Replace("{id}", OrDash(take.LevelId))
            .Replace("{date}", date);
        name = Sanitize(name);
        if (name.Length == 0) name = date;
        return name + ".mp4";
    }

    private static string OrDash(string s) => s.Length == 0 ? "-" : s;

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
