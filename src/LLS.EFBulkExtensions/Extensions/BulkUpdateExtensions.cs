using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Providers;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkUpdateExtensions
{
    public static Task BulkUpdateAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkUpdateOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkUpdateOptions();
        return BulkProviderRegistry.Resolve(context).Updater.BulkUpdateAsync(context, entities, options, cancellationToken);
    }
}
