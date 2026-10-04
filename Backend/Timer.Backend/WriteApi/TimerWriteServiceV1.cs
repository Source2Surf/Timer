using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

/// <summary>
/// Default-off MagicOnion v1 write endpoint.
/// </summary>
public sealed class TimerWriteServiceV1 : ServiceBase<ITimerWriteServiceV1>, ITimerWriteServiceV1
{
    private readonly TimerBackendWriteStorage _storage;
    private readonly TimerWriteApiOptions _options;
    private readonly ILogger<TimerWriteServiceV1> _logger;
    private readonly TimerBackendRuntimeOptions _runtimeOptions;

    public TimerWriteServiceV1(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _storage = services.GetRequiredService<TimerBackendWriteStorage>();
        _options = services.GetRequiredService<TimerWriteApiOptions>();
        _logger = services.GetRequiredService<ILogger<TimerWriteServiceV1>>();
        _runtimeOptions = services.GetService<TimerBackendRuntimeOptions>() ?? new TimerBackendRuntimeOptions();
    }

    public async UnaryResult<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request)
    {
        using var deadline = CreateDeadline();
        try
        {
            var command = TimerWriteRpcMapper.ToPlayerProfileCommand(request);
            var result = await _storage.EnsurePlayerProfileAsync(command, deadline.Token);
            return TimerWriteRpcMapper.ToPlayerProfileResponse(result);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CancellationOrDeadline(deadline);
        }
        catch (Exception exception)
        {
            throw TimerWriteRpcErrors.Translate(exception, _logger);
        }
    }

    public async UnaryResult<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request)
    {
        using var deadline = CreateDeadline();
        try
        {
            var (command, acceptNewWrites) = TimerWriteRpcMapper.ToReplayAwareCommand(request, _options);
            var result = await _storage.SubmitRunAsync(command, acceptNewWrites, deadline.Token);
            return TimerWriteRpcMapper.ToResponse(result);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CancellationOrDeadline(deadline);
        }
        catch (Exception exception)
        {
            throw TimerWriteRpcErrors.Translate(exception, _logger);
        }
    }

    public async UnaryResult<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request)
    {
        using var deadline = CreateDeadline();
        try
        {
            if (request is null || request.SubmissionId == Guid.Empty)
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            var result = await _storage.GetSubmissionStatusAsync(request.SubmissionId, deadline.Token);
            return result is null
                ? new GetSubmissionStatusResponse { Found = false }
                : new GetSubmissionStatusResponse
                {
                    Found = true,
                    Submission = TimerWriteRpcMapper.ToResponse(result),
                };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CancellationOrDeadline(deadline);
        }
        catch (Exception exception)
        {
            throw TimerWriteRpcErrors.Translate(exception, _logger);
        }
    }

    private CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.CallContext.CancellationToken);
        deadline.CancelAfter(_runtimeOptions.RequestTimeout);
        return deadline;
    }

    private RpcException CancellationOrDeadline(CancellationTokenSource deadline)
        => deadline.IsCancellationRequested && !Context.CallContext.CancellationToken.IsCancellationRequested
            ? new RpcException(new Status(StatusCode.DeadlineExceeded, "The request deadline elapsed."))
            : TimerWriteRpcErrors.CancellationOrDeadline(Context.CallContext.Deadline);
}
