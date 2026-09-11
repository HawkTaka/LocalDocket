# Architecture notes

Working notes for anyone changing Local Docket: how the pieces fit, the invariants that are easy to break, and the
conventions the code follows. `README.md` is the user-facing overview.

## What this is

Private Windows tray app (.NET 10, WPF) that watches `Desktop\_Inbox`, classifies whatever lands there
(deterministic rules first, local Ollama model second), asks the user only when needed, and moves items into
`D:\Work` (Work) or `D:\Personal` (Personal) with provenance recorded in SQLite and undo. `README.md` covers
usage. Read it before changing behaviour.

## Commands

```
dotnet build                                   # whole solution (LocalDocket.slnx)
dotnet test                                    # xunit, LocalDocket.Tests
dotnet test --filter "FullyQualifiedName~RuleEngineTests"                      # one test class
dotnet test --filter "FullyQualifiedName~MoverTests.Collision_gets_numbered_suffix"   # one test
dotnet run --project LocalDocket.Cli -- classify <path...> [--no-llm] [--json] [--no-embed] [--taxonomy file]
dotnet run --project LocalDocket.Cli -- scan <folder> [--apply]
dotnet run --project LocalDocket.Cli -- inbox [--apply]
dotnet run --project LocalDocket.Cli -- models        # checks Ollama is reachable and lists models
dotnet run --project LocalDocket.Cli -- index [folder] [--full] [--status]   # crawl the roots (or one folder) into the chat index
dotnet run --project LocalDocket.Cli -- index --remove-sidecars               # import + delete legacy .filer.json sidecars
dotnet run --project LocalDocket.Cli -- chat <question...> [--no-llm]         # RAG answer with numbered sources
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe [--scan [folder]] [--search] [--index] [--chat]
```

The CLI is the fastest way to iterate on prompts and rules: `classify --no-llm` exercises rules and hints only,
and `--taxonomy` points at a scratch copy. The tray app is single-instance (named mutex), so stop a running
instance before launching a rebuilt one.

## Tests need the repo `taxonomy.yaml`

`TaxonomyFixture` in `LocalDocket.Tests/CoreTests.cs` walks up from the test bin directory until it finds
`taxonomy.yaml`, rewrites the roots and inbox to a temp folder, and loads that. Tests therefore depend on the
real rules and categories in the repo file: renaming a category or rule (e.g. `prod-backup`, `Work/SQL/Backups`,
`Work/Spec`) breaks tests, and new rule behaviour is expected to get a test there.

## Architecture

Six projects, dependency direction strictly inward to `LocalDocket.Core`:

- `LocalDocket.Core` (net10.0): everything that decides and moves. No LLM or extraction code; it only declares the
  `IContentExtractor` and `IClassifierBackend` contracts in `Models.cs`.
- `LocalDocket.Extract`: `ContentExtractor` implements `IContentExtractor` (text head, PDF/DOCX/XLSX/PPTX, zip listing,
  `.bak` header via `RESTORE HEADERONLY` with name-parsing fallback, EXIF, downscaled JPEG for vision models).
- `LocalDocket.Llm`: `OllamaClient` (raw HTTP to `/api/chat`, `/api/embed`, `/api/show`) and `OllamaClassifier`
  implementing `IClassifierBackend` with a strict JSON schema response and optional escalation to a bigger model.
- `LocalDocket.Cli`, `LocalDocket.App`: two hosts over the same `Pipeline`. `DocketHost` in the App owns the watcher, timer
  tick, popup callbacks, and Scan mode.
- `LocalDocket.Tests`: xunit over Core only.

### One classification path

`Pipeline.ClassifyAsync` (LocalDocket.Core/Pipeline.cs) is the single path used by the CLI, the inbox watcher, and
Scan mode. Order matters and is easy to break:

1. Extract content into `FileItem` (`ContentText`, `ContentKind`, `Meta`).
2. `RuleEngine.Evaluate`: first matching taxonomy rule wins with confidence 1.0 and `DecidedBy = "rule:<name>"`.
   Rules short-circuit the model entirely.
3. Otherwise `RuleEngine.Hint` from category `match` globs (0.7 for a single hit, 0.5 for several) is passed
   to the model as a hint and used as the fallback when Ollama is down.
4. Model call, with up to 5 nearest past decisions retrieved from SQLite by embedding similarity as few-shot
   examples. The returned category is snapped onto a real one by `Taxonomy.NormalizeCategory`; an unknown
   category drops confidence to 0.4 and falls back to the hint.
5. Post-processing that applies to every path: `.bak` environment inference, known-client recovery from the
   reasoning text, and `ApplyCategoryAsks` (adds a category's `ask` questions when the field is still empty,
   removes answered ones).
6. `PathTemplate.Resolve` turns category + fields into a target directory. Missing tokens never fail: `{env}`
   becomes `Unsorted`, `{client}` becomes `Unassigned`, `{ticket}`/`{project}` fall back to a ticket found in
   the name, then the file stem. Dates come from the file's modified time.

`Classification.NeedsUser(threshold)` is the only place that decides auto-file vs popup: any open question,
confidence below `autoFileConfidence`, or empty category means ask. `Action == "skip"` items (shortcuts) are
left in the inbox.

### Moves, provenance, undo

`Mover.Move` is the only thing that moves files, for all hosts and for the bulk `apply` command. It preserves
timestamps, resolves collisions to `name (2)`, then records everything in one transaction
(`Store.RecordFiledAndMove`: child-row rename for folders, the `files` row with full provenance and embedding, the
`moves` row). If that transaction fails the item is moved back to where it came from and the call throws, so a file
is never left filed without a record. It refuses to move a folder into itself, and folder moves only fall back to
copy + delete across volumes (any other I/O error surfaces as-is). `PathTemplate.Resolve` treats a dropped *folder*
named `Foo` under a `{project}`/`{ticket}` template as the project itself (`Projects\Foo`, never `Projects\Foo\Foo`). `Mover.Undo` restores to the inbox under the original name and repoints the row at the actual
restore path. Do not add a second move path; route new bulk operations through `Mover` so undo and the index
stay correct.

`Store.RowDeleted` fires for any row the store removes on its own (a stale row at a path being reused) so the host
can drop its vectors; `VectorIndex` is also reloaded at the start of a prune pass when the store's chunk count differs
(the CLI wrote chunks). Rows are keyed by a GUID but there is exactly one row per `final_path` (unique, case-insensitive): `Move`
reuses the id the store already has for the source path (`Store.FindIdByPath`), so undo → re-file, or filing an
item the index already knows, updates the row instead of duplicating it. Folder moves call `Store.RenamePrefix`
so rows for files inside the folder follow it. `Store` holds one connection behind a lock, in WAL mode with a
busy timeout, and migrates older databases additively in its constructor (`Migrate`).

Before 2026-09-10 every move also wrote a hidden `<name>.filer.json` sidecar. `SidecarImport` (Core) folds any that
remain into the `files` row (merging metadata into existing rows, creating rows for unknown items) the first time the
indexer runs (`kv sidecars.imported`), and `docket index --remove-sidecars` deletes them after importing. Nothing else in
the code knows about sidecars beyond the enumerators skipping the name.

### Exclusions vs frozen

Two different lists keep Local Docket out of folders. `settings.excludeFolders` (plus the built-in `.git`, `bin`, `obj`,
`node_modules`, ... in `Exclusions.BuiltIn`) means "never scan, never index": pattern shape decides the rule
(absolute path = that subtree; root-relative like `Infra\Deploy` = under every root; bare name = any path segment).
A category's `frozen:` list only means "Scan mode must not propose moving this" and does **not** stop indexing.
`Taxonomy.ScannableEntries(folder)` applies both and is the single enumerator for the App's Scan window and the
CLI `scan`; the inbox enumerator (`Pipeline.SettledInboxItems`) deliberately ignores both. Source code is never
indexed: `D:\Personal\Repo` is excluded outright and `indexSkipRepos` makes the indexer skip any folder that holds
an `atomicMarkers` hit. `Taxonomy.SetExcludeFolders` rewrites the inline `excludeFolders: [...]` line in the YAML
text (same approach as `AddQuestionOption`).

### Indexing (the vector store behind chat)

Privacy guards live in `Taxonomy.IsIndexable` (exclusions, `indexSkipExtensions`, `indexSkipNames`, `indexSkipCategories`
via `CategoryForPath`) and `SecretScan.IsSensitive` (chunks that look like keys, connection strings or tokens are
withheld). Sniffed text (`ContentKind == "text?"`) is classified but never indexed. The store opens with
`secure_delete=ON` and `Vacuum()` runs after a prune that removed chunks. Transient extract failures (locked files) are
recorded with a NULL `index_mtime` so the next crawl retries them; an unmounted root is never pruned.

`docket.db` doubles as the vector store. `Indexer` (Core) crawls the taxonomy roots, skipping exclusions, hidden and
system entries, reparse points, repositories (`indexSkipRepos` + `atomicMarkers`), `indexSkipExtensions` and files over
`indexMaxFileMB`. Change detection is size + last-write time against `Store.IndexSnapshot()`; `--full` re-embeds
everything. Per file: `IContentExtractor.ExtractAsync(..., capBytes: indexCapBytesPerFile, attachImage: false)` →
`Chunker.Split` (`indexChunkChars` / `indexChunkOverlapChars`, boundary-aware) → `IEmbedder.EmbedBatchAsync`
(one `/api/embed` call per `indexEmbedBatch` inputs; `nomic` models get the `search_document:` prefix) →
`Store.UpsertIndexed` (row + `chunks` table in one transaction) → `VectorIndex.ReplaceFile`. Files without text still get
a row (name search) but no chunks. Crawled rows have `source='crawl'` and a best-effort category from
`Taxonomy.CategoryForPath`; a later `Mover.Move` upgrades the same row to `source='filed'` and keeps its chunks.

Pruning (full-root crawls only): crawled rows whose file is gone or now excluded are deleted; filed rows keep their
provenance and only lose their chunks (and their index timestamps, so they are re-read if the file returns). Both are
conditional on the row still pointing at the path the crawl snapshot saw, so an item filed while a crawl runs keeps
its new record. The run loop treats only its own cancellation token as shutdown: an HTTP timeout (also an
`OperationCanceledException`) counts as an embedder outage; `BeforeCrawl` lets the host re-probe Ollama before each
pass; wake-ups use one `TaskCompletionSource`, so "Reindex now" is never swallowed and a paused indexer does not spin
when items are enqueued. If the loop ever exits without cancellation it logs it and publishes a status error. A filed file moved by hand is re-linked by name + size + SHA-256 instead of
becoming a new row. An embed-model change (`kv index.embed_model`) drops all chunks. If the embedder is unreachable the
crawl stops, nothing is pruned, and the loop retries sooner.

`VectorIndex` holds unit-normalised chunk vectors in memory (brute-force cosine, `TopK` with an optional file-id filter)
and is loaded from `Store.LoadChunkVectors` at startup; it never holds metadata. `Store.Nearest` (few-shot examples for
classification) only considers rows with a decision, so crawled rows never leak into prompts.

### Chat (retrieval-augmented answers)

`ChatService` (Core) is the whole chat brain; `ChatWindow` and the CLI `chat` command only render it. Per question:
(1) if `chatExtractFilters`, `IChatBackend.CompleteJsonAsync` turns the question plus recent history into `ChatFilters`
(standalone query, domain/category/client/environment enums from the taxonomy, date range, `datesRefer` modified|filed,
path fragment) — any failure means no filters; (2) the query is embedded (`search_query:` prefix for nomic);
(3) `Store.FilterFileIds` narrows to matching rows when filters exist, `VectorIndex.TopK` (`chatTopK`, min similarity 0.35)
finds chunks, falling back to no filters and then to `Store.Search` name matches, each fallback setting a `Notice`;
(4) `Assemble` keeps the best 2 chunks for up to 8 files within `chatContextChars`; (5) `BuildMessages` = system prompt
(answer only from excerpts, cite `[n]`), last 6 turns as user/assistant, then excerpts + question; (6) the reply carries
the numbered `Sources` immediately and the answer as a lazy `IAsyncEnumerable<string>` streamed by
`OllamaClient.ChatStreamAsync` (NDJSON, `ResponseHeadersRead`, `chatNumCtx`). With Ollama down the reply is a file
list plus a notice, never an exception. `OllamaChat : IChatBackend` uses `chatModel` (null → `model`).

### Taxonomy is data and is edited in place

`taxonomy.yaml` drives roots, inbox, settings, questions, categories (path templates, `match` globs, `ask`),
and `rules`. The app copies the repo file to `%LOCALAPPDATA%\LocalDocket\taxonomy.yaml` on first run and reads that
copy afterwards (`LOCALDOCKET_TAXONOMY` overrides, `LOCALDOCKET_DATA` moves the data folder). The App and CLI projects link
the repo file into their output directory, so edits to the repo copy only reach a machine whose local copy is
missing or manually replaced.

`Taxonomy.AddQuestionOption`, `Taxonomy.AppendRule` and `Taxonomy.SetExcludeFolders` edit the YAML *text* with
regex/string appends so comments survive; they do not round-trip through the serializer. Every value they write is
single-quoted via `Taxonomy.YamlQuote`, and `SaveYaml` only replaces the file after the edited text loads; the
last known-good version is kept as `taxonomy.yaml.bak` (refreshed on every successful load and edit).
`Taxonomy.LoadWithFallback` restores it at startup when a hand edit broke the file (the broken copy is kept as
`.broken`, the tray shows a balloon). `RuleEngine.LearnedNamePattern` refuses to learn a rule from a name with fewer
than four letters/digits. Keep the YAML layout they expect (inline
`options: [...]` lists for questions, a trailing `rules:` list of `- name:` entries).

`DocketSettings` in `Taxonomy.cs` is the schema for the `settings:` block (camelCase in YAML); add a property
there when adding a setting.

### App threading model

`DocketHost.TickAsync` runs on a timer (period = max(2, `settleSeconds`); the inbox is polled, there is no watcher) and
is re-entrancy-guarded; it classifies all settled groups first, auto-files the certain ones, then *queues* the rest.
Popups are shown by `PumpAsksAsync` outside the tick guard, one group at a time, so an open popup blocks neither the
inbox nor the indexer. Items awaiting a popup stay in `_busy`; `_skipped` holds items the user left alone. Each tick
also reloads `taxonomy.yaml` if its timestamp changed and checks the index loop's health (`Notify` balloons: stale
crawl, index errors, restored taxonomy, unknown settings keys). An item the user undid earlier (`Store.WasUndoneAt`)
is demoted to a popup instead of being re-filed. `PopupWindow` and `ToastWindow` are driven through the `AskUser` / `Filed` callbacks; `ScanWindow`,
`SearchWindow` and `IndexWindow` are opened from the tray and call the host directly (`ScanAsync`, `Store`, `Indexer`).

The host also owns the background index: `Start` loads chunk vectors into `Index` on a worker task, waits 20 s, then runs
`Indexer.RunAsync` until `Dispose` cancels it. `Indexer.Busy` is `_processing == 1 || Paused`, so crawling yields to inbox
classification and to "Pause watching". `FileItem` enqueues each filed path for prompt indexing; `ReindexNow` and
`SetExcludeFolders` (writes the YAML, reloads, prunes on the next crawl) back the Indexing window. The tray status line
shows index progress only while the inbox is idle.

## `sort/`

One-off base sort of both roots done on 2026-09-09: a hand-authored Python generator, its `manifest.csv`, and
the `applied-*.csv` log from `docket apply`. It is a record, not code to extend; new bulk moves should produce
a fresh manifest and go through `docket apply` / `docket revert`.

## Conventions worth knowing

- Nullable and implicit usings are on everywhere; code is terse, one class per concern, few interfaces
  (`IContentExtractor`, `IEmbedder`, `IClassifierBackend : IEmbedder`, `IChatBackend` in `Models.cs`).
- Logging is via `Action<string>` callbacks (`PipelineOptions.Log`, `OllamaClassifier.Log`), so Core stays
  free of a logging framework. The App's `Log` writes `%LOCALAPPDATA%\LocalDocket\docket.log`. Chat questions are not logged.
- "Start with Windows" copies a dev build to `%LOCALAPPDATA%\LocalDocket\app` and registers that copy (`TrayIcon.Install`);
  a newer build refreshes it at start-up, and a registered path that vanished is repaired with a balloon.
- Large transient data (base64 image, embedding) travels in `FileItem.Meta` under `_`-prefixed keys and is
  stripped before anything is persisted.
- Ollama absence is a normal state: every model/embedding call is wrapped and the pipeline falls back to
  rules + hints + popup. Don't make the model a hard dependency.
