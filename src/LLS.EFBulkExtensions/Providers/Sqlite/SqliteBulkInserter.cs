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
/// Bulk insert implementation for SQLite.
/// Since SQLite does not expose a dedicated bulk API, this implementation:
/// - Builds a DataTable with DataTableBuilder
/// - Uses a single transaction (when requested) and a prepared INSERT command
/// - Executes the command once per row
/// This still yields significant gains compared to issuing separate inserts without a transaction.
/// </summary>
public sealed class SqliteBulkInserter : IBulkInserter
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
        if (list.Count == 0)
        {
            return;
        }

        var includeIdentity = options.PreserveIdentity;
        var (dataTable, _) = DataTableBuilder.Build(context, list, includeIdentity: includeIdentity);

        var conn = context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            shouldClose = true;
        }

        using var transaction = options.UseInternalTransaction ? await conn.BeginTransactionAsync(cancellationToken) : null;

        try
        {
            // Column list from DataTable (already respecting identity configuration)
            var columns = dataTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
            if (columns.Count == 0)
            {
                return;
            }

            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);

            // Support returning IDs only for single-column numeric PKs
            var store = StoreObjectIdentifier.Table(tableName, schema);
            var pk = entityType.FindPrimaryKey();
            var idProp = pk?.Properties.Count == 1 ? pk.Properties[0] : null;
            var supportsReturnIds = options.ReturnGeneratedIds && idProp != null;

            if (options.ReturnGeneratedIds && !supportsReturnIds)
            {
                throw new NotSupportedException("ReturnGeneratedIds em SQLite requer uma chave primária simples configurada na entidade.");
            }

            if (supportsReturnIds && options.PreserveIdentity)
            {
                throw new InvalidOperationException("ReturnGeneratedIds não é compatível com PreserveIdentity em SQLite.");
            }

            string? idCol = null;
            Type? idClrType = null;

            if (supportsReturnIds)
            {
                idCol = idProp!.GetColumnName(store)
                    ?? throw new InvalidOperationException($"Coluna de chave primária não encontrada para a entidade {entityType.DisplayName()}.");

                idClrType = idProp.ClrType;
                var underlying = Nullable.GetUnderlyingType(idClrType) ?? idClrType;

                // Em SQLite, IDs autogerados típicos são inteiros; suportamos apenas conversões numéricas aqui.
                if (!(underlying == typeof(long) ||
                      underlying == typeof(int) ||
                      underlying == typeof(short) ||
                      underlying == typeof(byte) ||
                      underlying == typeof(ulong) ||
                      underlying == typeof(uint) ||
                      underlying == typeof(ushort)))
                {
                    throw new NotSupportedException($"Tipo de ID não suportado para retorno de IDs em SQLite: {idClrType.FullName}");
                }
            }

            var columnList = string.Join(", ", columns.Select(Q));
            var paramNames = Enumerable.Range(0, columns.Count).Select(i => "@p" + i).ToArray();
            var valuesList = string.Join(", ", paramNames);

            var cmd = conn.CreateCommand();
            cmd.CommandText = supportsReturnIds
                ? $"INSERT INTO {dest} ({columnList}) VALUES ({valuesList}) RETURNING {Q(idCol!)};"
                : $"INSERT INTO {dest} ({columnList}) VALUES ({valuesList});";

            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            // Create parameters once and reuse
            for (int i = 0; i < columns.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = paramNames[i];
                cmd.Parameters.Add(p);
            }

            if (supportsReturnIds)
            {
                var propInfo = idProp!.PropertyInfo
                    ?? throw new InvalidOperationException($"Propriedade de chave primária {idProp.Name} não possui PropertyInfo associado.");
                var targetType = idClrType!;

                for (int i = 0; i < dataTable.Rows.Count; i++)
                {
                    var row = dataTable.Rows[i];
                    for (int cIndex = 0; cIndex < columns.Count; cIndex++)
                    {
                        var value = row[columns[cIndex]];
                        cmd.Parameters[cIndex].Value = value ?? DBNull.Value;
                    }

                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var idLong = reader.GetInt64(0);
                        var idVal = IdConversionHelper.FromInt64(idLong, targetType);
                        propInfo.SetValue(list[i], idVal);
                    }
                }
            }
            else
            {
                for (int i = 0; i < dataTable.Rows.Count; i++)
                {
                    var row = dataTable.Rows[i];
                    for (int cIndex = 0; cIndex < columns.Count; cIndex++)
                    {
                        var value = row[columns[cIndex]];
                        cmd.Parameters[cIndex].Value = value ?? DBNull.Value;
                    }

                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
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

