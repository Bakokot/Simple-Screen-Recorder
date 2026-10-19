// Screen Capture: a lightweight screen recorder.
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

namespace ScreenCapture;

public sealed class RecordingOptions
{
    public required Rectangle Region { get; init; }
    public required OutputFormat Format { get; init; }
    public required VideoQuality Quality { get; init; }
    public required int Fps { get; init; }
    public required bool ShowCursor { get; init; }
    /// <summary>Sounds recorded with the video, mixed together (ignored for formats without sound).</summary>
    public IReadOnlyList<AudioSource> AudioSources { get; init; } = [];
    public required string OutputPath { get; init; }
    public required string FfmpegPath { get; init; }
}

/// <summary>
/// Records a screen zone into a temporary video while the sound is recorded in parallel, then muxes
/// everything into the requested format.
/// </summary>
/// <remarks>
/// The platform offers capture pipelines, best first (on Windows: GPU capture and encoding, then software
/// encoding, then frames grabbed by the app); the first one that starts is used.
/// </remarks>
public sealed class ScreenRecorder : IDisposable
{
    private readonly IPlatform _platform;
    private readonly RecordingOptions _o;
    private readonly Rectangle _region;
    private readonly string _tempDir;
    private readonly string _tempVideo;
    private readonly Stopwatch _clock = new();
    private readonly StringBuilder _ffmpegLog = new();
    private readonly List<IAudioTrack> _audio = [];
    private IReadOnlyList<VideoPipeline> _pipelines = [];
    private VideoPipeline? _pipeline;
    private Process? _encoder;
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private TimeSpan _videoStartedAt;
    private TimeSpan _videoDuration;
    private long _framesWritten;
    private Exception? _captureError;

    public TimeSpan Elapsed => _clock.IsRunning ? _clock.Elapsed - _videoStartedAt : TimeSpan.Zero;

    public ScreenRecorder(IPlatform platform, RecordingOptions options)
    {
        _platform = platform;
        _o = options;
        // yuv420p needs even dimensions.
        _region = new Rectangle(options.Region.X, options.Region.Y, options.Region.Width & ~1, options.Region.Height & ~1);
        if (_region.Width < 16 || _region.Height < 16) throw new ArgumentException("The capture zone is too small.");

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
                foreach (var source in _o.AudioSources)
                {
                    var name = source switch
                    {
                        SystemSound => "system",
                        AppSound app => $"app-{app.ProcessId}",
                        _ => "mic",
                    };
                    _audio.Add(_platform.CreateAudioTrack(source, Path.Combine(_tempDir, $"{name}.wav")));
                }
            }
            // Audio starts before the first video frame; the lead-in is cut when muxing.
            foreach (var a in _audio) a.Start(_clock);

            _pipelines = await _platform.VideoPipelinesAsync(_o, _region);
            foreach (var pipeline in _pipelines)
            {
                _pipeline = pipeline;
                switch (pipeline)
                {
                    case FrameGrabberPipeline frames:
                        if (await TryStartFrameGrabberAsync(frames)) return;
                        frames.OnFailed?.Invoke();
                        break;
                    case FfmpegGrabPipeline grab:
                        if (await TryStartFfmpegGrabAsync(grab)) return;
                        grab.OnFailed?.Invoke();
                        break;
                }
            }
            throw new InvalidOperationException("The screen could not be captured.\n\n" + Log());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Stops recording and produces the final file. Returns the output path.</summary>
    public async Task<string> StopAsync(IProgress<string> status)
    {
        _stopRequested = true;
        if (_pipeline is FrameGrabberPipeline)
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
        if (_pipeline is FrameGrabberPipeline) _videoDuration = TimeSpan.FromSeconds((double)_framesWritten / _o.Fps);

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

    // ---------------------------------------------------------------- capture by FFmpeg

    /// <summary>Launches FFmpeg grabbing the screen itself; false if it could not start capturing.</summary>
    private async Task<bool> TryStartFfmpegGrabAsync(FfmpegGrabPipeline pipeline)
    {
        lock (_ffmpegLog) _ffmpegLog.Clear();
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ProgressParser(this, firstFrame);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y" };
        args.AddRange(pipeline.Arguments);
        // Screen grabbers may skip frames while the screen does not change; editors prefer a constant frame rate.
        args.AddRange(["-fps_mode", "cfr", "-progress", "pipe:1", "-stats_period", "0.1", _tempVideo]);
        var encoder = StartFfmpeg(args, redirectInput: true, progress.OnLine);

        var started = await Task.WhenAny(firstFrame.Task, encoder.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        if (started == firstFrame.Task)
        {
            _encoder = encoder;
            return true;
        }

        Debug.WriteLine($"{pipeline.Name} capture failed, falling back:\n{Log()}");
        try { if (!encoder.HasExited) encoder.Kill(); } catch { }
        encoder.Dispose();
        return false;
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

    // ---------------------------------------------------------------- capture by the app (fallback)

    /// <summary>Starts grabbing frames and the encoder; false if no frame could be encoded.</summary>
    private async Task<bool> TryStartFrameGrabberAsync(FrameGrabberPipeline pipeline)
    {
        lock (_ffmpegLog) _ffmpegLog.Clear();
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoder = StartFfmpeg(RawFramesEncoderArguments(pipeline), redirectInput: true);
        _encoder = encoder;
        _captureThread = new Thread(() => CaptureLoop(pipeline, firstFrame)) { IsBackground = true, Name = "Screen capture" };
        _captureThread.Start();

        var started = await Task.WhenAny(firstFrame.Task, encoder.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        if (started == firstFrame.Task && firstFrame.Task.IsCompletedSuccessfully && !encoder.HasExited) return true;

        Debug.WriteLine($"{pipeline.Name} capture failed, falling back:\n{_captureError}\n{Log()}");
        _stopRequested = true;
        try { if (!encoder.HasExited) encoder.Kill(); } catch { }
        // Grabbers give up waiting for a first frame after a while (see IFrameGrabber.Grab).
        await Task.Run(() => _captureThread.Join(TimeSpan.FromSeconds(15)));
        encoder.Dispose();
        _encoder = null;
        _captureThread = null;
        _stopRequested = false;
        _framesWritten = 0;
        _captureError = null;
        return false;
    }

    private void CaptureLoop(FrameGrabberPipeline pipeline, TaskCompletionSource firstFrame)
    {
        try
        {
            using var grabber = pipeline.CreateGrabber();
            var stdin = _encoder!.StandardInput.BaseStream;
            double frameMs = 1000.0 / _o.Fps;
            var sw = new Stopwatch();

            while (!_stopRequested)
            {
                var frame = grabber.Grab();
                if (!sw.IsRunning)
                {
                    // The first Grab may wait for the source to deliver a frame: the video starts now.
                    _videoStartedAt = _clock.Elapsed;
                    sw.Start();
                }

                // Keep a constant frame rate tied to wall-clock time: if capture or encoding fell behind,
                // repeat the frame so the video duration matches reality (and stays in sync with audio).
                long due = (long)(sw.Elapsed.TotalMilliseconds / frameMs) + 1;
                if (due <= _framesWritten) due = _framesWritten + 1;
                while (_framesWritten < due)
                {
                    stdin.Write(frame);
                    _framesWritten++;
                }
                firstFrame.TrySetResult();

                var wait = _framesWritten * frameMs - sw.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
            }
        }
        catch (Exception ex)
        {
            _captureError = ex;
            firstFrame.TrySetException(ex);
        }
    }

    // ---------------------------------------------------------------- ffmpeg

    private List<string> RawFramesEncoderArguments(FrameGrabberPipeline pipeline)
    {
        var size = pipeline.FrameSize ?? _region.Size;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        args.AddRange(pipeline.InputArguments);
        args.AddRange([
            "-f", "rawvideo", "-pix_fmt", pipeline.PixelFormat,
            "-video_size", $"{size.Width}x{size.Height}",
            "-framerate", _o.Fps.ToString(CultureInfo.InvariantCulture),
            "-i", "-",
        ]);
        args.AddRange(pipeline.Encoder ?? EncoderArguments.X264(_o, new Rectangle(Point.Empty, size)));
        args.Add(_tempVideo);
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
        foreach (var r in _pipelines.Select(p => p.Resources).OfType<IDisposable>().Distinct()) r.Dispose();
        _pipelines = [];
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>FFmpeg encoder options shared by the capture pipelines.</summary>
public static class EncoderArguments
{
    /// <summary>libx264 options for the temporary video, from the quality and the load of the capture.</summary>
    public static List<string> X264(RecordingOptions o, Rectangle region)
    {
        var args = new List<string> { "-c:v", "libx264" };
        if (o.Quality == VideoQuality.Lossless)
        {
            args.AddRange(["-preset", "ultrafast", "-qp", "0", "-pix_fmt", "yuv444p"]);
        }
        else if (FormatInfo.IsH264(o.Format))
        {
            // Final encode, done in real time.
            bool heavy = o.Fps > 30 || (long)region.Width * region.Height > 1920L * 1080;
            args.AddRange(["-preset", heavy ? "superfast" : "veryfast",
                           "-crf", FormatInfo.X264Crf(o.Quality).ToString(CultureInfo.InvariantCulture),
                           "-pix_fmt", "yuv420p"]);
        }
        else
        {
            // High-quality intermediate, transcoded to WebM/GIF after recording.
            args.AddRange(["-preset", "ultrafast", "-crf", "8", "-pix_fmt", "yuv420p"]);
        }
        return args;
    }
}
