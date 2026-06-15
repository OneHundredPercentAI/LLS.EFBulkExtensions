# Roadmap

Itens planejados e limitações conhecidas. Não é compromisso de prazo — é uma lista
honesta do que falta e do que foi adiado conscientemente.

## Novos provedores (objetivo original)
- [x] MySQL (Pomelo + MySqlConnector) — validado contra servidor local.
- [x] MariaDB — coberto pelo mesmo provider do MySQL; validado contra MariaDB 12.3.2 local
  (suite `MariaDbDeterministicTests`, 10/10 verde, incluindo `INSERT ... RETURNING`) e no CI.
- [ ] Oracle

Hoje há suporte a SQL Server, PostgreSQL, SQLite e MySQL/MariaDB, com insert, update,
delete e insert-or-update (upsert).

### Provider MySQL/MariaDB
- [x] `ReturnGeneratedIds`: no **MariaDB 10.5+** usa `INSERT ... RETURNING` (ID real por
  linha, independe de `auto_increment_increment` — seguro em Galera/multi-master); no
  **MySQL** (sem RETURNING) usa INSERT multi-linha + `LAST_INSERT_ID` (assume
  `auto_increment_increment = 1`). A detecção é automática via `SELECT VERSION()`.
- [x] Guard no caminho MySQL: valida `@@auto_increment_increment` e lança `NotSupportedException`
  quando ≠ 1, em vez de atribuir IDs errados em silêncio (ex.: Galera/multi-master).
- Requisito operacional: `MySqlBulkCopy` usa `LOAD DATA LOCAL INFILE` — precisa de
  `AllowLoadLocalInfile=true` na conexão e `local_infile` habilitado no servidor.

## Recursos de upsert
- [x] **`UpdateColumns` / `ExcludeUpdateColumns`** em `BulkInsertOrUpdateOptions`: controla
  quais colunas entram no ramo de UPDATE do upsert (ex.: atualizar só `Status` sem tocar em
  `CreatedAt`). Aceita nome da propriedade ou da coluna; nomes inexistentes lançam
  `InvalidOperationException`. Não altera schema nem o ramo de INSERT — é só a composição do
  comando. Válido nos 4 providers (SQL Server, PostgreSQL, MySQL/MariaDB, SQLite).
- [x] **`BulkInsertIfNotExistsAsync`** (e `InsertIfNotExists` nas options): insere apenas as
  chaves inexistentes, ignorando as já existentes — `ON CONFLICT DO NOTHING` (PG/SQLite),
  `INSERT IGNORE` (MySQL/MariaDB), `MERGE ... WHEN NOT MATCHED` (SQL Server).
- [x] **`BulkReadAsync`** — lê em massa por chave primária a partir de uma lista de entidades
  (só os valores de PK importam), em lotes via `Contains`, sem `WHERE IN` gigante. O EF traduz
  da melhor forma por provedor (PostgreSQL `= ANY(@array)`, SQL Server `OPENJSON`, SQLite/MySQL
  `IN`). Retorna entidades desanexadas; v1 só com PK de coluna única.
- [x] **Upsert por chave natural** (`MatchProperties`): correlaciona por colunas que não a PK,
  usando o mecanismo nativo (`ON CONFLICT` / `ON DUPLICATE KEY` / `MERGE`). Exige índice/constraint
  ÚNICO nas colunas de match (validado no modelo; erro claro caso contrário). A PK gerada não é
  inserida (o banco a gera) e as colunas de match não entram no UPDATE. Validado nos 4 providers.
- [ ] **Retorno de IDs no upsert** (`OUTPUT`/`RETURNING` correlacionado). O upsert ainda não
  devolve os IDs gerados — pendente como incremento separado.
- [x] **`BulkInsertOrUpdateOrDeleteAsync`** (sincronização/espelhamento de tabela): insere as
  chaves novas, atualiza as existentes e remove as linhas cuja PK não está no conjunto. Reaproveita
  o upsert (incl. `UpdateColumns`) e usa o `ExecuteDelete` do EF para o delete; tudo numa transação.
  Coleção vazia é rejeitada (não apaga a tabela por acidente). v1 só com PK de coluna única.

## Melhorias de performance
- [ ] **Streaming do caminho `ReturnGeneratedIds`** (SQL Server e PostgreSQL).
  Atualmente esse caminho ainda materializa um `DataTable` para o staging em tabela
  temporária (a correlação por coluna gerada exige cuidado). Valor menor (quem insere
  grandes volumes geralmente não precisa dos IDs de volta) e risco maior, por isso foi
  adiado. O caminho rápido (sem retorno de IDs) já é streaming nos três bancos.
- [ ] **`BatchSize` em todos os providers.** Hoje só o caminho do SQL Server
  (`SqlBulkCopy`) honra `BatchSize`. O `COPY` do PostgreSQL e o caminho linha-a-linha
  do SQLite ignoram a opção.
- [x] **`TimeoutSeconds` honrado em todos os providers.** Antes só SQL Server e MySQL/MariaDB
  aplicavam o timeout; agora os comandos de PostgreSQL e SQLite (insert/update/delete/upsert)
  também o respeitam. Exceção: o `COPY` (streaming) do PostgreSQL usa o timeout da
  connection string, não a opção por comando.

## Qualidade / API pública
- [ ] **XML docs** (`<GenerateDocumentationFile>` + comentários `///`) na superfície
  pública (extensões, options) para melhor experiência de IntelliSense/NuGet.
- [x] Dispatch de providers centralizado em `BulkProviderRegistry` (instâncias singleton
  stateless). Falta opcional: integração com DI (`IServiceCollection`).

## Limitações conhecidas
- **SQLite** não possui API de bulk nativa: insert/update/delete são executados
  com comando preparado por linha dentro de uma única transação. Ainda assim evita o
  `DataTable` e ganha muito sobre inserts avulsos sem transação.
- **Discriminador TPH**: o valor gravado vem dos metadados do EF
  (`IEntityType.GetDiscriminatorValue()`), honrando `HasDiscriminator().HasValue("...")`
  customizado, discriminadores não-string e coluna com nome customizado. Para a config
  padrão, o valor continua sendo o nome curto do tipo.
- **Tipos de ID para retorno**: numéricos inteiros (e anuláveis) e `Guid`. Outros
  tipos não são suportados em `ReturnGeneratedIds`.
- **Upsert (v1)**: correlaciona por PK (valores de PK obrigatórios) ou por chave natural via
  `MatchProperties` (exige índice único); não retorna IDs; colunas computadas/`OnAddOrUpdate`
  não são tratadas no ramo de insert.

## Testes
- Suíte determinística (rápida, em `Category!=Performance`) cobre SQLite, PostgreSQL,
  MySQL, MariaDB e SQL Server.
- Harnesses de 1M–10M linhas (`Category=Performance`) exigem servidor/arquivo e são
  para medição manual, não para CI.
- [x] Pipeline de CI (`.github/workflows/ci.yml`): build + SQLite (sem servidor) e
  integração Postgres/MySQL/MariaDB via service containers, com `--filter "Category!=Performance"`.
  SQL Server fica de fora (a senha de teste não atende à política do container mssql).
