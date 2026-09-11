using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalDocket.Core;

namespace LocalDocket.App;

/// <summary>The "where does this go?" dialog: one card per item, pre-filled with the classifier's suggestion.</summary>
public sealed class PopupWindow : Window
{
    readonly DocketHost _host;
    readonly FileGroup _group;
    readonly List<Card> _cards = new();
    bool _done;

    sealed class Card
    {
        public required Decision D;
        public required ComboBox Category;
        public required TextBox Target;
        public required CheckBox Include;
        public required CheckBox Remember;
        public bool TargetEdited;
        public string? DomainFilter;
        public Panel? QuestionsPanel;
    }

    public PopupWindow(DocketHost host, FileGroup group, List<Decision> decisions)
    {
        _host = host; _group = group;
        Title = decisions.Count == 1 ? "Local Docket — where does this go?" : $"Local Docket — where do these {decisions.Count} items go?";
        Width = 760;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;

        var root = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(Ui.Text(decisions.Count == 1 ? "1 item landed in the inbox" : $"{decisions.Count} items landed together", 17, true));
        header.Children.Add(Ui.Text(group.Reason + (host.OllamaUp ? "" : "   ·   Ollama is not reachable, suggestions come from name rules only"), 12, false, "Muted"));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(Ui.Button("Recycle Bin", Recycle));
        footer.Children.Add(Ui.Button("Skip for now", Skip));
        var fileBtn = Ui.Button(decisions.Count == 1 ? "File it" : "File them", FileAll, primary: true);
        fileBtn.IsDefault = true;
        footer.Children.Add(fileBtn);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var list = new StackPanel();
        for (int i = 0; i < decisions.Count; i++) list.Children.Add(BuildCard(decisions[i], i, decisions.Count > 1));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = root;
        Loaded += (_, _) => { Ui.PlaceCenterScreen(this); Topmost = false; Activate(); };
        Closing += (_, _) => { if (!_done) foreach (var c in _cards) _host.Skip(c.D.Item); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Skip(); };
    }

    Border BuildCard(Decision d, int index, bool multi)
    {
        var item = d.Item;
        var panel = new StackPanel();

        var title = new DockPanel();
        var include = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Untick to leave this one in the inbox" };
        DockPanel.SetDock(include, Dock.Left);
        title.Children.Add(include);
        var nameBlock = new StackPanel();
        nameBlock.Children.Add(Ui.Text(item.Name, 14, true));
        var meta = item.IsDirectory
            ? $"folder · {item.Meta.GetValueOrDefault("dir.files", "?")} files · {Ui.Human(item.Size)}" + (item.Meta.ContainsKey("dir.markers") ? " · project (" + item.Meta["dir.markers"] + ")" : "")
            : $"{Ui.Human(item.Size)} · {item.ContentKind ?? item.Extension} · modified {item.Modified:yyyy-MM-dd HH:mm}";
        var bakBits = item.Meta.Where(kv => kv.Key.StartsWith("bak.")).Select(kv => $"{kv.Key[4..]}={kv.Value}").ToList();
        if (bakBits.Count > 0) meta += "\n" + string.Join(" · ", bakBits);
        nameBlock.Children.Add(Ui.Text(meta, 12, false, "Muted"));
        title.Children.Add(nameBlock);
        panel.Children.Add(title);
        if (!item.IsDirectory && item.ContentKind == "image-meta")
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(item.Path); bi.DecodePixelWidth = 320; bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit();
                panel.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = bi, MaxWidth = 320, MaxHeight = 200, Stretch = System.Windows.Media.Stretch.Uniform } });
            }
            catch { }
        }

        var reasoning = string.IsNullOrWhiteSpace(d.Result.Reasoning) ? "No suggestion." : d.Result.Reasoning;
        var by = d.Result.DecidedBy.StartsWith("rule:") ? "rule" : d.Result.DecidedBy.StartsWith("llm:") ? _host.Taxonomy.Settings.Model : d.Result.DecidedBy.StartsWith("hint:") ? "name pattern" : d.Result.DecidedBy;
        panel.Children.Add(Ui.Text($"{by} ({d.Result.Confidence:0%}): {reasoning}", 12, false, "Muted"));
        ((TextBlock)panel.Children[^1]).Margin = new Thickness(0, 6, 0, 10);

        // Category
        var catRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var catLabel = Ui.Text("Category", 12, true); catLabel.Width = 90; catLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(catLabel, Dock.Left);
        catRow.Children.Add(catLabel);
        var combo = new ComboBox { ItemsSource = _host.Taxonomy.CategoryNames.OrderBy(x => x).ToList(), SelectedItem = string.IsNullOrEmpty(d.Result.Category) ? null : d.Result.Category, IsEditable = false };
        if (multi)
        {
            var applyAll = new Button { Content = "apply to all", Padding = new Thickness(8, 2, 8, 2), MinHeight = 24, Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
            applyAll.Click += (_, _) => { foreach (var c in _cards) if (c.D != d) c.Category.SelectedItem = combo.SelectedItem; };
            DockPanel.SetDock(applyAll, Dock.Right);
            catRow.Children.Add(applyAll);
        }
        catRow.Children.Add(combo);
        panel.Children.Add(catRow);

        // Questions
        var questions = new StackPanel();
        panel.Children.Add(questions);

        // Target
        var targetRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var tLabel = Ui.Text("Goes to", 12, true); tLabel.Width = 90; tLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(tLabel, Dock.Left);
        targetRow.Children.Add(tLabel);
        var target = new TextBox { Text = d.TargetDirectory, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
        targetRow.Children.Add(target);
        panel.Children.Add(targetRow);

        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(90, 0, 0, 0) };
        var remember = new CheckBox { Content = "Remember this as a rule for files named like this", FontSize = 12 };
        opts.Children.Add(remember);
        panel.Children.Add(opts);

        var card = new Card { D = d, Category = combo, Target = target, Include = include, Remember = remember, QuestionsPanel = questions };
        _cards.Add(card);

        target.TextChanged += (_, _) => { if (target.IsFocused) card.TargetEdited = true; };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string cat)
            {
                d.Result.Category = cat;
                d.Result.Domain = _host.Taxonomy.DomainOf(cat);
                d.Result.Questions.Remove("domain");
            }
            RebuildQuestions(card, index);
            Retarget(card);
        };
        RebuildQuestions(card, index);
        return Ui.Card(panel);
    }

    void RebuildQuestions(Card card, int index)
    {
        var d = card.D;
        var panel = card.QuestionsPanel!;
        panel.Children.Clear();
        var keys = new List<string>(d.Result.Questions);
        if (!string.IsNullOrEmpty(d.Result.Category) && _host.Taxonomy.Categories.TryGetValue(d.Result.Category, out var def))
            foreach (var a in def.Ask) if (!keys.Contains(a, StringComparer.OrdinalIgnoreCase)) keys.Add(a);
        if (string.IsNullOrEmpty(d.Result.Category) && !keys.Contains("domain")) keys.Insert(0, "domain");

        foreach (var key in keys)
        {
            if (!_host.Taxonomy.Questions.TryGetValue(key, out var q)) continue;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var label = Ui.Text(q.Prompt, 12, true); label.Width = 90; label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var chips = new WrapPanel();
            var current = key.ToLowerInvariant() switch { "environment" => d.Result.Environment, "client" => d.Result.Client, "domain" => card.DomainFilter ?? (string.IsNullOrEmpty(d.Result.Category) ? null : d.Result.Domain), _ => null };
            var group = $"{key}-{index}";
            foreach (var opt in q.Options)
            {
                var o = opt;
                chips.Children.Add(Ui.Chip(o, group, string.Equals(current, o, StringComparison.OrdinalIgnoreCase), () => Answer(card, key, o)));
            }
            if (q.AllowAdd)
            {
                var add = new TextBox { Width = 140, Margin = new Thickness(0, 0, 4, 6), ToolTip = "Type a new " + key + " and press Enter" };
                add.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(add.Text)) return;
                    var name = add.Text.Trim();
                    _host.AddClient(name);
                    Answer(card, key, name);
                    foreach (var c in _cards) RebuildQuestions(c, _cards.IndexOf(c));
                    e.Handled = true;
                };
                chips.Children.Add(add);
                chips.Children.Add(Ui.Text("↵ to add", 11, false, "Muted"));
            }
            row.Children.Add(chips);
            panel.Children.Add(row);
        }
    }

    void Answer(Card card, string key, string value)
    {
        var d = card.D;
        switch (key.ToLowerInvariant())
        {
            case "environment": d.Result.Environment = value; break;
            case "client": d.Result.Client = value; break;
            case "domain":
                card.DomainFilter = value;
                var cats = _host.Taxonomy.CategoryNames.Where(c => c.StartsWith(value + "/")).OrderBy(c => c).ToList();
                card.Category.ItemsSource = cats;
                if (string.IsNullOrEmpty(d.Result.Category) || !d.Result.Category.StartsWith(value + "/"))
                {
                    var hint = RuleEngine.Hint(d.Item, _host.Taxonomy);
                    card.Category.SelectedItem = hint != null && hint.Category.StartsWith(value + "/") ? hint.Category : cats.FirstOrDefault();
                }
                return;
        }
        d.Result.Questions.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        Retarget(card);
    }

    void Retarget(Card card)
    {
        if (card.TargetEdited) return;
        try { _host.Pipeline.Retarget(card.D); card.Target.Text = card.D.TargetDirectory; }
        catch { card.Target.Text = ""; }
    }

    bool _filing;

    async void FileAll()
    {
        if (_filing) return; // a second click while the first is still hashing/moving would file twice
        _filing = true;
        var errors = new List<string>();
        foreach (var c in _cards)
        {
            var d = c.D;
            if (c.Include.IsChecked != true) { _host.Skip(d.Item); continue; }
            if (string.IsNullOrWhiteSpace(c.Target.Text)) { errors.Add(d.Item.Name + ": no target folder"); continue; }
            d.Result.Category = c.Category.SelectedItem as string ?? d.Result.Category;
            d.Result.Confidence = 1.0;
            d.Result.DecidedBy = "user";
            d.Result.Questions.Clear();
            d.TargetDirectory = c.Target.Text.Trim();
            try
            {
                await Task.Run(() => _host.FileItem(d)); // hashing and cross-volume copies must not freeze the window
                if (c.Remember.IsChecked == true && !string.IsNullOrEmpty(d.Result.Category) && !_host.RememberRule(d))
                    errors.Add(d.Item.Name + ": name too short to become a rule (filed anyway)");
            }
            catch (Exception ex) { Log.Error("file " + d.Item.Name, ex); errors.Add(d.Item.Name + ": " + ex.Message); }
        }
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Local Docket — some items were not filed", MessageBoxButton.OK, MessageBoxImage.Warning);
        _done = true;
        Close();
    }

    void Skip()
    {
        foreach (var c in _cards) _host.Skip(c.D.Item);
        _done = true;
        Close();
    }

    void Recycle()
    {
        var chosen = _cards.Where(c => c.Include.IsChecked == true).ToList();
        if (chosen.Count == 0) return;
        var names = string.Join("\n", chosen.Select(c => c.D.Item.Name));
        if (MessageBox.Show(this, "Send to the Recycle Bin?\n\n" + names, "Local Docket", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var c in _cards)
        {
            if (c.Include.IsChecked == true) { try { _host.Recycle(c.D.Item); } catch (Exception ex) { Log.Error("recycle", ex); } }
            else _host.Skip(c.D.Item);
        }
        _done = true;
        Close();
    }
}
