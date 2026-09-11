using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using LocalDocket.Core;

namespace LocalDocket.App;

/// <summary>"Where is the Acme MSA outline?" — asks the chat service, streams the answer, lists the cited files.</summary>
public sealed class ChatWindow : Window
{
    readonly DocketHost _host;
    readonly StackPanel _turns;
    readonly ScrollViewer _scroll;
    readonly TextBox _input;
    readonly TextBlock _status;
    readonly Button _ask, _stop;
    readonly List<ChatTurn> _history = new();
    CancellationTokenSource? _cts;

    public ChatWindow(DocketHost host)
    {
        _host = host;
        Title = "Local Docket — chat with your files";
        Width = 900; Height = 680;
        Ui.PlaceCenterScreen(this);

        var root = new DockPanel { Margin = new Thickness(14) };

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var row = new DockPanel();
        _ask = Ui.Button("Ask", () => _ = AskAsync(), primary: true);
        _stop = Ui.Button("Stop", () => _cts?.Cancel());
        _stop.IsEnabled = false;
        var clear = Ui.Button("New chat", () => { _history.Clear(); _turns.Children.Clear(); _input.Focus(); });
        DockPanel.SetDock(_ask, Dock.Right); DockPanel.SetDock(_stop, Dock.Right); DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(_ask); row.Children.Add(_stop); row.Children.Add(clear);
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 120, FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 8, 6, 8),
            ToolTip = "Enter asks; Shift+Enter starts a new line"
        };
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; _ = AskAsync(); }
        };
        row.Children.Add(_input);
        bottom.Children.Add(row);
        _status = Ui.Text("", 12, false, "Muted");
        _status.Margin = new Thickness(0, 6, 0, 0);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _turns = new StackPanel();
        _scroll = new ScrollViewer { Content = _turns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(_scroll);

        Content = root;
        Loaded += (_, _) => { _input.Focus(); _ = RefreshStatusAsync(); };
        Closing += (_, _) => _cts?.Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_stop.IsEnabled) Close(); };
    }

    async Task RefreshStatusAsync()
    {
        var up = await _host.ProbeOllamaAsync();
        var c = _host.Store.IndexCounts();
        _status.Text = up
            ? $"{c.Files:n0} files · {c.Chunks:n0} chunks indexed · answers from {_host.Chat.ChatModelName ?? _host.Taxonomy.Settings.Model} · everything stays on this machine"
            : $"{c.Files:n0} files · {c.Chunks:n0} chunks indexed · Ollama is not reachable, so only name matches will show";
    }

    async Task AskAsync()
    {
        var question = _input.Text.Trim();
        if (question.Length == 0 || _stop.IsEnabled) return;
        _input.Clear();
        _cts = new CancellationTokenSource();
        _ask.IsEnabled = false; _stop.IsEnabled = true;

        var card = new StackPanel();
        card.Children.Add(Ui.Text(question, 14, true));
        var answer = new TextBox
        {
            IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
            FontSize = 13, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0), IsReadOnlyCaretVisible = false, Text = "Thinking…"
        };
        card.Children.Add(answer);
        var sourcesPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        card.Children.Add(sourcesPanel);
        var notice = Ui.Text("", 12, false, "Muted");
        notice.Margin = new Thickness(0, 6, 0, 0);
        notice.Visibility = Visibility.Collapsed;
        card.Children.Add(notice);
        _turns.Children.Add(Ui.Card(card));
        _scroll.ScrollToEnd();

        var sb = new System.Text.StringBuilder();
        IReadOnlyList<ChatSource> sources = Array.Empty<ChatSource>();
        try
        {
            _status.Text = "Searching the index…";
            var reply = await _host.Chat.AskAsync(question, _history, _cts.Token);
            sources = reply.Sources;
            ShowSources(sourcesPanel, sources);
            if (reply.Notice != null) { notice.Text = reply.Notice; notice.Visibility = Visibility.Visible; }
            if (reply.Filters.Any) _status.Text = "Filters: " + reply.Filters + " · answering…";
            else _status.Text = sources.Count == 0 ? "" : "Answering…";
            answer.Text = "";
            var any = false;
            await foreach (var token in reply.Tokens.WithCancellation(_cts.Token))
            {
                if (!any) { any = true; }
                sb.Append(token);
                answer.Text = sb.ToString();
                if (_scroll.VerticalOffset >= _scroll.ScrollableHeight - 40) _scroll.ScrollToEnd();
            }
            if (!any) answer.Text = sources.Count == 0 ? "Nothing in the index looks related to that." : "Matching files are listed below.";
        }
        catch (OperationCanceledException) { if (sb.Length == 0) answer.Text = "(stopped)"; else answer.Text = sb + " …(stopped)"; }
        catch (Exception ex)
        {
            Log.Error("chat", ex);
            answer.Text = "Something went wrong: " + ex.Message;
        }
        finally
        {
            _history.Add(new ChatTurn(question, answer.Text, sources));
            _ask.IsEnabled = true; _stop.IsEnabled = false;
            _cts = null;
            _ = RefreshStatusAsync();
            _input.Focus();
        }
    }

    static void ShowSources(WrapPanel panel, IReadOnlyList<ChatSource> sources)
    {
        panel.Children.Clear();
        if (sources.Count == 0) return;
        panel.Children.Add(Ui.Text("Sources: ", 12, true, "Muted"));
        foreach (var s in sources)
        {
            var link = new Hyperlink(new Run($"[{s.N}] {s.Name}")) { ToolTip = $"{s.Path}\n{s.Category ?? "(uncategorised)"} · relevance {s.Score:0.00}" };
            var path = s.Path;
            link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } };
            var tb = new TextBlock(link) { FontSize = 12, Margin = new Thickness(0, 0, 12, 4) };
            panel.Children.Add(tb);
        }
    }
}
