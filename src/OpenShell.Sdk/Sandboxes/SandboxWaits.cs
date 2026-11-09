// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>
/// The phase rules behind the sandbox wait helpers, shared by the real client and the in-memory fake.
/// Each step returns <see langword="null"/> when the wait is over, an empty string to keep polling, or the
/// message of a terminal failure.
/// </summary>
internal static class SandboxWaits
{
    public static string? Ready(Sandbox sandbox, string name) => sandbox.Phase switch
    {
        SandboxPhase.Ready or SandboxPhase.Completed => null,
        SandboxPhase.Stopped => $"sandbox '{name}' stopped before becoming ready",
        SandboxPhase.Error => $"sandbox '{name}' entered error phase",
        SandboxPhase.Suspending or SandboxPhase.Suspended => Suspended(name, "Ready"),
        _ => "",
    };

    public static string? Stopped(Sandbox sandbox, string name) => sandbox.Phase switch
    {
        SandboxPhase.Stopped => null,
        SandboxPhase.Error => $"sandbox '{name}' entered error phase",
        SandboxPhase.Suspending or SandboxPhase.Suspended => Suspended(name, "Stopped"),
        _ => "",
    };

    /// <summary>
    /// Suspending is the only phase on the way to Suspended. Unspecified and Unknown are transient reporting gaps;
    /// any other phase means the suspension did not happen or was undone.
    /// </summary>
    public static string? SuspendedStep(Sandbox sandbox, string name) => sandbox.Phase switch
    {
        SandboxPhase.Suspended => null,
        SandboxPhase.Suspending or SandboxPhase.Unspecified or SandboxPhase.Unknown => "",
        SandboxPhase.Error => $"sandbox '{name}' entered error phase",
        var phase => $"sandbox '{name}' is {phase}, not Suspended",
    };

    private static string Suspended(string name, string target) =>
        $"sandbox '{name}' is suspended; resume it before waiting for {target}";
}
