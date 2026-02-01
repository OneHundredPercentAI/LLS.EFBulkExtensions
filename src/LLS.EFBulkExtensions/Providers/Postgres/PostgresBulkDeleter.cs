using System;
using System.Collections.Generic;
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

public sealed class PostgresBulkDeleter : IBulkDeleter
{
    public async Task BulkDeleteAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var (dataTable, properties) = DataTableBuilder.Build(context, entities, includeIdentity: true);
        if (dataTable.Rows.Count == 0) return;

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State != System.Data.ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(cancellationToken);

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
            var tmpName = "tmp_delete_" + Guid.NewGuid().ToString("N");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TEMP TABLE {Q(tmpName)} AS SELECT * FROM {fullDest} LIMIT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var destCols = properties.Select(p => p.GetColumnName(store)!).ToList();
            var copyCols = string.Join(", ", destCols.Select(Q));
            using (var importer = await conn.BeginBinaryImportAsync($"COPY {Q(tmpName)} ({copyCols}) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (System.Data.DataRow row in dataTable.Rows)
                {
                    await importer.StartRowAsync(cancellationToken);
                    foreach (var col in destCols)
                    {
                        var v = row[col];
                        if (v == DBNull.Value) await importer.WriteNullAsync(cancellationToken);
                        else await importer.WriteAsync(v, null, cancellationToken);
                    }
                }
                await importer.CompleteAsync(cancellationToken);
            }

            var pkProps = entityType.FindPrimaryKey()?.Properties ?? throw new InvalidOperationException("Entidade sem chave primária.");
            var join = string.Join(" AND ", pkProps.Select(p => $"{Q("t")}.{Q(p.GetColumnName(store)!)} = {Q("s")}.{Q(p.GetColumnName(store)!)}"));

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM {fullDest} AS {Q("t")} USING {Q(tmpName)} AS {Q("s")} WHERE {join};";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DROP TABLE {Q(tmpName)};";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }
}
