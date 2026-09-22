using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Configuration;

namespace Timer.Backend.Infrastructure;

internal static class TimerBackendRuntimeRegistration
{
    internal static void Add(IServiceCollection services, TimerBackendRuntimeOptions options,
                              TimeSpan? leaderboardCacheDuration = null)
    {
        services.AddSingleton(options);
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
            outputCache.AddPolicy("TimerLeaderboard", policy =>
                policy.Expire(leaderboardCacheDuration ?? TimeSpan.FromSeconds(15)));
        });
    }
}
