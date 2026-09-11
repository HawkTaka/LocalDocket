using System.Text.RegularExpressions;

namespace LocalDocket.Core;

/// <summary>Deterministic classification: taxonomy rules first (confidence 1.0), then category match globs as a hint.</summary>
public static class RuleEngine
{
    /// <summary>
    /// Name regex for a "remember this as a rule" request: the stem with digits and spaces generalised. Null when the stem is
    /// too weak to be a rule (fewer than four letters/digits), since a rule fires at confidence 1.0 and never asks.
    /// </summary>
    public static string? LearnedNamePattern(string stem)
    {
        var strength = stem.Count(char.IsLetterOrDigit);
        if (strength < 4) return null;
        var pattern = System.Text.RegularExpressions.Regex.Escape(stem);
        pattern = System.Text.RegularExpressions.Regex.Replace(pattern, @"\d+", @"\d+");
        pattern = System.Text.RegularExpressions.Regex.Replace(pattern, @"(\\ )+", @"\s+");
        return "(?i)^" + pattern;
    }

    static readonly Regex MatchToken = new(@"\{match:(.+?)\}", RegexOptions.Compiled);

    public static Classification? Evaluate(FileItem item, Taxonomy taxonomy)
    {
        foreach (var rule in taxonomy.Rules)
        {
            if (!Matches(rule, item)) continue;
            var c = new Classification { DecidedBy = "rule:" + rule.Name, Confidence = 1.0, Reasoning = rule.Notes ?? $"Rule '{rule.Name}' matched." };
            foreach (var (k, v) in rule.Set)
            {
                var val = ExpandMatchToken(v, item.Name);
                switch (k.ToLowerInvariant())
                {
                    case "category":
                        c.Category = taxonomy.NormalizeCategory(val) ?? val;
                        c.Domain = taxonomy.DomainOf(c.Category);
                        break;
                    case "environment": c.Environment = val; break;
                    case "client": c.Client = val; break;
                    case "ticket": c.Ticket = val; break;
                    case "project": c.Project = val; break;
                    case "action": c.Action = val; break;
                    case "domain": c.Domain = val; break;
                }
            }
            c.Questions.AddRange(rule.Ask);
            ApplyBakInference(item, taxonomy, c);
            ApplyCategoryAsks(taxonomy, c);
            return c;
        }
        return null;
    }

    /// <summary>Weak candidate from category match globs; used as a hint for the model and as a fallback when the model is down.</summary>
    public static Classification? Hint(FileItem item, Taxonomy taxonomy)
    {
        var hits = new List<string>();
        foreach (var (name, def) in taxonomy.Categories)
        {
            if (def.Match.Any(g => PathTemplate.GlobToRegex(g).IsMatch(item.Name)))
                hits.Add(name);
        }
        if (hits.Count == 0) return null;
        // Prefer a hit whose glob is more specific (longer) than a bare extension glob.
        var best = hits
            .Select(h => (name: h, score: taxonomy.Categories[h].Match.Where(g => PathTemplate.GlobToRegex(g).IsMatch(item.Name)).Max(g => g.Length)))
            .OrderByDescending(x => x.score).First().name;
        var c = new Classification
        {
            Category = best,
            Domain = taxonomy.DomainOf(best),
            Confidence = hits.Count == 1 ? 0.7 : 0.5,
            DecidedBy = "hint:" + best,
            Reasoning = $"Name matches {best} ({string.Join(", ", hits)})."
        };
        ApplyBakInference(item, taxonomy, c);
        ApplyCategoryAsks(taxonomy, c);
        return c;
    }

    /// <summary>Add category-level asks (e.g. environment for backups) when that field is still empty, and drop asks that are now answered.</summary>
    public static void ApplyCategoryAsks(Taxonomy taxonomy, Classification c)
    {
        c.Questions.RemoveAll(q =>
            (q.Equals("environment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Environment)) ||
            (q.Equals("client", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Client)) ||
            (q.Equals("domain", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Category) && c.Confidence >= 0.85));
        if (string.IsNullOrEmpty(c.Category) || !taxonomy.Categories.TryGetValue(c.Category, out var def)) return;
        foreach (var ask in def.Ask)
        {
            var satisfied = ask.ToLowerInvariant() switch
            {
                "environment" => !string.IsNullOrEmpty(c.Environment),
                "client" => !string.IsNullOrEmpty(c.Client),
                "domain" => false,
                _ => false
            };
            if (!satisfied && !c.Questions.Contains(ask, StringComparer.OrdinalIgnoreCase)) c.Questions.Add(ask);
        }
    }

    /// <summary>Use the .bak header (server / logical names) to infer the environment.</summary>
    public static void ApplyBakInference(FileItem item, Taxonomy taxonomy, Classification c)
    {
        if (!string.IsNullOrEmpty(c.Environment)) return;
        var s = taxonomy.Settings;
        if (item.Meta.TryGetValue("bak.server", out var server) && s.BakServers.TryGetValue(server, out var env))
        {
            c.Environment = env;
            c.Reasoning += $" Backup came from server {server} → {env}.";
            return;
        }
        var names = string.Join(" ", item.Meta.Where(kv => kv.Key.StartsWith("bak.")).Select(kv => kv.Value));
        if (names.Length == 0) return;
        foreach (var (pattern, e) in s.BakNamePatterns)
        {
            if (Regex.IsMatch(names, pattern, RegexOptions.IgnoreCase))
            {
                c.Environment = e;
                c.Reasoning += $" Backup names match '{pattern}' → {e}.";
                return;
            }
        }
    }

    static bool Matches(RuleDef rule, FileItem item)
    {
        if (rule.When.Count == 0) return false;
        foreach (var (k, v) in rule.When)
        {
            switch (k.ToLowerInvariant())
            {
                case "name":
                    if (!Regex.IsMatch(item.Name, v.ToString() ?? "", RegexOptions.IgnoreCase)) return false;
                    break;
                case "ext":
                    var exts = AsList(v).Select(e => e.StartsWith('.') ? e : "." + e);
                    if (!exts.Contains(item.Extension, StringComparer.OrdinalIgnoreCase)) return false;
                    break;
                case "content":
                    if (item.ContentText == null || !Regex.IsMatch(item.ContentText, v.ToString() ?? "", RegexOptions.IgnoreCase)) return false;
                    break;
                case "content_not":
                    if (item.ContentText != null && Regex.IsMatch(item.ContentText, v.ToString() ?? "", RegexOptions.IgnoreCase)) return false;
                    break;
                case "isdirectory":
                    if (item.IsDirectory != bool.Parse(v.ToString() ?? "false")) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    static IEnumerable<string> AsList(object v) => v switch
    {
        IEnumerable<object> list => list.Select(x => x.ToString() ?? ""),
        string s => new[] { s },
        _ => Array.Empty<string>()
    };

    static string ExpandMatchToken(string value, string name) =>
        MatchToken.Replace(value, m =>
        {
            var mm = Regex.Match(name, m.Groups[1].Value, RegexOptions.IgnoreCase);
            return mm.Success ? mm.Value : "";
        });
}
