using System.Text.Json;
using Sharp.Shared.Definition;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

// The checkpoint chat line: progress, time, the SR's and PB's diffs and the speed, in grey with its values in colour.
public sealed class CheckpointMessageTests
{
    // 1035 ticks: a run's time is whole ticks.
    private const float Time = 1035 / 64f;

    [Fact]
    public void ALineHasProgressTimeBothDiffsAndSpeed()
    {
        var line = MessageModule.CheckpointLine(default, 1, 4, Time, -0.123f, 0.045f, 1290.4f);

        Assert.Equal("CP 1/4 | 16.171 | SR -0.123 | PB +0.045 | 1290 u/s", Plain(line));
        Assert.StartsWith(ChatColor.Grey, line);
        Assert.Contains(ChatColor.Gold + "16.171" + ChatColor.Grey, line);
        Assert.Contains(ChatColor.LightGreen + "-0.123", line);
        Assert.Contains(ChatColor.Red + "+0.045", line);
        Assert.Contains(ChatColor.Blue + "1290" + ChatColor.Grey, line);
    }

    [Fact]
    public void WithoutARecordOrPbItKeepsProgressTimeAndSpeed()
        => Assert.Equal("CP 2/4 | 16.171 | 980 u/s", Plain(MessageModule.CheckpointLine(default, 2, 4, Time, null, null, 980f)));

    [Fact]
    public void ChineseReadsTheSame()
    {
        var chinese = new ChatTr(new LocaleFile("zh-cn"), default);

        Assert.Equal("检查点 1/4 | 16.171 | SR -0.123 | PB +0.045 | 1290 u/s",
                     Plain(MessageModule.CheckpointLine(chinese, 1, 4, Time, -0.123f, 0.045f, 1290f)));
    }

    [Fact]
    public void ACheckpointPastTheMapsLastCountsAsTheTotal()
        => Assert.StartsWith("CP 3/3 |", Plain(MessageModule.CheckpointLine(default, 3, 0, Time, null, null, null)));

    // Without the colour codes.
    private static string Plain(string line)
        => new (line.Where(c => c >= ' ').ToArray());

    private sealed class LocaleFile(string language) : ILocalizationProvider
    {
        private readonly Dictionary<string, Dictionary<string, string>> _texts
            = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HudAssets.Locale()))!;

        public string? GetText(PlayerSlot slot, string key)
            => _texts.TryGetValue(key, out var translations) ? translations.GetValueOrDefault(language) : null;
    }
}
