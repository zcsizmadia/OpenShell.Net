// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.IntegrationTests;

/// <summary>Polls a condition that the gateway or supervisor reaches asynchronously, instead of sleeping a fixed time.</summary>
internal static class Poll
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Calls <paramref name="probe"/> until it returns a non-null value, and fails with <paramref name="what"/> and the
    /// last observation when <paramref name="timeout"/> elapses first.
    /// </summary>
    public static async Task<T> UntilAsync<T>(Func<Task<(T? Value, string Observed)>> probe, TimeSpan timeout, string what, CancellationToken cancellationToken)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (value, last) = await probe();
            if (value is not null)
            {
                return value;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"timed out after {timeout.TotalSeconds:0}s waiting for {what}; last observed: {last}");
            }

            await Task.Delay(Interval, cancellationToken);
        }
    }
}
