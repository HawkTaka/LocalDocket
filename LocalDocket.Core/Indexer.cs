using System.Threading.Channels;

namespace LocalDocket.Core;

public sealed record IndexStatus(string Phase, int Files, int Chunks, int Done, int Total, string? Current, DateTime? LastFullCrawl, string? LastError)
{
    public static readonly IndexStatus Idle = new("idle", 0, 0, 0, 0, null, null, null);
}

/// <summary>
/// Crawls the taxonomy roots (minus exclusions and repositories), extracts full text, chunks and embeds it, and keeps
/// <see cref="Store"/> and <see cref="VectorIndex"/> in step. Change detection is size + last-write time. Runs as a
/// background loop in the App (yielding to inbox work through <see cref="Busy"/>) or once from the CLI.
/// </summary>
public sealed class Indexer
{
    readonly Func<Taxonomy> _taxonomy;
    readonly Store _store;
    readonly VectorIndex _index;
    readonly IContentExtractor _extractor;
    readonly Func<IEmbedder?> _embedder;
    readonly Action<string> _log;
    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _fullRequested;
    IndexStatus _status = IndexStatus.Idle;
    DateTime _lastStatusPush = DateTime.MinValue;

    static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store",
        // Reserved device names: Win32 cannot open them even when a stray file carries the name.
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public Indexer(Func<Taxonomy> taxonomy, Store store, VectorIndex index, IContentExtractor extractor, Func<IEmbedder?> embedder, Action<string>? log = null)
    {
        _taxonomy = taxonomy; _store = store; _index = index; _extractor = extractor; _embedder = embedder; _log = log ?? (_ => { });
    }

    /// <summary>When true the crawler waits (the host sets this while the inbox is being classified or watching is paused).</summary>
    public Func<bool>? Busy { get; set; }
    public bool Paused { get; set; }
    public event Action<IndexStatus>? Changed;
    public IndexStatus Status => _status;
    /// <summary>Set by the crawl when the embedder was unreachable; the loop retries sooner in that case.</summary>
    public bool EmbedderDown { get; private set; }

    /// <summary>Index (or re-check) one path soon: a file or a folder that was just filed or undone.</summary>
    public void Enqueue(string path) => _queue.Writer.TryWrite(path);

    public void RequestCrawl(bool full = false)
    {
        if (full) _fullRequested = true;
        Volatile.Read(ref _wake).TrySetResult();
    }

    /// <summary>Optional hook awaited before each crawl attempt (the host re-probes Ollama here so a late start is noticed).</summary>
    public Func<CancellationToken, Task>? BeforeCrawl { get; set; }

    /// <summary>Background loop: full crawl, then re-crawl on the interval / on request, draining enqueued paths in between.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_store.GetKv(SidecarImport.DoneKey) == null)
            {
                try { await SidecarImport.RunAsync(_taxonomy(), _store, delete: false, _log, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log("sidecar import failed: " + ex.Message); }
            }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!Paused)
                    {
                        if (BeforeCrawl != null) await BeforeCrawl(ct);
                        var full = _fullRequested; _fullRequested = false;
                        await CrawlAsync(_taxonomy().Roots.Values, full, prune: true, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log("index crawl failed: " + ex.Message); Publish(_status with { Phase = "idle", LastError = ex.Message }, force: true); }

                try { await WaitForNextPassAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log("index wait failed: " + ex.Message); await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _log(ct.IsCancellationRequested ? "index loop stopped (shutdown)" : "index loop stopped unexpectedly");
            if (!ct.IsCancellationRequested) Publish(_status with { Phase = "idle", LastError = "index loop stopped; restart Local Docket" }, force: true);
        }
    }

    /// <summary>Sleep until the re-crawl interval elapses or a crawl is requested; enqueued paths are indexed as they arrive (unless paused).</summary>
    async Task WaitForNextPassAsync(CancellationToken ct)
    {
        var settings = _taxonomy().Settings;
        var wait = TimeSpan.FromMinutes(EmbedderDown ? Math.Min(5, Math.Max(1, settings.IndexRecrawlMinutes)) : Math.Max(1, settings.IndexRecrawlMinutes));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(wait, timeoutCts.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wake = Volatile.Read(ref _wake).Task;
                if (wake.IsCompleted) { ResetWake(); return; }
                var queued = _queue.Reader.WaitToReadAsync(ct).AsTask();
                var done = await Task.WhenAny(wake, queued, timeout);
                if (done == timeout) return;
                if (done == wake) { ResetWake(); return; }
                if (Paused) { await Task.Delay(TimeSpan.FromSeconds(2), ct); continue; } // leave it queued, no busy loop
                await DrainQueueAsync(ct);
            }
        }
        finally { timeoutCts.Cancel(); }
    }

    void ResetWake() => Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    async Task DrainQueueAsync(CancellationToken ct)
    {
        var paths = new List<string>();
        while (_queue.Reader.TryRead(out var p)) paths.Add(p);
        if (paths.Count == 0) return;
        try { await CrawlAsync(paths, full: false, prune: false, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log("index enqueue failed: " + ex.Message); }
    }

    /// <summary>
    /// One pass over the given roots (or single files). With <paramref name="prune"/>, rows whose file has gone or that
    /// now sit in an excluded folder are removed (crawled rows) or have their chunks dropped (filed rows keep provenance).
    /// </summary>
    public async Task<IndexStatus> CrawlAsync(IEnumerable<string> roots, bool full, bool prune, CancellationToken ct)
    {
        var taxonomy = _taxonomy();
        var settings = taxonomy.Settings;
        var embedder = _embedder();
        var counts = _store.IndexCounts();
        if (embedder == null)
        {
            EmbedderDown = true;
            Publish(new IndexStatus("idle", counts.Files, counts.Chunks, 0, 0, null, LastCrawl(), "embedding model unavailable"), force: true);
            return _status;
        }
        EnsureEmbedModel(settings.EmbedModel);

        Publish(new IndexStatus("scanning", counts.Files, counts.Chunks, 0, 0, null, LastCrawl(), null), force: true);
        if (prune && _index.Ready && counts.Chunks != _index.Count)
        {
            // Another process (the CLI) wrote or removed chunks: rebuild the in-memory vectors from the store.
            _log($"index: store has {counts.Chunks} chunks, memory has {_index.Count}; reloading vectors");
            _index.Clear();
            _store.LoadChunkVectors((fid, ord, vec) => _index.Add(fid, ord, vec));
        }
        var snapshot = _store.IndexSnapshot();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new List<FileInfo>();
        var rootList = roots.ToList();
        var presentRoots = rootList.Where(r => File.Exists(r) || Directory.Exists(r)).ToList(); // an unmounted root must not look "empty"
        foreach (var root in rootList)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(root)) Consider(new FileInfo(root), snapshot, seen, todo, full, settings, taxonomy);
            else if (Directory.Exists(root)) Walk(new DirectoryInfo(root), isRoot: true, taxonomy, settings, snapshot, seen, todo, full, ct);
        }

        int done = 0; string? lastError = null;
        var skipExts = new HashSet<string>(settings.IndexSkipExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var fi in todo)
        {
            ct.ThrowIfCancellationRequested();
            while (Busy?.Invoke() == true || Paused) await Task.Delay(1000, ct);
            Publish(new IndexStatus("indexing", counts.Files, counts.Chunks, done, todo.Count, fi.Name, LastCrawl(), lastError));
            try
            {
                var r = await IndexFileAsync(fi, snapshot, embedder, taxonomy, settings, skipExts, ct);
                if (r.Chunks > 0) counts = (counts.Files + (r.IsNew ? 1 : 0), counts.Chunks + r.Chunks - r.PreviousChunks);
                else if (r.IsNew) counts = (counts.Files + 1, counts.Chunks - r.PreviousChunks);
                else counts = (counts.Files, counts.Chunks - r.PreviousChunks);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (EmbedderUnavailableException ex)
            {
                EmbedderDown = true; lastError = ex.Message;
                _log("index: embedding unavailable, will retry: " + ex.Message);
                break;
            }
            catch (Exception ex)
            {
                lastError = $"{fi.Name}: {ex.Message}";
                _log("index failed " + fi.FullName + ": " + ex.Message);
                try { _store.MarkIndexError(snapshot.TryGetValue(fi.FullName, out var e) ? e.Id : null, fi.FullName, ex.Message); } catch { }
            }
            done++;
            if (settings.IndexThrottleMs > 0) await Task.Delay(settings.IndexThrottleMs, ct);
        }
        if (lastError == null) EmbedderDown = false;

        if (prune && !EmbedderDown)
        {
            Publish(new IndexStatus("pruning", counts.Files, counts.Chunks, done, todo.Count, null, LastCrawl(), lastError), force: true);
            var removedAny = false;
            foreach (var (path, entry) in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Contains(path)) continue;
                if (!presentRoots.Any(r => Under(path, r))) continue; // outside what this pass looked at, or under a root that is not there right now
                var exists = File.Exists(path) || Directory.Exists(path);
                if (exists && taxonomy.IsIndexable(path, out _) && !SkippedCategory(entry.Category, settings) && !IsUnderSkippedRepo(path, rootList, taxonomy, settings)) continue; // e.g. filed folder rows, oversized files
                // Conditional on the row still being where the snapshot saw it: an item filed during this crawl keeps its new record.
                if (entry.Source == "filed")
                {
                    if (entry.Chunks > 0 && _store.ClearChunks(entry.Id, exists ? "not indexed" : "missing", entry.Path) > 0) { _index.RemoveFile(entry.Id); counts = (counts.Files, counts.Chunks - entry.Chunks); removedAny = true; }
                }
                else if (_store.DeleteFile(entry.Id, entry.Path) > 0)
                {
                    _index.RemoveFile(entry.Id);
                    counts = (counts.Files - 1, counts.Chunks - entry.Chunks);
                    removedAny = entry.Chunks > 0 || removedAny;
                }
            }
            if (removedAny) _store.Vacuum(); // freed pages are zeroed (secure_delete) and returned, so pruned text does not linger on disk
            if (rootList.Count == presentRoots.Count) _store.SetKv("index.last_full_crawl", DateTime.Now.ToString("o")); // per-file errors are recorded on their rows
        }
        _log($"index pass done: {done}/{todo.Count} files processed, {counts.Files} files / {counts.Chunks} chunks indexed" + (lastError != null ? ", last error: " + lastError : ""));
        var final = _store.IndexCounts();
        Publish(new IndexStatus("idle", final.Files, final.Chunks, done, todo.Count, null, LastCrawl(), lastError), force: true);
        return _status;
    }

    sealed record FileResult(bool IsNew, int Chunks, int PreviousChunks);

    async Task<FileResult> IndexFileAsync(FileInfo fi, Dictionary<string, IndexEntry> snapshot, IEmbedder embedder, Taxonomy taxonomy, DocketSettings settings, HashSet<string> skipExts, CancellationToken ct)
    {
        snapshot.TryGetValue(fi.FullName, out var known);
        var id = known?.Id;
        var previousChunks = known?.Chunks ?? 0;
        if (id == null)
        {
            // A filed item that was moved by hand keeps its row when name, size and hash agree.
            var orphan = _store.FindOrphanFiled(fi.Name, fi.Length).FirstOrDefault(o => !File.Exists(o.Path) && !Directory.Exists(o.Path) && o.Sha256 != null && o.Sha256 == SafeHash(fi.FullName));
            if (orphan != null) { id = orphan.Id; previousChunks = orphan.Chunks; _log($"index: re-linked {fi.Name} → {fi.FullName}"); }
        }
        id ??= Guid.NewGuid().ToString("N");

        var item = FileItem.FromPath(fi.FullName);
        var chunks = new List<IndexChunk>();
        string? error = null;
        var retryLater = false;
        if (!skipExts.Contains(item.Extension) && fi.Length <= (long)settings.IndexMaxFileMB * 1024 * 1024)
        {
            try { await _extractor.ExtractAsync(item, settings, ct, settings.IndexCapBytesPerFile, attachImage: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "extract: " + ex.Message; retryLater = true; } // locked by Word / sync client: look again next crawl
            catch (Exception ex) { error = "extract: " + ex.Message; }
            item.Meta.Remove(IClassifierBackend.ImageMetaKey);
            if (item.Meta.TryGetValue("extract.error", out var xerr) && (xerr.Contains("being used by another process") || xerr.Contains("denied", StringComparison.OrdinalIgnoreCase))) { error ??= "extract: " + xerr; retryLater = true; }
            var text = item.ContentText;
            // "text?" is the extractor's guess for an unknown extension: good enough to classify, not something to index verbatim (keys, dumps).
            if (!string.IsNullOrWhiteSpace(text) && item.ContentKind != "binary" && item.ContentKind != "text?")
            {
                var pieces = Chunker.Split(text, settings.IndexChunkChars, settings.IndexChunkOverlapChars);
                if (pieces.Count > settings.IndexMaxChunksPerFile) pieces = pieces.Take(settings.IndexMaxChunksPerFile).ToList();
                if (settings.IndexSkipSensitiveChunks)
                {
                    var before = pieces.Count;
                    pieces = pieces.Where(p => !SecretScan.IsSensitive(p)).ToList();
                    if (pieces.Count < before) error = $"withheld {before - pieces.Count} chunk(s) that look like credentials";
                }
                var prefix = settings.EmbedModel.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_document: " : "";
                var inputs = pieces.Select(p => prefix + item.Name + "\n" + p).ToList();
                var vectors = new List<float[]>(inputs.Count);
                for (int i = 0; i < inputs.Count; i += Math.Max(1, settings.IndexEmbedBatch))
                {
                    var batch = inputs.Skip(i).Take(Math.Max(1, settings.IndexEmbedBatch)).ToList();
                    float[][] got;
                    try { got = await embedder.EmbedBatchAsync(batch, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    // Includes the HttpClient timeout, which surfaces as a cancellation that is not ours.
                    catch (Exception ex) { throw new EmbedderUnavailableException(ex is OperationCanceledException ? "embedding request timed out" : ex.Message, ex); }
                    if (got.Length != batch.Count) throw new EmbedderUnavailableException($"embedding returned {got.Length} vectors for {batch.Count} inputs");
                    vectors.AddRange(got);
                }
                for (int i = 0; i < pieces.Count; i++) chunks.Add(new IndexChunk(i, pieces[i], vectors[i]));
            }
        }
        else if (skipExts.Contains(item.Extension)) item.ContentKind ??= "skipped";
        else error = "too large";

        var (domain, category) = taxonomy.CategoryForPath(fi.FullName);
        var snippet = item.ContentText == null ? null : item.ContentText.Length > 600 ? item.ContentText[..600] : item.ContentText;
        _store.UpsertIndexed(id, fi.FullName, fi.Name, fi.Length, fi.LastWriteTimeUtc, item.ContentKind, domain, category, snippet,
            item.Meta.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value), chunks, error, retryLater);
        _index.ReplaceFile(id, chunks.Select(c => c.Embedding).ToList());
        return new FileResult(known == null, chunks.Count, previousChunks);
    }

    void Walk(DirectoryInfo dir, bool isRoot, Taxonomy taxonomy, DocketSettings settings, Dictionary<string, IndexEntry> snapshot, HashSet<string> seen, List<FileInfo> todo, bool full, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isRoot)
        {
            if ((dir.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return;
            if (taxonomy.Exclusions.IsExcluded(dir.FullName)) return;
            if (settings.IndexSkipRepos && Exclusions.IsRepository(dir.FullName, settings.AtomicMarkers)) return;
        }
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("index: cannot read " + dir.FullName + ": " + ex.Message); return; }
        foreach (var e in entries)
        {
            if (e is DirectoryInfo d) Walk(d, false, taxonomy, settings, snapshot, seen, todo, full, ct);
            else if (e is FileInfo f) Consider(f, snapshot, seen, todo, full, settings, taxonomy);
        }
    }

    void Consider(FileInfo f, Dictionary<string, IndexEntry> snapshot, HashSet<string> seen, List<FileInfo> todo, bool full, DocketSettings settings, Taxonomy taxonomy)
    {
        if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return;
        if (SkipNames.Contains(f.Name) || SkipNames.Contains(Path.GetFileNameWithoutExtension(f.Name)) || f.Name.StartsWith("~$") || f.Name.EndsWith(".filer.json", StringComparison.OrdinalIgnoreCase)) return;
        if (!taxonomy.IsIndexable(f.FullName, out _)) return;
        // A filed item carries its decided category even when it sits outside that category's template path (hand-typed target).
        if (snapshot.TryGetValue(f.FullName, out var row) && SkippedCategory(row.Category, settings)) return;
        seen.Add(f.FullName);
        if (!full && snapshot.TryGetValue(f.FullName, out var known) && known.Size == f.Length && known.MtimeUtc.HasValue && Math.Abs((known.MtimeUtc.Value - f.LastWriteTimeUtc).TotalSeconds) < 1)
            return;
        todo.Add(f);
    }

    static bool SkippedCategory(string? category, DocketSettings s) =>
        category != null && s.IndexSkipCategories.Any(c => category.Equals(c, StringComparison.OrdinalIgnoreCase) || category.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));

    static bool IsUnderSkippedRepo(string path, List<string> roots, Taxonomy taxonomy, DocketSettings settings)
    {
        if (!settings.IndexSkipRepos) return false;
        var dir = Path.GetDirectoryName(path);
        while (dir != null && roots.Any(r => Under(dir, r) && !dir.Equals(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            if (Exclusions.IsRepository(dir, settings.AtomicMarkers)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    static bool Under(string path, string root)
    {
        var r = root.TrimEnd('\\');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    void EnsureEmbedModel(string model)
    {
        var stored = _store.GetKv("index.embed_model");
        if (stored != null && !stored.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            _log($"index: embed model changed {stored} → {model}, dropping all chunks");
            _store.ClearAllChunks();
            _index.Clear();
        }
        if (stored != model) _store.SetKv("index.embed_model", model);
    }

    DateTime? LastCrawl() => DateTime.TryParse(_store.GetKv("index.last_full_crawl"), out var d) ? d : null;

    static string? SafeHash(string path) { try { return Mover.Hash(path); } catch { return null; } }

    void Publish(IndexStatus s, bool force = false)
    {
        _status = s;
        var now = DateTime.UtcNow;
        if (!force && (now - _lastStatusPush).TotalMilliseconds < 1000) return;
        _lastStatusPush = now;
        try { Changed?.Invoke(s); } catch { }
    }

    public sealed class EmbedderUnavailableException : Exception
    {
        public EmbedderUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
