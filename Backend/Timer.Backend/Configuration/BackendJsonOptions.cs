using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Source2Surf.Timer.Backend.Contracts;

namespace Timer.Backend.Configuration;

internal static class BackendJsonOptions
{
    public static void Configure(JsonOptions options)
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, BackendJsonContext.Default);

        // A context used only as an IJsonTypeInfoResolver does not transfer its own
        // serializer-wide defaults into ASP.NET's JsonSerializerOptions instance.
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
