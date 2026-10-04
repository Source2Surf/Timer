/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using MagicOnion.Client;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Managers.Request;

namespace Source2Surf.Timer.Managers.Submission;

/// <summary>
/// Narrow transport seam around the generated MagicOnion service. It keeps run submission and
/// player-profile calls on one channel while making sender behavior testable.
/// </summary>
internal interface IRunSubmissionTransport : IDisposable
{
    Task<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request,
                                                                CancellationToken          cancellationToken);

    Task<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request, CancellationToken cancellationToken);

    Task<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request,
                                                                CancellationToken               cancellationToken);
}

internal interface IRunSubmissionTransportFactory
{
    IRunSubmissionTransport Create();
}

internal sealed class MagicOnionRunSubmissionTransportFactory : IRunSubmissionTransportFactory
{
    private readonly BackendChannel _channel;

    public MagicOnionRunSubmissionTransportFactory(BackendChannel channel)
        => _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    public IRunSubmissionTransport Create()
        => new MagicOnionRunSubmissionTransport(_channel);
}

/// <summary>
/// One MagicOnion proxy on the shared <see cref="BackendChannel"/>, which outlives it; this class never
/// creates a channel for an individual submission.
/// </summary>
internal sealed class MagicOnionRunSubmissionTransport : IRunSubmissionTransport
{
    private readonly MagicOnionClientBase<ITimerWriteServiceV1> _clientBase;
    private int                                                 _disposed;

    public MagicOnionRunSubmissionTransport(BackendChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _clientBase = channel.CreateClient<ITimerWriteServiceV1>() as MagicOnionClientBase<ITimerWriteServiceV1>
                      ?? throw new InvalidOperationException(
                          "MagicOnion did not return a client proxy with call-option support.");
    }

    public async Task<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var client = GetClientWithCancellation(cancellationToken);
        return await client.SubmitRunAsync(request).ConfigureAwait(false);
    }

    public async Task<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request,
                                                                              CancellationToken          cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var client = GetClientWithCancellation(cancellationToken);
        return await client.EnsurePlayerProfileAsync(request).ConfigureAwait(false);
    }

    public async Task<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request,
                                                                              CancellationToken               cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var client = GetClientWithCancellation(cancellationToken);
        return await client.GetSubmissionStatusAsync(request).ConfigureAwait(false);
    }

    public void Dispose()
        => Interlocked.Exchange(ref _disposed, 1);

    private ITimerWriteServiceV1 GetClientWithCancellation(CancellationToken cancellationToken)
    {
        // WithCancellationToken clones only the MagicOnion proxy/call options, not the shared channel.
        return _clientBase.WithCancellationToken(cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MagicOnionRunSubmissionTransport));
        }
    }
}
