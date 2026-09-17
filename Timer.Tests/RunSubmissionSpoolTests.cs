using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Managers.Submission;
using Xunit;

namespace Timer.Tests;

public sealed class RunSubmissionSpoolTests
{
    [Fact]
    public void EmptySubmissionIdGetsAStableIdAndTheQueuedRequestIsDefensivelyCopied()
    {
        using var spool      = CreateSpool();
        var       submission = CreateRequest(Guid.Empty);

        var enqueue = spool.Enqueue(submission, TestNow);
        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, enqueue.Disposition);
        Assert.NotEqual(Guid.Empty, enqueue.SubmissionId);

        submission.TimeMicros              = 1;
        submission.Motion.MaxZ             = 999;
        submission.Checkpoints[0].Motion.EndY = 999;

        var lease = Assert.Single(spool.ClaimDueBatch(1, "generated-id", TestNow));
        Assert.Equal(enqueue.SubmissionId, lease.SubmissionId);
        Assert.Equal(enqueue.SubmissionId, lease.Request.SubmissionId);
        Assert.Equal(Guid.Empty, submission.SubmissionId);
        Assert.Equal(87_654_321, lease.Request.TimeMicros);
        Assert.Equal(3, lease.Request.Motion.MaxZ);
        Assert.Equal(6, lease.Request.Checkpoints[0].Motion.EndY);
    }

    [Fact]
    public void SameSubmissionIdWithDifferentPayloadIsRejectedWithoutReplacingTheQueuedRequest()
    {
        using var spool      = CreateSpool();
        var       submission = CreateRequest(Guid.NewGuid());
        var       conflicting = CreateRequest(submission.SubmissionId);
        conflicting.TimeMicros++;

        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(submission, TestNow).Disposition);
        Assert.Equal(SubmissionSpoolEnqueueDisposition.AlreadyQueued,
                     spool.Enqueue(CreateRequest(submission.SubmissionId), TestNow).Disposition);
        Assert.Equal(SubmissionSpoolEnqueueDisposition.ConflictingSubmissionId,
                     spool.Enqueue(conflicting, TestNow).Disposition);

        var lease = Assert.Single(spool.ClaimDueBatch(1, "same-id-conflict", TestNow));
        Assert.Equal(submission.TimeMicros, lease.Request.TimeMicros);
        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), spool.GetSnapshot());
    }

    [Fact]
    public void CapacityCountsQueuedAndPermanentlyQuarantinedEntries()
    {
        using var spool  = CreateSpool(capacity: 2);
        var       first  = CreateRequest(Guid.NewGuid());
        var       second = CreateRequest(Guid.NewGuid());
        var       third  = CreateRequest(Guid.NewGuid());

        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(first, TestNow).Disposition);
        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(second, TestNow).Disposition);
        var firstLease = Assert.Single(spool.ClaimDueBatch(1, "quarantine-first", TestNow));
        Assert.True(spool.Quarantine(firstLease.SubmissionId, firstLease.LeaseToken, "validation failure", TestNow));

        Assert.Equal(SubmissionSpoolEnqueueDisposition.CapacityExceeded, spool.Enqueue(third, TestNow).Disposition);
        Assert.Equal(new SubmissionSpoolSnapshot(2, 1, 1), spool.GetSnapshot());
    }

    [Theory]
    [InlineData("7fffffff-0000-0000-0000-000000000001", 1049)]
    [InlineData("80000000-0000-0000-0000-000000000001", 1050)]
    [InlineData("ffffffff-0000-0000-0000-000000000001", 1099)]
    public void RetryJitterHandlesTheFullUnsignedGuidRange(string submissionId, int dueAfterMilliseconds)
    {
        using var spool = CreateSpool();
        var request = CreateRequest(Guid.Parse(submissionId));
        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(request, TestNow).Disposition);
        var lease = Assert.Single(spool.ClaimDueBatch(1, "unsigned-guid", TestNow));

        Assert.True(spool.Retry(lease.SubmissionId, lease.LeaseToken, "Unavailable", TestNow));

        var dueAt = TestNow.AddMilliseconds(dueAfterMilliseconds);
        Assert.Empty(spool.ClaimDueBatch(1, "too-early", dueAt.AddTicks(-1)));
        Assert.Single(spool.ClaimDueBatch(1, "due", dueAt));
    }

    [Fact]
    public void TransientRetriesRemainEligibleBeyondTheFormerAttemptLimit()
    {
        using var spool      = CreateSpool(maximumAttempts: 1);
        var       submission = CreateRequest(Guid.NewGuid());
        var       retryNow   = TestNow;

        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(submission, retryNow).Disposition);

        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var lease = Assert.Single(spool.ClaimDueBatch(1, "retry-loop", retryNow, retryIndefinitely: true));
            Assert.Equal(attempt, lease.AttemptCount);
            Assert.True(spool.Retry(lease.SubmissionId, lease.LeaseToken, "gRPC Unavailable", retryNow));
            retryNow = retryNow.AddHours(1);
        }

        Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), spool.GetSnapshot());
    }

    [Fact]
    public void ExpiredClaimCannotAcknowledgeANewerLeaseEvenWithTheSameOwner()
    {
        using var spool   = CreateSpool();
        var       request = CreateRequest(Guid.NewGuid());
        Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued, spool.Enqueue(request, TestNow).Disposition);

        var first  = Assert.Single(spool.ClaimDueBatch(1, "same-owner", TestNow));
        var second = Assert.Single(spool.ClaimDueBatch(1, "same-owner", TestNow.AddMinutes(3)));

        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.False(spool.Acknowledge(request.SubmissionId, first.LeaseToken));
        Assert.True(spool.Acknowledge(request.SubmissionId, second.LeaseToken));
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), spool.GetSnapshot());
    }

    [Fact]
    public void ShutdownAndNewQueueLoseUnacknowledgedEntriesByDesign()
    {
        using (var first = CreateSpool())
        {
            Assert.Equal(SubmissionSpoolEnqueueDisposition.Enqueued,
                         first.Enqueue(CreateRequest(Guid.NewGuid()), TestNow).Disposition);
            Assert.Equal(new SubmissionSpoolSnapshot(1, 1, 0), first.GetSnapshot());
        }

        // A new plugin lifecycle uses a fresh queue only. No file is read to recover this entry.
        using var next = CreateSpool();
        Assert.Equal(new SubmissionSpoolSnapshot(0, 0, 0), next.GetSnapshot());
    }

    private static RunSubmissionSpool CreateSpool(int capacity = 1_024, int maximumAttempts = 8)
    {
        var spool = new RunSubmissionSpool(NullLogger<RunSubmissionSpool>.Instance, capacity, maximumAttempts);
        Assert.True(spool.Init());
        return spool;
    }

    private static SubmitRunRequest CreateRequest(Guid submissionId)
    {
        return new SubmitRunRequest
        {
            SubmissionId                  = submissionId,
            SteamId                       = 76561198000000001,
            MapName                       = "surf_spool",
            RunKind                       = RunKind.Stage,
            Style                         = 6,
            Track                         = 2,
            Stage                         = 3,
            TimeMicros                    = 87_654_321,
            Jumps                         = 42,
            Strafes                       = 17,
            Sync                          = 93.25f,
            Motion                        = new MotionDto { StartX = 1, AverageY = 2, MaxZ = 3, EndX = 4 },
            Checkpoints                   =
            [
                new CheckpointDto
                {
                    Index      = 7,
                    TimeMicros = 43_210_000,
                    Sync       = 87.5f,
                    Motion     = new MotionDto { StartY = 5, EndY = 6 },
                },
            ],
            FinishedAtUnixTimeMilliseconds = 1_725_000_000_000,
            ContractVersion                = 1,
            RulesetVersion                 = 4,
        };
    }

    private static readonly DateTime TestNow = new (2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
}
