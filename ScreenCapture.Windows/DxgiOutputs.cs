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

namespace ScreenCapture;

/// <summary>A monitor as DXGI numbers it, which is how FFmpeg's ddagrab picks the screen to capture.</summary>
/// <param name="Monitor">Its HMONITOR, which is how FFmpeg's gfxcapture picks it.</param>
/// <param name="Hdr">Whether Windows drives it in HDR (Settings › Display › Use HDR).</param>
internal sealed record DxgiOutput(int AdapterIndex, int OutputIndex, Rectangle Bounds, IntPtr Monitor, bool Hdr);

internal static unsafe class DxgiOutputs
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    // Vtable slots: IUnknown takes 0-2 and IDXGIObject 3-6, so IDXGIFactory::EnumAdapters,
    // IDXGIAdapter::EnumOutputs and IDXGIOutput::GetDesc are all the first method after them.
    private const int FirstMethod = 7;
    // IDXGIOutput6::GetDesc1 follows the 12 methods of IDXGIOutput and the 8 added up to IDXGIOutput5.
    private const int GetDesc1Method = 27;
    private const int DXGI_MODE_ROTATION_IDENTITY = 1;
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC1
    {
        public DXGI_OUTPUT_DESC Desc;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
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
                                    list.Add(new DxgiOutput((int)a, (int)o, Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom), d.Monitor, IsHdr(output)));
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

    /// <summary>Whether the output is in HDR: its color space is then PQ (ST 2084) instead of sRGB.</summary>
    private static bool IsHdr(IntPtr output)
    {
        if (Marshal.QueryInterface(output, IID_IDXGIOutput6, out var output6) < 0) return false; // before Windows 10 1703
        try
        {
            DXGI_OUTPUT_DESC1 d;
            var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC1*, int>)(*(void***)output6)[GetDesc1Method];
            return getDesc1(output6, &d) >= 0 && d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
        }
        finally { Marshal.Release(output6); }
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
