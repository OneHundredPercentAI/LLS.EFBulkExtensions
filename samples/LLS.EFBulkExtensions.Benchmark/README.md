# BulkInsert Benchmark

App de console para medir e comparar a performance do `BulkInsertAsync` entre os bancos suportados
(SQL Server, PostgreSQL, MySQL, MariaDB e SQLite).

Você escolhe o(s) banco(s), a **quantidade de registros** e a **quantidade de colunas**; o app gera
dados aleatórios, cria a tabela, executa o bulk insert cronometrado e imprime uma tabela comparativa.

## Como rodar

```bash
dotnet run --project samples/LLS.EFBulkExtensions.Benchmark
```

Fluxo:
1. Selecione um banco (`1`), vários para comparar (`1,3,4`) ou todos (`all`).
2. Para cada banco, confirme a connection string (Enter mantém o padrão) ou cole outra.
3. Informe nº de registros, nº de colunas e se quer retornar os IDs gerados (`ReturnGeneratedIds`).
4. Veja o resultado: tempo de geração, tempo de insert e linhas/segundo por banco.

## Connection strings padrão

| Banco | Padrão |
|------|--------|
| SQL Server | `Server=127.0.0.1;Database=BulkBenchDb;User Id=sa;Password=...;TrustServerCertificate=True` |
| PostgreSQL | `Host=127.0.0.1;Port=5432;Database=bulkbenchdb;Username=postgres;Password=...` |
| MySQL | `Server=127.0.0.1;Port=3306;Database=BulkBenchDb;User ID=root;Password=...;AllowLoadLocalInfile=true` |
| MariaDB | `Server=127.0.0.1;Port=3307;Database=BulkBenchDb;User ID=root;Password=...;AllowLoadLocalInfile=true` |
| SQLite | `Data Source=bulk_bench.db` (arquivo local) |

> O app usa um **banco dedicado** (`BulkBenchDb` / arquivo SQLite). A cada execução ele faz
> `EnsureDeleted` + `EnsureCreated` para recriar a tabela conforme o nº de colunas — não aponte para
> um banco de produção.

## Como funciona

- **Colunas variáveis:** um tipo CLR é gerado em runtime (`Reflection.Emit`) com `Id` (PK auto-incremento)
  + N colunas de tipos variados (`int`, `string`, `decimal`, `DateTime`, `bool`, `long`). A lib de bulk
  resolve as colunas pelo modelo do EF, então um tipo concreto é necessário (property bag não serve).
- **Schema:** criado pelo próprio EF (`EnsureCreated`), garantindo que o modelo e a tabela batam.
- **Dados:** gerados com setters compilados (Expression Trees) para que a geração não domine a medição.
- **Medição:** o `Stopwatch` envolve apenas o `BulkInsertAsync`; a geração de dados é cronometrada à parte.

## Observações de leitura dos números

- Com `ReturnGeneratedIds`, MySQL/MariaDB usam `INSERT` multi-linha (mais lento que o `LOAD DATA` puro),
  enquanto SQL Server/PostgreSQL fazem staging em tabela temporária — espere números menores que no
  caminho sem retorno de IDs.
- Compare sempre com o **mesmo nº de registros e colunas**; a comparação só é justa dentro da mesma rodada.
