using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LLS.EFBulkExtensions.Core.Internal;

/// <summary>
/// Gerencia a conexão de forma uniforme entre os providers:
/// abre a conexão se necessário, expõe a transação ambiente do EF (se houver) e,
/// ao ser descartada, fecha a conexão apenas se foi este escopo que a abriu.
/// Centraliza a parte de conexão/transação que já foi fonte de bugs.
/// </summary>
internal sealed class BulkConnection : IAsyncDisposable
{
    public DbConnection Connection { get; }

    /// <summary>Transação ambiente do EF, se uma estiver aberta no contexto; caso contrário, null.</summary>
    public DbTransaction? AmbientTransaction { get; }

    private readonly bool _openedByUs;

    private BulkConnection(DbConnection connection, DbTransaction? ambientTransaction, bool openedByUs)
    {
        Connection = connection;
        AmbientTransaction = ambientTransaction;
        _openedByUs = openedByUs;
    }

    public static async Task<BulkConnection> OpenAsync(DbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedByUs = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedByUs = true;
        }

        var ambient = context.Database.CurrentTransaction?.GetDbTransaction();
        return new BulkConnection(connection, ambient, openedByUs);
    }

    public async ValueTask DisposeAsync()
    {
        if (_openedByUs)
        {
            await Connection.CloseAsync();
        }
    }
}
