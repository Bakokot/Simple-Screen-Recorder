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
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ScreenCapture;

internal static class AudioApps
{
    /// <summary>Apps that currently have an audio session on any output device.</summary>
    public static List<AudioApp> WithAudioSessions(MMDeviceEnumerator devices)
    {
        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        try
        {
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;
                    int pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == self) continue;
                    var path = ImagePath(pid);
                    if (path == null) continue;
                    var exe = Path.GetFileNameWithoutExtension(path);
                    if (!apps.ContainsKey(exe)) apps[exe] = new AudioApp(exe, DisplayName(path, exe), path);
                }
            }
        }
        catch { /* no audio subsystem: no apps */ }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Top-level running processes of an app: those whose parent is not the same app.
    /// Capturing each one with its process tree covers apps that play sound from a child process (browsers…).
    /// </summary>
    public static List<int> RootProcessIds(string exe)
    {
        var all = new List<(int Pid, int Parent, string Exe)>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE) return [];
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32 { dwSize = Marshal.SizeOf<NativeMethods.PROCESSENTRY32>() };
            for (bool ok = NativeMethods.Process32First(snapshot, ref entry); ok; ok = NativeMethods.Process32Next(snapshot, ref entry))
                all.Add((entry.th32ProcessID, entry.th32ParentProcessID, Path.GetFileNameWithoutExtension(entry.szExeFile)));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        var mine = all.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        var pids = mine.Select(p => p.Pid).ToHashSet();
        return mine.Where(p => !pids.Contains(p.Parent)).Select(p => p.Pid).ToList();
    }

    /// <summary>Full path of a running copy of the app, or null if it is not running.</summary>
    public static string? FindRunningPath(string exe)
    {
        foreach (var pid in RootProcessIds(exe))
            if (ImagePath(pid) is { } path) return path;
        return null;
    }

    public static string DisplayName(string path, string exe)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch { /* no version info */ }
        return exe;
    }

    private static string? ImagePath(int pid)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
