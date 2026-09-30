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
using Grpc.Net.Client;
using MagicOnion.Client;
using Source2Surf.Timer.Backend.Rpc.Contracts;

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
    IRunSubmissionTransport Create(RunSubmissionSenderOptions options);
}

internal sealed class MagicOnionRunSubmissionTransportFactory : IRunSubmissionTransportFactory
{
    public IRunSubmissionTransport Create(RunSubmissionSenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MagicOnionRunSubmissionTransport(options);
    }
}

/// <summary>
/// One lifetime-owned gRPC channel and MagicOnion proxy. gRPC reconnects on its existing
/// channel; this class never creates a channel for an individual submission.
/// </summary>
internal sealed class MagicOnionRunSubmissionTransport : IRunSubmissionTransport
{
    private readonly GrpcChannel                                  _channel;
    private readonly MagicOnionClientBase<ITimerWriteServiceV1>    _clientBase;
    private int                                                    _disposed;

    public MagicOnionRunSubmissionTransport(RunSubmissionSenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || options.Endpoint is null)
        {
            throw new InvalidOperationException("The MagicOnion transport requires enabled sender options.");
        }

        _channel = GrpcChannel.ForAddress(options.Endpoint, CreateChannelOptions(options));

        var createdClient = MagicOnionClient.Create<ITimerWriteServiceV1>(_channel);
        _clientBase = createdClient as MagicOnionClientBase<ITimerWriteServiceV1>
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
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Dispose();
        }
    }

    private ITimerWriteServiceV1 GetClientWithCancellation(CancellationToken cancellationToken)
    {
        // WithCancellationToken clones only the MagicOnion proxy/call options; the
        // underlying GrpcChannel remains the single channel created in this constructor.
        return _clientBase.WithCancellationToken(cancellationToken);
    }

    private static GrpcChannelOptions CreateChannelOptions(RunSubmissionSenderOptions options)
    {
        if (options.Endpoint is not null && options.Endpoint.Scheme == Uri.UriSchemeHttp)
        {
            // MagicOnion 7.10.2 pins a Grpc.Net.Client target that does not expose per-channel
            // HTTP-version properties for this TFM. The documented h2c switch is global.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        return new GrpcChannelOptions();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MagicOnionRunSubmissionTransport));
        }
    }
}
