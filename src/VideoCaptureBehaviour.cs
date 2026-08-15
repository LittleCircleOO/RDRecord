using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RDRecord.Core;

/// <summary>Coroutine-based end-of-frame capture (plan section 2: mode B/"乙").</summary>
internal sealed class VideoCaptureBehaviour : MonoBehaviour
{
    private Coroutine? _loop;
    private RenderTexture[] _rts = Array.Empty<RenderTexture>();
    private bool[] _busy = Array.Empty<bool>();

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

        while (true)
        {
            yield return wait;

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
}
