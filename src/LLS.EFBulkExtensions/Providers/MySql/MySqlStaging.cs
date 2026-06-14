using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LLS.EFBulkExtensions.Providers.MySql;

/// <summary>
/// Helper de staging para MySQL: cria uma tabela temporária com a mesma estrutura do destino
/// e carrega o DataTable nela via MySqlBulkCopy (mapeando por nome de coluna).
/// Usado por update/delete/upsert.
/// </summary>
internal static class MySqlStaging
{
    public static async Task CreateAndFillAsync(
        MySqlConnection conn,
        MySqlTransaction? transaction,
        string fullDest,
        string tmpName,
        DataTable dataTable,
        int timeoutSeconds,
        CancellationToken cancellationToken)
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
        for (int i = 0; i < dataTable.Columns.Count; i++)
        {
            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, dataTable.Columns[i].ColumnName));
        }

        var result = await bulk.WriteToServerAsync(dataTable, cancellationToken);
        if (result.RowsInserted != dataTable.Rows.Count)
        {
            var warnings = string.Join("; ", System.Linq.Enumerable.Select(result.Warnings, w => w.Message));
            throw new System.InvalidOperationException($"MySqlBulkCopy (staging) carregou {result.RowsInserted} de {dataTable.Rows.Count} linhas. Avisos: {warnings}");
        }
    }
}
