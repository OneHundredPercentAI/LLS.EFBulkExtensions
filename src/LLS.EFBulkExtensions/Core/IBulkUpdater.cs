using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Core;

public interface IBulkUpdater
{
    Task BulkUpdateAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkUpdateOptions options, CancellationToken cancellationToken = default) where TEntity : class;
}
