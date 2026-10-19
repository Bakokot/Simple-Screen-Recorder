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

namespace ScreenCapture;

/// <summary>An H.264 encoder and its FFmpeg options, for 32-bit RGB frames.</summary>
/// <param name="InputArguments">Options placed before the input (the hardware device).</param>
internal sealed record LinuxEncoder(string Name, IReadOnlyList<string> InputArguments, IReadOnlyList<string> Arguments);

/// <summary>
/// Picks the encoders to try, best first: NVENC on NVIDIA GPUs, VAAPI on AMD and Intel GPUs, then x264.
/// Hardware encoders are probed once with a tiny encode, so missing drivers cost nothing when recording.
/// </summary>
internal static class LinuxEncoders
{
    private const string RenderNode = "/dev/dri/renderD128";

    // Frames are converted to limited-range BT.601 YUV (by the GPU or by swscale) but come from sRGB screens:
    // tag the video with what it really is so players show the right colors.
    private static readonly string[] ColorTags =
        ["-color_range", "tv", "-colorspace", "smpte170m", "-color_primaries", "bt709", "-color_trc", "bt709"];

    private static readonly Dictionary<string, bool> Probed = [];

    public static List<LinuxEncoder> For(RecordingOptions o, Size frame)
    {
        var inv = CultureInfo.InvariantCulture;
        bool lossless = o.Quality == VideoQuality.Lossless;
        // WebM / GIF are transcoded after recording: keep a high-quality intermediate.
        int cq = FormatInfo.IsH264(o.Format) ? FormatInfo.NvencCq(o.Quality) : 12;
        var list = new List<LinuxEncoder>();

        // NVENC refuses frames smaller than about 145 × 49.
        if (frame.Width >= 160 && frame.Height >= 96)
        {
            var nvenc = new List<string> { "-c:v", "h264_nvenc" };
            if (lossless) nvenc.AddRange(["-preset", "p4", "-tune", "lossless", "-rgb_mode", "yuv444"]);
            else nvenc.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(inv), "-b:v", "0"]);
            // No B-frames and no output delay: the progress reports then tell when capture began.
            nvenc.AddRange(["-bf", "0", "-delay", "0"]);
            nvenc.AddRange(ColorTags);
            Add(list, o.FfmpegPath, new LinuxEncoder("NVENC", [], nvenc));
        }

        // VAAPI has no lossless mode.
        if (!lossless && File.Exists(RenderNode))
        {
            Add(list, o.FfmpegPath, new LinuxEncoder("VAAPI", ["-vaapi_device", RenderNode],
                ["-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", cq.ToString(inv), "-bf", "0", .. ColorTags]));
        }

        var x264 = EncoderArguments.X264(o, new Rectangle(Point.Empty, frame));
        x264.AddRange(["-tune", "zerolatency"]);
        if (!lossless) x264.AddRange(ColorTags);
        list.Add(new LinuxEncoder("x264", [], x264));
        return list;
    }

    private static void Add(List<LinuxEncoder> list, string ffmpeg, LinuxEncoder encoder)
    {
        bool works;
        lock (Probed)
        {
            // Keyed by options: lossless NVENC may be missing where the normal mode works.
            var key = string.Join(" ", [.. encoder.InputArguments, .. encoder.Arguments]);
            if (!Probed.TryGetValue(key, out works))
                Probed[key] = works = Probe(ffmpeg, encoder);
        }
        if (works) list.Add(encoder);
    }

    /// <summary>Encodes a few blank frames, the way a recording would.</summary>
    private static bool Probe(string ffmpeg, LinuxEncoder encoder)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", .. encoder.InputArguments,
                     "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30,format=bgr0", "-frames:v", "3",
                     .. encoder.Arguments, "-f", "null", "-"])
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                p.Kill();
                return false;
            }
            Debug.WriteLineIf(p.ExitCode != 0, $"{encoder.Name} is not available: {error.Result}");
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
