using System.Text.Json;
using LocalDocket.Core;
using LocalDocket.Extract;
using LocalDocket.Llm;

// docket classify <path...> [--no-llm] [--json] [--taxonomy file]
// docket scan <folder> [--no-llm] [--json] [--apply] [--taxonomy file]
// docket inbox [--apply]              process settled inbox items once (headless)
// docket undo <moveId>
// docket search <text>
// docket models
// docket index [folder] [--full] [--status]   crawl the roots (or one folder) into the chat index
// docket index --remove-sidecars                import any legacy <name>.filer.json files into docket.db and delete them
// docket chat <question...> [--no-llm]         RAG answer with numbered sources (--no-llm: matching files only)

var args0 = args.ToList();
string? Opt(string name) { var i = args0.IndexOf(name); if (i < 0 || i + 1 >= args0.Count) return null; var v = args0[i + 1]; args0.RemoveRange(i, 2); return v; }
bool Flag(string name) { var i = args0.IndexOf(name); if (i < 0) return false; args0.RemoveAt(i); return true; }

var taxPath = Opt("--taxonomy") ?? DocketPaths.TaxonomyPath;
var noLlm = Flag("--no-llm");
var asJson = Flag("--json");
var apply = Flag("--apply");
var noEmbed = Flag("--no-embed");
var fullIndex = Flag("--full");
var indexStatus = Flag("--status");
var removeSidecars = Flag("--remove-sidecars");
if (args0.Count == 0) { Console.WriteLine("usage: docket classify|scan|inbox|undo|search|models|index|chat|apply|revert ..."); return 1; }
var cmd = args0[0]; args0.RemoveAt(0);

var taxonomy = Taxonomy.Load(taxPath);
var settings = taxonomy.Settings;
var client = new OllamaClient(settings.OllamaUrl);
IClassifierBackend? backend = noLlm ? null : new OllamaClassifier(client, settings) { Log = m => Console.Error.WriteLine("  " + m) };
using var store = new Store(DocketPaths.DbPath);
var pipeline = new Pipeline(taxonomy, new ContentExtractor(), backend, store, new PipelineOptions { UseLlm = !noLlm, Embed = !noEmbed, Log = m => Console.Error.WriteLine("  ! " + m) });
var mover = new Mover(store);
var jsonOpts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

switch (cmd)
{
    case "models":
        Console.WriteLine(await client.IsUpAsync() ? string.Join("\n", await client.ModelsAsync()) : "Ollama not reachable at " + settings.OllamaUrl);
        return 0;

    case "chat":
    {
        var question = string.Join(" ", args0).Trim();
        if (question.Length == 0) { Console.WriteLine("chat: give a question"); return 1; }
        var up = !noLlm && await client.IsUpAsync();
        var vindex = VectorIndex.Load(store);
        Console.Error.WriteLine($"{vindex.Files} files / {vindex.Count} chunks in the index" + (up ? "" : "; Ollama unavailable → name matches only"));
        var chatBackend = up ? new OllamaChat(client, settings) : null;
        var chat = new ChatService(() => taxonomy, store, vindex, () => up ? backend : null, () => chatBackend, m => Console.Error.WriteLine("  " + m));
        var reply = await chat.AskAsync(question, Array.Empty<ChatTurn>());
        if (reply.Notice != null) Console.Error.WriteLine("  ! " + reply.Notice);
        foreach (var s in reply.Sources) Console.WriteLine($"[{s.N}] {s.Name}  ({s.Category ?? "-"}, {s.Score:0.00})  {s.Path}");
        if (reply.Sources.Count > 0) Console.WriteLine();
        await foreach (var tok in reply.Tokens) Console.Write(tok);
        Console.WriteLine();
        return 0;
    }

    case "index":
    {
        if (removeSidecars)
        {
            var before = store.CountFiledWithoutMeta();
            var res = await SidecarImport.RunAsync(taxonomy, store, delete: true, m => Console.Error.WriteLine("  " + m));
            Console.WriteLine(res);
            Console.WriteLine($"filed rows without extractor metadata: {before} → {store.CountFiledWithoutMeta()}");
            foreach (var e in res.Errors) Console.WriteLine("  ! " + e);
            return res.Errors.Count == 0 ? 0 : 2;
        }
        if (indexStatus)
        {
            var c = store.IndexCounts();
            Console.WriteLine($"{c.Files} files, {c.Chunks} chunks, embed model {store.GetKv("index.embed_model") ?? "-"}, last full crawl {store.GetKv("index.last_full_crawl") ?? "never"}, sidecars imported {store.GetKv(SidecarImport.DoneKey) ?? "no"}");
            return 0;
        }
        if (backend == null) { Console.WriteLine("index needs the embedding model (drop --no-llm)"); return 1; }
        if (!await client.IsUpAsync()) { Console.WriteLine("Ollama not reachable at " + settings.OllamaUrl); return 1; }
        var roots = args0.Count > 0 ? args0.Select(Path.GetFullPath).ToList() : taxonomy.Roots.Values.ToList();
        var vindex = new VectorIndex(); // the CLI only writes; the tray app loads vectors into memory
        var indexer = new Indexer(() => taxonomy, store, vindex, new ContentExtractor(), () => backend, m => Console.Error.WriteLine("  " + m));
        string? last = null;
        indexer.Changed += s =>
        {
            var line = s.Phase == "indexing" ? $"{s.Phase} {s.Done}/{s.Total}  {s.Current}" : s.Phase;
            if (line != last) { Console.Error.WriteLine(line); last = line; }
        };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IndexStatus st;
        if (store.GetKv(SidecarImport.DoneKey) == null) Console.Error.WriteLine((await SidecarImport.RunAsync(taxonomy, store, delete: false, m => Console.Error.WriteLine("  " + m))).ToString());
        try { st = await indexer.CrawlAsync(roots, fullIndex, prune: args0.Count == 0, cts.Token); }
        catch (OperationCanceledException) { Console.WriteLine("cancelled"); return 130; }
        Console.WriteLine($"{st.Files} files, {st.Chunks} chunks in {sw.Elapsed:m\\:ss}" + (st.LastError != null ? $"  (last error: {st.LastError})" : ""));
        return st.LastError == null ? 0 : 2;
    }

    case "classify":
    {
        if (args0.Count == 0) { Console.WriteLine("classify: give at least one path"); return 1; }
        var items = args0.Select(FileItem.FromPath).ToList();
        foreach (var item in items) await Report(item);
        return 0;
    }

    case "scan":
    case "inbox":
    {
        string folder;
        if (cmd == "inbox") folder = taxonomy.InboxPath;
        else { if (args0.Count == 0) { Console.WriteLine("scan: give a folder"); return 1; } folder = args0[0]; }
        if (!Directory.Exists(folder)) { Console.WriteLine("no such folder: " + folder); return 1; }
        var items = cmd == "inbox"
            ? Pipeline.SettledInboxItems(folder, settings.SettleSeconds)
            : taxonomy.ScannableEntries(folder).Select(FileItem.FromPath).ToList();
        var groups = Grouper.Group(items, settings.GroupWindowSeconds);
        Console.Error.WriteLine($"{items.Count} items in {groups.Count} groups");
        var decisions = new List<Decision>();
        foreach (var g in groups)
        {
            if (!asJson) Console.WriteLine($"\n## group {g.Id}: {g.Reason}");
            foreach (var item in g.Items) decisions.Add(await Report(item));
        }
        if (apply)
        {
            int moved = 0, skipped = 0;
            foreach (var d in decisions)
            {
                if (d.Result.NeedsUser(settings.AutoFileConfidence) || string.IsNullOrEmpty(d.TargetDirectory)) { skipped++; continue; }
                var r = mover.Move(d.Item, d.Result, d.TargetDirectory, Pipeline.EmbeddingOf(d.Item));
                Console.WriteLine($"moved #{r.MoveId}: {d.Item.Name} → {r.FinalPath}");
                moved++;
            }
            Console.WriteLine($"\napplied: {moved} moved, {skipped} left for the popup (need a question or below {settings.AutoFileConfidence:0.00})");
        }
        return 0;
    }

    case "undo":
    {
        if (args0.Count == 0 || !long.TryParse(args0[0], out var id)) { Console.WriteLine("undo <moveId>"); return 1; }
        Console.WriteLine("restored to " + mover.Undo(id));
        return 0;
    }

    case "search":
    {
        var text = string.Join(' ', args0);
        float[]? emb = null;
        if (backend != null) { try { emb = await backend.EmbedAsync(text); } catch { } }
        foreach (var (name, path, cat, score) in store.Search(text, emb))
            Console.WriteLine($"{score:0.00}  {cat,-28} {path}");
        return 0;
    }

    case "apply":
    {
        // apply <manifest.csv> [--out applied.csv]: columns path,category,environment,client,ticket,project,target,name,note
        if (args0.Count == 0) { Console.WriteLine("apply <manifest.csv>"); return 1; }
        var outPath = Opt("--out") ?? Path.ChangeExtension(args0[0], ".applied.csv");
        var rows = ReadCsv(args0[0]);
        var ollamaUp = backend != null && await client.IsUpAsync();
        var results = new List<string> { "moveId,fileId,path,finalPath,category,environment,client,note" };
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            var path = row["path"];
            if (!File.Exists(path) && !Directory.Exists(path)) { Console.WriteLine($"MISSING  {path}"); results.Add(Csv("", "", path, "", row.GetValueOrDefault("category", ""), "", "", "missing")); fail++; continue; }
            try
            {
                var item = FileItem.FromPath(path);
                try { await new ContentExtractor().ExtractAsync(item, settings); } catch { }
                float[]? emb = null;
                if (ollamaUp) { try { emb = await backend!.EmbedAsync(Pipeline.EmbedText(item)); } catch { } }
                var c = new Classification
                {
                    Category = row.GetValueOrDefault("category", ""),
                    Domain = row.GetValueOrDefault("category", "Work/").Split('/')[0],
                    Environment = Blank(row.GetValueOrDefault("environment", "")),
                    Client = Blank(row.GetValueOrDefault("client", "")),
                    Ticket = Blank(row.GetValueOrDefault("ticket", "")),
                    Project = Blank(row.GetValueOrDefault("project", "")),
                    Confidence = 1.0, DecidedBy = "apply:" + Path.GetFileNameWithoutExtension(args0[0]), Reasoning = row.GetValueOrDefault("note", "Bulk apply from " + Path.GetFileName(args0[0]))
                };
                var target = Blank(row.GetValueOrDefault("target", ""));
                if (target == null && !string.IsNullOrEmpty(c.Category)) target = PathTemplate.Resolve(taxonomy, c, item);
                if (target == null) { Console.WriteLine($"NOTARGET {path}"); fail++; continue; }
                var r = mover.Move(item, c, target, emb, Blank(row.GetValueOrDefault("name", "")));
                Console.WriteLine($"#{r.MoveId,-4} {item.Name} → {r.FinalPath}");
                results.Add(Csv(r.MoveId.ToString(), r.FileId, path, r.FinalPath, c.Category, c.Environment ?? "", c.Client ?? "", c.Reasoning));
                ok++;
            }
            catch (Exception ex) { Console.WriteLine($"FAILED   {path}: {ex.Message}"); results.Add(Csv("", "", path, "", "", "", "", "failed: " + ex.Message)); fail++; }
        }
        File.WriteAllLines(outPath, results);
        Console.WriteLine($"\n{ok} moved, {fail} failed. Results: {outPath}. Revert with: docket revert \"{outPath}\"");
        return fail == 0 ? 0 : 2;
    }

    case "revert":
    {
        if (args0.Count == 0) { Console.WriteLine("revert <applied.csv>"); return 1; }
        var rows = ReadCsv(args0[0]);
        int ok = 0, fail = 0;
        foreach (var row in rows.AsEnumerable().Reverse())
        {
            if (!long.TryParse(row.GetValueOrDefault("moveId", ""), out var id)) continue;
            try { Console.WriteLine($"#{id} → {mover.Undo(id)}"); ok++; }
            catch (Exception ex) { Console.WriteLine($"#{id} FAILED: {ex.Message}"); fail++; }
        }
        Console.WriteLine($"{ok} restored, {fail} failed");
        return 0;
    }

    default:
        Console.WriteLine("unknown command " + cmd);
        return 1;
}

static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
static string Csv(params string[] cells) => string.Join(",", cells.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));

static List<Dictionary<string, string>> ReadCsv(string path)
{
    var lines = File.ReadAllLines(path);
    if (lines.Length == 0) return new();
    var header = ParseCsvLine(lines[0]);
    var list = new List<Dictionary<string, string>>();
    foreach (var line in lines.Skip(1))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var cells = ParseCsvLine(line);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++) d[header[i]] = i < cells.Count ? cells[i] : "";
        list.Add(d);
    }
    return list;
}

static List<string> ParseCsvLine(string line)
{
    var cells = new List<string>();
    var sb = new System.Text.StringBuilder();
    bool q = false;
    for (int i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (q)
        {
            if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
            else if (ch == '"') q = false;
            else sb.Append(ch);
        }
        else if (ch == '"') q = true;
        else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
        else sb.Append(ch);
    }
    cells.Add(sb.ToString());
    return cells;
}

async Task<Decision> Report(FileItem item)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var d = await pipeline.ClassifyAsync(item);
    sw.Stop();
    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { item.Name, item.ContentKind, item.Meta, d.Result, d.TargetDirectory, ms = sw.ElapsedMilliseconds }, jsonOpts));
    }
    else
    {
        var r = d.Result;
        var flag = r.NeedsUser(settings.AutoFileConfidence) ? "ASK " : "AUTO";
        Console.WriteLine($"{flag} {r.Confidence:0.00}  {item.Name}");
        Console.WriteLine($"      → {(string.IsNullOrEmpty(r.Category) ? "(none)" : r.Category)}" +
                          (r.Environment != null ? $"  env={r.Environment}" : "") + (r.Client != null ? $"  client={r.Client}" : "") +
                          (r.Questions.Count > 0 ? $"  ask=[{string.Join(",", r.Questions)}]" : "") + $"  by {r.DecidedBy}  ({sw.ElapsedMilliseconds} ms)");
        if (!string.IsNullOrEmpty(d.TargetDirectory)) Console.WriteLine($"      dir {d.TargetDirectory}");
        Console.WriteLine($"      {r.Reasoning}");
    }
    return d;
}
