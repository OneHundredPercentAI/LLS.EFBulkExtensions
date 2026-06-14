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
}
