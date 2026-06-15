using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LLS.EFBulkExtensions.Core.Internal;

/// <summary>
/// Resolve as colunas de correlação de um upsert: a PK por padrão, ou a chave natural informada
/// em <c>MatchProperties</c>. No caso natural, exige que as propriedades formem uma chave ÚNICA
/// (PK, alternate key ou índice único), pois o upsert usa o mecanismo nativo do banco
/// (<c>ON CONFLICT</c> / <c>ON DUPLICATE KEY</c> / <c>MERGE</c>).
/// </summary>
internal static class MatchKeyResolver
{
    /// <returns>As propriedades que formam a chave de correlação (PK ou chave natural).</returns>
    public static IReadOnlyList<IProperty> Resolve(IEntityType entityType, IReadOnlyList<string>? matchProperties)
    {
        if (matchProperties == null || matchProperties.Count == 0)
        {
            return entityType.FindPrimaryKey()?.Properties
                ?? throw new InvalidOperationException("Entidade não tem chave primária definida.");
        }

        var props = new List<IProperty>(matchProperties.Count);
        foreach (var name in matchProperties)
        {
            var p = entityType.FindProperty(name)
                ?? throw new InvalidOperationException(
                    $"MatchProperties: propriedade '{name}' não encontrada na entidade {entityType.DisplayName()}.");
            props.Add(p);
        }

        var set = new HashSet<IProperty>(props);
        var isUnique =
            (entityType.FindPrimaryKey() is { } pk && set.SetEquals(pk.Properties)) ||
            entityType.GetKeys().Any(k => set.SetEquals(k.Properties)) ||
            entityType.GetIndexes().Any(ix => ix.IsUnique && set.SetEquals(ix.Properties));

        if (!isUnique)
            throw new InvalidOperationException(
                $"MatchProperties exige um índice/constraint ÚNICO cobrindo exatamente " +
                $"[{string.Join(", ", matchProperties)}] na entidade {entityType.DisplayName()}. " +
                "O upsert por chave natural usa o mecanismo nativo (ON CONFLICT / ON DUPLICATE KEY / MERGE).");

        return props;
    }
}
