using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalDocket.App;

/// <summary>Small factory helpers so the windows can be built in code without XAML per window.</summary>
internal static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Text(string text, double size = 13, bool bold = false, string? colorKey = null, bool wrap = true)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (bold) tb.FontWeight = FontWeights.SemiBold;
        if (colorKey != null) tb.Foreground = Res(colorKey);
        return tb;
    }

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        b.Click += (_, _) => onClick();
        return b;
    }

    public static RadioButton Chip(string text, string group, bool isChecked, Action onChecked)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Style = (Style)Application.Current.Resources["Chip"] };
        r.Checked += (_, _) => onChecked();
        return r;
    }

    public static Border Card(UIElement content)
    {
        return new Border
        {
            Background = Res("Paper"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content
        };
    }

    public static string Human(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB"
    };

    public static void PlaceBottomRight(Window w, int slot = 0)
    {
        var area = SystemParameters.WorkArea;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = area.Right - w.Width - 16;
        w.Top = area.Bottom - (w.Height + 12) * (slot + 1) - 4;
    }

    public static void PlaceCenterScreen(Window w)
    {
        var area = SystemParameters.WorkArea;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = area.Left + (area.Width - w.Width) / 2;
        w.Top = area.Top + (area.Height - w.Height) / 2;
    }
}
