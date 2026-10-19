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

namespace ScreenCapture;

/// <summary>Everything the recorder and the interface need from the operating system.</summary>
public interface IPlatform
{
    /// <summary>How to get FFmpeg, shown when it cannot be found.</summary>
    string FfmpegInstallHint { get; }

    // ---------------------------------------------------------------- sound

    IReadOnlyList<AudioDevice> OutputDevices();
    IReadOnlyList<AudioDevice> InputDevices();
    /// <summary>Whether the sound of chosen apps can be recorded on its own ("Only selected apps").</summary>
    bool SupportsAppAudio { get; }
    /// <summary>Apps that currently play sound, for "Only selected apps".</summary>
    IReadOnlyList<AudioApp> AppsWithSound();
    /// <summary>A running copy of the app, or null if it is not running.</summary>
    AudioApp? FindRunningApp(string exe);
    /// <summary>Top-level processes of an app; each is recorded with its child processes.</summary>
    IReadOnlyList<int> RootProcessIds(string exe);
    /// <summary>The app's icon as a PNG image, or null.</summary>
    byte[]? AppIconPng(AudioApp app);
    IAudioTrack CreateAudioTrack(AudioSource source, string filePath);

    // ---------------------------------------------------------------- video

    /// <summary>Ways to capture <paramref name="region"/>, best first; the recorder uses the first one that starts.</summary>
    /// <remarks>May ask the user for permission to capture the screen (Wayland).</remarks>
    Task<IReadOnlyList<VideoPipeline>> VideoPipelinesAsync(RecordingOptions options, Rectangle region);

    // ---------------------------------------------------------------- desktop integration

    /// <summary>Lets the user draw the zone to record over the screen; null if cancelled.</summary>
    Task<Rectangle?> SelectRegionAsync(Rectangle? initial);
    /// <summary>Shows a frame around the zone being recorded until disposed. It must not appear in the video.</summary>
    IDisposable ShowRecordingBorder(Rectangle region);
    /// <summary>Keeps a window out of screen captures, so the app never records itself.</summary>
    void ExcludeFromCapture(IntPtr windowHandle);
    /// <summary>Registers Ctrl + Shift + F9 system-wide; null if it is not available.</summary>
    /// <remarks><paramref name="pressed"/> is called on a background thread.</remarks>
    IDisposable? RegisterStartStopHotkey(Action pressed);
    /// <summary>Opens the file manager with the file selected.</summary>
    void RevealInFileManager(string filePath);
}

/// <summary>A sound input or output device; a null id means the system default.</summary>
public sealed record AudioDevice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An application identified by its executable name (e.g. "spotify"), which survives restarts.</summary>
public sealed record AudioApp(string Exe, string Name, string? ExePath);

/// <summary>A sound to record alongside the video.</summary>
public abstract record AudioSource;

/// <summary>Everything an output device plays (null: the default device).</summary>
public sealed record SystemSound(string? DeviceId) : AudioSource;

/// <summary>The sound of one process and its child processes.</summary>
public sealed record AppSound(int ProcessId) : AudioSource;

/// <summary>A microphone (null: the default device).</summary>
public sealed record Microphone(string? DeviceId) : AudioSource;

/// <summary>One way to capture and encode the video.</summary>
public abstract record VideoPipeline(string Name)
{
    /// <summary>Released when the recording ends, whichever pipeline was used; pipelines may share it.</summary>
    public IDisposable? Resources { get; init; }
}

/// <summary>
/// FFmpeg grabs the screen itself. <see cref="Arguments"/> are the input, filter and encoder options; the
/// recorder adds the logging, progress and output options.
/// </summary>
/// <remarks>
/// The encoder must not delay its output (no B-frames, no lookahead): the progress reports then tell when
/// capture began, which keeps the sound in sync.
/// </remarks>
public sealed record FfmpegGrabPipeline(string Name, IReadOnlyList<string> Arguments) : VideoPipeline(Name)
{
    /// <summary>Called when FFmpeg could not start capturing with this pipeline.</summary>
    public Action? OnFailed { get; init; }
}

/// <summary>The app grabs the frames and pipes them to FFmpeg as raw 32-bit pixels.</summary>
/// <param name="CreateGrabber">Called on the capture thread, which then uses and disposes the grabber.</param>
public sealed record FrameGrabberPipeline(string Name, Func<IFrameGrabber> CreateGrabber) : VideoPipeline(Name)
{
    /// <summary>FFmpeg name of the 4-byte pixel format of the frames.</summary>
    public string PixelFormat { get; init; } = "bgra";
    /// <summary>Size of the frames, when it differs from the zone (screens scaled on Wayland). Must be even.</summary>
    public Size? FrameSize { get; init; }
    /// <summary>Options placed before the input (e.g. a hardware device for the encoder).</summary>
    public IReadOnlyList<string> InputArguments { get; init; } = [];
    /// <summary>Encoder options; libx264 when null.</summary>
    public IReadOnlyList<string>? Encoder { get; init; }
    /// <summary>Called when the pipeline could not start.</summary>
    public Action? OnFailed { get; init; }
}

public interface IFrameGrabber : IDisposable
{
    /// <summary>Captures one frame: top-down rows of the zone, valid until the next call.</summary>
    /// <remarks>
    /// May wait for the first frame (and must throw if none comes within a few seconds); afterwards it returns
    /// the latest one without waiting.
    /// </remarks>
    ReadOnlySpan<byte> Grab();
}
