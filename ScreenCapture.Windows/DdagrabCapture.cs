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

/// <summary>
/// Capture pipelines for Windows. Video is preferably captured by FFmpeg itself with Desktop Duplication
/// (ddagrab) and encoded on the GPU with NVENC: frames never leave video memory, so a game keeps its frame
/// rate. When that is not possible (zone spanning several screens, rotated screen, no NVIDIA GPU…) it falls
/// back to software encoding, and as a last resort to GDI capture piped to FFmpeg.
/// </summary>
/// <remarks>
/// On an HDR screen, Desktop Duplication and GDI hand out SDR content without scaling it down from the "SDR
/// content brightness" level, so videos and screenshots come out brighter and clipped. Windows.Graphics.Capture
/// (gfxcapture) does that scaling, so it is used there instead.
/// </remarks>
internal static class DdagrabCapture
{
    /// <summary>Adapters on which NVENC could not encode the captured frames (e.g. laptop iGPU screens).</summary>
    private static readonly HashSet<int> NvencFailed = [];

    // ddagrab tags its frames as full-range RGB and encoders copy that into the file, but they convert to
    // limited-range BT.601 YUV; tag the video with what it really is so players show the right colors.
    private const string ColorTags = "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709";

    public static IEnumerable<VideoPipeline> Pipelines(RecordingOptions o, Rectangle region)
    {
        var output = DxgiOutputs.Containing(region);
        if (output != null)
        {
            bool nvenc;
            lock (NvencFailed) nvenc = !NvencFailed.Contains(output.AdapterIndex);
            // NVENC refuses frames smaller than about 145 × 49.
            nvenc &= region.Width >= 160 && region.Height >= 96;

            // FFmpeg before 8.0 has no gfxcapture: these fail at once and ddagrab takes over (too bright, but works).
            if (output.Hdr)
            {
                var gfx = GraphicsCapture(o.Fps, o.ShowCursor, region, output);
                if (nvenc) yield return new FfmpegGrabPipeline("HDR screen capture + NVENC", Arguments(o, region, output, gfx, nvenc: true));
                yield return new FfmpegGrabPipeline("HDR screen capture + x264", Arguments(o, region, output, gfx, nvenc: false));
            }

            var grab = string.Create(CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={output.OutputIndex}:framerate={o.Fps}:video_size={region.Width}x{region.Height}" +
                $":offset_x={region.X - output.Bounds.X}:offset_y={region.Y - output.Bounds.Y}:draw_mouse={(o.ShowCursor ? 1 : 0)}");
            if (nvenc)
            {
                yield return new FfmpegGrabPipeline("GPU capture + NVENC", Arguments(o, region, output, grab, nvenc: true))
                {
                    OnFailed = () => { lock (NvencFailed) NvencFailed.Add(output.AdapterIndex); },
                };
            }
            yield return new FfmpegGrabPipeline("GPU capture + x264", Arguments(o, region, output, grab, nvenc: false));
        }
        yield return new FrameGrabberPipeline("GDI capture", () => new GdiFrameGrabber(region, o.ShowCursor));
    }

    /// <summary>
    /// A PNG of <paramref name="region"/> taken with Windows.Graphics.Capture when it lies on an HDR screen, where
    /// GDI would get the colors wrong; null otherwise or if it failed.
    /// </summary>
    public static byte[]? HdrScreenshotPng(Rectangle region, bool showCursor)
    {
        if (DxgiOutputs.Containing(region) is not { Hdr: true } output || FfmpegLocator.Find() is not { } ffmpeg) return null;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in DeviceArguments(output))
            psi.ArgumentList.Add(a);
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error",
                     "-filter_complex", $"{GraphicsCapture(1, showCursor, region, output)},hwdownload,format=bgra",
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

    /// <summary>gfxcapture filter grabbing <paramref name="region"/> of the output's monitor.</summary>
    private static string GraphicsCapture(int fps, bool showCursor, Rectangle region, DxgiOutput output) =>
        string.Create(CultureInfo.InvariantCulture,
            $"gfxcapture=hmonitor={(long)output.Monitor}:max_framerate={fps}:capture_cursor={(showCursor ? 1 : 0)}" +
            $":crop_left={region.X - output.Bounds.X}:crop_top={region.Y - output.Bounds.Y}" +
            $":crop_right={output.Bounds.Right - region.Right}:crop_bottom={output.Bounds.Bottom - region.Bottom}");

    /// <summary>
    /// ddagrab and gfxcapture use the default adapter unless given a device: the screen may be plugged into
    /// another GPU.
    /// </summary>
    private static string[] DeviceArguments(DxgiOutput output) => output.AdapterIndex != 0
        ? ["-init_hw_device", $"d3d11va=dx:{output.AdapterIndex}", "-filter_hw_device", "dx"]
        : [];

    private static List<string> Arguments(RecordingOptions o, Rectangle region, DxgiOutput output, string grab, bool nvenc)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>(DeviceArguments(output));
        bool lossless = o.Quality == VideoQuality.Lossless;

        if (nvenc)
        {
            args.AddRange(["-filter_complex", $"{grab},{ColorTags}", "-c:v", "h264_nvenc"]);
            if (lossless)
                args.AddRange(["-preset", "p4", "-tune", "lossless", "-rgb_mode", "yuv444"]);
            else
            {
                // WebM / GIF are transcoded after recording: keep a high-quality intermediate.
                var cq = FormatInfo.IsH264(o.Format) ? FormatInfo.NvencCq(o.Quality) : 12;
                args.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(inv), "-b:v", "0"]);
            }
            // No B-frames and no output delay: the progress reports then tell when capture began.
            args.AddRange(["-bf", "0", "-delay", "0"]);
        }
        else
        {
            args.AddRange(["-filter_complex", $"{grab},hwdownload,format=bgra,setparams=color_primaries=bt709:color_trc=bt709"]);
            args.AddRange(EncoderArguments.X264(o, region));
            if (!lossless) args.AddRange(["-colorspace", "smpte170m"]);
            args.AddRange(["-tune", "zerolatency"]);
        }
        return args;
    }
}
