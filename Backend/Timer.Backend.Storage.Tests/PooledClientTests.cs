using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Sharp.Shared.Units;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class PooledClientTests
{
    [DatabaseFact("TIMER_TEST_MYSQL")]
    public async Task MySqlDroppedConnectionDoesNotBreakLaterOperations()
    {
        var target = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")!);
        Assert.True(target.Server is "127.0.0.1" or "localhost" && target.Database.Contains("test", StringComparison.OrdinalIgnoreCase),
                    "Requires a disposable loopback database with 'test' in its name.");
        var setup = new StorageServiceImpl(DbType.MySql, target.ConnectionString, NullLogger<StorageServiceImpl>.Instance, false);
        setup.Db.DbMaintenance.CreateDatabase();
        setup.Init(startScoreRecalcWorker: false);
        setup.Shutdown();

        using var proxy = new DroppingProxy((int)target.Port);
        // No reset round trip before reuse, so the drop lands in the middle of the command itself.
        var proxied = new MySqlConnectionStringBuilder(target.ConnectionString)
        {
            Server = "127.0.0.1", Port = (uint)proxy.Port, ConnectionReset = false,
        };
        var store = new StorageServiceImpl(DbType.MySql, proxied.ConnectionString, NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            var player = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000));
            var map = $"surf_dropped_{Guid.NewGuid():N}";
            await store.RunOperationAsync(() => store.UpdatePlayerMapStatsAsync(player, map, 5f), CancellationToken.None);

            // The connection is reset mid-command, as a network failure or server restart would.
            var drop = Task.Run(async () => { await Task.Delay(300); proxy.DropAll(); });
            await Assert.ThrowsAnyAsync<Exception>(() => store.RunOperationAsync(async () =>
            {
                proxy.Frozen = true;
                await store.UpdatePlayerMapStatsAsync(player, map, 5f);
            }, CancellationToken.None));
            await drop;

            for (var i = 0; i < 6; i++)
            {
                await store.RunOperationAsync(() => store.GetAllMapNamesAsync(), CancellationToken.None);
            }
        }
        finally
        {
            store.Shutdown();
        }
    }

    // Forwards loopback connections to the database; DropAll resets every open one.
    private sealed class DroppingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _sockets = new();
        private readonly int _target;
        public volatile bool Frozen;
        public int Port { get; }

        public DroppingProxy(int target)
        {
            _target = target;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient inbound;
                try { inbound = await _listener.AcceptTcpClientAsync(); }
                catch { return; }

                var outbound = new TcpClient();
                await outbound.ConnectAsync(IPAddress.Loopback, _target);
                _sockets.Add(inbound);
                _sockets.Add(outbound);
                _ = PumpAsync(inbound, outbound);
                _ = PumpAsync(outbound, inbound);
            }
        }

        private async Task PumpAsync(TcpClient from, TcpClient to)
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    var read = await from.GetStream().ReadAsync(buffer);
                    if (read == 0) return;
                    while (Frozen) await Task.Delay(20);
                    await to.GetStream().WriteAsync(buffer.AsMemory(0, read));
                }
            }
            catch
            {
                // A dropped socket ends its pump.
            }
        }

        public void DropAll()
        {
            while (_sockets.TryTake(out var socket))
            {
                try
                {
                    socket.Client.LingerState = new LingerOption(true, 0);
                    socket.Close();
                }
                catch
                {
                    // Already closed.
                }
            }

            Frozen = false;
        }

        public void Dispose()
        {
            DropAll();
            _listener.Stop();
        }
    }

    private sealed class DatabaseFactAttribute : FactAttribute
    {
        public DatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}
