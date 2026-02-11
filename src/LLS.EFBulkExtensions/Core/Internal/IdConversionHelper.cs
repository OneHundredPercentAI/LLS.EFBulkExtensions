using System;

namespace LLS.EFBulkExtensions.Core.Internal;

internal static class IdConversionHelper
{
    /// <summary>
    /// Converte um valor numérico de identidade (Int64) para o tipo CLR de ID configurado na entidade.
    /// Suporta tipos numéricos inteiros comuns e seus equivalentes anuláveis.
    /// </summary>
    public static object FromInt64(long value, Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (underlying == typeof(long)) return value;
        if (underlying == typeof(int)) return (int)value;
        if (underlying == typeof(short)) return (short)value;
        if (underlying == typeof(byte)) return (byte)value;
        if (underlying == typeof(decimal)) return (decimal)value;
        if (underlying == typeof(ulong)) return (ulong)value;
        if (underlying == typeof(uint)) return (uint)value;
        if (underlying == typeof(ushort)) return (ushort)value;

        throw new NotSupportedException($"Tipo de ID não suportado para conversão a partir de Int64: {clrType.FullName}");
    }
}

