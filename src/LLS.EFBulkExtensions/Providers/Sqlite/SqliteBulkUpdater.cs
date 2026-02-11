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
/// Bulk update implementation for SQLite.
/// Uses DataTableBuilder to materialize rows and applies updates via a single transaction
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

            var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");

            var pkProps = pk.Properties.ToList();
            var pkCols = pkProps.Select(p => p.GetColumnName(store) ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para {p.Name}.")).ToList();

            var updatableProps = new List<IProperty>();
            var updatableCols = new List<string>();
            foreach (var p in properties)
            {
                var colName = p.GetColumnName(store);
                if (colName == null) continue;
                if (pkProps.Contains(p)) continue;
                if (p.ValueGenerated == ValueGenerated.OnAdd || p.ValueGenerated == ValueGenerated.OnUpdate) continue;

                updatableProps.Add(p);
                updatableCols.Add(colName);
            }

            if (updatableCols.Count == 0)
            {
                return;
            }

            // Parameters: first all updatable columns, then PK columns
            var setFragments = updatableCols.Select((c, i) => $"{Q(c)} = @p{i}").ToArray();
            var whereFragments = pkCols.Select((c, i) => $"{Q(c)} = @p{updatableCols.Count + i}").ToArray();

            var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {dest} SET {string.Join(", ", setFragments)} WHERE {string.Join(" AND ", whereFragments)};";
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            var totalParams = updatableCols.Count + pkCols.Count;
            for (int i = 0; i < totalParams; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                cmd.Parameters.Add(p);
            }

            // Map data rows back to PK values from original entities when necessary
            var pkClrTypes = pkProps.Select(p => Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).ToArray();

            foreach (DataRow row in dataTable.Rows)
            {
                // Updatable values
                for (int i = 0; i < updatableCols.Count; i++)
                {
                    var v = row[updatableCols[i]];
                    cmd.Parameters[i].Value = v ?? DBNull.Value;
                }

                // PK values
                for (int i = 0; i < pkCols.Count; i++)
                {
                    var v = row[pkCols[i]];
                    cmd.Parameters[updatableCols.Count + i].Value = v ?? DBNull.Value;
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

