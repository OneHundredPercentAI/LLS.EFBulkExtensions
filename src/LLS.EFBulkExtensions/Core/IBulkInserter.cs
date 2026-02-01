using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Options;

namespace LLS.EFBulkExtensions.Core;

public interface IBulkInserter
{
    Task BulkInsertAsync<TEntity>(DbContext context, IEnumerable<TEntity> entities, BulkInsertOptions options, CancellationToken cancellationToken = default) where TEntity : class;
}
