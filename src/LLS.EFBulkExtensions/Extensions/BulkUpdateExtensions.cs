using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Providers.Postgres;
using LLS.EFBulkExtensions.Providers.SqlServer;
using LLS.EFBulkExtensions.Providers.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkUpdateExtensions
{
    public static Task BulkUpdateAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkUpdateOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkUpdateOptions();

        var provider = context.Database.ProviderName;
        if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            IBulkUpdater updater = new SqlServerBulkUpdater();
            return updater.BulkUpdateAsync(context, entities, options, cancellationToken);
        }
        if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            IBulkUpdater updater = new PostgresBulkUpdater();
            return updater.BulkUpdateAsync(context, entities, options, cancellationToken);
        }
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            IBulkUpdater updater = new SqliteBulkUpdater();
            return updater.BulkUpdateAsync(context, entities, options, cancellationToken);
        }

        throw new System.NotSupportedException($"Provedor de banco de dados não suportado para BulkUpdate: {provider}");
    }
}
