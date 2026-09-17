using System;
using System.Data;
using System.Globalization;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

/// <summary>
/// Persists the public unsigned 32-bit score contract through a signed SQL BIGINT parameter.
/// PostgreSQL's SQLSugar provider otherwise normalizes a <see cref="uint"/> parameter through
/// <see cref="int"/>, which overflows before a valid value above <see cref="int.MaxValue"/>
/// reaches the database.
/// </summary>
public sealed class UInt32BigIntConverter : ISugarDataConverter
{
    public SugarParameter ParameterConverter<T>(object columnValue, int columnIndex)
    {
        var value = columnValue switch
        {
            uint points => (long)points,
            _ => Convert.ToInt64(columnValue, CultureInfo.InvariantCulture),
        };

        if (value is < uint.MinValue or > uint.MaxValue)
        {
            throw new OverflowException("A persisted score is outside the UInt32 range.");
        }

        return new SugarParameter($"@Column{columnIndex}", value, System.Data.DbType.Int64);
    }

    public T QueryConverter<T>(IDataRecord dataRecord, int columnIndex)
    {
        ArgumentNullException.ThrowIfNull(dataRecord);
        if (dataRecord.IsDBNull(columnIndex))
        {
            throw new InvalidOperationException("A non-null score column returned NULL.");
        }

        var value = Convert.ToInt64(dataRecord.GetValue(columnIndex), CultureInfo.InvariantCulture);
        if (value is < uint.MinValue or > uint.MaxValue)
        {
            throw new OverflowException("A persisted score is outside the UInt32 range.");
        }

        return (T)(object)checked((uint)value);
    }
}
