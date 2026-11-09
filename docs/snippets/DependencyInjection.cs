// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;
using OpenShell.DependencyInjection;

namespace Snippets;

/// <summary>Snippets for docs/guides/dependency-injection.md.</summary>
internal static class DependencyInjection
{
    internal static void WebApp(string[] args)
    {
        #region WebApp
        var builder = WebApplication.CreateBuilder(args);

        // Binds the "OpenShell" section (see appsettings.json) and validates it when the host starts.
        builder.Services.AddOpenShell(builder.Configuration.GetSection("OpenShell"));
        builder.Services.AddHealthChecks().AddOpenShellHealthCheck();

        var app = builder.Build();
        app.MapHealthChecks("/healthz");

        app.MapGet("/sandboxes", async (ISandboxClient sandboxes, CancellationToken cancellationToken) =>
        {
            var list = await sandboxes.List().ToListAsync(cancellationToken);
            return list.Select(s => new { s.Name, Phase = s.Phase.ToString() });
        });

        app.Run();
        #endregion
    }

    internal static void InCode(IServiceCollection services)
    {
        #region InCode
        services.AddOpenShell(options =>
        {
            options.Address = new Uri("https://gateway.example.com");
            options.Workspace = "team-a";
            options.Token = Environment.GetEnvironmentVariable("OPENSHELL_TOKEN");
            options.DefaultTimeout = TimeSpan.FromSeconds(30);
            options.Retry = new RetryOptions { MaxAttempts = 6 };
        });
        #endregion
    }

    internal static void FluentBuilder(IServiceCollection services)
    {
        #region FluentBuilder
        services.AddOpenShellClient("prod")
            .WithAddress("https://gateway.example.com")
            .WithWorkspace("team-a")
            .WithClientCredentials(
                "https://idp.example.com/realms/openshell",
                "openshell-service",
                cancellationToken => ValueTask.FromResult(Environment.GetEnvironmentVariable("OPENSHELL_OIDC_CLIENT_SECRET")!),
                "sandbox:read",
                "sandbox:write")
            .WithCaCertificate("/etc/openshell/ca.crt")
            .WithTimeout(TimeSpan.FromSeconds(30))
            .WithRetry(retry => retry.MaxAttempts = 6);

        services.AddOpenShellClient("local")
            .WithAddress("http://127.0.0.1:8080")
            .WithoutRetry();
        #endregion
    }

    #region Keyed
    internal sealed class SandboxJanitor(
        [FromKeyedServices("prod")] IOpenShellClient prod,
        IOpenShellClientFactory factory)
    {
        public async Task CleanUpAsync(CancellationToken cancellationToken)
        {
            var expired = new ListOptions { LabelSelector = "ttl=expired" };
            await foreach (var sandbox in prod.Sandboxes.List(expired).WithCancellation(cancellationToken))
            {
                await prod.Sandboxes.DeleteAsync(sandbox.Name, new DeleteOptions { AllowMissing = true }, cancellationToken);
            }

            // Or resolve a client by name at run time. The factory creates one client per name and owns it:
            // do not dispose it.
            var local = factory.Create("local");
            Console.WriteLine(await local.Health.CheckAsync(cancellationToken));
        }
    }
    #endregion

    internal static void AuthFromServices(IServiceCollection services)
    {
        #region AuthFromServices
        // Build the auth provider from other services, for example a token cache.
        services.AddOpenShellClient()
            .WithAddress("https://gateway.example.com")
            .WithAuth(provider => provider.GetRequiredService<ITokenCache>().AsAuthProvider());
        #endregion
    }

    /// <summary>A token cache of the application, used by <see cref="AuthFromServices"/>.</summary>
    internal interface ITokenCache
    {
        IAuthProvider AsAuthProvider();
    }
}
