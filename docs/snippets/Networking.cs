// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/networking.md.</summary>
internal static class Networking
{
    internal static async Task<string> ForwardAsync(IOpenShellClient client)
    {
        #region Forward
        // A duplex stream to port 8080 inside the sandbox.
        await using var stream = await client.Tcp.ForwardAsync("agent-1", 8080);

        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n"));
        await stream.CompleteWriteAsync();   // half-close: the server sees EOF, the response keeps flowing

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync();
        #endregion
        return response;
    }

    internal static async Task ListenAsync(IOpenShellClient client)
    {
        #region Listen
        // Binds a local port (0 picks a free one) and forwards each accepted connection to port 8080 in the sandbox.
        await using var listener = await client.Tcp.ListenAsync("agent-1", remotePort: 8080, localPort: 0);
        Console.WriteLine($"open http://{listener.LocalEndPoint}/");

        using var http = new HttpClient();
        var body = await http.GetStringAsync(new Uri($"http://{listener.LocalEndPoint}/health"));
        #endregion
        Console.WriteLine(body);
    }

    internal static async Task SshAsync(IOpenShellClient client)
    {
        #region Ssh
        // The raw SSH byte stream to the sandbox, for an SSH client library to drive. The tunnel creates its
        // own session and revokes it when the stream is disposed.
        await using (var tunnel = await client.Ssh.TunnelAsync("agent-1"))
        {
            // ... hand `tunnel` to your SSH library ...
        }
        #endregion

        #region SshSession
        // A short-lived session for an external SSH client. The token is a secret; ToString() redacts it.
        var session = await client.Ssh.CreateSessionAsync("agent-1");
        Console.WriteLine($"{session.GatewayScheme}://{session.GatewayHost}:{session.GatewayPort}, expires {session.ExpiresAt}");
        Console.WriteLine($"pin host key {session.HostKeyFingerprint}");

        // Revoke it as soon as it is no longer needed.
        await client.Ssh.RevokeSessionAsync(session.Token);
        #endregion
    }

    internal static async Task ServicesAsync(IOpenShellClient client)
    {
        #region Services
        // Expose port 8080 of the sandbox through the gateway.
        var web = await client.Services.ExposeAsync("agent-1", "web", 8080, new ExposeServiceOptions
        {
            Domain = true,
            AuthorizationMode = ServiceAuthorizationMode.Strip,   // remove Authorization before forwarding
        });
        Console.WriteLine($"{web.Name}: {web.Url}");

        await foreach (var service in client.Services.List("agent-1"))
        {
            Console.WriteLine($"{service.Name} -> {service.TargetPort}: {service.Url}");
        }

        await client.Services.DeleteAsync("agent-1", "web");
        #endregion
    }
}
