using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Providers;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkInsertExtensions
{
    public static Task BulkInsertAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkInsertOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkInsertOptions();

        // ReturnGeneratedIds pode ser habilitado a nível de modelo (anotação na PK).
        var entityType = context.Model.FindEntityType(typeof(TEntity));
        var pk = entityType?.FindPrimaryKey();
        var idProp = pk?.Properties.Count == 1 ? pk.Properties[0] : null;
        var returnIdsAnno = idProp?.FindAnnotation(ValueGenerationExtensions.ReturnGeneratedIdsAnnotation)?.Value as bool?;
        if (returnIdsAnno.HasValue)
        {
            options = new BulkInsertOptions
            {
                BatchSize = options.BatchSize,
                TimeoutSeconds = options.TimeoutSeconds,
                PreserveIdentity = options.PreserveIdentity,
                UseInternalTransaction = options.UseInternalTransaction,
                KeepNulls = options.KeepNulls,
                ReturnGeneratedIds = returnIdsAnno.Value
            };
        }

        return BulkProviderRegistry.Resolve(context).Inserter.BulkInsertAsync(context, entities, options, cancellationToken);
    }
}
