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

namespace LLS.EFBulkExtensions.Providers.Sqlite;

/// <summary>
/// Bulk delete implementation for SQLite.
/// Uses DataTableBuilder to materialize key values and applies deletes via
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

        var (dataTable, properties) = DataTableBuilder.Build(context, entities, includeIdentity: true);
        if (dataTable.Rows.Count == 0) return;

        var conn = context.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
        {
            await conn.OpenAsync(cancellationToken);
        }

        using var transaction = options.UseInternalTransaction ? await conn.BeginTransactionAsync(cancellationToken) : null;

        try
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            var pkProps = pk.Properties.ToList();
            var pkCols = pkProps.Select(p => p.GetColumnName(store) ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para {p.Name}.")).ToList();

            var whereFragments = pkCols.Select((c, i) => $"{Q(c)} = @p{i}").ToArray();

            var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {dest} WHERE {string.Join(" AND ", whereFragments)};";
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            for (int i = 0; i < pkCols.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                cmd.Parameters.Add(p);
            }

            foreach (DataRow row in dataTable.Rows)
            {
                for (int i = 0; i < pkCols.Count; i++)
                {
                    var v = row[pkCols[i]];
                    cmd.Parameters[i].Value = v ?? DBNull.Value;
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (shouldClose)
            {
                await conn.CloseAsync();
            }
        }
    }
}

