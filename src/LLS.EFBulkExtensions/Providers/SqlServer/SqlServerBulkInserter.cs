using System;
using System.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;

namespace LLS.EFBulkExtensions.Providers.SqlServer;

public sealed class SqlServerBulkInserter : IBulkInserter
{
    public async Task BulkInsertAsync<TEntity>(
        DbContext context, 
        IEnumerable<TEntity> entities, 
        BulkInsertOptions options, 
        CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado (GetTableName retornou null)");
        var schema = entityType.GetSchema();

        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        var includeIdentity = options.PreserveIdentity;
        var (dataTable, properties) = DataTableBuilder.Build(context, list, includeIdentity: includeIdentity);

        var conn = (SqlConnection)context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            shouldClose = true;
        }

        if (options.ReturnGeneratedIds)
        {
            var tmpName = "#tmp_bulk_" + Guid.NewGuid().ToString("N");
            string Q(string s) => "[" + s.Replace("]", "]]") + "]";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
            var store = StoreObjectIdentifier.Table(tableName, schema);
            var idProp = entityType.FindPrimaryKey()?.Properties.First();
            var idCol = idProp?.GetColumnName(store);
            var destCols = properties.Select(p => p.GetColumnName(store)!).Where(c => c != idCol).ToList();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT TOP 0 {string.Join(", ", destCols.Select(Q))} INTO {tmpName} FROM {dest}";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                cmd.CommandText = $"ALTER TABLE {tmpName} ADD [__corr] uniqueidentifier NOT NULL";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var corr = new Guid[list.Count];
            dataTable.Columns.Add("__corr", typeof(Guid));
            for (int i = 0; i < list.Count; i++)
            {
                var g = Guid.NewGuid();
                corr[i] = g;
                dataTable.Rows[i]["__corr"] = g;
            }

            var bulkOptions2 = SqlBulkCopyOptions.Default;
            if (options.UseInternalTransaction) bulkOptions2 |= SqlBulkCopyOptions.UseInternalTransaction;
            if (options.KeepNulls) bulkOptions2 |= SqlBulkCopyOptions.KeepNulls;
            using (var bulk = new SqlBulkCopy(conn, bulkOptions2, null)
            {
                DestinationTableName = tmpName,
                BatchSize = Math.Max(1, options.BatchSize),
                BulkCopyTimeout = Math.Max(0, options.TimeoutSeconds)
            })
            {
                foreach (var col in destCols)
                {
                    bulk.ColumnMappings.Add(col, col);
                }
                bulk.ColumnMappings.Add("__corr", "__corr");
                await bulk.WriteToServerAsync(dataTable, cancellationToken);
            }

            using (var cmd = conn.CreateCommand())
            {
                var colsList = string.Join(", ", destCols.Select(Q));
                var srcVals = string.Join(", ", destCols.Select(c => "src." + Q(c)));
                cmd.CommandText = $@"
DECLARE @out TABLE (Id bigint, corr uniqueidentifier);
MERGE {dest} AS d
USING {tmpName} AS src
ON 1 = 0
WHEN NOT MATCHED THEN
    INSERT ({colsList}) VALUES ({srcVals})
OUTPUT inserted.{Q(idCol!)}, src.__corr INTO @out;
SELECT Id, corr FROM @out;";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var propInfo = idProp?.PropertyInfo!;
                var idType = idProp?.ClrType ?? typeof(long);
                var map = new Dictionary<Guid, int>(list.Count);
                for (int i = 0; i < corr.Length; i++) map[corr[i]] = i;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var idVal = reader.GetInt64(0);
                    var corrVal = reader.GetGuid(1);
                    var idx = map[corrVal];
                    propInfo!.SetValue(list[idx], ConvertToClr(idVal, idType));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DROP TABLE {tmpName}";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            var bulkOptions = SqlBulkCopyOptions.Default;
            if (options.PreserveIdentity) bulkOptions |= SqlBulkCopyOptions.KeepIdentity;
            if (options.UseInternalTransaction) bulkOptions |= SqlBulkCopyOptions.UseInternalTransaction;
            if (options.KeepNulls) bulkOptions |= SqlBulkCopyOptions.KeepNulls;

            using var bulk = new SqlBulkCopy(conn, bulkOptions, null)
            {
                DestinationTableName = schema is null ? $"[{tableName}]" : $"[{schema}].[{tableName}]",
                BatchSize = Math.Max(1, options.BatchSize),
                BulkCopyTimeout = Math.Max(0, options.TimeoutSeconds)
            };

            foreach (var p in properties)
            {
                var store2 = StoreObjectIdentifier.Table(tableName, schema);
                var col = p.GetColumnName(store2)!;
                bulk.ColumnMappings.Add(col, col);
            }

            await bulk.WriteToServerAsync(dataTable, cancellationToken);
        }

        if (shouldClose)
        {
            await conn.CloseAsync();
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
