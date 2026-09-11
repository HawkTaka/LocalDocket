using System.Security.Cryptography;

namespace LocalDocket.Core;

public sealed record MoveResult(long MoveId, string FileId, string FinalPath);

/// <summary>Moves items into place, preserves timestamps, records provenance in the store. Undo restores to the inbox.</summary>
public sealed class Mover
{
    readonly Store _store;

    public Mover(Store store) => _store = store;

    /// <summary>Diagnostics for the rare paths (a move that had to be rolled back); hosts point this at their log.</summary>
    public Action<string>? Log { get; set; }

    public MoveResult Move(FileItem item, Classification c, string targetDirectory, float[]? embedding = null, string? newName = null)
    {
        var source = Path.GetFullPath(item.Path).TrimEnd('\\');
        var targetDir = Path.GetFullPath(targetDirectory).TrimEnd('\\');
        if (item.IsDirectory && (targetDir.Equals(source, StringComparison.OrdinalIgnoreCase) || targetDir.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Refusing to move '{item.Name}' into itself ({targetDir}).");

        Directory.CreateDirectory(targetDir);
        var target = UniquePath(Path.Combine(targetDir, string.IsNullOrWhiteSpace(newName) ? item.Name : newName.Trim()));
        // An item the store already knows at this path (undone earlier, or indexed in place) keeps its id, so its history and chunks follow it.
        var fileId = _store.FindIdByPath(item.Path) ?? Guid.NewGuid().ToString("N");

        item.Sha256 ??= item.IsDirectory ? null : Hash(item.Path);

        if (item.IsDirectory) MoveDirectory(item.Path, target);
        else File.Move(item.Path, target);

        try
        {
            if (item.IsDirectory) { Directory.SetCreationTime(target, item.Created); Directory.SetLastWriteTime(target, item.Modified); }
            else { File.SetCreationTime(target, item.Created); File.SetLastWriteTime(target, item.Modified); }
        }
        catch { /* timestamps are best-effort */ }

        long moveId;
        try
        {
            // One transaction: child-row rename (folders), the file row, and the move-log row. Nothing half-recorded.
            moveId = _store.RecordFiledAndMove(fileId, item, c, target, embedding, item.IsDirectory ? (item.Path, target) : null);
        }
        catch (Exception ex)
        {
            // The item is already at the target but nothing says so; put it back so the inbox still shows it and nothing is lost.
            var restored = false;
            try
            {
                if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                {
                    if (item.IsDirectory) MoveDirectory(target, item.Path); else File.Move(target, item.Path);
                    restored = true;
                }
            }
            catch (Exception ex2) { Log?.Invoke($"move rollback failed for {item.Name}: item is at {target} without a record: {ex2.Message}"); }
            Log?.Invoke($"move of {item.Name} → {target} could not be recorded ({ex.Message}); {(restored ? "moved back to " + item.Path : "left at target")}");
            throw new InvalidOperationException(restored
                ? $"Could not record the move of '{item.Name}' ({ex.Message}); it was put back."
                : $"Could not record the move of '{item.Name}' ({ex.Message}); it is at {target} but has no record.", ex);
        }
        return new MoveResult(moveId, fileId, target);
    }

    /// <summary>Put the item back where it came from (the inbox). The store row follows it, so a re-file keeps the same id.</summary>
    public string Undo(long moveId)
    {
        var m = _store.GetMove(moveId) ?? throw new InvalidOperationException($"No move {moveId}");
        if (m.Undone) return m.FromPath;
        if (!File.Exists(m.ToPath) && !Directory.Exists(m.ToPath)) throw new FileNotFoundException("Filed item no longer at " + m.ToPath);
        Directory.CreateDirectory(Path.GetDirectoryName(m.FromPath)!);
        var back = UniquePath(m.FromPath);
        var isDir = Directory.Exists(m.ToPath);
        if (isDir) MoveDirectory(m.ToPath, back);
        else File.Move(m.ToPath, back);
        if (isDir) _store.RenamePrefix(m.ToPath, back);
        _store.MarkUndone(moveId, back);
        return back;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static string Hash(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Rename on the same volume; copy + delete only across volumes. Any other I/O error (locked child, nesting) surfaces as-is.</summary>
    static void MoveDirectory(string from, string to)
    {
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume) { Directory.Move(from, to); return; }
        try { Directory.Move(from, to); return; }
        catch (IOException) { /* cross-volume: copy then delete */ }
        var temp = to + ".filer-copying";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        CopyDir(from, temp);
        Directory.Move(temp, to);
        Directory.Delete(from, true);
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(f));
            File.Copy(f, dest, true);
            try { File.SetCreationTime(dest, File.GetCreationTime(f)); File.SetLastWriteTime(dest, File.GetLastWriteTime(f)); } catch { }
        }
        foreach (var d in Directory.GetDirectories(from))
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // never copy through junctions
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
