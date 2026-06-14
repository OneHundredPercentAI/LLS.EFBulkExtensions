using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace LLS.EFBulkExtensions.Core.Internal;

/// <summary>
/// DbDataReader que projeta uma sequência de entidades como linhas, sob demanda,
/// usando as colunas resolvidas por <see cref="DataTableBuilder.BuildColumns{TEntity}"/>.
/// Evita materializar um DataTable inteiro em memória durante o bulk insert.
/// </summary>
internal sealed class EntityDataReader<TEntity> : DbDataReader where TEntity : class
{
    private readonly IReadOnlyList<DataTableBuilder.BulkColumn> _columns;
    private readonly Dictionary<string, int> _ordinals;
    private readonly IEnumerator<TEntity> _enumerator;
    private readonly object?[] _current;
    private bool _closed;

    public EntityDataReader(IEnumerable<TEntity> entities, IReadOnlyList<DataTableBuilder.BulkColumn> columns)
    {
        _columns = columns;
        _enumerator = entities.GetEnumerator();
        _current = new object?[columns.Count];
        _ordinals = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        for (int i = 0; i < columns.Count; i++)
        {
            _ordinals[columns[i].ColumnName] = i;
        }
    }

    public override bool Read()
    {
        if (!_enumerator.MoveNext()) return false;

        var entity = _enumerator.Current;
        for (int i = 0; i < _columns.Count; i++)
        {
            _current[i] = _columns[i].GetProviderValue(entity!);
        }
        return true;
    }

    public override int FieldCount => _columns.Count;
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;

    public override string GetName(int ordinal) => _columns[ordinal].ColumnName;
    public override Type GetFieldType(int ordinal) => _columns[ordinal].ColumnType;
    public override string GetDataTypeName(int ordinal) => _columns[ordinal].ColumnType.Name;

    public override int GetOrdinal(string name) =>
        _ordinals.TryGetValue(name, out var i)
            ? i
            : throw new IndexOutOfRangeException($"Coluna '{name}' não encontrada.");

    public override object GetValue(int ordinal) => _current[ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => _current[ordinal] is null;

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, _columns.Count);
        for (int i = 0; i < n; i++) values[i] = _current[i] ?? DBNull.Value;
        return n;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    // Getters tipados: o SqlBulkCopy genérico consome via GetValue; delegamos por robustez.
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var data = (byte[])GetValue(ordinal);
        if (buffer == null) return data.Length;
        var toCopy = (int)Math.Min(length, data.Length - dataOffset);
        if (toCopy <= 0) return 0;
        Array.Copy(data, dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var data = ((string)GetValue(ordinal)).ToCharArray();
        if (buffer == null) return data.Length;
        var toCopy = (int)Math.Min(length, data.Length - dataOffset);
        if (toCopy <= 0) return 0;
        Array.Copy(data, dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _enumerator.Dispose();
            _closed = true;
        }
        base.Dispose(disposing);
    }
}
