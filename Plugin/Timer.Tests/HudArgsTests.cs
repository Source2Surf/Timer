using System.Globalization;
using Cysharp.Text;
using Source2Surf.Timer.Modules.Hud;
using Xunit;

namespace Timer.Tests;

// HUD texts formatted straight into a builder read exactly as the string formatting they replace.
public sealed class HudArgsTests
{
    private static string Build<T>(T arg) where T : struct, IHudArg
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            arg.AppendTo(ref sb);

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    private static string Template<T1, T2>(string template, T1 a, T2 b, int args, out bool ok)
        where T1 : struct, IHudArg
        where T2 : struct, IHudArg
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            ok = HudTemplate.TryAppend(ref sb, template, a, b, args);

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-3f)]
    [InlineData(5.1234f)]
    [InlineData(65.5f)]
    [InlineData(3723.45f)]
    public void TimesMatchFormatTime(float seconds)
        => Assert.Equal(HudFormat.FormatTime(seconds), Build(new TimeArg(seconds)));

    [Theory]
    [InlineData(0L)]
    [InlineData(1500L)]
    [InlineData(-400L)]
    [InlineData(62030L)]
    public void DifferencesMatchFormatDiff(long ms)
        => Assert.Equal(HudFormat.FormatDiff(ms), Build(new DiffArg(ms)));

    [Theory]
    [InlineData(0f)]
    [InlineData(12.5f)]
    [InlineData(-9.4f)]
    [InlineData(301.6f)]
    public void SpeedDifferencesMatchFormatSpeedDiff(float difference)
        => Assert.Equal(HudFormat.FormatSpeedDiff(difference), Build(new SpeedDiffArg(difference)));

    [Theory]
    [InlineData(93.4f)]
    [InlineData(0f)]
    [InlineData(100f)]
    [InlineData(66.666f)]
    public void TwoDecimalsMatchF2(float value)
        => Assert.Equal(value.ToString("F2", CultureInfo.InvariantCulture), Build(new Fixed2Arg(value)));

    [Fact]
    public void TemplatesFillTheirPlaceholdersInAnyOrder()
    {
        Assert.Equal("Time: 5.000", Template("{0}: {1}", new TextArg("Time"), new TimeArg(5), 2, out var ok));
        Assert.True(ok);
        Assert.Equal("5.000：Time", Template("{1}：{0}", new TextArg("Time"), new TimeArg(5), 2, out _));
        Assert.Equal("{Speed} 7", Template("{{Speed}} {0}", new IntArg(7), default(NoArg), 1, out ok));
        Assert.True(ok);
    }

    [Theory]
    [InlineData("Speed: {1}")]
    [InlineData("Speed: {0")]
    [InlineData("Speed: 0}")]
    [InlineData("Speed: {x}")]
    public void ABadTemplateIsRefused(string template)
    {
        Template(template, new IntArg(7), default(NoArg), 1, out var ok);
        Assert.False(ok);
    }

    [Fact]
    public void ABadTranslationFallsBackToTheEnglishAfterWhatCameBefore()
    {
        var tr = new HudTr(_ => "Speed: {3}");
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            sb.Append("[");
            tr.Format(ref sb, HudTexts.LineSpeed, new IntArg(250));
            Assert.Equal("[" + string.Format(HudTexts.LineSpeed.English, 250), sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }
}
