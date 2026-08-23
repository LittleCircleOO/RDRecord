using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace RDRecord.Core;

/// <summary>Coroutine-based end-of-frame capture (plan section 2: mode B/"乙").</summary>
internal sealed class VideoCaptureBehaviour : MonoBehaviour
{
    private Coroutine? _loop;
    private RenderTexture[] _rts = Array.Empty<RenderTexture>();
    private bool[] _busy = Array.Empty<bool>();
    private volatile byte[]? _diagPending;   // first-frame bytes, consumed on main thread
    private bool _diagLogged;

    internal void Begin(TakeController take) => _loop = StartCoroutine(Loop(take));

    internal void End()
    {
        if (_loop != null) { StopCoroutine(_loop); _loop = null; }
    }

    private void OnDestroy()
    {
        foreach (var rt in _rts) if (rt != null && rt.IsCreated()) rt.Release();
        _rts = Array.Empty<RenderTexture>();
    }

    private IEnumerator Loop(TakeController take)
    {
        int fps = take.Fps;
        int w = Screen.width, h = Screen.height;

        // rotating RT pool: readback N may still be in flight while frame N+1 blits
        _rts = new RenderTexture[4];
        _busy = new bool[4];
        for (int i = 0; i < _rts.Length; i++)
        {
            _rts[i] = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = $"RDRecord-rt{i}" };
            _rts[i].Create();
        }

        var wait = new WaitForEndOfFrame();
        double acc = 0.0;
        double lastDsp = AudioSettings.dspTime;

        // flip/orientation diagnostics (issue: Linux vertical flip)
        Plugin.Log.LogInfo(
            $"[diag] capture start: {w}x{h}@{fps} gfx={SystemInfo.graphicsDeviceType} " +
            $"uvStartsAtTop={SystemInfo.graphicsUVStartsAtTop} device=\"{SystemInfo.graphicsDeviceName}\" " +
            $"ver={SystemInfo.graphicsDeviceVersion} readbackSupported={SystemInfo.supportsAsyncGPUReadback} rtFormat=ARGB32");

        // Row-order fix: on OpenGL-family backends (uvStartsAtTop=False, e.g. RD on
        // Linux defaults to OpenGLCore) readback bytes come bottom-up (row 0 = screen
        // bottom, GL window-origin convention). The rawvideo pipe is consumed by
        // ffmpeg with top-down semantics (row 0 = image top), which flipped the video.
        // Verified on Linux: PNG built from the raw bytes via Unity texture semantics
        // is upright, the encoded video is flipped -> flip the byte rows ourselves.
        bool flipRows = !SystemInfo.graphicsUVStartsAtTop;
        byte[]? flipScratch = flipRows ? new byte[w * 4] : null;
        if (flipRows)
            Plugin.Log.LogInfo("[diag] uvStartsAtTop=False -> flipping readback rows to top-down for the rawvideo pipe");
        else if (SystemInfo.graphicsDeviceType is GraphicsDeviceType.OpenGLCore or GraphicsDeviceType.OpenGLES2 or GraphicsDeviceType.OpenGLES3)
            Plugin.Log.LogWarning("[diag] OpenGL-family backend with uvStartsAtTop=True: no row flip applied (unexpected combination)");

        while (true)
        {
            yield return wait;

            if (_diagPending != null) { DumpFirstFrame(_diagPending, w, h); _diagPending = null; }

            // resolution changed -> resegment (end+begin a new take)
            if (Screen.width != w || Screen.height != h)
            {
                take.ResegmentRequested = true;
                yield break;
            }

            if (fps < 60)
            {
                double now = AudioSettings.dspTime;
                acc += now - lastDsp;
                lastDsp = now;
                if (acc < 1.0 / fps) continue;
                acc -= 1.0 / fps;
                if (acc > 1.0 / fps) acc = 1.0 / fps; // no runaway catch-up
            }

            int idx = -1;
            for (int i = 0; i < _rts.Length; i++) if (!_busy[i]) { idx = i; break; }
            if (idx < 0) { take.FramesDropped++; continue; }   // all in flight: skip this one
            _busy[idx] = true;

            var rt = _rts[idx];
            double tCapture = take.CaptureNowSec;   // frame slot time for CFR shaping
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            var busy = _busy;
            AsyncGPUReadback.Request(rt, 0, (AsyncGPUReadbackRequest r) =>
            {
                busy[idx] = false;
                if (r.hasError) { take.FramesDropped++; return; }
                var frame = take.RentFrame();
                try
                {
                    r.GetData<byte>().CopyTo(frame);
                    // diag snapshot BEFORE the flip: the PNG reflects raw readback
                    // bytes under Unity texture semantics (upright on GL backends)
                    if (!_diagLogged)
                    {
                        _diagLogged = true;
                        var d = new byte[frame.Length];
                        Buffer.BlockCopy(frame, 0, d, 0, d.Length);
                        _diagPending = d;
                    }
                    if (flipScratch != null) FlipRows(frame, flipScratch, w, h);
                    take.EnqueueFrame(frame, frame.Length, tCapture);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"readback copy failed: {e.Message}");
                    take.FramesDropped++;
                }
            });
        }
    }

    /// <summary>First-frame orientation diagnostics: corner pixels + row checksums go to
    /// the log (permanent), the full PNG goes next to the recordings (config-gated).
    /// Compare the PNG against a game screenshot: PNG flipped -> flip is in
    /// ScreenCapture/readback (backend row order); PNG upright -> look downstream.</summary>
    private static void DumpFirstFrame(byte[] rgba, int w, int h)
    {
        try
        {
            long top = 0, bottom = 0;
            int rowBytes = w * 4;
            for (int x = 0; x < rowBytes; x++) { top += rgba[x]; bottom += rgba[(h - 1) * rowBytes + x]; }
            Plugin.Log.LogInfo(
                $"[diag] first frame: row0 checksum={top} row{h - 1} checksum={bottom} " +
                $"topLeft={HexAt(rgba, 0, 0, w)} bottomLeft={HexAt(rgba, 0, h - 1, w)}");

            if (Plugin.Cfg.DumpFirstFrame.Value)
            {
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                tex.LoadRawTextureData(rgba);
                tex.Apply(false, false);
                var png = tex.EncodeToPNG();
                Destroy(tex);
                string path = Path.Combine(Plugin.Recorder.OutputDir, $".diag-firstframe-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                File.WriteAllBytes(path, png);
                Plugin.Log.LogInfo($"[diag] first frame dumped: {path}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[diag] first-frame dump failed: {e.Message}");
        }
    }

    /// <summary>In-place vertical row flip of an RGBA buffer (row swap via one scratch
    /// row; O(h/2) BlockCopys, ~1ms at 2112x1188). Zero allocation after scratch init.</summary>
    private static void FlipRows(byte[] f, byte[] scratch, int w, int h)
    {
        int row = w * 4;
        for (int a = 0, b = (h - 1) * row; a < b; a += row, b -= row)
        {
            Buffer.BlockCopy(f, a, scratch, 0, row);
            Buffer.BlockCopy(f, b, f, a, row);
            Buffer.BlockCopy(scratch, 0, f, b, row);
        }
    }

    private static string HexAt(byte[] rgba, int x, int y, int w)
    {
        int i = (y * w + x) * 4;
        return $"{rgba[i]:X2}{rgba[i + 1]:X2}{rgba[i + 2]:X2}{rgba[i + 3]:X2}";
    }
}
