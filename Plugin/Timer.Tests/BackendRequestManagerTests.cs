using System.Reflection;
using Grpc.Core;
using MagicOnion;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Request;
using Xunit;

namespace Timer.Tests;

public sealed class BackendRequestManagerTests
{
    private readonly ITimerStorageServiceV1 _client = DispatchProxy.Create<ITimerStorageServiceV1, RecordingClient>();
    private readonly BackendRequestManager  _requests;

    public BackendRequestManagerTests()
        => _requests = new BackendRequestManager(_client,
                                                 BackendOptions.CreateForTests(new Uri("http://127.0.0.1:5082"),
                                                                               rpcDeadline: TimeSpan.FromSeconds(5)),
                                                 CancellationToken.None);

    private RecordingClient Recorded => (RecordingClient)(object)_client;

    [Fact]
    public async Task OnlyTheBoundMapCarriesItsWorkshopItem()
    {
        _requests.SetMapWorkshopId("surf_ws", 123);

        await _requests.GetZonesAsync("SURF_WS");
        await _requests.GetZonesAsync("surf_other");
        _requests.SetMapWorkshopId("surf_ws", 0);
        await _requests.GetZonesAsync("surf_ws");

        Assert.Equal([123UL, 0UL, 0UL], Recorded.Calls.Select(x => (ulong)x.Args[1]!));
    }

    [Fact]
    public async Task EachLeaderboardOverloadAsksForItsShape()
    {
        await _requests.GetMapRecords("surf_a");
        await _requests.GetMapStageRecords("surf_a", 0, 1, 3, 10);

        var all   = Recorded.Calls[0].Args;
        var board = Recorded.Calls[1].Args;
        Assert.Equal((RunKind.Main, true), ((RunKind)all[2]!, (bool)all[3]!));
        Assert.Equal((RunKind.Stage, false, 1, 3, 10), ((RunKind)board[2]!, (bool)board[3]!, (int)board[5]!, (int)board[6]!, (int)board[7]!));
    }

    [Fact]
    public async Task StoredReplayLookupsAreChunked()
    {
        var ids = Enumerable.Range(1, 2500).Select(x => (ulong)x).ToList();

        await _requests.GetStoredReplayRunIdsAsync(ids);

        Assert.Equal([1000, 1000, 500], Recorded.Calls.Select(x => ((ulong[])x.Args[0]!).Length));
    }

    [Fact]
    public async Task EveryCallGetsTheDeadline()
    {
        var before = DateTime.UtcNow;

        await _requests.GetPlayerPointsRank(new SteamID(76561198000000001));

        var deadline = Assert.Single(Recorded.Options).Deadline!.Value;
        Assert.InRange(deadline, before.AddSeconds(5), DateTime.UtcNow.AddSeconds(5));
    }

    public class RecordingClient : DispatchProxy
    {
        public List<(string Method, object?[] Args)> Calls   { get; } = [];
        public List<CallOptions>                     Options { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITimerStorageServiceV1.WithOptions))
            {
                Options.Add((CallOptions)args![0]!);

                return this;
            }

            Calls.Add((method.Name, args ?? []));

            return method.ReturnType == typeof(UnaryResult)
                       ? UnaryResult.CompletedResult
                       : Activator.CreateInstance(method.ReturnType, Default(method.ReturnType.GetGenericArguments()[0]));
        }

        private static object? Default(Type type)
            => type == typeof(string)                ? null
             : type.IsArray                          ? Array.CreateInstance(type.GetElementType()!, 0)
             : type.IsValueType                      ? Activator.CreateInstance(type)
             : Activator.CreateInstance(type);
    }
}
