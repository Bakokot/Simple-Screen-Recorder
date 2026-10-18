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

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenCapture;

/// <summary>A row of the "Only selected apps" list.</summary>
internal sealed class AudioAppEntry
{
    public required string Exe { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public bool Running { get; init; }
    public bool Selected { get; set; }
    public string Status => Running ? "" : "not running";

    public static AudioAppEntry From(AudioApp app, bool running, bool selected) => new()
    {
        Exe = app.Exe,
        Name = app.Name,
        Icon = app.ExePath != null ? LoadIcon(app.ExePath) : null,
        Running = running,
        Selected = selected,
    };

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
