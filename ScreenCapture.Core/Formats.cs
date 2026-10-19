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

namespace ScreenCapture;

public enum OutputFormat { Mp4, Mkv, Mov, WebM, Gif }

public enum VideoQuality { Low, Medium, High, Lossless }

public static class FormatInfo
{
    public static string DisplayName(OutputFormat f) => f switch
    {
        OutputFormat.Mp4 => "MP4 (H.264 + AAC)",
        OutputFormat.Mkv => "MKV (H.264 + AAC)",
        OutputFormat.Mov => "MOV (H.264 + AAC)",
        OutputFormat.WebM => "WebM (VP9 + Opus)",
        OutputFormat.Gif => "GIF (no sound)",
        _ => f.ToString(),
    };

    public static string DisplayName(VideoQuality q) => q switch
    {
        VideoQuality.Low => "Low (small file)",
        VideoQuality.Medium => "Medium",
        VideoQuality.High => "High",
        VideoQuality.Lossless => "Lossless (very large file)",
        _ => q.ToString(),
    };

    public static string Extension(OutputFormat f) => f switch
    {
        OutputFormat.Mp4 => ".mp4",
        OutputFormat.Mkv => ".mkv",
        OutputFormat.Mov => ".mov",
        OutputFormat.WebM => ".webm",
        OutputFormat.Gif => ".gif",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static bool SupportsAudio(OutputFormat f) => f != OutputFormat.Gif;

    /// <summary>H.264 formats are encoded once, in real time; the others are transcoded after recording.</summary>
    public static bool IsH264(OutputFormat f) => f is OutputFormat.Mp4 or OutputFormat.Mkv or OutputFormat.Mov;

    public static int X264Crf(VideoQuality q) => q switch
    {
        VideoQuality.Low => 30,
        VideoQuality.Medium => 24,
        VideoQuality.High => 18,
        _ => 0,
    };

    /// <summary>NVENC constant-quality level, tuned to look like the matching x264 CRF.</summary>
    public static int NvencCq(VideoQuality q) => q switch
    {
        VideoQuality.Low => 31,
        VideoQuality.Medium => 25,
        _ => 19,
    };

    public static int Vp9Crf(VideoQuality q) => q switch
    {
        VideoQuality.Low => 40,
        VideoQuality.Medium => 33,
        _ => 24,
    };

    public static string AudioBitrate(VideoQuality q) => q switch
    {
        VideoQuality.Low => "96k",
        VideoQuality.Medium => "128k",
        VideoQuality.High => "192k",
        _ => "320k",
    };

    public static int GifFps(VideoQuality q, int fps) => Math.Min(fps, q switch
    {
        VideoQuality.Low => 10,
        VideoQuality.Medium => 12,
        VideoQuality.High => 15,
        _ => 25,
    });
}
