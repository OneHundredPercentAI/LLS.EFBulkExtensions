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

public sealed class PostgresBulkUpdater : IBulkUpdater
{
    public async Task BulkUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        if (list.Count == 0) return;
        var (columns, _, _) = BulkMapper.BuildColumns(context, list, includeIdentity: true);

        var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (NpgsqlConnection)bulkConn.Connection;

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
            var tmpName = "tmp_update_" + Guid.NewGuid().ToString("N");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE TEMP TABLE {Q(tmpName)} AS SELECT * FROM {fullDest} LIMIT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var copyCols = string.Join(", ", columns.Select(c => Q(c.ColumnName)));
            using (var importer = await conn.BeginBinaryImportAsync($"COPY {Q(tmpName)} ({copyCols}) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var entity in list)
                {
                    await importer.StartRowAsync(cancellationToken);
                    foreach (var col in columns)
                    {
                        var v = col.GetProviderValue(entity!);
                        if (v == null) await importer.WriteNullAsync(cancellationToken);
                        else await importer.WriteAsync(v, null!, cancellationToken);
                    }
                }
                await importer.CompleteAsync(cancellationToken);
            }

            var pkProps = entityType.FindPrimaryKey()?.Properties ?? throw new InvalidOperationException("Entidade sem chave primária.");
            var join = string.Join(" AND ", pkProps.Select(p => $"{Q("t")}.{Q(p.GetColumnName(store)!)} = {Q("s")}.{Q(p.GetColumnName(store)!)}"));

            var setCols = new List<string>();
            foreach (var col in columns)
            {
                if (pkProps.Contains(col.Property)) continue;
                if (col.Property.ValueGenerated == ValueGenerated.OnAdd || col.Property.ValueGenerated == ValueGenerated.OnUpdate) continue;
                setCols.Add($"{Q(col.ColumnName)} = {Q("s")}.{Q(col.ColumnName)}");
            }

            if (setCols.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE {fullDest} AS {Q("t")} SET {string.Join(", ", setCols)} FROM {Q(tmpName)} AS {Q("s")} WHERE {join};";
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
