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

using System.Text.Json;

namespace ScreenCapture;

internal sealed class AppSettings
{
    public OutputFormat Format { get; set; } = OutputFormat.Mp4;
    public VideoQuality Quality { get; set; } = VideoQuality.High;
    public int Fps { get; set; } = 30;
    public bool ShowCursor { get; set; } = true;
    public bool ShowRecordingBorder { get; set; } = true;
    public bool CaptureSystemAudio { get; set; } = true;
    public string? SystemAudioDeviceId { get; set; }
    /// <summary>Record only the apps listed in <see cref="SystemAudioApps"/> instead of everything the device plays.</summary>
    public bool SystemAudioAppsOnly { get; set; }
    /// <summary>Executable names (without ".exe") of the apps whose sound is recorded.</summary>
    public List<string> SystemAudioApps { get; set; } = [];
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Captures");
    /// <summary>Window size as [width, height] once resized by the user; null while it fits its content.</summary>
    public double[]? WindowSize { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Last capture zone as [x, y, width, height] in screen pixels.</summary>
    public int[]? Region { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCapture", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience; never fail the app over them */ }
    }
}
