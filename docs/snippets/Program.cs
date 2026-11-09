// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

// Runs the documentation snippets that need no gateway: the testing guide, and the gateway snippets that also work
// against FakeOpenShellClient. scripts/docs.sh runs it after the build; a failed check exits non-zero.
using Microsoft.Extensions.Options;
using OpenShell;
using OpenShell.DependencyInjection;
using OpenShell.Testing;
using Snippets;

await Run("Testing.ScriptExec", Testing.ScriptExecAsync);
await Run("Testing.TimeControl", Testing.TimeControlAsync);
await Run("Testing.Failures", Testing.FailuresAsync);
await Run("Testing.Watch", Testing.WatchAsync);
await Run("Testing.Forward", Testing.ForwardAsync);
await Run("Testing.Files", Testing.FilesAsync);
await Run("Testing.SharedGateway", Testing.SharedGatewayAsync);
await Run("Testing.EndpointStatuses", Testing.EndpointStatusesAsync);

await Run("GettingStarted.FirstProgram", async () =>
{
    using var fake = new FakeOpenShellClient();
    fake.Gateway.OnExec(call => call.CommandLine == "echo hello from OpenShell"
        ? FakeExecScript.Success("hello from OpenShell\n")
        : null);

    await GettingStarted.RunFirstProgramAsync(fake);
    var operations = fake.Gateway.Calls.Select(c => c.Operation).Distinct().ToList();
    Check(
        operations.SequenceEqual(["Sandboxes.Create", "Sandboxes.Get", "Exec.Run", "Sandboxes.Delete"]),
        "unexpected calls: " + string.Join(", ", operations));
    Check((await fake.Sandboxes.List().ToListAsync()).Count == 0, "the sandbox was not deleted");
});

await Run("Sandboxes", async () =>
{
    using var fake = new FakeOpenShellClient();
    await Sandboxes.CreateAsync(fake);
    await Sandboxes.WaitAsync(fake);
    await Sandboxes.GetAndListAsync(fake);
    await Sandboxes.StopStartAsync(fake);
    await Sandboxes.SuspendResumeAsync(fake);
    await Sandboxes.StopStartAsync(fake);
    await fake.Sandboxes.StopAsync("agent-1");
    await fake.Sandboxes.WaitStoppedAsync("agent-1");
    await Sandboxes.UpdateAsync(fake);
    await Sandboxes.EndpointStatusAsync(fake);
    await Sandboxes.TemplatesAsync(fake);
    await Sandboxes.DeleteAsync(fake);
    Check((await fake.Sandboxes.List().ToListAsync()).Select(s => s.Name).SequenceEqual(["agent-2"]), "agent-1 was not deleted");
});

await Run("ExecAndStreaming", async () =>
{
    using var fake = new FakeOpenShellClient();
    fake.Gateway.OnExec(call => call.Command[0] == "sh" ? new FakeExecScript(3, "out\n"u8.ToArray(), "err\n"u8.ToArray()) : null);
    await fake.Sandboxes.CreateAsync("agent-1", new SandboxSpec { Image = "img" });
    await ExecAndStreaming.RunAsync(fake);
    await ExecAndStreaming.PythonAsync(fake);
    Check(await ExecAndStreaming.StreamAsync(fake) == 3, "the exit event was not seen");
});

await Run("ClientBehavior", async () =>
{
    using var fake = new FakeOpenShellClient();
    await ClientBehavior.WorkspacesAsync(fake);
    await fake.Sandboxes.CreateAsync("agent-1", new SandboxSpec { Image = "img" });
    await ClientBehavior.PaginationAsync(fake);
    await ClientBehavior.ErrorsAsync(fake);
    await ClientBehavior.MutationLockAsync(fake);
});

await Run("Policy.Settings", async () =>
{
    using var fake = new FakeOpenShellClient();
    await fake.Sandboxes.CreateAsync("agent-1", new SandboxSpec { Image = "img" });
    await Policy.SettingsAsync(fake);
});

await Run("Observability.UpstreamRelease", () =>
{
    Check(!string.IsNullOrEmpty(Observability.UpstreamRelease()), "OpenShellUpstreamRelease metadata is missing");
    return Task.CompletedTask;
});

await Run("DependencyInjection.appsettings.json", async () =>
{
    // The configuration sample binds to the options the guide describes.
    var configuration = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
        .Build();
    var services = new ServiceCollection();
    services.AddOpenShell(configuration.GetSection("OpenShell"));

    // The certificate files do not exist here; drop them before the options are validated.
    services.PostConfigure<OpenShellOptions>(o => o.CaCertificatePath = o.ClientCertificatePath = o.ClientKeyPath = null);
#pragma warning disable ASP0000 // A standalone container, not the one of an ASP.NET Core app.
    await using var provider = services.BuildServiceProvider();
#pragma warning restore ASP0000

    var options = provider.GetRequiredService<IOptionsMonitor<OpenShellOptions>>().CurrentValue;
    Check(options.Address == new Uri("https://gateway.example.com"), "Address was not bound");
    Check(options.Workspace == "team-a", "Workspace was not bound");
    Check(options.DefaultTimeout == TimeSpan.FromSeconds(30), "DefaultTimeout was not bound");
    Check(options.Retry is { MaxAttempts: 6 }, "Retry was not bound");
    Check(provider.GetRequiredService<IOpenShellClient>().Workspace == "team-a", "the client was not created");
});

Console.WriteLine("All snippet checks passed.");
return 0;

static async Task Run(string name, Func<Task> snippet)
{
    Console.WriteLine($"== {name}");
    await snippet();
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("snippet check failed: " + message);
    }
}
