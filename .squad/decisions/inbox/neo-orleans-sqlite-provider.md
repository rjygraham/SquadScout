# Proposed: Orleans SQLite Provider for Phase 2

**Status:** PROPOSED (not yet accepted)
**Author:** Neo (Lead)
**Date:** 2026-07-14
**Requested by:** Ryan Graham

## Recommendation

Use the **official Orleans ADO.NET persistence provider** (`Microsoft.Orleans.Persistence.AdoNet` 10.0.x) with `Microsoft.Data.Sqlite` as the ADO.NET driver. Pair it with `UseLocalhostClustering()` so no clustering or membership database is needed.

### NuGet Packages

- `Microsoft.Orleans.Persistence.AdoNet` (10.0.x)
- `Microsoft.Data.Sqlite` (latest)

### Configuration Shape

```csharp
siloBuilder
    .UseLocalhostClustering()
    .AddAdoNetGrainStorage("Default", options =>
    {
        options.Invariant = "Microsoft.Data.Sqlite";
        options.ConnectionString = "Data Source=squadscout.db;Mode=ReadWriteCreate;";
    });
```

### DDL Scripts

The official Orleans repo ships `Sqlite-Persistence.sql` (and `Sqlite-Main.sql` for shared schema) inside the persistence package. These must be run against the database file on first launch. The Orleans test suite itself uses these scripts in `SqlitePersistenceGrainStorageFixture.cs`, confirming they are maintained.

## Why This Choice

1. **First-party support for persistence.** Orleans 10 officially includes SQLite in its ADO.NET persistence package — DDL scripts, driver mapping (`AdoNetInvariants.InvariantNameSqlLite`), and test coverage all ship in `dotnet/orleans` main.

2. **Perfect fit for our constraints.** Phase 2 is a single in-process silo on one developer's machine. SQLite's single-writer model is a non-issue with one silo. `UseLocalhostClustering()` provides in-memory membership, so we never need `Sqlite-Clustering.sql` (which doesn't exist anyway).

3. **No reminders DB needed.** Our heartbeat mechanism uses grain timers, not Orleans reminders. There is no official `Sqlite-Reminders.sql` script, but this doesn't affect us.

4. **Zero infrastructure.** A single `.db` file next to the broker executable. No SQL Server, no external process, no connection management beyond file access.

## What's NOT Supported in SQLite

- **Clustering** (`UseAdoNetClustering`): No official `Sqlite-Clustering.sql` script exists. Not needed — `UseLocalhostClustering()` handles this.
- **Reminders** (`UseAdoNetReminderService`): No official `Sqlite-Reminders.sql` script exists. Not needed — we use grain timers.
- **Multi-silo access**: SQLite is file-locked, single-writer. Not a concern with one silo.

## Alternatives Considered

| Option | Verdict |
|--------|---------|
| **Custom `IGrainStorage` over raw SQLite** | Higher risk, more code to maintain, no benefit over the official ADO.NET path that already works. |
| **LiteDB / other embedded DB** | No official Orleans provider. Would require a custom `IGrainStorage` implementation. More moving parts. |
| **SQL Server LocalDB** | Heavier install footprint, requires SQL Server components on the dev machine. Overkill for single-user local broker. |
| **In-memory only (no persistence)** | Defeats the purpose of Phase 2 — we need durable state for replay after broker restart. |

## Impact on Architecture

- **Schema bootstrap:** The broker startup code should check for the SQLite file and run `Sqlite-Main.sql` + `Sqlite-Persistence.sql` if the tables don't exist. One-time init.
- **File location:** Default to `{appDataFolder}/SquadScout/squadscout.db`. Configurable via appsettings.
- **Migration path:** If we ever need a server DB (Phase 4+, multi-machine), swap the invariant and connection string. The grain state serialization format is the same across all ADO.NET providers.
- **No new abstractions needed.** `IPersistentState<T>` with `[PersistentState("state", "Default")]` just works.

## Open Risk

GitHub issue [dotnet/orleans#8187](https://github.com/dotnet/orleans/issues/8187) (filed 2022, still open) reported that `Microsoft.Data.Sqlite` wasn't registered in older Orleans versions. Source inspection of `DbConnectionFactory.cs` on `main` confirms this is now fixed — the invariant `Microsoft.Data.Sqlite` maps to `Microsoft.Data.Sqlite.SqliteFactory`. Still worth a quick smoke test early in Phase 2 to confirm NuGet package version alignment.
