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
            var idProp = entityType.FindPrimaryKey()?.Properties.First();
            var idCol = idProp?.GetColumnName(store);
            var destCols = properties.Select(p => p.GetColumnName(store)!).ToList();

            var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            if (options.ReturnGeneratedIds)
            {
                var tmpName = "tmp_bulk_" + Guid.NewGuid().ToString("N");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TEMP TABLE {Q(tmpName)} AS SELECT {string.Join(", ", destCols.Select(Q))} FROM {fullDest} LIMIT 0;";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    cmd.CommandText = $"ALTER TABLE {Q(tmpName)} ADD COLUMN \"__corr\" uuid NOT NULL;";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                dataTable.Columns.Add("__corr", typeof(Guid));
                var corr = new Guid[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    corr[i] = Guid.NewGuid();
                    dataTable.Rows[i]["__corr"] = corr[i];
                }

                var copyCols = string.Join(", ", destCols.Select(Q).Concat(new[] { Q("__corr") }));
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
                        await importer.WriteAsync((Guid)row["__corr"], null!, cancellationToken);
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
RETURNING {Q(idCol!)}, ""__corr"";";
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    var propInfo = idProp?.PropertyInfo!;
                    var idType = idProp?.ClrType ?? typeof(long);
                    var map = new Dictionary<Guid, int>(list.Count);
                    for (int i = 0; i < corr.Length; i++) map[corr[i]] = i;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var idVal = reader.GetFieldValue<long>(0);
                        var corrVal = reader.GetFieldValue<Guid>(1);
                        var idx = map[corrVal];
                        propInfo!.SetValue(list[idx], ConvertToClr(idVal, idType));
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

    private static object ConvertToClr(long value, Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (underlying == typeof(long)) return value;
        if (underlying == typeof(int)) return (int)value;
        if (underlying == typeof(short)) return (short)value;
        if (underlying == typeof(byte)) return (byte)value;
        if (underlying == typeof(decimal)) return (decimal)value;
        if (underlying == typeof(ulong)) return (ulong)value;
        if (underlying == typeof(uint)) return (uint)value;
        if (underlying == typeof(ushort)) return (ushort)value;
        throw new NotSupportedException($"Tipo de ID não suportado para conversão: {clrType.FullName}");
    }
}
