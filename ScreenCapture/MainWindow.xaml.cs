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

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using Rectangle = System.Drawing.Rectangle;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ScreenCapture;

public partial class MainWindow : FluentWindow
{
    private const int HotkeyId = 1;

    private sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record Choice<T>(T Value, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MMDeviceEnumerator _devices = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Rectangle? _region;

    private ScreenRecorder? _recorder;
    private RecordingBorderForm? _border;
    private bool _busy;
    private bool _closeConfirmed;
    private string? _lastFile;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        // Follow the Windows light / dark setting, live.
        ApplicationThemeManager.ApplySystemTheme();
        SystemThemeWatcher.Watch(this);
        RestoreWindowSize();
        LoadSettingsIntoUi();
        WireEvents();
    }

    // ---------------------------------------------------------------- settings

    private void LoadSettingsIntoUi()
    {
        FormatBox.ItemsSource = Enum.GetValues<OutputFormat>().Select(f => new Choice<OutputFormat>(f, FormatInfo.DisplayName(f))).ToList();
        QualityBox.ItemsSource = Enum.GetValues<VideoQuality>().Select(q => new Choice<VideoQuality>(q, FormatInfo.DisplayName(q))).ToList();
        var fps = new[] { 10, 15, 24, 30, 60 }.Select(f => new Choice<int>(f, $"{f} fps")).ToList();
        FpsBox.ItemsSource = fps;

        FormatBox.SelectedIndex = (int)_settings.Format;
        QualityBox.SelectedIndex = (int)_settings.Quality;
        FpsBox.SelectedItem = fps.FirstOrDefault(f => f.Value == _settings.Fps) ?? fps.First(f => f.Value == 30);
        CursorSwitch.IsChecked = _settings.ShowCursor;
        BorderSwitch.IsChecked = _settings.ShowRecordingBorder;
        FolderBox.Text = _settings.OutputFolder;
        SysAudioSwitch.IsChecked = _settings.CaptureSystemAudio;
        MicSwitch.IsChecked = _settings.CaptureMicrophone;

        FillDevices(SysDeviceBox, DataFlow.Render, _settings.SystemAudioDeviceId);
        FillDevices(MicDeviceBox, DataFlow.Capture, _settings.MicrophoneDeviceId);
        (_settings.SystemAudioAppsOnly ? OnlyAppsRadio : AllAppsRadio).IsChecked = true;
        RefreshApps(_settings.SystemAudioApps);

        if (_settings.Region is [var x, var y, var w, var h] && Forms.SystemInformation.VirtualScreen.IntersectsWith(new Rectangle(x, y, w, h)))
            _region = new Rectangle(x, y, w, h);
        UpdateZoneUi();
        UpdateAudioEnabled();
    }

    private void FillDevices(ComboBox combo, DataFlow flow, string? selectedId)
    {
        var items = new List<DeviceItem> { new(null, "Default device") };
        try
        {
            foreach (var d in _devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
                items.Add(new DeviceItem(d.ID, d.FriendlyName));
        }
        catch { /* no audio subsystem: keep only "Default" */ }
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(d => d.Id == selectedId) ?? items[0];
    }

    private void SaveSettings()
    {
        _settings.Format = SelectedFormat;
        _settings.Quality = SelectedQuality;
        _settings.Fps = SelectedFps;
        _settings.ShowCursor = CursorSwitch.IsChecked == true;
        _settings.ShowRecordingBorder = BorderSwitch.IsChecked == true;
        _settings.OutputFolder = FolderBox.Text.Trim();
        _settings.CaptureSystemAudio = SysAudioSwitch.IsChecked == true;
        _settings.SystemAudioDeviceId = ((DeviceItem)SysDeviceBox.SelectedItem).Id;
        _settings.SystemAudioAppsOnly = OnlyAppsRadio.IsChecked == true;
        _settings.SystemAudioApps = SelectedAppExes();
        _settings.CaptureMicrophone = MicSwitch.IsChecked == true;
        _settings.MicrophoneDeviceId = ((DeviceItem)MicDeviceBox.SelectedItem).Id;
        _settings.Region = _region is { } r ? [r.X, r.Y, r.Width, r.Height] : null;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var size = _settings.WindowMaximized ? RestoreBounds.Size : new Size(ActualWidth, ActualHeight);
        _settings.WindowSize = _autoHeight ? null : [size.Width, size.Height];
        _settings.Save();
    }

    private OutputFormat SelectedFormat => ((Choice<OutputFormat>)FormatBox.SelectedItem).Value;
    private VideoQuality SelectedQuality => ((Choice<VideoQuality>)QualityBox.SelectedItem).Value;
    private int SelectedFps => ((Choice<int>)FpsBox.SelectedItem).Value;

    // ---------------------------------------------------------------- window events

    private void WireEvents()
    {
        FormatBox.SelectionChanged += (_, _) => UpdateAudioEnabled();
        SysAudioSwitch.Click += (_, _) => UpdateAudioEnabled();
        MicSwitch.Click += (_, _) => UpdateAudioEnabled();
        AllAppsRadio.Checked += (_, _) => UpdateAudioEnabled();
        OnlyAppsRadio.Checked += (_, _) => { RefreshApps(SelectedAppExes()); UpdateAudioEnabled(); };
        // Apps started while the window was in the background show up when coming back to it.
        Activated += (_, _) => { if (AppsListBorder.IsVisible && AppsListBorder.IsEnabled) RefreshApps(SelectedAppExes()); };
        _tick.Tick += (_, _) => { if (_recorder != null) TimerText.Text = _recorder.Elapsed.ToString(@"hh\:mm\:ss"); };
        // Window resized, or content grown / shrunk (app list shown…).
        ContentScroller.SizeChanged += (_, _) => QueueLayoutUpdate();
        LayoutRoot.SizeChanged += (_, _) => QueueLayoutUpdate();

        // WPF's SizeToContent fights maximizing, snapping and programmatic resizes: it only sizes the first
        // layout, then the window follows its content itself (FitHeightToContent) until resized by the user.
        ContentRendered += (_, _) =>
        {
            if (SizeToContent == SizeToContent.Manual) return;
            _autoHeightValue = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            QueueLayoutUpdate();
        };
        // Dragging an edge is caught in WndProc (WM_SIZING); this catches snapping and other resizes, while
        // ignoring the few pixels the window frame adjusts by itself after it is first shown.
        SizeChanged += (_, e) =>
        {
            if (_autoHeight && SizeToContent == SizeToContent.Manual &&
                (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 16 || Math.Abs(e.NewSize.Height - _autoHeightValue) > 16))
                _autoHeight = false;
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) _autoHeight = false; };
    }

    // ---------------------------------------------------------------- responsive layout

    // Widths the layouts are designed for (scale 1), margins included.
    private const double NarrowWidth = 460, WideWidth = 900, MaxScale = 3;
    private bool _wide, _layoutQueued;
    /// <summary>True until the user resizes the window: its height then follows the content.</summary>
    private bool _autoHeight = true;
    private double _autoHeightValue;

    private void RestoreWindowSize()
    {
        if (_settings.WindowSize is not [var w, var h]) return;
        var screen = SystemParameters.WorkArea;
        _autoHeight = false;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, screen.Width));
        Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, screen.Height));
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void QueueLayoutUpdate()
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateLayoutMode);
    }

    /// <summary>
    /// Picks one or two columns and the zoom that best fill the window: the content grows with the window
    /// as long as it fits both its width and its height.
    /// </summary>
    private void UpdateLayoutMode()
    {
        _layoutQueued = false;
        double w = ContentScroller.ActualWidth, h = ContentScroller.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // While the window sizes itself to its content, its height follows the zoom: stay at 1 to avoid a feedback loop.
        bool autoHeight = _autoHeight && WindowState == WindowState.Normal;
        bool bestWide = false;
        double bestScale = 0, bestHeight = double.MaxValue;
        foreach (var wide in w >= WideWidth ? [false, true] : new[] { false })
        {
            double design = wide ? WideWidth : NarrowWidth;
            SetColumns(wide);
            LayoutRoot.Measure(new Size(design, double.PositiveInfinity));
            double natural = LayoutRoot.DesiredSize.Height;
            double scale = autoHeight ? 1 : Math.Clamp(Math.Floor(Math.Min(w / design, h / natural) * 100) / 100, 1, MaxScale);
            // Bigger wins; at the same zoom, the shorter layout (less scrolling).
            if (scale > bestScale + 0.01 || (Math.Abs(scale - bestScale) <= 0.01 && natural < bestHeight))
                (bestWide, bestScale, bestHeight) = (wide, scale, natural);
        }
        SetColumns(bestWide);
        UiScale.ScaleX = UiScale.ScaleY = bestScale;
        if (autoHeight && SizeToContent == SizeToContent.Manual) FitHeightToContent(ActualHeight - h + bestHeight);
    }

    /// <summary>Sizes the window to its content, but never past the bottom of the screen (the content scrolls then).</summary>
    private void FitHeightToContent(double height)
    {
        var area = Forms.Screen.FromHandle(_hwnd).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double top = area.Top / dpi.DpiScaleY, bottom = area.Bottom / dpi.DpiScaleY;
        height = Math.Max(MinHeight, Math.Min(height, bottom - top));
        if (Math.Abs(height - ActualHeight) <= 1) return;
        _autoHeightValue = height;
        Height = height;
        if (Top + height > bottom) Top = Math.Max(top, bottom - height);
    }

    /// <summary>Two columns: capture and video on the left, sound and the record button on the right.</summary>
    private void SetColumns(bool wide)
    {
        if (wide == _wide) return;
        _wide = wide;
        var target = wide ? RightColumn : LeftColumn;
        foreach (var card in new FrameworkElement[] { SoundCard, RecordPanel })
        {
            ((Panel)card.Parent).Children.Remove(card);
            target.Children.Add(card);
        }
        ColumnGap.Width = new GridLength(wide ? 16 : 0);
        RightColumnDef.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightColumn.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Keep this window out of the recording if it overlaps the capture zone.
        NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, (uint)Forms.Keys.F9);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            _ = ToggleRecordingAsync();
        }
        else if (msg == NativeMethods.WM_SIZING)
        {
            _autoHeight = false;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if ((_busy || _recorder != null) && !_closeConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }
        _recorder?.Dispose();
        _border?.Close();
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        SaveSettings();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        if (!await AskAsync("A recording is in progress. Quit and discard it?", "Quit", "Keep recording")) return;
        _closeConfirmed = true;
        Close();
    }

    private void UpdateAudioEnabled()
    {
        bool supported = FormatInfo.SupportsAudio(SelectedFormat);
        bool audio = supported && _recorder == null && !_busy;
        bool system = SysAudioSwitch.IsChecked == true;
        bool appsOnly = OnlyAppsRadio.IsChecked == true;
        SysAudioSwitch.IsEnabled = MicSwitch.IsEnabled = audio;
        // Per-app capture follows the apps whatever device they play on.
        SysDeviceBox.IsEnabled = audio && system && !appsOnly;
        MicDeviceBox.IsEnabled = audio && MicSwitch.IsChecked == true;
        AppsPanel.Visibility = supported && system ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.IsEnabled = audio;
        AppsListBorder.Visibility = AppsHint.Visibility = appsOnly ? Visibility.Visible : Visibility.Collapsed;
        SoundHint.Text = $"{FormatInfo.DisplayName(SelectedFormat)} videos have no sound.";
        SoundHint.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- per-app sound

    private List<string> SelectedAppExes() =>
        AppsList.ItemsSource is IEnumerable<AudioAppEntry> entries
            ? entries.Where(e => e.Selected).Select(e => e.Exe).ToList()
            : [.. _settings.SystemAudioApps];

    /// <summary>Lists apps that have played sound, plus the selected ones even if they are not running.</summary>
    private void RefreshApps(IReadOnlyCollection<string> selected)
    {
        var chosen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var entries = AudioApps.WithAudioSessions(_devices)
            .Select(a => AudioAppEntry.From(a, running: true, chosen.Contains(a.Exe)))
            .ToList();
        foreach (var exe in chosen.Where(x => !entries.Any(e => string.Equals(e.Exe, x, StringComparison.OrdinalIgnoreCase))))
        {
            var path = AudioApps.FindRunningPath(exe);
            var app = new AudioApp(exe, path != null ? AudioApps.DisplayName(path, exe) : exe, path);
            entries.Add(AudioAppEntry.From(app, running: path != null, selected: true));
        }
        AppsList.ItemsSource = entries.OrderByDescending(e => e.Selected).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        NoAppsText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshApps(SelectedAppExes());

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = FolderBox.Text, Title = "Folder for recordings" };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        Directory.CreateDirectory(folder);
        if (_lastFile != null && File.Exists(_lastFile))
            Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        else
            Shell(folder);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile != null) Shell(_lastFile);
    }

    private static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private Task ShowErrorAsync(string message) =>
        new MessageBox { Owner = this, Title = Title, Content = message, CloseButtonText = "OK" }.ShowDialogAsync();

    private async Task<bool> AskAsync(string message, string yes, string no) =>
        await new MessageBox { Owner = this, Title = Title, Content = message, PrimaryButtonText = yes, CloseButtonText = no }
            .ShowDialogAsync() == MessageBoxResult.Primary;

    // ---------------------------------------------------------------- capture zone

    private void UpdateZoneUi()
    {
        var screens = Forms.Screen.AllScreens;
        ScreenChevron.Visibility = screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;

        bool isScreen = false;
        if (_region is not { } r)
            ZoneLabel.Text = "Pick a region or a screen to record";
        else if (Array.FindIndex(screens, s => s.Bounds == r) is var i and >= 0)
        {
            isScreen = true;
            ZoneLabel.Text = screens.Length > 1 ? $"Screen {i + 1}  ·  {r.Width} × {r.Height}" : $"Full screen  ·  {r.Width} × {r.Height}";
        }
        else if (screens.Length > 1 && r == Forms.SystemInformation.VirtualScreen)
        {
            isScreen = true;
            ZoneLabel.Text = $"All screens  ·  {r.Width} × {r.Height}";
        }
        else
            ZoneLabel.Text = $"Region  ·  {r.Width} × {r.Height} at ({r.X}, {r.Y})";

        ZoneIcon.Visibility = _region != null ? Visibility.Visible : Visibility.Collapsed;
        RegionTile.Appearance = _region != null && !isScreen ? ControlAppearance.Primary : ControlAppearance.Secondary;
        ScreenTile.Appearance = isScreen ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void SetRegion(Rectangle region)
    {
        _region = region;
        UpdateZoneUi();
    }

    private async void RegionTile_Click(object sender, RoutedEventArgs e) => await SelectZoneAsync();

    private void ScreenTile_Click(object sender, RoutedEventArgs e)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 1) { SetRegion(screens[0].Bounds); return; }

        var current = Forms.Screen.FromHandle(_hwnd);
        var menu = new ContextMenu { PlacementTarget = ScreenTile, Placement = PlacementMode.Bottom };
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var label = $"Screen {i + 1}  ·  {s.Bounds.Width} × {s.Bounds.Height}" +
                        (s.Primary ? "   (primary)" : "") + (s.Equals(current) ? "   (this window)" : "");
            menu.Items.Add(ScreenMenuItem(label, s.Bounds));
        }
        menu.Items.Add(new Separator());
        var all = Forms.SystemInformation.VirtualScreen;
        menu.Items.Add(ScreenMenuItem($"All screens  ·  {all.Width} × {all.Height}", all));
        menu.IsOpen = true;
    }

    private MenuItem ScreenMenuItem(string header, Rectangle region)
    {
        var item = new MenuItem { Header = header, IsChecked = _region == region };
        item.Click += (_, _) => SetRegion(region);
        return item;
    }

    private async Task<bool> SelectZoneAsync()
    {
        if (_recorder != null || _busy) return false;
        Hide();
        await Task.Delay(200); // let the window disappear before the screen is frozen
        try
        {
            using var selector = new RegionSelectorForm(_region);
            if (selector.ShowDialog() != Forms.DialogResult.OK) return false;
            SetRegion(selector.SelectedRegion);
            return true;
        }
        finally
        {
            Show();
            Activate();
        }
    }

    // ---------------------------------------------------------------- recording

    private async void Record_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private async Task ToggleRecordingAsync()
    {
        if (_busy) return;
        if (_recorder == null) await StartRecordingAsync();
        else await StopRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null)
        {
            await ShowErrorAsync("FFmpeg was not found. Install it with:\n\n    winget install Gyan.FFmpeg\n\nor copy ffmpeg.exe next to ScreenCapture.exe.");
            return;
        }

        if (_region == null && !await SelectZoneAsync()) return;
        var region = _region!.Value;

        var folder = FolderBox.Text.Trim();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Cannot use the output folder:\n{ex.Message}");
            return;
        }

        var format = SelectedFormat;
        var output = Path.Combine(folder, $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{FormatInfo.Extension(format)}");

        bool systemSound = FormatInfo.SupportsAudio(format) && SysAudioSwitch.IsChecked == true;
        bool appsOnly = systemSound && OnlyAppsRadio.IsChecked == true;
        List<int> appPids = [];
        if (appsOnly)
        {
            var apps = SelectedAppExes();
            if (apps.Count == 0)
            {
                await ShowErrorAsync("Choose at least one app whose sound should be recorded, or switch to \"All apps\".");
                return;
            }
            appPids = apps.SelectMany(AudioApps.RootProcessIds).ToList();
            if (appPids.Count == 0)
            {
                await ShowErrorAsync("None of the selected apps is running. Start them before recording.");
                return;
            }
        }

        _busy = true;
        RecordButton.IsEnabled = false;
        StatusText.Text = "Starting…";
        try
        {
            var options = new RecordingOptions
            {
                Region = region,
                Format = format,
                Quality = SelectedQuality,
                Fps = SelectedFps,
                ShowCursor = CursorSwitch.IsChecked == true,
                SystemAudioDevice = systemSound && !appsOnly ? ResolveDevice(SysDeviceBox, DataFlow.Render) : null,
                AppProcessIds = appPids,
                MicrophoneDevice = FormatInfo.SupportsAudio(format) && MicSwitch.IsChecked == true ? ResolveDevice(MicDeviceBox, DataFlow.Capture) : null,
                OutputPath = output,
                FfmpegPath = ffmpeg,
            };
            var recorder = new ScreenRecorder(options);
            await recorder.StartAsync();
            _recorder = recorder;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ready";
            await ShowErrorAsync($"Could not start recording:\n{ex.Message}");
            return;
        }
        finally
        {
            _busy = false;
            RecordButton.IsEnabled = true;
        }

        SaveSettings();
        // A window above a game can keep it from presenting straight to the screen, so the frame is optional.
        if (BorderSwitch.IsChecked == true)
        {
            _border = new RecordingBorderForm(region);
            _border.Show();
        }
        OpenFileButton.Visibility = Visibility.Collapsed;
        TimerText.Text = "00:00:00";
        TimerText.Foreground = (System.Windows.Media.Brush)FindResource("RecordRed");
        StatusText.Text = "Recording…";
        SetRecordingUi(true);
        _tick.Start();
    }

    private MMDevice ResolveDevice(ComboBox combo, DataFlow flow)
    {
        var id = ((DeviceItem)combo.SelectedItem).Id;
        return id != null ? _devices.GetDevice(id) : _devices.GetDefaultAudioEndpoint(flow, flow == DataFlow.Render ? Role.Multimedia : Role.Communications);
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder!;
        _busy = true;
        _tick.Stop();
        _border?.Close();
        _border = null;
        RecordButton.IsEnabled = false;
        TimerText.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        StatusText.Text = "Saving…";

        try
        {
            var file = await recorder.StopAsync(new Progress<string>(s => StatusText.Text = s));
            _lastFile = file;
            StatusText.Text = $"Saved {Path.GetFileName(file)}";
            OpenFileButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Recording failed.";
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            recorder.Dispose();
            _recorder = null;
            _busy = false;
            RecordButton.IsEnabled = true;
            SetRecordingUi(false);
        }
    }

    private void SetRecordingUi(bool recording)
    {
        RecordDot.Width = RecordDot.Height = recording ? 30 : 60;
        RecordDot.CornerRadius = new CornerRadius(recording ? 6 : 30);
        RecordButton.ToolTip = recording ? "Stop recording" : "Start recording";
        foreach (var c in new UIElement[] { RegionTile, ScreenTile, FormatBox, QualityBox, FpsBox, CursorSwitch, BorderSwitch, FolderBox, BrowseButton })
            c.IsEnabled = !recording;
        UpdateAudioEnabled();
    }
}
