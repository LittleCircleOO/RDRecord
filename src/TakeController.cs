using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RDRecord.Core;

/// <summary>One recording = one "take". Owns the two ffmpeg child processes,
/// the pump threads, capture metadata and the finalize (mux) routine.</summary>
internal sealed class TakeController
{
    // ---- meta (filled on main thread) ----
    internal string Song = "";
    internal string Artist = "";
    internal string Author = "";
    internal string Difficulty = "";
    internal string LevelId = "";
    internal string? Rank;              // null until rank screen saved
    internal int Mistakes = -1;
    internal float RankSavedAt;         // Time.unscaledTime when rank saved

    // ---- stats ----
    internal long FramesQueued;
    internal long FramesDropped;
    internal long FramesRepeated;
    internal long AudioChunksDropped;

    // ---- timing (L1) ----
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    internal double? T0Video;           // seconds since take start, first video byte written to stdin
    internal double? T0Audio;           // seconds since take start, first audio byte written to stdin

    internal string TmpDir = null!;
    internal string VideoTmpPath => Path.Combine(TmpDir, "video.tmp.mp4");
    internal string AudioTmpPath => Path.Combine(TmpDir, "audio.tmp.aac");

    internal readonly int Fps;
    internal readonly int Width;
    internal readonly int Height;
    internal readonly int Crf;
    internal readonly string Encoder;

    private FfmpegProc? _videoProc;
    private FfmpegProc? _audioProc;
    private readonly object _procLock = new();
    internal volatile bool Stopping;    // pipe dead / abort

    // video frame queue + pool: items carry their capture time (sec, Stopwatch-based)
    private readonly Queue<(byte[] buf, double t)> _frameQ = new();
    private readonly Stack<byte[]> _framePool = new();
    private readonly int _frameSize;
    private const int QueueCap = 8;
    private const int PoolCap = QueueCap + 8;
    private bool _videoClosed;          // no more frames will be enqueued

    // audio ring (single-producer DSP thread / single-consumer pump thread)
    private readonly byte[] _ring = new byte[4 * 1024 * 1024];
    private long _ringHead;             // monotonic write offset (bytes)
    private long _ringTail;             // monotonic read offset (bytes)
    internal int AudioRate;             // filled on first push (under _ring lock)
    internal int AudioChannels;
    private bool _audioClosed;

    internal volatile bool ResegmentRequested;   // resolution changed mid-take

    private readonly Thread _videoThread;
    private readonly Thread _audioThread;

    internal TakeController(int width, int height, int fps, int crf, string encoder)
    {
        Width = width; Height = height; Fps = fps; Crf = crf; Encoder = encoder;
        _frameSize = width * height * 4;
        for (int i = 0; i < QueueCap; i++) _framePool.Push(new byte[_frameSize]);
        _videoThread = new Thread(VideoPumpLoop) { Name = "RDRecord-video", IsBackground = true };
        _audioThread = new Thread(AudioPumpLoop) { Name = "RDRecord-audio", IsBackground = true };
    }

    private double NowSec => (Stopwatch.GetTimestamp() - _startTimestamp) * 1.0 / Stopwatch.Frequency;

    /// <summary>Capture-time clock shared with the capture coroutine.</summary>
    internal double CaptureNowSec => NowSec;

    // ================= video path (readback callback, main thread) =================

    internal byte[] RentFrame()
    {
        lock (_frameQ) { return _framePool.Count > 0 ? _framePool.Pop() : new byte[_frameSize]; }
    }

    internal void EnqueueFrame(byte[] frame, int length, double tCapture)
    {
        lock (_frameQ)
        {
            if (Stopping || _videoClosed || length != _frameSize)
            {
                FramesDropped++;
                ReturnFrameLocked(frame);
                return;
            }
            while (_frameQ.Count >= QueueCap)
            {
                // drop oldest: protect the game, not the take
                // (CFR shaping later fills the resulting timeline hole with repeats)
                ReturnFrameLocked(_frameQ.Dequeue().buf);
                FramesDropped++;
            }
            _frameQ.Enqueue((frame, tCapture));
            FramesQueued++;
            Monitor.Pulse(_frameQ);
        }
    }

    private void ReturnFrameLocked(byte[] b) { if (_framePool.Count < PoolCap) _framePool.Push(b); }

    internal void CloseVideoInput()
    {
        lock (_frameQ) { _videoClosed = true; Monitor.Pulse(_frameQ); }
    }

    // ================= audio path (DSP thread - never block) =================

    internal void PushAudio(float[] data, int channels)
    {
        int byteLen = data.Length * 4;
        lock (_ring)
        {
            if (AudioRate == 0) AudioRate = UnityEngine.AudioSettings.outputSampleRate;
            AudioChannels = channels;
            long used = _ringHead - _ringTail;
            int free = _ring.Length - (int)used;
            if (free < byteLen)
            {
                _ringTail += byteLen - free;   // drop oldest
                AudioChunksDropped++;
            }
            int pos = (int)(_ringHead % _ring.Length);
            int first = Math.Min(byteLen, _ring.Length - pos);
            Buffer.BlockCopy(data, 0, _ring, pos, first);
            if (first < byteLen) Buffer.BlockCopy(data, first / 4, _ring, 0, byteLen - first);
            _ringHead += byteLen;
            Monitor.Pulse(_ring);
        }
    }

    internal void CloseAudioInput() { lock (_ring) { _audioClosed = true; Monitor.Pulse(_ring); } }

    // ================= pumps (started at begin) =================

    internal void StartPumps()
    {
        _videoThread.Start();
        _audioThread.Start();
    }

    private void VideoPumpLoop()
    {
        long lastIdx = -1;   // CFR frame index of the last written frame
        try
        {
            while (true)
            {
                (byte[] frame, double t) item;
                lock (_frameQ)
                {
                    while (_frameQ.Count == 0)
                    {
                        if (_videoClosed || Stopping)
                        {
                            // drain grace: allow late readback callbacks to land
                            if (_drainDeadline == DateTime.MinValue)
                                _drainDeadline = DateTime.UtcNow.AddMilliseconds(500);
                            if (DateTime.UtcNow >= _drainDeadline || Stopping) return;
                        }
                        Monitor.Wait(_frameQ, 100);
                    }
                    item = _frameQ.Dequeue();
                }

                if (!EnsureVideoProc()) { lock (_frameQ) ReturnFrameLocked(item.frame); continue; }
                try
                {
                    lock (_procLock)
                    {
                        // ---- CFR shaping: rawvideo pipe gets pts from -r alone
                        // (the demuxer assigns increasing pts; wallclock stamps
                        // never engage), so the pipe MUST receive a strictly
                        // constant frame cadence or the take speeds up/skews
                        // against audio. Every timeline hole (game present rate
                        // below target, dropped frames) is filled with repeats
                        // of the incoming frame: x264 codes them as skip
                        // macroblocks at ~zero bitrate cost, playback shows a
                        // frozen frame (honest: no new frame existed), and
                        // A/V sync stays rigid.
                        if (T0Video == null) { T0Video = item.t; lastIdx = 0; }
                        long idx = (long)Math.Round((item.t - T0Video.Value) * Fps);
                        if (idx <= lastIdx)
                        {
                            // same slot again (burst) - drop the stale copy
                            FramesDropped++;
                        }
                        else
                        {
                            while (lastIdx < idx - 1)   // hole: repeat incoming frame
                            {
                                _videoProc!.Stdin.Write(item.frame, 0, item.frame.Length);
                                lastIdx++; FramesRepeated++;
                            }
                            _videoProc!.Stdin.Write(item.frame, 0, item.frame.Length);
                            lastIdx = idx;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"video pipe write failed: {e.Message}");
                    Stopping = true;
                    lock (_frameQ) ReturnFrameLocked(item.frame);
                    return;
                }
                lock (_frameQ) ReturnFrameLocked(item.frame);
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"video pump crashed: {e}"); }
        finally { lock (_procLock) { try { _videoProc?.CloseInput(); } catch { } } }
    }
    private DateTime _drainDeadline = DateTime.MinValue;

    private bool EnsureVideoProc()
    {
        lock (_procLock)
        {
            if (_videoProc != null) return true;
            if (Stopping) return false;
            var proc = FfmpegProc.Start(FfmpegArgs.Video(Width, Height, Fps, Crf, Encoder), TmpDir, "video");
            if (proc == null) { Plugin.Log.LogError("failed to start video ffmpeg"); Stopping = true; return false; }
            _videoProc = proc;
            return true;
        }
    }

    private void AudioPumpLoop()
    {
        var carry = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int take;
                lock (_ring)
                {
                    while (_ringHead == _ringTail)
                    {
                        if (_audioClosed || Stopping) return;
                        Monitor.Wait(_ring, 100);
                    }
                    take = (int)Math.Min(_ringHead - _ringTail, carry.Length);
                    int pos = (int)(_ringTail % _ring.Length);
                    int first = Math.Min(take, _ring.Length - pos);
                    Array.Copy(_ring, pos, carry, 0, first);
                    if (first < take) Array.Copy(_ring, 0, carry, first, take - first);
                    _ringTail += take;
                }

                lock (_procLock)
                {
                    if (Stopping) return;
                    if (_audioProc == null)
                    {
                        int rate, ch;
                        lock (_ring) { rate = AudioRate; ch = AudioChannels; }
                        var p = FfmpegProc.Start(FfmpegArgs.Audio(rate, Math.Max(1, ch)), TmpDir, "audio");
                        if (p == null) { Plugin.Log.LogError("failed to start audio ffmpeg"); Stopping = true; return; }
                        _audioProc = p;
                    }
                    try
                    {
                        if (T0Audio == null) T0Audio = NowSec;
                        _audioProc.Stdin.Write(carry, 0, take);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"audio pipe write failed: {e.Message}");
                        Stopping = true;
                        return;
                    }
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"audio pump crashed: {e}"); }
        finally { lock (_procLock) { try { _audioProc?.CloseInput(); } catch { } } }
    }

    // ================= finalize (background thread) =================

    internal void WaitPumps(int ms)
    {
        try { if (_videoThread.IsAlive) _videoThread.Join(ms); } catch { }
        try { if (_audioThread.IsAlive) _audioThread.Join(ms); } catch { }
    }

    internal void FinalizeAndMux(string finalPath)
    {
        var sw = Stopwatch.StartNew();
        FfmpegProc? v, a;
        lock (_procLock) { v = _videoProc; a = _audioProc; }

        v?.WaitForExit(8000); a?.WaitForExit(8000);
        v?.Kill(); a?.Kill();

        bool hasVideo = v != null && File.Exists(VideoTmpPath) && new FileInfo(VideoTmpPath).Length > 0;
        bool hasAudio = a != null && File.Exists(AudioTmpPath) && new FileInfo(AudioTmpPath).Length > 0;

        if (!hasVideo && !hasAudio)
        {
            Plugin.Log.LogWarning("take produced no data; tmp kept for inspection: " + TmpDir +
                $" (videoProc={v != null}, err: {v?.StderrText()})");
            return;
        }

        int exit;
        if (hasVideo && hasAudio)
        {
            double off = (T0Audio ?? 0) - (T0Video ?? 0);
            var mux = FfmpegProc.Start(FfmpegArgs.Mux(VideoTmpPath, AudioTmpPath, finalPath, off), TmpDir, "mux");
            mux?.WaitForExit(20000); mux?.Kill();
            exit = mux?.ExitCode ?? -1;
        }
        else if (hasVideo)
        {
            var mux = FfmpegProc.Start(FfmpegArgs.Remux(VideoTmpPath, finalPath), TmpDir, "mux");
            mux?.WaitForExit(20000); mux?.Kill();
            exit = mux?.ExitCode ?? -1;
        }
        else
        {
            File.Copy(AudioTmpPath, Path.ChangeExtension(finalPath, ".aac"), true);
            Plugin.Log.LogWarning("audio-only take (no video frames reached the pipe); kept raw aac.");
            exit = 0;
        }

        if (exit == 0 && (File.Exists(finalPath) || !hasVideo))
        {
            if (File.Exists(finalPath))
            {
                var size = new FileInfo(finalPath).Length;
                Plugin.Log.LogInfo(
                    $"take saved: {Path.GetFileName(finalPath)}  {size / 1024.0 / 1024.0:F1} MiB" +
                    $"  frames={FramesQueued} dropped={FramesDropped} repeated={FramesRepeated} audioDrops={AudioChunksDropped}" +
                    $"  finalize={sw.Elapsed.TotalSeconds:F1}s");
                try { Directory.Delete(TmpDir, true); } catch { }
            }
            else
            {
                try { Directory.Delete(TmpDir, true); } catch { }
            }
        }
        else
        {
            Plugin.Log.LogError($"mux failed (exit={exit}); video stderr: {v?.StderrText()}");
        }
    }

    internal void Discard()
    {
        try { Directory.Delete(TmpDir, true); } catch { }
    }
}
