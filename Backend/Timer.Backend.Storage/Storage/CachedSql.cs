using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using SqlSugar;

namespace Timer.Backend.Storage;

/// <summary>
/// A statement whose SQL SqlSugar generates once per shape, from the same typed query or update the ORM would run.
/// Later calls bind their own values to it, skipping SqlSugar's statement building (~0.3–1 ms a call) and, for
/// reads, its row mapping (3–5 µs a row).
/// </summary>
internal sealed class CachedSql
{
    private readonly SugarParameter[] _template;
    private readonly int[]            _argument;
    private int[]?                    _ordinals;

    public string Sql { get; }

    private CachedSql(string sql, SugarParameter[] template, int[] argument)
    {
        Sql       = sql;
        _template = template;
        _argument = argument;
    }

    /// <summary>
    /// The shape was generated with <paramref name="sentinels"/> as its arguments, so each parameter holding a
    /// sentinel takes that argument; every other parameter is a constant of the shape.
    /// </summary>
    public static CachedSql Create(KeyValuePair<string, List<SugarParameter>> generated, object[] sentinels)
    {
        var (sql, parameters) = generated;

        foreach (var sentinel in sentinels)
        {
            if (sql.Contains(Literal(sentinel), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"SqlSugar wrote an argument into the SQL instead of a parameter: {sql}");
            }
        }

        var argument = new int[parameters.Count];
        var used     = new bool[sentinels.Length];

        for (var i = 0; i < argument.Length; i++)
        {
            argument[i] = Array.FindIndex(sentinels, sentinel => SameValue(parameters[i].Value, sentinel));

            if (argument[i] >= 0)
            {
                used[argument[i]] = true;
            }
        }

        if (Array.IndexOf(used, false) is var missing and >= 0)
        {
            throw new InvalidOperationException($"No parameter holds argument {missing}: {sql}");
        }

        return new CachedSql(sql, parameters.ToArray(), argument);
    }

    public SugarParameter[] Bind(object[] arguments)
    {
        var bound = new SugarParameter[_template.Length];

        for (var i = 0; i < bound.Length; i++)
        {
            var template = _template[i];
            var value    = _argument[i] < 0
                               ? template.Value
                               : Convert.ChangeType(arguments[_argument[i]], template.Value.GetType(), CultureInfo.InvariantCulture);

            // Everything the provider types the parameter by; Size is left to follow the new value.
            bound[i] = new SugarParameter(template.ParameterName, value)
            {
                DbType       = template.DbType,
                Direction    = template.Direction,
                IsNullable   = template.IsNullable,
                Scale        = template.Scale,
                TypeName     = template.TypeName,
                UdtTypeName  = template.UdtTypeName,
                CustomDbType = template.CustomDbType,
                IsJson       = template.IsJson,
                IsArray      = template.IsArray,
                IsRefCursor  = template.IsRefCursor,
                IsClob       = template.IsClob,
                IsNClob      = template.IsNClob,
                IsNvarchar2  = template.IsNvarchar2,
            };
        }

        return bound;
    }

    /// <summary>The columns' ordinals, looked up once: a shape's column order never changes.</summary>
    public int[] Ordinals(DbDataReader reader, string[] columns)
    {
        if (_ordinals is { } ordinals)
        {
            return ordinals;
        }

        ordinals = new int[columns.Length];

        for (var i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = reader.GetOrdinal(columns[i]);
        }

        return _ordinals = ordinals;
    }

    private static string Literal(object sentinel)
        => sentinel is DateTime date
               ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
               : Convert.ToString(sentinel, CultureInfo.InvariantCulture)!;

    private static bool SameValue(object? value, object sentinel)
        => value is DateTime date
               ? sentinel is DateTime other && date == other
               : value is IConvertible convertible
                 && sentinel is not DateTime
                 && convertible.GetTypeCode() is >= TypeCode.SByte and <= TypeCode.Decimal
                 && convertible.ToDecimal(CultureInfo.InvariantCulture) == Convert.ToDecimal(sentinel, CultureInfo.InvariantCulture);
}
