using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalDocket.App;

/// <summary>"Find me that spec about X": name + snippet + embedding search over everything Local Docket has filed.</summary>
public sealed class SearchWindow : Window
{
    readonly DocketHost _host;
    readonly TextBox _query;
    readonly ListView _list;
    readonly TextBlock _status;

    sealed record Row(string Score, string Category, string Name, string Path);

    public SearchWindow(DocketHost host)
    {
        _host = host;
        Title = "Local Docket — search filed items";
        Width = 900; Height = 520;
        Ui.PlaceCenterScreen(this);

        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var go = Ui.Button("Search", () => _ = RunAsync(), primary: true);
        go.IsDefault = true;
        DockPanel.SetDock(go, Dock.Right);
        top.Children.Add(go);
        _query = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 14 };
        top.Children.Add(_query);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _status = Ui.Text($"{host.Store.CountFiled()} items indexed. Double-click a result to show it in Explorer.", 12, false, "Muted");
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Score", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Score)) });
        gv.Columns.Add(new GridViewColumn { Header = "Category", Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Category)) });
        gv.Columns.Add(new GridViewColumn { Header = "Name", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = "Path", Width = 340, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Path)) });
        _list = new ListView { View = gv, Background = Ui.Res("Paper"), BorderBrush = Ui.Res("Line") };
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is Row r)
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.Path}\"") { UseShellExecute = true }); } catch { }
        };
        root.Children.Add(_list);
        Content = root;
        Loaded += (_, _) => _query.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    async Task RunAsync()
    {
        var text = _query.Text.Trim();
        if (text.Length == 0) return;
        _status.Text = "Searching…";
        float[]? emb = null;
        try { emb = await _host.Pipeline.EmbedForSearchAsync(text); } catch { }
        var results = _host.Store.Search(text, emb);
        _list.ItemsSource = results.Select(r => new Row(r.Score.ToString("0.00"), r.Category, r.Name, r.Path)).ToList();
        _status.Text = results.Count == 0 ? "No matches." : $"{results.Count} result(s)" + (emb == null ? " (name match only, embeddings unavailable)" : "");
    }
}
