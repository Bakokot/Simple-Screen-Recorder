# Screen Capture

A lightweight, no-nonsense screen recorder for Windows 10 / 11, with a Linux version in progress.
Pick a region or a whole screen, choose which sounds to record, hit the big red button.
Built to stay out of the way: frames are captured and encoded on the GPU, so recording a game
barely touches its frame rate.

> 🤖 **This app was made with AI.** Its code was written by [Claude](https://www.anthropic.com/claude)
> (Anthropic), working in [Claude Code](https://claude.com/claude-code), from a human's requests,
> testing and feedback. See [About this project](#about-this-project).

## Features

**Capture**
- Record a **region** (drawn over a frozen screenshot: drag, move, resize, Enter to confirm) or a **whole screen**, on any monitor.
- Optional **mouse cursor**, and an optional red frame showing the recorded zone (never visible in the video).
- **10 to 60 fps.**
- Global shortcut: **Ctrl + Shift + F9** to start and stop, even while the app is in the background.

**Performance**
- GPU capture with Desktop Duplication (FFmpeg `ddagrab`) and **NVENC hardware encoding** on NVIDIA GPUs:
  frames never leave video memory, so games keep their frame rate.
- Automatic fallbacks: GPU capture + x264 software encoding, then classic GDI capture
  (e.g. for a zone spanning several screens).
- Encoder processes run at below-normal priority, so the app in the foreground always wins.

**Sound**
- **System sound** from any output device, or **only from the apps you choose**
  (e.g. just the game and Discord, not your music), using WASAPI per-process loopback.
- **Microphone**, mixed with the system sound.
- Audio kept in sync with the video, even when the apps are silent.

**Output**
- **MP4, MKV, MOV** (H.264 + AAC), **WebM** (VP9 + Opus) or **GIF**.
- Quality presets from *Low* (small files) to *Lossless* (true 4:4:4).
- Correct colors: videos are tagged with the color space they are really encoded in.

**Interface**
- Fluent design (Windows 11 style) that follows the system light / dark theme.
- Responsive window: the interface zooms with the window and switches to two columns when it's wide,
  from a small side window up to a maximized 4K screen.
- All settings, and the window size, are remembered between sessions.

## Requirements

### Windows

- **Windows 10 version 2004** or later (needed for per-app sound), or Windows 11.
- [**.NET 9 Desktop Runtime**](https://dotnet.microsoft.com/download/dotnet/9.0).
- [**FFmpeg**](https://ffmpeg.org/) **6.0 or later**, which does the encoding. The easiest way to install it:
  ```
  winget install Gyan.FFmpeg
  ```
  You can also put `ffmpeg.exe` next to `ScreenCapture.exe`. The app finds FFmpeg there, on the `PATH`,
  or in the winget install folder.
- Optional: an **NVIDIA GPU** for hardware encoding. Other GPUs work too, with software encoding.

### Linux (preview)

- An **X11** or **Wayland** desktop, with PulseAudio or PipeWire for sound.
- **FFmpeg 6.0 or later** (`sudo apt install ffmpeg` on Ubuntu 24.04 / Debian 13; older releases ship an older FFmpeg).
- `pactl` to list the sound devices (`pulseaudio-utils`; without it only the default devices are offered).
- On **Wayland**: GStreamer with its PipeWire plugin (`sudo apt install gstreamer1.0-tools gstreamer1.0-pipewire`)
  and a desktop portal with screen sharing (included with GNOME and KDE Plasma).
- Optional: hardware encoding with **NVENC** (NVIDIA) or **VAAPI** (AMD, Intel); x264 otherwise.

What is different on Linux:
- On Wayland, the desktop asks once which screens the app may record. A zone must fit within one screen.
- On Wayland there is no global shortcut yet; use the record button.
- "Only selected apps" sound is not available yet; system sound and microphone are.
- The app window cannot hide itself from the recording.

## Build and run

```
git clone https://github.com/<your-account>/screen_video_capture.git
cd screen_video_capture
dotnet build -c Release
```

Then run `ScreenCapture/bin/Release/net9.0-windows/ScreenCapture.exe`.
Building needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

For Linux, build a self-contained app (no .NET needed on the target machine):

```
dotnet publish ScreenCapture -f net9.0 -r linux-x64 --self-contained -c Release -o out/linux
./out/linux/ScreenCapture
```

The code is split by operating system:

| Project | Contents |
|---|---|
| `ScreenCapture.Core` | Everything that does not depend on the operating system: settings, formats, FFmpeg recording and muxing, and the `IPlatform` interface |
| `ScreenCapture.Windows` | The Windows implementation of `IPlatform`: capture, sound, region selector, red frame, global shortcut |
| `ScreenCapture.Linux` | The Linux implementation of `IPlatform` |
| `ScreenCapture` | The interface (Avalonia), built for Windows (`net9.0-windows`) and for Linux (`net9.0`) |

## How to use

1. Click **Region** to draw a zone, or **Screen** to pick a monitor.
2. Choose the format, quality and frame rate, and where to save the videos.
3. Choose the sound: system sound (all apps or only selected apps) and / or microphone.
4. Click the red button, or press **Ctrl + Shift + F9**. Do the same again to stop.
   The video is saved in the chosen folder as `Capture_<date>_<time>.<ext>`.

**Recording games:** turn off the red frame. Any window drawn over a game, even a click-through one,
can keep it from presenting directly to the screen and lower its frame rate.

## How it works

| Part | Windows | Linux |
|---|---|---|
| Video capture | FFmpeg `ddagrab` (DXGI Desktop Duplication), GDI `BitBlt` as a fallback | X11: FFmpeg `x11grab`. Wayland: ScreenCast portal → PipeWire, read by GStreamer |
| Video encoding | FFmpeg: `h264_nvenc` on NVIDIA GPUs, `libx264` otherwise | FFmpeg: `h264_nvenc`, `h264_vaapi`, or `libx264` |
| Sound | WASAPI loopback, per-process loopback and capture, through [NAudio](https://github.com/naudio/NAudio) | FFmpeg's PulseAudio input (also works with PipeWire) |
| Final file | FFmpeg muxes video and sound, or converts to WebM / GIF | Same |
| Interface | [Avalonia](https://avaloniaui.net/) with its Fluent theme and [Fluent UI System Icons](https://github.com/davidxuang/FluentIcons); the region selector and red frame are WinForms windows | Same interface; Avalonia region selector and red frame |

Settings are stored in `%APPDATA%\ScreenCapture\settings.json` on Windows and `~/.config/ScreenCapture/settings.json` on Linux.

## About this project

This app was **made with AI**. Its source code was written by Claude, Anthropic's AI model, through
the Claude Code coding assistant. A human described what the app should do, tested it on real hardware
and reported problems, and the AI designed, wrote and fixed the code.

The code has been built and tested, but it has not been reviewed line by line by a human developer.
It may contain bugs. Bug reports and pull requests are welcome.

## License

Copyright (C) 2026 Bakou

This program is free software: you can redistribute it and/or modify it under the terms of the
**GNU General Public License** as published by the Free Software Foundation, either **version 3** of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**; without even
the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public
License for more details.

You should have received a copy of the GNU General Public License along with this program, in the
[`LICENSE`](LICENSE) file. If not, see <https://www.gnu.org/licenses/>.

### Third-party software

| Component | License | How it is used |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | MIT | NuGet package, included in the build |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | NuGet package, included in the build |
| [FluentIcons](https://github.com/davidxuang/FluentIcons) | MIT | NuGet package, included in the build (icons from Microsoft's Fluent UI System Icons, MIT) |
| [Tmds.DBus](https://github.com/tmds/Tmds.DBus) | MIT | NuGet package, included in the Linux build |
| [GStreamer](https://gstreamer.freedesktop.org/) | LGPL 2.1+ | **Not included.** Installed separately on Linux (Wayland) and run as a separate program |
| [FFmpeg](https://ffmpeg.org/) | LGPL 2.1+ / GPL 2+, depending on the build | **Not included.** Installed separately by the user and run as a separate program |

FFmpeg is a trademark of Fabrice Bellard. NVIDIA and NVENC are trademarks of NVIDIA Corporation.
Windows is a trademark of Microsoft Corporation. This project is not affiliated with any of them.
