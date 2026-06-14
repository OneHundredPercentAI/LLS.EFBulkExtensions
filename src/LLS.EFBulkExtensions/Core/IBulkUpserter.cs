using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Options;

namespace LLS.EFBulkExtensions.Core;

public interface IBulkUpserter
{
    Task BulkInsertOrUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class;
}
