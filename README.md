# LLS.EFBulkExtensions

[English] | [Português](README.pt-BR.md)

High-performance extensions for EF Core bulk operations: insert, update and delete with large volumes, streaming (no full in-memory copy), type conversions, owned types, TPH, and optional returning of generated IDs. Supports SQL Server, PostgreSQL, SQLite and MySQL/MariaDB.

## Features
- Bulk insert, update, delete and insert-or-update (upsert) via `DbContext` extension methods
- Streaming inserts (entities are fed directly to the provider, without materializing an intermediate `DataTable`)
- Support for SQL Server (`SqlBulkCopy`/`MERGE`), PostgreSQL (binary `COPY`), SQLite (prepared command per row within a transaction) and MySQL/MariaDB (`MySqlBulkCopy`)
- Optional returning of generated IDs on inserts
- Handling of value conversions (e.g., `EnumToString`), owned types and TPH discriminator
- Participates in the ambient EF transaction when one is open; otherwise an optional internal transaction

## Compatibility and Dependencies
- .NET target frameworks: `net8.0`, `net9.0`, `net10.0`
- EF Core (Relational) versions matched per target framework:

| .NET TFM | EF Core Relational |
|----------|--------------------|
| net8.0   | 8.0.x              |
| net9.0   | 9.0.x              |
| net10.0  | 10.0.x             |

- Providers:
  - SQL Server: `Microsoft.Data.SqlClient`
  - PostgreSQL: `Npgsql`
  - SQLite: the EF Core SQLite provider (`Microsoft.EntityFrameworkCore.Sqlite`)
  - MySQL/MariaDB: `MySqlConnector` (EF provider: `Pomelo.EntityFrameworkCore.MySql`)

See [LLS.EFBulkExtensions.csproj](src/LLS.EFBulkExtensions/LLS.EFBulkExtensions.csproj).

## Installation
- Local project: add a `ProjectReference` to `src/LLS.EFBulkExtensions`.
- NuGet (if published): reference the `LLS.EFBulkExtensions` package and ensure the matching EF Core provider:
  - SQL Server: `Microsoft.EntityFrameworkCore.SqlServer`
  - PostgreSQL: `Npgsql.EntityFrameworkCore.PostgreSQL`
  - SQLite: `Microsoft.EntityFrameworkCore.Sqlite`
  - MySQL/MariaDB: `Pomelo.EntityFrameworkCore.MySql` (set `AllowLoadLocalInfile=true` in the connection string; the server needs `local_infile` enabled)

## Quick Start
Import the extensions and call the methods from your `DbContext`:

```csharp
using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
```

The provider is resolved automatically from the `DbContext` (SQL Server, PostgreSQL or SQLite).

Insert:
```csharp
await context.BulkInsertAsync(entities, new BulkInsertOptions {
    ReturnGeneratedIds = false,
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    PreserveIdentity = false,
    UseInternalTransaction = true,
    KeepNulls = false
});
```

Update:
```csharp
await context.BulkUpdateAsync(entitiesToUpdate, new BulkUpdateOptions {
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    UseInternalTransaction = false
});
```

Delete:
```csharp
await context.BulkDeleteAsync(entitiesToDelete, new BulkDeleteOptions {
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    UseInternalTransaction = false
});
```

Insert or update (upsert), correlated by primary key:
```csharp
await context.BulkInsertOrUpdateAsync(entities, new BulkInsertOrUpdateOptions {
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    UseInternalTransaction = false
});
```
> Upsert matches by **primary key**: rows whose PK already exists are updated, the rest are inserted. The PK values must be set on the entities. See limitations below.

## Options
- [BulkInsertOptions](src/LLS.EFBulkExtensions/Options/BulkInsertOptions.cs): `ReturnGeneratedIds`, `BatchSize`, `TimeoutSeconds`, `PreserveIdentity`, `UseInternalTransaction`, `KeepNulls`
- [BulkUpdateOptions](src/LLS.EFBulkExtensions/Options/BulkUpdateOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`
- [BulkDeleteOptions](src/LLS.EFBulkExtensions/Options/BulkDeleteOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`
- [BulkInsertOrUpdateOptions](src/LLS.EFBulkExtensions/Options/BulkInsertOrUpdateOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`

> Note: `BatchSize` is honored by the SQL Server path (`SqlBulkCopy`). The PostgreSQL `COPY` and SQLite per-row paths do not chunk by `BatchSize`.

You can also enable returning generated IDs at the model level:
```csharp
builder.Property(p => p.Id).ValueGeneratedOnAdd(ReturnGeneratedIds: true);
```
Execution options such as `BatchSize`/`TimeoutSeconds` belong to `BulkInsertOptions`, not to model annotations.
See [SequenceModelExtensions](src/LLS.EFBulkExtensions/Extensions/SequenceModelExtensions.cs).

## Database Support
- SQL Server (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkDeleter.cs)
- PostgreSQL (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkDeleter.cs)
- SQLite (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkDeleter.cs)
- MySQL/MariaDB (insert/update/delete/upsert): [Inserter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkDeleter.cs), [Upserter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkUpserter.cs)

## How It Works
- Insert:
  - SQL Server: fast path streams the entities into `SqlBulkCopy` via a `DbDataReader`. When returning IDs, it stages into a temp table and uses `MERGE ... OUTPUT` correlated by a generated column.
  - PostgreSQL: fast path streams the entities into a binary `COPY`. When returning IDs, it `COPY`s into a temp table and runs `INSERT ... SELECT ... ORDER BY <ordinal> RETURNING`, correlating IDs by position.
  - SQLite: a prepared `INSERT` (optionally `RETURNING` for IDs) executed once per row inside a single transaction; rows are streamed, no `DataTable` is materialized.
  - MySQL/MariaDB: `MySqlBulkCopy` (`LOAD DATA LOCAL INFILE`) fed by the streaming reader. `ReturnGeneratedIds` is not yet supported. Update/delete stage into a `TEMPORARY TABLE` and apply `UPDATE`/`DELETE ... JOIN` on the PK; upsert uses `INSERT ... ON DUPLICATE KEY UPDATE`.
- Update: stages rows into a temp table and applies `UPDATE` with a JOIN on the PK, ignoring value-generated columns (SQLite applies a prepared `UPDATE` per row).
- Delete: stages keys and applies `DELETE` with a JOIN on the PK (SQLite applies a prepared `DELETE` per row).
- Insert or update (upsert), matched by PK: SQL Server `MERGE` (with `SET IDENTITY_INSERT` when the PK is an identity column); PostgreSQL `INSERT ... ON CONFLICT (pk) DO UPDATE`; SQLite `INSERT ... ON CONFLICT(pk) DO UPDATE` per row.

Column mapping (properties, CLR/provider types, value conversions including `EnumToString`, owned types and TPH discriminator) is resolved by [DataTableBuilder.BuildColumns](src/LLS.EFBulkExtensions/Core/Internal/DataTableBuilder.cs) and consumed by the streaming [EntityDataReader](src/LLS.EFBulkExtensions/Core/Internal/EntityDataReader.cs) (and by `DataTable` staging where still required).

## Examples / Tests
Deterministic test suites double as usage examples:
- SQL Server: [SqlServerDeterministicTests.cs](tests/LLS.EFBulkExtensions.Tests.SqlServer/SqlServerDeterministicTests.cs)
- PostgreSQL: [PostgresDeterministicTests.cs](tests/LLS.EFBulkExtensions.Tests.Postgres/PostgresDeterministicTests.cs)
- SQLite: [BulkSqliteFunctionalTests.cs](tests/LLS.EFBulkExtensions.Tests.Sqlite/BulkSqliteFunctionalTests.cs)

Run the fast (deterministic) suite, excluding the large performance harnesses:
```
dotnet test --filter "Category!=Performance"
```

## Requirements
- EF Core with correct entity mapping (table, schema, PK)
- To return IDs on inserts:
  - Use a PK with `ValueGeneratedOnAdd` and optionally annotate `ReturnGeneratedIds` on the model.
  - Supported ID CLR types: numeric `long`, `int`, `short`, `byte`, `ulong`, `uint`, `ushort` (and nullable variants); `Guid` (SQL Server and PostgreSQL).

## APIs
- [BulkInsertAsync](src/LLS.EFBulkExtensions/Extensions/BulkInsertExtensions.cs)
- [BulkUpdateAsync](src/LLS.EFBulkExtensions/Extensions/BulkUpdateExtensions.cs)
- [BulkDeleteAsync](src/LLS.EFBulkExtensions/Extensions/BulkDeleteExtensions.cs)
- [BulkInsertOrUpdateAsync](src/LLS.EFBulkExtensions/Extensions/BulkInsertOrUpdateExtensions.cs)

## Upsert — limitations (v1)
- Match is by **primary key only**; the PK values must be present on the entities.
- Does **not** return generated IDs.
- Identity/serial PKs: SQL Server uses `SET IDENTITY_INSERT`; PostgreSQL `GENERATED ALWAYS` columns use `OVERRIDING SYSTEM VALUE`.
- Computed/`OnAddOrUpdate` columns are not handled in the insert branch.

## Roadmap
See [ROADMAP.md](ROADMAP.md) for planned improvements and known limitations.
