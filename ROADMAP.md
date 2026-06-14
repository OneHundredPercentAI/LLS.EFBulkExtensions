# Roadmap

Itens planejados e limitações conhecidas. Não é compromisso de prazo — é uma lista
honesta do que falta e do que foi adiado conscientemente.

## Novos provedores (objetivo original)
- [x] MySQL (Pomelo + MySqlConnector) — validado contra servidor local.
- [~] MariaDB — coberto pelo mesmo provider do MySQL; falta validar contra servidor MariaDB.
- [ ] Oracle

Hoje há suporte a SQL Server, PostgreSQL, SQLite e MySQL/MariaDB, com insert, update,
delete e insert-or-update (upsert).

### Provider MySQL
- [x] `ReturnGeneratedIds` via INSERT multi-linha + `LAST_INSERT_ID` (assume
  `auto_increment_increment = 1`).
- Requisito operacional: `MySqlBulkCopy` usa `LOAD DATA LOCAL INFILE` — precisa de
  `AllowLoadLocalInfile=true` na conexão e `local_infile` habilitado no servidor.

## Melhorias de performance
- [ ] **Streaming do caminho `ReturnGeneratedIds`** (SQL Server e PostgreSQL).
  Atualmente esse caminho ainda materializa um `DataTable` para o staging em tabela
  temporária (a correlação por coluna gerada exige cuidado). Valor menor (quem insere
  grandes volumes geralmente não precisa dos IDs de volta) e risco maior, por isso foi
  adiado. O caminho rápido (sem retorno de IDs) já é streaming nos três bancos.
- [ ] **`BatchSize` em todos os providers.** Hoje só o caminho do SQL Server
  (`SqlBulkCopy`) honra `BatchSize`. O `COPY` do PostgreSQL e o caminho linha-a-linha
  do SQLite ignoram a opção.

## Qualidade / API pública
- [ ] **XML docs** (`<GenerateDocumentationFile>` + comentários `///`) na superfície
  pública (extensões, options) para melhor experiência de IntelliSense/NuGet.
- [x] Dispatch de providers centralizado em `BulkProviderRegistry` (instâncias singleton
  stateless). Falta opcional: integração com DI (`IServiceCollection`).
- [ ] **Upsert por chave natural** (`MatchProperties`) além da PK; e **retorno de IDs**
  no upsert. O upsert v1 correlaciona somente por PK e não retorna IDs.

## Limitações conhecidas
- **SQLite** não possui API de bulk nativa: insert/update/delete são executados
  com comando preparado por linha dentro de uma única transação. Ainda assim evita o
  `DataTable` e ganha muito sobre inserts avulsos sem transação.
- **Discriminador TPH**: o valor gravado é o nome curto do tipo CLR. Valores de
  discriminador customizados (`HasDiscriminator().HasValue("...")`) não são honrados.
- **Tipos de ID para retorno**: numéricos inteiros (e anuláveis) e `Guid`. Outros
  tipos não são suportados em `ReturnGeneratedIds`.
- **Upsert (v1)**: correspondência somente por PK (valores de PK obrigatórios); não
  retorna IDs; colunas computadas/`OnAddOrUpdate` não são tratadas no ramo de insert.

## Testes
- Suíte determinística (rápida, em `Category!=Performance`) cobre os três bancos.
- Harnesses de 1M–10M linhas (`Category=Performance`) exigem servidor/arquivo e são
  para medição manual, não para CI.
- [ ] Sugestão: pipeline de CI rodando `dotnet test --filter "Category!=Performance"`.
