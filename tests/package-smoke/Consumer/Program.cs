// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

// Uses the three packages the way an application does: the fake from OpenShell.Net.Testing, the real client
// through OpenShell.Net.DependencyInjection, and the metadata stamped into OpenShell.Net.
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Testing;

using (var fake = new FakeOpenShellClient())
{
    fake.Gateway.OnExec(call => call.CommandLine == "echo hi" ? FakeExecScript.Success("hi\n") : null);

    await fake.Sandboxes.CreateAsync("smoke", new SandboxSpec { Image = "img" });
    await fake.Sandboxes.WaitReadyAsync("smoke");
    var result = await fake.Exec.RunAsync("smoke", ["echo", "hi"]);
    Check(result.StdoutText == "hi\n", $"exec output was '{result.StdoutText}'");

    var deletion = await fake.Sandboxes.DeleteAsync("smoke");
    Check(deletion.IsComplete, $"deletion outcome was {deletion.Outcome}");
}

var services = new ServiceCollection();
services.AddOpenShellClient().WithAddress("http://127.0.0.1:9");
await using (var provider = services.BuildServiceProvider())
{
    var client = provider.GetRequiredService<IOpenShellClient>();
    Check(client.Sandboxes is not null, "the DI client has no sandbox client");
}

var upstream = typeof(OpenShellClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .SingleOrDefault(a => a.Key == "OpenShellUpstreamRelease")?.Value;
Check(!string.IsNullOrEmpty(upstream), "OpenShellUpstreamRelease metadata is missing");

var version = typeof(OpenShellClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
Console.WriteLine($"OK {RuntimeInformation.FrameworkDescription}: OpenShell.Net {version}, upstream {upstream}");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("package smoke test failed: " + message);
    }
}
