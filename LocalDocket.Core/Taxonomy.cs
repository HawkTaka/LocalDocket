using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LocalDocket.Core;

public sealed class DocketSettings
{
    public double AutoFileConfidence { get; set; } = 0.85;
    public double EscalateBelow { get; set; } = 0.60;
    public int SettleSeconds { get; set; } = 5;
    public int GroupWindowSeconds { get; set; } = 90;
    public int ContentCapBytesPerFile { get; set; } = 4096;
    public string Model { get; set; } = "qwen3.5:9b";
    public string? EscalationModel { get; set; }
    public string EmbedModel { get; set; } = "nomic-embed-text";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string KeepAlive { get; set; } = "10m";
    public bool Thinking { get; set; } = false;
    /// <summary>Attach a downscaled copy of image files to the model request (needs a vision-capable model).</summary>
    public bool ImageVision { get; set; } = true;
    /// <summary>One or two sentences about whose documents these are and what they work on; the classifier reads it before every decision.</summary>
    public string Profile { get; set; } = "You file documents for the person who owns these folders.";
    public int ImageMaxEdge { get; set; } = 1024;
    public List<string> AtomicMarkers { get; set; } = new() { ".git", "*.sln", "*.csproj" };
    /// <summary>Folders Scan mode and the indexer never enter, on top of <see cref="Exclusions.BuiltIn"/>. See <see cref="Exclusions"/> for pattern shapes.</summary>
    public List<string> ExcludeFolders { get; set; } = new();
    /// <summary>Skip any folder that holds an <see cref="AtomicMarkers"/> hit (a repository / project) when indexing, wherever it sits.</summary>
    public bool IndexSkipRepos { get; set; } = true;
    /// <summary>How much text the indexer extracts per file (the classifier keeps using ContentCapBytesPerFile).</summary>
    public int IndexCapBytesPerFile { get; set; } = 200_000;
    public int IndexChunkChars { get; set; } = 3200;
    public int IndexChunkOverlapChars { get; set; } = 480;
    public int IndexMaxChunksPerFile { get; set; } = 64;
    public int IndexMaxFileMB { get; set; } = 64;
    public int IndexEmbedBatch { get; set; } = 16;
    public int IndexThrottleMs { get; set; } = 50;
    public int IndexRecrawlMinutes { get; set; } = 60;
    /// <summary>Model for the chat window; null → <see cref="Model"/>.</summary>
    public string? ChatModel { get; set; }
    public int ChatNumCtx { get; set; } = 16384;
    /// <summary>Character budget for excerpts in one answer (≈ 6k tokens).</summary>
    public int ChatContextChars { get; set; } = 24000;
    public int ChatTopK { get; set; } = 24;
    /// <summary>Ask the model to turn the question into metadata filters (client, category, dates) before retrieval.</summary>
    public bool ChatExtractFilters { get; set; } = true;
    public List<string> IndexSkipExtensions { get; set; } = new()
    {
        ".exe", ".dll", ".pdb", ".iso", ".vhdx", ".msi", ".nupkg", ".mp4", ".mkv", ".mov", ".bak", ".trn", ".db", ".sqlite", ".kdbx",
        ".pem", ".key", ".ppk", ".pfx", ".p12", ".cer", ".crt", ".jks", ".keystore", ".ovpn", ".tfstate",
    };
    /// <summary>File-name globs never indexed for chat (credentials and config that tends to hold them). Matched against the name only.</summary>
    public List<string> IndexSkipNames { get; set; } = new()
    {
        ".env", ".env.*", "id_rsa*", "id_ed25519*", "id_ecdsa*", "id_dsa*", "known_hosts", "*.secrets*", "secrets.*", "appsettings*.json",
        "web.config", "*credential*", "*password*", "*.htpasswd", ".netrc", "_netrc", ".npmrc", ".pypirc", "*.kdb",
    };
    /// <summary>Categories whose files are never chunked for chat (the row keeps its provenance). HR and payroll by default.</summary>
    public List<string> IndexSkipCategories { get; set; } = new() { "Work/HR", "Work/Team" };
    /// <summary>Withhold a chunk from the index when it looks like credentials (private keys, connection strings, tokens).</summary>
    public bool IndexSkipSensitiveChunks { get; set; } = true;
    /// <summary>SQL Server host name (as found in a .bak header) → environment.</summary>
    public Dictionary<string, string> BakServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Regexes over a .bak's logical file / database names → environment.</summary>
    public Dictionary<string, string> BakNamePatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QuestionDef
{
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public bool AllowAdd { get; set; }
}

public sealed class CategoryDef
{
    public string Path { get; set; } = "";
    public List<string> Match { get; set; } = new();
    public List<string> Ask { get; set; } = new();
    public string? Hint { get; set; }
    public string? Notes { get; set; }
    public List<string> Frozen { get; set; } = new();
}

public sealed class RuleDef
{
    public string Name { get; set; } = "";
    public Dictionary<string, object> When { get; set; } = new();
    public Dictionary<string, string> Set { get; set; } = new();
    public List<string> Ask { get; set; } = new();
    public string? Notes { get; set; }
}

public sealed class Taxonomy
{
    public Dictionary<string, string> Roots { get; set; } = new();
    public string Inbox { get; set; } = "";
    public DocketSettings Settings { get; set; } = new();
    public Dictionary<string, QuestionDef> Questions { get; set; } = new();
    public Dictionary<string, CategoryDef> Categories { get; set; } = new();
    public List<RuleDef> Rules { get; set; } = new();

    [YamlIgnore] public string SourcePath { get; private set; } = "";
    /// <summary>Keys under <c>settings:</c> that match no <see cref="DocketSettings"/> property (typos would otherwise silently do nothing).</summary>
    [YamlIgnore] public List<string> UnknownSettings { get; private set; } = new();

    Exclusions? _exclusions;
    /// <summary>Exclusion matcher for the current settings and roots (rebuilt when the exclusion list changes).</summary>
    [YamlIgnore] public Exclusions Exclusions => _exclusions ??= new Exclusions(Settings.ExcludeFolders, Roots.Values);

    public string InboxPath => System.Environment.ExpandEnvironmentVariables(Inbox);

    public string RootFor(string domain) =>
        Roots.TryGetValue(domain, out var r) ? r : throw new InvalidOperationException($"No root for domain '{domain}'");

    public IEnumerable<string> CategoryNames => Categories.Keys.Where(k => !k.EndsWith("/Keep"));

    public string DomainOf(string category) => category.Split('/')[0];

    /// <summary>Snap a model-supplied category name onto a real one ("SQL/Scripts" → "Work/SQL/Scripts").</summary>
    public string? NormalizeCategory(string? raw, string? domainHint = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var r = raw.Trim().Replace('\\', '/').Trim('/');
        if (Categories.ContainsKey(r)) return r;
        var ci = Categories.Keys.FirstOrDefault(k => k.Equals(r, StringComparison.OrdinalIgnoreCase));
        if (ci != null) return ci;
        var suffix = Categories.Keys.Where(k => k.EndsWith("/" + r, StringComparison.OrdinalIgnoreCase)).ToList();
        if (suffix.Count == 1) return suffix[0];
        if (suffix.Count > 1 && domainHint != null)
        {
            var d = suffix.FirstOrDefault(k => k.StartsWith(domainHint + "/", StringComparison.OrdinalIgnoreCase));
            if (d != null) return d;
        }
        var last = r.Split('/').Last();
        var lastMatch = Categories.Keys.Where(k => k.Split('/').Last().Equals(last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (lastMatch.Count == 1) return lastMatch[0];
        if (lastMatch.Count > 1 && domainHint != null)
            return lastMatch.FirstOrDefault(k => k.StartsWith(domainHint + "/", StringComparison.OrdinalIgnoreCase)) ?? lastMatch[0];
        return null;
    }

    /// <summary>
    /// Load, and if the file is broken (a bad hand edit) fall back to the <c>.bak</c> copy of the last version that loaded
    /// (refreshed on every successful load and in-app edit): the broken file is kept as <c>.broken</c>, the backup is
    /// restored, and <paramref name="warning"/> says so.
    /// </summary>
    public static Taxonomy LoadWithFallback(string path, out string? warning)
    {
        warning = null;
        try
        {
            var ok = Load(path);
            try { File.Copy(path, path + ".bak", true); } catch { } // this version is known good; keep it as the fallback
            return ok;
        }
        catch (Exception ex)
        {
            var bak = path + ".bak";
            if (!File.Exists(bak)) throw;
            var restored = Load(bak);
            try
            {
                File.Copy(path, path + ".broken", true);
                File.Copy(bak, path, true);
            }
            catch (Exception ex2) { throw new InvalidOperationException($"taxonomy failed to load ({ex.Message}) and the backup could not be restored: {ex2.Message}", ex); }
            warning = $"taxonomy.yaml failed to load ({FirstLine(ex.Message)}); the previous version was restored and the broken file kept as taxonomy.yaml.broken.";
            return Load(path);
        }
    }

    static string FirstLine(string s) { var i = s.IndexOf('\n'); return i < 0 ? s : s[..i]; }

    /// <summary>Single-quoted YAML scalar; safe for any text (#, [, ], :, commas, quotes).</summary>
    public static string YamlQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Write an edited taxonomy text only if it still loads, then refresh the <c>.bak</c> (last known-good) copy.</summary>
    void SaveYaml(string text)
    {
        var tmp = SourcePath + ".tmp";
        File.WriteAllText(tmp, text);
        try { Load(tmp); }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("The edit would make taxonomy.yaml unreadable, so it was not written: " + FirstLine(ex.Message), ex);
        }
        File.Move(tmp, SourcePath, true);
        try { File.Copy(SourcePath, SourcePath + ".bak", true); } catch { } // last known-good version
    }

    public static Taxonomy Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var yaml = File.ReadAllText(path);
        var t = deserializer.Deserialize<Taxonomy>(yaml) ?? new Taxonomy();
        t.SourcePath = path;
        t.Questions = new Dictionary<string, QuestionDef>(t.Questions, StringComparer.OrdinalIgnoreCase);
        t.UnknownSettings = FindUnknownSettings(yaml);
        return t;
    }

    static List<string> FindUnknownSettings(string yaml)
    {
        var known = new HashSet<string>(typeof(DocketSettings).GetProperties().Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]), StringComparer.Ordinal);
        var unknown = new List<string>();
        try
        {
            var stream = new YamlDotNet.RepresentationModel.YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlDotNet.RepresentationModel.YamlMappingNode rootMap) return unknown;
            var settingsNode = rootMap.Children.FirstOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode { Value: "settings" }).Value;
            if (settingsNode is YamlDotNet.RepresentationModel.YamlMappingNode settings)
                foreach (var kv in settings.Children)
                    if (kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } key } && !known.Contains(key)) unknown.Add(key);
        }
        catch { }
        return unknown;
    }

    List<Regex>? _skipNameGlobs;

    /// <summary>
    /// May the chat index read this file? False for excluded folders, credential-looking names (<c>indexSkipNames</c> /
    /// <c>indexSkipExtensions</c>) and files under <c>indexSkipCategories</c>. Repositories are decided by the crawler (needs I/O).
    /// </summary>
    public bool IsIndexable(string fullPath, out string? reason)
    {
        reason = null;
        if (Exclusions.IsExcluded(fullPath)) { reason = "excluded folder"; return false; }
        var name = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(name);
        if (ext.Length > 0 && Settings.IndexSkipExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) { reason = "skipped extension"; return false; }
        _skipNameGlobs ??= Settings.IndexSkipNames.Select(PathTemplate.GlobToRegex).ToList();
        foreach (var rx in _skipNameGlobs) if (rx.IsMatch(name)) { reason = "credential-like name"; return false; }
        if (Settings.IndexSkipCategories.Count > 0)
        {
            var (_, category) = CategoryForPath(fullPath);
            if (category != null && Settings.IndexSkipCategories.Any(c => category.Equals(c, StringComparison.OrdinalIgnoreCase) || category.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase)))
            { reason = "category " + category + " is not indexed"; return false; }
        }
        return true;
    }

    List<(string Category, Regex Rx, int Weight)>? _catPaths;

    /// <summary>
    /// Best-effort category for a file already sitting under a root, from the category path templates with tokens
    /// treated as wildcards (longest template wins). Used to label crawled rows so chat filters work on them.
    /// </summary>
    public (string? Domain, string? Category) CategoryForPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return (null, null); }
        _catPaths ??= Categories
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Path) && Roots.ContainsKey(DomainOf(kv.Key)))
            .Select(kv =>
            {
                var template = kv.Value.Path.Replace('/', '\\').TrimEnd('\\');
                var glob = Regex.Replace(template, @"\{[^}]+\}", "*");
                var pattern = "^" + Regex.Escape(Path.Combine(Roots[DomainOf(kv.Key)], glob).TrimEnd('\\')).Replace(@"\*", @"[^\\]*") + @"(\\.*)?$";
                return (kv.Key, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), template.Count(ch => ch == '\\') * 100 + template.Length);
            })
            .ToList();
        string? best = null; int bestWeight = -1;
        foreach (var (cat, rx, weight) in _catPaths)
            if (weight > bestWeight && rx.IsMatch(full)) { best = cat; bestWeight = weight; }
        if (best != null) return (DomainOf(best), best);
        foreach (var (domain, rootPath) in Roots)
        {
            var r = rootPath.TrimEnd('\\');
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return (domain, null);
        }
        return (null, null);
    }

    /// <summary>Full paths of the folders under the roots that Scan mode must leave alone (category <c>frozen</c> lists).</summary>
    public HashSet<string> FrozenPaths()
    {
        var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in Roots.Values)
            foreach (var cat in Categories.Values)
                foreach (var f in cat.Frozen) frozen.Add(Path.GetFullPath(Path.Combine(rootPath, f)).TrimEnd('\\'));
        return frozen;
    }

    /// <summary>
    /// Top-level entries of a folder that Scan mode should classify: no sidecars or desktop.ini, nothing frozen,
    /// nothing excluded. Shared by the App's Scan window and the CLI <c>scan</c> command.
    /// </summary>
    public List<string> ScannableEntries(string folder)
    {
        var frozen = FrozenPaths();
        var ex = Exclusions;
        return Directory.EnumerateFileSystemEntries(folder)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                if (name.EndsWith(".filer.json", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;
                try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false; } catch { return false; } // junctions point elsewhere
                var full = Path.GetFullPath(p).TrimEnd('\\');
                return !frozen.Contains(full) && !ex.IsExcluded(full);
            })
            .ToList();
    }

    /// <summary>Replace the exclusion list in memory and in the YAML text (inline list under <c>settings:</c>), keeping comments intact.</summary>
    public void SetExcludeFolders(IEnumerable<string> folders)
    {
        var list = folders.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Settings.ExcludeFolders = list;
        _exclusions = null;
        if (string.IsNullOrEmpty(SourcePath) || !File.Exists(SourcePath)) return;
        var text = File.ReadAllText(SourcePath);
        var inline = "excludeFolders: [" + string.Join(", ", list.Select(YamlQuote)) + "]";
        // Items are single-quoted, so a ']' inside a pattern must not end the list.
        var rx = new Regex(@"^([ \t]*)excludeFolders[ \t]*:[ \t]*\[(?:'(?:[^']|'')*'|[^\]'])*\]", RegexOptions.Multiline);
        var m = rx.Match(text);
        if (m.Success)
            text = text[..m.Index] + m.Groups[1].Value + inline + text[(m.Index + m.Length)..];
        else
        {
            var settings = Regex.Match(text, @"^settings[ \t]*:[ \t]*\r?\n", RegexOptions.Multiline);
            if (!settings.Success) return;
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            text = text[..(settings.Index + settings.Length)] + "  " + inline + nl + text[(settings.Index + settings.Length)..];
        }
        SaveYaml(text);
    }

    /// <summary>Append an option to a question's inline list in the YAML text, keeping comments intact.</summary>
    public void AddQuestionOption(string question, string option)
    {
        if (Questions.TryGetValue(question, out var q))
        {
            if (q.Options.Contains(option, StringComparer.OrdinalIgnoreCase)) return;
            q.Options = new List<string>(q.Options) { option }; // replace, never mutate: the tick may be iterating the old list
        }
        if (string.IsNullOrEmpty(SourcePath) || !File.Exists(SourcePath)) return;
        var text = File.ReadAllText(SourcePath);
        var rx = new Regex(@"(^\s*" + Regex.Escape(question) + @"\s*:\s*\{[^\n]*?options\s*:\s*\[)([^\]]*)(\])", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var m = rx.Match(text);
        if (!m.Success) return;
        var list = m.Groups[2].Value.Trim();
        var quoted = YamlQuote(option);
        var replaced = string.IsNullOrEmpty(list) ? quoted : list + ", " + quoted;
        text = text[..m.Groups[2].Index] + replaced + text[(m.Groups[2].Index + m.Groups[2].Length)..];
        SaveYaml(text);
    }

    /// <summary>Append a learned rule to the YAML rules section.</summary>
    public void AppendRule(RuleDef rule)
    {
        Rules = new List<RuleDef>(Rules) { rule }; // replace, never mutate: RuleEngine.Evaluate may be iterating
        if (string.IsNullOrEmpty(SourcePath)) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  - name: {rule.Name}");
        var when = string.Join(", ", rule.When.Select(kv => kv.Value is IEnumerable<string> l
            ? $"{kv.Key}: [{string.Join(", ", l.Select(YamlQuote))}]"
            : $"{kv.Key}: {YamlQuote(kv.Value?.ToString() ?? "")}"));
        sb.AppendLine($"    when: {{ {when} }}");
        if (rule.Set.Count > 0)
            sb.AppendLine($"    set:  {{ {string.Join(", ", rule.Set.Select(kv => $"{kv.Key}: {YamlQuote(kv.Value)}"))} }}");
        if (rule.Ask.Count > 0)
            sb.AppendLine($"    ask:  [{string.Join(", ", rule.Ask.Select(YamlQuote))}]");
        if (!string.IsNullOrEmpty(rule.Notes))
            sb.AppendLine($"    notes: {YamlQuote(rule.Notes)}");
        var text = File.ReadAllText(SourcePath).TrimEnd('\r', '\n') + System.Environment.NewLine;
        if (!Regex.IsMatch(text, @"^rules\s*:", RegexOptions.Multiline))
            text += "rules:" + System.Environment.NewLine;
        SaveYaml(text + sb);
    }
}
