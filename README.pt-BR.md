# LLS.EFBulkExtensions

[Português] | [English](README.md)

Extensões de alto desempenho para operações em massa (bulk) com EF Core: insert, update e delete em grandes volumes, com streaming (sem cópia integral em memória), conversões de tipos, owned types, TPH e retorno opcional de IDs gerados. Suporta SQL Server, PostgreSQL, SQLite e MySQL/MariaDB.

## Recursos
- Bulk insert, update, delete e insert-or-update (upsert) via métodos de extensão do `DbContext`
- Inserts em streaming (as entidades alimentam o provider diretamente, sem materializar um `DataTable` intermediário)
- Suporte a SQL Server (`SqlBulkCopy`/`MERGE`), PostgreSQL (`COPY` binário), SQLite (comando preparado por linha dentro de uma transação) e MySQL/MariaDB (`MySqlBulkCopy`)
- Retorno opcional de IDs gerados em inserts
- Tratamento de conversões (ex.: `EnumToString`), owned types e discriminador TPH
- Participa da transação ambiente do EF quando há uma aberta; caso contrário, transação interna opcional

## Compatibilidade e Dependências
- Target frameworks .NET: `net8.0`, `net9.0`, `net10.0`
- Versões de EF Core (Relational) alinhadas por target framework:

| .NET TFM | EF Core Relational |
|----------|--------------------|
| net8.0   | 8.0.x              |
| net9.0   | 9.0.x              |
| net10.0  | 10.0.x             |

- Provedores:
  - SQL Server: `Microsoft.Data.SqlClient`
  - PostgreSQL: `Npgsql`
  - SQLite: provider SQLite do EF Core (`Microsoft.EntityFrameworkCore.Sqlite`)
  - MySQL/MariaDB: `MySqlConnector` (provider EF: `Pomelo.EntityFrameworkCore.MySql`)

Veja [LLS.EFBulkExtensions.csproj](src/LLS.EFBulkExtensions/LLS.EFBulkExtensions.csproj).

## Instalação
- Projeto local: adicione uma `ProjectReference` para `src/LLS.EFBulkExtensions`.
- NuGet (se publicado): referencie o pacote `LLS.EFBulkExtensions` e garanta o provider EF Core correspondente:
  - SQL Server: `Microsoft.EntityFrameworkCore.SqlServer`
  - PostgreSQL: `Npgsql.EntityFrameworkCore.PostgreSQL`
  - SQLite: `Microsoft.EntityFrameworkCore.Sqlite`
  - MySQL/MariaDB: `Pomelo.EntityFrameworkCore.MySql` (defina `AllowLoadLocalInfile=true` na connection string; o servidor precisa de `local_infile` habilitado)

## Uso Rápido
Importe as extensões e chame os métodos a partir do seu `DbContext`:

```csharp
using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
```

O provider é resolvido automaticamente a partir do `DbContext` (SQL Server, PostgreSQL ou SQLite).

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

Insert ou update (upsert), correlacionado pela chave primária:
```csharp
await context.BulkInsertOrUpdateAsync(entities, new BulkInsertOrUpdateOptions {
    BatchSize = 10_000,
    TimeoutSeconds = 120,
    UseInternalTransaction = false
});
```
> O upsert correlaciona pela **chave primária**: linhas cuja PK já existe são atualizadas, as demais inseridas. Os valores de PK devem estar preenchidos nas entidades. Veja as limitações abaixo.

Atualizar apenas algumas colunas (as demais, nas linhas existentes, ficam intactas — útil para preservar `CreatedAt`):
```csharp
await context.BulkInsertOrUpdateAsync(entities, new BulkInsertOrUpdateOptions {
    UpdateColumns = new[] { nameof(Order.Status), nameof(Order.UpdatedAt) }
    // ou: ExcludeUpdateColumns = new[] { nameof(Order.CreatedAt) }
});
```
> `UpdateColumns`/`ExcludeUpdateColumns` aceitam nome da propriedade ou da coluna. Nomes inexistentes lançam exceção. Afetam só o ramo de UPDATE — nunca o schema e nunca o ramo de INSERT.

Inserir apenas as linhas que ainda não existem (chaves já existentes são ignoradas, sem update):
```csharp
await context.BulkInsertIfNotExistsAsync(entities);
```
> Mapeia para `ON CONFLICT DO NOTHING` (PostgreSQL/SQLite), `INSERT IGNORE` (MySQL/MariaDB) ou `MERGE ... WHEN NOT MATCHED` (SQL Server). Útil para importações idempotentes.

## Opções
- [BulkInsertOptions](src/LLS.EFBulkExtensions/Options/BulkInsertOptions.cs): `ReturnGeneratedIds`, `BatchSize`, `TimeoutSeconds`, `PreserveIdentity`, `UseInternalTransaction`, `KeepNulls`
- [BulkUpdateOptions](src/LLS.EFBulkExtensions/Options/BulkUpdateOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`
- [BulkDeleteOptions](src/LLS.EFBulkExtensions/Options/BulkDeleteOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`
- [BulkInsertOrUpdateOptions](src/LLS.EFBulkExtensions/Options/BulkInsertOrUpdateOptions.cs): `BatchSize`, `TimeoutSeconds`, `UseInternalTransaction`, `UpdateColumns`, `ExcludeUpdateColumns`, `InsertIfNotExists`

> Nota: `BatchSize` é respeitado pelo caminho do SQL Server (`SqlBulkCopy`). O `COPY` do PostgreSQL e o caminho linha-a-linha do SQLite não fatiam por `BatchSize`.

Também é possível habilitar o retorno de IDs gerados a nível de modelo:
```csharp
builder.Property(p => p.Id).ValueGeneratedOnAdd(ReturnGeneratedIds: true);
```
Opções de execução como `BatchSize`/`TimeoutSeconds` pertencem a `BulkInsertOptions`, não a anotações de modelo.
Veja [SequenceModelExtensions](src/LLS.EFBulkExtensions/Extensions/SequenceModelExtensions.cs).

## Suporte por Banco
- SQL Server (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkDeleter.cs)
- PostgreSQL (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkDeleter.cs)
- SQLite (insert/update/delete): [Inserter](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/Sqlite/SqliteBulkDeleter.cs)
- MySQL/MariaDB (insert/update/delete/upsert): [Inserter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkInserter.cs), [Updater](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkUpdater.cs), [Deleter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkDeleter.cs), [Upserter](src/LLS.EFBulkExtensions/Providers/MySql/MySqlBulkUpserter.cs)

## Como Funciona
- Insert:
  - SQL Server: o caminho rápido faz streaming das entidades no `SqlBulkCopy` via um `DbDataReader`. Ao retornar IDs, faz staging em tabela temporária e usa `MERGE ... OUTPUT` correlacionado por uma coluna gerada.
  - PostgreSQL: o caminho rápido faz streaming das entidades em um `COPY` binário. Ao retornar IDs, faz `COPY` para tabela temporária e roda `INSERT ... SELECT ... ORDER BY <ordinal> RETURNING`, correlacionando os IDs por posição.
  - SQLite: um `INSERT` preparado (opcionalmente `RETURNING` para IDs) executado uma vez por linha dentro de uma única transação; as linhas são percorridas em streaming, sem materializar `DataTable`.
  - MySQL/MariaDB: `MySqlBulkCopy` (`LOAD DATA LOCAL INFILE`) alimentado pelo reader em streaming; `ReturnGeneratedIds` usa `INSERT` multi-linha + `LAST_INSERT_ID` (assume passo de auto-increment = 1). Update/delete fazem staging em `TEMPORARY TABLE` e aplicam `UPDATE`/`DELETE ... JOIN` na PK; upsert usa `INSERT ... ON DUPLICATE KEY UPDATE`.
- Update: faz staging das linhas em tabela temporária e aplica `UPDATE` com JOIN na PK, ignorando colunas value-generated (no SQLite, `UPDATE` preparado por linha).
- Delete: faz staging das chaves e aplica `DELETE` com JOIN na PK (no SQLite, `DELETE` preparado por linha).
- Insert ou update (upsert), por PK: SQL Server `MERGE` (com `SET IDENTITY_INSERT` quando a PK é identity); PostgreSQL `INSERT ... ON CONFLICT (pk) DO UPDATE`; SQLite `INSERT ... ON CONFLICT(pk) DO UPDATE` por linha.

O mapeamento de colunas (propriedades, tipos CLR/provider, conversões incluindo `EnumToString`, owned types e discriminador TPH) é resolvido por [DataTableBuilder.BuildColumns](src/LLS.EFBulkExtensions/Core/Internal/DataTableBuilder.cs) e consumido pelo [EntityDataReader](src/LLS.EFBulkExtensions/Core/Internal/EntityDataReader.cs) (e pelo staging em `DataTable` onde ainda é necessário).

## Exemplos / Testes
As suítes de teste determinísticas servem também como exemplos de uso:
- SQL Server: [SqlServerDeterministicTests.cs](tests/LLS.EFBulkExtensions.Tests.SqlServer/SqlServerDeterministicTests.cs)
- PostgreSQL: [PostgresDeterministicTests.cs](tests/LLS.EFBulkExtensions.Tests.Postgres/PostgresDeterministicTests.cs)
- SQLite: [BulkSqliteFunctionalTests.cs](tests/LLS.EFBulkExtensions.Tests.Sqlite/BulkSqliteFunctionalTests.cs)

Rodar a suíte rápida (determinística), excluindo os harnesses grandes de performance:
```
dotnet test --filter "Category!=Performance"
```

## Requisitos
- EF Core com mapeamento correto das entidades (tabela, schema, PK)
- Para retornar IDs em inserts:
  - Utilize PK com `ValueGeneratedOnAdd` e, se desejar, anote `ReturnGeneratedIds` no modelo.
  - Tipos CLR de ID suportados: numéricos `long`, `int`, `short`, `byte`, `ulong`, `uint`, `ushort` (e variantes anuláveis); `Guid` (SQL Server e PostgreSQL).

## APIs
- [BulkInsertAsync](src/LLS.EFBulkExtensions/Extensions/BulkInsertExtensions.cs)
- [BulkUpdateAsync](src/LLS.EFBulkExtensions/Extensions/BulkUpdateExtensions.cs)
- [BulkDeleteAsync](src/LLS.EFBulkExtensions/Extensions/BulkDeleteExtensions.cs)
- [BulkInsertOrUpdateAsync](src/LLS.EFBulkExtensions/Extensions/BulkInsertOrUpdateExtensions.cs)
- [BulkInsertIfNotExistsAsync](src/LLS.EFBulkExtensions/Extensions/BulkInsertOrUpdateExtensions.cs)

## Upsert — limitações (v1)
- Correspondência **somente por chave primária**; os valores de PK devem estar preenchidos.
- **Não** retorna IDs gerados.
- PKs identity/serial: SQL Server usa `SET IDENTITY_INSERT`; colunas `GENERATED ALWAYS` no PostgreSQL usam `OVERRIDING SYSTEM VALUE`.
- Colunas computadas/`OnAddOrUpdate` não são tratadas no ramo de insert.

## Roadmap
Veja [ROADMAP.md](ROADMAP.md) para melhorias planejadas e limitações conhecidas.
