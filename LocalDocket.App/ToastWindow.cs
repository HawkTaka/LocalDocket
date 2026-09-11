using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalDocket.Core;

namespace LocalDocket.App;

/// <summary>Bottom-right "filed X → folder" card with Undo. Auto-closes unless the mouse is over it.</summary>
public sealed class ToastWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    public int Slot { get; set; }

    public ToastWindow(DocketHost host, Decision d, MoveResult r)
    {
        Width = 400; Height = 96;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;

        var panel = new StackPanel();
        var head = new DockPanel();
        var close = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 11 };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(Ui.Text($"Filed · {d.Result.Category}", 12, true, "Muted"));
        panel.Children.Add(head);
        var name = Ui.Text(d.Item.Name, 13, true); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        panel.Children.Add(name);
        var where = Ui.Text("→ " + Shorten(Path.GetDirectoryName(r.FinalPath) ?? ""), 11, false, "Muted"); where.TextTrimming = TextTrimming.CharacterEllipsis; where.TextWrapping = TextWrapping.NoWrap;
        panel.Children.Add(where);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Ui.Button("Undo", async () =>
        {
            try { await Task.Run(() => host.Undo(r.MoveId)); } catch (Exception ex) { Log.Error("undo", ex); MessageBox.Show(ex.Message, "Local Docket — undo failed"); }
            Close();
        }));
        buttons.Children.Add(Ui.Button("Open folder", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.FinalPath}\"") { UseShellExecute = true }); } catch { }
        }));
        foreach (Button b in buttons.Children) { b.MinHeight = 26; b.Padding = new Thickness(10, 2, 10, 2); b.FontSize = 12; }
        panel.Children.Add(buttons);

        Content = new Border
        {
            Background = Ui.Res("Paper"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 10), Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 }
        };

        Loaded += (_, _) => { Ui.PlaceBottomRight(this, Slot); _timer.Start(); };
        _timer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        Closed += (_, _) => _timer.Stop();
    }

    static string Shorten(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Length <= 4 ? path : string.Join(Path.DirectorySeparatorChar, parts[..2]) + "\\…\\" + string.Join(Path.DirectorySeparatorChar, parts[^2..]);
    }
}
