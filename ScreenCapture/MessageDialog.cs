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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ScreenCapture;

/// <summary>A small modal message with one or two buttons.</summary>
internal sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message, string? primary, string close)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 320;
        MaxWidth = 520;
        this[!BackgroundProperty] = this.GetResourceObservable("WindowBackgroundBrush").ToBinding();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
        };
        if (primary != null)
        {
            var yes = new Button { Content = primary, IsDefault = true, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            yes.Classes.Add("accent");
            yes.Click += (_, _) => Close(true);
            buttons.Children.Add(yes);
        }
        var no = new Button { Content = close, IsCancel = true, IsDefault = primary == null, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        no.Click += (_, _) => Close(false);
        buttons.Children.Add(no);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
    }

    public static Task ShowAsync(Window owner, string message) =>
        new MessageDialog(owner.Title ?? "", message, null, "OK").ShowDialog<bool>(owner);

    public static Task<bool> AskAsync(Window owner, string message, string yes, string no) =>
        new MessageDialog(owner.Title ?? "", message, yes, no).ShowDialog<bool>(owner);
}
