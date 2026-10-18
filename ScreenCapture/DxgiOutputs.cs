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

using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenCapture;

/// <summary>A monitor as DXGI numbers it, which is how FFmpeg's ddagrab picks the screen to capture.</summary>
internal sealed record DxgiOutput(int AdapterIndex, int OutputIndex, Rectangle Bounds);

internal static unsafe class DxgiOutputs
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    // Vtable slots: IUnknown takes 0-2 and IDXGIObject 3-6, so IDXGIFactory::EnumAdapters,
    // IDXGIAdapter::EnumOutputs and IDXGIOutput::GetDesc are all the first method after them.
    private const int FirstMethod = 7;
    private const int DXGI_MODE_ROTATION_IDENTITY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    /// <summary>The unrotated monitor that fully contains <paramref name="region"/>, or null.</summary>
    public static DxgiOutput? Containing(Rectangle region) =>
        Enumerate().FirstOrDefault(o => o.Bounds.Contains(region));

    public static List<DxgiOutput> Enumerate()
    {
        var list = new List<DxgiOutput>();
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0) return list;
            try
            {
                for (uint a = 0; Enum(factory, a, out var adapter) >= 0; a++)
                {
                    try
                    {
                        for (uint o = 0; Enum(adapter, o, out var output) >= 0; o++)
                        {
                            try
                            {
                                DXGI_OUTPUT_DESC d;
                                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)(*(void***)output)[FirstMethod];
                                // Rotated screens would need the frames turned; leave those to GDI capture.
                                if (getDesc(output, &d) >= 0 && d.AttachedToDesktop != 0 && d.Rotation <= DXGI_MODE_ROTATION_IDENTITY)
                                    list.Add(new DxgiOutput((int)a, (int)o, Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom)));
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch { /* no DXGI: the caller falls back to GDI capture */ }
        return list;
    }

    private static int Enum(IntPtr parent, uint index, out IntPtr child)
    {
        IntPtr result;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)parent)[FirstMethod];
        int hr = fn(parent, index, &result);
        child = hr >= 0 ? result : IntPtr.Zero;
        return hr;
    }
}
