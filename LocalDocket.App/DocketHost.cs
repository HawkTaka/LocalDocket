using System.IO;
using LocalDocket.Core;
using LocalDocket.Extract;
using LocalDocket.Llm;

namespace LocalDocket.App;

/// <summary>Owns the taxonomy, store, pipeline, inbox poll and the background indexer. UI-agnostic; the tray wires the callbacks.</summary>
public sealed class DocketHost : IDisposable
{
    public Taxonomy Taxonomy { get; private set; } = null!;
    public Store Store { get; private set; } = null!;
    public Mover Mover { get; private set; } = null!;
    public Pipeline Pipeline { get; private set; } = null!;
    public VectorIndex Index { get; } = new();
    public Indexer Indexer { get; private set; } = null!;
    public ChatService Chat { get; private set; } = null!;
    OllamaClient _ollama = null!;
    string? _ollamaUrl;
    OllamaClassifier _backend = null!;
    OllamaChat _chat = null!;

    System.Threading.Timer? _timer;
    int _processing;   // 1 while a tick classifies / auto-files
    int _asking;       // 1 while popups are being shown
    readonly System.Collections.Concurrent.ConcurrentQueue<(FileGroup g, List<Decision> ask)> _askQueue = new();
    readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _skipped = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    readonly CancellationTokenSource _shutdown = new();
    Task? _indexLoop;
    DateTime _taxonomyStamp;
    string? _lastIndexError;
    DateTime _lastStaleWarning = DateTime.MinValue;

    public bool Paused { get; set; }
    public bool OllamaUp { get; private set; }
    /// <summary>Things start-up wants the user to see once (a restored taxonomy, unknown settings, a remote Ollama URL).</summary>
    public string? StartupWarning { get; private set; }

    /// <summary>Show the popup for a group and return when the user has dealt with it.</summary>
    public Func<FileGroup, List<Decision>, Task>? AskUser { get; set; }
    public Action<Decision, MoveResult>? Filed { get; set; }
    public Action<string>? Status { get; set; }
    /// <summary>Progress of the background indexer (throttled to about once a second while crawling).</summary>
    public Action<IndexStatus>? IndexStatus { get; set; }
    /// <summary>Something the user should see once: title, text, isWarning.</summary>
    public Action<string, string, bool>? Notify { get; set; }

    public void Start()
    {
        LoadTaxonomy();
        Store = new Store(DocketPaths.DbPath) { RowDeleted = id => Index.RemoveFile(id) }; // rows the store drops on its own leave memory too
        Mover = new Mover(Store) { Log = Log.Warn };
        BuildPipeline();
        Directory.CreateDirectory(Taxonomy.InboxPath);
        // The inbox is polled: FileSystemWatcher events were only ever a hint, and polling every settleSeconds is what actually settles files.
        var period = TimeSpan.FromSeconds(Math.Max(2, Taxonomy.Settings.SettleSeconds));
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(3), period);
        _ = Task.Run(async () => OllamaUp = await _ollama.IsUpAsync());

        // The indexer reads the taxonomy through a delegate, so a reload is picked up on its next pass.
        Indexer = new Indexer(() => Taxonomy, Store, Index, new ContentExtractor(), () => OllamaUp ? _backend : null, Log.Info)
        {
            Busy = () => _processing == 1 || Paused,
            BeforeCrawl = async _ => { ReloadTaxonomyIfChanged(); await ProbeOllamaAsync(); }, // late Ollama start and hand edits are noticed
        };
        Indexer.Changed += OnIndexStatus;
        _indexLoop = Task.Run(async () =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Store.LoadChunkVectors((id, ord, vec) => Index.Add(id, ord, vec));
                Index.Ready = true;
                Log.Info($"vector index loaded: {Index.Count} chunks / {Index.Files} files in {sw.ElapsedMilliseconds} ms");
                // A few seconds' grace so start-up inbox work and the Ollama probe finish first.
                await Task.Delay(TimeSpan.FromSeconds(20), _shutdown.Token);
                OllamaUp = await _ollama.IsUpAsync();
                await Indexer.RunAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("index loop", ex); Notify?.Invoke("Indexing stopped", ex.Message + " (restart Local Docket)", true); }
        });
    }

    void LoadTaxonomy()
    {
        Taxonomy = Taxonomy.LoadWithFallback(DocketPaths.TaxonomyPath, out var warning);
        _taxonomyStamp = SafeStamp();
        var warnings = new List<string>();
        if (warning != null) warnings.Add(warning);
        if (Taxonomy.UnknownSettings.Count > 0) warnings.Add("Unknown keys under settings: " + string.Join(", ", Taxonomy.UnknownSettings) + " (typo? they are ignored).");
        if (Uri.TryCreate(Taxonomy.Settings.OllamaUrl, UriKind.Absolute, out var u) && !u.IsLoopback)
            warnings.Add($"ollamaUrl points at {u.Host}: document text will leave this machine.");
        foreach (var w in warnings) Log.Warn(w);
        StartupWarning = warnings.Count == 0 ? null : string.Join("\n", warnings);
    }

    DateTime SafeStamp() { try { return File.GetLastWriteTimeUtc(DocketPaths.TaxonomyPath); } catch { return DateTime.MinValue; } }

    void BuildPipeline()
    {
        if (_ollamaUrl != Taxonomy.Settings.OllamaUrl) { _ollama = new OllamaClient(Taxonomy.Settings.OllamaUrl); _ollamaUrl = Taxonomy.Settings.OllamaUrl; }
        _backend = new OllamaClassifier(_ollama, Taxonomy.Settings) { Log = Log.Info };
        Pipeline = new Pipeline(Taxonomy, new ContentExtractor(), _backend, Store, new PipelineOptions { Log = Log.Warn });
        _chat = new OllamaChat(_ollama, Taxonomy.Settings);
        Chat = new ChatService(() => Taxonomy, Store, Index, () => OllamaUp ? _backend : null, () => OllamaUp ? _chat : null, Log.Info);
    }

    /// <summary>Re-check Ollama now (the chat window calls this so a freshly started Ollama is used without waiting for the next tick).</summary>
    public async Task<bool> ProbeOllamaAsync() => OllamaUp = await _ollama.IsUpAsync();

    public void ReloadTaxonomy()
    {
        LoadTaxonomy();
        BuildPipeline();
        Directory.CreateDirectory(Taxonomy.InboxPath);
        Log.Info("taxonomy reloaded from " + Taxonomy.SourcePath);
        if (StartupWarning != null) Notify?.Invoke("Taxonomy", StartupWarning, true);
    }

    /// <summary>Pick up a hand edit of taxonomy.yaml without a menu click; a broken edit is reported, not applied.</summary>
    public void ReloadTaxonomyIfChanged()
    {
        var stamp = SafeStamp();
        if (stamp == _taxonomyStamp) return;
        try { ReloadTaxonomy(); }
        catch (Exception ex)
        {
            _taxonomyStamp = stamp; // report once, keep running on the last good taxonomy
            Log.Error("taxonomy reload", ex);
            Notify?.Invoke("taxonomy.yaml not reloaded", ex.Message, true);
        }
    }

    public void ProcessNow() => _ = TickAsync();

    /// <summary>Re-crawl the roots now (full = re-embed everything). Also probes Ollama so a restart is noticed at once.</summary>
    public void ReindexNow(bool full = false)
    {
        _ = Task.Run(async () => { OllamaUp = await _ollama.IsUpAsync(); Indexer.RequestCrawl(full); });
    }

    /// <summary>Replace the exclusion list (writes taxonomy.yaml) and reload so every consumer sees it; the next crawl prunes.</summary>
    public void SetExcludeFolders(IEnumerable<string> folders)
    {
        Taxonomy.SetExcludeFolders(folders);
        ReloadTaxonomy();
        Log.Info("exclusions: " + string.Join(", ", Taxonomy.Settings.ExcludeFolders));
    }

    void OnIndexStatus(IndexStatus s)
    {
        IndexStatus?.Invoke(s);
        if (s.LastError != null && s.LastError != _lastIndexError) { _lastIndexError = s.LastError; Notify?.Invoke("Indexing", s.LastError, true); }
        if (s.LastError == null && s.Phase == "idle") _lastIndexError = null;
    }

    /// <summary>A dead or stalled index loop would otherwise go unnoticed for weeks.</summary>
    void CheckIndexHealth()
    {
        if (Indexer == null || !Index.Ready || Indexer.EmbedderDown || Indexer.Paused) return;
        var s = Indexer.Status;
        if (s.Phase != "idle" || s.LastFullCrawl is not { } last) return;
        var limit = TimeSpan.FromMinutes(2 * Math.Max(1, Taxonomy.Settings.IndexRecrawlMinutes) + 20);
        if (DateTime.Now - last > limit && DateTime.Now - _lastStaleWarning > TimeSpan.FromHours(6))
        {
            _lastStaleWarning = DateTime.Now;
            Notify?.Invoke("Index may be stale", $"Last full crawl {last:yyyy-MM-dd HH:mm}. Open Indexing… and click Reindex now.", true);
        }
    }

    async Task TickAsync()
    {
        if (Paused) return;
        try { CheckIndexHealth(); ReloadTaxonomyIfChanged(); } catch (Exception ex) { Log.Error("tick housekeeping", ex); }
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
        var pending = new List<(FileGroup g, List<Decision> ask)>();
        try
        {
            List<FileItem> items;
            lock (_gate)
            {
                var ignore = new HashSet<string>(_busy, StringComparer.OrdinalIgnoreCase);
                foreach (var (path, mtime) in _skipped.ToList())
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) { _skipped.Remove(path); continue; }
                    var now = Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                    if (now == mtime) ignore.Add(path); else _skipped.Remove(path);
                }
                items = Pipeline.SettledInboxItems(Taxonomy.InboxPath, Taxonomy.Settings.SettleSeconds, ignore);
                foreach (var i in items) _busy.Add(i.Path);
            }
            if (items.Count == 0) return;
            OllamaUp = await _ollama.IsUpAsync();
            Status?.Invoke($"classifying {items.Count} item(s)…");
            var groups = Grouper.Group(items, Taxonomy.Settings.GroupWindowSeconds);
            var threshold = Taxonomy.Settings.AutoFileConfidence;
            // 1) classify everything, 2) auto-file what is certain, 3) queue the rest for popups (shown outside this guarded section).
            foreach (var g in groups)
            {
                var decisions = new List<Decision>();
                foreach (var item in g.Items)
                {
                    try
                    {
                        var d = await Pipeline.ClassifyAsync(item);
                        // Durable undo: an item the user put back must not be re-filed by the same decision after a restart.
                        if (!d.Result.NeedsUser(threshold) && Store.WasUndoneAt(item.Path, item.Size, item.Modified))
                        {
                            d.Result.Confidence = Math.Min(d.Result.Confidence, 0.5);
                            d.Result.Reasoning = "You undid this filing earlier, so Local Docket asks instead of re-filing. " + d.Result.Reasoning;
                        }
                        decisions.Add(d);
                    }
                    catch (Exception ex) { Log.Error("classify " + item.Name, ex); decisions.Add(new Decision { Item = item, Result = new Classification { Confidence = 0, Reasoning = ex.Message, DecidedBy = "error" } }); }
                }
                var auto = decisions.Where(d => !d.Result.NeedsUser(threshold) && !string.IsNullOrEmpty(d.TargetDirectory) && d.Result.Action != "skip").ToList();
                var ask = decisions.Except(auto).Where(d => d.Result.Action != "skip").ToList();
                foreach (var d in auto)
                {
                    try { FileItem(d); }
                    catch (Exception ex) { Log.Error("auto-file " + d.Item.Name, ex); ask.Add(d); }
                }
                foreach (var d in decisions.Where(d => d.Result.Action == "skip")) Skip(d.Item);
                if (ask.Count > 0) pending.Add((g, ask));
                lock (_gate) foreach (var d in auto) _busy.Remove(d.Item.Path);
            }
            Status?.Invoke(pending.Count == 0 ? "idle" : $"waiting for you on {pending.Sum(p => p.ask.Count)} item(s)");
        }
        catch (Exception ex) { Log.Error("tick", ex); }
        finally
        {
            lock (_gate)
            {
                // Items waiting for a popup stay busy so the next tick leaves them alone; everything else is released.
                var keep = new HashSet<string>(pending.SelectMany(p => p.ask).Select(d => d.Item.Path), StringComparer.OrdinalIgnoreCase);
                _busy.RemoveWhere(p => !keep.Contains(p));
            }
            Interlocked.Exchange(ref _processing, 0);
        }
        foreach (var p in pending) _askQueue.Enqueue(p);
        if (pending.Count > 0) _ = PumpAsksAsync();
    }

    /// <summary>Show queued popups one at a time. Runs outside the tick guard, so an open popup no longer blocks the inbox or the indexer.</summary>
    async Task PumpAsksAsync()
    {
        if (Interlocked.CompareExchange(ref _asking, 1, 0) != 0) return; // the running pump will pick the new groups up
        try
        {
            while (_askQueue.TryDequeue(out var p))
            {
                if (AskUser != null)
                {
                    try { await AskUser(p.g, p.ask); }
                    catch (Exception ex) { Log.Error("popup", ex); }
                }
                lock (_gate) foreach (var d in p.ask) _busy.Remove(d.Item.Path);
            }
            Status?.Invoke("idle");
        }
        finally
        {
            Interlocked.Exchange(ref _asking, 0);
            if (!_askQueue.IsEmpty) _ = PumpAsksAsync();
        }
    }

    public MoveResult FileItem(Decision d)
    {
        if (string.IsNullOrEmpty(d.TargetDirectory)) Pipeline.Retarget(d);
        var r = Mover.Move(d.Item, d.Result, d.TargetDirectory, Pipeline.EmbeddingOf(d.Item));
        Log.Info($"filed #{r.MoveId} {d.Item.Name} → {r.FinalPath} ({d.Result.DecidedBy}, {d.Result.Confidence:0.00})");
        Indexer?.Enqueue(r.FinalPath); // full-text index the filed item soon
        Filed?.Invoke(d, r);
        return r;
    }

    public string Undo(long moveId)
    {
        var back = Mover.Undo(moveId);
        Log.Info($"undo #{moveId} → {back}");
        lock (_gate) _skipped[back] = Directory.Exists(back) ? Directory.GetLastWriteTime(back) : File.GetLastWriteTime(back);
        return back;
    }

    /// <summary>Leave it in the inbox and stop asking until the item changes.</summary>
    public void Skip(FileItem item)
    {
        lock (_gate) _skipped[item.Path] = item.IsDirectory ? Directory.GetLastWriteTime(item.Path) : File.GetLastWriteTime(item.Path);
    }

    public void Recycle(FileItem item)
    {
        if (item.IsDirectory)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        Log.Info("recycled " + item.Path);
    }

    /// <summary>Turn the user's answer into a name rule so the same shape of file never asks again. False when the name is too weak to be a rule.</summary>
    public bool RememberRule(Decision d)
    {
        var stem = d.Item.Stem;
        var pattern = RuleEngine.LearnedNamePattern(stem);
        if (pattern == null) { Log.Warn($"not learning a rule from '{d.Item.Name}': name too short to be safe"); return false; }
        var rule = new RuleDef
        {
            Name = "learned-" + System.Text.RegularExpressions.Regex.Replace(stem.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') + "-" + DateTime.Now.ToString("yyyyMMddHHmm"),
            When = new Dictionary<string, object> { ["name"] = pattern },
            Notes = "Learned from " + d.Item.Name
        };
        if (!d.Item.IsDirectory && d.Item.Extension.Length > 0) rule.When["ext"] = new List<string> { d.Item.Extension };
        rule.Set["category"] = d.Result.Category;
        if (!string.IsNullOrEmpty(d.Result.Environment)) rule.Set["environment"] = d.Result.Environment;
        if (!string.IsNullOrEmpty(d.Result.Client)) rule.Set["client"] = d.Result.Client;
        Taxonomy.AppendRule(rule);
        _taxonomyStamp = SafeStamp(); // our own edit, already in memory
        Log.Info("learned rule " + rule.Name);
        return true;
    }

    public void AddClient(string name)
    {
        Taxonomy.AddQuestionOption("client", name);
        _taxonomyStamp = SafeStamp();
        Log.Info("client added: " + name);
    }

    /// <summary>Dry-run classification of a folder's top-level entries for Scan mode.</summary>
    public async Task<List<Decision>> ScanAsync(string folder, IProgress<(int done, int total, string name)> progress, CancellationToken ct)
    {
        var entries = Taxonomy.ScannableEntries(folder);
        var results = new List<Decision>();
        int n = 0;
        foreach (var p in entries)
        {
            ct.ThrowIfCancellationRequested();
            var item = Core.FileItem.FromPath(p);
            progress.Report((n, entries.Count, item.Name));
            try
            {
                var d = await Pipeline.ClassifyAsync(item, ct);
                // Already where it belongs? Then there is nothing to do.
                if (!string.IsNullOrEmpty(d.TargetDirectory) && Path.GetFullPath(d.TargetDirectory).TrimEnd('\\').Equals(Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    d.Result.Action = "skip";
                results.Add(d);
            }
            catch (Exception ex) { Log.Error("scan " + item.Name, ex); }
            n++;
        }
        progress.Report((entries.Count, entries.Count, "done"));
        return results;
    }

    public void Dispose()
    {
        Paused = true;
        _timer?.Dispose();
        _shutdown.Cancel();
        // Let a move that is half-way through finish recording before the connection goes.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _processing) == 1 && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
        try { _indexLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        Store?.Dispose();
    }
}
