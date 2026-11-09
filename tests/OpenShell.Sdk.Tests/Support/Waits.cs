// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;

namespace OpenShell.Tests.Support;

/// <summary>
/// Bounded waits for tests. Every loop here is bounded by real (wall-clock) time, not by an iteration count, so a slow
/// runner gets the same patience as a fast one, and a wait that runs out fails with a message naming what it waited for.
/// </summary>
internal static class Waits
{
    /// <summary>How long any one wait may take in real time before the test fails.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Advances a fake clock by <paramref name="step"/> at a time until <paramref name="task"/> completes, then returns
    /// without observing the task's result (the caller awaits or asserts on it).
    /// </summary>
    /// <param name="time">The fake clock driving the code under test.</param>
    /// <param name="task">The operation to drive to completion.</param>
    /// <param name="step">How far to move the clock per step.</param>
    /// <param name="pauseBetweenSteps">
    /// Pause about a millisecond of real time between steps, so work running on other threads (an HTTP handler, a
    /// <c>Task.Run</c> consumer) keeps pace with the clock. Without it the pump only yields, which suits code whose
    /// continuations run inline.
    /// </param>
    /// <param name="what">The waited-for expression, for the timeout message.</param>
    /// <exception cref="TimeoutException">The task did not complete within <see cref="Budget"/> of real time.</exception>
    public static async Task AdvanceUntilDoneAsync(
        this FakeTimeProvider time,
        Task task,
        TimeSpan step,
        bool pauseBetweenSteps = false,
        [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < Budget)
        {
            time.Advance(step);
            if (pauseBetweenSteps)
            {
                await Task.Delay(1);
            }
            else
            {
                await Task.Yield();
            }
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException(
                $"'{what}' did not complete within {Budget.TotalSeconds:0} s of real time; the fake clock reached {time.GetUtcNow():O}.");
        }
    }

    /// <summary>Waits for <paramref name="task"/> for at most <see cref="Budget"/> of real time, then returns its result.</summary>
    public static async Task<T> WithinBudget<T>(this Task<T> task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        await ((Task)task).WithinBudget(what);
        return await task;
    }

    /// <summary>Waits for <paramref name="task"/> for at most <see cref="Budget"/> of real time, then observes it.</summary>
    public static async Task WithinBudget(this Task task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        try
        {
            await task.WaitAsync(Budget);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"'{what}' did not complete within {Budget.TotalSeconds:0} s of real time.");
        }
    }

    /// <summary>Waits for a signal that never faults, failing with <paramref name="message"/> if <paramref name="timeout"/> passes first.</summary>
    internal static async Task SignalWithin(Task signal, TimeSpan timeout, string message)
    {
        try
        {
            await signal.WaitAsync(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
        }
        catch (TimeoutException) when (!signal.IsCompleted)
        {
            throw new TimeoutException(message);
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, for at most <see cref="Budget"/> of real time.</summary>
    public static async Task UntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? what = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= Budget)
            {
                throw new TimeoutException($"'{what}' did not hold within {Budget.TotalSeconds:0} s of real time.");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports each timer it creates, so a test can move the clock only once the code
/// under test is actually waiting on it, instead of guessing with a real delay.
/// </summary>
internal sealed class TimerSignalingTimeProvider : FakeTimeProvider
{
    private readonly object _sync = new();
    private readonly List<TimeSpan> _created = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        TaskCompletionSource changed;
        lock (_sync)
        {
            _created.Add(dueTime);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        return timer;
    }

    /// <summary>
    /// Waits until <paramref name="count"/> timers with the given due time have been created in total (bounded by
    /// <see cref="Waits.Budget"/>). The timer is registered with the clock by the time this returns.
    /// </summary>
    public async Task WaitForTimerAsync(TimeSpan dueTime, int count = 1)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_created.Count(t => t == dueTime) >= count)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await Waits.SignalWithin(
                changed,
                Waits.Budget - clock.Elapsed,
                $"timer #{count} with due time {dueTime} was not created within {Waits.Budget.TotalSeconds:0} s of real time.");
        }
    }

    /// <summary>Waits for the <paramref name="count"/>th timer due in <paramref name="seconds"/>, then advances the clock by that much.</summary>
    public async Task AdvanceWhenWaitingAsync(int seconds, int count = 1)
    {
        await WaitForTimerAsync(TimeSpan.FromSeconds(seconds), count);
        Advance(TimeSpan.FromSeconds(seconds));
    }
}
