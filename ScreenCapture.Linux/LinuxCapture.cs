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
using System.Globalization;

namespace ScreenCapture;

/// <summary>
/// Capture pipelines for Linux. On X11, FFmpeg grabs the screen itself (x11grab). On Wayland, apps cannot read
/// the screen: the desktop shares it through the ScreenCast portal as a PipeWire stream, which GStreamer reads
/// and hands to us as raw frames. Either way the frames are encoded with NVENC, VAAPI or x264.
/// </summary>
internal static class LinuxCapture
{
    public static bool IsWayland => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";

    public static Task<IReadOnlyList<VideoPipeline>> PipelinesAsync(RecordingOptions o, Rectangle region) =>
        IsWayland ? WaylandPipelinesAsync(o, region) : Task.Run(() => X11Pipelines(o, region));

    // ---------------------------------------------------------------- X11

    private static IReadOnlyList<VideoPipeline> X11Pipelines(RecordingOptions o, Rectangle region)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        string[] grab =
        [
            "-f", "x11grab", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{region.Width}x{region.Height}", "-draw_mouse", o.ShowCursor ? "1" : "0",
            "-i", string.Create(CultureInfo.InvariantCulture, $"{display}+{region.X},{region.Y}"),
        ];
        return LinuxEncoders.For(o, region.Size)
            .Select(e => (VideoPipeline)new FfmpegGrabPipeline($"x11grab + {e.Name}", [.. e.InputArguments, .. grab, .. e.Arguments]))
            .ToList();
    }

    // ---------------------------------------------------------------- Wayland

    private static async Task<IReadOnlyList<VideoPipeline>> WaylandPipelinesAsync(RecordingOptions o, Rectangle region)
    {
        var gst = FindProgram("gst-launch-1.0") ?? throw new InvalidOperationException(
            "Recording on Wayland needs GStreamer with its PipeWire plugin.\n\n" +
            "Install it with your package manager, e.g. on Ubuntu / Debian:\n\n    sudo apt install gstreamer1.0-pipewire gstreamer1.0-tools");

        var session = await ScreenCastSession.StartAsync(o.ShowCursor);
        PipeWireSource? source = null;
        try
        {
            var stream = session.Containing(region) ?? throw new InvalidOperationException(
                "Share the screen that contains the zone to record.\n\nOn Wayland, a zone spanning several screens cannot be recorded.");
            source = await PipeWireSource.StartAsync(gst, stream.NodeId);
            var crop = Crop(region, stream.Bounds, source.Size);
            var resources = new Disposables(source, session);
            var frames = source;
            var encoders = await Task.Run(() => LinuxEncoders.For(o, crop.Size));
            return encoders
                .Select(e => (VideoPipeline)new FrameGrabberPipeline($"PipeWire + {e.Name}", () => frames.CreateGrabber(crop))
                {
                    PixelFormat = "bgr0",
                    FrameSize = crop.Size,
                    InputArguments = e.InputArguments,
                    Encoder = e.Arguments,
                    Resources = resources,
                })
                .ToList();
        }
        catch
        {
            source?.Dispose();
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The zone in frame pixels. Positions are in logical pixels while frames are in device pixels, which
    /// differ on scaled screens.
    /// </summary>
    private static Rectangle Crop(Rectangle region, Rectangle? screen, Size frame)
    {
        var s = screen ?? new Rectangle(Point.Empty, frame);
        double sx = frame.Width / (double)s.Width, sy = frame.Height / (double)s.Height;
        int x = Math.Clamp((int)Math.Round((region.X - s.X) * sx), 0, frame.Width - 16);
        int y = Math.Clamp((int)Math.Round((region.Y - s.Y) * sy), 0, frame.Height - 16);
        // yuv420p needs even dimensions.
        int w = Math.Min((int)Math.Round(region.Width * sx), frame.Width - x) & ~1;
        int h = Math.Min((int)Math.Round(region.Height * sy), frame.Height - y) & ~1;
        return new Rectangle(x, y, w, h);
    }

    public static string? FindProgram(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items) item.Dispose();
        }
    }
}
