using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RDRecord.Core;

/// <summary>Locates / verifies / (optionally) downloads the ffmpeg binary.
/// Default location: BepInEx/plugins/RDRecord/bin/ffmpeg[.exe]</summary>
internal static class FfmpegBinary
{
    // Per-platform download sources (Application.platform branches).
    // All are single static-binary archives; extraction differs per format.
    private static readonly Dictionary<UnityEngine.RuntimePlatform, (string url, string kind)> Sources = new()
    {
        // zip: archive contains <top>/bin/ffmpeg.exe
        { UnityEngine.RuntimePlatform.WindowsPlayer,
            ("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", "zip") },
        // tar.xz: archive contains ffmpeg-release-<ver>-amd64-static/ffmpeg (needs system tar+xz)
        { UnityEngine.RuntimePlatform.LinuxPlayer,
            ("https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz", "tarxz") },
        // zip: archive contains ffmpeg at root (x86_64; runs under Rosetta on Apple Silicon)
        { UnityEngine.RuntimePlatform.OSXPlayer,
            ("https://evermeet.cx/pub/ffmpeg/get-ffmpeg.zip", "zip") },
    };

    private static string ExeName =>
        UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsPlayer ? "ffmpeg.exe" : "ffmpeg";

    private static string? _cachedPath;
    private static string? _cachedVersion;

    internal static string PluginDir =>
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;

    private static string BinDir => Path.Combine(PluginDir, "bin");
    internal static string ExePath => Path.Combine(BinDir,
        UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsPlayer ? "ffmpeg.exe" : "ffmpeg");

    internal static string? CurrentPath => _cachedPath;

    private static HashSet<string>? _encoders;
    private static readonly Dictionary<string, bool> _encoderInitCache = new();

    /// <summary>True if the resolved ffmpeg build exposes the named encoder
    /// (e.g. "libx265"). Cached after first probe.</summary>
    internal static bool HasEncoder(string name)
    {
        if (_cachedPath == null) return false;
        if (_encoders != null) return _encoders.Contains(name);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cachedPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var set = new HashSet<string>(StringComparer.Ordinal);
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                // format: " V....D libx265  description..."
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) set.Add(parts[1]);
            }
            if (!p.WaitForExit(5000)) { p.Kill(); return false; }
            _encoders = set;
            return set.Contains(name);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"encoder probe failed: {e.Message}");
            return false;
        }
    }

    /// <summary>True if the encoder actually initializes on this machine
    /// (presence in -encoders is not enough: driver/GPU may be absent).
    /// Verified via a tiny lavfi test encode; note NVENC rejects frames below
    /// ~145px so the probe uses 256x256. Result cached per encoder.</summary>
    internal static bool EncoderInitializes(string name)
    {
        if (_cachedPath == null) return false;
        if (_encoderInitCache.TryGetValue(name, out var ok)) return ok;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cachedPath,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i nullsrc=s=256x256:d=0.04 -c:v {name} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000)) { p.Kill(); return false; }
            _encoderInitCache[name] = p.ExitCode == 0;
            return _encoderInitCache[name];
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"encoder init probe failed for {name}: {e.Message}");
            return false;
        }
    }

    public static string DescribeStatus()
    {
        return _cachedPath != null ? $"{_cachedPath} [{_source}] ({_cachedVersion})" : "not found (will search config/bin/PATH, then download if enabled)";
    }

    /// <summary>Multi-source resolution chain, idempotent per take attempt:
    /// 1. configured FFmpegPath (if set)
    /// 2. plugin bin folder (BepInEx/plugins/RDRecord/bin)
    /// 3. system PATH (bare-name spawn probe, then where/which for a stable
    ///    absolute path; bare name kept as last resort)
    /// 4. internet download (if AutoDownloadFFmpeg)</summary>
    public static void Initialize(PluginConfig cfg)
    {
        // fast path: already resolved and still usable (bare PATH name = not rooted)
        if (_cachedPath != null && (!Path.IsPathRooted(_cachedPath) || File.Exists(_cachedPath))) return;

        if (TryLocate(cfg, out var path, out var ver, out var source))
        {
            _cachedPath = path; _cachedVersion = ver; _source = source;
            Plugin.Log.LogInfo($"ffmpeg resolved [{source}]: {path} ({ver})");
            return;
        }
        Plugin.Log.LogWarning("ffmpeg not found (searched: configured path, plugin bin, system PATH)" +
            (cfg.AutoDownloadFFmpeg.Value ? " - attempting download..." : " - enable AutoDownloadFFmpeg or place it in the bin folder."));
        if (cfg.AutoDownloadFFmpeg.Value && TryDownload())
        {
            if (TryLocate(cfg, out path, out ver, out source))
            {
                _cachedPath = path; _cachedVersion = ver; _source = source;
            }
        }
    }

    private static string _source = "";

    private static bool TryLocate(PluginConfig cfg, out string path, out string version, out string source)
    {
        path = ""; version = ""; source = "";

        // 1. configured explicit path
        var cfgPath = cfg.FFmpegPath.Value;
        if (cfgPath.Length > 0 && !string.Equals(Path.GetFullPath(cfgPath), ExePath, StringComparison.OrdinalIgnoreCase))
        {
            var v = ProbeVersion(cfgPath);
            if (v != null) { path = Path.GetFullPath(cfgPath); version = v; source = "config"; return true; }
            Plugin.Log.LogWarning($"configured FFmpegPath '{cfgPath}' is not a working ffmpeg - continuing search");
        }

        // 2. plugin bin folder
        var bv = ProbeVersion(ExePath);
        if (bv != null) { path = Path.GetFullPath(ExePath); version = bv; source = "plugin-bin"; return true; }

        // 3. system PATH: bare name resolves via PATH on both Windows and Unix
        var pv = ProbeVersion("ffmpeg");
        if (pv != null)
        {
            string? resolved = ResolveOnPath();
            path = resolved ?? "ffmpeg";
            version = pv;
            source = "system-path";
            return true;
        }
        return false;
    }

    /// <summary>where/which to pin an absolute path (a bare "ffmpeg" keeps
    /// working but breaks if the process environment changes mid-session).</summary>
    private static string? ResolveOnPath()
    {
        try
        {
            bool win = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsPlayer;
            var psi = new ProcessStartInfo
            {
                FileName = win ? "where" : "which",
                Arguments = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string first = (p.StandardOutput.ReadLine() ?? "").Trim();
            if (!p.WaitForExit(5000)) { p.Kill(); return null; }
            return p.ExitCode == 0 && first.Length > 0 && File.Exists(first) ? first : null;
        }
        catch { return null; }
    }

    private static string? ProbeVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string first = p.StandardOutput.ReadLine() ?? "";
            if (!p.WaitForExit(5000)) { p.Kill(); return null; }
            return first.StartsWith("ffmpeg version") ? first.Substring(15).Trim() : first;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"ffmpeg probe failed for {exe}: {e.Message}");
            return null;
        }
    }

    private static bool TryDownload()
    {
        var platform = UnityEngine.Application.platform;
        if (!Sources.TryGetValue(platform, out var src))
        {
            Plugin.Log.LogWarning($"auto-download unsupported on platform {platform}; place ffmpeg at {ExePath} manually.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(BinDir);
            string archivePath = Path.Combine(BinDir, "ffmpeg-download" + (src.kind == "tarxz" ? ".tar.xz" : ".zip"));
            Plugin.Log.LogInfo($"downloading ffmpeg: {src.url}");
            DownloadTo(src.url, archivePath);

            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(archivePath))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            Plugin.Log.LogInfo($"ffmpeg archive sha256={hash} (verify against the source site if unsure)");

            bool ok = src.kind == "tarxz" ? ExtractTarXz(archivePath) : ExtractZip(archivePath);
            try { File.Delete(archivePath); } catch { }
            if (!ok)
            {
                Plugin.Log.LogError("ffmpeg extraction failed - place the binary manually at " + ExePath);
                return false;
            }

            if (platform != UnityEngine.RuntimePlatform.WindowsPlayer) ChmodPlusX(ExePath);
            if (platform == UnityEngine.RuntimePlatform.OSXPlayer)
                Plugin.Log.LogInfo("note: evermeet build is x86_64; on Apple Silicon it runs via Rosetta.");
            Plugin.Log.LogInfo($"ffmpeg extracted to {ExePath}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ffmpeg download failed: {e.Message} - place the binary manually at {ExePath}");
            return false;
        }
    }

    private static void DownloadTo(string url, string destPath)
    {
        using var http = new System.Net.Http.HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var resp = http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).Result;
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        using var srcStream = resp.Content.ReadAsStreamAsync().Result;
        using var dst = File.Create(destPath);
        var buf = new byte[1 << 20];
        long done = 0; int n;
        long lastPct = -1;
        while ((n = srcStream.Read(buf, 0, buf.Length)) > 0)
        {
            dst.Write(buf, 0, n);
            done += n;
            if (total is long t)
            {
                long pct = done * 100 / t;
                if (pct != lastPct && pct % 10 == 0) { Plugin.Log.LogInfo($"ffmpeg download {pct}%"); lastPct = pct; }
            }
        }
    }

    private static bool ExtractZip(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                Plugin.Log.LogError($"{ExeName} not found inside downloaded zip");
                return false;
            }
            entry.ExtractToFile(ExePath, true);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"zip extraction failed: {e.Message}");
            return false;
        }
    }

    /// <summary>System tar handles tar.xz (needs tar + xz-utils, present on
    /// virtually every distro). Extract next to bin/, move ffmpeg into place,
    /// remove the leftover folder.</summary>
    private static bool ExtractTarXz(string archivePath)
    {
        try
        {
            if (RunCommand("tar", $"-xJf \"{archivePath}\" -C \"{BinDir}\"", out string tarErr) != 0)
            {
                Plugin.Log.LogError($"tar extraction failed ({tarErr}); ensure tar and xz-utils are installed, or place ffmpeg manually.");
                return false;
            }
            // locate the extracted ffmpeg anywhere under bin (tar creates ffmpeg-release-<ver>-amd64-static/ffmpeg)
            var found = Directory.EnumerateFiles(BinDir, ExeName, SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, ExePath, StringComparison.Ordinal));
            if (found == null)
            {
                Plugin.Log.LogError($"{ExeName} not found after tar extraction");
                return false;
            }
            File.Copy(found, ExePath, true);
            try { File.Delete(found); } catch { }
            // remove the leftover extracted folder(s) and stray binaries (ffprobe etc.)
            foreach (var dir in Directory.EnumerateDirectories(BinDir))
                try { Directory.Delete(dir, true); } catch { }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"tar.xz extraction failed: {e.Message}");
            return false;
        }
    }

    private static void ChmodPlusX(string path)
    {
        try
        {
            if (RunCommand("chmod", $"+x \"{path}\"", out _) != 0)
                Plugin.Log.LogWarning($"chmod +x failed on {path} (extraction may have preserved the exec bit)");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"chmod failed: {e.Message}"); }
    }

    private static int RunCommand(string fileName, string args, out string stderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(); return -1; }
        return p.ExitCode;
    }
}
