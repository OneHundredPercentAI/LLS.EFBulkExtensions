using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LLS.EFBulkExtensions.Providers.SqlServer;

public sealed class SqlServerBulkUpserter : IBulkUpserter
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

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (SqlConnection)bulkConn.Connection;
        var transaction = (SqlTransaction?)bulkConn.AmbientTransaction;

        var tempTableName = $"#TmpUpsert_{Guid.NewGuid():N}";
        var fullTableName = schema == null ? $"[{tableName}]" : $"[{schema}].[{tableName}]";

        // SET IDENTITY_INSERT é necessário apenas quando a PK é uma coluna IDENTITY do SQL Server.
        var pkIsIdentity = pk.Properties.Any(p =>
            string.Equals(p.FindAnnotation("SqlServer:ValueGenerationStrategy")?.Value?.ToString(), "IdentityColumn", StringComparison.Ordinal));

        try
        {
            using (var cmd = new SqlCommand($"SELECT TOP 0 * INTO {tempTableName} FROM {fullTableName}", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var bulkOptions = SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls;
            if (options.UseInternalTransaction && transaction == null)
            {
                bulkOptions |= SqlBulkCopyOptions.UseInternalTransaction;
            }

            using (var bulk = new SqlBulkCopy(conn, bulkOptions, transaction)
            {
                DestinationTableName = tempTableName,
                BatchSize = options.BatchSize,
                BulkCopyTimeout = options.TimeoutSeconds
            })
            {
                foreach (var p in properties)
                {
                    var col = p.GetColumnName(store)!;
                    bulk.ColumnMappings.Add(col, col);
                }
                await bulk.WriteToServerAsync(dataTable, cancellationToken);
            }

            var pkCols = pk.Properties.Select(p => p.GetColumnName(store)!).ToList();
            var insertCols = properties.Select(p => p.GetColumnName(store)!).ToList();
            var updateCols = properties
                .Where(p => !pk.Properties.Contains(p) && p.ValueGenerated != ValueGenerated.OnAdd && p.ValueGenerated != ValueGenerated.OnUpdate)
                .Select(p => p.GetColumnName(store)!)
                .ToList();

            var onClause = string.Join(" AND ", pkCols.Select(c => $"T.[{c}] = S.[{c}]"));
            var insertColsList = string.Join(", ", insertCols.Select(c => $"[{c}]"));
            var insertValsList = string.Join(", ", insertCols.Select(c => $"S.[{c}]"));
            var matchedClause = updateCols.Count > 0
                ? $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updateCols.Select(c => $"T.[{c}] = S.[{c}]"))}"
                : string.Empty;

            var sb = new StringBuilder();
            if (pkIsIdentity) sb.AppendLine($"SET IDENTITY_INSERT {fullTableName} ON;");
            sb.AppendLine($"MERGE {fullTableName} AS T USING {tempTableName} AS S ON {onClause}");
            if (matchedClause.Length > 0) sb.AppendLine(matchedClause);
            sb.AppendLine($"WHEN NOT MATCHED THEN INSERT ({insertColsList}) VALUES ({insertValsList});");
            if (pkIsIdentity) sb.AppendLine($"SET IDENTITY_INSERT {fullTableName} OFF;");

            using (var cmd = new SqlCommand(sb.ToString(), conn, transaction) { CommandTimeout = options.TimeoutSeconds })
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            try
            {
                using var cmd = new SqlCommand($"IF OBJECT_ID('tempdb..{tempTableName}') IS NOT NULL DROP TABLE {tempTableName};", conn, transaction);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch { }
        }
    }
}
