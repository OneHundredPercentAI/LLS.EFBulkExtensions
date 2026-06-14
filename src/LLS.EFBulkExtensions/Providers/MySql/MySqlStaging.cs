using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Core.Internal;
using MySqlConnector;

namespace LLS.EFBulkExtensions.Providers.MySql;

/// <summary>
/// Helper de staging para MySQL: cria uma tabela temporária com a mesma estrutura do destino
/// e carrega as entidades nela via MySqlBulkCopy, em streaming (sem DataTable).
/// Usado por update/delete/upsert.
/// </summary>
internal static class MySqlStaging
{
    public static async Task CreateAndFillAsync<TEntity>(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        string fullDest,
        string tmpName,
        IReadOnlyList<BulkMapper.BulkColumn> columns,
        IList<TEntity> rows,
        int timeoutSeconds,
        CancellationToken cancellationToken) where TEntity : class
    {
        string Q(string s) => "`" + s.Replace("`", "``") + "`";

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"CREATE TEMPORARY TABLE {Q(tmpName)} LIKE {fullDest};";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var bulk = new MySqlBulkCopy(conn, transaction)
        {
            DestinationTableName = Q(tmpName),
            BulkCopyTimeout = timeoutSeconds
        };
        for (int i = 0; i < columns.Count; i++)
        {
            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, columns[i].ColumnName));
        }

        using var reader = new EntityDataReader<TEntity>(rows, columns);
        var result = await bulk.WriteToServerAsync(reader, cancellationToken);
        if (result.RowsInserted != rows.Count)
        {
            var warnings = string.Join("; ", result.Warnings.Select(w => w.Message));
            throw new System.InvalidOperationException($"MySqlBulkCopy (staging) carregou {result.RowsInserted} de {rows.Count} linhas. Avisos: {warnings}");
        }
    }
}
