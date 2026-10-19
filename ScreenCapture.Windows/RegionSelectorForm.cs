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
using System.Windows.Forms;
using System.Drawing.Drawing2D;

namespace ScreenCapture;

/// <summary>
/// Full-screen overlay (spanning all monitors) over a frozen screenshot.
/// Drag to draw a zone, drag inside it to move it, drag the handles to resize it.
/// Enter or double-click confirms, Esc or right-click cancels.
/// </summary>
internal sealed class RegionSelectorForm : Form
{
    private const int HandleSize = 8;
    private const int MinSize = 16;

    private enum Hit { None, Inside, N, S, E, W, NE, NW, SE, SW }

    private readonly Rectangle _virtual = SystemInformation.VirtualScreen;
    private readonly Bitmap _screenshot;
    private readonly Bitmap _dimmed;
    private readonly Font _labelFont = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _helpFont = new("Segoe UI", 11f);

    private Rectangle _sel; // client coordinates (== screen pixels offset by the virtual-screen origin)
    private bool _hasSel;
    private Hit _drag = Hit.None;
    private bool _creating;
    private Point _dragStart;
    private Rectangle _selAtDragStart;
    private Rectangle? _selBeforeCreate;

    public Rectangle SelectedRegion => new(_sel.X + _virtual.X, _sel.Y + _virtual.Y, _sel.Width, _sel.Height);

    public RegionSelectorForm(Rectangle? initial)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = _virtual;

        _screenshot = new Bitmap(_virtual.Width, _virtual.Height);
        using (var g = Graphics.FromImage(_screenshot))
            g.CopyFromScreen(_virtual.Location, Point.Empty, _virtual.Size);

        _dimmed = new Bitmap(_screenshot);
        using (var g = Graphics.FromImage(_dimmed))
        using (var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, _dimmed.Width, _dimmed.Height);

        if (initial is { Width: >= MinSize, Height: >= MinSize } r)
        {
            r.Offset(-_virtual.X, -_virtual.Y);
            r.Intersect(new Rectangle(Point.Empty, _virtual.Size));
            if (r.Width >= MinSize && r.Height >= MinSize) { _sel = r; _hasSel = true; }
        }
    }

    protected override void WndProc(ref Message m)
    {
        // The overlay must stay pixel-exact across monitors with different scaling: ignore DPI-change resizes.
        if (m.Msg == NativeMethods.WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_dimmed, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);

        if (_hasSel && _sel.Width > 0 && _sel.Height > 0)
        {
            g.DrawImage(_screenshot, _sel, _sel, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            using (var pen = new Pen(Color.FromArgb(0, 120, 215), 2))
                g.DrawRectangle(pen, _sel.X - 1, _sel.Y - 1, _sel.Width + 1, _sel.Height + 1);

            foreach (var h in HandleRects())
            {
                g.FillRectangle(Brushes.White, h);
                using var p = new Pen(Color.FromArgb(0, 120, 215));
                g.DrawRectangle(p, h);
            }

            var label = $"{_sel.Width} × {_sel.Height}   ({_sel.X + _virtual.X}, {_sel.Y + _virtual.Y})";
            var size = g.MeasureString(label, _labelFont);
            var ly = _sel.Y - size.Height - 8 >= 0 ? _sel.Y - size.Height - 8 : _sel.Bottom + 8;
            var labelRect = new RectangleF(_sel.X, ly, size.Width + 10, size.Height + 4);
            using (var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20)))
                g.FillRectangle(bg, labelRect);
            g.DrawString(label, _labelFont, Brushes.White, labelRect.X + 5, labelRect.Y + 2);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        DrawHelp(g);
    }

    private Rectangle HelpRect()
    {
        var primary = Screen.PrimaryScreen!.Bounds;
        primary.Offset(-_virtual.X, -_virtual.Y);
        return new Rectangle(primary.X + primary.Width / 2 - 380, primary.Y + 24, 760, 40);
    }

    private void DrawHelp(Graphics g)
    {
        var text = _hasSel
            ? "Drag inside to move  •  Drag the handles to resize  •  Enter / double-click to confirm  •  Esc to cancel"
            : "Drag to select the zone to capture  •  Esc to cancel";
        var r = HelpRect();
        using var bg = new SolidBrush(Color.FromArgb(210, 20, 20, 20));
        g.FillRectangle(bg, r);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, _helpFont, Brushes.White, r, sf);
    }

    private IEnumerable<Rectangle> HandleRects() =>
        HandlePoints().Select(p => new Rectangle(p.pt.X - HandleSize / 2, p.pt.Y - HandleSize / 2, HandleSize, HandleSize));

    private IEnumerable<(Hit hit, Point pt)> HandlePoints()
    {
        int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom, cx = l + _sel.Width / 2, cy = t + _sel.Height / 2;
        yield return (Hit.NW, new Point(l, t));
        yield return (Hit.N, new Point(cx, t));
        yield return (Hit.NE, new Point(r, t));
        yield return (Hit.E, new Point(r, cy));
        yield return (Hit.SE, new Point(r, b));
        yield return (Hit.S, new Point(cx, b));
        yield return (Hit.SW, new Point(l, b));
        yield return (Hit.W, new Point(l, cy));
    }

    private Hit HitTest(Point p)
    {
        if (!_hasSel) return Hit.None;
        foreach (var (hit, pt) in HandlePoints())
            if (Math.Abs(p.X - pt.X) <= HandleSize && Math.Abs(p.Y - pt.Y) <= HandleSize) return hit;
        return _sel.Contains(p) ? Hit.Inside : Hit.None;
    }

    private static Cursor CursorFor(Hit h) => h switch
    {
        Hit.Inside => Cursors.SizeAll,
        Hit.N or Hit.S => Cursors.SizeNS,
        Hit.E or Hit.W => Cursors.SizeWE,
        Hit.NE or Hit.SW => Cursors.SizeNESW,
        Hit.NW or Hit.SE => Cursors.SizeNWSE,
        _ => Cursors.Cross,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button != MouseButtons.Left) return;

        _dragStart = e.Location;
        _selAtDragStart = _sel;
        _drag = HitTest(e.Location);
        _creating = _drag == Hit.None;
        if (_creating)
        {
            _selBeforeCreate = _hasSel ? _sel : null;
            Invalidate();
            _sel = new Rectangle(e.Location, Size.Empty);
            _hasSel = true;
            _drag = Hit.SE;
            _selAtDragStart = _sel;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Hit.None)
        {
            Cursor = CursorFor(HitTest(e.Location));
            return;
        }

        var old = _sel;
        int dx = e.X - _dragStart.X, dy = e.Y - _dragStart.Y;
        var s = _selAtDragStart;
        int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;

        if (_drag == Hit.Inside)
        {
            l = Math.Clamp(l + dx, 0, _virtual.Width - s.Width);
            t = Math.Clamp(t + dy, 0, _virtual.Height - s.Height);
            r = l + s.Width;
            b = t + s.Height;
        }
        else
        {
            if (_drag is Hit.W or Hit.NW or Hit.SW) l += dx;
            if (_drag is Hit.E or Hit.NE or Hit.SE) r += dx;
            if (_drag is Hit.N or Hit.NW or Hit.NE) t += dy;
            if (_drag is Hit.S or Hit.SW or Hit.SE) b += dy;
        }

        var rect = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        rect.Intersect(new Rectangle(Point.Empty, _virtual.Size));
        _sel = rect;
        InvalidateAround(old);
        InvalidateAround(_sel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _drag == Hit.None) return;
        _drag = Hit.None;
        if (_sel.Width < MinSize || _sel.Height < MinSize)
        {
            // A click or a tiny drag keeps the previous zone (if any).
            var previous = _creating ? _selBeforeCreate : _selAtDragStart;
            _hasSel = previous.HasValue;
            if (previous.HasValue) _sel = previous.Value;
        }
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _hasSel && _sel.Contains(e.Location)) Confirm();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        else if (e.KeyCode == Keys.Enter) Confirm();
    }

    private void Confirm()
    {
        if (_hasSel && _sel.Width >= MinSize && _sel.Height >= MinSize) DialogResult = DialogResult.OK;
    }

    private void InvalidateAround(Rectangle r)
    {
        r.Inflate(HandleSize + 4, HandleSize + 4);
        // Room for the size label above or below the zone.
        Invalidate(Rectangle.FromLTRB(r.Left, r.Top - 40, Math.Max(r.Right, r.Left + 320), r.Bottom + 40));
        Invalidate(HelpRect());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _screenshot.Dispose();
            _dimmed.Dispose();
            _labelFont.Dispose();
            _helpFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
