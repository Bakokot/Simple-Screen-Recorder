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

using static ScreenCapture.NativeMethods;

namespace ScreenCapture;

/// <summary>
/// A system-wide keyboard shortcut. It is registered on a thread of its own with its own message loop,
/// so it works whatever UI toolkit owns the windows.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 1;

    private readonly Thread _thread;
    private uint _threadId;

    private GlobalHotkey(uint modifiers, uint key, Action pressed, TaskCompletionSource<bool> registered)
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            // Creates the message queue of the thread before anyone can post WM_QUIT to it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            if (!RegisterHotKey(IntPtr.Zero, Id, modifiers | MOD_NOREPEAT, key))
            {
                registered.SetResult(false);
                return;
            }
            registered.SetResult(true);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WM_HOTKEY) pressed();
            UnregisterHotKey(IntPtr.Zero, Id);
        }) { IsBackground = true, Name = "Global shortcut" };
        _thread.Start();
    }

    /// <summary>Registers the shortcut; null if another app already owns it.</summary>
    public static GlobalHotkey? TryRegister(uint modifiers, uint key, Action pressed)
    {
        var registered = new TaskCompletionSource<bool>();
        var hotkey = new GlobalHotkey(modifiers, key, pressed, registered);
        return registered.Task.GetAwaiter().GetResult() ? hotkey : null;
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
