using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace LLS.EFBulkExtensions.Providers.SqlServer;

public sealed class SqlServerBulkUpdater : IBulkUpdater
{
    public async Task BulkUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        if (list.Count == 0) return;
        var (columns, _, _) = BulkMapper.BuildColumns(context, list, includeIdentity: true);

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (SqlConnection)bulkConn.Connection;
        var transaction = (SqlTransaction?)bulkConn.AmbientTransaction;
        var tempTableName = $"#TmpUpdate_{Guid.NewGuid():N}";
        var fullTableName = schema == null ? $"[{tableName}]" : $"[{schema}].[{tableName}]";

        try
        {
            var sqlCreate = $"SELECT TOP 0 * INTO {tempTableName} FROM {fullTableName}";
            
            using (var cmd = new SqlCommand(sqlCreate, conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var bulkOptions = SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls;
            if (options.UseInternalTransaction && transaction == null)
            {
                bulkOptions |= SqlBulkCopyOptions.UseInternalTransaction;
            }

            using (var bulk = new SqlBulkCopy(conn, bulkOptions, transaction))
            {
                bulk.DestinationTableName = tempTableName;
                bulk.BatchSize = options.BatchSize;
                bulk.BulkCopyTimeout = options.TimeoutSeconds;

                foreach (var column in columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                using var reader = new EntityDataReader<TEntity>(list, columns);
                await bulk.WriteToServerAsync(reader, cancellationToken);
            }

            var setClauses = new List<string>();
            var joinClauses = new List<string>();
            
            var pkProperties = entityType.FindPrimaryKey()?.Properties ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");

            foreach (var column in columns)
            {
                var colName = column.ColumnName;
                if (pkProperties.Contains(column.Property))
                {
                    joinClauses.Add($"T.[{colName}] = S.[{colName}]");
                }
                else if (column.Property.ValueGenerated != ValueGenerated.OnAdd && column.Property.ValueGenerated != ValueGenerated.OnUpdate)
                {
                    setClauses.Add($"T.[{colName}] = S.[{colName}]");
                }
            }

            if (setClauses.Any() && joinClauses.Any())
            {
                var sqlUpdate = $@"
                    UPDATE T
                    SET {string.Join(", ", setClauses)}
                    FROM {fullTableName} T
                    INNER JOIN {tempTableName} S ON {string.Join(" AND ", joinClauses)};
                ";

                using (var cmd = new SqlCommand(sqlUpdate, conn, transaction))
                {
                    cmd.CommandTimeout = options.TimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                var sqlDrop = $"IF OBJECT_ID('{tempTableName}') IS NOT NULL DROP TABLE {tempTableName};";
                using (var cmd = new SqlCommand(sqlDrop, conn, transaction))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            catch { }
        }
    }
}
