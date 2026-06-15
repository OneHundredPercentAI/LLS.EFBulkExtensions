using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LLS.EFBulkExtensions.Options;
using Microsoft.EntityFrameworkCore;

namespace LLS.EFBulkExtensions.Extensions;

public static class BulkReadExtensions
{
    /// <summary>
    /// Lê em massa as linhas correspondentes às chaves primárias das entidades informadas,
    /// sem montar um <c>WHERE pk IN (...)</c> gigante: as chaves são consultadas em lotes via
    /// <c>Contains</c>, que o EF traduz da melhor forma para cada provedor
    /// (PostgreSQL <c>= ANY(@array)</c>, SQL Server <c>OPENJSON</c>, SQLite/MySQL <c>IN</c>).
    /// Apenas os valores de PK das entidades de entrada são usados. As entidades retornadas
    /// vêm <b>desanexadas</b> (AsNoTracking) e a ordem não é garantida. Chaves sem correspondência
    /// simplesmente não aparecem no resultado.
    /// </summary>
    public static async Task<List<TEntity>> BulkReadAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> keys,
        BulkReadOptions? options = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keys);
        options ??= new BulkReadOptions();

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var pk = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        if (pk.Properties.Count != 1)
            throw new NotSupportedException("BulkRead (v1) suporta apenas chave primária de coluna única.");

        var keyProp = pk.Properties[0];
        var keyClr = Nullable.GetUnderlyingType(keyProp.ClrType) ?? keyProp.ClrType;
        var pi = keyProp.PropertyInfo
            ?? throw new InvalidOperationException($"Propriedade de chave primária {keyProp.Name} não possui PropertyInfo associado.");

        // Coleta valores de chave distintos, já no tipo CLR da chave (List<TKey>).
        var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(keyClr))!;
        var seen = new HashSet<object>();
        foreach (var k in keys)
        {
            if (k == null) continue;
            var v = pi.GetValue(k);
            if (v == null) continue;
            if (seen.Add(v)) typedList.Add(v);
        }

        if (typedList.Count == 0) return new List<TEntity>();

        var chunkSize = options.BatchSize > 0 ? options.BatchSize : 20_000;

        var core = typeof(BulkReadExtensions)
            .GetMethod(nameof(ReadCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TEntity), keyClr);

        var task = (Task)core.Invoke(null, new object[] { context, keyProp.Name, typedList, chunkSize, cancellationToken })!;
        await task.ConfigureAwait(false);
        return (List<TEntity>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task<List<TEntity>> ReadCoreAsync<TEntity, TKey>(
        DbContext context,
        string keyName,
        List<TKey> keyValues,
        int chunkSize,
        CancellationToken cancellationToken) where TEntity : class
    {
        var set = context.Set<TEntity>().AsNoTracking();
        var result = new List<TEntity>(keyValues.Count);

        for (int i = 0; i < keyValues.Count; i += chunkSize)
        {
            var chunk = keyValues.GetRange(i, Math.Min(chunkSize, keyValues.Count - i));
            var rows = await set
                .Where(e => chunk.Contains(EF.Property<TKey>(e, keyName)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(rows);
        }

        return result;
    }
}
