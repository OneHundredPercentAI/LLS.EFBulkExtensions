using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace LLS.EFBulkExtensions.Providers.Postgres;

public sealed class PostgresBulkInserter : IBulkInserter
{
    public async Task BulkInsertAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado (GetTableName retornou null)");
        var schema = entityType.GetSchema();
        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        var includeIdentity = options.PreserveIdentity;
        var (dataTable, properties) = DataTableBuilder.Build(context, list, includeIdentity: includeIdentity);

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            shouldClose = true;
        }

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var store = StoreObjectIdentifier.Table(tableName, schema);
            var idProp = entityType.FindPrimaryKey()?.Properties.First()
                ?? throw new InvalidOperationException($"A entidade {entityType.DisplayName()} não possui chave primária configurada.");
            var idCol = idProp.GetColumnName(store)
                ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para a entidade {entityType.DisplayName()}.");
            var destCols = properties.Select(p => p.GetColumnName(store)!).ToList();

            var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            if (options.ReturnGeneratedIds)
            {
                var tmpName = "tmp_bulk_" + Guid.NewGuid().ToString("N");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TEMP TABLE {Q(tmpName)} AS SELECT {string.Join(", ", destCols.Select(Q))} FROM {fullDest} LIMIT 0;";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    cmd.CommandText = $"ALTER TABLE {Q(tmpName)} ADD COLUMN \"__ord\" bigint NOT NULL;";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // Coluna ordinal. RETURNING não consegue projetar colunas que existem apenas na origem
                // (temp table); por isso correlacionamos por posição: INSERT ... SELECT ... ORDER BY "__ord"
                // emite as linhas do RETURNING na ordem de inserção, igual à ordem da lista.
                dataTable.Columns.Add("__ord", typeof(long));
                for (int i = 0; i < list.Count; i++)
                {
                    dataTable.Rows[i]["__ord"] = (long)i;
                }

                var copyCols = string.Join(", ", destCols.Select(Q).Concat(new[] { Q("__ord") }));
                using (var importer = await conn.BeginBinaryImportAsync($"COPY {Q(tmpName)} ({copyCols}) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    foreach (DataRow row in dataTable.Rows)
                    {
                        await importer.StartRowAsync(cancellationToken);
                        foreach (var col in destCols)
                        {
                            var v = row[col];
                            if (v == DBNull.Value) await importer.WriteNullAsync(cancellationToken);
                            else await importer.WriteAsync(v, null!, cancellationToken);
                        }
                        await importer.WriteAsync((long)row["__ord"], null!, cancellationToken);
                    }
                    await importer.CompleteAsync(cancellationToken);
                }

                using (var cmd = conn.CreateCommand())
                {
                    var colsList = string.Join(", ", destCols.Select(Q));
                    cmd.CommandText = $@"
INSERT INTO {fullDest} ({colsList})
SELECT {colsList}
FROM {Q(tmpName)}
ORDER BY ""__ord""
RETURNING {Q(idCol!)};";
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    var propInfo = idProp.PropertyInfo
                        ?? throw new InvalidOperationException($"Propriedade de chave primária {idProp.Name} não possui PropertyInfo associado.");
                    var idType = idProp.ClrType;
                    var idUnderlyingType = Nullable.GetUnderlyingType(idType) ?? idType;
                    var index = 0;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        object idVal;
                        if (idUnderlyingType == typeof(Guid))
                        {
                            idVal = reader.GetFieldValue<Guid>(0);
                        }
                        else
                        {
                            var idLong = reader.GetFieldValue<long>(0);
                            idVal = IdConversionHelper.FromInt64(idLong, idType);
                        }

                        propInfo.SetValue(list[index], idVal);
                        index++;
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"DROP TABLE {Q(tmpName)};";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                // Fast path without returning IDs: COPY directly to destination
                var copyCols = string.Join(", ", destCols.Select(Q));
                using (var importer = await conn.BeginBinaryImportAsync($"COPY {fullDest} ({copyCols}) FROM STDIN (FORMAT BINARY)", cancellationToken))
                {
                    foreach (DataRow row in dataTable.Rows)
                    {
                        await importer.StartRowAsync(cancellationToken);
                        foreach (var col in destCols)
                        {
                            var v = row[col];
                            if (v == DBNull.Value) await importer.WriteNullAsync(cancellationToken);
                            else await importer.WriteAsync(v, null!, cancellationToken);
                        }
                    }
                    await importer.CompleteAsync(cancellationToken);
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                await conn.CloseAsync();
            }
        }
    }
}
