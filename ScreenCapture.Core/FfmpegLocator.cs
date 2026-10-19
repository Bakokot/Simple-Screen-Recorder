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

public static class FfmpegLocator
{
    public static string? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", exe),
        };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), exe));

        if (!OperatingSystem.IsWindows()) return candidates.FirstOrDefault(Exists);

        // winget installs: the PATH of an already-running session may not include them yet.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe));

        if (candidates.FirstOrDefault(Exists) is { } found) return found;

        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
            {
                var path = Directory.EnumerateFiles(pkg, exe, SearchOption.AllDirectories).FirstOrDefault();
                if (path != null) return path;
            }
        }
        return null;
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); } catch { return false; /* malformed PATH entry */ }
    }
}
