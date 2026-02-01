using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Providers.SqlServer;
using Microsoft.EntityFrameworkCore.Metadata;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Core;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkInsertExtensions
{
    public static Task BulkInsertAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkInsertOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkInsertOptions();
        var entityType = context.Model.FindEntityType(typeof(TEntity));
        var pk = entityType?.FindPrimaryKey();
        var idProp = pk?.Properties.Count == 1 ? pk.Properties[0] : null;
        var returnIdsAnno = idProp?.FindAnnotation("Trae:ReturnGeneratedIds")?.Value as bool?;
        if (returnIdsAnno.HasValue)
        {
            options = new BulkInsertOptions
            {
                BatchSize = options.BatchSize,
                TimeoutSeconds = options.TimeoutSeconds,
                PreserveIdentity = options.PreserveIdentity,
                UseInternalTransaction = options.UseInternalTransaction,
                KeepNulls = options.KeepNulls,
                UseAppLock = options.UseAppLock,
                ReturnGeneratedIds = returnIdsAnno.Value
            };
        }

        // DbGenerated/None: no client-side pre-assignment path here

        var provider = context.Database.ProviderName;
        
        if (provider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            IBulkInserter inserter = new SqlServerBulkInserter();
            return inserter.BulkInsertAsync(context, entities, options, cancellationToken);
        }
        if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            IBulkInserter inserter = new LLS.EFBulkExtensions.Providers.Postgres.PostgresBulkInserter();
            return inserter.BulkInsertAsync(context, entities, options, cancellationToken);
        }

        throw new System.NotSupportedException($"Provedor de banco de dados não suportado para BulkInsert: {provider}");
    }
}
