using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

// Persist Steam IDs as signed BIGINT, matching the existing database schema.
// The request/replay interfaces continue to use ModSharp SteamID; conversion
// happens only at the storage boundary, never inside SqlSugar expressions.
internal abstract class BaseSteamIdEntity
{
    [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")]
    public long SteamId { get; set; }
}

internal abstract class BaseSteamIdSerialEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    [SugarColumn(ColumnDataType = "bigint")]
    public long SteamId { get; set; }
}
