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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCapture;

public partial class MainWindow : Window
{
    private sealed record Choice<T>(T Value, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly IPlatform _platform = App.Platform;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Rectangle? _region;

    private ScreenRecorder? _recorder;
    private IDisposable? _border;
    private IDisposable? _hotkey;
    private bool _opened;
    private bool _busy;
    private bool _closeConfirmed;
    private string? _lastFile;

    public MainWindow()
    {
        InitializeComponent();
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

        FillDevices(SysDeviceBox, _platform.OutputDevices(), _settings.SystemAudioDeviceId);
        FillDevices(MicDeviceBox, _platform.InputDevices(), _settings.MicrophoneDeviceId);
        (_settings.SystemAudioAppsOnly ? OnlyAppsRadio : AllAppsRadio).IsChecked = true;
        RefreshApps(_settings.SystemAudioApps);

        if (_settings.Region is [var x, var y, var w, var h] && VirtualScreen().IntersectsWith(new Rectangle(x, y, w, h)))
            _region = new Rectangle(x, y, w, h);
        UpdateZoneUi();
        UpdateAudioEnabled();
    }

    private static void FillDevices(ComboBox combo, IReadOnlyList<AudioDevice> devices, string? selectedId)
    {
        var items = new List<AudioDevice> { new(null, "Default device") };
        items.AddRange(devices);
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
        _settings.OutputFolder = (FolderBox.Text ?? "").Trim();
        _settings.CaptureSystemAudio = SysAudioSwitch.IsChecked == true;
        _settings.SystemAudioDeviceId = ((AudioDevice)SysDeviceBox.SelectedItem!).Id;
        _settings.SystemAudioAppsOnly = OnlyAppsRadio.IsChecked == true;
        _settings.SystemAudioApps = SelectedAppExes();
        _settings.CaptureMicrophone = MicSwitch.IsChecked == true;
        _settings.MicrophoneDeviceId = ((AudioDevice)MicDeviceBox.SelectedItem!).Id;
        _settings.Region = _region is { } r ? [r.X, r.Y, r.Width, r.Height] : null;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var size = _normalSize ?? ClientSize;
        _settings.WindowSize = SizeToContent == SizeToContent.Height ? null : [size.Width, size.Height];
        _settings.Save();
    }

    private OutputFormat SelectedFormat => ((Choice<OutputFormat>)FormatBox.SelectedItem!).Value;
    private VideoQuality SelectedQuality => ((Choice<VideoQuality>)QualityBox.SelectedItem!).Value;
    private int SelectedFps => ((Choice<int>)FpsBox.SelectedItem!).Value;
    private string OutputFolder => (FolderBox.Text ?? "").Trim();
    /// <summary>Record only the selected apps instead of everything the output device plays.</summary>
    private bool AppsOnly => _platform.SupportsAppAudio && OnlyAppsRadio.IsChecked == true;

    // ---------------------------------------------------------------- window events

    private void WireEvents()
    {
        FormatBox.SelectionChanged += (_, _) => UpdateAudioEnabled();
        SysAudioSwitch.IsCheckedChanged += (_, _) => UpdateAudioEnabled();
        MicSwitch.IsCheckedChanged += (_, _) => UpdateAudioEnabled();
        AllAppsRadio.IsCheckedChanged += (_, _) => { if (AllAppsRadio.IsChecked == true) UpdateAudioEnabled(); };
        OnlyAppsRadio.IsCheckedChanged += (_, _) =>
        {
            if (OnlyAppsRadio.IsChecked != true) return;
            RefreshApps(SelectedAppExes());
            UpdateAudioEnabled();
        };
        // Apps started while the window was in the background show up when coming back to it.
        Activated += (_, _) => { if (AppsListBorder.IsEffectivelyVisible && AppsListBorder.IsEffectivelyEnabled) RefreshApps(SelectedAppExes()); };
        _tick.Tick += (_, _) => { if (_recorder != null) TimerText.Text = _recorder.Elapsed.ToString(@"hh\:mm\:ss"); };
        // Window resized, or content grown / shrunk (app list shown…).
        ContentScroller.SizeChanged += (_, _) => QueueLayoutUpdate();
        LayoutRoot.SizeChanged += (_, _) => QueueLayoutUpdate();

        Resized += (_, e) =>
        {
            // Once resized by the user, the window keeps its size instead of following its content.
            if (e.Reason == WindowResizeReason.User) SizeToContent = SizeToContent.Manual;
            if (WindowState == WindowState.Normal) _normalSize = e.ClientSize;
            if (AutoHeight) KeepOnScreen();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Raised again each time the window is shown after picking a region.
        if (_opened) return;
        _opened = true;
        // Keep this window out of the recording if it overlaps the capture zone.
        if (TryGetPlatformHandle() is { } handle) _platform.ExcludeFromCapture(handle.Handle);
        _hotkey = _platform.RegisterStartStopHotkey(() => Dispatcher.UIThread.Post(() => _ = ToggleRecordingAsync()));
        HotkeyHint.IsVisible = _hotkey != null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            // Maximized and back: keep the size the user had, not the content's.
            if (WindowState == WindowState.Maximized) SizeToContent = SizeToContent.Manual;
        }
        else if (change.Property == ActualTransparencyLevelProperty)
        {
            // Mica (Windows 11) shows through a transparent background; elsewhere the theme background is drawn.
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica) Background = Brushes.Transparent;
            else Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));
        }
        else if (change.Property == IsExtendedIntoWindowDecorationsProperty || change.Property == WindowDecorationMarginProperty)
        {
            TitleBar.IsVisible = IsExtendedIntoWindowDecorations;
            if (WindowDecorationMargin.Top > 0) TitleBar.Height = WindowDecorationMargin.Top;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if ((_busy || _recorder != null) && !_closeConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }
        _recorder?.Dispose();
        _border?.Dispose();
        _hotkey?.Dispose();
        SaveSettings();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        if (!await AskAsync("A recording is in progress. Quit and discard it?", "Quit", "Keep recording")) return;
        _closeConfirmed = true;
        Close();
    }

    // ---------------------------------------------------------------- responsive layout

    // Widths the layouts are designed for (scale 1), margins included.
    private const double NarrowWidth = 460, WideWidth = 900, MaxScale = 3;
    private bool _wide, _layoutQueued;
    /// <summary>Last size of the window while neither maximized nor minimized.</summary>
    private Size? _normalSize;

    /// <summary>True until the user resizes the window: its height then follows the content.</summary>
    private bool AutoHeight => SizeToContent == SizeToContent.Height && WindowState == WindowState.Normal;

    private void RestoreWindowSize()
    {
        if (_settings.WindowSize is not [var w, var h]) return;
        var screen = Screens.Primary;
        var area = screen != null ? screen.WorkingArea.Size.ToSize(screen.Scaling) : new Size(w, h);
        SizeToContent = SizeToContent.Manual;
        Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, area.Height));
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void QueueLayoutUpdate()
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.UIThread.Post(UpdateLayoutMode, DispatcherPriority.Background);
    }

    /// <summary>
    /// Picks one or two columns and the zoom that best fill the window: the content grows with the window
    /// as long as it fits both its width and its height.
    /// </summary>
    private void UpdateLayoutMode()
    {
        _layoutQueued = false;
        double w = ContentScroller.Bounds.Width, h = ContentScroller.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // While the window sizes itself to its content, its height follows the zoom: stay at 1 to avoid a feedback loop.
        bool autoHeight = AutoHeight;
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
        UiScale.LayoutTransform = new ScaleTransform(bestScale, bestScale);
        LimitHeightToScreen();
    }

    /// <summary>While the window follows its content, it never grows past the screen (the content scrolls then).</summary>
    private void LimitHeightToScreen()
    {
        if (!AutoHeight)
        {
            MaxHeight = double.PositiveInfinity;
            return;
        }
        if (Screens.ScreenFromVisual(this) is not { } screen) return;
        double frame = (FrameSize?.Height ?? ClientSize.Height) - ClientSize.Height;
        MaxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - frame);
    }

    /// <summary>Moves the window up when growing with its content pushed its bottom off the screen.</summary>
    private void KeepOnScreen()
    {
        if (Screens.ScreenFromVisual(this) is not { } screen) return;
        var area = screen.WorkingArea;
        int height = (int)Math.Ceiling((FrameSize?.Height ?? ClientSize.Height) * screen.Scaling);
        if (Position.Y + height > area.Bottom)
            Position = new PixelPoint(Position.X, Math.Max(area.Y, area.Bottom - height));
    }

    /// <summary>Two columns: capture and video on the left, sound and the record button on the right.</summary>
    private void SetColumns(bool wide)
    {
        if (wide == _wide) return;
        _wide = wide;
        var target = wide ? RightColumn : LeftColumn;
        foreach (var card in new Control[] { SoundCard, RecordPanel })
        {
            ((Panel)card.Parent!).Children.Remove(card);
            target.Children.Add(card);
        }
        LayoutRoot.ColumnDefinitions[1].Width = new GridLength(wide ? 16 : 0);
        LayoutRoot.ColumnDefinitions[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightColumn.IsVisible = wide;
    }

    private void UpdateAudioEnabled()
    {
        bool supported = FormatInfo.SupportsAudio(SelectedFormat);
        bool audio = supported && _recorder == null && !_busy;
        bool system = SysAudioSwitch.IsChecked == true;
        bool appsOnly = AppsOnly;
        SysAudioSwitch.IsEnabled = MicSwitch.IsEnabled = audio;
        // Per-app capture follows the apps whatever device they play on.
        SysDeviceBox.IsEnabled = audio && system && !appsOnly;
        MicDeviceBox.IsEnabled = audio && MicSwitch.IsChecked == true;
        AppsPanel.IsVisible = supported && system && _platform.SupportsAppAudio;
        AppsPanel.IsEnabled = audio;
        AppsListBorder.IsVisible = AppsHint.IsVisible = appsOnly;
        SoundHint.Text = $"{FormatInfo.DisplayName(SelectedFormat)} videos have no sound.";
        SoundHint.IsVisible = !supported;
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
        var entries = _platform.AppsWithSound()
            .Select(a => AudioAppEntry.From(_platform, a, running: true, chosen.Contains(a.Exe)))
            .ToList();
        foreach (var exe in chosen.Where(x => !entries.Any(e => string.Equals(e.Exe, x, StringComparison.OrdinalIgnoreCase))))
        {
            var app = _platform.FindRunningApp(exe);
            entries.Add(AudioAppEntry.From(_platform, app ?? new AudioApp(exe, exe, null), running: app != null, selected: true));
        }
        AppsList.ItemsSource = entries.OrderByDescending(e => e.Selected).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        NoAppsText.IsVisible = entries.Count == 0;
    }

    private void RefreshApps_Click(object? sender, RoutedEventArgs e) => RefreshApps(SelectedAppExes());

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(OutputFolder) ? await StorageProvider.TryGetFolderFromPathAsync(OutputFolder) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for recordings",
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) FolderBox.Text = path;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = OutputFolder;
        Directory.CreateDirectory(folder);
        if (_lastFile != null && File.Exists(_lastFile))
            _platform.RevealInFileManager(_lastFile);
        else
            Shell(folder);
    }

    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastFile != null) Shell(_lastFile);
    }

    private static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private Task ShowErrorAsync(string message) => MessageDialog.ShowAsync(this, message);

    private Task<bool> AskAsync(string message, string yes, string no) => MessageDialog.AskAsync(this, message, yes, no);

    // ---------------------------------------------------------------- capture zone

    private static Rectangle ToRectangle(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The bounding box of all screens, in pixels.</summary>
    private Rectangle VirtualScreen() =>
        Screens.All.Select(s => ToRectangle(s.Bounds)).DefaultIfEmpty().Aggregate(Rectangle.Union);

    private void UpdateZoneUi()
    {
        var screens = Screens.All;
        ScreenChevron.IsVisible = screens.Count > 1;

        bool isScreen = false;
        int index = -1;
        for (int i = 0; i < screens.Count && _region is { } zone; i++)
            if (ToRectangle(screens[i].Bounds) == zone) index = i;

        if (_region is not { } r)
            ZoneLabel.Text = "Pick a region or a screen to record";
        else if (index >= 0)
        {
            isScreen = true;
            ZoneLabel.Text = screens.Count > 1 ? $"Screen {index + 1}  ·  {r.Width} × {r.Height}" : $"Full screen  ·  {r.Width} × {r.Height}";
        }
        else if (screens.Count > 1 && r == VirtualScreen())
        {
            isScreen = true;
            ZoneLabel.Text = $"All screens  ·  {r.Width} × {r.Height}";
        }
        else
            ZoneLabel.Text = $"Region  ·  {r.Width} × {r.Height} at ({r.X}, {r.Y})";

        ZoneIcon.IsVisible = _region != null;
        RegionTile.Classes.Set("accent", _region != null && !isScreen);
        ScreenTile.Classes.Set("accent", isScreen);
    }

    private void SetRegion(Rectangle region)
    {
        _region = region;
        UpdateZoneUi();
    }

    private async void RegionTile_Click(object? sender, RoutedEventArgs e) => await SelectZoneAsync();

    private void ScreenTile_Click(object? sender, RoutedEventArgs e)
    {
        var screens = Screens.All;
        if (screens.Count == 1) { SetRegion(ToRectangle(screens[0].Bounds)); return; }

        var current = Screens.ScreenFromVisual(this);
        var menu = new MenuFlyout { Placement = PlacementMode.Bottom };
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            var label = $"Screen {i + 1}  ·  {s.Bounds.Width} × {s.Bounds.Height}" +
                        (s.IsPrimary ? "   (primary)" : "") + (s.Bounds == current?.Bounds ? "   (this window)" : "");
            menu.Items.Add(ScreenMenuItem(label, ToRectangle(s.Bounds)));
        }
        menu.Items.Add(new Separator());
        var all = VirtualScreen();
        menu.Items.Add(ScreenMenuItem($"All screens  ·  {all.Width} × {all.Height}", all));
        menu.ShowAt(ScreenTile);
    }

    private MenuItem ScreenMenuItem(string header, Rectangle region)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = _region == region };
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
            if (await _platform.SelectRegionAsync(_region) is not { } region) return false;
            SetRegion(region);
            return true;
        }
        finally
        {
            Show();
            Activate();
        }
    }

    // ---------------------------------------------------------------- recording

    private async void Record_Click(object? sender, RoutedEventArgs e) => await ToggleRecordingAsync();

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
            await ShowErrorAsync($"FFmpeg was not found. {_platform.FfmpegInstallHint}");
            return;
        }

        if (_region == null && !await SelectZoneAsync()) return;
        var region = _region!.Value;

        var folder = OutputFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Cannot use the output folder:\n{ex.Message}");
            return;
        }

        var format = SelectedFormat;
        var output = Path.Combine(folder, $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{FormatInfo.Extension(format)}");

        bool sound = FormatInfo.SupportsAudio(format);
        bool systemSound = sound && SysAudioSwitch.IsChecked == true;
        bool appsOnly = systemSound && AppsOnly;
        var sources = new List<AudioSource>();
        if (systemSound && !appsOnly) sources.Add(new SystemSound(((AudioDevice)SysDeviceBox.SelectedItem!).Id));
        if (appsOnly)
        {
            var apps = SelectedAppExes();
            if (apps.Count == 0)
            {
                await ShowErrorAsync("Choose at least one app whose sound should be recorded, or switch to \"All apps\".");
                return;
            }
            var pids = apps.SelectMany(_platform.RootProcessIds).ToList();
            if (pids.Count == 0)
            {
                await ShowErrorAsync("None of the selected apps is running. Start them before recording.");
                return;
            }
            sources.AddRange(pids.Select(pid => new AppSound(pid)));
        }
        if (sound && MicSwitch.IsChecked == true) sources.Add(new Microphone(((AudioDevice)MicDeviceBox.SelectedItem!).Id));

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
                AudioSources = sources,
                OutputPath = output,
                FfmpegPath = ffmpeg,
            };
            var recorder = new ScreenRecorder(_platform, options);
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
        if (BorderSwitch.IsChecked == true) _border = _platform.ShowRecordingBorder(region);
        OpenFileButton.IsVisible = false;
        TimerText.Text = "00:00:00";
        TimerText.Classes.Set("recording", true);
        StatusText.Text = "Recording…";
        SetRecordingUi(true);
        _tick.Start();
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder!;
        _busy = true;
        _tick.Stop();
        _border?.Dispose();
        _border = null;
        RecordButton.IsEnabled = false;
        TimerText.Classes.Set("recording", false);
        StatusText.Text = "Saving…";

        try
        {
            var file = await recorder.StopAsync(new Progress<string>(s => StatusText.Text = s));
            _lastFile = file;
            StatusText.Text = $"Saved {Path.GetFileName(file)}";
            OpenFileButton.IsVisible = true;
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
        ToolTip.SetTip(RecordButton, recording ? "Stop recording" : "Start recording");
        foreach (var c in new Control[] { RegionTile, ScreenTile, FormatBox, QualityBox, FpsBox, CursorSwitch, BorderSwitch, FolderBox, BrowseButton })
            c.IsEnabled = !recording;
        UpdateAudioEnabled();
    }
}
