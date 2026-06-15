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

public sealed class PostgresBulkUpserter : IBulkUpserter
{
    public async Task BulkInsertOrUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var (dataTable, properties) = BulkMapper.Build(context, entities, includeIdentity: true);
        if (dataTable.Rows.Count == 0) return;

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");

        var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (NpgsqlConnection)bulkConn.Connection;

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
            var tmpName = "tmp_upsert_" + Guid.NewGuid().ToString("N");

            var destCols = properties.Select(p => p.GetColumnName(store)!).ToList();
            var pkCols = pk.Properties.Select(p => p.GetColumnName(store)!).ToList();
            var updatableProps = properties
                .Where(p => !pk.Properties.Contains(p) && p.ValueGenerated != ValueGenerated.OnAdd && p.ValueGenerated != ValueGenerated.OnUpdate)
                .ToList();
            var allowedUpdate = UpsertColumnResolver.ResolveUpdateColumns(
                updatableProps.Select(p => (p.Name, p.GetColumnName(store)!)).ToList(),
                properties.Select(p => (p.Name, p.GetColumnName(store)!)).ToList(),
                options);
            var updateCols = updatableProps
                .Select(p => p.GetColumnName(store)!)
                .Where(allowedUpdate.Contains)
                .ToList();

            // GENERATED ALWAYS exige OVERRIDING SYSTEM VALUE para inserir PK explícita.
            var overriding = pk.Properties.Any(p =>
                string.Equals(p.FindAnnotation("Npgsql:ValueGenerationStrategy")?.Value?.ToString(), "IdentityAlwaysColumn", StringComparison.Ordinal))
                ? "OVERRIDING SYSTEM VALUE "
                : string.Empty;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TEMP TABLE {Q(tmpName)} AS SELECT {string.Join(", ", destCols.Select(Q))} FROM {fullDest} LIMIT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

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
                        else await importer.WriteAsync(v, null!, cancellationToken);
                    }
                }
                await importer.CompleteAsync(cancellationToken);
            }

            var conflict = string.Join(", ", pkCols.Select(Q));
            var action = updateCols.Count > 0
                ? $"DO UPDATE SET {string.Join(", ", updateCols.Select(c => $"{Q(c)} = EXCLUDED.{Q(c)}"))}"
                : "DO NOTHING";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
INSERT INTO {fullDest} ({copyCols}) {overriding}
SELECT {copyCols} FROM {Q(tmpName)}
ON CONFLICT ({conflict}) {action};";
                cmd.CommandTimeout = options.TimeoutSeconds;
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
            await bulkConn.DisposeAsync();
        }
    }
}
