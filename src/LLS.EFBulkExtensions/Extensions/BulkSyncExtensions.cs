using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkSyncExtensions
{
    /// <summary>
    /// Sincroniza (espelha) a tabela com o conjunto informado: insere as chaves novas, atualiza
    /// as existentes e <b>remove</b> as linhas cuja PK não está na lista. Insert/update reaproveitam
    /// <see cref="BulkInsertOrUpdateExtensions.BulkInsertOrUpdateAsync{TEntity}"/> (inclusive
    /// <see cref="BulkInsertOrUpdateOptions.UpdateColumns"/> etc.); o delete usa o
    /// <c>ExecuteDelete</c> do EF (uma única instrução). Tudo numa transação: ou já existe uma
    /// ambiente, ou este método abre/commita a sua.
    /// </summary>
    /// <remarks>
    /// Apaga <b>todas</b> as linhas da tabela que não estiverem no conjunto. Uma coleção vazia
    /// apagaria a tabela inteira e por isso é rejeitada (use <c>BulkDelete</c> para limpar de
    /// propósito). v1 suporta PK de coluna única.
    /// </remarks>
    public static async Task BulkInsertOrUpdateOrDeleteAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        BulkInsertOrUpdateOptions? options = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        options ??= new BulkInsertOrUpdateOptions();

        var list = entities as IList<TEntity> ?? entities.ToList();

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var pk = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        if (pk.Properties.Count != 1)
            throw new NotSupportedException("BulkInsertOrUpdateOrDelete (v1) suporta apenas chave primária de coluna única.");

        if (list.Count == 0)
            throw new InvalidOperationException(
                "BulkInsertOrUpdateOrDelete com coleção vazia apagaria a tabela inteira. " +
                "Use BulkDelete explicitamente se a intenção é limpar a tabela.");

        var keyProp = pk.Properties[0];
        var keyClr = Nullable.GetUnderlyingType(keyProp.ClrType) ?? keyProp.ClrType;
        var pi = keyProp.PropertyInfo
            ?? throw new InvalidOperationException($"Propriedade de chave primária {keyProp.Name} não possui PropertyInfo associado.");

        // Chaves de origem (mantidas; o resto será apagado).
        var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(keyClr))!;
        foreach (var e in list)
        {
            var v = pi.GetValue(e);
            if (v != null) typedList.Add(v);
        }

        // Upsert + delete devem ser atômicos. Se já há transação ambiente, participamos dela.
        var ownTx = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            // Force UseInternalTransaction=false: a transação é gerida aqui (ou pelo chamador).
            var upsertOptions = new BulkInsertOrUpdateOptions
            {
                BatchSize = options.BatchSize,
                TimeoutSeconds = options.TimeoutSeconds,
                UseInternalTransaction = false,
                UpdateColumns = options.UpdateColumns,
                ExcludeUpdateColumns = options.ExcludeUpdateColumns,
                InsertIfNotExists = options.InsertIfNotExists,
            };
            await context.BulkInsertOrUpdateAsync(list, upsertOptions, cancellationToken).ConfigureAwait(false);

            var del = typeof(BulkSyncExtensions)
                .GetMethod(nameof(DeleteNotInAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TEntity), keyClr);
            var task = (Task)del.Invoke(null, new object[] { context, keyProp.Name, typedList, cancellationToken })!;
            await task.ConfigureAwait(false);

            if (ownTx != null) await ownTx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (ownTx != null) await ownTx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Task<int> DeleteNotInAsync<TEntity, TKey>(
        DbContext context, string keyName, List<TKey> keys, CancellationToken cancellationToken) where TEntity : class
        => context.Set<TEntity>()
            .Where(e => !keys.Contains(EF.Property<TKey>(e, keyName)))
            .ExecuteDeleteAsync(cancellationToken);
}
