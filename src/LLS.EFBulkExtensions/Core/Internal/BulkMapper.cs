using System;
using System.Data;
using System.Linq;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LLS.EFBulkExtensions.Core.Internal;

public static class BulkMapper
{
    private class PropertyMapping
    {
        public IProperty Property { get; set; } = null!;
        public Func<object, object?> ValueAccessor { get; set; } = null!;
        public object? DefaultClrValue { get; set; }
        public object? MetadataDefaultValue { get; set; }
        public Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter? Converter { get; set; }
        public bool IsEnumToString { get; set; }
    }

    /// <summary>
    /// Coluna resolvida para bulk: nome, tipo CLR usado na coluna e um acessor que devolve
    /// o valor já pronto para o provider (conversões/enum/default aplicados). null => DBNull.
    /// Usado tanto pela construção de DataTable quanto pelo <see cref="EntityDataReader{TEntity}"/> (streaming).
    /// </summary>
    internal sealed class BulkColumn
    {
        public IProperty Property { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public Type ColumnType { get; init; } = null!;
        public Func<object, object?> GetProviderValue { get; init; } = null!;
    }

    /// <summary>
    /// Resolve o conjunto de colunas (com acessores de valor já convertidos) para a entidade,
    /// considerando owned types, TPH/discriminator e value converters. Não materializa linhas.
    /// </summary>
    internal static (IReadOnlyList<BulkColumn> Columns, string TableName, string? Schema) BuildColumns<TEntity>(
        DbContext context, IList<TEntity> list, bool includeIdentity) where TEntity : class
    {
        if (list.Count == 0) throw new InvalidOperationException("Não há entidades para inserir.");

        var fallbackType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");
        var firstType = context.Model.FindEntityType(list[0]!.GetType()) ?? fallbackType;

        var tableName = firstType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado no modelo para a entidade.");
        var schema = firstType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName!, schema);

        var entityTypesUsed = new HashSet<IEntityType>();
        foreach (var e in list)
        {
            var rt = context.Model.FindEntityType(e!.GetType());
            if (rt != null)
            {
                // validar mesma tabela
                if (rt.GetTableName() != tableName || rt.GetSchema() != schema)
                {
                    throw new InvalidOperationException("Todas as entidades em uma operação de bulk devem mapear para a mesma tabela.");
                }
                entityTypesUsed.Add(rt);
            }
        }
        if (entityTypesUsed.Count == 0) entityTypesUsed.Add(fallbackType);

        var mappingsAll = new List<PropertyMapping>();
        foreach (var et in entityTypesUsed)
        {
            var pkEt = et.FindPrimaryKey();
            CollectProperties(et, store, pkEt, obj => obj, mappingsAll, includeIdentity);
        }
        // Discriminador TPH: resolvido pelos metadados do EF — honra HasValue customizado,
        // discriminadores não-string e coluna com nome customizado. Tratado à parte (abaixo),
        // nunca pelo nome curto do tipo CLR.
        var discProp = firstType.FindDiscriminatorProperty();
        var discColumn = discProp?.GetColumnName(store);

        // Deduplicar por coluna
        var byColumn = new Dictionary<string, PropertyMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappingsAll)
        {
            var col = m.Property.GetColumnName(store);
            if (col == null) continue;
            if (!byColumn.ContainsKey(col)) byColumn[col] = m;
        }

        // A coluna do discriminador (se mapeada como propriedade) é removida daqui para ser
        // preenchida com o valor dos metadados, não com um acessor de propriedade comum.
        if (discColumn != null) byColumn.Remove(discColumn);

        var columns = new List<BulkColumn>(byColumn.Count);
        foreach (var mapping in byColumn.Values)
        {
            var p = mapping.Property;
            var converter = mapping.Converter;
            Type colType;

            if (converter != null)
            {
                colType = converter.ProviderClrType;
                colType = Nullable.GetUnderlyingType(colType) ?? colType;
            }
            else
            {
                var providerType = p.GetProviderClrType();
                var underlyingClr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;

                if (underlyingClr.IsEnum && providerType == typeof(string))
                {
                    colType = typeof(string);
                    mapping.IsEnumToString = true;
                }
                else
                {
                    colType = underlyingClr;
                    if (colType.IsEnum)
                    {
                        colType = Enum.GetUnderlyingType(colType);
                    }
                }
            }

            var colName = p.GetColumnName(store)!;

            // Captura local para o closure
            var rawAccessor = mapping.ValueAccessor;
            var metadataDefault = mapping.MetadataDefaultValue;
            var defaultClr = mapping.DefaultClrValue;
            var isEnumToString = mapping.IsEnumToString;
            var clrUnderlying = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
            var clrIsEnum = clrUnderlying.IsEnum;
            var enumUnderlying = clrIsEnum ? Enum.GetUnderlyingType(clrUnderlying) : null;

            Func<object, object?> getProviderValue = obj =>
            {
                object? value = rawAccessor(obj);

                if (metadataDefault != null && Equals(value, defaultClr))
                {
                    value = metadataDefault;
                }

                if (value == null) return null;
                if (converter != null) return converter.ConvertToProvider(value);
                if (isEnumToString) return value.ToString();
                if (clrIsEnum) return Convert.ChangeType(value, enumUnderlying!);
                return value;
            };

            columns.Add(new BulkColumn
            {
                Property = p,
                ColumnName = colName,
                ColumnType = colType,
                GetProviderValue = getProviderValue
            });
        }

        // Coluna do discriminador TPH: valor configurado no modelo, resolvido pelo tipo concreto
        // de cada linha (honra HasValue customizado). Para a config padrão, GetDiscriminatorValue()
        // já devolve o nome curto do tipo — mantendo o comportamento anterior.
        if (discProp != null && discColumn != null)
        {
            var discConverter = discProp.GetValueConverter();
            var valueByType = new Dictionary<Type, object?>();
            foreach (var et in entityTypesUsed)
            {
                valueByType[et.ClrType] = et.GetDiscriminatorValue();
            }
            var defaultDiscValue = firstType.GetDiscriminatorValue();

            Type discColType;
            if (discConverter != null)
            {
                discColType = Nullable.GetUnderlyingType(discConverter.ProviderClrType) ?? discConverter.ProviderClrType;
            }
            else
            {
                var u = Nullable.GetUnderlyingType(discProp.ClrType) ?? discProp.ClrType;
                discColType = u.IsEnum ? Enum.GetUnderlyingType(u) : u;
            }

            columns.Add(new BulkColumn
            {
                Property = discProp,
                ColumnName = discColumn,
                ColumnType = discColType,
                GetProviderValue = obj =>
                {
                    var v = valueByType.TryGetValue(obj.GetType(), out var found) ? found : defaultDiscValue;
                    if (v == null) return null;
                    if (discConverter != null) return discConverter.ConvertToProvider(v);
                    if (v is Enum) return Convert.ChangeType(v, Enum.GetUnderlyingType(v.GetType()));
                    return v;
                }
            });
        }

        return (columns, tableName!, schema);
    }

    public static (DataTable Table, IReadOnlyList<IProperty> IncludedProperties) Build<TEntity>(DbContext context, IEnumerable<TEntity> entities, bool includeIdentity) where TEntity : class
    {
        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));

        var (columns, tableName, _) = BuildColumns(context, list, includeIdentity);

        var table = new DataTable(tableName);
        foreach (var column in columns)
        {
            table.Columns.Add(column.ColumnName, column.ColumnType);
        }

        foreach (var e in list)
        {
            var row = table.NewRow();
            foreach (var column in columns)
            {
                row[column.ColumnName] = column.GetProviderValue(e!) ?? DBNull.Value;
            }
            table.Rows.Add(row);
        }

        return (table, columns.Select(c => c.Property).ToList());
    }

    private static void CollectProperties(
        IEntityType entityType,
        StoreObjectIdentifier store,
        IKey? pk,
        Func<object, object?> accessor,
        List<PropertyMapping> mappings,
        bool includeIdentity)
    {
        foreach (var p in entityType.GetProperties())
        {
            if (p.PropertyInfo == null) continue;
            if (p.GetColumnName(store) == null) continue;

            var isPk = pk?.Properties.Contains(p) == true;
            if (!includeIdentity && isPk && p.ValueGenerated == ValueGenerated.OnAdd)
            {
                continue;
            }

            var propType = p.ClrType;
            object? defaultClrValue = propType.IsValueType ? Activator.CreateInstance(propType) : null;

            mappings.Add(new PropertyMapping
            {
                Property = p,
                ValueAccessor = obj =>
                {
                    var parent = accessor(obj);
                    if (parent == null) return null;
                    var decl = p.PropertyInfo.DeclaringType!;
                    if (!decl.IsAssignableFrom(parent.GetType())) return null;
                    return p.PropertyInfo.GetValue(parent);
                },
                DefaultClrValue = defaultClrValue,
                MetadataDefaultValue = p.GetDefaultValue(),
                Converter = p.GetValueConverter()
            });
        }

        // O discriminador TPH é tratado de forma centralizada em BuildColumns, a partir dos
        // metadados do EF (GetDiscriminatorValue), e não aqui.

        foreach (var nav in entityType.GetNavigations())
        {
            if (!nav.TargetEntityType.IsOwned()) continue;

            var targetTable = nav.TargetEntityType.GetTableName();
            var targetSchema = nav.TargetEntityType.GetSchema();

            if (targetTable != store.Name || targetSchema != store.Schema) continue;

            if (nav.PropertyInfo == null) continue;

            Func<object, object?> childAccessor = obj =>
            {
                var parent = accessor(obj);
                return parent == null ? null : nav.PropertyInfo.GetValue(parent);
            };

            CollectProperties(nav.TargetEntityType, store, pk, childAccessor, mappings, includeIdentity);
        }
    }
}
