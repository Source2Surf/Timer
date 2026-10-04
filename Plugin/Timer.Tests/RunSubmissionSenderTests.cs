using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Managers.Submission;
using Xunit;

namespace Timer.Tests;

public sealed class RunSubmissionSenderTests : IDisposable
{
    private RunSubmissionSender? _sender;
    private RunSubmissionSpool? _spool;

    [Fact]
    public async Task EnqueueAndWaitAcknowledgesOnlyAfterACanonicalSubmitResponse()
    {
        var transport = new FakeTransport
        {
            Submit = (request, _) => Task.FromResult(Canonical(request.SubmissionId)),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        var response = await sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()))
                                   .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SubmissionDisposition.Accepted, response.Disposition);
        Assert.Equal(1, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task AmbiguousSubmitRetriesOriginalPayloadBeforeAcknowledging()
    {
        var submissionId = Guid.NewGuid();
        var attempts = 0;
        var transport = new FakeTransport
        {
            Submit = (request, _) => ++attempts == 1
                ? throw new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline"))
                : Task.FromResult(Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied)),
            Status = (request, _) => Task.FromResult(new GetSubmissionStatusResponse
            {
                Found = true,
                Submission = Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied),
            }),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        var response = await sender.EnqueueAndWaitAsync(CreateRequest(submissionId))
                                   .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(submissionId, response.SubmissionId);
        Assert.Equal(SubmissionDisposition.AlreadyApplied, response.Disposition);
        Assert.Equal(2, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), GetSpool().GetSnapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownResultEnumsAreNotAcknowledgedBeforePayloadReplay(bool unknownAttemptResult)
    {
        var attempts = 0;
        var transport = new FakeTransport
        {
            Submit = (request, _) =>
            {
                if (++attempts > 1)
                {
                    return Task.FromResult(Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied));
                }
                var response = Canonical(request.SubmissionId);
                if (unknownAttemptResult)
                {
                    response.AttemptResult = (AttemptResult)255;
                }
                else
                {
                    response.RankState = (RankState)255;
                }

                return Task.FromResult(response);
            },
            Status = (request, _) => Task.FromResult(new GetSubmissionStatusResponse
            {
                Found = true,
                Submission = Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied),
            }),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        var response = await sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()))
                                   .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SubmissionDisposition.AlreadyApplied, response.Disposition);
        Assert.Equal(2, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), GetSpool().GetSnapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnprojectableRankOrRunIdRequiresPayloadReplay(bool invalidRank)
    {
        var attempts = 0;
        var transport = new FakeTransport
        {
            Submit = (request, _) =>
            {
                if (++attempts > 1)
                {
                    return Task.FromResult(Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied));
                }
                var response = Canonical(request.SubmissionId);
                if (invalidRank)
                {
                    response.Rank = -1;
                }
                else
                {
                    response.RunId = (ulong)long.MaxValue + 1;
                }

                return Task.FromResult(response);
            },
            Status = (request, _) => Task.FromResult(new GetSubmissionStatusResponse
            {
                Found = true,
                Submission = Canonical(request.SubmissionId, SubmissionDisposition.AlreadyApplied),
            }),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        var response = await sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()))
                                   .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SubmissionDisposition.AlreadyApplied, response.Disposition);
        Assert.Equal(2, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), GetSpool().GetSnapshot());
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.AlreadyExists)]
    public async Task PermanentRpcFailureQuarantinesAndFailsTheLiveWaiter(StatusCode statusCode)
    {
        var transport = new FakeTransport
        {
            Submit = (_, _) => throw new RpcException(new Status(statusCode, "permanent")),
        };
        var sender = CreateSender(transport);
        var request = CreateRequest(Guid.NewGuid());

        Assert.True(sender.Init());

        var exception = await Assert.ThrowsAsync<RunSubmissionRejectedException>(
                            () => sender.EnqueueAndWaitAsync(request));

        Assert.Equal(request.SubmissionId, exception.SubmissionId);
        Assert.Equal(1, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(1, 0, 1), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task NotFoundRemainsQueuedForPlayerOrMapProvisioning()
    {
        var transport = new FakeTransport
        {
            Submit = (_, _) => throw new RpcException(new Status(StatusCode.NotFound, "profile pending")),
            Status = (_, _) => Task.FromResult(new GetSubmissionStatusResponse { Found = false }),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                                             cancellation.Token));

        await WaitForAsync(() => transport.SubmitCalls > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, transport.SubmitCalls);
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task NotFoundLongAfterTheFinishIsQuarantined()
    {
        var transport = new FakeTransport
        {
            Submit = (_, _) => throw new RpcException(new Status(StatusCode.NotFound, "map never provisioned")),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        var finishedLongAgo = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        await Assert.ThrowsAsync<RunSubmissionRejectedException>(
            () => sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid(), finishedLongAgo)).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new SubmissionSpoolSnapshot(1, 0, 1), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task ProxyAuthFailuresStayPermanent()
    {
        var transport = new FakeTransport
        {
            Submit = (_, _) => throw new RpcException(new Status(StatusCode.PermissionDenied, "Bad gRPC response. HTTP status code: 403")),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        await Assert.ThrowsAsync<RunSubmissionRejectedException>(
            () => sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid())).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(StatusCode.Unimplemented, 404)]
    public async Task HttpErrorsFromAProxyAreRetriedNotQuarantined(StatusCode statusCode, int httpStatus)
    {
        var transport = new FakeTransport
        {
            // Grpc.Net.Client's wording when a non-gRPC HTTP response carries no grpc-status.
            Submit = (_, _) => throw new RpcException(new Status(statusCode, $"Bad gRPC response. HTTP status code: {httpStatus}")),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()), cancellation.Token));

        await WaitForAsync(() => transport.SubmitCalls > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task ShutdownGivesARunWaitingOutABackoffOneFinalAttempt()
    {
        var online = 0;
        var offlineFailures = 0;
        var transport = new FakeTransport
        {
            Submit = (request, _) =>
            {
                if (Volatile.Read(ref online) == 0)
                {
                    Interlocked.Increment(ref offlineFailures);
                    throw new RpcException(new Status(StatusCode.Unavailable, "backend offline"));
                }

                return Task.FromResult(Canonical(request.SubmissionId));
            },
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());
        _ = sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()));
        // Wait for the first attempt to have actually failed, not merely started.
        await WaitForAsync(() => Volatile.Read(ref offlineFailures) == 1, TimeSpan.FromSeconds(2));

        // The backend recovers while the entry is still inside its retry backoff.
        Volatile.Write(ref online, 1);
        sender.Shutdown();

        Assert.Equal(2, transport.SubmitCalls);
    }

    [Fact]
    public async Task CancellingTheWaiterLeavesAnAmbiguousSubmissionInMemoryQueued()
    {
        var transport = new FakeTransport
        {
            Submit = (_, _) => throw new RpcException(new Status(StatusCode.Unavailable, "offline")),
            Status = (_, _) => Task.FromResult(new GetSubmissionStatusResponse { Found = false }),
        };
        var sender = CreateSender(transport);

        Assert.True(sender.Init());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.EnqueueAndWaitAsync(CreateRequest(Guid.NewGuid()), cancellation.Token));

        await WaitForAsync(() => transport.SubmitCalls > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(0, transport.StatusCalls);
        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), GetSpool().GetSnapshot());
    }

    [Fact]
    public async Task BackendReconnectResendsTheSameQueuedSubmissionIdAndEventuallyAcknowledges()
    {
        var online = 0;
        var offlineFailures = 0;
        var seenIds = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        var transport = new FakeTransport
        {
            Submit = (request, _) =>
            {
                seenIds.Enqueue(request.SubmissionId);
                if (Volatile.Read(ref online) == 0)
                {
                    Interlocked.Increment(ref offlineFailures);
                    throw new RpcException(new Status(StatusCode.Unavailable, "backend offline"));
                }

                return Task.FromResult(Canonical(request.SubmissionId));
            },
            Status = (_, _) => Task.FromResult(new GetSubmissionStatusResponse { Found = false }),
        };
        var sender = CreateSender(transport);
        var submissionId = Guid.NewGuid();

        Assert.True(sender.Init());
        var pending = sender.EnqueueAndWaitAsync(CreateRequest(submissionId));

        await WaitForAsync(() => Volatile.Read(ref offlineFailures) >= 1
                                 && GetSpool().GetSnapshot() == new SubmissionSpoolSnapshot(1, 1, 0),
                           TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), GetSpool().GetSnapshot());

        Volatile.Write(ref online, 1);
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(submissionId, response.SubmissionId);
        Assert.True(seenIds.Count >= 2);
        Assert.All(seenIds, id => Assert.Equal(submissionId, id));
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), GetSpool().GetSnapshot());
    }

    [Fact]
    public void SenderCreatesExactlyOneTransportForItsLifecycle()
    {
        var transport = new FakeTransport
        {
            Submit = (request, _) => Task.FromResult(Canonical(request.SubmissionId)),
        };
        var factory = new FakeTransportFactory(transport);
        var sender = CreateSender(factory);

        Assert.True(sender.Init());
        Assert.True(sender.Init());

        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public void MagicOnionTransportCreatesOneReusableClientWithoutConnecting()
    {
        using var channel   = new BackendChannel(BackendOptions.CreateForTests(new Uri("https://127.0.0.1:65535")));
        using var transport = new MagicOnionRunSubmissionTransport(channel);

        Assert.IsAssignableFrom<IRunSubmissionTransport>(transport);
    }

    [Fact]
    public void EnabledSenderRefusesToStartWhenALegacyQueueMayExist()
    {
        var transport = new FakeTransport();
        var factory   = new FakeTransportFactory(transport);
        _spool = new RunSubmissionSpool(NullLogger<RunSubmissionSpool>.Instance,
                                        legacyDatabasePath: "legacy-run-submissions.db",
                                        legacyDatabaseExists: _ => true);
        _sender = new RunSubmissionSender(_spool,
                                          BackendOptions.CreateForTests(new Uri("https://timer.test")),
                                          factory,
                                          CancellationToken.None,
                                          NullLogger<RunSubmissionSender>.Instance);

        Assert.False(_sender.Init());
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task EnsurePlayerProfileUsesTheSameTransportLifecycleAndDeadline()
    {
        var transport = new FakeTransport
        {
            EnsureProfile = (request, cancellationToken) =>
            {
                Assert.Equal(76561198000000001, request.SteamId);
                Assert.Equal("Profile Player", request.Name);
                Assert.True(cancellationToken.CanBeCanceled);
                return Task.FromResult(new EnsurePlayerProfileResponse
                {
                    PlayerId                       = 42,
                    SteamId                        = request.SteamId,
                    Name                           = request.Name,
                    Points                         = 123,
                    JoinDateUnixTimeMilliseconds   = 1_725_000_000_000,
                    LastSeenDateUnixTimeMilliseconds = 1_725_000_000_001,
                });
            },
        };
        var factory = new FakeTransportFactory(transport);
        var sender  = CreateSender(factory);

        Assert.True(sender.Init());

        var response = await sender.EnsurePlayerProfileAsync(76561198000000001, "Profile Player")
                                   .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal((long)76561198000000001, response.SteamId);
        Assert.Equal("Profile Player", response.Name);
        Assert.Equal(1, transport.EnsureProfileCalls);
        Assert.Equal(0, transport.SubmitCalls);
        Assert.Equal(1, factory.CreateCalls);
    }

    public void Dispose()
    {
        _sender?.Dispose();
    }

    private RunSubmissionSender CreateSender(FakeTransport transport)
        => CreateSender(new FakeTransportFactory(transport));

    private RunSubmissionSender CreateSender(FakeTransportFactory factory)
    {
        _spool = new RunSubmissionSpool(NullLogger<RunSubmissionSpool>.Instance);
        _sender = new RunSubmissionSender(_spool,
                                          BackendOptions.CreateForTests(new Uri("https://timer.test")),
                                          factory,
                                          CancellationToken.None,
                                          NullLogger<RunSubmissionSender>.Instance);
        return _sender;
    }

    private RunSubmissionSpool GetSpool()
        => Assert.IsType<RunSubmissionSpool>(_spool);

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected background sender action did not occur.");
            }

            await Task.Delay(10);
        }
    }

    private static SubmitRunRequest CreateRequest(Guid submissionId, long finishedAtUnixTimeMilliseconds = 1_725_000_000_000)
    {
        return new SubmitRunRequest
        {
            SubmissionId                  = submissionId,
            SteamId                       = 76561198000000001,
            MapName                       = "surf_sender",
            RunKind                       = RunKind.Main,
            Style                         = 0,
            Track                         = 0,
            Stage                         = 0,
            TimeMicros                    = 12_345_678,
            Motion                        = new MotionDto(),
            Checkpoints                   = [],
            FinishedAtUnixTimeMilliseconds = finishedAtUnixTimeMilliseconds,
            ContractVersion                = 1,
            RulesetVersion                 = 1,
        };
    }

    private static SubmitRunResponse Canonical(Guid submissionId,
                                                SubmissionDisposition disposition = SubmissionDisposition.Accepted)
    {
        return new SubmitRunResponse
        {
            SubmissionId                  = submissionId,
            RunId                         = 42,
            AttemptResult                 = AttemptResult.NewPersonalRecord,
            Rank                          = 2,
            ReceivedAtUnixTimeMilliseconds = 1_725_000_000_100,
            Disposition                   = disposition,
            RankState                     = RankState.Ready,
        };
    }

    private sealed class FakeTransportFactory : IRunSubmissionTransportFactory
    {
        private readonly IRunSubmissionTransport _transport;

        public FakeTransportFactory(IRunSubmissionTransport transport)
            => _transport = transport;

        public int CreateCalls { get; private set; }

        public IRunSubmissionTransport Create()
        {
            CreateCalls++;
            return _transport;
        }
    }

    private sealed class FakeTransport : IRunSubmissionTransport
    {
        public Func<EnsurePlayerProfileRequest, CancellationToken, Task<EnsurePlayerProfileResponse>> EnsureProfile { get; init; }
            = (_, _) => throw new InvalidOperationException("EnsurePlayerProfile was not configured.");

        public Func<SubmitRunRequest, CancellationToken, Task<SubmitRunResponse>> Submit { get; init; }
            = (_, _) => throw new InvalidOperationException("Submit was not configured.");

        public Func<GetSubmissionStatusRequest, CancellationToken, Task<GetSubmissionStatusResponse>> Status { get; init; }
            = (_, _) => throw new InvalidOperationException("Status was not configured.");

        public int SubmitCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public int EnsureProfileCalls { get; private set; }

        public Task<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request,
                                                                           CancellationToken          cancellationToken)
        {
            EnsureProfileCalls++;
            return EnsureProfile(request, cancellationToken);
        }

        public Task<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Submit(request, cancellationToken);
        }

        public Task<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request,
                                                                            CancellationToken cancellationToken)
        {
            StatusCalls++;
            return Status(request, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
