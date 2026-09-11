namespace LocalDocket.Core;

public static class DocketPaths
{
    const string Folder = "LocalDocket";
    const string LegacyFolder = "Filer";
    static string? _dataDir;

    /// <summary>
    /// %LOCALAPPDATA%\LocalDocket (LOCALDOCKET_DATA overrides; the old FILER_DATA is still honoured). The first start
    /// after the rename moves %LOCALAPPDATA%\Filer here and renames its files; if that folder is locked by an older
    /// instance the old location keeps being used until next start.
    /// </summary>
    public static string DataDir => _dataDir ??= Resolve();

    static string Resolve()
    {
        var env = System.Environment.GetEnvironmentVariable("LOCALDOCKET_DATA") ?? System.Environment.GetEnvironmentVariable("FILER_DATA");
        if (!string.IsNullOrEmpty(env)) return env;
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, Folder);
        var legacy = Path.Combine(local, LegacyFolder);
        if (!Directory.Exists(dir) && Directory.Exists(legacy))
        {
            try { Directory.Move(legacy, dir); }
            catch { return legacy; }
        }
        if (Directory.Exists(dir))
        {
            foreach (var (from, to) in new[] { ("filer.db", "docket.db"), ("filer.db-wal", "docket.db-wal"), ("filer.db-shm", "docket.db-shm"), ("filer.log", "docket.log"), ("filer.log.1", "docket.log.1") })
            {
                var a = Path.Combine(dir, from); var b = Path.Combine(dir, to);
                if (File.Exists(a) && !File.Exists(b)) { try { File.Move(a, b); } catch { } }
            }
        }
        return dir;
    }

    static string Pick(string preferred, string legacy)
    {
        var p = Path.Combine(DataDir, preferred);
        var l = Path.Combine(DataDir, legacy);
        return !File.Exists(p) && File.Exists(l) ? l : p;
    }

    public static string DbPath => Pick("filer.db", "docket.db");
    public static string LogPath => Pick("filer.log", "docket.log");

    /// <summary>LOCALDOCKET_TAXONOMY (or FILER_TAXONOMY) env var, else DataDir\taxonomy.yaml (seeded from the copy shipped next to the exe).</summary>
    public static string TaxonomyPath
    {
        get
        {
            var env = System.Environment.GetEnvironmentVariable("LOCALDOCKET_TAXONOMY") ?? System.Environment.GetEnvironmentVariable("FILER_TAXONOMY");
            if (!string.IsNullOrEmpty(env)) return env;
            var local = Path.Combine(DataDir, "taxonomy.yaml");
            if (!File.Exists(local))
            {
                var shipped = Path.Combine(AppContext.BaseDirectory, "taxonomy.yaml");
                Directory.CreateDirectory(DataDir);
                if (File.Exists(shipped)) File.Copy(shipped, local);
            }
            return local;
        }
    }
}

public sealed class PipelineOptions
{
    public bool UseLlm { get; set; } = true;
    public bool Embed { get; set; } = true;
    public bool Hash { get; set; } = true;
    public Action<string>? Log { get; set; }
}

/// <summary>Extract → rules → model → decision, shared by the CLI, the watcher and Scan mode.</summary>
public sealed class Pipeline
{
    readonly Taxonomy _taxonomy;
    readonly IContentExtractor _extractor;
    readonly IClassifierBackend? _backend;
    readonly Store? _store;
    readonly PipelineOptions _opt;

    public Pipeline(Taxonomy taxonomy, IContentExtractor extractor, IClassifierBackend? backend, Store? store, PipelineOptions? options = null)
    {
        _taxonomy = taxonomy; _extractor = extractor; _backend = backend; _store = store; _opt = options ?? new();
    }

    public Taxonomy Taxonomy => _taxonomy;
    public DocketSettings Settings => _taxonomy.Settings;

    public async Task<Decision> ClassifyAsync(FileItem item, CancellationToken ct = default)
    {
        var log = _opt.Log ?? (_ => { });
        try { await _extractor.ExtractAsync(item, _taxonomy.Settings, ct); }
        catch (Exception ex) { log($"extract failed for {item.Name}: {ex.Message}"); }

        Classification? result = RuleEngine.Evaluate(item, _taxonomy);
        var hint = result == null ? RuleEngine.Hint(item, _taxonomy) : null;
        float[]? embedding = null;

        if (result == null && _opt.UseLlm && _backend != null)
        {
            var examples = new List<PastDecision>();
            var isImage = item.Meta.ContainsKey(IClassifierBackend.ImageMetaKey);
            if (_store != null && _opt.Embed && !isImage)
            {
                try
                {
                    embedding = await _backend.EmbedAsync(EmbedText(item), ct);
                    if (embedding != null) examples = _store.Nearest(embedding);
                }
                catch (Exception ex) { log($"embed failed: {ex.Message}"); }
            }
            try
            {
                result = await _backend.ClassifyAsync(item, _taxonomy, hint, examples, ct);
                if (result != null)
                {
                    var norm = _taxonomy.NormalizeCategory(result.Category, result.Domain);
                    if (norm == null)
                    {
                        log($"model category '{result.Category}' unknown → falling back to hint/popup");
                        result.Confidence = Math.Min(result.Confidence, 0.4);
                        result.Category = hint?.Category ?? "";
                    }
                    else result.Category = norm;
                    if (!string.IsNullOrEmpty(result.Category)) result.Domain = _taxonomy.DomainOf(result.Category);
                    if (isImage && string.IsNullOrWhiteSpace(result.ImageDescription) && result.Reasoning.StartsWith("The image", StringComparison.OrdinalIgnoreCase))
                        result.ImageDescription = result.Reasoning;
                    if (!string.IsNullOrWhiteSpace(result.ImageDescription))
                    {
                        item.Meta["image.description"] = result.ImageDescription!;
                        item.ContentText = "Image: " + result.ImageDescription + "\n" + (item.ContentText ?? "");
                        result.Reasoning = "Image shows: " + result.ImageDescription.TrimEnd('.') + ". " + result.Reasoning;
                    }
                    if (isImage && _store != null && _opt.Embed)
                    {
                        try { embedding = await _backend.EmbedAsync(EmbedText(item), ct); } catch (Exception ex) { log($"embed failed: {ex.Message}"); }
                    }
                    RuleEngine.ApplyBakInference(item, _taxonomy, result);
                    FillKnownClient(item, result);
                    RuleEngine.ApplyCategoryAsks(_taxonomy, result);
                    // A hint from a specific name glob beats a low-confidence model guess.
                    if (hint != null && result.Confidence < 0.5 && hint.Confidence >= 0.7 && string.IsNullOrEmpty(result.Category))
                        result = hint;
                }
            }
            catch (Exception ex) { log($"model failed: {ex.Message}"); }
        }

        result ??= hint ?? new Classification { Confidence = 0, Reasoning = "No rule, hint or model answer.", DecidedBy = "none" };
        var decision = new Decision { Item = item, Result = result };
        if (!string.IsNullOrEmpty(result.Category) && result.Action != "skip")
        {
            try { decision.TargetDirectory = PathTemplate.Resolve(_taxonomy, result, item); }
            catch (Exception ex) { log($"path resolve failed: {ex.Message}"); }
        }
        item.Meta["_embedding"] = embedding == null ? "" : Convert.ToBase64String(Store.ToBytes(embedding));
        item.Meta.Remove(IClassifierBackend.ImageMetaKey); // large; never persisted
        return decision;
    }

    /// <summary>The model tends to name the client in its reasoning but leave the field empty; recover it from the known list.</summary>
    void FillKnownClient(FileItem item, Classification c)
    {
        if (!string.IsNullOrEmpty(c.Client)) { c.Client = MatchClient(c.Client) ?? c.Client; return; }
        var wantsClient = !string.IsNullOrEmpty(c.Category) && _taxonomy.Categories.TryGetValue(c.Category, out var def) && def.Ask.Contains("client", StringComparer.OrdinalIgnoreCase);
        if (!wantsClient) return;
        if (!_taxonomy.Questions.TryGetValue("client", out var q)) return;
        var hay = item.Name + " " + c.Reasoning;
        foreach (var opt in q.Options.OrderByDescending(o => o.Length))
        {
            if (opt.Length < 3) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(hay, @"(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(opt) + @"(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { c.Client = opt; return; }
        }
    }

    string? MatchClient(string raw) =>
        _taxonomy.Questions.TryGetValue("client", out var q) ? q.Options.FirstOrDefault(o => o.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase)) : null;

    public static float[]? EmbeddingOf(FileItem item) =>
        item.Meta.TryGetValue("_embedding", out var b64) && b64.Length > 0 ? Store.FromBytes(Convert.FromBase64String(b64)) : null;

    public static string EmbedText(FileItem item)
    {
        var head = item.ContentText == null ? "" : item.ContentText.Length > 1500 ? item.ContentText[..1500] : item.ContentText;
        return $"{item.Name}\n{item.ContentKind}\n{head}";
    }

    public Task<float[]?> EmbedForSearchAsync(string text, CancellationToken ct = default) =>
        _backend == null ? Task.FromResult<float[]?>(null) : _backend.EmbedAsync(text, ct);

    /// <summary>Re-resolve the target after the user changed the classification.</summary>
    public void Retarget(Decision d)
    {
        d.TargetDirectory = string.IsNullOrEmpty(d.Result.Category) || d.Result.Action == "skip" ? "" : PathTemplate.Resolve(_taxonomy, d.Result, d.Item);
    }

    /// <summary>Items in the inbox that have settled (not changed for settleSeconds and not locked).</summary>
    public static List<FileItem> SettledInboxItems(string inbox, int settleSeconds, ISet<string>? ignore = null)
    {
        var list = new List<FileItem>();
        if (!Directory.Exists(inbox)) return list;
        var cutoff = DateTime.Now.AddSeconds(-settleSeconds);
        foreach (var path in Directory.EnumerateFileSystemEntries(inbox))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".filer.json", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (ignore != null && ignore.Contains(path)) continue;
            try
            {
                var item = FileItem.FromPath(path);
                var touched = item.IsDirectory ? LatestWrite(path) : item.Modified;
                if (touched > cutoff || item.Created > cutoff) continue;
                if (!item.IsDirectory && IsLocked(path)) continue;
                item.DroppedAt = item.Created;
                list.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A reserved device name or an unreadable entry must not stop the whole inbox; it simply stays where it is.
            }
        }
        return list;
    }

    static DateTime LatestWrite(string dir)
    {
        var latest = Directory.GetLastWriteTime(dir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var w = File.GetLastWriteTime(f);
                if (w > latest) latest = w;
            }
        }
        catch { }
        return latest;
    }

    public static bool IsLocked(string path)
    {
        try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
