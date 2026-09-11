using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LocalDocket.Core;

namespace LocalDocket.App;

/// <summary>Backlog mode: dry-run a folder, review the proposals in a grid, apply the ticked rows.</summary>
public sealed class ScanWindow : Window
{
    readonly DocketHost _host;
    readonly ObservableCollection<ScanRow> _rows = new();
    readonly TextBox _folder;
    readonly TextBlock _status;
    readonly Button _scanBtn;
    readonly Button _applyBtn;
    readonly DataGrid _grid;
    CancellationTokenSource? _cts;

    public sealed class ScanRow : INotifyPropertyChanged
    {
        readonly DocketHost _host;
        public Decision D { get; }
        bool _apply;
        public ScanRow(DocketHost host, Decision d) { _host = host; D = d; _apply = !d.Result.NeedsUser(host.Taxonomy.Settings.AutoFileConfidence) && d.Result.Action != "skip" && !string.IsNullOrEmpty(d.TargetDirectory); }
        public bool Apply { get => _apply; set { _apply = value; On(); } }
        public string Name => D.Item.Name;
        public string Kind => D.Item.IsDirectory ? "folder" : D.Item.ContentKind ?? D.Item.Extension;
        public string Size => Ui.Human(D.Item.Size);
        public string Category { get => D.Result.Category; set { D.Result.Category = value ?? ""; if (!string.IsNullOrEmpty(value)) D.Result.Domain = _host.Taxonomy.DomainOf(value); Retarget(); On(); } }
        public string Environment { get => D.Result.Environment ?? ""; set { D.Result.Environment = string.IsNullOrEmpty(value) ? null : value; Retarget(); On(); } }
        public string Client { get => D.Result.Client ?? ""; set { D.Result.Client = string.IsNullOrEmpty(value) ? null : value; Retarget(); On(); } }
        public string Confidence => D.Result.Action == "skip" ? "in place" : D.Result.Confidence.ToString("0%");
        public string Questions => string.Join(", ", D.Result.Questions);
        public string Target { get => D.Result.Action == "skip" ? "(already where it belongs)" : D.TargetDirectory; set { D.TargetDirectory = value; D.Result.Action = "file"; On(); } }
        public string Reasoning => D.Result.Reasoning;
        void Retarget()
        {
            try { D.Result.Action = "file"; _host.Pipeline.Retarget(D); } catch { D.TargetDirectory = ""; }
            On(nameof(Target)); On(nameof(Confidence));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        void On([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public ScanWindow(DocketHost host, string? initialFolder = null)
    {
        _host = host;
        Title = "Local Docket — scan a folder";
        Width = 1280; Height = 720;
        Ui.PlaceCenterScreen(this);

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _scanBtn = Ui.Button("Scan", () => _ = ScanAsync(), primary: true);
        var browse = Ui.Button("Browse…", Browse);
        DockPanel.SetDock(_scanBtn, Dock.Right); DockPanel.SetDock(browse, Dock.Right);
        top.Children.Add(_scanBtn); top.Children.Add(browse);
        _folder = new TextBox { Text = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        top.Children.Add(_folder);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _applyBtn = Ui.Button("Apply ticked rows", () => Apply(), primary: true);
        _applyBtn.IsEnabled = false;
        DockPanel.SetDock(_applyBtn, Dock.Right);
        bottom.Children.Add(_applyBtn);
        var tickConfident = Ui.Button("Tick confident", () => { foreach (var r in _rows) r.Apply = !r.D.Result.NeedsUser(_host.Taxonomy.Settings.AutoFileConfidence) && r.D.Result.Action != "skip" && !string.IsNullOrEmpty(r.D.TargetDirectory); });
        var untickAll = Ui.Button("Untick all", () => { foreach (var r in _rows) r.Apply = false; });
        DockPanel.SetDock(tickConfident, Dock.Right); DockPanel.SetDock(untickAll, Dock.Right);
        bottom.Children.Add(untickAll); bottom.Children.Add(tickConfident);
        _status = Ui.Text("Nothing moves until you click Apply. Repos and project folders are one row each. Frozen folders from the taxonomy are skipped.", 12, false, "Muted");
        _status.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _grid = new DataGrid
        {
            ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 28, FontSize = 12, Background = Ui.Res("Paper"), BorderBrush = Ui.Res("Line"),
            SelectionMode = DataGridSelectionMode.Extended
        };
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Apply", Binding = new Binding(nameof(ScanRow.Apply)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 50 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(ScanRow.Name)), IsReadOnly = true, Width = 220 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Kind", Binding = new Binding(nameof(ScanRow.Kind)), IsReadOnly = true, Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding(nameof(ScanRow.Size)), IsReadOnly = true, Width = 70 });
        _grid.Columns.Add(new DataGridComboBoxColumn { Header = "Category", ItemsSource = _host.Taxonomy.CategoryNames.OrderBy(x => x).ToList(), SelectedItemBinding = new Binding(nameof(ScanRow.Category)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 190 });
        _grid.Columns.Add(new DataGridComboBoxColumn { Header = "Env", ItemsSource = new[] { "", "Production", "UAT", "DEV", "Local" }, SelectedItemBinding = new Binding(nameof(ScanRow.Environment)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Client", Binding = new Binding(nameof(ScanRow.Client)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Conf", Binding = new Binding(nameof(ScanRow.Confidence)), IsReadOnly = true, Width = 55 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Target", Binding = new Binding(nameof(ScanRow.Target)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(3, DataGridLengthUnitType.Star), MinWidth = 220 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Why", Binding = new Binding(nameof(ScanRow.Reasoning)), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star), MinWidth = 160 });
        _grid.RowStyle = new Style(typeof(DataGridRow));
        _grid.RowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(ScanRow.Reasoning))));
        root.Children.Add(_grid);

        Content = root;
        Closing += (_, _) => _cts?.Cancel();
    }

    void Browse()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { InitialDirectory = _folder.Text, ShowNewFolderButton = false };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    async Task ScanAsync()
    {
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { MessageBox.Show(this, "Folder not found: " + folder, "Local Docket"); return; }
        if (folder.TrimEnd('\\').Equals(_host.Taxonomy.InboxPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "The inbox is handled by the watcher; use 'Process inbox now'.", "Local Docket"); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _rows.Clear();
        _scanBtn.IsEnabled = false; _applyBtn.IsEnabled = false;
        var progress = new Progress<(int done, int total, string name)>(p => _status.Text = p.done >= p.total ? $"Scanned {p.total} entries." : $"Classifying {p.done + 1}/{p.total}: {p.name}");
        try
        {
            var decisions = await Task.Run(() => _host.ScanAsync(folder, progress, _cts.Token)); // extraction is CPU work; keep it off the dispatcher
            foreach (var d in decisions.OrderBy(d => d.Result.Action == "skip").ThenByDescending(d => d.Result.Confidence)) _rows.Add(new ScanRow(_host, d));
            _status.Text = $"{_rows.Count} entries · {_rows.Count(r => r.Apply)} ticked (confident, no open question) · {_rows.Count(r => r.D.Result.Action == "skip")} already in place";
        }
        catch (OperationCanceledException) { _status.Text = "Scan cancelled."; }
        catch (Exception ex) { Log.Error("scan", ex); _status.Text = "Scan failed: " + ex.Message; }
        finally { _scanBtn.IsEnabled = true; _applyBtn.IsEnabled = _rows.Count > 0; }
    }

    async void Apply()
    {
        if (!_applyBtn.IsEnabled) return;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var chosen = _rows.Where(r => r.Apply && r.D.Result.Action != "skip" && !string.IsNullOrWhiteSpace(r.D.TargetDirectory)).ToList();
        if (chosen.Count == 0) { MessageBox.Show(this, "Nothing ticked.", "Local Docket"); return; }
        if (MessageBox.Show(this, $"Move {chosen.Count} item(s) to their target folders?", "Local Docket", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int ok = 0; var errors = new List<string>();
        _applyBtn.IsEnabled = false;
        try
        {
            foreach (var r in chosen)
            {
                try
                {
                    r.D.Result.DecidedBy = r.D.Result.DecidedBy.StartsWith("rule:") ? r.D.Result.DecidedBy : "user";
                    r.D.Result.Confidence = 1.0; r.D.Result.Questions.Clear();
                    _status.Text = $"Moving {r.Name}…";
                    await Task.Run(() => _host.FileItem(r.D)); // same path as the popup: logs, indexes and toasts the move
                    ok++;
                    _rows.Remove(r);
                }
                catch (Exception ex) { Log.Error("scan apply " + r.Name, ex); errors.Add(r.Name + ": " + ex.Message); }
            }
        }
        finally { _applyBtn.IsEnabled = _rows.Count > 0; }
        _status.Text = $"Moved {ok}. {(errors.Count > 0 ? errors.Count + " failed (see log)." : "")} Undo is in the tray menu under Recent moves.";
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors.Take(15)), "Local Docket — some moves failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
