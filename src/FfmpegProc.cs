using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RDRecord.Core;

/// <summary>Managed child ffmpeg process with async stderr drain.</summary>
internal sealed class FfmpegProc
{
    private readonly Process _p;
    private readonly StringBuilder _err = new();

    private FfmpegProc(Process p) { _p = p; }

    public Stream Stdin => _p.StandardInput.BaseStream;
    public int ExitCode => _p.HasExited ? _p.ExitCode : -1;

    public static FfmpegProc? Start(string args, string workDir, string tag)
    {
        var exe = FfmpegBinary.CurrentPath;
        if (exe == null)
        {
            Plugin.Log.LogError($"[{tag}] ffmpeg not available (see log).");
            return null;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            };
            var p = Process.Start(psi)!;
            var f = new FfmpegProc(p);
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (f._err) f._err.AppendLine(e.Data);
            };
            p.BeginErrorReadLine();
            return f;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[{tag}] failed to spawn ffmpeg: {e.Message}");
            return null;
        }
    }

    public void CloseInput() { try { _p.StandardInput.Close(); } catch { } }
    public bool WaitForExit(int ms)
    {
        try { return _p.WaitForExit(ms); }
        catch { return true; }
    }
    public void Kill() { try { if (!_p.HasExited) _p.Kill(); } catch { } }

    public string StderrText() { lock (_err) return _err.ToString(); }
}

/// <summary>Command line builders - field-validated against ffmpeg 8.1.2 (E2E tested).</summary>
internal static class FfmpegArgs
{
    /// <summary>Resolve the output size for a Resolution preset against a source size.
    /// Fit-inside semantics: sources at or below the preset box pass through untouched;
    /// larger ones scale by min(boxW/srcW, boxH/srcH) preserving aspect (no letterbox,
    /// one axis lands on the box edge, the other fits inside it). Output axes are
    /// rounded down to even numbers (yuv420p requirement).</summary>
    public static (int w, int h) ComputeOutputSize(int srcW, int srcH, string preset)
    {
        switch (preset)
        {
            case "1080p":
                return FitInside(srcW, srcH, 1920, 1080);
            case "720p":
                return FitInside(srcW, srcH, 1280, 720);
            default:
                return (srcW, srcH);
        }
    }

    private static (int, int) FitInside(int srcW, int srcH, int boxW, int boxH)
    {
        if (srcW <= boxW && srcH <= boxH) return (srcW, srcH);   // smaller or equal: passthrough
        double scale = Math.Min((double)boxW / srcW, (double)boxH / srcH);
        int w = Math.Max(2, (int)Math.Floor(srcW * scale)) & ~1;
        int h = Math.Max(2, (int)Math.Floor(srcH * scale)) & ~1;
        return (w, h);
    }

    public static string Video(int w, int h, int outW, int outH, int fps, int crf, int maxRateMbps, string encoder)
    {
        int gop = fps * 10;
        int mbps = maxRateMbps > 0 ? maxRateMbps : (fps >= 60 ? 4 : 2);
        string maxrate = $"{mbps}M";
        string bufsize = $"{mbps * 2}M";
        // NOTE: no -use_wallclock_as_timestamps here: the rawvideo demuxer
        // assigns increasing pts itself (from -r), so wallclock stamps never
        // engage; cadence integrity is the plugin's job (CFR shaping in
        // TakeController.VideoPumpLoop).

        string codecArgs = encoder switch
        {
            // software: quality baseline (crf as configured)
            "libx264" =>
                "-c:v libx264 -preset veryfast -profile:v high " +
                $"-bf 0 -g {gop} -crf {crf}",
            "libx265" =>
                // x265 crf scale: +5 approximates x264 quality parity
                "-c:v libx265 -preset veryfast " +
                $"-crf {Math.Min(crf + 5, 51)} -x265-params \"bframes=0:keyint={gop}:log-level=error\" " +
                "-tag:v hvc1",
            // NVENC: quality parity found at cq ~= crf + 9 (RTX 3060 laptop,
            // game-capture content): same size as x264 crf, ~3x encode speed,
            // near-zero CPU; slightly softer text edges in motion vs x264
            "h264_nvenc" =>
                "-c:v h264_nvenc -preset p5 -profile:v high " +
                $"-bf 0 -g {gop} -rc vbr -cq {Math.Min(crf + 9, 51)} -b:v 0 -spatial_aq 1",
            "hevc_nvenc" =>
                "-c:v hevc_nvenc -preset p5 " +
                $"-bf 0 -g {gop} -rc vbr -cq {Math.Min(crf + 12, 51)} -b:v 0 -spatial_aq 1 -tag:v hvc1",
            // AMF / QSV: best-effort params (not benchmarked on dev machine);
            // quality-based rate control with qp roughly mapped from crf
            "h264_amf" =>
                "-c:v h264_amf -quality quality -rc cqp " +
                $"-qp_i {crf + 4} -qp_p {crf + 6} -g {gop}",
            "hevc_amf" =>
                "-c:v hevc_amf -quality quality -rc cqp " +
                $"-qp_i {crf + 8} -qp_p {crf + 10} -g {gop} -tag:v hvc1",
            "h264_qsv" =>
                "-c:v h264_qsv -preset veryfast " +
                $"-global_quality {crf + 4} -bf 0 -g {gop}",
            "hevc_qsv" =>
                "-c:v hevc_qsv -preset veryfast " +
                $"-global_quality {crf + 9} -bf 0 -g {gop} -tag:v hvc1",
            _ => throw new ArgumentException($"unknown encoder {encoder}"),
        };

        // -maxrate/-bufsize apply to software CRF mode only (hw uses cq/vbr
        // rate control; clamping it with maxrate degrades quality unexpectedly)
        string rateArgs = encoder.StartsWith("lib") ? $"-maxrate {maxrate} -bufsize {bufsize} " : "";

        // scale (down only) before pixel-format conversion; lanczos keeps chart
        // text sharp. Identical dims would make scale a cheap no-op, but we skip
        // it entirely for passthrough to keep the filter graph minimal.
        bool scaling = outW != w || outH != h;
        string vf = scaling
            ? $"-vf \"scale={outW}:{outH}:flags=lanczos,format=yuv420p\" "
            : "-vf \"format=yuv420p\" ";

        return
            "-hide_banner -loglevel error " +
            $"-f rawvideo -pix_fmt rgba -s {w}x{h} -r {fps} " +
            "-i pipe:0 " +
            vf +
            codecArgs + " " + rateArgs +
            "-fps_mode passthrough " +
            "-movflags +frag_keyframe+empty_moov+default_base_moof " +
            "-y video.tmp.mp4";
    }

    public static string Audio(int rate, int channels)
    {
        channels = Math.Max(1, Math.Min(2, channels));
        return
            "-hide_banner -loglevel error " +
            $"-f f32le -ar {rate} -ac {channels} -i pipe:0 " +
            "-c:a aac -profile:a aac_low -ac 1 -ar 32000 -b:a 126k " +
            "-f adts -y audio.tmp.aac";
    }

    public static string Mux(string video, string audio, string output, double offsetSeconds)
    {
        // offset = t0a - t0v (E2E verified): positive when audio started after video.
        // Negative offsets are legal; avoid_negative_ts make_zero shifts the whole
        // timeline which preserves relative sync either way.
        return
            "-hide_banner -loglevel error " +
            $"-i \"{Q(video)}\" -itsoffset {offsetSeconds:F3} -i \"{Q(audio)}\" " +
            "-c copy -avoid_negative_ts make_zero -movflags +faststart " +
            $"-y \"{Q(output)}\"";
    }

    public static string Remux(string video, string output)
    {
        return $"-hide_banner -loglevel error -i \"{Q(video)}\" -c copy -movflags +faststart -y \"{Q(output)}\"";
    }

    private static string Q(string p) => p.Replace("\"", "\\\"");
}
