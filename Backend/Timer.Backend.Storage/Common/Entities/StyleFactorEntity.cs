using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

// A style's score factor as the game servers registered it (their timer-styles.jsonc score_factor).
[SugarTable("surf_style_factors")]
internal sealed class StyleFactorEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Style { get; set; }

    public double Factor { get; set; }

    [SugarColumn(ColumnDataType = "bigint")]
    public long UpdatedAtUnixMilliseconds { get; set; }
}
