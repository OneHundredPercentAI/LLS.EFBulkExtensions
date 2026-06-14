using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Providers;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkDeleteExtensions
{
    public static Task BulkDeleteAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkDeleteOptions();
        return BulkProviderRegistry.Resolve(context).Deleter.BulkDeleteAsync(context, entities, options, cancellationToken);
    }
}
