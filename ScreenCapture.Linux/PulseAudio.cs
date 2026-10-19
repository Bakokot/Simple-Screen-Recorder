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
using System.Globalization;
using System.Text;

namespace ScreenCapture;

/// <summary>
/// Sound devices of PulseAudio, or of PipeWire through its PulseAudio server (the default on current desktops).
/// What an output device plays is recorded from its "monitor" source.
/// </summary>
internal static class PulseAudio
{
    /// <summary>Source names that PulseAudio resolves to the current default devices.</summary>
    public const string DefaultMonitor = "@DEFAULT_MONITOR@", DefaultSource = "@DEFAULT_SOURCE@";

    public const string MonitorSuffix = ".monitor";

    public static List<AudioDevice> Outputs() => List("sinks");

    public static List<AudioDevice> Inputs() => List("sources").Where(d => !d.Id!.EndsWith(MonitorSuffix, StringComparison.Ordinal)).ToList();

    /// <summary>Parses "pactl list sinks|sources": blocks of "Name: …" and "Description: …" lines.</summary>
    private static List<AudioDevice> List(string kind)
    {
        var devices = new List<AudioDevice>();
        try
        {
            var psi = new ProcessStartInfo("pactl", ["list", kind])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Field names are translated otherwise.
            psi.Environment["LC_ALL"] = "C";
            using var p = Process.Start(psi)!;
            string? name = null;
            while (p.StandardOutput.ReadLine() is { } line)
            {
                var l = line.Trim();
                if (l.StartsWith("Name: ", StringComparison.Ordinal)) name = l[6..];
                else if (l.StartsWith("Description: ", StringComparison.Ordinal) && name != null)
                {
                    devices.Add(new AudioDevice(name, l[13..]));
                    name = null;
                }
            }
            p.WaitForExit(5000);
        }
        catch { /* no pactl: only the default devices */ }
        return devices;
    }
}

/// <summary>Records a PulseAudio source to a WAV file with FFmpeg.</summary>
/// <remarks>
/// PulseAudio sources deliver silence when nothing plays, so the track stays as long as the video. Its start
/// time comes from FFmpeg's progress reports, like the video's: "now − recorded duration".
/// </remarks>
internal sealed class FfmpegAudioTrack(string ffmpeg, string source, string filePath) : IAudioTrack
{
    private readonly StringBuilder _log = new();
    private Process? _ffmpeg;
    private Stopwatch? _clock;
    private long _outTimeUs;
    private volatile bool _hasData;

    public string FilePath { get; } = filePath;
    public TimeSpan StartedAt { get; private set; }
    public bool HasData => _hasData;
    public Exception? Error { get; private set; }

    public void Start(Stopwatch clock)
    {
        _clock = clock;
        StartedAt = clock.Elapsed;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-nostats", "-y",
                     "-f", "pulse", "-i", source, "-c:a", "pcm_s16le",
                     "-progress", "pipe:1", "-stats_period", "0.1", FilePath])
            psi.ArgumentList.Add(a);

        _ffmpeg = new Process { StartInfo = psi };
        _ffmpeg.OutputDataReceived += (_, e) => OnProgress(e.Data);
        _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.AppendLine(e.Data); };
        _ffmpeg.Start();
        _ffmpeg.BeginOutputReadLine();
        _ffmpeg.BeginErrorReadLine();
    }

    private void OnProgress(string? line)
    {
        if (line == null) return;
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out _outTimeUs);
        else if (line.StartsWith("progress=", StringComparison.Ordinal) && _outTimeUs > 0 && !_hasData)
        {
            var start = _clock!.Elapsed - TimeSpan.FromMicroseconds(_outTimeUs);
            if (start > StartedAt) StartedAt = start;
            _hasData = true;
        }
    }

    public async Task StopAsync()
    {
        if (_ffmpeg == null) return;
        try
        {
            // "q" makes FFmpeg stop and finish the file cleanly.
            await _ffmpeg.StandardInput.WriteAsync('q');
            await _ffmpeg.StandardInput.FlushAsync();
        }
        catch (IOException) { /* already gone; reported below */ }
        await _ffmpeg.WaitForExitAsync();
        if (_ffmpeg.ExitCode != 0)
        {
            lock (_log) Error = new InvalidOperationException($"Recording sound from {source} failed.\n\n{_log.ToString().Trim()}");
        }
    }

    public void Dispose()
    {
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(); } catch { }
        _ffmpeg?.Dispose();
    }
}
