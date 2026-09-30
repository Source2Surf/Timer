using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Configuration;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class BackendJsonContractTests
{
    [Fact]
    public void RecordEnvelopeUsesCamelCaseAndOmitsNullFields()
    {
        var response = new RecordListResponse
        {
            MapName = "surf_contract",
            Records = [new RunRecordDto
            {
                Id      = "1",
                RunDate = 1_800_000_000_123,
                SteamId = "2",
                MapId   = "3",
            }],
        };

        var options = new JsonOptions();
        BackendJsonOptions.Configure(options);
        var json = JsonSerializer.Serialize(response, options.SerializerOptions);

        Assert.Contains("\"apiVersion\":\"v1\"", json);
        Assert.Contains("\"mapName\":\"surf_contract\"", json);
        Assert.Contains("\"runDate\":1800000000123", json);
        Assert.DoesNotContain("\"runDate\":\"", json);
        Assert.Contains("\"timeMicros\":0", json);
        Assert.DoesNotContain("PlayerName", json);
        Assert.DoesNotContain("playerName", json);
        Assert.DoesNotContain("checkpoints", json);
    }

    [Fact]
    public void MapProfileTiersSerializeAsANumberArray()
    {
        var profile = new MapProfileDto { MapId = "1", MapName = "surf_contract", Tier = [1, 6] };

        var options = new JsonOptions();
        BackendJsonOptions.Configure(options);
        var json = JsonSerializer.Serialize(profile, options.SerializerOptions);

        Assert.Contains("\"tier\":[1,6]", json);
    }

    [Fact]
    public void MapCatalogSerializesNamesWithoutProfiles()
    {
        var response = new MapListResponse { MapNames = ["surf_a", "surf_b"] };

        var json = JsonSerializer.Serialize(response, BackendJsonContext.Default.MapListResponse);

        Assert.Equal("{\"apiVersion\":\"v1\",\"mapNames\":[\"surf_a\",\"surf_b\"]}", json);
    }
}
