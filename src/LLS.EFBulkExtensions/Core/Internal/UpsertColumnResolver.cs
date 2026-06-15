using System;
using System.Collections.Generic;
using System.Linq;
using LLS.EFBulkExtensions.Options;

namespace LLS.EFBulkExtensions.Core.Internal;

/// <summary>
/// Resolve quais colunas entram no ramo de UPDATE de um upsert, aplicando
/// <see cref="BulkInsertOrUpdateOptions.UpdateColumns"/>,
/// <see cref="BulkInsertOrUpdateOptions.ExcludeUpdateColumns"/> e
/// <see cref="BulkInsertOrUpdateOptions.InsertIfNotExists"/>. Trabalha apenas com nomes
/// (propriedade ou coluna) — não toca em schema. É puramente a composição do texto do comando.
/// </summary>
internal static class UpsertColumnResolver
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <param name="updatable">
    /// Colunas candidatas ao UPDATE (já filtradas: sem PK e sem geração no banco).
    /// </param>
    /// <param name="all">Todas as colunas mapeadas (usado para validar nomes informados).</param>
    /// <returns>Conjunto (case-insensitive) de nomes de coluna que devem entrar no SET.</returns>
    public static HashSet<string> ResolveUpdateColumns(
        IReadOnlyList<(string PropertyName, string ColumnName)> updatable,
        IReadOnlyList<(string PropertyName, string ColumnName)> all,
        BulkInsertOrUpdateOptions options)
    {
        Validate(options.UpdateColumns, nameof(BulkInsertOrUpdateOptions.UpdateColumns), all);
        Validate(options.ExcludeUpdateColumns, nameof(BulkInsertOrUpdateOptions.ExcludeUpdateColumns), all);

        var result = new HashSet<string>(Cmp);
        if (options.InsertIfNotExists)
        {
            // Conjunto vazio => cada provider cai no ramo "sem UPDATE" (DO NOTHING / INSERT IGNORE).
            return result;
        }

        var include = options.UpdateColumns;
        var exclude = options.ExcludeUpdateColumns;

        foreach (var col in updatable)
        {
            if (include is { Count: > 0 } && !Matches(include, col)) continue;
            if (exclude is { Count: > 0 } && Matches(exclude, col)) continue;
            result.Add(col.ColumnName);
        }

        return result;
    }

    private static bool Matches(IReadOnlyList<string> names, (string PropertyName, string ColumnName) col)
        => names.Any(n => Cmp.Equals(col.PropertyName, n) || Cmp.Equals(col.ColumnName, n));

    private static void Validate(
        IReadOnlyList<string>? names,
        string optionName,
        IReadOnlyList<(string PropertyName, string ColumnName)> all)
    {
        if (names == null) return;
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n))
                throw new InvalidOperationException($"{optionName} contém um nome de coluna vazio.");

            var hit = all.Any(c => Cmp.Equals(c.PropertyName, n) || Cmp.Equals(c.ColumnName, n));
            if (!hit)
                throw new InvalidOperationException(
                    $"{optionName}: '{n}' não corresponde a nenhuma propriedade ou coluna mapeada da entidade.");
        }
    }
}
