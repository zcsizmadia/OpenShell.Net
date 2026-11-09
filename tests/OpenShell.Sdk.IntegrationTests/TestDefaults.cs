// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using TUnit.Core.Interfaces;

// Every integration test talks to the one real gateway (and most start a sandbox on it); unbounded parallelism
// overloads a small CI runner and makes the tests time out on load rather than on behaviour.
[assembly: ParallelLimiter<OpenShell.IntegrationTests.IntegrationParallelism>]

namespace OpenShell.IntegrationTests;

/// <summary>At most three integration tests run at once.</summary>
public sealed class IntegrationParallelism : IParallelLimit
{
    /// <inheritdoc />
    public int Limit => 3;
}
