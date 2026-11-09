// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

// Runs as a Native AOT binary. Each client calls a gateway that is not there, which exercises options binding,
// channel setup, protobuf serialization, and error mapping without a server.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;

const string Unreachable = "http://127.0.0.1:9";

using (var direct = new OpenShellClient(new OpenShellClientOptions { Address = new Uri(Unreachable), Retry = RetryPolicy.None }))
{
    await ExpectUnavailableAsync(direct, "direct client");
}

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OpenShell:Address"] = Unreachable,
        ["OpenShell:Workspace"] = "smoke",
        ["OpenShell:DefaultTimeout"] = "00:00:10",
        ["OpenShell:Retry:MaxAttempts"] = "1",
    })
    .Build();

var services = new ServiceCollection();
services.AddOpenShell(configuration.GetSection("OpenShell"));
await using (var provider = services.BuildServiceProvider())
{
    await ExpectUnavailableAsync(provider.GetRequiredService<IOpenShellClient>(), "DI client");
}

Console.WriteLine("OK Native AOT");
return 0;

static async Task ExpectUnavailableAsync(IOpenShellClient client, string what)
{
    try
    {
        await client.Health.CheckAsync();
    }
    catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.Unavailable)
    {
        Console.WriteLine($"{what}: {e.Kind} as expected");
        return;
    }

    throw new InvalidOperationException($"{what}: the health check reached a gateway that should not exist");
}
