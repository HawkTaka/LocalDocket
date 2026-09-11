using System.Text.RegularExpressions;

namespace LocalDocket.Core;

/// <summary>
/// Folders that Scan mode and the indexer never look inside. Built from <c>settings.excludeFolders</c> plus a built-in
/// list of tool folders. Pattern shape decides how it is applied (all case-insensitive, globs allowed):
/// an absolute path excludes that folder and everything below; a root-relative path (<c>Infra\Deploy</c>) is expanded
/// under every taxonomy root; a bare name (<c>.git</c>, <c>node_modules</c>, <c>*.tmp</c>) matches any path segment.
/// </summary>
public sealed class Exclusions
{
    /// <summary>Always excluded, whatever the taxonomy says: build output, package caches, VCS internals.</summary>
    public static readonly string[] BuiltIn =
    {
        ".git", ".vs", ".idea", ".svn", "node_modules", "bin", "obj", "packages", "__pycache__", ".venv", "venv",
        "$RECYCLE.BIN", "System Volume Information",
    };

    readonly List<Regex> _full = new();
    readonly List<Regex> _segment = new();

    public IReadOnlyList<string> Patterns { get; }
    public IReadOnlyList<string> Roots { get; }

    public Exclusions(IEnumerable<string> patterns, IEnumerable<string> roots, bool includeBuiltIn = true)
    {
        Roots = roots.Select(Norm).ToList();
        var all = (includeBuiltIn ? BuiltIn.AsEnumerable() : Array.Empty<string>()).Concat(patterns)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Patterns = all;
        foreach (var p in all)
        {
            var g = p.Replace('/', '\\').TrimEnd('\\');
            if (Path.IsPathRooted(g) && (g.Length > 1 && g[1] == ':' || g.StartsWith(@"\\")))
                _full.Add(Prefix(g));
            else if (g.Contains('\\'))
                foreach (var r in Roots) _full.Add(Prefix(r + "\\" + g));
            else
                _segment.Add(PathTemplate.GlobToRegex(g));
        }
    }

    /// <summary>True when the path, or any folder above it, is excluded.</summary>
    public bool IsExcluded(string path)
    {
        var full = Norm(path);
        foreach (var rx in _full) if (rx.IsMatch(full)) return true;
        if (_segment.Count == 0) return false;
        // Only segments below a root (or below the drive when the path is outside every root) are candidates,
        // so a root named "bin" would not exclude itself.
        var start = 0;
        foreach (var r in Roots)
            if (full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) { start = r.Length + 1; break; }
        var rest = full[start..];
        if (start == 0) { var i = rest.IndexOf('\\'); rest = i < 0 ? "" : rest[(i + 1)..]; }
        foreach (var seg in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            foreach (var rx in _segment) if (rx.IsMatch(seg)) return true;
        return false;
    }

    /// <summary>A folder is a repository / project when it directly contains one of the markers (".git", "*.sln", ...).</summary>
    public static bool IsRepository(string dir, IEnumerable<string> markers)
    {
        if (!Directory.Exists(dir)) return false;
        List<Regex>? globs = null;
        foreach (var m in markers)
        {
            if (m.Contains('*') || m.Contains('?')) (globs ??= new()).Add(PathTemplate.GlobToRegex(m));
            else if (Directory.Exists(Path.Combine(dir, m)) || File.Exists(Path.Combine(dir, m))) return true;
        }
        if (globs == null) return false;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                foreach (var g in globs) if (g.IsMatch(name)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    static Regex Prefix(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", "[^\\\\]*").Replace(@"\?", ".") + @"(\\.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string Norm(string path)
    {
        var p = path.Replace('/', '\\');
        try { p = Path.GetFullPath(p); } catch { }
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }
}
