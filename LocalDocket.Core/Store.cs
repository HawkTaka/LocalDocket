using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalDocket.Core;

public sealed record MoveRecord(long Id, string FileId, string FromPath, string ToPath, DateTime MovedAt, bool Undone);

/// <summary>One row of the files table: everything Local Docket knows about an item (the former sidecar payload).</summary>
public sealed record FileRecord(
    string Id, string Name, string OriginalPath, string FinalPath, string? Sha256, long Size, bool IsDirectory,
    string? Domain, string? Category, string? Environment, string? Client, string? Ticket, string? Project,
    double? Confidence, string? DecidedBy, string? Reasoning, string? ContentKind, Dictionary<string, string> Meta,
    string Source, DateTime? FiledAt, DateTime? OriginalModified);

/// <summary>What the indexer needs to know about a row to decide whether the file changed.</summary>
public sealed record IndexEntry(string Id, string Path, long Size, DateTime? MtimeUtc, string Source, int Chunks, string? Category = null);
public sealed record IndexChunk(int Ordinal, string Text, float[] Embedding);
public sealed record OrphanCandidate(string Id, string Path, string? Sha256, int Chunks);

/// <summary>
/// SQLite index: every filed item with its provenance, the move log that drives Undo, and embeddings.
/// One connection guarded by a lock (the timer tick, Scan mode and later the indexer/chat all share it); WAL + busy timeout
/// let the CLI open the same file while the tray app is running.
/// </summary>
public sealed class Store : IDisposable
{
    public const int SchemaVersion = 2;
    readonly SqliteConnection _db;
    readonly object _lock = new();

    /// <summary>Raised (outside the lock) with the id of any row the store removed on its own, so an in-memory vector index can follow.</summary>
    public Action<string>? RowDeleted { get; set; }

    /// <summary>Rows other than <paramref name="keepId"/> that sit at the path; they would violate the unique path index.</summary>
    List<string> DeleteOthersAt(string path, string keepId)
    {
        var ids = new List<string>();
        using (var sel = _db.CreateCommand())
        {
            sel.CommandText = "SELECT id FROM files WHERE final_path=$fp COLLATE NOCASE AND id<>$id";
            sel.Parameters.AddWithValue("$fp", path);
            sel.Parameters.AddWithValue("$id", keepId);
            using var r = sel.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        if (ids.Count == 0) return ids;
        using var del = _db.CreateCommand();
        del.CommandText = "DELETE FROM files WHERE final_path=$fp COLLATE NOCASE AND id<>$id";
        del.Parameters.AddWithValue("$fp", path);
        del.Parameters.AddWithValue("$id", keepId);
        del.ExecuteNonQuery();
        return ids;
    }

    void Notify(List<string> deleted)
    {
        if (deleted.Count == 0 || RowDeleted == null) return;
        foreach (var id in deleted) { try { RowDeleted(id); } catch { } }
    }

    public Store(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS files (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, sha256 TEXT, size INTEGER, is_dir INTEGER,
              original_path TEXT NOT NULL, final_path TEXT NOT NULL,
              original_created TEXT, original_modified TEXT, dropped_at TEXT, filed_at TEXT,
              domain TEXT, category TEXT, environment TEXT, client TEXT, ticket TEXT, project TEXT,
              confidence REAL, decided_by TEXT, reasoning TEXT, classification_json TEXT,
              snippet TEXT, embedding BLOB);
            CREATE INDEX IF NOT EXISTS ix_files_category ON files(category);
            CREATE INDEX IF NOT EXISTS ix_files_sha ON files(sha256);
            CREATE TABLE IF NOT EXISTS moves (
              id INTEGER PRIMARY KEY AUTOINCREMENT, file_id TEXT NOT NULL, from_path TEXT NOT NULL, to_path TEXT NOT NULL,
              moved_at TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);
            """);
        Migrate();
    }

    /// <summary>Additive, idempotent: adds the columns a v1 database lacks, collapses duplicate paths, enforces one row per path.</summary>
    void Migrate()
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(files)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(1));
        }
        var wanted = new (string Name, string Decl)[]
        {
            ("content_kind", "TEXT"), ("meta_json", "TEXT"), ("source", "TEXT NOT NULL DEFAULT 'filed'"),
            ("indexed_at", "TEXT"), ("index_size", "INTEGER"), ("index_mtime", "TEXT"),
            ("chunk_count", "INTEGER NOT NULL DEFAULT 0"), ("index_error", "TEXT"),
        };
        foreach (var (name, decl) in wanted)
            if (!have.Contains(name)) Exec($"ALTER TABLE files ADD COLUMN {name} {decl}");
        // v1 could accumulate several rows for one path (undo → re-file minted a new id each time). Keep the newest.
        Exec("DELETE FROM files WHERE rowid NOT IN (SELECT MAX(rowid) FROM files GROUP BY final_path COLLATE NOCASE)");
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(final_path COLLATE NOCASE)");
        Exec("""
            CREATE TABLE IF NOT EXISTS chunks (
              file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL, text TEXT NOT NULL, embedding BLOB NOT NULL,
              PRIMARY KEY (file_id, ordinal)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_files_name ON files(name COLLATE NOCASE);
            """);
        SetKv("schema_version", SchemaVersion.ToString());
    }

    /// <summary>Insert or update the row for a filed item. Re-filing an id keeps its index columns and embedding when none is given.</summary>
    public void RecordFiled(string fileId, FileItem item, Classification c, string finalPath, float[]? embedding, DateTime? filedAt = null)
    {
        List<string> gone;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            gone = RecordFiledCore(fileId, item, c, finalPath, embedding, filedAt);
            tx.Commit();
        }
        Notify(gone);
    }

    /// <summary>Everything a move has to record, committed together: child-row rename for folders, the file row, the move-log row.</summary>
    public long RecordFiledAndMove(string fileId, FileItem item, Classification c, string finalPath, float[]? embedding, (string From, string To)? renamePrefix)
    {
        long moveId; List<string> gone;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            if (renamePrefix is { } rp) RenamePrefixCore(rp.From, rp.To);
            gone = RecordFiledCore(fileId, item, c, finalPath, embedding, null);
            moveId = RecordMoveCore(fileId, item.Path, finalPath);
            tx.Commit();
        }
        Notify(gone);
        return moveId;
    }

    List<string> RecordFiledCore(string fileId, FileItem item, Classification c, string finalPath, float[]? embedding, DateTime? filedAt)
    {
        {
            // A stale row already sitting at the target path (an older undone cycle) would violate the unique path index.
            var gone = DeleteOthersAt(finalPath, fileId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files (id,name,sha256,size,is_dir,original_path,final_path,original_created,original_modified,dropped_at,filed_at,
                  domain,category,environment,client,ticket,project,confidence,decided_by,reasoning,classification_json,snippet,embedding,
                  content_kind,meta_json,source)
                VALUES ($id,$name,$sha,$size,$dir,$op,$fp,$oc,$om,$da,$fa,$dom,$cat,$env,$cli,$tic,$proj,$conf,$by,$reason,$json,$snip,$emb,$kind,$meta,'filed')
                ON CONFLICT(id) DO UPDATE SET
                  name=excluded.name, sha256=COALESCE(excluded.sha256, files.sha256), size=excluded.size, is_dir=excluded.is_dir,
                  original_path=excluded.original_path, final_path=excluded.final_path,
                  original_created=excluded.original_created, original_modified=excluded.original_modified,
                  dropped_at=excluded.dropped_at, filed_at=excluded.filed_at,
                  domain=excluded.domain, category=excluded.category, environment=excluded.environment, client=excluded.client,
                  ticket=excluded.ticket, project=excluded.project, confidence=excluded.confidence, decided_by=excluded.decided_by,
                  reasoning=excluded.reasoning, classification_json=excluded.classification_json,
                  snippet=COALESCE(excluded.snippet, files.snippet), embedding=COALESCE(excluded.embedding, files.embedding),
                  content_kind=COALESCE(excluded.content_kind, files.content_kind), meta_json=COALESCE(excluded.meta_json, files.meta_json),
                  source='filed'
                """;
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$name", item.Name);
            cmd.Parameters.AddWithValue("$sha", (object?)item.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", item.Size);
            cmd.Parameters.AddWithValue("$dir", item.IsDirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("$op", item.Path);
            cmd.Parameters.AddWithValue("$fp", finalPath);
            cmd.Parameters.AddWithValue("$oc", item.Created.ToString("o"));
            cmd.Parameters.AddWithValue("$om", item.Modified.ToString("o"));
            cmd.Parameters.AddWithValue("$da", item.DroppedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fa", (filedAt ?? DateTime.Now).ToString("o"));
            cmd.Parameters.AddWithValue("$dom", c.Domain);
            cmd.Parameters.AddWithValue("$cat", c.Category);
            cmd.Parameters.AddWithValue("$env", (object?)c.Environment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cli", (object?)c.Client ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tic", (object?)c.Ticket ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$proj", (object?)c.Project ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conf", c.Confidence);
            cmd.Parameters.AddWithValue("$by", c.DecidedBy);
            cmd.Parameters.AddWithValue("$reason", c.Reasoning);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(c));
            cmd.Parameters.AddWithValue("$snip", (object?)Snippet(item) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$emb", embedding == null ? DBNull.Value : ToBytes(embedding));
            cmd.Parameters.AddWithValue("$kind", (object?)item.ContentKind ?? DBNull.Value);
            var meta = item.Meta.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value);
            cmd.Parameters.AddWithValue("$meta", meta.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(meta));
            cmd.ExecuteNonQuery();
            return gone;
        }
    }

    /// <summary>Fill provenance a v1 row lacks (imported from a legacy sidecar); never overwrites what is already there.</summary>
    public void FillLegacyProvenance(string id, string? contentKind, Dictionary<string, string> meta, string? sha256)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE files SET content_kind=COALESCE(content_kind,$k), meta_json=COALESCE(meta_json,$m), sha256=COALESCE(sha256,$s) WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$k", (object?)contentKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", meta.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(meta));
            cmd.Parameters.AddWithValue("$s", (object?)sha256 ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Filed rows still missing extractor metadata (before/after a sidecar import).</summary>
    public int CountFiledWithoutMeta()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE source='filed' AND meta_json IS NULL AND content_kind IS NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Id of the row currently recorded at this path (case-insensitive), if any.</summary>
    public string? FindIdByPath(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM files WHERE final_path=$p COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() as string;
        }
    }

    public FileRecord? GetFile(string id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id,name,original_path,final_path,sha256,size,is_dir,domain,category,environment,client,ticket,project,
                       confidence,decided_by,reasoning,content_kind,meta_json,source,filed_at,original_modified
                FROM files WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTime? D(int i) => r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i));
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (S(17) is { } mj)
                try { foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(mj) ?? new()) meta[kv.Key] = kv.Value; } catch { }
            return new FileRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(4), r.IsDBNull(5) ? 0 : r.GetInt64(5), !r.IsDBNull(6) && r.GetInt64(6) == 1,
                S(7), S(8), S(9), S(10), S(11), S(12), r.IsDBNull(13) ? null : r.GetDouble(13), S(14), S(15), S(16), meta, S(18) ?? "filed", D(19), D(20));
        }
    }

    public long RecordMove(string fileId, string from, string to) { lock (_lock) return RecordMoveCore(fileId, from, to); }

    long RecordMoveCore(string fileId, string from, string to)
    {
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO moves (file_id,from_path,to_path,moved_at) VALUES ($f,$a,$b,$t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.Parameters.AddWithValue("$a", from);
            cmd.Parameters.AddWithValue("$b", to);
            cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Flag the move undone and point the file row at where the item actually landed (may differ from from_path on collision).</summary>
    public void MarkUndone(long moveId, string backPath)
    {
        List<string> gone;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            string? fileId;
            using (var q = _db.CreateCommand())
            {
                q.CommandText = "SELECT file_id FROM moves WHERE id=$id";
                q.Parameters.AddWithValue("$id", moveId);
                fileId = q.ExecuteScalar() as string;
            }
            gone = fileId == null ? new() : DeleteOthersAt(backPath, fileId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE moves SET undone=1 WHERE id=$id; UPDATE files SET final_path=$back WHERE id=$f";
            cmd.Parameters.AddWithValue("$id", moveId);
            cmd.Parameters.AddWithValue("$back", backPath);
            cmd.Parameters.AddWithValue("$f", (object?)fileId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        Notify(gone);
    }

    /// <summary>True when the item at this inbox path is one the user undid and has not changed since (size and modified time agree).</summary>
    public bool WasUndoneAt(string path, long size, DateTime modified)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT f.size, f.original_modified FROM files f
                JOIN moves m ON m.file_id=f.id AND m.id=(SELECT MAX(id) FROM moves WHERE file_id=f.id)
                WHERE f.final_path=$p COLLATE NOCASE AND m.undone=1 AND f.source='filed'
                """;
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return false;
            var sameSize = r.IsDBNull(0) || r.GetInt64(0) == size;
            var sameTime = r.IsDBNull(1) || !DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var om) || Math.Abs((om - modified).TotalSeconds) < 2;
            return sameSize && sameTime;
        }
    }

    /// <summary>Reclaim space and scrub freed pages after chunks were deleted (secure_delete zeroes them; VACUUM returns them).</summary>
    public void Vacuum()
    {
        lock (_lock)
        {
            try { using var cmd = _db.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;"; cmd.ExecuteNonQuery(); }
            catch (SqliteException) { /* busy or inside a transaction: try again after the next prune */ }
        }
    }

    /// <summary>A folder moved: rewrite the path of every row at or below the old folder path.</summary>
    public int RenamePrefix(string oldDir, string newDir) { lock (_lock) return RenamePrefixCore(oldDir, newDir); }

    int RenamePrefixCore(string oldDir, string newDir)
    {
        oldDir = oldDir.TrimEnd('\\'); newDir = newDir.TrimEnd('\\');
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE files SET final_path = $new || substr(final_path, length($old) + 1)
                WHERE final_path = $old COLLATE NOCASE OR substr(final_path, 1, length($old) + 1) = $old || '\' COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$old", oldDir);
            cmd.Parameters.AddWithValue("$new", newDir);
            return cmd.ExecuteNonQuery();
        }
    }

    public MoveRecord? GetMove(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,file_id,from_path,to_path,moved_at,undone FROM moves WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadMove(r) : null;
        }
    }

    public List<MoveRecord> RecentMoves(int limit = 20)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,file_id,from_path,to_path,moved_at,undone FROM moves ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<MoveRecord>();
            while (r.Read()) list.Add(ReadMove(r));
            return list;
        }
    }

    static MoveRecord ReadMove(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTime.Parse(r.GetString(4)), r.GetInt64(5) == 1);

    /// <summary>Past decisions closest to the given embedding (cosine), for few-shot prompting. Only rows that carry a decision.</summary>
    public List<PastDecision> Nearest(float[] query, int k = 5, double minSimilarity = 0.45)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT name,category,environment,client,snippet,embedding FROM files WHERE embedding IS NOT NULL AND decided_by IS NOT NULL AND decided_by<>''";
            using var r = cmd.ExecuteReader();
            var scored = new List<PastDecision>();
            while (r.Read())
            {
                var emb = FromBytes((byte[])r[5]);
                var sim = Cosine(query, emb);
                if (sim < minSimilarity) continue;
                scored.Add(new PastDecision(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), sim));
            }
            return scored.OrderByDescending(s => s.Similarity).Take(k).ToList();
        }
    }

    /// <summary>Simple search over name + snippet + category, ranked by embedding when given.</summary>
    public List<(string Name, string Path, string Category, double Score)> Search(string text, float[]? embedding, int limit = 25)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT name,final_path,COALESCE(category,''),snippet,embedding FROM files";
            using var r = cmd.ExecuteReader();
            var results = new List<(string, string, string, double)>();
            var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            while (r.Read())
            {
                var name = r.GetString(0); var path = r.GetString(1); var cat = r.GetString(2);
                var snip = r.IsDBNull(3) ? "" : r.GetString(3);
                double score = 0;
                foreach (var t in terms)
                {
                    if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 0.5;
                    if (cat.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 0.2;
                    if (snip.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 0.15;
                }
                if (embedding != null && !r.IsDBNull(4))
                {
                    var sim = Cosine(embedding, FromBytes((byte[])r[4]));
                    if (sim > 0.5) score += (sim - 0.5) * 2;
                }
                if (score > 0.25) results.Add((name, path, cat, score));
            }
            return results.OrderByDescending(x => x.Item4).Take(limit).ToList();
        }
    }

    public string? GetKv(string key)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetKv(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO kv (key,value) VALUES ($k,$v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Items Local Docket itself filed (crawled rows, once they exist, are not counted).</summary>
    public int CountFiled()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE source='filed'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ---------------- index (crawler) side ----------------

    /// <summary>Every row by path: what the crawler compares the file system against.</summary>
    public Dictionary<string, IndexEntry> IndexSnapshot()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, final_path, index_size, index_mtime, source, chunk_count, category FROM files";
            using var r = cmd.ExecuteReader();
            var d = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                var mt = r.IsDBNull(3) ? (DateTime?)null : DateTime.ParseExact(r.GetString(3), "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                d[r.GetString(1)] = new IndexEntry(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? -1 : r.GetInt64(2), mt, r.IsDBNull(4) ? "filed" : r.GetString(4), r.IsDBNull(5) ? 0 : (int)r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6));
            }
            return d;
        }
    }

    /// <summary>
    /// Insert or refresh a crawled file and replace its chunks, in one transaction. A row Local Docket filed keeps its decision
    /// columns and source; only the index columns, content kind and snippet are refreshed.
    /// </summary>
    public void UpsertIndexed(string id, string path, string name, long size, DateTime mtimeUtc, string? contentKind, string? domain, string? category,
        string? snippet, Dictionary<string, string>? meta, IReadOnlyList<IndexChunk> chunks, string? error, bool retryLater = false)
    {
        List<string> gone;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            gone = DeleteOthersAt(path, id);
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO files (id,name,original_path,final_path,size,is_dir,original_modified,domain,category,content_kind,meta_json,snippet,source,
                                       indexed_at,index_size,index_mtime,chunk_count,index_error,embedding)
                    VALUES ($id,$name,$fp,$fp,$size,0,$om,$dom,$cat,$kind,$meta,$snip,'crawl',$now,$size,$mt,$n,$err,$emb)
                    ON CONFLICT(id) DO UPDATE SET
                      name=excluded.name, final_path=excluded.final_path, size=excluded.size,
                      original_modified=CASE WHEN files.source='crawl' THEN excluded.original_modified ELSE COALESCE(files.original_modified, excluded.original_modified) END,
                      domain=CASE WHEN files.source='crawl' THEN excluded.domain ELSE files.domain END,
                      category=CASE WHEN files.source='crawl' THEN excluded.category ELSE files.category END,
                      content_kind=COALESCE(excluded.content_kind, files.content_kind),
                      meta_json=COALESCE(files.meta_json, excluded.meta_json),
                      snippet=COALESCE(excluded.snippet, files.snippet),
                      indexed_at=excluded.indexed_at, index_size=excluded.index_size, index_mtime=excluded.index_mtime,
                      chunk_count=excluded.chunk_count, index_error=excluded.index_error,
                      embedding=COALESCE(files.embedding, excluded.embedding)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$fp", path);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$om", mtimeUtc.ToLocalTime().ToString("o"));
                cmd.Parameters.AddWithValue("$dom", (object?)domain ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$kind", (object?)contentKind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", meta == null || meta.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(meta));
                cmd.Parameters.AddWithValue("$snip", (object?)snippet ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
                cmd.Parameters.AddWithValue("$mt", retryLater ? DBNull.Value : mtimeUtc.ToUniversalTime().ToString("o")); // NULL = look again next crawl
                cmd.Parameters.AddWithValue("$n", chunks.Count);
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$emb", chunks.Count == 0 ? DBNull.Value : ToBytes(chunks[0].Embedding));
                cmd.ExecuteNonQuery();
            }
            using (var dc = _db.CreateCommand())
            {
                dc.CommandText = "DELETE FROM chunks WHERE file_id=$id";
                dc.Parameters.AddWithValue("$id", id);
                dc.ExecuteNonQuery();
            }
            if (chunks.Count > 0)
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = "INSERT INTO chunks (file_id, ordinal, text, embedding) VALUES ($f, $o, $t, $e)";
                var pf = ins.Parameters.Add("$f", SqliteType.Text); var po = ins.Parameters.Add("$o", SqliteType.Integer);
                var pt = ins.Parameters.Add("$t", SqliteType.Text); var pe = ins.Parameters.Add("$e", SqliteType.Blob);
                foreach (var c in chunks)
                {
                    pf.Value = id; po.Value = c.Ordinal; pt.Value = c.Text; pe.Value = ToBytes(c.Embedding);
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        Notify(gone);
    }

    /// <summary>Record a failure against a row (by id, else by path) without touching its chunks.</summary>
    public void MarkIndexError(string? id, string path, string error)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = id != null
                ? "UPDATE files SET index_error=$e WHERE id=$id"
                : "UPDATE files SET index_error=$e WHERE final_path=$p COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Filed rows with this name and size, for re-linking a file that was moved by hand (caller checks the hash).</summary>
    public List<OrphanCandidate> FindOrphanFiled(string name, long size)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, final_path, sha256, chunk_count FROM files WHERE source='filed' AND name=$n COLLATE NOCASE AND size=$s";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$s", size);
            using var r = cmd.ExecuteReader();
            var list = new List<OrphanCandidate>();
            while (r.Read()) list.Add(new OrphanCandidate(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? 0 : (int)r.GetInt64(3)));
            return list;
        }
    }

    /// <summary>
    /// Drop a file's chunks but keep the row (filed items whose file went missing or moved under an exclusion). The index
    /// columns are reset so the file is re-read if it reappears. With <paramref name="expectedPath"/> the row is only touched
    /// when it still points there (a move during the crawl must not be undone by a stale snapshot). Returns rows affected.
    /// </summary>
    public int ClearChunks(string id, string reason, string? expectedPath = null)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            int n;
            using (var upd = _db.CreateCommand())
            {
                upd.CommandText = "UPDATE files SET chunk_count=0, index_error=$r, index_mtime=NULL, index_size=NULL, indexed_at=NULL WHERE id=$id" +
                                  (expectedPath != null ? " AND final_path=$p COLLATE NOCASE" : "");
                upd.Parameters.AddWithValue("$id", id);
                upd.Parameters.AddWithValue("$r", reason);
                upd.Parameters.AddWithValue("$p", (object?)expectedPath ?? DBNull.Value);
                n = upd.ExecuteNonQuery();
            }
            if (n > 0)
            {
                using var del = _db.CreateCommand();
                del.CommandText = "DELETE FROM chunks WHERE file_id=$id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }
    }

    /// <summary>Everything must be re-embedded (embedding model changed).</summary>
    public void ClearAllChunks()
    {
        Exec("DELETE FROM chunks; UPDATE files SET chunk_count=0, indexed_at=NULL, index_size=NULL, index_mtime=NULL, index_error=NULL");
    }

    /// <summary>
    /// Remove a crawled row (chunks cascade). With <paramref name="expectedPath"/> only a row that is still a crawled row at
    /// that path goes, so a file filed while the crawl ran keeps its new record. Returns rows affected.
    /// </summary>
    public int DeleteFile(string id, string? expectedPath = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE id=$id" + (expectedPath != null ? " AND final_path=$p COLLATE NOCASE AND source='crawl'" : "");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$p", (object?)expectedPath ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }

    public List<FileRecord> GetFiles(IEnumerable<string> ids) => ids.Distinct().Select(GetFile).Where(f => f != null).Select(f => f!).ToList();

    public List<(int Ordinal, string Text)> GetChunks(string fileId, IEnumerable<int>? ordinals = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var want = ordinals?.ToList();
            cmd.CommandText = "SELECT ordinal, text FROM chunks WHERE file_id=$f" + (want is { Count: > 0 } ? $" AND ordinal IN ({string.Join(",", want)})" : "") + " ORDER BY ordinal";
            cmd.Parameters.AddWithValue("$f", fileId);
            using var r = cmd.ExecuteReader();
            var list = new List<(int, string)>();
            while (r.Read()) list.Add(((int)r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Stream every chunk vector (startup load of the in-memory index).</summary>
    public void LoadChunkVectors(Action<string, int, float[]> onChunk)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT file_id, ordinal, embedding FROM chunks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(2)) continue;
                var blob = (byte[])r[2];
                if (blob.Length < 8 || blob.Length % 4 != 0) continue; // a damaged row must not stop the whole load
                onChunk(r.GetString(0), (int)r.GetInt64(1), FromBytes(blob));
            }
        }
    }

    /// <summary>Ids of rows matching every given filter (null = don't care). Category matches itself or any sub-category.</summary>
    public HashSet<string> FilterFileIds(string? domain, string? category, string? client, string? environment, DateTime? from, DateTime? to, string? pathContains, bool datesAreFiledAt = false)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var where = new List<string>();
            if (domain != null) { where.Add("domain=$dom COLLATE NOCASE"); cmd.Parameters.AddWithValue("$dom", domain); }
            if (category != null) { where.Add(@"(category=$cat COLLATE NOCASE OR category LIKE $catp ESCAPE '\')"); cmd.Parameters.AddWithValue("$cat", category); cmd.Parameters.AddWithValue("$catp", LikeEscape(category) + "/%"); }
            if (client != null) { where.Add("client=$cli COLLATE NOCASE"); cmd.Parameters.AddWithValue("$cli", client); }
            if (environment != null) { where.Add("environment=$env COLLATE NOCASE"); cmd.Parameters.AddWithValue("$env", environment); }
            var dateCol = datesAreFiledAt ? "filed_at" : "original_modified";
            if (from != null) { where.Add($"{dateCol} >= $from"); cmd.Parameters.AddWithValue("$from", from.Value.ToString("o")); }
            if (to != null) { where.Add($"{dateCol} <= $to"); cmd.Parameters.AddWithValue("$to", to.Value.ToString("o")); }
            if (pathContains != null) { where.Add(@"final_path LIKE $pc ESCAPE '\'"); cmd.Parameters.AddWithValue("$pc", "%" + LikeEscape(pathContains) + "%"); }
            cmd.CommandText = "SELECT id FROM files" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    static string LikeEscape(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public (int Files, int Chunks) IndexCounts()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(chunk_count),0) FROM files WHERE indexed_at IS NOT NULL";
            using var r = cmd.ExecuteReader();
            return r.Read() ? ((int)r.GetInt64(0), (int)r.GetInt64(1)) : (0, 0);
        }
    }

    static string? Snippet(FileItem item) =>
        item.ContentText == null ? null : item.ContentText.Length > 600 ? item.ContentText[..600] : item.ContentText;

    public static byte[] ToBytes(float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    public static float[] FromBytes(byte[] b) { var v = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, v, 0, b.Length); return v; }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    void Exec(string sql) { lock (_lock) { using var cmd = _db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); } }
    public void Dispose() { lock (_lock) _db.Dispose(); }
}
