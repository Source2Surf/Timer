using System;
using MagicOnion.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Keeps the MagicOnion write surface completely absent unless the explicit write-role switch
/// is enabled. The read-only REST host therefore retains its existing dependency graph and routes.
/// </summary>
internal static class TimerWriteApiRegistration
{
    internal const int MaxMessageSizeBytes = 64 * 1024;
    internal const int MaxResponseSizeBytes = 64 * 1024 * 1024;

    public static void Add(IServiceCollection services, TimerWriteApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        // Deliberately do not widen TimerBackendStorage. This separate facade shares the
        // owner's lifecycle/scope while keeping REST-facing storage publicly read-only.
        services.AddSingleton<TimerBackendWriteStorage>();
        services.AddSingleton<TimerBackendGameStorage>();
        services.AddGrpc(grpc =>
        {
            // A legal v1 request contains at most 63 compact checkpoints and is only a
            // few KiB. Keep bounded headroom for future append-only fields without accepting
            // the 4 MiB framework default into the public deserialization path.
            grpc.MaxReceiveMessageSize = MaxMessageSizeBytes;
            // Responses are the backend's own data; a full map's leaderboards run to a few MiB.
            grpc.MaxSendMessageSize = MaxResponseSizeBytes;
            grpc.EnableDetailedErrors = false;

            // Endpoint routing is not bound to a Kestrel listener, so without this the
            // unauthenticated write service is reachable on the read port too (e.g. over TLS).
            if (options.LocalPorts.Count != 0)
            {
                grpc.Interceptors.Add<TimerWriteListenerInterceptor>();
            }
        });
        services.AddMagicOnion(magicOnion =>
            magicOnion.MessageSerializer = new TimerWriteMessageSerializerProvider());
    }

    public static void Map(IEndpointRouteBuilder endpoints, TimerWriteApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Enabled)
        {
            endpoints.MapMagicOnionService([typeof(TimerWriteServiceV1), typeof(TimerStorageServiceV1)]);
        }
    }
}
