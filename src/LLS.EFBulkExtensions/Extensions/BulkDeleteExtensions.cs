using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Providers.SqlServer;
using LLS.EFBulkExtensions.Providers.Postgres;
using LLS.EFBulkExtensions.Core;
using LLS.EFBulkExtensions.Options;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkDeleteExtensions
{
    public static Task BulkDeleteAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkDeleteOptions();

        var provider = context.Database.ProviderName;
        if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            IBulkDeleter deleter = new SqlServerBulkDeleter();
            return deleter.BulkDeleteAsync(context, entities, options, cancellationToken);
        }
        if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            IBulkDeleter deleter = new PostgresBulkDeleter();
            return deleter.BulkDeleteAsync(context, entities, options, cancellationToken);
        }

        throw new System.NotSupportedException($"Provedor de banco de dados não suportado para BulkDelete: {provider}");
    }
}
