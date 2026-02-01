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

public sealed class SqlServerBulkDeleter : IBulkDeleter
{
    public async Task BulkDeleteAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        var (dataTable, properties) = DataTableBuilder.Build(context, entities, includeIdentity: true);

        if (dataTable.Rows.Count == 0) return;

        var conn = (SqlConnection)context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            shouldClose = true;
        }

        var transaction = (SqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
        var tempTableName = $"#TmpDelete_{Guid.NewGuid():N}";
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
                
                foreach (var p in properties)
                {
                    var col = p.GetColumnName(store)!;
                    bulk.ColumnMappings.Add(col, col);
                }

                await bulk.WriteToServerAsync(dataTable, cancellationToken);
            }

            var joinClauses = new List<string>();
            foreach (var p in pk.Properties)
            {
                var colName = p.GetColumnName(store)!;
                joinClauses.Add($"T.[{colName}] = S.[{colName}]");
            }

            if (joinClauses.Any())
            {
                var sqlDelete = $@"
                    DELETE T
                    FROM {fullTableName} T
                    INNER JOIN {tempTableName} S ON {string.Join(" AND ", joinClauses)};
                ";

                using (var cmd = new SqlCommand(sqlDelete, conn, transaction))
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

            if (shouldClose)
            {
                await conn.CloseAsync();
            }
        }
    }
}
