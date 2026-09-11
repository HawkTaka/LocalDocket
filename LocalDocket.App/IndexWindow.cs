using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalDocket.Core;

namespace LocalDocket.App;

/// <summary>Index status plus the excluded-folder list: what the chat can see, and what it must never read.</summary>
public sealed class IndexWindow : Window
{
    readonly DocketHost _host;
    readonly TextBlock _phase, _counts, _lastCrawl, _lastError, _hint;
    readonly ListBox _list;
    readonly TextBox _pattern;
    readonly Button _pause;

    public IndexWindow(DocketHost host)
    {
        _host = host;
        Title = "Local Docket — indexing";
        Width = 760; Height = 560;
        Ui.PlaceCenterScreen(this);

        var root = new DockPanel { Margin = new Thickness(14) };

        // ---- status card ----
        var status = new StackPanel();
        status.Children.Add(Ui.Text("Index", 15, true));
        _phase = Ui.Text("", 13); _counts = Ui.Text("", 12, false, "Muted"); _lastCrawl = Ui.Text("", 12, false, "Muted"); _lastError = Ui.Text("", 12, false, "Muted");
        status.Children.Add(_phase); status.Children.Add(_counts); status.Children.Add(_lastCrawl); status.Children.Add(_lastError);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Ui.Button("Reindex now", () => { _host.ReindexNow(); _phase.Text = "crawl requested…"; }, primary: true));
        actions.Children.Add(Ui.Button("Re-embed everything", () =>
        {
            if (MessageBox.Show(this, "Drop every chunk and embed all documents again? This takes several minutes.", "Local Docket", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            { _host.ReindexNow(full: true); _phase.Text = "full crawl requested…"; }
        }));
        _pause = Ui.Button(_host.Indexer.Paused ? "Resume indexing" : "Pause indexing", () =>
        {
            _host.Indexer.Paused = !_host.Indexer.Paused;
            _pause.Content = _host.Indexer.Paused ? "Resume indexing" : "Pause indexing";
            Render(_host.Indexer.Status);
        });
        actions.Children.Add(_pause);
        status.Children.Add(actions);
        var card = Ui.Card(status);
        DockPanel.SetDock(card, Dock.Top);
        root.Children.Add(card);

        // ---- exclusions ----
        var exHead = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        exHead.Children.Add(Ui.Text("Excluded folders", 15, true));
        DockPanel.SetDock(exHead, Dock.Top);
        root.Children.Add(exHead);
        _hint = Ui.Text("Never scanned or indexed. An absolute path excludes that folder and everything below it; 'Infra\\Deploy' is taken under each root; " +
                        "a bare name such as node_modules matches any folder. .git, bin, obj, node_modules and similar are always excluded, and repositories " +
                        "(folders with .git, *.sln, …) are skipped when indexSkipRepos is on. Frozen folders from the taxonomy are still indexed; exclude them here if they should not be. " +
                        "Changes are written to taxonomy.yaml and take effect on the next crawl.", 12, false, "Muted");
        DockPanel.SetDock(_hint, Dock.Top);
        root.Children.Add(_hint);

        var add = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        var addBtn = Ui.Button("Add", AddPattern, primary: true);
        var browse = Ui.Button("Browse…", Browse);
        var remove = Ui.Button("Remove selected", RemoveSelected);
        DockPanel.SetDock(addBtn, Dock.Right); DockPanel.SetDock(browse, Dock.Right); DockPanel.SetDock(remove, Dock.Right);
        add.Children.Add(addBtn); add.Children.Add(browse); add.Children.Add(remove);
        _pattern = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Absolute path, root-relative path, or folder name (globs allowed)" };
        _pattern.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddPattern(); };
        add.Children.Add(_pattern);
        DockPanel.SetDock(add, Dock.Bottom);
        root.Children.Add(add);

        _list = new ListBox { Background = Ui.Res("Paper"), BorderBrush = Ui.Res("Line"), FontSize = 13, SelectionMode = SelectionMode.Extended };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };
        root.Children.Add(_list);

        Content = root;
        FillList();
        Render(_host.Indexer.Status);
        Action<IndexStatus> onStatus = s => Dispatcher.BeginInvoke(() => Render(s));
        _host.Indexer.Changed += onStatus;
        Closed += (_, _) => _host.Indexer.Changed -= onStatus;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void Render(IndexStatus s)
    {
        var paused = _host.Indexer.Paused ? " (paused)" : "";
        _phase.Text = s.Phase switch
        {
            "indexing" => $"Indexing {s.Done + 1}/{s.Total}: {s.Current}{paused}",
            "scanning" => "Checking the roots for changed files…" + paused,
            "pruning" => "Removing entries for deleted or excluded files…" + paused,
            _ => (_host.Index.Ready ? "Idle" : "Loading vectors…") + paused,
        };
        _counts.Text = $"{s.Files:n0} files · {s.Chunks:n0} chunks in docket.db · {_host.Index.Count:n0} vectors in memory · model {_host.Taxonomy.Settings.EmbedModel}";
        _lastCrawl.Text = s.LastFullCrawl is { } t ? $"Last full crawl {t:yyyy-MM-dd HH:mm}; re-crawls every {_host.Taxonomy.Settings.IndexRecrawlMinutes} min and after each filing." : "No full crawl yet.";
        _lastError.Text = s.LastError == null ? "" : "Last error: " + s.LastError;
        _lastError.Visibility = s.LastError == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void FillList()
    {
        _list.Items.Clear();
        foreach (var p in _host.Taxonomy.Settings.ExcludeFolders) _list.Items.Add(p);
        foreach (var p in Exclusions.BuiltIn) _list.Items.Add(new ListBoxItem { Content = p + "   (built in)", IsEnabled = false, Foreground = Ui.Res("Muted") });
    }

    void AddPattern()
    {
        var p = _pattern.Text.Trim().Trim('"');
        if (p.Length == 0) return;
        Save(_host.Taxonomy.Settings.ExcludeFolders.Append(p));
        _pattern.Clear();
    }

    void Browse()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { ShowNewFolderButton = false, Description = "Folder to exclude from indexing and Scan mode" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) Save(_host.Taxonomy.Settings.ExcludeFolders.Append(dlg.SelectedPath));
    }

    void RemoveSelected()
    {
        var selected = _list.SelectedItems.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return;
        Save(_host.Taxonomy.Settings.ExcludeFolders.Where(p => !selected.Contains(p)));
    }

    void Save(IEnumerable<string> patterns)
    {
        try { _host.SetExcludeFolders(patterns.ToList()); }
        catch (Exception ex) { Log.Error("exclusions", ex); MessageBox.Show(this, "Could not update taxonomy.yaml: " + ex.Message, "Local Docket"); }
        FillList();
    }
}
