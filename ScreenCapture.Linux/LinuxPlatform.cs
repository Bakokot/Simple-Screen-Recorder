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
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using ScreenCapture.DBus;
using Tmds.DBus;

namespace ScreenCapture;

/// <summary>
/// Linux, on X11 or Wayland: x11grab or the ScreenCast portal for video, PulseAudio / PipeWire for sound.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    public string FfmpegInstallHint =>
        "Install it with your package manager, e.g. on Ubuntu / Debian:\n\n    sudo apt install ffmpeg\n\n" +
        "FFmpeg 6.0 or later is needed.";

    // ---------------------------------------------------------------- sound

    public IReadOnlyList<AudioDevice> OutputDevices() => PulseAudio.Outputs();

    public IReadOnlyList<AudioDevice> InputDevices() => PulseAudio.Inputs();

    // Per-app capture is not written yet on Linux (it would link PipeWire streams of the chosen apps).
    public bool SupportsAppAudio => false;

    public IReadOnlyList<AudioApp> AppsWithSound() => [];

    public AudioApp? FindRunningApp(string exe) => null;

    public IReadOnlyList<int> RootProcessIds(string exe) => [];

    public byte[]? AppIconPng(AudioApp app) => null;

    public IAudioTrack CreateAudioTrack(AudioSource source, string filePath)
    {
        var ffmpeg = FfmpegLocator.Find() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var name = source switch
        {
            SystemSound s => s.DeviceId != null ? s.DeviceId + PulseAudio.MonitorSuffix : PulseAudio.DefaultMonitor,
            Microphone m => m.DeviceId ?? PulseAudio.DefaultSource,
            _ => throw new NotSupportedException("Recording the sound of single apps is not supported on Linux yet."),
        };
        return new FfmpegAudioTrack(ffmpeg, name, filePath);
    }

    // ---------------------------------------------------------------- video

    public Task<IReadOnlyList<VideoPipeline>> VideoPipelinesAsync(RecordingOptions options, Rectangle region) =>
        LinuxCapture.PipelinesAsync(options, region);

    public async Task<byte[]?> ScreenshotPngAsync(Rectangle region, bool showCursor)
    {
        if (!LinuxCapture.IsWayland) return await Task.Run(() => X11Screenshot(region, showCursor));
        // The portal only gives the whole desktop (without the cursor): cut the zone out of it.
        if (await PortalScreenshotAsync() is not { } png) return null;
        var screens = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens.All;
        if (screens is not { Count: > 0 }) return null;
        var desktop = screens.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        return Crop(png, region, desktop);
    }

    /// <summary>
    /// The zone out of a picture of the whole desktop. Positions are in logical pixels while the picture may be
    /// in device pixels (scaled screens): it is stretched over the desktop, as in the region selector.
    /// </summary>
    private static byte[]? Crop(byte[] png, Rectangle region, PixelRect desktop)
    {
        try
        {
            using var full = new Bitmap(new MemoryStream(png));
            double sx = full.PixelSize.Width / (double)desktop.Width, sy = full.PixelSize.Height / (double)desktop.Height;
            var source = new Rect((region.X - desktop.X) * sx, (region.Y - desktop.Y) * sy, region.Width * sx, region.Height * sy);
            var size = new PixelSize(Math.Max(1, (int)Math.Round(source.Width)), Math.Max(1, (int)Math.Round(source.Height)));
            using var target = new RenderTargetBitmap(size);
            using (var g = target.CreateDrawingContext())
                g.DrawImage(full, source, new Rect(0, 0, size.Width, size.Height));
            using var output = new MemoryStream();
            target.Save(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- desktop integration

    public async Task<Rectangle?> SelectRegionAsync(Rectangle? initial)
    {
        var selector = new RegionSelectorWindow(initial);
        var d = selector.Desktop;
        var png = await Task.Run(() => LinuxCapture.IsWayland
            ? PortalScreenshotAsync()
            : Task.FromResult(X11Screenshot(new Rectangle(d.X, d.Y, d.Width, d.Height), showCursor: false)));
        if (png != null)
        {
            try { selector.SetScreenshot(new Bitmap(new MemoryStream(png))); }
            catch { /* unreadable: select over the live screen */ }
        }
        selector.Show();
        return await selector.Result;
    }

    /// <summary>A PNG of part of the X screen, taken by FFmpeg; null if it failed.</summary>
    private static byte[]? X11Screenshot(Rectangle area, bool showCursor)
    {
        if (FfmpegLocator.Find() is not { } ffmpeg) return null;
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-f", "x11grab",
                     "-video_size", $"{area.Width}x{area.Height}", "-draw_mouse", showCursor ? "1" : "0",
                     "-i", string.Create(CultureInfo.InvariantCulture, $"{display}+{area.X},{area.Y}"),
                     "-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "-"])
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEndAsync();
            using var png = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(png);
            p.WaitForExit(5000);
            return p.ExitCode == 0 && png.Length > 0 ? png.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A PNG of the whole desktop from the Screenshot portal (Wayland); null if refused or unavailable.</summary>
    private static async Task<byte[]?> PortalScreenshotAsync()
    {
        try
        {
            using var portal = await Portal.ConnectAsync();
            var shot = portal.Proxy<IScreenshot>();
            var results = await portal.RequestAsync(token => shot.ScreenshotAsync("", new Dictionary<string, object>
            {
                ["handle_token"] = token,
                ["interactive"] = false,
            }));
            if (!results.TryGetValue("uri", out var uri) || uri is not string s) return null;
            var path = new Uri(s).LocalPath;
            var png = await File.ReadAllBytesAsync(path);
            // The desktop saved it for us only: do not leave it behind.
            try { File.Delete(path); } catch { }
            return png;
        }
        catch
        {
            return null;
        }
    }

    public IDisposable ShowRecordingBorder(Rectangle region) => new RecordingBorder(region);

    // No Linux display server lets a window hide itself from screen captures.
    public void ExcludeFromCapture(IntPtr windowHandle) { }

    public IDisposable? RegisterStartStopHotkey(Action pressed) =>
        LinuxCapture.IsWayland ? null : X11Hotkey.TryRegister(pressed);

    public void RevealInFileManager(string filePath) => _ = RevealAsync(filePath);

    /// <summary>Asks the file manager to show the file; opens its folder if no file manager answers.</summary>
    private static async Task RevealAsync(string filePath)
    {
        try
        {
            using var connection = new Connection(Address.Session);
            await connection.ConnectAsync();
            var fm = connection.CreateProxy<IFileManager1>("org.freedesktop.FileManager1", new ObjectPath("/org/freedesktop/FileManager1"));
            await fm.ShowItemsAsync([new Uri(filePath).AbsoluteUri], "");
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(filePath)!]) { UseShellExecute = false }); }
            catch { /* no desktop integration */ }
        }
    }
}
