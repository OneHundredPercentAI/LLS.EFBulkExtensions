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
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException($"Tipo de entidade {typeof(TEntity).Name} não encontrado no modelo.");

        var tableName = entityType.GetTableName() ?? throw new InvalidOperationException("Nome da tabela não encontrado no modelo para a entidade.");
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName!, schema);

        var pk = entityType.FindPrimaryKey();
        
        var mappings = new List<PropertyMapping>();
        CollectProperties(entityType, store, pk, obj => obj, mappings, includeIdentity);

        var table = new DataTable(tableName);
        
        foreach (var mapping in mappings)
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

        foreach (var e in entities)
        {
            var row = table.NewRow();
            foreach (var mapping in mappings)
            {
                var p = mapping.Property;
                var value = mapping.ValueAccessor(e);

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

        return (table, mappings.Select(m => m.Property).ToList());
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
                    return parent == null ? null : p.PropertyInfo.GetValue(parent);
                },
                DefaultClrValue = defaultClrValue,
                MetadataDefaultValue = p.GetDefaultValue(),
                Converter = p.GetValueConverter()
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
