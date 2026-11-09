// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenShell.DependencyInjection;

/// <summary>Creates and caches one <see cref="IOpenShellClient"/> per configured gateway name.</summary>
public interface IOpenShellClientFactory
{
    /// <summary>
    /// Returns the client for <paramref name="name"/>, creating it on first use. Pass <see langword="null"/> for the
    /// default (unnamed) registration. Clients are shared and owned by the factory; do not dispose them.
    /// </summary>
    IOpenShellClient Create(string? name = null);
}

internal sealed class OpenShellClientFactory(IOptionsMonitor<OpenShellOptions> options, ILoggerFactory? loggerFactory = null)
    : IOpenShellClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, OpenShellClient> _clients = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;

    public IOpenShellClient Create(string? name = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var key = name ?? Options.DefaultName;
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // One build per name at a time, and only a successful build is cached: a failure (for example a certificate
        // file that is not there yet) is thrown to this caller and the next call tries again.
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(key, out existing))
            {
                return existing;
            }

            var client = Build(key);
            _clients[key] = client;
            return client;
        }
    }

    public void Dispose()
    {
        OpenShellClient[] clients;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            clients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private OpenShellClient Build(string name) =>
        new(options.Get(name).ToClientOptions(loggerFactory));
}
