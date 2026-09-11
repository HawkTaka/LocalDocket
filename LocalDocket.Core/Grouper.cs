using System.Text.RegularExpressions;

namespace LocalDocket.Core;

/// <summary>
/// Decides which items share a popup. A dropped folder is always its own group.
/// Files are split into drop bursts (gap > window starts a new burst), then clustered by a
/// normalized stem so "Screenshot 2026-06-09 103712.png" and "Screenshot 2026-06-09 104810.png"
/// travel together while a .bak dropped at the same time gets its own dialog.
/// </summary>
public static class Grouper
{
    static readonly Regex Noise = new(@"(\(\d+\))|(\bcopy of\b)|(\d{4}[-_]?\d{2}[-_]?\d{2})|(\d{6,})|(\d+)|[\s_\-\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizedKey(FileItem item)
    {
        var stem = item.Stem;
        var key = Noise.Replace(stem, " ").Trim().ToLowerInvariant();
        return key.Length == 0 ? stem.ToLowerInvariant() : key;
    }

    public static List<FileGroup> Group(IEnumerable<FileItem> items, int windowSeconds)
    {
        var groups = new List<FileGroup>();
        var ordered = items.OrderBy(i => i.DroppedAt).ToList();

        foreach (var dir in ordered.Where(i => i.IsDirectory))
            groups.Add(new FileGroup { Items = { dir }, Reason = "dropped folder" });

        var files = ordered.Where(i => !i.IsDirectory).ToList();
        var burst = new List<FileItem>();
        DateTime? last = null;
        foreach (var f in files)
        {
            if (last != null && (f.DroppedAt - last.Value).TotalSeconds > windowSeconds)
            {
                groups.AddRange(SplitBurst(burst));
                burst = new List<FileItem>();
            }
            burst.Add(f);
            last = f.DroppedAt;
        }
        if (burst.Count > 0) groups.AddRange(SplitBurst(burst));
        return groups;
    }

    static IEnumerable<FileGroup> SplitBurst(List<FileItem> burst)
    {
        if (burst.Count == 1)
        {
            yield return new FileGroup { Items = { burst[0] }, Reason = "single drop" };
            yield break;
        }
        // Cluster by normalized stem; a zip and its sibling folder share a stem naturally.
        var clusters = burst.GroupBy(NormalizedKey).ToList();
        foreach (var c in clusters)
        {
            var list = c.ToList();
            yield return new FileGroup
            {
                Items = list,
                Reason = list.Count == 1 ? "dropped together" : $"{list.Count} related names ('{c.Key}')"
            };
        }
    }
}
