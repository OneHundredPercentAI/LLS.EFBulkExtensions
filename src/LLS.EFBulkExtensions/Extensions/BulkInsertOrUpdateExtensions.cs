using Microsoft.EntityFrameworkCore;
using LLS.EFBulkExtensions.Options;
using LLS.EFBulkExtensions.Providers;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkInsertOrUpdateExtensions
{
    /// <summary>
    /// Insere ou atualiza em massa, correlacionando pela chave primária:
    /// linhas cuja PK já existe são atualizadas; as demais são inseridas.
    /// Os valores de PK devem estar preenchidos nas entidades.
    /// </summary>
    public static Task BulkInsertOrUpdateAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkInsertOrUpdateOptions();
        return BulkProviderRegistry.Resolve(context).Upserter.BulkInsertOrUpdateAsync(context, entities, options, cancellationToken);
    }

    /// <summary>
    /// Insere em massa apenas as linhas cuja chave primária ainda não existe; linhas já
    /// existentes são ignoradas (nenhum UPDATE). Útil para importações idempotentes.
    /// Mapeia para <c>ON CONFLICT DO NOTHING</c> / <c>INSERT IGNORE</c> /
    /// <c>MERGE ... WHEN NOT MATCHED</c> conforme o provedor.
    /// </summary>
    public static Task BulkInsertIfNotExistsAsync<TEntity>(this DbContext context, IEnumerable<TEntity> entities, BulkInsertOrUpdateOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        options ??= new BulkInsertOrUpdateOptions();
        var effective = options.InsertIfNotExists
            ? options
            : new BulkInsertOrUpdateOptions
            {
                BatchSize = options.BatchSize,
                TimeoutSeconds = options.TimeoutSeconds,
                UseInternalTransaction = options.UseInternalTransaction,
                InsertIfNotExists = true,
            };
        return BulkProviderRegistry.Resolve(context).Upserter.BulkInsertOrUpdateAsync(context, entities, effective, cancellationToken);
    }
}
