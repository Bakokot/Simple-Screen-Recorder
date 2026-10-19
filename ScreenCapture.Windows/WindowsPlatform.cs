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
using System.Drawing.Imaging;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace ScreenCapture;

/// <summary>Windows 10 (2004+) / 11: DXGI and GDI capture, WASAPI sound, WinForms overlays.</summary>
public sealed class WindowsPlatform : IPlatform, IDisposable
{
    private readonly MMDeviceEnumerator _devices = new();

    public string FfmpegInstallHint =>
        "Install it with:\n\n    winget install Gyan.FFmpeg\n\nor copy ffmpeg.exe next to ScreenCapture.exe.";

    // ---------------------------------------------------------------- sound

    public IReadOnlyList<AudioDevice> OutputDevices() => Devices(DataFlow.Render);

    public IReadOnlyList<AudioDevice> InputDevices() => Devices(DataFlow.Capture);

    private List<AudioDevice> Devices(DataFlow flow)
    {
        try
        {
            return _devices.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch
        {
            return []; // no audio subsystem
        }
    }

    public bool SupportsAppAudio => true;

    public IReadOnlyList<AudioApp> AppsWithSound() => AudioApps.WithAudioSessions(_devices);

    public AudioApp? FindRunningApp(string exe) =>
        AudioApps.FindRunningPath(exe) is { } path ? new AudioApp(exe, AudioApps.DisplayName(path, exe), path) : null;

    public IReadOnlyList<int> RootProcessIds(string exe) => AudioApps.RootProcessIds(exe);

    public byte[]? AppIconPng(AudioApp app)
    {
        if (app.ExePath == null) return null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(app.ExePath);
            if (icon == null) return null;
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public IAudioTrack CreateAudioTrack(AudioSource source, string filePath) => source switch
    {
        SystemSound s => new AudioRecorder(Device(s.DeviceId, DataFlow.Render), loopback: true, filePath),
        AppSound a => new AppAudioRecorder(a.ProcessId, filePath),
        Microphone m => new AudioRecorder(Device(m.DeviceId, DataFlow.Capture), loopback: false, filePath),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private MMDevice Device(string? id, DataFlow flow) =>
        id != null ? _devices.GetDevice(id) : _devices.GetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Communications);

    // ---------------------------------------------------------------- video

    public Task<IReadOnlyList<VideoPipeline>> VideoPipelinesAsync(RecordingOptions options, Rectangle region) =>
        Task.FromResult<IReadOnlyList<VideoPipeline>>(DdagrabCapture.Pipelines(options, region).ToList());

    public Task<byte[]?> ScreenshotPngAsync(Rectangle region, bool showCursor) => Task.Run(() => (byte[]?)ScreenshotPng(region, showCursor));

    /// <summary>One GDI frame, as for recordings: works for any zone, even spanning several screens.</summary>
    private static unsafe byte[] ScreenshotPng(Rectangle region, bool showCursor)
    {
        using var grabber = new GdiFrameGrabber(region, showCursor);
        var pixels = grabber.Grab();
        fixed (byte* bits = pixels)
        {
            // 32bppRgb: BitBlt leaves the alpha bytes undefined.
            using var bitmap = new Bitmap(region.Width, region.Height, region.Width * 4, PixelFormat.Format32bppRgb, (IntPtr)bits);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
    }

    // ---------------------------------------------------------------- desktop integration

    public Task<Rectangle?> SelectRegionAsync(Rectangle? initial)
    {
        using var selector = new RegionSelectorForm(initial);
        return Task.FromResult(selector.ShowDialog() == Forms.DialogResult.OK ? selector.SelectedRegion : (Rectangle?)null);
    }

    public IDisposable ShowRecordingBorder(Rectangle region)
    {
        var border = new RecordingBorderForm(region);
        border.Show();
        return border;
    }

    public void ExcludeFromCapture(IntPtr windowHandle) =>
        NativeMethods.SetWindowDisplayAffinity(windowHandle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

    public IDisposable? RegisterStartStopHotkey(Action pressed) =>
        GlobalHotkey.TryRegister(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_F9, pressed);

    public void RevealInFileManager(string filePath) => Process.Start("explorer.exe", $"/select,\"{filePath}\"");

    public void Dispose() => _devices.Dispose();
}
