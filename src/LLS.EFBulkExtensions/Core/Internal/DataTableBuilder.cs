using System;
using System.Data;
using System.Linq;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LLS.EFBulkExtensions.Core.Internal;

public static class DataTableBuilder
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

    public static (DataTable Table, IReadOnlyList<IProperty> IncludedProperties) Build<TEntity>(DbContext context, IEnumerable<TEntity> entities, bool includeIdentity) where TEntity : class
    {
        var list = entities as IList<TEntity> ?? (entities is ICollection<TEntity> c ? new List<TEntity>(c) : new List<TEntity>(entities));
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
        // Discriminator (se houver)
        var discriminator = firstType.GetProperties()
            .FirstOrDefault(prop =>
                prop.GetColumnName(store) != null &&
                string.Equals(prop.Name, "Discriminator", StringComparison.OrdinalIgnoreCase));
        if (discriminator != null)
        {
            mappingsAll.Add(new PropertyMapping
            {
                Property = discriminator,
                ValueAccessor = _ =>
                {
                    // nome curto do tipo da primeira entidade, aplicado por linha abaixo
                    return null;
                },
                DefaultClrValue = null,
                MetadataDefaultValue = discriminator.GetDefaultValue(),
                Converter = discriminator.GetValueConverter()
            });
        }

        // Deduplicar por coluna
        var byColumn = new Dictionary<string, PropertyMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappingsAll)
        {
            var col = m.Property.GetColumnName(store);
            if (col == null) continue;
            if (!byColumn.ContainsKey(col)) byColumn[col] = m;
        }

        var table = new DataTable(tableName);
        
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
            table.Columns.Add(p.GetColumnName(store)!, colType);
        }

        foreach (var e in list)
        {
            var row = table.NewRow();
            foreach (var mapping in byColumn.Values)
            {
                var p = mapping.Property;
                object? value;
                if (string.Equals(p.Name, "Discriminator", StringComparison.OrdinalIgnoreCase))
                {
                    var full = e!.GetType().Name;
                    value = full;
                }
                else
                {
                    value = mapping.ValueAccessor(e);
                }

                if (mapping.MetadataDefaultValue != null && object.Equals(value, mapping.DefaultClrValue))
                {
                    value = mapping.MetadataDefaultValue;
                }

                var colName = p.GetColumnName(store)!;

                if (value == null)
                {
                    row[colName] = DBNull.Value;
                }
                else
                {
                    if (mapping.Converter != null)
                    {
                        var convertedValue = mapping.Converter.ConvertToProvider(value);
                        row[colName] = convertedValue ?? DBNull.Value;
                    }
                    else if (mapping.IsEnumToString)
                    {
                        row[colName] = value.ToString();
                    }
                    else
                    {
                        var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                        if (type.IsEnum)
                        {
                            row[colName] = Convert.ChangeType(value, Enum.GetUnderlyingType(type));
                        }
                        else
                        {
                            row[colName] = value;
                        }
                    }
                }
            }
            table.Rows.Add(row);
        }

        return (table, byColumn.Values.Select(m => m.Property).ToList());
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

        var discriminator = entityType.GetProperties()
            .FirstOrDefault(prop =>
                prop.GetColumnName(store) != null &&
                string.Equals(prop.Name, "Discriminator", StringComparison.OrdinalIgnoreCase));
        if (discriminator != null)
        {
            mappings.Add(new PropertyMapping
            {
                Property = discriminator,
                ValueAccessor = _ =>
                {
                    var clr = Nullable.GetUnderlyingType(discriminator.ClrType) ?? discriminator.ClrType;
                    if (clr == typeof(string))
                    {
                        var full = entityType.Name;
                        var idx = full.LastIndexOf('.');
                        return idx >= 0 ? full[(idx + 1)..] : full;
                    }
                    return discriminator.GetDefaultValue();
                },
                DefaultClrValue = null,
                MetadataDefaultValue = discriminator.GetDefaultValue(),
                Converter = discriminator.GetValueConverter()
            });
        }

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
