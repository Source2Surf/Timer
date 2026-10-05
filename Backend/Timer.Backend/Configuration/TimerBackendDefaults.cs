using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Timer.Backend.Configuration;

/// <summary>
/// Without listeners of its own, the backend serves its read API on 127.0.0.1:5081 and the game servers' gRPC on
/// 127.0.0.1:5082, and keeps the unauthenticated write API to that port.
/// </summary>
internal static class TimerBackendDefaults
{
    public const string HttpUrl  = "http://127.0.0.1:5081";
    public const string GrpcUrl  = "http://127.0.0.1:5082";
    public const int    GrpcPort = 5082;

    public static void Apply(IConfigurationManager configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").Exists() || !string.IsNullOrWhiteSpace(configuration["urls"]))
        {
            return;
        }

        var defaults = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"]       = HttpUrl,
            ["Kestrel:Endpoints:Http:Protocols"] = "Http1",
            ["Kestrel:Endpoints:Grpc:Url"]       = GrpcUrl,
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
        };

        if (!configuration.GetSection($"{TimerWriteApiOptions.SectionName}:LocalPorts").Exists())
        {
            defaults[$"{TimerWriteApiOptions.SectionName}:LocalPorts:0"] = GrpcPort.ToString();
        }

        configuration.AddInMemoryCollection(defaults);
    }
}
