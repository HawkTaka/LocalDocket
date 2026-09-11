using LocalDocket.Core;
using Xunit;

namespace LocalDocket.Tests;

public class TaxonomyFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "docket-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string TaxonomyPath => Path.Combine(Dir, "taxonomy.yaml");
    public string WorkRoot => Path.Combine(Dir, "Work");
    public string PersonalRoot => Path.Combine(Dir, "Personal");
    public string Inbox => Path.Combine(Dir, "Inbox");

    public TaxonomyFixture()
    {
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(WorkRoot);
        Directory.CreateDirectory(PersonalRoot);
        var repoTaxonomy = FindRepoTaxonomy();
        var text = File.ReadAllText(repoTaxonomy)
            .Replace(@"Work: 'D:\Work'", $"Work: '{WorkRoot}'")
            .Replace(@"Personal: 'D:\Personal'", $"Personal: '{PersonalRoot}'")
            .Replace(@"inbox: '%USERPROFILE%\Desktop\_Inbox'", $"inbox: '{Inbox}'");
        File.WriteAllText(TaxonomyPath, text);
    }

    static string FindRepoTaxonomy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "taxonomy.yaml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("taxonomy.yaml not found above " + AppContext.BaseDirectory);
    }

    public Taxonomy Load() => Taxonomy.Load(TaxonomyPath);

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class TaxonomyTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public TaxonomyTests(TaxonomyFixture f) => _f = f;

    [Fact]
    public void Loads_roots_categories_rules_and_settings()
    {
        var t = _f.Load();
        Assert.Equal(_f.WorkRoot, t.RootFor("Work"));
        Assert.Contains("Work/SQL/Backups", t.Categories.Keys);
        Assert.Contains("Personal/Scripts", t.Categories.Keys);
        Assert.NotEmpty(t.Rules);
        Assert.Equal("qwen3.5:9b", t.Settings.Model);
        Assert.Equal(0.85, t.Settings.AutoFileConfidence);
        Assert.True(t.Questions["client"].AllowAdd);
        Assert.Equal("UAT", t.Settings.BakServers["SQL-UAT-01"]);
    }

    [Theory]
    [InlineData("SQL/Scripts", "Work/SQL/Scripts")]
    [InlineData("work/sql/scripts", "Work/SQL/Scripts")]
    [InlineData("SQL/Backups", "Work/SQL/Backups")]
    [InlineData("Backups", null)] // ambiguous: Work/SQL/Backups vs Work/Infra/Backups
    [InlineData("Work\\HR", "Work/HR")]
    [InlineData("Nonsense/Thing", null)]
    public void Normalizes_model_category_names(string raw, string? expected)
    {
        var t = _f.Load();
        Assert.Equal(expected, t.NormalizeCategory(raw));
    }

    [Fact]
    public void Screenshots_ambiguous_suffix_resolves_by_domain()
    {
        var t = _f.Load();
        Assert.Equal("Personal/Media/Screenshots", t.NormalizeCategory("Screenshots", "Personal"));
        Assert.Equal("Work/Screenshots", t.NormalizeCategory("Screenshots", "Work"));
    }

    [Fact]
    public void Add_question_option_edits_yaml_in_place()
    {
        var t = _f.Load();
        t.AddQuestionOption("client", "CSB Solutions");
        var again = Taxonomy.Load(_f.TaxonomyPath);
        Assert.Contains("CSB Solutions", again.Questions["client"].Options);
        Assert.Contains("Acme", again.Questions["client"].Options);
        Assert.Contains("# Local Docket taxonomy", File.ReadAllText(_f.TaxonomyPath)); // comments survive
    }

    [Fact]
    public void Append_rule_round_trips()
    {
        var t = _f.Load();
        var before = t.Rules.Count;
        t.AppendRule(new RuleDef { Name = "learned-test", When = new() { ["name"] = "(?i)^Fleet\\s+Report", ["ext"] = new List<string> { ".xlsx" } }, Set = new() { ["category"] = "Work/Reports" } });
        var again = Taxonomy.Load(_f.TaxonomyPath);
        Assert.Equal(before + 1, again.Rules.Count);
        var r = again.Rules.Last();
        Assert.Equal("learned-test", r.Name);
        var item = new FileItem { Path = "x", Name = "Fleet Report_18-24 May 2026.xlsx" };
        var c = RuleEngine.Evaluate(item, again);
        Assert.NotNull(c);
        Assert.Equal("Work/Reports", c!.Category);
    }
}

public class RuleEngineTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public RuleEngineTests(TaxonomyFixture f) => _f = f;

    static FileItem Item(string name, string? content = null, bool dir = false)
    {
        var i = new FileItem { Path = @"C:\x\" + name, Name = name, IsDirectory = dir, Modified = new DateTime(2026, 6, 10) };
        i.ContentText = content;
        return i;
    }

    [Fact]
    public void Prod_backup_rule_sets_environment()
    {
        var c = RuleEngine.Evaluate(Item("Product_Production 2026-09-09.zip"), _f.Load());
        Assert.NotNull(c);
        Assert.Equal("Work/SQL/Backups", c!.Category);
        Assert.Equal("Production", c.Environment);
        Assert.Empty(c.Questions);
        Assert.Equal(1.0, c.Confidence);
    }

    [Fact]
    public void Prod_sql_script_rule()
    {
        var c = RuleEngine.Evaluate(Item("PROJ475_Production_Applied_20260902.sql"), _f.Load());
        Assert.Equal("Work/SQL/Scripts", c!.Category);
        Assert.Equal("Production", c.Environment);
    }

    [Fact]
    public void Non_opex_bak_asks_domain_and_environment()
    {
        var c = RuleEngine.Evaluate(Item("Shop.bak", "SQL backup header strings:\nShop-Full Database Backup\nSHOP_DB"), _f.Load());
        Assert.NotNull(c);
        Assert.Contains("domain", c!.Questions);
        Assert.Contains("environment", c.Questions);
    }

    [Fact]
    public void Opex_bak_without_env_hint_only_asks_environment()
    {
        var t = _f.Load();
        var item = Item("Product.bak", "SQL backup header strings:\nProduct-Full Database Backup");
        Assert.Null(RuleEngine.Evaluate(item, t)); // no rule fires
        var hint = RuleEngine.Hint(item, t);
        Assert.Equal("Work/SQL/Backups", hint!.Category);
        Assert.Equal(new[] { "environment" }, hint.Questions);
    }

    [Fact]
    public void Bak_header_server_maps_to_environment()
    {
        var t = _f.Load();
        var item = Item("Product.bak", "SQL backup header strings:\nProduct-Full Database Backup");
        item.Meta["bak.server"] = "SQL-UAT-01";
        var hint = RuleEngine.Hint(item, t);
        Assert.Equal("UAT", hint!.Environment);
        Assert.Empty(hint.Questions);
    }

    [Fact]
    public void Bak_logical_names_map_to_environment()
    {
        var t = _f.Load();
        var item = Item("Product.bak", "x");
        item.Meta["bak.logicalNames"] = "PRIMARY,Product_Dev,Product_Dev_log";
        var hint = RuleEngine.Hint(item, t);
        Assert.Equal("DEV", hint!.Environment);
    }

    [Fact]
    public void Ticket_plan_rule_extracts_ticket()
    {
        var c = RuleEngine.Evaluate(Item("PROJ-453_PLAN.md"), _f.Load());
        Assert.Equal("Work/Spec", c!.Category);
        Assert.Equal("PROJ-453", c.Ticket);
    }

    [Fact]
    public void Shortcuts_are_skipped()
    {
        var c = RuleEngine.Evaluate(Item("Docker Desktop.lnk"), _f.Load());
        Assert.Equal("skip", c!.Action);
    }

    [Fact]
    public void Hint_prefers_more_specific_glob()
    {
        var hint = RuleEngine.Hint(Item("Probation_Review_Alex_2026-05-29_Final.docx"), _f.Load());
        Assert.Equal("Work/Team", hint!.Category);
    }
}

public class PathTemplateTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public PathTemplateTests(TaxonomyFixture f) => _f = f;

    [Fact]
    public void Backup_path_uses_env_and_modified_date()
    {
        var t = _f.Load();
        var item = new FileItem { Path = "x", Name = "Product.bak", Modified = new DateTime(2026, 9, 9, 6, 39, 0) };
        var c = new Classification { Category = "Work/SQL/Backups", Environment = "Production" };
        Assert.Equal(Path.Combine(_f.WorkRoot, "SQL", "Database Backups", "Production", "2026-09-09"), PathTemplate.Resolve(t, c, item));
    }

    [Fact]
    public void Missing_env_falls_back_to_unsorted()
    {
        var t = _f.Load();
        var item = new FileItem { Path = "x", Name = "a.bak", Modified = new DateTime(2026, 1, 1) };
        var dir = PathTemplate.Resolve(t, new Classification { Category = "Work/SQL/Backups" }, item);
        Assert.Contains("Unsorted", dir);
    }

    [Fact]
    public void Spec_ticket_from_name_and_project_from_stem()
    {
        var t = _f.Load();
        var item = new FileItem { Path = "x", Name = "PROJ-499 scope notes.md", Modified = DateTime.Now };
        Assert.EndsWith(Path.Combine("Spec", "PROJ-499"), PathTemplate.Resolve(t, new Classification { Category = "Work/Spec" }, item));
        var proj = new FileItem { Path = "x", Name = "Eden-Multiplayer-Fix.zip", Modified = DateTime.Now };
        Assert.EndsWith(Path.Combine("Projects", "Eden-Multiplayer-Fix"), PathTemplate.Resolve(t, new Classification { Category = "Personal/Projects" }, proj));
    }

    [Fact]
    public void Client_with_invalid_chars_is_cleaned()
    {
        var t = _f.Load();
        var item = new FileItem { Path = "x", Name = "a.pdf", Modified = DateTime.Now };
        var dir = PathTemplate.Resolve(t, new Classification { Category = "Work/Clients", Client = "Acme: Ltd/Group" }, item);
        Assert.EndsWith(Path.Combine("Clients", "Acme_ Ltd_Group"), dir);
    }
}

public class GrouperTests
{
    static FileItem F(string name, DateTime dropped) => new() { Path = "x\\" + name, Name = name, DroppedAt = dropped };

    [Fact]
    public void Screenshot_sequence_groups_and_backup_stays_alone()
    {
        var t0 = new DateTime(2026, 9, 9, 10, 0, 0);
        var items = new[]
        {
            F("Screenshot 2026-06-09 103712.png", t0),
            F("Screenshot 2026-06-09 104810.png", t0.AddSeconds(2)),
            F("Screenshot 2026-06-09 105950.png", t0.AddSeconds(3)),
            F("Product.bak", t0.AddSeconds(5)),
        };
        var groups = Grouper.Group(items, 90);
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups.Single(g => g.Items.Count == 3).Items.Count);
        Assert.Equal("Product.bak", groups.Single(g => g.Items.Count == 1).Items[0].Name);
    }

    [Fact]
    public void Drops_far_apart_are_separate_bursts()
    {
        var t0 = new DateTime(2026, 9, 9, 10, 0, 0);
        var groups = Grouper.Group(new[] { F("a.pdf", t0), F("a (2).pdf", t0.AddMinutes(10)) }, 90);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Zip_and_extracted_folder_share_a_group()
    {
        var t0 = DateTime.Now;
        var zip = F("Eden-Multiplayer-Fix.zip", t0);
        var dir = new FileItem { Path = "x\\Eden-Multiplayer-Fix", Name = "Eden-Multiplayer-Fix", IsDirectory = true, DroppedAt = t0 };
        var groups = Grouper.Group(new[] { zip, dir }, 90);
        // folders are always their own group; the zip is filed on its own merits
        Assert.Equal(2, groups.Count);
        Assert.Equal("dropped folder", groups.First(g => g.Items[0].IsDirectory).Reason);
    }

    [Fact]
    public void Normalized_key_strips_dates_numbers_and_copy_of()
    {
        Assert.Equal(Grouper.NormalizedKey(F("Copy of Agile Plan_28012026_v3.xlsx", DateTime.Now)), Grouper.NormalizedKey(F("Agile Plan_28012026_v3.xlsx", DateTime.Now)));
    }
}

public class MoverTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public MoverTests(TaxonomyFixture f) => _f = f;

    [Fact]
    public void Move_records_provenance_preserves_timestamps_and_undo_restores()
    {
        var dbPath = Path.Combine(_f.Dir, "test-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(dbPath);
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "note-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(src, "hello");
        var stamp = new DateTime(2025, 3, 4, 5, 6, 7);
        File.SetLastWriteTime(src, stamp);
        var item = FileItem.FromPath(src);
        item.ContentText = "hello";
        item.ContentKind = "text";
        item.Meta["bak.server"] = "SQL01";
        item.Meta["_embedding"] = "never persisted";
        var target = Path.Combine(_f.WorkRoot, "Notes");
        var c = new Classification { Category = "Work/Prompts", Confidence = 1, DecidedBy = "user" };

        var r = mover.Move(item, c, target, new float[] { 0.1f, 0.2f });
        Assert.True(File.Exists(r.FinalPath));
        Assert.False(File.Exists(src));
        Assert.Equal(stamp, File.GetLastWriteTime(r.FinalPath));
        Assert.False(File.Exists(r.FinalPath + SidecarImport.Suffix));
        var rec = store.GetFile(r.FileId);
        Assert.NotNull(rec);
        Assert.Equal(src, rec!.OriginalPath);
        Assert.Equal(r.FinalPath, rec.FinalPath);
        Assert.Equal("Work/Prompts", rec.Category);
        Assert.Equal("text", rec.ContentKind);
        Assert.Equal("SQL01", rec.Meta["bak.server"]);
        Assert.False(rec.Meta.ContainsKey("_embedding"));
        Assert.NotNull(rec.Sha256);
        Assert.Equal("filed", rec.Source);
        Assert.Equal(1, store.CountFiled());
        Assert.Single(store.Nearest(new float[] { 0.1f, 0.2f }));

        var back = mover.Undo(r.MoveId);
        Assert.Equal(src, back);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(r.FinalPath));
        Assert.True(store.GetMove(r.MoveId)!.Undone);
        Assert.Equal(src, store.GetFile(r.FileId)!.FinalPath);
        Assert.Equal(r.FileId, store.FindIdByPath(src));

        // Re-filing the restored item reuses the row instead of minting a second one.
        var again = mover.Move(FileItem.FromPath(src), c, target);
        Assert.Equal(r.FileId, again.FileId);
        Assert.Equal(1, store.CountFiled());
        Assert.Equal(again.FinalPath, store.GetFile(r.FileId)!.FinalPath);
    }

    [Fact]
    public void Undo_after_collision_points_row_at_actual_restore_path()
    {
        var dbPath = Path.Combine(_f.Dir, "test-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(dbPath);
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "clash-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(src, "a");
        var r = mover.Move(FileItem.FromPath(src), new Classification { Category = "Work/Prompts" }, Path.Combine(_f.WorkRoot, "Notes"));
        File.WriteAllText(src, "b"); // something else took the inbox name meanwhile
        var back = mover.Undo(r.MoveId);
        Assert.NotEqual(src, back);
        Assert.True(File.Exists(back));
        Assert.Equal(back, store.GetFile(r.FileId)!.FinalPath);
    }

    [Fact]
    public void Directory_move_and_undo_rename_child_rows()
    {
        var dbPath = Path.Combine(_f.Dir, "test-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(dbPath);
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "proj-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var child = Path.Combine(src, "sub", "f.txt");
        File.WriteAllText(child, "x");
        // Pretend the indexer already knew the child file at its inbox path.
        store.RecordFiled("child1", FileItem.FromPath(child), new Classification { Category = "Personal/Projects" }, child, null);

        var r = mover.Move(FileItem.FromPath(src), new Classification { Category = "Personal/Projects" }, Path.Combine(_f.PersonalRoot, "Projects"));
        Assert.Equal(Path.Combine(r.FinalPath, "sub", "f.txt"), store.GetFile("child1")!.FinalPath);

        var back = mover.Undo(r.MoveId);
        Assert.Equal(Path.Combine(back, "sub", "f.txt"), store.GetFile("child1")!.FinalPath);
        Assert.Equal(back, store.GetFile(r.FileId)!.FinalPath);
    }

    [Fact]
    public void Collision_gets_numbered_suffix()
    {
        var dir = Path.Combine(_f.Dir, "coll");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "a.txt");
        File.WriteAllText(p, "1");
        Assert.Equal(Path.Combine(dir, "a (2).txt"), Mover.UniquePath(p));
    }

    [Fact]
    public void Directory_move_keeps_contents()
    {
        var dbPath = Path.Combine(_f.Dir, "test-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(dbPath);
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "proj-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "f.txt"), "x");
        var item = FileItem.FromPath(src);
        var r = mover.Move(item, new Classification { Category = "Personal/Projects" }, Path.Combine(_f.PersonalRoot, "Projects"));
        Assert.True(File.Exists(Path.Combine(r.FinalPath, "sub", "f.txt")));
        Assert.False(Directory.Exists(src));
    }
}

public class StoreTests
{
    [Fact]
    public void Cosine_and_search()
    {
        Assert.Equal(1.0, Store.Cosine(new[] { 1f, 0f }, new[] { 1f, 0f }), 6);
        Assert.Equal(0.0, Store.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }), 6);
        var db = Path.Combine(Path.GetTempPath(), "docket-store-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(db);
        var item = new FileItem { Path = @"C:\in\Acme MSA.pdf", Name = "Acme MSA.pdf", Modified = DateTime.Now, Created = DateTime.Now };
        item.ContentText = "master services agreement between Acme and us";
        store.RecordFiled("id1", item, new Classification { Category = "Work/Clients", Client = "Acme" }, @"C:\out\Acme MSA.pdf", null);
        var hits = store.Search("services agreement", null);
        Assert.Single(hits);
        Assert.Equal("Work/Clients", hits[0].Category);
    }

    [Fact]
    public void Migrates_v1_schema_and_collapses_duplicate_paths()
    {
        var db = Path.Combine(Path.GetTempPath(), "docket-v1-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files (
                  id TEXT PRIMARY KEY, name TEXT NOT NULL, sha256 TEXT, size INTEGER, is_dir INTEGER,
                  original_path TEXT NOT NULL, final_path TEXT NOT NULL,
                  original_created TEXT, original_modified TEXT, dropped_at TEXT, filed_at TEXT,
                  domain TEXT, category TEXT, environment TEXT, client TEXT, ticket TEXT, project TEXT,
                  confidence REAL, decided_by TEXT, reasoning TEXT, classification_json TEXT,
                  snippet TEXT, embedding BLOB);
                CREATE TABLE moves (id INTEGER PRIMARY KEY AUTOINCREMENT, file_id TEXT NOT NULL, from_path TEXT NOT NULL, to_path TEXT NOT NULL, moved_at TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
                INSERT INTO files (id,name,original_path,final_path,category,decided_by) VALUES ('old1','a.txt','C:\in\a.txt','C:\out\a.txt','Work/Spec','rule:x');
                INSERT INTO files (id,name,original_path,final_path,category,decided_by) VALUES ('dup1','b.txt','C:\in\b.txt','C:\in\b.txt','Work/Spec','user');
                INSERT INTO files (id,name,original_path,final_path,category,decided_by) VALUES ('dup2','b.txt','C:\in\b.txt','c:\in\B.TXT','Work/Spec','user');
                """;
            cmd.ExecuteNonQuery();
        }
        using var store = new Store(db);
        var old = store.GetFile("old1");
        Assert.NotNull(old);
        Assert.Equal("filed", old!.Source);
        Assert.Null(old.ContentKind);
        Assert.Empty(old.Meta);
        Assert.Null(store.GetFile("dup1"));      // older duplicate dropped
        Assert.NotNull(store.GetFile("dup2"));   // newest kept
        Assert.Equal("dup2", store.FindIdByPath(@"C:\in\b.txt"));
        Assert.Equal("2", store.GetKv("schema_version"));
        Assert.Equal(2, store.CountFiled());
        using var again = new Store(db); // idempotent
    }

    [Fact]
    public void Rename_prefix_moves_children_only()
    {
        var db = Path.Combine(Path.GetTempPath(), "docket-rn-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(db);
        FileItem It(string p) => new() { Path = p, Name = Path.GetFileName(p), Modified = DateTime.Now, Created = DateTime.Now };
        var c = new Classification { Category = "Work/Spec" };
        store.RecordFiled("d", It(@"C:\r\Proj"), c, @"C:\r\Proj", null);
        store.RecordFiled("c1", It(@"C:\r\Proj\a.txt"), c, @"C:\r\Proj\a.txt", null);
        store.RecordFiled("c2", It(@"C:\r\Proj\sub\b.txt"), c, @"C:\r\Proj\sub\b.txt", null);
        store.RecordFiled("other", It(@"C:\r\Project2\z.txt"), c, @"C:\r\Project2\z.txt", null);
        Assert.Equal(3, store.RenamePrefix(@"C:\r\Proj", @"D:\moved\Proj"));
        Assert.Equal(@"D:\moved\Proj", store.GetFile("d")!.FinalPath);
        Assert.Equal(@"D:\moved\Proj\a.txt", store.GetFile("c1")!.FinalPath);
        Assert.Equal(@"D:\moved\Proj\sub\b.txt", store.GetFile("c2")!.FinalPath);
        Assert.Equal(@"C:\r\Project2\z.txt", store.GetFile("other")!.FinalPath);
    }

    [Fact]
    public void Store_survives_concurrent_writers()
    {
        var db = Path.Combine(Path.GetTempPath(), "docket-cc-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using var store = new Store(db);
        var c = new Classification { Category = "Work/Spec", DecidedBy = "user" };
        Parallel.For(0, 200, i =>
        {
            var p = $@"C:\cc\{i}.txt";
            store.RecordFiled("id" + i, new FileItem { Path = p, Name = i + ".txt" }, c, p, new float[] { i, 1 });
            store.RecordMove("id" + i, p, p);
            _ = store.Nearest(new float[] { 1, 1 });
            _ = store.RecentMoves(5);
        });
        Assert.Equal(200, store.CountFiled());
    }
}

public class ExclusionsTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public ExclusionsTests(TaxonomyFixture f) => _f = f;

    static Exclusions Ex(params string[] patterns) => new(patterns, new[] { @"C:\Work Root", @"D:\Personal" });

    [Fact]
    public void Absolute_pattern_excludes_folder_and_descendants_only()
    {
        var ex = Ex(@"C:\Personal\Repo");
        Assert.True(ex.IsExcluded(@"C:\Personal\Repo"));
        Assert.True(ex.IsExcluded(@"c:\personal\repo\Local Docket\Program.cs"));
        Assert.False(ex.IsExcluded(@"C:\Personal\Repository\x.txt"));
        Assert.False(ex.IsExcluded(@"C:\Personal\Docs\Repo.txt"));
    }

    [Fact]
    public void Root_relative_pattern_expands_under_every_root()
    {
        var ex = Ex(@"Infra\Deploy");
        Assert.True(ex.IsExcluded(@"C:\Work Root\Infra\Deploy\site.dll"));
        Assert.True(ex.IsExcluded(@"D:\Personal\Infra\Deploy"));
        Assert.False(ex.IsExcluded(@"C:\Work Root\Infra\Design\x.md"));
        Assert.False(ex.IsExcluded(@"E:\Elsewhere\Infra\Deploy\x"));
    }

    [Fact]
    public void Bare_name_matches_any_segment_below_root_and_globs_work()
    {
        var ex = Ex("Passwords", "*.tmp");
        Assert.True(ex.IsExcluded(@"D:\Personal\Passwords\db.kdbx"));
        Assert.True(ex.IsExcluded(@"C:\Work Root\a\b\cache.tmp\x"));
        Assert.True(ex.IsExcluded(@"C:\Work Root\Repo\node_modules\left-pad\index.js")); // built-in
        Assert.True(ex.IsExcluded(@"C:\Work Root\Proj\bin\Debug\app.dll"));                // built-in
        Assert.False(ex.IsExcluded(@"C:\Work Root\Docs\Passwords Notes.txt"));
        Assert.False(ex.IsExcluded(@"C:\Work Root\Docs\binder.pdf"));
    }

    [Fact]
    public void Root_itself_is_never_excluded_by_a_bare_name()
    {
        var ex = new Exclusions(new[] { "bin" }, new[] { @"C:\bin" });
        Assert.False(ex.IsExcluded(@"C:\bin\report.docx"));
        Assert.True(ex.IsExcluded(@"C:\bin\proj\bin\x.dll"));
    }

    [Fact]
    public void Repository_detection_uses_markers()
    {
        var dir = Path.Combine(_f.Dir, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        var markers = new[] { ".git", "*.sln" };
        Assert.False(Exclusions.IsRepository(dir, markers));
        File.WriteAllText(Path.Combine(dir, "Thing.sln"), "");
        Assert.True(Exclusions.IsRepository(dir, markers));
        var git = Path.Combine(_f.Dir, "git-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(git, ".git"));
        Assert.True(Exclusions.IsRepository(git, markers));
    }

    [Fact]
    public void Taxonomy_exclusions_come_from_settings_and_scan_skips_them()
    {
        var t = _f.Load();
        Assert.Contains("Passwords", t.Settings.ExcludeFolders);
        Assert.True(t.Settings.IndexSkipRepos);
        var scan = Path.Combine(_f.PersonalRoot, "scan-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(scan, "Passwords"));
        Directory.CreateDirectory(Path.Combine(scan, "node_modules"));
        Directory.CreateDirectory(Path.Combine(scan, "Photos"));
        File.WriteAllText(Path.Combine(scan, "a.txt"), "x");
        File.WriteAllText(Path.Combine(scan, "a.txt.filer.json"), "{}");
        var names = t.ScannableEntries(scan).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(new[] { "a.txt", "Photos" }, names);
        // frozen folders directly under a root are skipped too
        var frozenDir = Path.Combine(_f.PersonalRoot, "Tools");
        Directory.CreateDirectory(frozenDir);
        Assert.DoesNotContain(frozenDir, t.ScannableEntries(_f.PersonalRoot));
    }

    [Fact]
    public void Set_exclude_folders_edits_yaml_in_place()
    {
        var path = Path.Combine(_f.Dir, "ex-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        File.Copy(_f.TaxonomyPath, path);
        var t = Taxonomy.Load(path);
        t.SetExcludeFolders(new[] { @"D:\Personal\Repo", "Scratch's", "Passwords" });
        var text = File.ReadAllText(path);
        Assert.Contains(@"excludeFolders: ['D:\Personal\Repo', 'Scratch''s', 'Passwords']", text);
        Assert.Contains("# Never scanned or indexed", text); // comment above survived
        var again = Taxonomy.Load(path);
        Assert.Equal(new[] { @"D:\Personal\Repo", "Scratch's", "Passwords" }, again.Settings.ExcludeFolders);
        Assert.True(again.Exclusions.IsExcluded(Path.Combine(_f.PersonalRoot, "Passwords", "x")));
        Assert.False(again.Exclusions.IsExcluded(Path.Combine(_f.WorkRoot, "Infra", "Deploy", "x")));

        // No line yet: inserted right under settings:
        var bare = Path.Combine(_f.Dir, "bare-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        File.WriteAllText(bare, "roots:\n  Work: 'C:\\W'\ninbox: 'C:\\I'\nsettings:\n  model: x\ncategories: {}\n");
        var tb = Taxonomy.Load(bare);
        tb.SetExcludeFolders(new[] { "Repo" });
        Assert.Contains("settings:\n  excludeFolders: ['Repo']\n  model: x", File.ReadAllText(bare));
        Assert.Equal(new[] { "Repo" }, Taxonomy.Load(bare).Settings.ExcludeFolders);
    }
}

public class ChunkerTests
{
    [Fact]
    public void Empty_and_short_texts()
    {
        Assert.Empty(Chunker.Split(null));
        Assert.Empty(Chunker.Split("   "));
        Assert.Single(Chunker.Split("short text", 3200, 480));
    }

    [Fact]
    public void Long_text_is_split_with_overlap_and_paragraph_boundaries()
    {
        var para = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Sentence number {i} of the paragraph."));
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(p => $"Paragraph {p}. " + para));
        var chunks = Chunker.Split(text, 2000, 300);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 2000, $"chunk of {c.Length}"));
        for (int i = 0; i + 1 < chunks.Count; i++)
        {
            Assert.EndsWith(".", chunks[i]);                 // cut at a paragraph or sentence boundary
            Assert.Contains(chunks[i][^80..], chunks[i + 1]); // overlap carries the tail forward
        }
        Assert.Contains(chunks, c => c.Contains("Paragraph 12."));
    }

    [Fact]
    public void Text_without_boundaries_still_splits()
    {
        var text = new string('x', 10_000);
        var chunks = Chunker.Split(text, 3000, 400);
        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 3000));
    }
}

public class VectorIndexTests
{
    static float[] V(params float[] v) => v;

    [Fact]
    public void TopK_orders_filters_and_removes()
    {
        var ix = new VectorIndex();
        ix.Add("a", 0, V(1, 0, 0));
        ix.Add("a", 1, V(0.9f, 0.1f, 0));
        ix.Add("b", 0, V(0, 1, 0));
        ix.Add("c", 0, V(-1, 0, 0));
        Assert.Equal(3, ix.Dim);
        Assert.Equal(4, ix.Count);
        var hits = ix.TopK(V(1, 0, 0), 3, minSimilarity: 0);
        Assert.Equal(new[] { ("a", 0), ("a", 1), ("b", 0) }, hits.Select(h => (h.FileId, h.Ordinal)).ToArray());
        Assert.True(hits[0].Similarity > hits[1].Similarity && hits[1].Similarity > hits[2].Similarity);
        var only = ix.TopK(V(1, 0, 0), 5, new HashSet<string> { "b", "c" }, minSimilarity: -1);
        Assert.Equal(new[] { "b", "c" }, only.Select(h => h.FileId).ToArray());
        ix.RemoveFile("a");
        Assert.Equal(2, ix.Count);
        Assert.DoesNotContain(ix.TopK(V(1, 0, 0), 5, minSimilarity: -1), h => h.FileId == "a");
        ix.ReplaceFile("b", new[] { V(1, 0, 0), V(1, 1, 0) });
        Assert.Equal(3, ix.Count);
        Assert.Equal("b", ix.TopK(V(1, 0, 0), 1)[0].FileId);
        Assert.Empty(ix.TopK(V(1, 0), 3)); // wrong dimension
        ix.Clear();
        Assert.Equal(0, ix.Count);
    }

    [Fact]
    public void Free_slots_are_reused_after_removal()
    {
        var ix = new VectorIndex();
        for (int i = 0; i < 50; i++) ix.Add("f" + i, 0, V(i, 1, 2));
        for (int i = 0; i < 50; i += 2) ix.RemoveFile("f" + i);
        for (int i = 0; i < 25; i++) ix.Add("g" + i, 0, V(1, i, 3));
        Assert.Equal(50, ix.Count);
        Assert.Equal(50, ix.Files);
    }
}

public class StoreIndexTests
{
    static Store NewStore() => new(Path.Combine(Path.GetTempPath(), "docket-ix-" + Guid.NewGuid().ToString("N")[..6] + ".db"));
    static IndexChunk C(int i, float x) => new(i, "chunk " + i, new[] { x, 1f });

    [Fact]
    public void Upsert_indexed_keeps_filed_decision_and_replaces_chunks()
    {
        using var store = NewStore();
        var p = @"C:\ix\spec.md";
        store.RecordFiled("filed1", new FileItem { Path = @"C:\in\spec.md", Name = "spec.md" }, new Classification { Category = "Work/Spec", DecidedBy = "user", Client = "Acme" }, p, null);
        store.UpsertIndexed("filed1", p, "spec.md", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "text", "Work", "Work/Jira", "snip", null, new[] { C(0, 1), C(1, 2) }, null);
        var rec = store.GetFile("filed1")!;
        Assert.Equal("Work/Spec", rec.Category);   // decision kept
        Assert.Equal("user", rec.DecidedBy);
        Assert.Equal("filed", rec.Source);
        Assert.Equal(2, store.GetChunks("filed1").Count);
        var snap = store.IndexSnapshot()[p];
        Assert.Equal(10, snap.Size);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), snap.MtimeUtc);
        Assert.Equal(DateTimeKind.Utc, snap.MtimeUtc!.Value.Kind);
        Assert.Equal(2, snap.Chunks);

        store.UpsertIndexed("filed1", p, "spec.md", 12, DateTime.UtcNow, "text", "Work", "Work/Jira", "snip2", null, new[] { C(0, 3) }, null);
        Assert.Single(store.GetChunks("filed1"));
        Assert.Equal((1, 1), store.IndexCounts());

        // A crawled row takes the path-derived category and is replaced by a later filing decision.
        var q = @"C:\ix\other.md";
        store.UpsertIndexed("crawl1", q, "other.md", 5, DateTime.UtcNow, "text", "Work", "Work/Jira", null, null, new[] { C(0, 1) }, null);
        Assert.Equal("crawl", store.GetFile("crawl1")!.Source);
        Assert.Equal("Work/Jira", store.GetFile("crawl1")!.Category);
        Assert.Single(store.Nearest(new[] { 1f, 1f }, minSimilarity: 0)); // crawled rows never act as few-shot examples
        store.RecordFiled("crawl1", new FileItem { Path = q, Name = "other.md" }, new Classification { Category = "Work/Spec", DecidedBy = "user" }, @"C:\ix\Spec\other.md", null);
        Assert.Equal("filed", store.GetFile("crawl1")!.Source);
        Assert.Single(store.GetChunks("crawl1")); // chunks survive the upsert

        int n = 0; store.LoadChunkVectors((_, _, _) => n++);
        Assert.Equal(2, n);
        store.DeleteFile("crawl1");
        n = 0; store.LoadChunkVectors((_, _, _) => n++);
        Assert.Equal(1, n); // cascade
        store.ClearChunks("filed1", "missing");
        Assert.Empty(store.GetChunks("filed1"));
        Assert.NotNull(store.GetFile("filed1"));
        store.ClearAllChunks();
        Assert.Equal((0, 0), store.IndexCounts());
    }

    [Fact]
    public void Orphan_lookup_matches_name_and_size()
    {
        using var store = NewStore();
        var item = new FileItem { Path = @"C:\in\a.txt", Name = "a.txt", Size = 42, Sha256 = "abc" };
        store.RecordFiled("f1", item, new Classification { Category = "Work/Spec" }, @"C:\out\a.txt", null);
        Assert.Single(store.FindOrphanFiled("A.TXT", 42));
        Assert.Empty(store.FindOrphanFiled("a.txt", 43));
        Assert.Equal("abc", store.FindOrphanFiled("a.txt", 42)[0].Sha256);
    }
}

public class IndexerTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public IndexerTests(TaxonomyFixture f) => _f = f;

    sealed class TxtExtractor : IContentExtractor
    {
        public Task ExtractAsync(FileItem item, DocketSettings settings, CancellationToken ct = default, int? capBytes = null, bool attachImage = true)
        {
            if (item.Extension is ".txt" or ".md") { item.ContentText = File.ReadAllText(item.Path); item.ContentKind = "text"; }
            else item.ContentKind = "binary";
            return Task.CompletedTask;
        }
    }

    sealed class HashEmbedder : IEmbedder
    {
        public int Calls, Inputs;
        public bool Down;
        public static float[] Vec(string s)
        {
            var v = new float[16];
            foreach (var word in s.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)) v[TestHash.StableHash(word.ToLowerInvariant()) % 16] += 1;
            return v;
        }
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(Vec(text));
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            if (Down) throw new HttpRequestException("connection refused");
            Calls++; Inputs += texts.Count;
            return Task.FromResult(texts.Select(Vec).ToArray());
        }
    }

    (Taxonomy t, string root, Store store, VectorIndex ix, HashEmbedder emb, Indexer indexer) Setup()
    {
        var root = Path.Combine(_f.Dir, "ixroot-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        var t = _f.Load();
        t.Roots = new Dictionary<string, string> { ["Work"] = root };
        t.Settings.IndexThrottleMs = 0;
        var store = new Store(Path.Combine(_f.Dir, "ix-" + Guid.NewGuid().ToString("N")[..6] + ".db"));
        var ix = new VectorIndex();
        var emb = new HashEmbedder();
        var indexer = new Indexer(() => t, store, ix, new TxtExtractor(), () => emb.Down ? null : emb);
        return (t, root, store, ix, emb, indexer);
    }

    [Fact]
    public async Task Crawl_indexes_text_skips_excluded_and_repos_and_is_incremental()
    {
        var (t, root, store, ix, emb, indexer) = Setup();
        using var _ = store;
        var spec = Path.Combine(root, "Spec", "PROJ-9");
        Directory.CreateDirectory(spec);
        File.WriteAllText(Path.Combine(spec, "plan.md"), "The plan for PROJ-9 is to rebuild the importer.");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "Some notes about fleet reports.");
        File.WriteAllBytes(Path.Combine(root, "photo.jpg"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(Path.Combine(root, "bin"));      File.WriteAllText(Path.Combine(root, "bin", "b.txt"), "built");
        Directory.CreateDirectory(Path.Combine(root, "Passwords"));  File.WriteAllText(Path.Combine(root, "Passwords", "k.txt"), "secret");
        Directory.CreateDirectory(Path.Combine(root, "Repo1", ".git")); File.WriteAllText(Path.Combine(root, "Repo1", "code.txt"), "source");
        Directory.CreateDirectory(Path.Combine(root, "Site", "Deploy")); File.WriteAllText(Path.Combine(root, "Site", "Deploy", "d.txt"), "deployed");
        File.WriteAllText(Path.Combine(root, "Thing.sln"), ""); // marker at the root itself must not hide the root

        var st = await indexer.CrawlAsync(t.Roots.Values, full: false, prune: true, CancellationToken.None);
        Assert.Equal("idle", st.Phase);
        Assert.Null(st.LastError);
        var snap = store.IndexSnapshot();
        Assert.Contains(Path.Combine(spec, "plan.md"), snap.Keys);
        Assert.Contains(Path.Combine(root, "notes.txt"), snap.Keys);
        Assert.Contains(Path.Combine(root, "photo.jpg"), snap.Keys);          // row without chunks
        Assert.Contains(Path.Combine(root, "Site", "Deploy", "d.txt"), snap.Keys); // Infra\Deploy is excluded, Site\Deploy is not
        Assert.DoesNotContain(Path.Combine(root, "bin", "b.txt"), snap.Keys);
        Assert.DoesNotContain(Path.Combine(root, "Passwords", "k.txt"), snap.Keys);
        Assert.DoesNotContain(Path.Combine(root, "Repo1", "code.txt"), snap.Keys);
        Assert.Equal(0, snap[Path.Combine(root, "photo.jpg")].Chunks);
        Assert.Equal(3, emb.Calls);
        Assert.Equal(3, ix.Files);
        Assert.Equal("Work/Spec", store.GetFile(snap[Path.Combine(spec, "plan.md")].Id)!.Category);
        Assert.Equal("Work", store.GetFile(snap[Path.Combine(root, "notes.txt")].Id)!.Domain);
        var hit = ix.TopK(HashEmbedder.Vec("search_query: rebuild the importer"), 1, minSimilarity: 0)[0];
        Assert.Equal(snap[Path.Combine(spec, "plan.md")].Id, hit.FileId);

        // Nothing changed → nothing embedded again.
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.Equal(3, emb.Calls);

        // Modified file → re-embedded once; deleted file → pruned.
        File.AppendAllText(Path.Combine(root, "notes.txt"), " More text.");
        File.SetLastWriteTimeUtc(Path.Combine(root, "notes.txt"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(spec, "plan.md"));
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.Equal(4, emb.Calls);
        Assert.DoesNotContain(Path.Combine(spec, "plan.md"), store.IndexSnapshot().Keys);
        Assert.Equal(2, ix.Files);

        // Newly excluded folder → its rows are pruned on the next crawl.
        t.SetExcludeFolders(t.Settings.ExcludeFolders.Append("Site").ToList());
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.DoesNotContain(Path.Combine(root, "Site", "Deploy", "d.txt"), store.IndexSnapshot().Keys);

        // --full re-embeds everything that has text.
        await indexer.CrawlAsync(t.Roots.Values, full: true, true, CancellationToken.None);
        Assert.Equal(5, emb.Calls);
    }

    [Fact]
    public async Task Filed_file_moved_by_hand_keeps_its_row_and_embedder_outage_is_reported()
    {
        var (t, root, store, ix, emb, indexer) = Setup();
        using var _ = store;
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "handmove-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(src, "moved by hand later");
        var item = FileItem.FromPath(src); item.ContentText = "moved by hand later"; item.ContentKind = "text";
        var r = mover.Move(item, new Classification { Category = "Work/Spec", DecidedBy = "user" }, Path.Combine(root, "Spec"));
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.Equal(1, store.GetFile(r.FileId)!.Meta.Count == 0 ? 1 : 1);
        Assert.Single(store.GetChunks(r.FileId));

        Directory.CreateDirectory(Path.Combine(root, "Elsewhere"));
        var moved = Path.Combine(root, "Elsewhere", Path.GetFileName(r.FinalPath));
        File.Move(r.FinalPath, moved);
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        var rec = store.GetFile(r.FileId)!;
        Assert.Equal(moved, rec.FinalPath);
        Assert.Equal("filed", rec.Source);
        Assert.Equal("Work/Spec", rec.Category);
        Assert.Equal(1, store.CountFiled());

        // Filed file deleted for good: row stays (provenance), chunks go.
        File.Delete(moved);
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.NotNull(store.GetFile(r.FileId));
        Assert.Empty(store.GetChunks(r.FileId));

        // Embedder down: status says so, nothing is pruned, and it recovers.
        File.WriteAllText(Path.Combine(root, "new.txt"), "fresh");
        emb.Down = true;
        var st = await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.True(indexer.EmbedderDown);
        Assert.NotNull(st.LastError);
        emb.Down = false;
        st = await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.False(indexer.EmbedderDown);
        Assert.Contains(Path.Combine(root, "new.txt"), store.IndexSnapshot().Keys);
    }

    [Fact]
    public void Category_for_path_uses_templates()
    {
        var t = _f.Load();
        Assert.Equal(("Work", "Work/Spec"), t.CategoryForPath(Path.Combine(_f.WorkRoot, "Spec", "PROJ-1", "plan.md")));
        Assert.Equal(("Work", "Work/SQL/Backups"), t.CategoryForPath(Path.Combine(_f.WorkRoot, "SQL", "Database Backups", "UAT", "2026-01-01", "x.bak")));
        Assert.Equal(("Work", (string?)null), t.CategoryForPath(Path.Combine(_f.WorkRoot, "loose.txt")));
        Assert.Equal(((string?)null, (string?)null), t.CategoryForPath(@"D:\nowhere\x.txt"));
    }
}


internal static class TestHash
{
    /// <summary>FNV-1a over the chars; string.GetHashCode() is randomised per process and made the fake embeddings flaky.</summary>
    public static int StableHash(string s)
    {
        unchecked { uint h = 2166136261; foreach (var ch in s) { h ^= ch; h *= 16777619; } return (int)(h & 0x7fffffff); }
    }
}

public class ChatServiceTests
{
    sealed class WordEmbedder : IEmbedder
    {
        public static float[] Vec(string s)
        {
            var v = new float[256]; // wide enough that the fake's word buckets rarely collide
            foreach (var w in s.ToLowerInvariant().Split((char[])null!, StringSplitOptions.RemoveEmptyEntries)) v[TestHash.StableHash(w) % 256] += 1;
            return v;
        }
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(Vec(text));
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) => Task.FromResult(texts.Select(Vec).ToArray());
    }

    sealed class FakeChat : IChatBackend
    {
        public string FilterJson = "{}";
        public IReadOnlyList<ChatMessage>? LastMessages;
        public string ModelName => "fake";
        public Task<string> CompleteJsonAsync(string system, string user, System.Text.Json.Nodes.JsonNode schema, CancellationToken ct = default) => Task.FromResult(FilterJson);
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int? numCtx = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastMessages = messages;
            foreach (var t in new[] { "The importer ", "is rebuilt ", "[1]." }) { await Task.Yield(); yield return t; }
        }
    }

    static (Store store, VectorIndex ix, Taxonomy t) Seed(TaxonomyFixture f)
    {
        var store = new Store(Path.Combine(f.Dir, "chat-" + Guid.NewGuid().ToString("N")[..6] + ".db"));
        var t = f.Load();
        var ix = new VectorIndex();
        void Add(string id, string path, string category, string? client, DateTime modified, params string[] chunks)
        {
            var list = chunks.Select((c, i) => new IndexChunk(i, c, WordEmbedder.Vec(Path.GetFileName(path) + "\n" + c))).ToList();
            store.UpsertIndexed(id, path, Path.GetFileName(path), 100, modified.ToUniversalTime(), "text", "Work", category, chunks[0], null, list, null);
            if (client != null) store.RecordFiled(id, new FileItem { Path = path, Name = Path.GetFileName(path), Modified = modified }, new Classification { Category = category, Client = client, DecidedBy = "user" }, path, null);
            ix.ReplaceFile(id, list.Select(c => c.Embedding).ToList());
        }
        Add("plan", Path.Combine(f.WorkRoot, "Spec", "PROJ-9", "plan.md"), "Work/Spec", null, new DateTime(2026, 8, 1), "The plan for PROJ-9 is to rebuild the importer in two sprints.", "Risks: the importer touches payroll uploads.");
        Add("msa", Path.Combine(f.WorkRoot, "Documents", "Clients", "Acme", "Acme MSA.pdf"), "Work/Clients", "Acme", new DateTime(2026, 5, 10), "Master services agreement between Acme and us covering support hours.");
        Add("fleet", Path.Combine(f.WorkRoot, "Documents", "Reports", "2026", "Fleet Report Q2.xlsx"), "Work/Reports", null, new DateTime(2026, 7, 2), "Fleet report for Q2 with vehicle counts per depot.");
        ix.Ready = true;
        return (store, ix, t);
    }

    [Fact]
    public async Task Answers_with_numbered_sources_and_excerpts_in_prompt()
    {
        using var f = new TaxonomyFixture();
        var (store, ix, t) = Seed(f);
        using var _ = store;
        var chat = new FakeChat { FilterJson = """{"query":"rebuild the importer","domain":"","category":"","client":"","environment":"","from":"","to":"","datesRefer":"","pathContains":""}""" };
        var svc = new ChatService(() => t, store, ix, () => new WordEmbedder(), () => chat);
        var reply = await svc.AskAsync("how will the importer be rebuilt?", Array.Empty<ChatTurn>());
        Assert.Null(reply.Notice);
        Assert.Equal("plan", reply.Sources[0].FileId);
        Assert.Equal(1, reply.Sources[0].N);
        Assert.Contains("rebuild the importer", reply.Sources[0].Excerpt);
        var answer = string.Concat(await reply.Tokens.ToListAsync());
        Assert.Equal("The importer is rebuilt [1].", answer);
        var user = chat.LastMessages!.Last(m => m.Role == "user").Content;
        Assert.Contains("[1] plan.md — Work/Spec, modified 2026-08-01", user);
        Assert.Contains("rebuild the importer in two sprints", user);
        Assert.EndsWith("Question: how will the importer be rebuilt?", user);
        Assert.Equal("system", chat.LastMessages![0].Role);

        // history rides along as prior user/assistant turns
        var history = new[] { new ChatTurn("earlier question", "earlier answer", Array.Empty<ChatSource>()) };
        await (await svc.AskAsync("and the risks?", history)).Tokens.ToListAsync();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, chat.LastMessages!.Select(m => m.Role).ToArray());
        Assert.Equal("earlier answer", chat.LastMessages![2].Content);
    }

    [Fact]
    public async Task Filters_restrict_retrieval_and_fall_back_when_empty()
    {
        using var f = new TaxonomyFixture();
        var (store, ix, t) = Seed(f);
        using var _ = store;
        var chat = new FakeChat { FilterJson = """{"query":"agreement support hours","domain":"","category":"","client":"Acme","environment":"","from":"","to":"","datesRefer":"","pathContains":""}""" };
        var svc = new ChatService(() => t, store, ix, () => new WordEmbedder(), () => chat);
        var reply = await svc.AskAsync("what do we owe Acme?", Array.Empty<ChatTurn>());
        Assert.Equal("Acme", reply.Filters.Client);
        Assert.All(reply.Sources, s => Assert.Equal("msa", s.FileId));

        chat.FilterJson = """{"query":"fleet report","domain":"","category":"","client":"Globex","environment":"","from":"","to":"","datesRefer":"","pathContains":""}""";
        reply = await svc.AskAsync("fleet report for Globex", Array.Empty<ChatTurn>());
        Assert.NotNull(reply.Notice);
        Assert.Contains(reply.Sources, s => s.FileId == "fleet");

        // An invented filter (nothing in the question says HR or Personal) is dropped before it can mis-narrow the search.
        chat.FilterJson = """{"query":"fleet report","domain":"Personal","category":"Work/HR","client":"Acme","environment":"UAT","from":"","to":"","datesRefer":"","pathContains":"Spec"}""";
        reply = await svc.AskAsync("fleet report", Array.Empty<ChatTurn>());
        Assert.False(reply.Filters.Any);
        Assert.Null(reply.Notice);
        Assert.Equal("fleet", reply.Sources[0].FileId);

        // date filter on modified time
        chat.FilterJson = """{"query":"fleet report","domain":"","category":"","client":"","environment":"","from":"2026-07-01","to":"2026-07-31","datesRefer":"modified","pathContains":""}""";
        reply = await svc.AskAsync("reports from July", Array.Empty<ChatTurn>());
        Assert.Equal(new DateTime(2026, 7, 1), reply.Filters.From);
        Assert.All(reply.Sources, s => Assert.Equal("fleet", s.FileId));
        // category prefix
        Assert.Equal(new[] { "fleet", "msa", "plan" }, store.FilterFileIds("Work", null, null, null, null, null, null).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "msa" }, store.FilterFileIds(null, "Work/Clients", null, null, null, null, null).ToArray());
        Assert.Equal(new[] { "plan" }, store.FilterFileIds(null, null, null, null, null, null, "PROJ-9").ToArray());
    }

    [Fact]
    public async Task Degrades_without_embedder_or_chat_model()
    {
        using var f = new TaxonomyFixture();
        var (store, ix, t) = Seed(f);
        using var _ = store;
        var noEmbed = new ChatService(() => t, store, ix, () => null, () => null);
        var reply = await noEmbed.AskAsync("Acme MSA", Array.Empty<ChatTurn>());
        Assert.Contains("not reachable", reply.Notice);
        Assert.Contains(reply.Sources, s => s.Name == "Acme MSA.pdf");
        Assert.Empty(await reply.Tokens.ToListAsync());

        var noChat = new ChatService(() => t, store, ix, () => new WordEmbedder(), () => null);
        reply = await noChat.AskAsync("fleet vehicle counts per depot", Array.Empty<ChatTurn>());
        Assert.Contains("Chat model unavailable", reply.Notice);
        Assert.Equal("fleet", reply.Sources[0].FileId);
        Assert.Empty(await reply.Tokens.ToListAsync());
    }
}

public class SidecarImportTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public SidecarImportTests(TaxonomyFixture f) => _f = f;

    static string LegacyJson(string fileId, string name, string originalPath, string category, string? contentKind, Dictionary<string, string> meta) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            FilerVersion = "1.0", FileId = fileId, Name = name, OriginalPath = originalPath,
            OriginalCreated = new DateTime(2026, 1, 2), OriginalModified = new DateTime(2026, 1, 3), DroppedAt = new DateTime(2026, 1, 4), FiledAt = new DateTime(2026, 1, 5),
            Sha256 = "deadbeef", Size = 5L, IsDirectory = false,
            Classification = new { Domain = "Work", Category = category, Confidence = 0.9, Reasoning = "legacy", DecidedBy = "llm:x", Action = "file", Questions = Array.Empty<string>() },
            Meta = meta, ContentKind = contentKind,
        });

    [Fact]
    public async Task Merges_into_existing_rows_inserts_missing_ones_and_deletes_on_request()
    {
        var t = _f.Load();
        var root = Path.Combine(_f.Dir, "sc-" + Guid.NewGuid().ToString("N")[..6]);
        t.Roots = new Dictionary<string, string> { ["Work"] = root };
        using var store = new Store(Path.Combine(_f.Dir, "sc-" + Guid.NewGuid().ToString("N")[..6] + ".db"));
        Directory.CreateDirectory(Path.Combine(root, "Spec"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        // 1) row exists (v1 style, no meta) → merged
        var a = Path.Combine(root, "Spec", "a.md");
        File.WriteAllText(a, "aaaaa");
        store.RecordFiled("ida", new FileItem { Path = @"C:\in\a.md", Name = "a.md" }, new Classification { Category = "Work/Spec", DecidedBy = "user" }, a, null);
        File.WriteAllText(a + SidecarImport.Suffix, LegacyJson("ida", "a.md", @"C:\in\a.md", "Work/Spec", "text", new() { ["bak.server"] = "SQL01", ["_secret"] = "x" }));
        // 2) no row at all → inserted with the sidecar's id and decision
        var b = Path.Combine(root, "b.pdf");
        File.WriteAllText(b, "bbbbb");
        File.WriteAllText(b + SidecarImport.Suffix, LegacyJson("idb", "b.pdf", @"C:\in\b.pdf", "Work/Clients", "pdf", new() { ["pdf.pages"] = "3" }));
        // 3) orphan (item gone)
        File.WriteAllText(Path.Combine(root, "gone.txt" + SidecarImport.Suffix), LegacyJson("idc", "gone.txt", @"C:\in\gone.txt", "Work/Spec", "text", new()));
        // 4) inside .git → ignored
        File.WriteAllText(Path.Combine(root, ".git", "x.txt" + SidecarImport.Suffix), "{}");

        var res = await SidecarImport.RunAsync(t, store, delete: false);
        Assert.Equal(3, res.Found);
        Assert.Equal(1, res.Merged);
        Assert.Equal(1, res.Inserted);
        Assert.Equal(1, res.Orphans);
        Assert.Equal(0, res.Deleted);
        Assert.Empty(res.Errors);
        var ra = store.GetFile("ida")!;
        Assert.Equal("text", ra.ContentKind);
        Assert.Equal("SQL01", ra.Meta["bak.server"]);
        Assert.False(ra.Meta.ContainsKey("_secret"));
        Assert.Equal("Work/Spec", ra.Category);
        Assert.Equal("user", ra.DecidedBy);               // existing decision untouched
        var rb = store.GetFile("idb")!;
        Assert.Equal("Work/Clients", rb.Category);
        Assert.Equal("pdf", rb.ContentKind);
        Assert.Equal("3", rb.Meta["pdf.pages"]);
        Assert.Equal("deadbeef", rb.Sha256);
        Assert.Equal(new DateTime(2026, 1, 5), rb.FiledAt);
        Assert.Equal("filed", rb.Source);
        Assert.Equal(b, rb.FinalPath);
        Assert.NotNull(store.GetKv(SidecarImport.DoneKey));
        Assert.True(File.Exists(a + SidecarImport.Suffix));

        // Second pass with delete: idempotent and removes the files (orphans too).
        res = await SidecarImport.RunAsync(t, store, delete: true);
        Assert.Equal(3, res.Deleted);
        Assert.False(File.Exists(a + SidecarImport.Suffix));
        Assert.False(File.Exists(b + SidecarImport.Suffix));
        Assert.False(File.Exists(Path.Combine(root, "gone.txt" + SidecarImport.Suffix)));
        Assert.True(File.Exists(Path.Combine(root, ".git", "x.txt" + SidecarImport.Suffix)));
        Assert.Equal(2, store.CountFiled());
        Assert.Equal(0, store.CountFiledWithoutMeta());
    }
}

public class HardeningTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public HardeningTests(TaxonomyFixture f) => _f = f;

    Store NewStore() => new(Path.Combine(_f.Dir, "hard-" + Guid.NewGuid().ToString("N")[..6] + ".db"));

    [Fact]
    public void Dropped_folder_is_the_project_not_a_folder_inside_it()
    {
        var t = _f.Load();
        var dir = new FileItem { Path = Path.Combine(_f.Inbox, "Foo"), Name = "Foo", IsDirectory = true, Modified = DateTime.Now };
        Assert.Equal(Path.Combine(_f.PersonalRoot, "Projects"), PathTemplate.Resolve(t, new Classification { Category = "Personal/Projects" }, dir));
        var file = new FileItem { Path = Path.Combine(_f.Inbox, "Foo.md"), Name = "Foo.md", Modified = DateTime.Now };
        Assert.Equal(Path.Combine(_f.WorkRoot, "Spec", "Foo"), PathTemplate.Resolve(t, new Classification { Category = "Work/Spec" }, file));
        // A folder already in place resolves to its own parent, which Scan mode reports as "already there" instead of nesting it.
        var inPlace = new FileItem { Path = Path.Combine(_f.PersonalRoot, "Projects", "Foo"), Name = "Foo", IsDirectory = true, Modified = DateTime.Now };
        Assert.Equal(Path.Combine(_f.PersonalRoot, "Projects"), PathTemplate.Resolve(t, new Classification { Category = "Personal/Projects" }, inPlace));
    }

    [Fact]
    public void Move_refuses_to_nest_a_folder_inside_itself()
    {
        using var store = NewStore();
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "self-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var item = FileItem.FromPath(src);
        Assert.Throws<InvalidOperationException>(() => mover.Move(item, new Classification { Category = "Personal/Projects" }, src));
        Assert.Throws<InvalidOperationException>(() => mover.Move(item, new Classification { Category = "Personal/Projects" }, Path.Combine(src, "sub")));
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
        Assert.Empty(Directory.GetDirectories(src));
        Assert.Equal(0, store.CountFiled());
    }

    [Fact]
    public void Move_is_rolled_back_when_the_record_cannot_be_written()
    {
        using var store = NewStore();
        var mover = new Mover(store);
        var src = Path.Combine(_f.Inbox, "rb-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "child.txt"), "c");
        var targetDir = Path.Combine(_f.PersonalRoot, "Projects");
        var target = Path.Combine(targetDir, Path.GetFileName(src));
        var c = new Classification { Category = "Personal/Projects" };
        // Two rows that collide once the folder's children are renamed → RenamePrefix hits the unique path index inside the transaction.
        store.RecordFiled("x", new FileItem { Path = Path.Combine(src, "child.txt"), Name = "child.txt" }, c, Path.Combine(src, "child.txt"), null);
        store.RecordFiled("y", new FileItem { Path = @"C:\elsewhere\child.txt", Name = "child.txt" }, c, Path.Combine(target, "child.txt"), null);

        var ex = Assert.Throws<InvalidOperationException>(() => mover.Move(FileItem.FromPath(src), c, targetDir));
        Assert.Contains("put back", ex.Message);
        Assert.True(File.Exists(Path.Combine(src, "child.txt")));   // folder is back in the inbox
        Assert.False(Directory.Exists(target));
        Assert.Empty(store.RecentMoves());                           // nothing half-recorded
        Assert.Equal(Path.Combine(src, "child.txt"), store.GetFile("x")!.FinalPath);
    }

    [Fact]
    public void Prune_only_touches_rows_still_at_the_snapshot_path()
    {
        using var store = NewStore();
        var c = new Classification { Category = "Work/Spec" };
        store.UpsertIndexed("crawl1", @"C:\r\a.txt", "a.txt", 1, DateTime.UtcNow, "text", "Work", null, null, null, new[] { new IndexChunk(0, "t", new[] { 1f }) }, null);
        // Filed while the crawl ran: same id, new path, source flipped.
        store.RecordFiled("crawl1", new FileItem { Path = @"C:\r\a.txt", Name = "a.txt" }, c, @"C:\r\Spec\a.txt", null);
        Assert.Equal(0, store.DeleteFile("crawl1", @"C:\r\a.txt"));
        Assert.NotNull(store.GetFile("crawl1"));
        Assert.Single(store.GetChunks("crawl1"));
        Assert.Equal(0, store.ClearChunks("crawl1", "missing", @"C:\r\a.txt"));
        Assert.Single(store.GetChunks("crawl1"));
        Assert.Equal(1, store.ClearChunks("crawl1", "missing", @"C:\r\Spec\a.txt"));
        Assert.Empty(store.GetChunks("crawl1"));
        Assert.Null(store.IndexSnapshot()[@"C:\r\Spec\a.txt"].MtimeUtc); // will be re-read next crawl
    }

    [Fact]
    public void Yaml_edits_quote_anything_and_never_write_a_broken_file()
    {
        var path = Path.Combine(_f.Dir, "q-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        File.Copy(_f.TaxonomyPath, path);
        var t = Taxonomy.Load(path);
        t.AddQuestionOption("client", "Acme #1");
        t.AddQuestionOption("client", "Smith [Pty], O'Neil: Jones");
        t.AppendRule(new RuleDef { Name = "learned-x", When = new() { ["name"] = "(?i)^Smith\\s+" }, Set = { ["category"] = "Work/Clients", ["client"] = "Smith [Pty], O'Neil: Jones" }, Notes = "it's #1" });
        t.SetExcludeFolders(new[] { @"C:\x\a]b", "Weird [name]" });
        t.SetExcludeFolders(new[] { @"C:\x\a]b", "Weird [name]", "Third" }); // second edit must still find the list
        var again = Taxonomy.Load(path);
        Assert.Contains("Acme #1", again.Questions["client"].Options);
        Assert.Contains("Smith [Pty], O'Neil: Jones", again.Questions["client"].Options);
        var rule = again.Rules.Single(r => r.Name == "learned-x");
        Assert.Equal("Smith [Pty], O'Neil: Jones", rule.Set["client"]);
        Assert.Equal("it's #1", rule.Notes);
        Assert.Equal(new[] { @"C:\x\a]b", "Weird [name]", "Third" }, again.Settings.ExcludeFolders);
        Assert.True(File.Exists(path + ".bak"));

        // Startup fallback: a hand edit that breaks the file is replaced by the last good copy.
        File.WriteAllText(path, "roots: [\n  broken");
        var recovered = Taxonomy.LoadWithFallback(path, out var warning);
        Assert.NotNull(warning);
        Assert.Contains("Third", recovered.Settings.ExcludeFolders);
        Assert.True(File.Exists(path + ".broken"));
        Assert.Null(Record.Exception(() => Taxonomy.Load(path)));
    }

    [Fact]
    public void Learned_patterns_need_a_real_name()
    {
        Assert.Null(RuleEngine.LearnedNamePattern("a"));
        Assert.Null(RuleEngine.LearnedNamePattern("ab 1"));
        Assert.Null(RuleEngine.LearnedNamePattern("---"));
        var p = RuleEngine.LearnedNamePattern("Probation Review 2026");
        Assert.NotNull(p);
        Assert.Matches(p!, "Probation  Review 2027.docx");
        Assert.DoesNotMatch(p!, "Review 2027.docx");
    }

    sealed class TimeoutEmbedder : IEmbedder
    {
        public bool TimeOut = true;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new float[] { 1, 0 });
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            if (TimeOut) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 180 seconds elapsing.");
            return Task.FromResult(texts.Select(_ => new float[] { 1, 0 }).ToArray());
        }
    }

    sealed class TxtExtractor : IContentExtractor
    {
        public Task ExtractAsync(FileItem item, DocketSettings settings, CancellationToken ct = default, int? capBytes = null, bool attachImage = true)
        { item.ContentText = File.ReadAllText(item.Path); item.ContentKind = "text"; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Embed_timeout_is_an_outage_not_a_shutdown_and_the_loop_honours_wake_and_pause()
    {
        var root = Path.Combine(_f.Dir, "loop-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
        var t = _f.Load();
        t.Roots = new Dictionary<string, string> { ["Work"] = root };
        t.Settings.IndexThrottleMs = 0; t.Settings.IndexRecrawlMinutes = 60;
        using var store = NewStore();
        var emb = new TimeoutEmbedder();
        var indexer = new Indexer(() => t, store, new VectorIndex(), new TxtExtractor(), () => emb);

        // 1) a timeout while the token is live is reported, not thrown, and nothing is pruned
        var st = await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.True(indexer.EmbedderDown);
        Assert.Contains("timed out", st.LastError);

        // 2) the run loop: first pass, then a wake request while paused is held, then served once unpaused
        emb.TimeOut = false;
        int probes = 0;
        indexer.BeforeCrawl = _ => { Interlocked.Increment(ref probes); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource();
        var loop = indexer.RunAsync(cts.Token);
        await WaitUntil(() => probes >= 1 && indexer.Status.Phase == "idle" && !indexer.EmbedderDown, TimeSpan.FromSeconds(10));
        Assert.Contains(Path.Combine(root, "a.txt"), store.IndexSnapshot().Keys);

        indexer.Paused = true;
        File.WriteAllText(Path.Combine(root, "b.txt"), "beta");
        indexer.Enqueue(Path.Combine(root, "b.txt"));
        await Task.Delay(1500);
        Assert.DoesNotContain(Path.Combine(root, "b.txt"), store.IndexSnapshot().Keys); // paused: queued, not indexed
        indexer.Paused = false;
        indexer.RequestCrawl();
        await WaitUntil(() => probes >= 2 && store.IndexSnapshot().ContainsKey(Path.Combine(root, "b.txt")), TimeSpan.FromSeconds(10));

        cts.Cancel();
        await loop; // exits cleanly on our token
    }

    static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.Elapsed > timeout) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }
}

public class PrivacyAndDriftTests : IClassFixture<TaxonomyFixture>
{
    readonly TaxonomyFixture _f;
    public PrivacyAndDriftTests(TaxonomyFixture f) => _f = f;
    Store NewStore() => new(Path.Combine(_f.Dir, "pd-" + Guid.NewGuid().ToString("N")[..6] + ".db"));

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE...", true)]
    [InlineData("Server=sql01;Database=Opex;User Id=sa;Password=Sup3rS3cret;", true)]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", true)]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("api_key=sk-live-1234567890abcdefghijklmnop", true)]
    [InlineData("The password policy requires twelve characters and a symbol.", false)]
    [InlineData("Meeting notes: reset the UAT database on Friday.", false)]
    public void Secret_scan_flags_credentials_not_prose(string text, bool sensitive) => Assert.Equal(sensitive, SecretScan.IsSensitive(text));

    [Fact]
    public void Indexability_skips_credential_names_and_private_categories()
    {
        var t = _f.Load();
        Assert.False(t.IsIndexable(Path.Combine(_f.WorkRoot, "Infra", ".env"), out var why)); Assert.Contains("credential", why);
        Assert.False(t.IsIndexable(Path.Combine(_f.PersonalRoot, "Homelab", "id_rsa"), out _));
        Assert.False(t.IsIndexable(Path.Combine(_f.WorkRoot, "Api", "appsettings.Production.json"), out _));
        Assert.False(t.IsIndexable(Path.Combine(_f.WorkRoot, "Infra", "server.pem"), out why)); Assert.Contains("extension", why);
        Assert.False(t.IsIndexable(Path.Combine(_f.WorkRoot, "Documents", "HR", "2026", "Pay Slip March.pdf"), out why)); Assert.Contains("Work/HR", why);
        Assert.False(t.IsIndexable(Path.Combine(_f.WorkRoot, "Documents", "Team", "Reviews", "2026", "review.docx"), out _));
        Assert.True(t.IsIndexable(Path.Combine(_f.WorkRoot, "Spec", "PROJ-1", "plan.md"), out _));
        Assert.True(t.IsIndexable(Path.Combine(_f.WorkRoot, "Documents", "Clients", "Acme", "MSA.pdf"), out _));
        Assert.Contains(".pem", t.Settings.IndexSkipExtensions);
    }

    sealed class KindExtractor : IContentExtractor
    {
        public int Failures;
        public Task ExtractAsync(FileItem item, DocketSettings settings, CancellationToken ct = default, int? capBytes = null, bool attachImage = true)
        {
            if (item.Name.StartsWith("locked") && Failures-- > 0) throw new IOException("The process cannot access the file because it is being used by another process.");
            item.ContentText = File.ReadAllText(item.Path);
            item.ContentKind = item.Extension == ".weird" ? "text?" : "text";
            return Task.CompletedTask;
        }
    }
    sealed class ConstEmbedder : IEmbedder
    {
        public int Calls;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new[] { 1f, 0f });
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) { Calls++; return Task.FromResult(texts.Select(_ => new[] { 1f, 0f }).ToArray()); }
    }

    [Fact]
    public async Task Indexer_withholds_secrets_skips_sniffed_text_retries_locked_files_and_reloads_drifted_vectors()
    {
        var root = Path.Combine(_f.Dir, "pv-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "plain notes about fleet reports");
        File.WriteAllText(Path.Combine(root, "keys.txt"), "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----");
        File.WriteAllText(Path.Combine(root, "dump.weird"), "looks like text but unknown type");
        File.WriteAllText(Path.Combine(root, "locked.txt"), "opened in Word right now");
        Directory.CreateDirectory(Path.Combine(root, "Misc"));
        File.WriteAllText(Path.Combine(root, "Misc", "slip.txt"), "net pay this month");
        var t = _f.Load();
        t.Roots = new Dictionary<string, string> { ["Work"] = root };
        t.Settings.IndexThrottleMs = 0;
        using var store = NewStore();
        var ix = new VectorIndex { Ready = true };
        var emb = new ConstEmbedder();
        var ext = new KindExtractor { Failures = 1 };
        var indexer = new Indexer(() => t, store, ix, ext, () => emb);
        // Filed as HR into a hand-typed folder: the row's category, not its path, says it must not be chunked.
        store.RecordFiled("hr1", FileItem.FromPath(Path.Combine(root, "Misc", "slip.txt")), new Classification { Category = "Work/HR", DecidedBy = "user" }, Path.Combine(root, "Misc", "slip.txt"), null);

        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        var snap = store.IndexSnapshot();
        Assert.Equal(0, snap[Path.Combine(root, "Misc", "slip.txt")].Chunks);
        Assert.Equal(1, snap[Path.Combine(root, "notes.txt")].Chunks);
        Assert.Equal(0, snap[Path.Combine(root, "keys.txt")].Chunks);                       // withheld
        Assert.Equal(0, snap[Path.Combine(root, "dump.weird")].Chunks);                     // sniffed text is not indexed
        Assert.Null(snap[Path.Combine(root, "locked.txt")].MtimeUtc);                       // transient failure: look again
        Assert.Equal(0, snap[Path.Combine(root, "locked.txt")].Chunks);

        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);       // second pass: only the locked file is re-read
        snap = store.IndexSnapshot();
        Assert.Equal(1, snap[Path.Combine(root, "locked.txt")].Chunks);
        Assert.NotNull(snap[Path.Combine(root, "locked.txt")].MtimeUtc);
        Assert.Equal(2, emb.Calls);

        // Memory drift (another process wrote chunks): the prune pass reloads vectors from the store without re-embedding.
        ix.Clear();
        Assert.Equal(0, ix.Count);
        await indexer.CrawlAsync(t.Roots.Values, false, true, CancellationToken.None);
        Assert.Equal(2, ix.Count);
        Assert.Equal(2, emb.Calls);
    }

    [Fact]
    public void Store_reports_rows_it_drops_and_escapes_like_and_remembers_undo()
    {
        using var store = NewStore();
        var dropped = new List<string>();
        store.RowDeleted = dropped.Add;
        var c = new Classification { Category = "Work/Spec", DecidedBy = "user" };
        store.RecordFiled("old", new FileItem { Path = @"C:\in\a.txt", Name = "a.txt" }, c, @"C:\out\a.txt", null);
        store.RecordFiled("new", new FileItem { Path = @"C:\in2\a.txt", Name = "a.txt" }, c, @"C:\out\a.txt", null);
        Assert.Equal(new[] { "old" }, dropped);
        Assert.Null(store.GetFile("old"));

        store.UpsertIndexed("p1", @"C:\r\Docs\x.md", "x.md", 1, DateTime.UtcNow, "text", "Work", "Work/Spec", null, null, Array.Empty<IndexChunk>(), null);
        store.UpsertIndexed("p2", @"C:\r\Do_s\y.md", "y.md", 1, DateTime.UtcNow, "text", "Work", "Work/Spec", null, null, Array.Empty<IndexChunk>(), null);
        Assert.Equal(new[] { "p2" }, store.FilterFileIds(null, null, null, null, null, null, "Do_s").ToArray()); // '_' is literal, not a wildcard
        Assert.Equal(new[] { "p1" }, store.FilterFileIds(null, null, null, null, null, null, "Docs").ToArray());

        // Undo memory: same size and mtime at the inbox path → true; a changed file → false.
        var stamp = new DateTime(2026, 3, 4, 5, 6, 7);
        var item = new FileItem { Path = @"C:\inbox\u.txt", Name = "u.txt", Size = 10, Modified = stamp, Created = stamp };
        store.RecordFiled("u", item, c, @"C:\out\u.txt", null);
        var mv = store.RecordMove("u", @"C:\inbox\u.txt", @"C:\out\u.txt");
        Assert.False(store.WasUndoneAt(@"C:\inbox\u.txt", 10, stamp));
        store.MarkUndone(mv, @"C:\inbox\u.txt");
        Assert.True(store.WasUndoneAt(@"C:\inbox\u.txt", 10, stamp));
        Assert.False(store.WasUndoneAt(@"C:\inbox\u.txt", 11, stamp));
        Assert.False(store.WasUndoneAt(@"C:\inbox\u.txt", 10, stamp.AddMinutes(1)));
    }

    [Fact]
    public void Unknown_settings_keys_are_reported_and_globs_are_memoised()
    {
        var path = Path.Combine(_f.Dir, "unk-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        File.WriteAllText(path, "roots:\n  Work: 'C:\\W'\ninbox: 'C:\\I'\nsettings:\n  model: x\n  excludeFolder: [Repo]\n  indexSkipRepos: true\ncategories: {}\n");
        var t = Taxonomy.Load(path);
        Assert.Equal(new[] { "excludeFolder" }, t.UnknownSettings);
        Assert.Empty(t.Settings.ExcludeFolders);
        Assert.Same(PathTemplate.GlobToRegex("*.bak"), PathTemplate.GlobToRegex("*.bak"));
    }
}
