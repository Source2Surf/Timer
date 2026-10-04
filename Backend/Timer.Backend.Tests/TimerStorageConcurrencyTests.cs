using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

// Several game servers reading and writing one map through the backend at the same time, each on its own connection.
public sealed class TimerStorageConcurrencyTests
{
    private const int Servers          = 4;
    private const int PlayersPerServer = 6;
    private const int RunsPerPlayer    = 3;
    private const int SessionsPerServer = 10;
    private const int VisitsPerPlayer  = 5;
    private const int ZoneSavesPerServer = 5;

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task ConcurrentGameServersOnMySql()
        => ConcurrentGameServersAsync("mysql", Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")!);

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task ConcurrentGameServersOnPostgreSql()
        => ConcurrentGameServersAsync("postgresql", Environment.GetEnvironmentVariable("TIMER_TEST_POSTGRES")!);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task ConcurrentRequestsAgreeOnARenamedWorkshopMapOnMySql()
        => RenamedWorkshopMapAsync("mysql", Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")!);

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task ConcurrentRequestsAgreeOnARenamedWorkshopMapOnPostgreSql()
        => RenamedWorkshopMapAsync("postgresql", Environment.GetEnvironmentVariable("TIMER_TEST_POSTGRES")!);

    private static async Task ConcurrentGameServersAsync(string databaseType, string connectionString)
    {
        using var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.None));
        using var storage = TimerBackendStorageFactory.Create(databaseType, connectionString, loggerFactory);
        storage.Start(initializeSchema: true, allowReadRepair: false);
        var (app, firstChannel, _) = await TimerStorageLoopbackTests.StartAsync(storage, explicitStyleFactors: true);
        await using var _ = app;
        firstChannel.Dispose();

        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var servers = Enumerable.Range(0, Servers).Select(_ => new GameServer(address)).ToArray();

        try
        {
            var map = $"surf_conc_{Guid.NewGuid():N}";
            var mapId = (await servers[0].Storage.GetMapInfoAsync(map, 0)).MapId;
            var players = new long[Servers, PlayersPerServer];
            var best = new ConcurrentDictionary<long, long>();

            await Task.WhenAll(Enumerable.Range(0, Servers).SelectMany(s => Enumerable.Range(0, PlayersPerServer).Select(async p =>
            {
                var steamId = 76_561_198_000_000_000L + Random.Shared.NextInt64(1, 1_000_000_000);
                players[s, p] = steamId;
                await servers[s].Writes.EnsurePlayerProfileAsync(new EnsurePlayerProfileRequest { SteamId = steamId, Name = $"p{s}_{p}" });
            })));

            var writing   = new CancellationTokenSource();
            var tornZones = 0;
            var reads     = 0;

            // Readers keep reading while every write below runs.
            var readers = servers.Select(server => Task.Run(async () =>
            {
                while (!writing.IsCancellationRequested)
                {
                    await server.Storage.GetMapRecordsAsync(map, 0, RunKind.Main, true, 0, 0, 0, 5000);
                    await server.Storage.GetMapInfoAsync(map, 0);
                    await server.Storage.GetPlayerPointsRankAsync((ulong)players[0, 0]);

                    if (!IsCompleteSnapshot(await server.Storage.GetZonesAsync(map, 0)))
                    {
                        Interlocked.Increment(ref tornZones);
                    }

                    Interlocked.Increment(ref reads);
                }
            })).ToArray();

            var writes = new List<Task>();

            for (var s = 0; s < Servers; s++)
            {
                var server = servers[s];
                var marker = s;

                for (var p = 0; p < PlayersPerServer; p++)
                {
                    var steamId = players[s, p];

                    writes.Add(Task.Run(async () =>
                    {
                        for (var run = 0; run < RunsPerPlayer; run++)
                        {
                            var timeMicros = Random.Shared.NextInt64(60_000_000, 90_000_000);
                            await server.Writes.SubmitRunAsync(Submission(map, steamId, timeMicros));
                            best.AddOrUpdate(steamId, timeMicros, (_, current) => Math.Min(current, timeMicros));
                        }
                    }));

                    writes.Add(Task.Run(async () =>
                    {
                        for (var visit = 0; visit < VisitsPerPlayer; visit++)
                        {
                            await server.Storage.UpdatePlayerMapStatsAsync((ulong)steamId, map, 0, 1);
                        }
                    }));
                }

                writes.Add(Task.Run(async () =>
                {
                    for (var session = 0; session < SessionsPerServer; session++)
                    {
                        await server.Storage.IncrementMapStatsAsync(map, 0, 1);
                    }
                }));

                writes.Add(Task.Run(async () =>
                {
                    for (var save = 0; save < ZoneSavesPerServer; save++)
                    {
                        await server.Storage.SaveZonesAsync(map, 0, Snapshot(marker));
                    }
                }));
            }

            try
            {
                await Task.WhenAll(writes);
            }
            finally
            {
                writing.Cancel();
                await Task.WhenAll(readers);
            }

            Assert.True(reads > 0);
            Assert.Equal(0, tornZones);
            Assert.True(IsCompleteSnapshot(await servers[0].Storage.GetZonesAsync(map, 0)));

            var info = await servers[1].Storage.GetMapInfoAsync(map, 0);
            Assert.Equal(mapId, info.MapId);
            Assert.Equal(Servers * SessionsPerServer, info.PlayCount);
            Assert.Equal(Servers * SessionsPerServer, info.TotalPlayTime, 3);

            var board = await servers[2].Storage.GetMapRecordsAsync(map, 0, RunKind.Main, false, 0, 0, 0, 5000);
            Assert.Equal(Servers * PlayersPerServer, board.Length);
            Assert.Equal(best.Count, board.Select(x => x.SteamId).Distinct().Count());

            foreach (var record in board)
            {
                // MySQL hands FLOAT back rounded to 6 significant digits.
                var expected = best[(long)record.SteamId] / 1_000_000d;
                Assert.InRange(record.Time, expected - 0.001, expected + 0.001);
            }

            for (var s = 0; s < Servers; s++)
            {
                for (var p = 0; p < PlayersPerServer; p++)
                {
                    var stats = await servers[3].Storage.GetPlayerMapStatsAsync((ulong)players[s, p], map, 0);
                    Assert.Equal(VisitsPerPlayer, stats.PlayCount);
                }
            }
        }
        finally
        {
            foreach (var server in servers)
            {
                server.Dispose();
            }
        }
    }

    private static async Task RenamedWorkshopMapAsync(string databaseType, string connectionString)
    {
        using var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.None));
        using var storage = TimerBackendStorageFactory.Create(databaseType, connectionString, loggerFactory);
        storage.Start(initializeSchema: true, allowReadRepair: false);
        var (app, firstChannel, client) = await TimerStorageLoopbackTests.StartAsync(storage, explicitStyleFactors: true);
        await using var _ = app;
        using var __ = firstChannel;

        var suffix     = Guid.NewGuid().ToString("N");
        var workshopId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        var original   = await client.GetMapInfoAsync($"surf_ws_old_{suffix}", workshopId);
        var renamed    = $"surf_ws_new_{suffix}";

        // A whole map load's worth of requests for the renamed map, all at once.
        var mapIds = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i => (i % 3) switch
        {
            0 => (await client.GetMapInfoAsync(renamed, workshopId)).MapId,
            1 => (await client.GetZonesAsync(renamed, workshopId)).Length == 0 ? original.MapId : 0,
            _ => (await client.GetMapRecordsAsync(renamed, workshopId, RunKind.Main, true, 0, 0, 0, 10)).Length == 0 ? original.MapId : 0,
        }));

        Assert.All(mapIds, id => Assert.Equal(original.MapId, id));
        var rows = (await client.GetMapProfilesAsync()).Where(x => x.WorkshopId == workshopId).ToArray();
        Assert.Equal(renamed, Assert.Single(rows).MapName);
    }

    private static ZoneDto[] Snapshot(int marker)
        => Enumerable.Range(0, marker + 1)
                     .Select(i => new ZoneDto
                     {
                         Type = 1, Track = 0, Sequence = marker,
                         Mins = new VectorDto { X = i }, Maxs = new VectorDto { X = i + 1 }, Center = new VectorDto(),
                     })
                     .ToArray();

    // A snapshot is all zones from one save: every zone carries its saver's marker, and there are marker + 1 of them.
    private static bool IsCompleteSnapshot(ZoneDto[] zones)
        => zones.Length == 0
           || (zones.All(x => x.Sequence == zones[0].Sequence) && zones.Length == zones[0].Sequence + 1);

    private static SubmitRunRequest Submission(string map, long steamId, long timeMicros)
        => new ()
        {
            SubmissionId                   = Guid.NewGuid(),
            SteamId                        = steamId,
            MapName                        = map,
            RunKind                        = RunKind.Main,
            TimeMicros                     = timeMicros,
            Jumps                          = 5,
            Strafes                        = 10,
            Sync                           = 90,
            FinishedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ContractVersion                = 1,
            RulesetVersion                 = 1,
        };

    private sealed class GameServer : IDisposable
    {
        private readonly GrpcChannel _channel;

        public GameServer(Uri address)
        {
            _channel = GrpcChannel.ForAddress(address);
            Writes   = MagicOnionClient.Create<ITimerWriteServiceV1>(_channel);
            Storage  = MagicOnionClient.Create<ITimerStorageServiceV1>(_channel);
        }

        public ITimerWriteServiceV1   Writes  { get; }
        public ITimerStorageServiceV1 Storage { get; }

        public void Dispose()
            => _channel.Dispose();
    }

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string environment)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environment)))
            {
                Skip = $"Set {environment} to a disposable test database.";
            }
        }
    }
}
