using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Core.Internal;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MySqlConnector;

namespace LLS.EFBulkExtensions.Providers.MySql;

public sealed class MySqlBulkDeleter : IBulkDeleter
{
    public async Task BulkDeleteAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var (dataTable, _) = BulkMapper.Build(context, entities, includeIdentity: true);
        if (dataTable.Rows.Count == 0) return;

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (MySqlConnection)bulkConn.Connection;
        var transaction = (MySqlTransaction?)bulkConn.AmbientTransaction;

        string Q(string s) => "`" + s.Replace("`", "``") + "`";
        var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
        var tmp = "tmp_delete_" + Guid.NewGuid().ToString("N");

        try
        {
            await MySqlStaging.CreateAndFillAsync(conn, transaction, fullDest, tmp, dataTable, options.TimeoutSeconds, cancellationToken);

            var pkCols = pk.Properties.Select(p => p.GetColumnName(store)!).ToList();
            var join = string.Join(" AND ", pkCols.Select(c => $"T.{Q(c)} = S.{Q(c)}"));

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.TimeoutSeconds;
            cmd.CommandText = $"DELETE T FROM {fullDest} AS T JOIN {Q(tmp)} AS S ON {join};";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            using var drop = conn.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TEMPORARY TABLE IF EXISTS {Q(tmp)};";
            try { await drop.ExecuteNonQueryAsync(cancellationToken); } catch { }
        }
    }
}
