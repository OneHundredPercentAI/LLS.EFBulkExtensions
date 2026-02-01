using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Core;

public interface IBulkDeleter
{
    Task BulkDeleteAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkDeleteOptions options, CancellationToken cancellationToken = default) where TEntity : class;
}
