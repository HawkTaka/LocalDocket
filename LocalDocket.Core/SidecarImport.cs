using System.Text.Json;

namespace LocalDocket.Core;

public sealed record SidecarImportResult(int Found, int Merged, int Inserted, int Orphans, int Deleted, List<string> Errors)
{
    public override string ToString() => $"{Found} sidecars: {Merged} merged into existing rows, {Inserted} rows created, {Orphans} orphans, {Deleted} deleted" + (Errors.Count > 0 ? $", {Errors.Count} errors" : "");
}

/// <summary>
/// One-off migration of the hidden <c>&lt;name&gt;.filer.json</c> files Local Docket wrote next to filed items before 2026-09-10.
/// Their payload (extractor metadata, content kind, hash, classification) goes into the <c>files</c> row; the sidecar itself
/// is only deleted when asked (<c>docket index --remove-sidecars</c>).
/// </summary>
public static class SidecarImport
{
    public const string Suffix = ".filer.json";
    public const string DoneKey = "sidecars.imported";

    /// <summary>Shape of the legacy file. Kept private: nothing else in Local Docket knows about sidecars any more.</summary>
    sealed class Legacy
    {
        public string FileId { get; set; } = "";
        public string Name { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public DateTime OriginalCreated { get; set; }
        public DateTime OriginalModified { get; set; }
        public DateTime DroppedAt { get; set; }
        public DateTime FiledAt { get; set; }
        public string? Sha256 { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public Classification Classification { get; set; } = new();
        public Dictionary<string, string> Meta { get; set; } = new();
        public string? ContentKind { get; set; }
    }

    public static Task<SidecarImportResult> RunAsync(Taxonomy taxonomy, Store store, bool delete, Action<string>? log = null, CancellationToken ct = default) =>
        Task.Run(() => Run(taxonomy, store, delete, log ?? (_ => { }), ct), ct);

    public static SidecarImportResult Run(Taxonomy taxonomy, Store store, bool delete, Action<string> log, CancellationToken ct = default)
    {
        int found = 0, merged = 0, inserted = 0, orphans = 0, deleted = 0;
        var errors = new List<string>();
        foreach (var root in taxonomy.Roots.Values.Where(Directory.Exists))
        {
            foreach (var sidecar in Enumerate(new DirectoryInfo(root), taxonomy, log))
            {
                ct.ThrowIfCancellationRequested();
                found++;
                var target = sidecar[..^Suffix.Length];
                var exists = File.Exists(target) || Directory.Exists(target);
                try
                {
                    if (!exists)
                    {
                        orphans++;
                        log($"sidecar orphan (item gone): {sidecar}");
                    }
                    else
                    {
                        Legacy? sc;
                        try { sc = JsonSerializer.Deserialize<Legacy>(File.ReadAllText(sidecar)); }
                        catch (Exception ex) { errors.Add($"{sidecar}: {ex.Message}"); continue; }
                        if (sc == null) { errors.Add($"{sidecar}: empty"); continue; }
                        var meta = sc.Meta.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value);
                        var id = (sc.FileId.Length > 0 && store.GetFile(sc.FileId) != null ? sc.FileId : null) ?? store.FindIdByPath(target);
                        if (id != null)
                        {
                            store.FillLegacyProvenance(id, sc.ContentKind, meta, sc.Sha256);
                            merged++;
                        }
                        else
                        {
                            var item = new FileItem
                            {
                                Path = sc.OriginalPath.Length > 0 ? sc.OriginalPath : target, Name = sc.Name.Length > 0 ? sc.Name : Path.GetFileName(target), IsDirectory = sc.IsDirectory,
                                Size = sc.Size, Created = sc.OriginalCreated, Modified = sc.OriginalModified, DroppedAt = sc.DroppedAt, Sha256 = sc.Sha256, ContentKind = sc.ContentKind,
                            };
                            foreach (var kv in meta) item.Meta[kv.Key] = kv.Value;
                            var c = sc.Classification;
                            if (string.IsNullOrEmpty(c.DecidedBy)) c.DecidedBy = "sidecar";
                            store.RecordFiled(sc.FileId.Length > 0 ? sc.FileId : Guid.NewGuid().ToString("N"), item, c, target, null, sc.FiledAt == default ? null : sc.FiledAt);
                            inserted++;
                        }
                    }
                    if (delete)
                    {
                        File.SetAttributes(sidecar, FileAttributes.Normal);
                        File.Delete(sidecar);
                        deleted++;
                    }
                }
                catch (Exception ex) { errors.Add($"{sidecar}: {ex.Message}"); }
            }
        }
        var result = new SidecarImportResult(found, merged, inserted, orphans, deleted, errors);
        log("sidecar import: " + result);
        if (!delete || errors.Count == 0) store.SetKv(DoneKey, DateTime.Now.ToString("o"));
        return result;
    }

    /// <summary>Sidecars under a root. Excluded folders are still walked (an item filed before an exclusion existed keeps its provenance), only VCS/tool folders are skipped.</summary>
    static IEnumerable<string> Enumerate(DirectoryInfo dir, Taxonomy taxonomy, Action<string> log)
    {
        var skip = new HashSet<string>(Exclusions.BuiltIn, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("sidecar import: cannot read " + d.FullName + ": " + ex.Message); continue; }
            foreach (var e in entries)
            {
                if (e is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || skip.Contains(sub.Name)) continue;
                    stack.Push(sub);
                }
                else if (e.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) yield return e.FullName;
            }
        }
    }
}
