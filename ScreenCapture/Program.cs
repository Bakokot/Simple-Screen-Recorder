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

namespace ScreenCapture;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CreatePlatform() is not { } platform)
        {
            Console.Error.WriteLine("Screen Capture does not support this operating system yet.");
            return 1;
        }
        App.Platform = platform;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            (platform as IDisposable)?.Dispose();
        }
    }

    private static IPlatform? CreatePlatform()
    {
#if WINDOWS
        return new WindowsPlatform();
#else
        return null;
#endif
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
