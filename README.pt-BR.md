# LLS.EFBulkExtensions

[Português] | [English](README.md)

Extensões de alto desempenho para operações em massa (bulk) com Entity Framework Core 9: insert, update e delete em grandes volumes, com processamento em lote, suporte a SQL Server e PostgreSQL, tratamento de tipos/conversões, owned types e opção de retorno de IDs gerados.

## Recursos
- Bulk insert, update e delete com DbContext via métodos de extensão
- Processamento em lotes (BatchSize) e controle de timeout
- Suporte a SQL Server (SqlBulkCopy/MERGE) e PostgreSQL (COPY binário)
- Retorno opcional de IDs gerados em inserts
- Tratamento de propriedades com conversões (ex.: EnumToString) e owned types
- Transação interna opcional e configurações adicionais por anotação de modelo

## Compatibilidade e Dependências
- .NET: net9.0
- EF Core 9.0 (Relational)
- Provedores:
  - SQL Server: Microsoft.Data.SqlClient
  - PostgreSQL: Npgsql
Veja o projeto [LLS.EFBulkExtensions.csproj](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/LLS.EFBulkExtensions.csproj).

## Instalação
- Projeto local: adicione uma ProjectReference para `src/LLS.EFBulkExtensions`.
- NuGet (se publicado): referencie o pacote `LLS.EFBulkExtensions` e garanta:
  - SQL Server: `Microsoft.EntityFrameworkCore.SqlServer`
  - PostgreSQL: `Npgsql.EntityFrameworkCore.PostgreSQL`

## Uso Rápido
Importe as extensões e chame os métodos a partir do seu DbContext:

```csharp
using LLS.EFBulkExtensions.Extensions;
using LLS.EFBulkExtensions.Options;
```

SQL Server:
```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("<sua-connection-string>")
    .Options;
```
PostgreSQL:
```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql("<sua-connection-string>")
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

## Opções
- BulkInsertOptions: [arquivo](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkInsertOptions.cs)
  - ReturnGeneratedIds, BatchSize, TimeoutSeconds, PreserveIdentity, UseInternalTransaction, KeepNulls, UseAppLock
- BulkUpdateOptions: [arquivo](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkUpdateOptions.cs)
  - BatchSize, TimeoutSeconds, UseInternalTransaction
- BulkDeleteOptions: [arquivo](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Options/BulkDeleteOptions.cs)
  - BatchSize, TimeoutSeconds, UseInternalTransaction

Também é possível configurar anotações diretamente no modelo para o insert:
```csharp
builder.Property(p => p.Id).ValueGeneratedOnAdd(ReturnGeneratedIds: true, BatchSize: 10000);
```
Extensão: [SequenceModelExtensions](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/SequenceModelExtensions.cs).

## Suporte por Banco
- SQL Server: [SqlServerBulkInserter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkInserter.cs), [SqlServerBulkUpdater](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkUpdater.cs), [SqlServerBulkDeleter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/SqlServer/SqlServerBulkDeleter.cs)
- PostgreSQL: [PostgresBulkInserter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkInserter.cs), [PostgresBulkUpdater](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkUpdater.cs), [PostgresBulkDeleter](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Providers/Postgres/PostgresBulkDeleter.cs)

## Como Funciona
- Insert:
  - SQL Server: usa SqlBulkCopy para staging e MERGE com OUTPUT quando necessário para retornar IDs.
  - PostgreSQL: usa COPY binário; quando solicitado, insere em tabela temporária e retorna IDs via INSERT ... RETURNING.
- Update: cria tabela temporária, faz bulk para temp e aplica UPDATE com JOIN por PK, ignorando colunas ValueGenerated.
- Delete: cria tabela temporária e aplica DELETE com JOIN por PK.
Construção de dados: [DataTableBuilder](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Core/Internal/DataTableBuilder.cs) mapeia propriedades, tipos, conversões (inclui EnumToString) e owned types.

## Exemplos Completos
- SQL Server: [BulkInsertConsoleStyleTests.cs](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/tests/LLS.EFBulkExtensions.Tests.SqlServer/BulkInsertConsoleStyleTests.cs)
- PostgreSQL: [BulkInsertConsoleStyleTests.cs](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/tests/LLS.EFBulkExtensions.Tests.Postgres/BulkInsertConsoleStyleTests.cs)

## Requisitos
- EF Core 9 com mapeamento correto das entidades (tabela, schema, PK)
- Para retornar IDs em inserts, utilize PK com `ValueGeneratedOnAdd` e, se desejar, anote `ReturnGeneratedIds` via modelo

## APIs
- Inserts: [BulkInsertAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkInsertExtensions.cs)
- Updates: [BulkUpdateAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkUpdateExtensions.cs)
- Deletes: [BulkDeleteAsync](file:///c:/Projetos/LLServTec/LLS.EFBulkExtensions/src/LLS.EFBulkExtensions/Extensions/BulkDeleteExtensions.cs)

## Observações
- BatchSize e Timeout configuráveis por opções e anotações
- Transação interna opcional
- Suporte a PreserveIdentity e KeepNulls no insert
