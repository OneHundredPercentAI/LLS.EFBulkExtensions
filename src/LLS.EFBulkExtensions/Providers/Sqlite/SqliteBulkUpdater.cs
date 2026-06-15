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
using Microsoft.EntityFrameworkCore.Storage;

namespace LLS.EFBulkExtensions.Providers.Sqlite;

/// <summary>
/// Bulk update implementation for SQLite.
/// Uses BulkMapper to materialize rows and applies updates via a single transaction
/// and prepared UPDATE command executed per row.
/// </summary>
public sealed class SqliteBulkUpdater : IBulkUpdater
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

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = bulkConn.Connection;

        // Participa da transação ambiente do EF se houver; só abre transação própria quando não há
        // (SQLite não suporta transações aninhadas).
        var ambientTransaction = bulkConn.AmbientTransaction;
        await using var ownTransaction = ambientTransaction == null && options.UseInternalTransaction
            ? await conn.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = ambientTransaction ?? ownTransaction;

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
            var pkProps = pk.Properties;
            var pkCols = pkProps.Select(p => p.GetColumnName(store) ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para {p.Name}.")).ToList();
            var pkColSet = new HashSet<string>(pkCols, StringComparer.Ordinal);

            var colByName = columns.ToDictionary(c => c.ColumnName, StringComparer.Ordinal);
            var pkColumns = pkCols.Select(n => colByName[n]).ToList();
            var updatableColumns = columns
                .Where(c => !pkColSet.Contains(c.ColumnName) && c.Property.ValueGenerated != ValueGenerated.OnAdd && c.Property.ValueGenerated != ValueGenerated.OnUpdate)
                .ToList();

            if (updatableColumns.Count == 0)
            {
                return;
            }

            // Parâmetros: primeiro as colunas atualizáveis, depois as colunas de PK.
            var setFragments = updatableColumns.Select((c, i) => $"{Q(c.ColumnName)} = @p{i}").ToArray();
            var whereFragments = pkColumns.Select((c, i) => $"{Q(c.ColumnName)} = @p{updatableColumns.Count + i}").ToArray();

            var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {dest} SET {string.Join(", ", setFragments)} WHERE {string.Join(" AND ", whereFragments)};";
            cmd.CommandTimeout = options.TimeoutSeconds;
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            var totalParams = updatableColumns.Count + pkColumns.Count;
            for (int i = 0; i < totalParams; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                cmd.Parameters.Add(p);
            }

            foreach (var entity in list)
            {
                for (int i = 0; i < updatableColumns.Count; i++)
                {
                    cmd.Parameters[i].Value = updatableColumns[i].GetProviderValue(entity!) ?? DBNull.Value;
                }
                for (int i = 0; i < pkColumns.Count; i++)
                {
                    cmd.Parameters[updatableColumns.Count + i].Value = pkColumns[i].GetProviderValue(entity!) ?? DBNull.Value;
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

