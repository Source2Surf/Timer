/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
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
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer.Extensions;

internal static class RetryHelper
{
    /// <summary>
    /// Retry an async operation with exponential backoff.
    /// Only retries on exceptions matching the <paramref name="shouldRetry"/> predicate.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>>         action,
        Func<Exception, bool> shouldRetry,
        ILogger               logger,
        string                operationName,
        int                   maxRetries      = 3,
        int                   baseDelayMs     = 500,
        CancellationToken     cancellationToken = default)
    {
        for (var attempt = 0;; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && shouldRetry(ex))
            {
                var delay = baseDelayMs * (1 << attempt); // 500, 1000, 2000, ...

                logger.LogWarning(ex,
                    "{Operation} failed on attempt {Attempt}/{MaxRetries}, retrying in {Delay}ms",
                    operationName, attempt + 1, maxRetries, delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retry an async void operation with exponential backoff.
    /// </summary>
    public static async Task RetryAsync(
        Func<Task>            action,
        Func<Exception, bool> shouldRetry,
        ILogger               logger,
        string                operationName,
        int                   maxRetries      = 3,
        int                   baseDelayMs     = 500,
        CancellationToken     cancellationToken = default)
    {
        await RetryAsync(
            async () => { await action().ConfigureAwait(false); return 0; },
            shouldRetry,
            logger,
            operationName,
            maxRetries,
            baseDelayMs,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true for transient exceptions that are worth retrying
    /// (network errors, timeouts, transient DB failures).
    /// Does NOT retry <see cref="OperationCanceledException"/>.
    /// </summary>
    public static bool IsTransient(Exception ex) =>
        ex is TimeoutException
            or System.Net.Http.HttpRequestException
            or System.IO.IOException
        || (ex is not OperationCanceledException
            && ex.InnerException is not null
            && IsTransient(ex.InnerException));
}
