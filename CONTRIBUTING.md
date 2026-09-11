# Contributing

Thanks for taking an interest. Local Docket is a small, opinionated tool; changes are welcome when they keep it
simple, local and private.

## Ground rules

- `main` is protected: every change arrives through a pull request, and CI (build + tests on Windows) must pass.
  Nobody pushes to `main` directly, including maintainers.
- One topic per pull request. Describe what changed and why; link an issue if there is one.
- Keep the dependency direction: everything points inward to `LocalDocket.Core`, which contains no LLM or
  extraction code, only the contracts. Read `ARCHITECTURE.md` before touching the pipeline, the store or the indexer.
- New behaviour comes with a test in `LocalDocket.Tests`. The suite is hermetic: no network, no Ollama, fake
  extractors and embedders. It must stay that way.
- Never weaken a privacy guard (exclusions, credential detection, skipped categories) without saying so in the
  pull request title.

## Working locally

```
dotnet build
dotnet test
dotnet run --project LocalDocket.Cli -- classify <some file> --no-llm
```

The tray app is single-instance; exit it from the tray before launching a rebuilt one. Point `LOCALDOCKET_DATA`
and `LOCALDOCKET_TAXONOMY` at scratch locations to experiment without touching your real index.

## Pull request flow

1. Fork (or branch, if you have write access) from `main`.
2. Make the change with its test; run `dotnet test`.
3. Open the pull request against `main`. CI runs automatically.
4. A maintainer reviews and merges with a squash, so the branch history does not need to be tidy.

## Reporting a problem

Open an issue with the log lines from `%LOCALAPPDATA%\LocalDocket\docket.log` around the time it happened.
Please strip anything from your own documents before pasting. For security-sensitive reports see `SECURITY.md`.
