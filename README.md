# Local Docket

[![CI](https://github.com/HawkTaka/LocalDocket/actions/workflows/ci.yml/badge.svg)](https://github.com/HawkTaka/LocalDocket/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A Windows tray app that files whatever lands in `Desktop\_Inbox`, keeps a searchable index of your document
folders, and lets you ask questions about your own files in plain language. Everything runs on your machine:
a local [Ollama](https://ollama.com) model classifies and answers, SQLite holds the index, nothing is sent anywhere.

```
                 ┌──────────────┐   rules → model → (ask you?)   ┌──────────────────┐
  Desktop\_Inbox │   watcher    │ ─────────────────────────────▶ │  D:\Work         │  Work root
                 │  (5 s poll)  │      move + record + undo      │  D:\Personal     │  Personal root
                 └──────────────┘                                └────────┬─────────┘
                                                                          │ crawl (hourly + after each move)
                 ┌──────────────┐        top-k chunks + filters  ┌────────▼─────────┐
   "where is the │     Chat     │ ◀──────────────────────────── │    docket.db      │  rows · moves · chunks
    Acme MSA?"   │  (cited)     │ ─────── streamed answer ─────▶ │  + vectors in RAM │
                 └──────────────┘                                └──────────────────┘
```

The roots, categories, questions and rules are all yours to define in one YAML file. The examples below use two
roots, `D:\Work` and `D:\Personal`, a client called Acme, and tickets shaped like `PROJ-450`; swap in your own.

## What it does

**Files your inbox.** Drop anything on `Desktop\_Inbox`. Once it has stopped changing, Local Docket extracts what it can
(text, PDF, Office, zip listings, SQL Server `.bak` headers, EXIF, a downscaled image for vision models), applies
your deterministic rules first, asks the local model second, and moves the item into the right folder under the
Work or Personal root. Confident decisions are filed automatically with a toast and an **Undo** button. Anything
uncertain gets a small popup: pick the category, answer a chip question (Production or UAT? which client?), edit
the target, tick **Remember as rule** so the same shape of file never asks again.

**Remembers everything.** Every move writes one row to `docket.db`: where the item came from, when, its hash,
what the extractor saw, what the classifier decided and why, and the move itself. Undo restores the item to the
inbox under its original name and is remembered across restarts, so an item you put back is asked about, never
silently re-filed.

**Indexes your roots.** A background crawler walks every root, reads the full text of every document it may
read, splits it into overlapping chunks, embeds them with `nomic-embed-text`, and stores text and vectors in the
same database. Only changed files are re-read. Repositories, build output, excluded folders, credential-looking
files and private categories never reach the index.

**Answers questions.** Tray → **Chat with your files…** Ask "what did the PROJ-450 plan say about the importer"
or "what did I file for Acme last month". Local Docket pulls filters out of the question (client, category,
environment, dates, folder), retrieves the most relevant passages, and the model answers from those passages
only, citing them as `[1]`, `[2]`. Click a citation to open the file in Explorer. Follow-up questions keep
context. If Ollama is down you still get the matching files, just no prose.

## Running it

```
dotnet build
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe            # tray app
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe --scan     # + Scan window on the Desktop
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe --search   # + search window
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe --index    # + indexing window
LocalDocket.App\bin\Debug\net10.0-windows\LocalDocket.exe --chat     # + chat window
```

Requirements: .NET 10 SDK, Ollama on `http://localhost:11434` with a chat model and an embedding model pulled
(`qwen3.5:9b` and `nomic-embed-text` by default, plus a larger `escalationModel` if you keep escalation on).
Without Ollama the app still runs on name rules and asks you for the rest.

Tray menu: Open inbox · Process inbox now · Scan a folder · Search filed items · Chat with your files · Indexing ·
Recent moves (Undo) · Edit / Reload taxonomy · Open log · Start with Windows · Pause watching · Exit.

**Start with Windows** copies the build to `%LOCALAPPDATA%\LocalDocket\app` and registers that copy, so a cleaned `bin`
folder never breaks login; **Update installed copy** refreshes it from the build you are running.

## How a drop is handled

1. The inbox is polled every `settleSeconds`. An item counts once its size and modified time have been stable
   for that long and it is not locked. Files dropped together within `groupWindowSeconds`, a zip next to its
   extracted folder, and numbered sequences share one popup; every file is still filed on its own merits. A folder
   containing `.git`, `*.sln`, `*.csproj`, `package.json` or `docker-compose.yml` is atomic and never split.
2. **Extract:** text head, PDF/DOCX/XLSX/PPTX text, zip listing, `.bak` header fields (server, database, logical
   file names), image EXIF, and a downscaled copy of images for vision-capable models.
3. **Rules** from `taxonomy.yaml` fire first at confidence 1.0. Then the model answers a strict JSON schema:
   category, environment, client, confidence, one-line reasoning, open questions. Up to five similar past
   decisions ride along as examples, so repeated patterns stop asking.
4. Confidence at or above `autoFileConfidence` with no open question → moved, toast with **Undo**. Otherwise the
   popup.
5. The move preserves timestamps, resolves name collisions to `name (2)`, records provenance and the move in one
   transaction, and if that record cannot be written the item is put back where it came from.

## How the index and chat work

**Crawl.** Shortly after start, every `indexRecrawlMinutes`, and after every filing, the indexer compares each
file's size and modified time with the database. Changed files are read up to `indexCapBytesPerFile`, split
into `indexChunkChars` pieces with `indexChunkOverlapChars` overlap at paragraph or sentence boundaries, embedded
in batches, and written as chunks next to the file's row. Files that vanished are pruned; files that were filed
by hand elsewhere are re-linked by name, size and hash. Crawling waits while the inbox is being classified or
watching is paused. The Indexing window shows progress and lets you exclude folders, re-crawl, re-embed
everything, or pause.

**Retrieval.** A question is first turned into a structured request by the model: a standalone search phrase
plus optional filters (domain, category, client, environment, a date range on modified or filed time, a path
fragment). Filters the question never actually mentioned are discarded. The phrase is embedded, the best chunks
are found by cosine similarity over vectors held in memory (optionally restricted to rows matching the filters),
grouped to the best two passages for up to eight files within a character budget, and numbered.

**Answer.** The model receives the numbered passages wrapped as data, a system prompt that forbids answering
outside them, and the last six turns of the conversation, and streams its reply. The window shows the sources
before the first word arrives.

## Privacy

- Nothing leaves the machine as long as `ollamaUrl` is local; Local Docket warns at start-up if it is not.
- Never indexed: folders in `excludeFolders` (plus `.git`, `bin`, `obj`, `node_modules` and similar), any folder
  that looks like a repository, credential-looking names (`.env`, `id_rsa*`, `appsettings*.json`, `web.config`,
  `*password*`, …), key and certificate extensions, and files of unknown type (they are only sniffed to classify).
- Files in `indexSkipCategories` (HR, team reviews and payroll imports in the sample taxonomy) keep their
  provenance row but are never chunked.
- Any chunk that looks like a private key, connection string or API token is withheld.
- `docket.db` is not encrypted. Deleted text is scrubbed (`secure_delete`) and the file is compacted after every
  prune. Chat questions are not logged.

## Where things live

Local Docket used to be called Filer. The first start after the rename moves `%LOCALAPPDATA%\Filer` to
`%LOCALAPPDATA%\LocalDocket`, renames the database and log, and re-registers Start with Windows; the old
`FILER_DATA` and `FILER_TAXONOMY` variables are still honoured.

| What | Where |
|---|---|
| Taxonomy (tree, questions, rules, settings) | `%LOCALAPPDATA%\LocalDocket\taxonomy.yaml`, seeded from the repo copy; `LOCALDOCKET_TAXONOMY` overrides |
| Index, provenance, move log, chunks | `%LOCALAPPDATA%\LocalDocket\docket.db` (SQLite; `LOCALDOCKET_DATA` overrides the folder) |
| Log | `%LOCALAPPDATA%\LocalDocket\docket.log` |
| Installed copy for autostart | `%LOCALAPPDATA%\LocalDocket\app` |

## The taxonomy

`taxonomy.yaml` is the whole configuration and is meant to be edited by hand. The tray app picks up changes
within a few seconds; a broken edit is reported in a balloon and the last good version keeps running. Every
in-app edit validates the file before writing and keeps `taxonomy.yaml.bak`. Unknown keys under `settings:` are
reported, not ignored.

```yaml
roots:
  Work: 'D:\Work'
  Personal: 'D:\Personal'
inbox: '%USERPROFILE%\Desktop\_Inbox'

questions:
  environment: { prompt: 'Which environment?', options: [Production, UAT, DEV, Local] }
  client:      { prompt: 'Which client?',      options: [Acme, Globex, Initech], allowAdd: true }

categories:
  Work/SQL/Backups:
    path: 'SQL\Backups\{env}\{yyyy-MM-dd}'
    match: ['*.bak', '*.trn']
    ask: [environment]
  Work/Spec:
    path: 'Spec\{ticket}'
    match: ['PROJ-*_PLAN.md', '*Specification*.md']
  Work/Clients:
    path: 'Documents\Clients\{client}'
    ask: [client]
    hint: 'Contracts, MSAs, take-on packs, client-specific process docs.'
  Personal/Media/Photos:
    path: 'Media\Photos\{yyyy}'
    match: ['IMG_*.jpg', '*.heic']

rules:
  - name: prod-backup
    when: { name: '(?i)^Production_.*\.bak$' }
    set:  { category: Work/SQL/Backups, environment: Production }
  - name: shortcuts
    when: { ext: ['.lnk', '.url'] }
    set:  { action: skip }
```

- **Roots** and the inbox path.
- **Questions** the popup can ask, with their chip options. New clients typed into the popup are appended.
- **Categories:** a key like `Work/Clients` with a `path` template (`{yyyy}`, `{yyyy-MM}`, `{yyyy-MM-dd}`,
  `{env}`, `{client}`, `{ticket}`, `{project}`), optional `match` globs, `ask` questions, a `hint` for the model,
  and `frozen` folders Scan mode must leave alone.
- **Rules:** `when` (`name` regex, `ext` list, `content` / `content_not` regex) plus `set` (category, environment,
  client, ticket, `action: skip`) or `ask`. "Remember this as a rule" appends one.
- **Exclusions:** an absolute path excludes that subtree, `Infra\Deploy` is taken under each root, a bare name
  such as `node_modules` matches any folder.

### Settings reference

| Key | Default | Meaning |
|---|---|---|
| `autoFileConfidence` | 0.85 | Auto-file at or above this; popup below |
| `escalateBelow` / `escalationModel` | 0.60 / – | Re-ask a bigger model when the first is this unsure |
| `settleSeconds` / `groupWindowSeconds` | 5 / 90 | Inbox poll and settle time; drop-burst grouping window |
| `contentCapBytesPerFile` | 4096 | Text the classifier reads per file |
| `model` / `embedModel` / `chatModel` | qwen3.5:9b / nomic-embed-text / (model) | Ollama models |
| `ollamaUrl` / `keepAlive` / `thinking` | localhost:11434 / 10m / false | Ollama connection and options |
| `imageVision` / `imageMaxEdge` | true / 1024 | Send downscaled images to the classifier |
| `profile` | generic | One or two sentences about whose documents these are; read by the classifier |
| `atomicMarkers` | .git, *.sln, *.csproj, package.json, docker-compose.yml | Markers that make a folder one unit |
| `excludeFolders` | – | Never scanned or indexed (built-ins always apply) |
| `indexSkipRepos` | true | Skip any folder holding an `atomicMarkers` hit when indexing |
| `indexSkipCategories` | Work/HR, Work/Team | Never chunked for chat |
| `indexSkipNames` / `indexSkipExtensions` | credential names / binaries and keys | Never indexed |
| `indexSkipSensitiveChunks` | true | Withhold chunks that look like secrets |
| `indexCapBytesPerFile` | 200000 | Text the indexer reads per file |
| `indexChunkChars` / `indexChunkOverlapChars` / `indexMaxChunksPerFile` | 3200 / 480 / 64 | Chunking |
| `indexMaxFileMB` / `indexEmbedBatch` / `indexThrottleMs` / `indexRecrawlMinutes` | 64 / 16 / 50 / 60 | Crawl limits and cadence |
| `chatNumCtx` / `chatTopK` / `chatContextChars` / `chatExtractFilters` | 16384 / 24 / 24000 / true | Chat retrieval and prompt budget |
| `bakServers` / `bakNamePatterns` | – | `.bak` header hints → environment |

## Command line

The CLI shares the pipeline with the tray app and is the quickest way to tune prompts and rules.

```
dotnet run --project LocalDocket.Cli -- classify <path...> [--no-llm] [--json] [--no-embed] [--taxonomy file]
dotnet run --project LocalDocket.Cli -- scan <folder> [--no-llm] [--json] [--apply]      # dry run unless --apply
dotnet run --project LocalDocket.Cli -- inbox [--apply]                                  # process settled inbox items once
dotnet run --project LocalDocket.Cli -- undo <moveId>
dotnet run --project LocalDocket.Cli -- search <text>
dotnet run --project LocalDocket.Cli -- index [folder] [--full] [--status]               # crawl into the chat index
dotnet run --project LocalDocket.Cli -- index --remove-sidecars                          # one-off import of legacy .filer.json files
dotnet run --project LocalDocket.Cli -- chat <question...> [--no-llm]                    # answer with numbered sources
dotnet run --project LocalDocket.Cli -- models                                           # is Ollama up, which models
dotnet run --project LocalDocket.Cli -- apply <manifest.csv> [--out applied.csv]         # bulk moves from a CSV
dotnet run --project LocalDocket.Cli -- revert <applied.csv>                             # undo a whole apply run
```

`--no-llm` exercises rules and hints only; `--taxonomy` points at a scratch copy. The tray app is single-instance,
so exit it before launching a rebuilt one. Bulk moves of an existing tree go through `apply` with a CSV manifest
(`path,category,environment,client,ticket,project,target,name,note`) and can be undone as a whole with `revert`.

## Solution layout

```
LocalDocket.slnx
  LocalDocket.Core      pipeline, grouping, rules, taxonomy, move/undo, SQLite store, chunker, vector index,
                  indexer, chat service, exclusions, secret scan      (net10.0, no LLM or extraction code)
  LocalDocket.Extract   text / PDF / Office / zip / .bak header / EXIF / image downscale
  LocalDocket.Llm       Ollama client: JSON-schema chat, streaming chat, embeddings
  LocalDocket.App       WPF tray app: host, popup, toasts, scan, search, chat and indexing windows
  LocalDocket.Cli       the same pipeline from a terminal
  LocalDocket.Tests     xunit over Core (75 tests, hermetic, no network)
```

Dependencies point inward to `LocalDocket.Core`, which only declares the extractor, embedder, classifier and chat
contracts. `ARCHITECTURE.md` explains the invariants worth knowing before changing behaviour. See
`CONTRIBUTING.md` for the pull-request flow.

## Tests

```
dotnet test
```
