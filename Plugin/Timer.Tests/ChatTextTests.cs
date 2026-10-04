using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

// The chat's texts against the shipped locale file, and the translator's fallbacks.
public sealed class ChatTextTests
{
    private sealed class FakeLocalization(Func<string, string?> lookup) : ILocalizationProvider
    {
        public string? GetText(PlayerSlot slot, string key)
            => lookup(key);
    }

    [Fact]
    public void EveryChatTextIsInTheLocaleFileWithItsEnglish()
    {
        var locale = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HudAssets.Locale()))!;

        Assert.Equal(ChatTexts.All.Count, ChatTexts.All.Select(t => t.Key).Distinct().Count());

        foreach (var text in ChatTexts.All)
        {
            Assert.True(locale.TryGetValue(text.Key, out var translations), text.Key);
            Assert.Equal(text.English, translations["en-us"]);
            Assert.True(translations.ContainsKey("zh-cn"), text.Key);
        }

        Assert.Empty(locale.Keys.Where(x => x.StartsWith("chat.", StringComparison.Ordinal)).Except(ChatTexts.All.Select(t => t.Key)));
    }

    [Fact]
    public void TheTranslatorFallsBackToEnglish()
    {
        var chinese = new ChatTr(new FakeLocalization(key => key == ChatTexts.LocSaved.Key ? "已保存存点 #{0}。" : null), default);
        Assert.Equal("已保存存点 #3。", chinese.Format(ChatTexts.LocSaved, 3));
        Assert.Equal("Teleported to loc #1/2.", chinese.Format(ChatTexts.LocTeleported, 1, 2)); // no translation

        var broken = new ChatTr(new FakeLocalization(_ => "{3}"), default);
        Assert.Equal("Saved location #3.", broken.Format(ChatTexts.LocSaved, 3)); // placeholders that don't fit
        Assert.Equal("Saved location #3.", default(ChatTr).Format(ChatTexts.LocSaved, 3));
        Assert.Equal("Bonus 2", default(ChatTr).Track(2));
        Assert.Equal("Main", default(ChatTr).Track(0));
    }
}
