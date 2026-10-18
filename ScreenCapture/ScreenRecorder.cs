// Screen Capture: a lightweight screen recorder for Windows.
// Copyright (C) 2026 Bakou
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.CoreAudioApi;
using static ScreenCapture.NativeMethods;

namespace ScreenCapture;

internal sealed class RecordingOptions
{
    public required Rectangle Region { get; init; }
    public required OutputFormat Format { get; init; }
    public required VideoQuality Quality { get; init; }
    public required int Fps { get; init; }
    public required bool ShowCursor { get; init; }
    /// <summary>Render endpoint to capture (loopback), or null for no system sound.</summary>
    public MMDevice? SystemAudioDevice { get; init; }
    /// <summary>Processes whose sound is recorded instead of the whole output device (each with its child processes).</summary>
    public IReadOnlyList<int> AppProcessIds { get; init; } = [];
    /// <summary>Capture endpoint (microphone), or null for no microphone.</summary>
    public MMDevice? MicrophoneDevice { get; init; }
    public required string OutputPath { get; init; }
    public required string FfmpegPath { get; init; }
}

/// <summary>
/// Records a screen zone into a temporary video while WASAPI records audio in parallel, then muxes
/// everything into the requested format.
/// </summary>
/// <remarks>
/// Video is preferably captured by FFmpeg itself with Desktop Duplication (ddagrab) and encoded on the
/// GPU with NVENC: frames never leave video memory, so a game keeps its frame rate. When that is not
/// possible (zone spanning several screens, rotated screen, no NVIDIA GPU…) it falls back to software
/// encoding, and as a last resort to GDI capture piped to FFmpeg.
/// </remarks>
internal sealed class ScreenRecorder : IDisposable
{
    private enum Pipeline { GpuNvenc, GpuX264, Gdi }

    /// <summary>Adapters on which NVENC could not encode the captured frames (e.g. laptop iGPU screens).</summary>
    private static readonly HashSet<int> NvencFailed = [];

    // ddagrab tags its frames as full-range RGB and encoders copy that into the file, but they convert to
    // limited-range BT.601 YUV; tag the video with what it really is so players show the right colors.
    private const string ColorTags = "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709";

    private readonly RecordingOptions _o;
    private readonly Rectangle _region;
    private readonly DxgiOutput? _output;
    private readonly string _tempDir;
    private readonly string _tempVideo;
    private readonly Stopwatch _clock = new();
    private readonly StringBuilder _ffmpegLog = new();
    private readonly List<IAudioTrack> _audio = [];
    private Pipeline _pipeline;
    private Process? _encoder;
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private TimeSpan _videoStartedAt;
    private TimeSpan _videoDuration;
    private long _framesWritten;
    private Exception? _captureError;

    public TimeSpan Elapsed => _clock.IsRunning ? _clock.Elapsed - _videoStartedAt : TimeSpan.Zero;

    public ScreenRecorder(RecordingOptions options)
    {
        _o = options;
        // yuv420p needs even dimensions.
        _region = new Rectangle(options.Region.X, options.Region.Y, options.Region.Width & ~1, options.Region.Height & ~1);
        if (_region.Width < 16 || _region.Height < 16) throw new ArgumentException("The capture zone is too small.");
        _output = DxgiOutputs.Containing(_region);

        _tempDir = Path.Combine(Path.GetTempPath(), "ScreenCapture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempVideo = Path.Combine(_tempDir, "video.mkv");
    }

    /// <summary>Starts recording; completes once the first video frame has been captured.</summary>
    public async Task StartAsync()
    {
        _clock.Start();
        try
        {
            if (FormatInfo.SupportsAudio(_o.Format))
            {
                if (_o.SystemAudioDevice != null)
                    _audio.Add(new AudioRecorder(_o.SystemAudioDevice, loopback: true, Path.Combine(_tempDir, "system.wav")));
                foreach (var pid in _o.AppProcessIds)
                    _audio.Add(new AppAudioRecorder(pid, Path.Combine(_tempDir, $"app-{pid}.wav")));
                if (_o.MicrophoneDevice != null)
                    _audio.Add(new AudioRecorder(_o.MicrophoneDevice, loopback: false, Path.Combine(_tempDir, "mic.wav")));
            }
            // Audio starts before the first video frame; the lead-in is cut when muxing.
            foreach (var a in _audio) a.Start(_clock);

            foreach (var pipeline in Pipelines())
            {
                _pipeline = pipeline;
                if (pipeline == Pipeline.Gdi)
                {
                    StartGdi();
                    return;
                }
                if (await TryStartGpuAsync()) return;
                if (pipeline == Pipeline.GpuNvenc) lock (NvencFailed) NvencFailed.Add(_output!.AdapterIndex);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IEnumerable<Pipeline> Pipelines()
    {
        if (_output != null)
        {
            bool nvenc;
            lock (NvencFailed) nvenc = !NvencFailed.Contains(_output.AdapterIndex);
            // NVENC refuses frames smaller than about 145 × 49.
            if (nvenc && _region.Width >= 160 && _region.Height >= 96) yield return Pipeline.GpuNvenc;
            yield return Pipeline.GpuX264;
        }
        yield return Pipeline.Gdi;
    }

    /// <summary>Stops recording and produces the final file. Returns the output path.</summary>
    public async Task<string> StopAsync(IProgress<string> status)
    {
        _stopRequested = true;
        if (_pipeline == Pipeline.Gdi)
        {
            await Task.Run(() => _captureThread?.Join());
        }
        else
        {
            // "q" makes FFmpeg stop grabbing and finish the file cleanly.
            try
            {
                _encoder!.StandardInput.Write('q');
                _encoder.StandardInput.Flush();
            }
            catch (IOException) { /* encoder already gone; reported below */ }
        }
        await Task.WhenAll(_audio.Select(a => a.StopAsync()));

        status.Report("Finishing video encoding…");
        try { _encoder!.StandardInput.Close(); } catch (IOException) { /* encoder already gone; reported below */ }
        await _encoder!.WaitForExitAsync();
        if (_encoder.ExitCode != 0 || Interlocked.Read(ref _framesWritten) == 0)
            throw new InvalidOperationException("Video encoding failed.\n\n" + Log(), _captureError);
        if (_pipeline == Pipeline.Gdi) _videoDuration = TimeSpan.FromSeconds((double)_framesWritten / _o.Fps);

        status.Report(FormatInfo.IsH264(_o.Format) ? "Saving file…" : $"Converting to {_o.Format.ToString().ToUpperInvariant()}…");
        var mux = StartFfmpeg(MuxArguments(), redirectInput: false);
        await mux.WaitForExitAsync();
        if (mux.ExitCode != 0)
            throw new InvalidOperationException("Creating the output file failed.\n\n" + Log());

        return _o.OutputPath;
    }

    private string Log()
    {
        lock (_ffmpegLog) return _ffmpegLog.ToString().Trim();
    }

    // ---------------------------------------------------------------- GPU capture (FFmpeg ddagrab)

    /// <summary>Launches FFmpeg grabbing the screen itself; false if it could not start capturing.</summary>
    private async Task<bool> TryStartGpuAsync()
    {
        lock (_ffmpegLog) _ffmpegLog.Clear();
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ProgressParser(this, firstFrame);
        var encoder = StartFfmpeg(GpuEncoderArguments(), redirectInput: true, progress.OnLine);

        var started = await Task.WhenAny(firstFrame.Task, encoder.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        if (started == firstFrame.Task)
        {
            _encoder = encoder;
            return true;
        }

        Debug.WriteLine($"{_pipeline} capture failed, falling back:\n{Log()}");
        try { if (!encoder.HasExited) encoder.Kill(); } catch { }
        encoder.Dispose();
        return false;
    }

    private List<string> GpuEncoderArguments()
    {
        var inv = CultureInfo.InvariantCulture;
        var o = _output!;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y" };
        // ddagrab uses the default adapter unless given a device: the screen may be plugged into another GPU.
        if (o.AdapterIndex != 0)
            args.AddRange(["-init_hw_device", $"d3d11va=dx:{o.AdapterIndex}", "-filter_hw_device", "dx"]);

        var grab = string.Create(inv,
            $"ddagrab=output_idx={o.OutputIndex}:framerate={_o.Fps}:video_size={_region.Width}x{_region.Height}" +
            $":offset_x={_region.X - o.Bounds.X}:offset_y={_region.Y - o.Bounds.Y}:draw_mouse={(_o.ShowCursor ? 1 : 0)}");
        bool lossless = _o.Quality == VideoQuality.Lossless;

        if (_pipeline == Pipeline.GpuNvenc)
        {
            args.AddRange(["-filter_complex", $"{grab},{ColorTags}", "-c:v", "h264_nvenc"]);
            if (lossless)
                args.AddRange(["-preset", "p4", "-tune", "lossless", "-rgb_mode", "yuv444"]);
            else
            {
                // WebM / GIF are transcoded after recording: keep a high-quality intermediate.
                var cq = FormatInfo.IsH264(_o.Format) ? FormatInfo.NvencCq(_o.Quality) : 12;
                args.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(inv), "-b:v", "0"]);
            }
            // No B-frames and no output delay: the progress reports then tell when capture began (see ProgressParser).
            args.AddRange(["-bf", "0", "-delay", "0"]);
        }
        else
        {
            args.AddRange(["-filter_complex", $"{grab},hwdownload,format=bgra,setparams=color_primaries=bt709:color_trc=bt709"]);
            args.AddRange(X264Arguments());
            if (!lossless) args.AddRange(["-colorspace", "smpte170m"]);
            args.AddRange(["-tune", "zerolatency"]);
        }

        // ddagrab skips frames while the screen does not change; editors prefer a constant frame rate.
        args.AddRange(["-fps_mode", "cfr", "-progress", "pipe:1", "-stats_period", "0.1", _tempVideo]);
        return args;
    }

    /// <summary>Reads FFmpeg's -progress reports: frame count, encoded duration, and when the first frame came.</summary>
    /// <remarks>
    /// Without encoder delay, out_time is the end of the last frame written, which was captured within about
    /// a frame of the report: so "now − out_time" is when the first frame was captured.
    /// </remarks>
    private sealed class ProgressParser(ScreenRecorder owner, TaskCompletionSource firstFrame)
    {
        private long _frame, _outTimeUs;

        public void OnLine(string line)
        {
            var eq = line.IndexOf('=');
            if (eq < 0) return;
            var value = line.AsSpan(eq + 1).Trim();
            switch (line.AsSpan(0, eq))
            {
                case "frame":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _frame);
                    break;
                case "out_time_us":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _outTimeUs);
                    break;
                case "progress" when _frame > 0:
                    var encoded = TimeSpan.FromMicroseconds(_outTimeUs);
                    Interlocked.Exchange(ref owner._framesWritten, _frame);
                    owner._videoDuration = encoded;
                    if (!firstFrame.Task.IsCompleted)
                    {
                        var start = owner._clock.Elapsed - encoded;
                        owner._videoStartedAt = start > TimeSpan.Zero ? start : TimeSpan.Zero;
                        firstFrame.TrySetResult();
                    }
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- GDI capture (fallback)

    private void StartGdi()
    {
        lock (_ffmpegLog) _ffmpegLog.Clear();
        _encoder = StartFfmpeg(GdiEncoderArguments(), redirectInput: true);
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Screen capture" };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        int w = _region.Width, h = _region.Height, frameBytes = w * h * 4;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldObj = IntPtr.Zero;
        IntPtr lastCursor = IntPtr.Zero;
        int hotX = 0, hotY = 0;
        timeBeginPeriod(1);
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            var header = new BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down rows, as FFmpeg expects
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = CreateDIBSection(memDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the capture buffer.");
            oldObj = SelectObject(memDc, dib);

            var stdin = _encoder!.StandardInput.BaseStream;
            double frameMs = 1000.0 / _o.Fps;
            _videoStartedAt = _clock.Elapsed;
            var sw = Stopwatch.StartNew();

            while (!_stopRequested)
            {
                BitBlt(memDc, 0, 0, w, h, screenDc, _region.X, _region.Y, SRCCOPY);

                if (_o.ShowCursor)
                {
                    var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
                    if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                    {
                        if (ci.hCursor != lastCursor && GetIconInfo(ci.hCursor, out var ii))
                        {
                            lastCursor = ci.hCursor;
                            hotX = ii.xHotspot;
                            hotY = ii.yHotspot;
                            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                        }
                        DrawIconEx(memDc, ci.ptScreenPos.X - hotX - _region.X, ci.ptScreenPos.Y - hotY - _region.Y,
                            ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                    }
                }
                GdiFlush();

                // Keep a constant frame rate tied to wall-clock time: if capture or encoding fell behind,
                // repeat the frame so the video duration matches reality (and stays in sync with audio).
                long due = (long)(sw.Elapsed.TotalMilliseconds / frameMs) + 1;
                if (due <= _framesWritten) due = _framesWritten + 1;
                unsafe
                {
                    var frame = new ReadOnlySpan<byte>((void*)bits, frameBytes);
                    while (_framesWritten < due)
                    {
                        stdin.Write(frame);
                        _framesWritten++;
                    }
                }

                var wait = _framesWritten * frameMs - sw.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
            }
        }
        catch (Exception ex)
        {
            _captureError = ex;
        }
        finally
        {
            timeEndPeriod(1);
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------------------------------------------------------- ffmpeg

    private List<string> GdiEncoderArguments()
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-video_size", $"{_region.Width}x{_region.Height}",
            "-framerate", _o.Fps.ToString(CultureInfo.InvariantCulture),
            "-i", "-",
        };
        args.AddRange(X264Arguments());
        args.Add(_tempVideo);
        return args;
    }

    private List<string> X264Arguments()
    {
        var args = new List<string> { "-c:v", "libx264" };
        if (_o.Quality == VideoQuality.Lossless)
        {
            args.AddRange(["-preset", "ultrafast", "-qp", "0", "-pix_fmt", "yuv444p"]);
        }
        else if (FormatInfo.IsH264(_o.Format))
        {
            // Final encode, done in real time.
            bool heavy = _o.Fps > 30 || (long)_region.Width * _region.Height > 1920L * 1080;
            args.AddRange(["-preset", heavy ? "superfast" : "veryfast",
                           "-crf", FormatInfo.X264Crf(_o.Quality).ToString(CultureInfo.InvariantCulture),
                           "-pix_fmt", "yuv420p"]);
        }
        else
        {
            // High-quality intermediate, transcoded to WebM/GIF after recording.
            args.AddRange(["-preset", "ultrafast", "-crf", "8", "-pix_fmt", "yuv420p"]);
        }
        return args;
    }

    private List<string> MuxArguments()
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", _tempVideo };

        var tracks = FormatInfo.SupportsAudio(_o.Format) ? _audio.Where(a => a.HasData && a.Error == null).ToList() : [];
        foreach (var a in tracks)
        {
            // Audio started slightly before the first video frame: skip that lead-in.
            var lead = Math.Max(0, (_videoStartedAt - a.StartedAt).TotalSeconds);
            args.AddRange(["-ss", lead.ToString("0.000", inv), "-i", a.FilePath]);
        }

        args.AddRange(["-map", "0:v"]);
        if (tracks.Count == 1)
        {
            args.AddRange(["-map", "1:a"]);
        }
        else if (tracks.Count > 1)
        {
            var inputs = string.Concat(Enumerable.Range(1, tracks.Count).Select(i => $"[{i}:a]"));
            args.AddRange(["-filter_complex", $"{inputs}amix=inputs={tracks.Count}:duration=longest:normalize=0[aout]", "-map", "[aout]"]);
        }

        switch (_o.Format)
        {
            case OutputFormat.Mp4:
            case OutputFormat.Mov:
                args.AddRange(["-c:v", "copy", "-movflags", "+faststart"]);
                break;
            case OutputFormat.Mkv:
                args.AddRange(["-c:v", "copy"]);
                break;
            case OutputFormat.WebM:
                args.AddRange(["-c:v", "libvpx-vp9", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]);
                if (_o.Quality == VideoQuality.Lossless) args.AddRange(["-lossless", "1"]);
                else args.AddRange(["-crf", FormatInfo.Vp9Crf(_o.Quality).ToString(inv), "-pix_fmt", "yuv420p"]);
                break;
            case OutputFormat.Gif:
                var dither = _o.Quality >= VideoQuality.High ? "sierra2_4a" : "bayer:bayer_scale=4";
                args.AddRange(["-vf",
                    $"fps={FormatInfo.GifFps(_o.Quality, _o.Fps)},split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither={dither}",
                    "-loop", "0"]);
                break;
        }

        if (tracks.Count > 0)
        {
            args.AddRange(_o.Format == OutputFormat.WebM ? ["-c:a", "libopus"] : ["-c:a", "aac"]);
            args.AddRange(["-b:a", FormatInfo.AudioBitrate(_o.Quality)]);
        }

        args.AddRange(["-t", _videoDuration.TotalSeconds.ToString("0.000", inv), _o.OutputPath]);
        return args;
    }

    private Process StartFfmpeg(List<string> arguments, bool redirectInput, Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_ffmpegLog) _ffmpegLog.AppendLine(e.Data); };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput?.Invoke(e.Data); };
        p.Start();
        // Whatever is in the foreground (a game…) gets the CPU first.
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* already exited */ }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        return p;
    }

    public void Dispose()
    {
        _stopRequested = true;
        _captureThread?.Join(2000);
        try { if (_encoder is { HasExited: false }) _encoder.Kill(); } catch { }
        _encoder?.Dispose();
        foreach (var a in _audio) a.Dispose();
        _audio.Clear();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
