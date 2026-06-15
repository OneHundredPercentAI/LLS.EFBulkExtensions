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

public sealed class MySqlBulkUpserter : IBulkUpserter
{
    public async Task BulkInsertOrUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado.");
        var schema = entityType.GetSchema();

        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
        if (list.Count == 0) return;

        var pk = entityType.FindPrimaryKey() ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        var store = StoreObjectIdentifier.Table(tableName, schema);
        var matchProps = MatchKeyResolver.Resolve(entityType, options.MatchProperties);
        var useNaturalKey = options.MatchProperties is { Count: > 0 };
        var (columns, _, _) = BulkMapper.BuildColumns(context, list, includeIdentity: !useNaturalKey);

        await using var bulkConn = await BulkConnection.OpenAsync(context, cancellationToken);
        var conn = (MySqlConnection)bulkConn.Connection;
        var transaction = (MySqlTransaction?)bulkConn.AmbientTransaction;

        string Q(string s) => "`" + s.Replace("`", "``") + "`";
        var fullDest = schema is null ? Q(tableName) : Q(schema) + "." + Q(tableName);
        var tmp = "tmp_upsert_" + Guid.NewGuid().ToString("N");

        try
        {
            await MySqlStaging.CreateAndFillAsync(conn, transaction, fullDest, tmp, columns, list, options.TimeoutSeconds, cancellationToken);

            var insertCols = columns.Select(c => c.ColumnName).ToList();
            var matchColSet = new HashSet<string>(matchProps.Select(p => p.GetColumnName(store)!), StringComparer.Ordinal);
            var updatableCols = columns
                .Where(c => !matchColSet.Contains(c.ColumnName) && !pk.Properties.Contains(c.Property)
                            && c.Property.ValueGenerated != ValueGenerated.OnAdd && c.Property.ValueGenerated != ValueGenerated.OnUpdate)
                .ToList();
            var allowedUpdate = UpsertColumnResolver.ResolveUpdateColumns(
                updatableCols.Select(c => (c.Property.Name, c.ColumnName)).ToList(),
                columns.Select(c => (c.Property.Name, c.ColumnName)).ToList(),
                options);
            var updateCols = updatableCols
                .Select(c => c.ColumnName)
                .Where(allowedUpdate.Contains)
                .ToList();

            var colsList = string.Join(", ", insertCols.Select(Q));
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = options.TimeoutSeconds;

            if (updateCols.Count > 0)
            {
                var setList = string.Join(", ", updateCols.Select(c => $"{Q(c)} = VALUES({Q(c)})"));
                cmd.CommandText = $"INSERT INTO {fullDest} ({colsList}) SELECT {colsList} FROM {Q(tmp)} ON DUPLICATE KEY UPDATE {setList};";
            }
            else
            {
                // Sem colunas atualizáveis: ignora conflitos de chave (apenas insere os novos).
                cmd.CommandText = $"INSERT IGNORE INTO {fullDest} ({colsList}) SELECT {colsList} FROM {Q(tmp)};";
            }

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
