using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Modules.Timer;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

// What a HUD refresh keeps from the last one, so an unchanged text isn't formatted into a new string: the kept text,
// and the keys it's kept by, which must compare without allocating.
public sealed class HudRefreshTests
{
    private static readonly Dictionary<string, Dictionary<string, string>> Locale
        = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HudAssets.Locale()))!;

    private static long Allocated(Action action)
    {
        action(); // JIT and first-call work out of the way

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 100; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void AMemoKeepsItsTextUntilTheKeyChanges()
    {
        var memo = new HudMemo<(int Track, bool Practice, int Epoch)>();

        Assert.False(memo.TryGet((1, false, 0), out _));

        var text = memo.Set((1, false, 0), "Bonus 1 Completed!");

        Assert.True(memo.TryGet((1, false, 0), out var kept));
        Assert.Same(text, kept);
        Assert.False(memo.TryGet((1, true, 0), out _));
        Assert.False(memo.TryGet((1, false, 1), out _)); // the reader's language came in
    }

    [Fact]
    public void TheTitlesKeysCompareWithoutAllocating()
    {
        var header = new ReplayFileHeader { PlayerName = "aoba", Time = 61.5f };
        var p      = new HudPlayer(new PlayerSlot(1), 0f);

        p.FinishTitle.Set((2, true, 3), "a");
        p.ReplayTitle.Set((header, 1, 0, 2, 4, 3), "c");
        p.ReplayAfter.Set((true, 1f, 3), "d");

        var hits = 0;

        Assert.Equal(0, Allocated(() =>
        {
            hits += p.FinishTitle.TryGet((2, true, 3), out _) ? 1 : 0;
            hits += p.ReplayTitle.TryGet((header, 1, 0, 2, 4, 3), out _) ? 1 : 0;
            hits += p.ReplayAfter.TryGet((true, 1f, 3), out _) ? 1 : 0;
        }));

        Assert.Equal(303, hits);
    }

    [Fact]
    public void TheMenusDrawKeysCompareWithoutAllocatingAndSeeAChange()
    {
        IReadOnlyList<RunRecord> runs = [];

        var menu = new HudModule.ReplayMenuDrawn(1, 2, 3, 4, 0, 1, 0, 2, 0, 3, false, null, runs, false, null, EReplayBotStatus.Idle,
                                                 0, null, 0, 0, 0, 0, 0, false, 1f, new PlayerSlot(5), true, false, false, null);
        var card = new HudModule.ProfileDrawn(1, 2, 3, 4, 5, 6, 0, 1, runs, 10, 3, 1500, null, true, 3600, 4, true,
                                              new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), new PlayerCountry("CN", "China"));
        HudModule.ReplayMenuDrawn? menuDrawn = menu;
        HudModule.ProfileDrawn?    cardDrawn = card;

        var same = 0;

        Assert.Equal(0, Allocated(() =>
        {
            same += menuDrawn == menu ? 1 : 0;
            same += cardDrawn == card ? 1 : 0;
        }));

        Assert.Equal(202, same);

        Assert.NotEqual(menu, menu with { BotStatus = EReplayBotStatus.Running });
        Assert.NotEqual(menu, menu with { Busy = new PlayerSlot(5) });
        Assert.NotEqual(menu, menu with { Minute = 5 });
        Assert.NotEqual(card, card with { Points = 1501 });
        Assert.NotEqual(card, card with { SessionMinutes = 6 });
    }

    // The texts now formatted in place read exactly as through string formatting, in every language the locale has.
    [Theory]
    [InlineData("hud.specs.head")]
    [InlineData("ui.locs.title_count")]
    [InlineData("ui.locs.teleport_to")]
    [InlineData("ui.locs.clear_ask")]
    public void TextsFormattedInPlaceReadAsBefore(string key)
    {
        var text = HudTexts.All.Single(t => t.Key == key);

        foreach (var (language, template) in Locale[key])
        {
            var tr = new HudTr(k => k == key ? template : null);

            // String formatting takes ZString's one scratch builder too, so it goes first.
            var expected = tr.Format(text, 7);
            var sb       = ZString.CreateStringBuilder(true);

            try
            {
                tr.Format(ref sb, text, new IntArg(7));
                Assert.Equal(expected, sb.ToString());
            }
            finally
            {
                sb.Dispose();
            }

            Assert.False(string.IsNullOrEmpty(language));
        }
    }
}
