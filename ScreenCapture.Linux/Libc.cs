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

using System.Runtime.InteropServices;

namespace ScreenCapture;

internal static class Libc
{
    public const int O_WRONLY = 0x1;
    public const int O_NONBLOCK = 0x800;

    [DllImport("libc", SetLastError = true)] public static extern int mkfifo(string path, uint mode);
    [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
    [DllImport("libc")] public static extern int close(int fd);
}
