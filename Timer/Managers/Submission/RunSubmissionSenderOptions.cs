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
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Source2Surf.Timer.Managers.Submission;

/// <summary>
/// Immutable plugin-side configuration for the in-memory write sender. A configured endpoint
/// enables it unless <c>Enabled=false</c> is explicitly set; an absent endpoint stays disabled.
/// </summary>
internal sealed class RunSubmissionSenderOptions
{
    internal const string SectionName = "Timer:RunSubmissionSender";

    private const int DefaultBatchSize = 16;

    internal static RunSubmissionSenderOptions Disabled { get; } = new (enabled: false,
                                                                         endpoint: null,
                                                                         rpcDeadline: TimeSpan.FromSeconds(10),
                                                                         pollInterval: TimeSpan.FromMilliseconds(250),
                                                                         shutdownDrainTimeout: TimeSpan.FromSeconds(15),
                                                                         batchSize: DefaultBatchSize,
                                                                         allowInsecureLoopback: false);

    public bool Enabled { get; }

    public Uri? Endpoint { get; }

    public TimeSpan RpcDeadline { get; }

    public TimeSpan PollInterval { get; }

    public TimeSpan ShutdownDrainTimeout { get; }

    public int BatchSize { get; }

    /// <summary>
    /// Legacy compatibility setting. HTTP/2 cleartext endpoints no longer require this switch.
    /// </summary>
    public bool AllowInsecureLoopback { get; }

    private RunSubmissionSenderOptions(bool     enabled,
                                       Uri?     endpoint,
                                       TimeSpan rpcDeadline,
                                       TimeSpan pollInterval,
                                       TimeSpan shutdownDrainTimeout,
                                       int      batchSize,
                                       bool     allowInsecureLoopback)
    {
        Enabled                   = enabled;
        Endpoint                  = endpoint;
        RpcDeadline               = rpcDeadline;
        PollInterval              = pollInterval;
        ShutdownDrainTimeout      = shutdownDrainTimeout;
        BatchSize                 = batchSize;
        AllowInsecureLoopback     = allowInsecureLoopback;
    }

    public static RunSubmissionSenderOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        ValidateKnownSettings(section);

        var enabled                   = string.IsNullOrWhiteSpace(section["Enabled"])
                                            ? !string.IsNullOrWhiteSpace(section["Endpoint"])
                                            : ParseBoolean(section["Enabled"], "Enabled");
        var allowInsecureLoopback     = ParseBoolean(section["AllowInsecureLoopback"], "AllowInsecureLoopback");
        var rpcDeadline               = ParseMilliseconds(section["RpcDeadlineMilliseconds"],
                                                            "RpcDeadlineMilliseconds",
                                                            TimeSpan.FromSeconds(10),
                                                            minimum: 100,
                                                            maximum: 300_000);
        var pollInterval              = ParseMilliseconds(section["PollIntervalMilliseconds"],
                                                            "PollIntervalMilliseconds",
                                                            TimeSpan.FromMilliseconds(250),
                                                            minimum: 10,
                                                            maximum: 60_000);
        var shutdownDrainTimeout      = ParseMilliseconds(section["ShutdownDrainTimeoutMilliseconds"],
                                                            "ShutdownDrainTimeoutMilliseconds",
                                                            TimeSpan.FromSeconds(15),
                                                            minimum: 100,
                                                            maximum: 300_000);
        var batchSize                 = ParseInteger(section["BatchSize"],
                                                     "BatchSize",
                                                     DefaultBatchSize,
                                                     minimum: 1,
                                                     maximum: 128);
        var endpoint                  = ParseEndpoint(section["Endpoint"], enabled);

        return new RunSubmissionSenderOptions(enabled,
                                               endpoint,
                                               rpcDeadline,
                                               pollInterval,
                                               shutdownDrainTimeout,
                                               batchSize,
                                               allowInsecureLoopback);
    }

    internal static RunSubmissionSenderOptions CreateForTests(Uri      endpoint,
                                                               TimeSpan? rpcDeadline = null,
                                                               TimeSpan? pollInterval = null,
                                                               TimeSpan? shutdownDrainTimeout = null,
                                                               int      batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return CreateEnabled(endpoint,
                             rpcDeadline ?? TimeSpan.FromSeconds(1),
                             pollInterval ?? TimeSpan.FromMilliseconds(10),
                             shutdownDrainTimeout ?? TimeSpan.FromSeconds(2),
                             batchSize,
                             allowInsecureLoopback: endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);
    }

    internal static RunSubmissionSenderOptions CreateEnabled(Uri      endpoint,
                                                              TimeSpan rpcDeadline,
                                                              TimeSpan pollInterval,
                                                              TimeSpan shutdownDrainTimeout,
                                                              int      batchSize,
                                                              bool     allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        ValidateDuration(rpcDeadline, nameof(rpcDeadline), minimumMilliseconds: 100, maximumMilliseconds: 300_000);
        ValidateDuration(pollInterval, nameof(pollInterval), minimumMilliseconds: 10, maximumMilliseconds: 60_000);
        ValidateDuration(shutdownDrainTimeout,
                         nameof(shutdownDrainTimeout),
                         minimumMilliseconds: 100,
                         maximumMilliseconds: 300_000);

        if (batchSize is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return new RunSubmissionSenderOptions(true,
                                               endpoint,
                                               rpcDeadline,
                                               pollInterval,
                                               shutdownDrainTimeout,
                                               batchSize,
                                               allowInsecureLoopback);
    }

    private static void ValidateKnownSettings(IConfigurationSection section)
    {
        foreach (var setting in section.GetChildren())
        {
            if (string.Equals(setting.Key, "Enabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "Endpoint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "RpcDeadlineMilliseconds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "PollIntervalMilliseconds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "ShutdownDrainTimeoutMilliseconds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "BatchSize", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setting.Key, "AllowInsecureLoopback", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new InvalidOperationException($"{SectionName}:{setting.Key} is not supported.");
        }
    }

    private static bool ParseBoolean(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw, out var value))
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be true or false.");
        }

        return value;
    }

    private static int ParseInteger(string? raw, string name, int defaultValue, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }

    private static TimeSpan ParseMilliseconds(string? raw,
                                              string  name,
                                              TimeSpan defaultValue,
                                              int     minimum,
                                              int     maximum)
    {
        var milliseconds = ParseInteger(raw,
                                        name,
                                        checked((int)defaultValue.TotalMilliseconds),
                                        minimum,
                                        maximum);

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static Uri? ParseEndpoint(string? raw, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (enabled)
            {
                throw new InvalidOperationException($"{SectionName}:Endpoint is required when enabled.");
            }

            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"{SectionName}:Endpoint must be an absolute URI.");
        }

        ValidateEndpoint(endpoint);
        return endpoint;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException($"{SectionName}:Endpoint must be a plain absolute service URI.");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SectionName}:Endpoint must use HTTP or HTTPS.");
    }

    private static void ValidateDuration(TimeSpan value,
                                         string   name,
                                         int      minimumMilliseconds,
                                         int      maximumMilliseconds)
    {
        var milliseconds = value.TotalMilliseconds;
        if (!double.IsFinite(milliseconds)
            || milliseconds < minimumMilliseconds
            || milliseconds > maximumMilliseconds)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
