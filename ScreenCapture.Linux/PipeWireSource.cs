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

using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCapture;

/// <summary>
/// Frames of a shared screen, read from PipeWire by GStreamer and passed to us as raw BGRx through a named
/// pipe. A background thread keeps the latest frame; the screen only sends frames when it changes.
/// </summary>
internal sealed partial class PipeWireSource : IDisposable
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _gst;
    private readonly string _dir;
    private readonly string _fifo;
    private readonly StringBuilder _log = new();
    private readonly TaskCompletionSource<Size> _caps = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _firstFrame = new();
    private readonly object _lock = new();
    private readonly Thread _reader;
    private byte[] _latest = [];
    private volatile bool _stopped;
    private Exception? _error;

    /// <summary>Size of the frames, in pixels.</summary>
    public Size Size { get; private set; }

    private PipeWireSource(string gstLaunch, uint nodeId)
    {
        _dir = Path.Combine(Path.GetTempPath(), "ScreenCapture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fifo = Path.Combine(_dir, "frames");
        if (Libc.mkfifo(_fifo, 0x180 /* 0600 */) != 0) throw new IOException("Could not create the frame pipe.");

        var psi = new ProcessStartInfo(gstLaunch)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -v prints the negotiated caps, which give the frame size.
        foreach (var a in (string[])["-v", "pipewiresrc", $"path={nodeId}", "do-timestamp=true",
                     "!", "videoconvert", "!", "video/x-raw,format=BGRx",
                     "!", "filesink", $"location={_fifo}", "sync=false", "buffer-mode=unbuffered"])
            psi.ArgumentList.Add(a);

        _gst = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _gst.OutputDataReceived += (_, e) => OnOutput(e.Data);
        _gst.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        _gst.Exited += (_, _) =>
        {
            _caps.TrySetException(new InvalidOperationException("GStreamer stopped.\n\n" + Log()));
            _firstFrame.Set();
        };
        _gst.Start();
        _gst.BeginOutputReadLine();
        _gst.BeginErrorReadLine();

        _reader = new Thread(ReadFrames) { IsBackground = true, Name = "PipeWire frames" };
        _reader.Start();
    }

    /// <summary>Starts reading the screen; completes once the frame size is known.</summary>
    public static async Task<PipeWireSource> StartAsync(string gstLaunch, uint nodeId)
    {
        var source = new PipeWireSource(gstLaunch, nodeId);
        try
        {
            var caps = source._caps.Task;
            if (await Task.WhenAny(caps, Task.Delay(FirstFrameTimeout)) != caps)
                throw new TimeoutException("The shared screen sent no picture.\n\n" + source.Log());
            source.Size = await caps;
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    [GeneratedRegex(@"GstCapsFilter.*caps = video/x-raw.*width=\(int\)(\d+), height=\(int\)(\d+)")]
    private static partial Regex CapsLine();

    private void OnOutput(string? line)
    {
        if (line == null) return;
        if (!_caps.Task.IsCompleted && CapsLine().Match(line) is { Success: true } m)
            _caps.TrySetResult(new Size(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        else lock (_log) _log.AppendLine(line);
    }

    private string Log()
    {
        lock (_log) return _log.ToString().Trim();
    }

    private void ReadFrames()
    {
        try
        {
            // Opening a named pipe waits for GStreamer to open the other end.
            using var pipe = new FileStream(_fifo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
            var size = _caps.Task.GetAwaiter().GetResult();
            var back = new byte[size.Width * size.Height * 4];
            while (!_stopped)
            {
                pipe.ReadExactly(back);
                lock (_lock) (_latest, back) = (back, _latest.Length == back.Length ? _latest : new byte[back.Length]);
                _firstFrame.Set();
            }
        }
        catch (Exception ex) when (!_stopped)
        {
            _error = ex;
            _firstFrame.Set();
        }
        catch { /* stopping */ }
    }

    /// <summary>A grabber that crops the frames to <paramref name="crop"/> (in frame pixels).</summary>
    public IFrameGrabber CreateGrabber(Rectangle crop) => new Grabber(this, crop);

    private sealed class Grabber(PipeWireSource source, Rectangle crop) : IFrameGrabber
    {
        private readonly byte[] _frame = new byte[crop.Width * crop.Height * 4];

        public ReadOnlySpan<byte> Grab()
        {
            if (!source._firstFrame.Wait(FirstFrameTimeout) || source._latest.Length == 0)
                throw new InvalidOperationException("The shared screen sent no picture.\n\n" + source.Log(), source._error);
            int stride = source.Size.Width * 4, row = crop.Width * 4;
            lock (source._lock)
            {
                for (int y = 0; y < crop.Height; y++)
                    Buffer.BlockCopy(source._latest, (crop.Y + y) * stride + crop.X * 4, _frame, y * row, row);
            }
            return _frame;
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        _stopped = true;
        try { if (!_gst.HasExited) _gst.Kill(); } catch { }
        // Unblocks the reader if GStreamer never opened the pipe (fails harmlessly if nobody reads it).
        int fd = Libc.open(_fifo, Libc.O_WRONLY | Libc.O_NONBLOCK);
        if (fd >= 0) Libc.close(fd);
        _reader.Join(2000);
        _gst.Dispose();
        _firstFrame.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
