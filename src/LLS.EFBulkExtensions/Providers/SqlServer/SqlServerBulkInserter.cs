using System;
using System.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
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
        if (list.Count == 0) return;
        var includeIdentity = options.PreserveIdentity;

        var conn = (SqlConnection)context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            shouldClose = true;
        }

        // Quando há uma transação ambiente do EF, os comandos e o SqlBulkCopy devem participar dela;
        // caso contrário, o SqlBulkCopy lança em conexão com transação local pendente.
        var transaction = (SqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();

        if (options.ReturnGeneratedIds)
        {
            // Caminho com retorno de IDs ainda usa DataTable (correlação via coluna __corr no MERGE).
            var (dataTable, properties) = DataTableBuilder.Build(context, list, includeIdentity: includeIdentity);

            var tmpName = "#tmp_bulk_" + Guid.NewGuid().ToString("N");
            string Q(string s) => "[" + s.Replace("]", "]]") + "]";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
            var store = StoreObjectIdentifier.Table(tableName, schema);
            var idProp = entityType.FindPrimaryKey()?.Properties.First()
                ?? throw new InvalidOperationException($"A entidade {entityType.DisplayName()} não possui chave primária configurada.");
            var idCol = idProp.GetColumnName(store)
                ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para a entidade {entityType.DisplayName()}.");

            var idClrType = idProp.ClrType;
            var idUnderlyingClrType = Nullable.GetUnderlyingType(idClrType) ?? idClrType;
            string idSqlType;

            if (idUnderlyingClrType == typeof(long) ||
                idUnderlyingClrType == typeof(int) ||
                idUnderlyingClrType == typeof(short) ||
                idUnderlyingClrType == typeof(byte) ||
                idUnderlyingClrType == typeof(ulong) ||
                idUnderlyingClrType == typeof(uint) ||
                idUnderlyingClrType == typeof(ushort))
            {
                idSqlType = "bigint";
            }
            else if (idUnderlyingClrType == typeof(Guid))
            {
                idSqlType = "uniqueidentifier";
            }
            else
            {
                throw new NotSupportedException($"Tipo de ID não suportado para retorno de IDs no SqlServerBulkInserter: {idClrType.FullName}");
            }

            var destCols = properties.Select(p => p.GetColumnName(store)!)
                                     .Where(c => c != idCol)
                                     .ToList();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
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
            if (options.UseInternalTransaction && transaction == null) bulkOptions2 |= SqlBulkCopyOptions.UseInternalTransaction;
            if (options.KeepNulls) bulkOptions2 |= SqlBulkCopyOptions.KeepNulls;
            using (var bulk = new SqlBulkCopy(conn, bulkOptions2, transaction)
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
                cmd.Transaction = transaction;
                var colsList = string.Join(", ", destCols.Select(Q));
                var srcVals = string.Join(", ", destCols.Select(c => "src." + Q(c)));
                cmd.CommandText = $@"
DECLARE @out TABLE (Id {idSqlType}, corr uniqueidentifier);
MERGE {dest} AS d
USING {tmpName} AS src
ON 1 = 0
WHEN NOT MATCHED THEN
    INSERT ({colsList}) VALUES ({srcVals})
OUTPUT inserted.{Q(idCol)}, src.__corr INTO @out;
SELECT Id, corr FROM @out;";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var propInfo = idProp.PropertyInfo
                    ?? throw new InvalidOperationException($"Propriedade de chave primária {idProp.Name} não possui PropertyInfo associado.");
                var idType = idClrType;
                var idUnderlyingType = idUnderlyingClrType;
                var map = new Dictionary<Guid, int>(list.Count);
                for (int i = 0; i < corr.Length; i++) map[corr[i]] = i;
                while (await reader.ReadAsync(cancellationToken))
                {
                    object idVal;
                    if (idUnderlyingType == typeof(Guid))
                    {
                        idVal = reader.GetGuid(0);
                    }
                    else
                    {
                        var idLong = reader.GetInt64(0);
                        idVal = IdConversionHelper.FromInt64(idLong, idType);
                    }

                    var corrVal = reader.GetGuid(1);
                    var idx = map[corrVal];
                    propInfo.SetValue(list[idx], idVal);
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"DROP TABLE {tmpName}";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            // Caminho rápido: streaming via EntityDataReader, sem materializar um DataTable.
            var (columns, _, _) = DataTableBuilder.BuildColumns(context, list, includeIdentity: includeIdentity);

            var bulkOptions = SqlBulkCopyOptions.Default;
            if (options.PreserveIdentity) bulkOptions |= SqlBulkCopyOptions.KeepIdentity;
            if (options.UseInternalTransaction && transaction == null) bulkOptions |= SqlBulkCopyOptions.UseInternalTransaction;
            if (options.KeepNulls) bulkOptions |= SqlBulkCopyOptions.KeepNulls;

            using var bulk = new SqlBulkCopy(conn, bulkOptions, transaction)
            {
                DestinationTableName = schema is null ? $"[{tableName}]" : $"[{schema}].[{tableName}]",
                BatchSize = Math.Max(1, options.BatchSize),
                BulkCopyTimeout = Math.Max(0, options.TimeoutSeconds)
            };

            foreach (var column in columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            using var reader = new EntityDataReader<TEntity>(list, columns);
            await bulk.WriteToServerAsync(reader, cancellationToken);
        }

        if (shouldClose)
        {
            await conn.CloseAsync();
        }
    }
}
