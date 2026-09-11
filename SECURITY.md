# Security

Local Docket reads every document under the roots you configure and keeps their text and embeddings in a local
SQLite database. Treat `%LOCALAPPDATA%\LocalDocket\docket.db` as sensitive: it is not encrypted.

What the app does to limit exposure is described in the README's Privacy section (local-only model calls,
exclusions, credential-name and credential-content filters, skipped categories, secure delete).

If you find a way for document content to leave the machine, to bypass an exclusion, or for indexed text to
survive a prune, please report it privately through GitHub's "Report a vulnerability" button on the Security tab
rather than in a public issue. Include the version (`Help` → tray tooltip, or the release tag) and steps to
reproduce. You will get a response within a week.
