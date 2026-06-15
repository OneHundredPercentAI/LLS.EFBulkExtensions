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
/// Bulk delete implementation for SQLite.
/// Uses BulkMapper to materialize key values and applies deletes via
/// a single transaction and prepared DELETE command executed per row.
/// </summary>
public sealed class SqliteBulkDeleter : IBulkDeleter
{
    public async Task BulkDeleteAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");

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

            var pkProps = pk.Properties;
            var pkCols = pkProps.Select(p => p.GetColumnName(store) ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para {p.Name}.")).ToList();
            var colByName = columns.ToDictionary(c => c.ColumnName, StringComparer.Ordinal);
            var pkColumns = pkCols.Select(n => colByName[n]).ToList();

            var whereFragments = pkCols.Select((c, i) => $"{Q(c)} = @p{i}").ToArray();

            var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {dest} WHERE {string.Join(" AND ", whereFragments)};";
            cmd.CommandTimeout = options.TimeoutSeconds;
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            for (int i = 0; i < pkColumns.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                cmd.Parameters.Add(p);
            }

            foreach (var entity in list)
            {
                for (int i = 0; i < pkColumns.Count; i++)
                {
                    cmd.Parameters[i].Value = pkColumns[i].GetProviderValue(entity!) ?? DBNull.Value;
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

