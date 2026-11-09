// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/observability.md and docs/versioning.md.</summary>
internal static class Observability
{
    internal static OpenShellClient Logging()
    {
        #region Logging
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Retries and watch reconnects log under "OpenShell.Sdk", the edge tunnel under "OpenShell.Edge.EdgeTunnelProxy",
        // and the gRPC channel under "Grpc.Net.Client".
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            LoggerFactory = loggerFactory,
        });
        #endregion
        return client;
    }

    #region Listeners
    /// <summary>Prints every OpenShell RPC span and duration measurement, without OpenTelemetry.</summary>
    internal static IDisposable ListenToOpenShell()
    {
        var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenShellTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Console.WriteLine(
                $"{activity.DisplayName} {activity.GetTagItem("rpc.grpc.status_code")} {activity.Duration.TotalMilliseconds:F1} ms"),
        };
        ActivitySource.AddActivityListener(activities);

        var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenShellTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<double>((instrument, seconds, tags, _) =>
            Console.WriteLine($"{instrument.Name}: {seconds:F3} s"));
        meters.Start();

        return new Listeners(activities, meters);
    }

    private sealed class Listeners(ActivityListener activities, MeterListener meters) : IDisposable
    {
        public void Dispose()
        {
            activities.Dispose();
            meters.Dispose();
        }
    }
    #endregion

    internal static string? UpstreamRelease()
    {
        #region UpstreamRelease
        var assembly = typeof(OpenShellClient).Assembly;
        var upstream = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "OpenShellUpstreamRelease")?.Value;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Console.WriteLine($"OpenShell.Net {version}, built from NVIDIA OpenShell {upstream}");
        #endregion
        return upstream;
    }
}
