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

/// <summary>
/// A system-wide keyboard shortcut on X11, grabbed on the root window through a connection and a thread of
/// its own. (Wayland has no equivalent that works on every desktop.)
/// </summary>
internal sealed unsafe class X11Hotkey : IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private const uint ShiftMask = 1 << 0, LockMask = 1 << 1, ControlMask = 1 << 2, Mod2Mask = 1 << 4;
    private const int GrabModeAsync = 1;
    private const int KeyPress = 2, KeyRelease = 3;
    private const nint XK_F9 = 0xFFC6;

    [DllImport(LibX11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] private static extern nint XDefaultRootWindow(nint display);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(nint display, nint keysym);
    [DllImport(LibX11)] private static extern int XGrabKey(nint display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
    [DllImport(LibX11)] private static extern int XSync(nint display, int discard);
    [DllImport(LibX11)] private static extern int XPending(nint display);
    [DllImport(LibX11)] private static extern int XNextEvent(nint display, void* evt);
    [DllImport(LibX11)] private static extern int XkbSetDetectableAutoRepeat(nint display, int detectable, nint supported);
    [DllImport(LibX11)] private static extern nint XSetErrorHandler(nint handler);

    // Error handling is process-wide in Xlib: while grabbing, errors on our connection mean another app owns
    // the shortcut; the others go to the previous handler (Avalonia's).
    private static readonly object ErrorLock = new();
    private static nint _grabDisplay, _previousHandler;
    private static bool _grabFailed;

    private readonly nint _display;
    private readonly nint _root;
    private readonly int _keycode;
    private readonly Action _pressed;
    private readonly Thread _thread;
    private volatile bool _stop;

    private X11Hotkey(nint display, nint root, int keycode, Action pressed)
    {
        _display = display;
        _root = root;
        _keycode = keycode;
        _pressed = pressed;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Global shortcut" };
        _thread.Start();
    }

    /// <summary>Grabs Ctrl + Shift + F9; null if there is no X server or another app owns it.</summary>
    public static X11Hotkey? TryRegister(Action pressed)
    {
        try
        {
            var display = XOpenDisplay(0);
            if (display == 0) return null;
            var root = XDefaultRootWindow(display);
            int keycode = XKeysymToKeycode(display, XK_F9);
            if (keycode == 0 || !Grab(display, root, keycode))
            {
                XCloseDisplay(display);
                return null;
            }
            // Holding the keys sends a single press, not one per auto-repeat.
            XkbSetDetectableAutoRepeat(display, 1, 0);
            return new X11Hotkey(display, root, keycode, pressed);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The shortcut must work whatever the state of Caps Lock and Num Lock.</summary>
    private static IEnumerable<uint> Modifiers() =>
        new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask }.Select(m => ControlMask | ShiftMask | m);

    private static bool Grab(nint display, nint root, int keycode)
    {
        lock (ErrorLock)
        {
            _grabDisplay = display;
            _grabFailed = false;
            _previousHandler = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&OnError);
            try
            {
                foreach (var m in Modifiers()) XGrabKey(display, keycode, m, root, 0, GrabModeAsync, GrabModeAsync);
                XSync(display, 0);
            }
            finally
            {
                XSetErrorHandler(_previousHandler);
                _grabDisplay = 0;
            }
            if (_grabFailed)
            {
                foreach (var m in Modifiers()) XUngrabKey(display, keycode, m, root);
                XSync(display, 0);
            }
            return !_grabFailed;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnError(nint display, nint errorEvent)
    {
        if (display == _grabDisplay)
        {
            _grabFailed = true;
            return 0;
        }
        return _previousHandler != 0 ? ((delegate* unmanaged<nint, nint, int>)_previousHandler)(display, errorEvent) : 0;
    }

    private void Loop()
    {
        // XEvent is a union of at most 24 longs.
        var evt = stackalloc long[24];
        bool down = false;
        while (!_stop)
        {
            while (XPending(_display) > 0)
            {
                XNextEvent(_display, evt);
                int type = *(int*)evt;
                if (type == KeyPress && !down)
                {
                    down = true;
                    _pressed();
                }
                else if (type == KeyRelease) down = false;
            }
            Thread.Sleep(30);
        }
        foreach (var m in Modifiers()) XUngrabKey(_display, _keycode, m, _root);
        XCloseDisplay(_display);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }
}
