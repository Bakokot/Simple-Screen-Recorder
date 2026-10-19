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

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCapture;

/// <summary>
/// Full-screen overlay (spanning all monitors), preferably over a frozen screenshot.
/// Drag to draw a zone, drag inside it to move it, drag the handles to resize it.
/// Enter or double-click confirms, Esc or right-click cancels.
/// </summary>
/// <remarks>The zone is kept in window units and converted to screen pixels when confirmed.</remarks>
internal sealed class RegionSelectorWindow : Window
{
    private const double HandleSize = 8;
    private const double MinPixels = 16;

    private enum Hit { None, Inside, N, S, E, W, NE, NW, SE, SW }

    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(210, 20, 20, 20));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 2);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1);

    private readonly TaskCompletionSource<Rectangle?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly double _scale;
    private readonly Rect _help;
    private readonly Surface _surface;
    private Bitmap? _screenshot;

    private Rect _sel;
    private bool _hasSel;
    private Hit _drag = Hit.None;
    private bool _creating;
    private Point _dragStart;
    private Rect _selAtDragStart;
    private Rect? _selBeforeCreate;

    /// <summary>The area covered by the overlay, in screen pixels.</summary>
    public PixelRect Desktop { get; }

    /// <summary>The confirmed zone in screen pixels, or null if cancelled.</summary>
    public Task<Rectangle?> Result => _result.Task;

    public RegionSelectorWindow(Rectangle? initial)
    {
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        var screens = Screens.All;
        Desktop = screens.Select(s => s.Bounds).DefaultIfEmpty(new PixelRect(0, 0, 1920, 1080)).Aggregate((a, b) => a.Union(b));
        _scale = DesktopScaling > 0 ? DesktopScaling : 1;
        Position = Desktop.Position;
        Width = Desktop.Width / _scale;
        Height = Desktop.Height / _scale;

        var primary = Screens.Primary?.Bounds ?? Desktop;
        _help = new Rect(ToWindow(primary.X - Desktop.X) + ToWindow(primary.Width) / 2 - 380, ToWindow(primary.Y - Desktop.Y) + 24, 760, 40);

        if (initial is { } r && r.Width >= MinPixels && r.Height >= MinPixels)
        {
            var sel = new Rect(ToWindow(r.X - Desktop.X), ToWindow(r.Y - Desktop.Y), ToWindow(r.Width), ToWindow(r.Height))
                .Intersect(new Rect(0, 0, Width, Height));
            if (sel.Width * _scale >= MinPixels && sel.Height * _scale >= MinPixels) { _sel = sel; _hasSel = true; }
        }
        Content = _surface = new Surface(this);
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Draws the overlay (a window's own Render would be painted over by its content).</summary>
    private sealed class Surface(RegionSelectorWindow owner) : Control
    {
        public override void Render(DrawingContext context) => owner.Draw(context);
    }

    /// <summary>Shows a frozen picture of the desktop under the overlay (stretched over it).</summary>
    public void SetScreenshot(Bitmap? screenshot)
    {
        _screenshot = screenshot;
        if (screenshot != null) Background = Brushes.Black;
        _surface.InvalidateVisual();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        Focus();
    }

    private double ToWindow(int pixels) => pixels / _scale;

    // ---------------------------------------------------------------- drawing

    private void Draw(DrawingContext g)
    {
        var all = new Rect(Bounds.Size);
        if (_screenshot != null) g.DrawImage(_screenshot, new Rect(_screenshot.Size), all);

        if (_hasSel && _sel.Width > 0 && _sel.Height > 0)
        {
            // Darken everything but the zone.
            g.FillRectangle(Shade, new Rect(0, 0, all.Width, _sel.Top));
            g.FillRectangle(Shade, new Rect(0, _sel.Bottom, all.Width, all.Height - _sel.Bottom));
            g.FillRectangle(Shade, new Rect(0, _sel.Top, _sel.Left, _sel.Height));
            g.FillRectangle(Shade, new Rect(_sel.Right, _sel.Top, all.Width - _sel.Right, _sel.Height));
            g.DrawRectangle(null, Outline, _sel.Inflate(1));
            foreach (var h in HandleRects()) g.DrawRectangle(Brushes.White, HandlePen, h);

            var px = ToPixels(_sel);
            var label = Text($"{px.Width} × {px.Height}   ({px.X}, {px.Y})", 13, FontWeight.Bold);
            var y = _sel.Y - label.Height - 8 >= 0 ? _sel.Y - label.Height - 8 : _sel.Bottom + 8;
            var box = new Rect(_sel.X, y, label.Width + 10, label.Height + 4);
            g.FillRectangle(LabelBack, box);
            g.DrawText(label, new Point(box.X + 5, box.Y + 2));
        }
        else
        {
            g.FillRectangle(Shade, all);
        }

        var help = Text(_hasSel
            ? "Drag inside to move  •  Drag the handles to resize  •  Enter / double-click to confirm  •  Esc to cancel"
            : "Drag to select the zone to capture  •  Esc to cancel", 14, FontWeight.Normal);
        g.FillRectangle(LabelBack, _help);
        g.DrawText(help, new Point(_help.X + (_help.Width - help.Width) / 2, _help.Y + (_help.Height - help.Height) / 2));
    }

    private static FormattedText Text(string text, double size, FontWeight weight) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Brushes.White);

    private IEnumerable<Rect> HandleRects() =>
        HandlePoints().Select(p => new Rect(p.Pt.X - HandleSize / 2, p.Pt.Y - HandleSize / 2, HandleSize, HandleSize));

    private IEnumerable<(Hit Hit, Point Pt)> HandlePoints()
    {
        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom, cx = _sel.Center.X, cy = _sel.Center.Y;
        yield return (Hit.NW, new Point(l, t));
        yield return (Hit.N, new Point(cx, t));
        yield return (Hit.NE, new Point(r, t));
        yield return (Hit.E, new Point(r, cy));
        yield return (Hit.SE, new Point(r, b));
        yield return (Hit.S, new Point(cx, b));
        yield return (Hit.SW, new Point(l, b));
        yield return (Hit.W, new Point(l, cy));
    }

    // ---------------------------------------------------------------- mouse and keyboard

    private Hit HitTest(Point p)
    {
        if (!_hasSel) return Hit.None;
        foreach (var (hit, pt) in HandlePoints())
            if (Math.Abs(p.X - pt.X) <= HandleSize && Math.Abs(p.Y - pt.Y) <= HandleSize) return hit;
        return _sel.Contains(p) ? Hit.Inside : Hit.None;
    }

    private static Cursor CursorFor(Hit h) => new(h switch
    {
        Hit.Inside => StandardCursorType.SizeAll,
        Hit.N or Hit.S => StandardCursorType.SizeNorthSouth,
        Hit.E or Hit.W => StandardCursorType.SizeWestEast,
        Hit.NW => StandardCursorType.TopLeftCorner,
        Hit.NE => StandardCursorType.TopRightCorner,
        Hit.SW => StandardCursorType.BottomLeftCorner,
        Hit.SE => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.Cross,
    });

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        if (point.Properties.IsRightButtonPressed) { Cancel(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            if (_hasSel && _sel.Contains(p)) Confirm();
            return;
        }

        _dragStart = p;
        _selAtDragStart = _sel;
        _drag = HitTest(p);
        _creating = _drag == Hit.None;
        if (_creating)
        {
            _selBeforeCreate = _hasSel ? _sel : null;
            _sel = new Rect(p, p);
            _hasSel = true;
            _drag = Hit.SE;
            _selAtDragStart = _sel;
        }
        _surface.InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Hit.None)
        {
            Cursor = CursorFor(HitTest(p));
            return;
        }

        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        var s = _selAtDragStart;
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        if (_drag == Hit.Inside)
        {
            l = Math.Clamp(l + dx, 0, Bounds.Width - s.Width);
            t = Math.Clamp(t + dy, 0, Bounds.Height - s.Height);
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
        _sel = new Rect(new Point(Math.Min(l, r), Math.Min(t, b)), new Point(Math.Max(l, r), Math.Max(t, b)))
            .Intersect(new Rect(Bounds.Size));
        _surface.InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _drag == Hit.None) return;
        _drag = Hit.None;
        var px = ToPixels(_sel);
        if (px.Width < MinPixels || px.Height < MinPixels)
        {
            // A click or a tiny drag keeps the previous zone (if any).
            var previous = _creating ? _selBeforeCreate : _selAtDragStart;
            _hasSel = previous.HasValue;
            if (previous.HasValue) _sel = previous.Value;
        }
        _surface.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
        else if (e.Key is Key.Enter or Key.Return) Confirm();
        else base.OnKeyDown(e);
    }

    private Rectangle ToPixels(Rect r)
    {
        int x = (int)Math.Round(r.X * _scale), y = (int)Math.Round(r.Y * _scale);
        int right = (int)Math.Round(r.Right * _scale), bottom = (int)Math.Round(r.Bottom * _scale);
        // Where the window really is: some window managers shift it from the position asked for.
        var origin = IsVisible ? Position : Desktop.Position;
        return new Rectangle(origin.X + x, origin.Y + y, right - x, bottom - y);
    }

    private void Confirm()
    {
        var px = ToPixels(_sel);
        if (!_hasSel || px.Width < MinPixels || px.Height < MinPixels) return;
        _result.TrySetResult(px);
        Close();
    }

    private void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }
}
