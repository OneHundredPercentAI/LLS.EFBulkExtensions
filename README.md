# LLS.EFBulkExtensions

[English] | [Português](README.pt-BR.md)

High-performance extensions for EF Core bulk operations: insert, update and delete with large volumes, batch processing, SQL Server and PostgreSQL support, type conversions, owned types, and optional returning of generated IDs.

## Features
- Bulk insert, update and delete via DbContext extension methods
- Batch processing (BatchSize) and timeout control
- Support for SQL Server (SqlBulkCopy/MERGE) and PostgreSQL (binary COPY)
- Optional returning of generated IDs on inserts
- Handling properties with conversions (e.g., EnumToString) and owned types
- Optional internal transaction and extra configuration via model annotations

## Compatibility and Dependencies
- .NET target frameworks:
  - net5.0, net6.0, net7.0, net8.0, net9.0, net10.0
- EF Core (Relational) versions:
  - 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 (matched per target framework)

| .NET TFM | EF Core Relational |
|---------|---------------------|
| net5.0  | 5.0.x               |
| net6.0  | 6.0.x               |
| net7.0  | 7.0.x               |
| net8.0  | 8.0.x               |
| net9.0  | 9.0.x               |
| net10.0 | 10.0.x              |

- Providers:
  - SQL Server: Microsoft.Data.SqlClient
  - PostgreSQL: Npgsql
See project [LLS.EFBulkExtensions.csproj](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/LLS.EFBulkExtensions.csproj).

## Installation
- Local project: add a ProjectReference to `src/LLS.EFBulkExtensions`.
- NuGet (if published): reference the `LLS.EFBulkExtensions` package and ensure:
  - SQL Server: `Microsoft.EntityFrameworkCore.SqlServer`
  - PostgreSQL: `Npgsql.EntityFrameworkCore.PostgreSQL`

## Quick Start
Import the extensions and call the methods from your DbContext:

```csharp
using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
```

SQL Server:
```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("<your-connection-string>")
    .Options;
```
PostgreSQL:
```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql("<your-connection-string>")
    .Options;
```

Insert:
```csharp
await context.BulkInsertAsync(entities, new BulkInsertOptions {
    ReturnGeneratedIds = false,
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    PreserveIdentity = false,
    UseInternalTransaction = false,
    KeepNulls = false,
    UseAppLock = false
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

## Options
- BulkInsertOptions: [file](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkInsertOptions.cs)
  - ReturnGeneratedIds, BatchSize, TimeoutSeconds, PreserveIdentity, UseInternalTransaction, KeepNulls, UseAppLock
- BulkUpdateOptions: [file](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkUpdateOptions.cs)
  - BatchSize, TimeoutSeconds, UseInternalTransaction
- BulkDeleteOptions: [file](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkDeleteOptions.cs)
  - BatchSize, TimeoutSeconds, UseInternalTransaction

You can also configure annotations directly on the model for insert:
```csharp
builder.Property(p => p.Id).ValueGeneratedOnAdd(ReturnGeneratedIds: true, BatchSize: 10000);
```
Extension: [SequenceModelExtensions](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/SequenceModelExtensions.cs).

## Database Support
- SQL Server: [SqlServerBulkInserter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkInserter.cs), [SqlServerBulkUpdater](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkUpdater.cs), [SqlServerBulkDeleter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkDeleter.cs)
- PostgreSQL: [PostgresBulkInserter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkInserter.cs), [PostgresBulkUpdater](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkUpdater.cs), [PostgresBulkDeleter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkDeleter.cs)

## How It Works
- Insert:
  - SQL Server: uses SqlBulkCopy for staging and MERGE with OUTPUT when needed to return IDs.
  - PostgreSQL: uses binary COPY; when requested, inserts into a temporary table and returns IDs via INSERT ... RETURNING.
- Update: creates a temporary table, bulk loads into temp and applies UPDATE with JOIN on PK, ignoring ValueGenerated columns.
- Delete: creates a temporary table and applies DELETE with JOIN on PK.
Data construction: [DataTableBuilder](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Core/Internal/DataTableBuilder.cs) maps properties, types, conversions (includes EnumToString) and owned types.

## Full Examples
- SQL Server: [BulkInsertConsoleStyleTests.cs](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/tests/LLS.EFBulkExtensions.Tests.SqlServer/BulkInsertConsoleStyleTests.cs)
- PostgreSQL: [BulkInsertConsoleStyleTests.cs](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/tests/LLS.EFBulkExtensions.Tests.Postgres/BulkInsertConsoleStyleTests.cs)

## Requirements
- EF Core with correct entity mapping (table, schema, PK)
- To return IDs on inserts:
  - Use PK with `ValueGeneratedOnAdd` and optionally annotate `ReturnGeneratedIds` via model.
  - Supported ID CLR types for returning generated IDs:
    - Numeric: `long`, `int`, `short`, `byte`, `ulong`, `uint`, `ushort` (and nullable variants).
    - `Guid` (both SQL Server and PostgreSQL).

## APIs
- Inserts: [BulkInsertAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkInsertExtensions.cs)
- Updates: [BulkUpdateAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkUpdateExtensions.cs)
- Deletes: [BulkDeleteAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkDeleteExtensions.cs)

## Notes
- BatchSize and Timeout configurable via options and annotations
- Optional internal transaction
- Support for PreserveIdentity and KeepNulls on insert
