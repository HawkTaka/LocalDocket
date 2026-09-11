using System.Text.RegularExpressions;

namespace LocalDocket.Core;

public static class PathTemplate
{
    static readonly Regex Token = new(@"\{(yyyy-MM-dd|yyyy-MM|yyyy|env|client|ticket|project)\}", RegexOptions.Compiled);
    static readonly Regex TicketRx = new(@"\b([A-Z]{2,6}-\d{1,6})\b", RegexOptions.Compiled);

    /// <summary>Resolve the category's path template to an absolute target directory.</summary>
    public static string Resolve(Taxonomy taxonomy, Classification c, FileItem item)
    {
        if (string.IsNullOrEmpty(c.Category) || !taxonomy.Categories.TryGetValue(c.Category, out var def))
            throw new InvalidOperationException($"Unknown category '{c.Category}'");
        var root = taxonomy.RootFor(taxonomy.DomainOf(c.Category));
        var rel = Expand(def.Path, c, item);
        if (item.IsDirectory && !string.IsNullOrEmpty(rel) && Token.IsMatch(def.Path))
        {
            // Folder "Foo" under "Projects\{project}" resolves to Projects\Foo: the folder itself is the project, so it
            // lands as Projects\Foo, never Projects\Foo\Foo (which would also mean moving a folder into itself on re-scan).
            var parts = rel.TrimEnd('\\').Split('\\');
            if (parts.Length > 0 && parts[^1].Equals(Clean(item.Stem), StringComparison.OrdinalIgnoreCase))
                rel = string.Join('\\', parts[..^1]);
        }
        return string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel);
    }

    public static string Expand(string template, Classification c, FileItem item)
    {
        var date = item.Modified == default ? DateTime.Now : item.Modified;
        return Token.Replace(template, m => m.Groups[1].Value switch
        {
            "yyyy-MM-dd" => date.ToString("yyyy-MM-dd"),
            "yyyy-MM" => date.ToString("yyyy-MM"),
            "yyyy" => date.ToString("yyyy"),
            "env" => Clean(c.Environment) ?? "Unsorted",
            "client" => Clean(c.Client) ?? "Unassigned",
            "ticket" => Clean(c.Ticket) ?? Clean(c.Project) ?? TicketFrom(item.Name) ?? Clean(item.Stem)!,
            "project" => Clean(c.Project) ?? Clean(item.Stem)!,
            _ => m.Value
        });
    }

    public static string? TicketFrom(string name)
    {
        var m = TicketRx.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? null : cleaned;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> GlobCache = new(StringComparer.Ordinal);

    /// <summary>Glob → regex (case-insensitive, whole name). Memoised: callers run these per file, per marker, per crawl.</summary>
    public static Regex GlobToRegex(string glob) =>
        GlobCache.GetOrAdd(glob, g => new("^" + Regex.Escape(g).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled));
}
