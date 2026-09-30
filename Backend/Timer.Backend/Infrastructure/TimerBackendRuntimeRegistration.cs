using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Configuration;

namespace Timer.Backend.Infrastructure;

internal static class TimerBackendRuntimeRegistration
{
    private static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(5);

    internal static void Add(IServiceCollection services, TimerBackendRuntimeOptions options,
                              TimeSpan? leaderboardCacheDuration = null)
    {
        services.AddSingleton(options);

        // The host stops Kestrel (draining requests, each bounded by RequestTimeout) and then the
        // storage worker within ONE HostOptions.ShutdownTimeout, 30 s by default. Without this a
        // WorkerShutdownTimeout above ~30 s had no effect. Only ever raise the host budget.
        services.Configure<HostOptions>(host =>
        {
            var needed = options.RequestTimeout + options.WorkerShutdownTimeout + ShutdownMargin;
            if (host.ShutdownTimeout < needed)
            {
                host.ShutdownTimeout = needed;
            }
        });

        services.AddRequestTimeouts(timeouts =>
        {
            timeouts.AddPolicy("TimerRead", new RequestTimeoutPolicy
            {
                Timeout = options.RequestTimeout,
                WriteTimeoutResponse = context => context.Response.WriteAsJsonAsync(new ApiErrorDto
                {
                    Code = "request_timeout",
                    Message = "The request deadline elapsed.",
                    RequestId = context.TraceIdentifier,
                }, BackendJsonContext.Default.ApiErrorDto),
            });
            timeouts.AddPolicy("TimerHealth", TimeSpan.FromSeconds(5));
        });
        services.AddOutputCache(outputCache =>
        {
            outputCache.SizeLimit = 100 * 1024 * 1024;
            outputCache.MaximumBodySize = 64 * 1024 * 1024;
            // Request locking would hand the first request's exception to every coalesced
            // waiter, so one client disconnect or timeout would fail them all with 500.
            outputCache.AddPolicy("TimerLeaderboard", policy =>
                policy.Expire(leaderboardCacheDuration ?? TimeSpan.FromSeconds(15))
                      .SetLocking(false)
                      // Key only on parameters the handlers read. The default varies on every
                      // query parameter, so ?x=1, ?x=2, ... each missed and ran a 5,000-row query.
                      .SetVaryByQuery("style", "track", "stage", "limit"));
        });
    }
}
