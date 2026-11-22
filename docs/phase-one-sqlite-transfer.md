# Transfer the SQLite backup to PostgreSQL

Run this command during the single-household cutover, before the PostgreSQL service accepts application writes. Stop SQLite writers and the expiry reminder worker, take a consistent SQLite backup, and keep the backup and its checksum for retries. If SQLite uses WAL, produce the backup with SQLite's backup API or `.backup` command.

Set `ConnectionStrings__FreshDays` in the command's environment to the PostgreSQL destination. Use PostgreSQL 16 or later with the connection configured for the intended schema. The command opens the SQLite backup read-only and imports into the legacy household `00000000-0000-0000-0000-000000000001`.

From an existing backend image, run the migration entry point with the backup mounted read-only at `/backup/freshdays.db`:

```sh
dotnet /app/FreshDays.Migration.dll /backup/freshdays.db --dry-run
dotnet /app/FreshDays.Migration.dll /backup/freshdays.db
```

From a development checkout, the equivalent is:

```sh
dotnet run --project backend/src/FreshDays.Migration -- /path/to/freshdays.db --dry-run
dotnet run --project backend/src/FreshDays.Migration -- /path/to/freshdays.db
```

## Read the report

The command prints one JSON report to standard output after checking the proposed destination. The `groups` object includes `inventory`, `shopping`, `subscribers`, and `notifications`. Each group reports `source` and `destination` counts, and a `contentMatches` boolean. Content checks compare the ordered list entries and their complete JSON values, exact subscriber IDs, and notification dates, subscriber IDs, retry keys, and sent flags. The report includes the destination household ID.

A dry run performs the same transfer and check inside a PostgreSQL transaction and rolls back the proposed rows and any schema creation. The destination counts in a dry-run report describe the rows that would be committed. A real run commits only after every group matches. A successful run exits with code 0.

Invalid SQLite data produces diagnostics on standard error that identify the legacy table and `rowid`, plus the list name and 1-based item index where applicable. Investigate those records in a copy of the backup before restarting the transfer. Code 1 indicates a failed transfer or verification. A destination mismatch includes a JSON report showing the mismatched groups and the proposed counts before rollback. Code 2 indicates invalid usage or configuration. Reopen service writes only after retaining a successful report from a committed run.

## Recover from an interruption

Keep application writes frozen and rerun against the same backup and PostgreSQL destination. The transaction rolls back all rows if the connection ends before commit. If the connection ends after commit but before the report is captured, the rerun confirms the existing rows and exits successfully. The tool preserves rows that already match, rolls back if the destination contains conflicting content or extra rows in the legacy household, and scopes the transfer and checks to that household.

## Reproduce the rehearsal

Set `FRESHDAYS_TEST_POSTGRES` to a disposable PostgreSQL database and run:

```sh
dotnet test backend/tests/FreshDays.Tests --filter FullyQualifiedName~SqliteMigrationTests
```

The fixture tests use a real SQLite backup and an isolated PostgreSQL schema. They show that dry run predicts counts without creating schema, a repeat run preserves data and ownership, and invalid source rows or conflicting destination values stop the transfer. The interruption test pauses PostgreSQL during notification insertion, terminates the connection, verifies that earlier list and subscriber writes rolled back, and imports all groups on the retry.
