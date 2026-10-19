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

using System.Drawing;
using System.Runtime.InteropServices;
using static ScreenCapture.NativeMethods;

namespace ScreenCapture;

/// <summary>
/// Grabs frames with GDI BitBlt: slower than Desktop Duplication, but works for any zone
/// (spanning several screens, rotated screen…).
/// </summary>
internal sealed unsafe class GdiFrameGrabber : IFrameGrabber
{
    private readonly Rectangle _region;
    private readonly bool _showCursor;
    private readonly IntPtr _screenDc, _memDc, _dib, _oldObj, _bits;
    private IntPtr _lastCursor;
    private int _hotX, _hotY;

    public GdiFrameGrabber(Rectangle region, bool showCursor)
    {
        _region = region;
        _showCursor = showCursor;
        // Precise sleeps between frames.
        timeBeginPeriod(1);
        try
        {
            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = region.Width,
                biHeight = -region.Height, // top-down rows, as FFmpeg expects
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = CreateDIBSection(_memDc, ref header, 0, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the capture buffer.");
            _oldObj = SelectObject(_memDc, _dib);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ReadOnlySpan<byte> Grab()
    {
        BitBlt(_memDc, 0, 0, _region.Width, _region.Height, _screenDc, _region.X, _region.Y, SRCCOPY);

        if (_showCursor)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
            {
                if (ci.hCursor != _lastCursor && GetIconInfo(ci.hCursor, out var ii))
                {
                    _lastCursor = ci.hCursor;
                    _hotX = ii.xHotspot;
                    _hotY = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                DrawIconEx(_memDc, ci.ptScreenPos.X - _hotX - _region.X, ci.ptScreenPos.Y - _hotY - _region.Y,
                    ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
        }
        GdiFlush();
        return new ReadOnlySpan<byte>((void*)_bits, _region.Width * _region.Height * 4);
    }

    public void Dispose()
    {
        timeEndPeriod(1);
        if (_oldObj != IntPtr.Zero) SelectObject(_memDc, _oldObj);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
