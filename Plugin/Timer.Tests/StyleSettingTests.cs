using System.Text.Json;
using Source2Surf.Timer;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

public sealed class StyleSettingTests
{
    [Fact]
    public void KeysForOtherModulesAreKept()
    {
        const string json = """
                            [
                                // a module's own key
                                { "name": "Other", "wishspeed": 40, "other_option": 1.5 }
                            ]
                            """;

        var style = Assert.Single(JsonSerializer.Deserialize<List<StyleSetting>>(json, Utils.DeserializerOptions)!);

        Assert.Equal(40f, style.WishSpeed);
        Assert.Equal(1.5f, style.ExtensionData!["other_option"].GetSingle());
        Assert.False(style.ExtensionData.ContainsKey("wishspeed"));
    }

    [Fact]
    public void DefaultStyleWritesNoExtraKeys()
    {
        var json = JsonSerializer.Serialize(new StyleSetting(), Utils.SerializerOptions);

        Assert.Null(JsonSerializer.Deserialize<StyleSetting>(json)!.ExtensionData);
    }
}
