# Roadmap

Itens planejados e limitações conhecidas. Não é compromisso de prazo — é uma lista
honesta do que falta e do que foi adiado conscientemente.

## Novos provedores (objetivo original)
- [ ] Oracle
- [ ] MySQL
- [ ] MariaDB

Hoje há suporte a SQL Server, PostgreSQL e SQLite.

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
- [ ] Considerar registro via DI / factory de providers em vez de `new` direto por
  operação (hoje é barato porque os providers são stateless).

## Limitações conhecidas
- **SQLite** não possui API de bulk nativa: insert/update/delete são executados
  com comando preparado por linha dentro de uma única transação. Ainda assim evita o
  `DataTable` e ganha muito sobre inserts avulsos sem transação.
- **Discriminador TPH**: o valor gravado é o nome curto do tipo CLR. Valores de
  discriminador customizados (`HasDiscriminator().HasValue("...")`) não são honrados.
- **Tipos de ID para retorno**: numéricos inteiros (e anuláveis) e `Guid`. Outros
  tipos não são suportados em `ReturnGeneratedIds`.

## Testes
- Suíte determinística (rápida, em `Category!=Performance`) cobre os três bancos.
- Harnesses de 1M–10M linhas (`Category=Performance`) exigem servidor/arquivo e são
  para medição manual, não para CI.
- [ ] Sugestão: pipeline de CI rodando `dotnet test --filter "Category!=Performance"`.
