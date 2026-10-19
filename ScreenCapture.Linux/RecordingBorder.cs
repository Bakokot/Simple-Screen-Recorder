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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCapture;

/// <summary>
/// Red frame drawn just outside the zone being recorded, made of four thin windows: nothing covers the zone
/// itself, so clicks reach the apps below and the frame never appears in the video.
/// </summary>
internal sealed class RecordingBorder : IDisposable
{
    private const int Thickness = 3;

    private readonly List<Window> _strips = [];

    public RecordingBorder(Rectangle region)
    {
        var outer = Rectangle.Inflate(region, Thickness, Thickness);
        Rectangle[] strips =
        [
            new(outer.X, outer.Y, outer.Width, Thickness),
            new(outer.X, region.Bottom, outer.Width, Thickness),
            new(outer.X, region.Y, Thickness, region.Height),
            new(region.Right, region.Y, Thickness, region.Height),
        ];
        foreach (var s in strips)
        {
            var strip = new Window
            {
                SystemDecorations = SystemDecorations.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                CanResize = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Focusable = false,
                MinWidth = 0,
                MinHeight = 0,
                Background = Brushes.Red,
                Position = new PixelPoint(s.X, s.Y),
            };
            var scale = strip.DesktopScaling > 0 ? strip.DesktopScaling : 1;
            strip.Width = s.Width / scale;
            strip.Height = s.Height / scale;
            strip.Show();
            _strips.Add(strip);
        }
    }

    public void Dispose()
    {
        foreach (var s in _strips) s.Close();
        _strips.Clear();
    }
}
