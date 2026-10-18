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

using System.Diagnostics;

namespace ScreenCapture;

/// <summary>One audio source recorded to a WAV file in parallel with the video.</summary>
internal interface IAudioTrack : IDisposable
{
    string FilePath { get; }
    /// <summary>Time, on the recording clock, at which capture started.</summary>
    TimeSpan StartedAt { get; }
    bool HasData { get; }
    Exception? Error { get; }
    void Start(Stopwatch clock);
    Task StopAsync();
}
