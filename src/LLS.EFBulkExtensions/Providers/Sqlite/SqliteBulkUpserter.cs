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

namespace LLS.EFBulkExtensions.Providers.Sqlite;

/// <summary>
/// Bulk insert-or-update para SQLite via INSERT ... ON CONFLICT(pk) DO UPDATE,
/// executado por linha dentro de uma transação. Correlaciona pela chave primária.
/// </summary>
public sealed class SqliteBulkUpserter : IBulkUpserter
{
    public async Task BulkInsertOrUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        if (list.Count == 0) return;

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        var matchProps = MatchKeyResolver.Resolve(entityType, options.MatchProperties);
        var useNaturalKey = options.MatchProperties is { Count: > 0 };

        var (columns, _, _) = BulkMapper.BuildColumns(context, list, includeIdentity: !useNaturalKey);
        if (columns.Count == 0) return;

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = bulkConn.Connection;

        var ambientTransaction = bulkConn.AmbientTransaction;
        await using var ownTransaction = ambientTransaction == null && options.UseInternalTransaction
            ? await conn.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = ambientTransaction ?? ownTransaction;

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            var matchCols = matchProps.Select(p => p.GetColumnName(store)!).ToList();
            var matchColSet = new HashSet<string>(matchCols, StringComparer.Ordinal);
            var updatableCols = columns
                .Where(col => !matchColSet.Contains(col.ColumnName)
                              && !pk.Properties.Contains(col.Property)
                              && col.Property.ValueGenerated != ValueGenerated.OnAdd
                              && col.Property.ValueGenerated != ValueGenerated.OnUpdate)
                .ToList();
            var allowedUpdate = UpsertColumnResolver.ResolveUpdateColumns(
                updatableCols.Select(c => (c.Property.Name, c.ColumnName)).ToList(),
                columns.Select(c => (c.Property.Name, c.ColumnName)).ToList(),
                options);
            var updateCols = updatableCols
                .Select(col => col.ColumnName)
                .Where(allowedUpdate.Contains)
                .ToList();

            var columnList = string.Join(", ", columns.Select(c => Q(c.ColumnName)));
            var paramNames = Enumerable.Range(0, columns.Count).Select(i => "@p" + i).ToArray();
            var valuesList = string.Join(", ", paramNames);
            var conflictTarget = string.Join(", ", matchCols.Select(Q));
            var action = updateCols.Count > 0
                ? $"DO UPDATE SET {string.Join(", ", updateCols.Select(c => $"{Q(c)} = excluded.{Q(c)}"))}"
                : "DO NOTHING";

            var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {dest} ({columnList}) VALUES ({valuesList}) ON CONFLICT ({conflictTarget}) {action};";
            cmd.CommandTimeout = options.TimeoutSeconds;
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            for (int i = 0; i < columns.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = paramNames[i];
                cmd.Parameters.Add(p);
            }

            foreach (var entity in list)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    cmd.Parameters[i].Value = columns[i].GetProviderValue(entity!) ?? DBNull.Value;
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (ownTransaction != null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownTransaction != null)
            {
                await ownTransaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }
}
