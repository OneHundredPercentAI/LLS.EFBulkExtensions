using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MySqlConnector;

namespace LLS.EFBulkExtensions.Providers.MySql;

/// <summary>
/// Bulk insert para MySQL/MariaDB. Caminho rápido via MySqlBulkCopy (LOAD DATA LOCAL INFILE)
/// alimentado pelo EntityDataReader; caminho com retorno de IDs via INSERT multi-linha +
/// LAST_INSERT_ID(). Requer AllowLoadLocalInfile=true e local_infile no servidor (caminho rápido).
/// </summary>
public sealed class MySqlBulkInserter : IBulkInserter
{
    public async Task BulkInsertAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();

        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        if (list.Count == 0) return;

        if (options.ReturnGeneratedIds)
        {
            await InsertReturningIdsAsync(context, entityType, tableName, schema, list, options, cancellationToken);
            return;
        }

        var columns = BulkMapper.BuildColumns(context, list, includeIdentity: options.PreserveIdentity).Columns;

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (MySqlConnection)bulkConn.Connection;
        var transaction = (MySqlTransaction?)bulkConn.AmbientTransaction;

        var dest = schema is null ? $"`{tableName}`" : $"`{schema}`.`{tableName}`";

        var bulk = new MySqlBulkCopy(conn, transaction)
        {
            DestinationTableName = dest,
            BulkCopyTimeout = options.TimeoutSeconds
        };
        for (int i = 0; i < columns.Count; i++)
        {
            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, columns[i].ColumnName));
        }

        using var reader = new EntityDataReader<TEntity>(list, columns);
        var result = await bulk.WriteToServerAsync(reader, cancellationToken);

        if (result.RowsInserted != list.Count)
        {
            var warnings = string.Join("; ", result.Warnings.Select(w => w.Message));
            throw new InvalidOperationException($"MySqlBulkCopy inseriu {result.RowsInserted} de {list.Count} linhas. Avisos: {warnings}");
        }
    }

    private static async Task InsertReturningIdsAsync<TEntity>(
        DbContext context, IEntityType entityType, string tableName, string? schema,
        IList<TEntity> list, BulkInsertOptions options, CancellationToken cancellationToken) where TEntity : class
    {
        var store = StoreObjectIdentifier.Table(tableName, schema);
        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        var idProp = pk.Properties.Count == 1
            ? pk.Properties[0]
            : throw new NotSupportedException("ReturnGeneratedIds no MySQL requer uma chave primária simples.");

        var idClrType = idProp.ClrType;
        var idUnderlying = Nullable.GetUnderlyingType(idClrType) ?? idClrType;
        if (!(idUnderlying == typeof(long) || idUnderlying == typeof(int) || idUnderlying == typeof(short) ||
              idUnderlying == typeof(byte) || idUnderlying == typeof(ulong) || idUnderlying == typeof(uint) ||
              idUnderlying == typeof(ushort)))
        {
            throw new NotSupportedException($"Tipo de ID não suportado para ReturnGeneratedIds no MySQL: {idClrType.FullName}");
        }
        var propInfo = idProp.PropertyInfo ?? throw new InvalidOperationException($"Propriedade de chave primária {idProp.Name} não possui PropertyInfo associado.");

        // includeIdentity:false => coluna auto_increment fica fora do INSERT (gerada pelo banco).
        var columns = BulkMapper.BuildColumns(context, list, includeIdentity: false).Columns;
        string Q(string s) => "`" + s.Replace("`", "``") + "`";
        var dest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
        var colList = string.Join(", ", columns.Select(c => Q(c.ColumnName)));

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (MySqlConnection)bulkConn.Connection;
        var ambient = (MySqlTransaction?)bulkConn.AmbientTransaction;
        await using var ownTransaction = ambient == null && options.UseInternalTransaction
            ? await conn.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = ambient ?? ownTransaction;

        try
        {
            var colCount = Math.Max(1, columns.Count);
            // Mantém o nº de placeholders por statement abaixo do limite do protocolo (~65535).
            var maxRows = Math.Max(1, Math.Min(options.BatchSize, 60000 / colCount));

            var offset = 0;
            while (offset < list.Count)
            {
                var n = Math.Min(maxRows, list.Count - offset);

                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandTimeout = options.TimeoutSeconds;

                var sb = new StringBuilder();
                sb.Append("INSERT INTO ").Append(dest).Append(" (").Append(colList).Append(") VALUES ");
                for (int r = 0; r < n; r++)
                {
                    if (r > 0) sb.Append(',');
                    sb.Append('(');
                    for (int col = 0; col < columns.Count; col++)
                    {
                        if (col > 0) sb.Append(',');
                        var name = $"@p{r}_{col}";
                        sb.Append(name);
                        cmd.Parameters.AddWithValue(name, columns[col].GetProviderValue(list[offset + r]!) ?? DBNull.Value);
                    }
                    sb.Append(')');
                }
                sb.Append(';');
                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                using var idCmd = conn.CreateCommand();
                idCmd.Transaction = transaction;
                idCmd.CommandText = "SELECT LAST_INSERT_ID();";
                var firstId = Convert.ToInt64(await idCmd.ExecuteScalarAsync(cancellationToken));

                // MySQL aloca IDs contíguos para INSERT VALUES com nº de linhas conhecido (assume increment = 1).
                for (int k = 0; k < n; k++)
                {
                    propInfo.SetValue(list[offset + k], IdConversionHelper.FromInt64(firstId + k, idClrType));
                }

                offset += n;
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
