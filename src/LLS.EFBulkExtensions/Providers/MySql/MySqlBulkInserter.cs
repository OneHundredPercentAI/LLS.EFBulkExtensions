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
using MySqlConnector;

namespace LLS.EFBulkExtensions.Providers.MySql;

/// <summary>
/// Bulk insert para MySQL/MariaDB via MySqlBulkCopy (LOAD DATA LOCAL INFILE), alimentado
/// pelo EntityDataReader em streaming. Requer AllowLoadLocalInfile=true na conexão e
/// local_infile habilitado no servidor.
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
            throw new NotSupportedException("ReturnGeneratedIds ainda não é suportado no provider MySQL.");

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
}
