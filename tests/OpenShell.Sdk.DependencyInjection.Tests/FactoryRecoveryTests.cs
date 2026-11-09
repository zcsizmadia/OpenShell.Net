// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace OpenShell.DependencyInjection.Tests;

/// <summary>A client that could not be built is not cached: once the cause is fixed, the next request builds it.</summary>
public class FactoryRecoveryTests
{
    [Test]
    public async Task A_failed_build_is_retried_once_the_cause_is_fixed()
    {
        using var ca = new MissingCaFile();
        using var provider = ca.Provider(health: false);
        var factory = provider.GetRequiredService<IOpenShellClientFactory>();

        await Assert.That(() => factory.Create("gw")).Throws<OptionsValidationException>();

        ca.Fix(provider);

        var client = factory.Create("gw");
        await Assert.That(client).IsNotNull();
        await Assert.That(factory.Create("gw")).IsSameReferenceAs(client);
    }

    [Test]
    public async Task The_health_check_recovers_once_the_cause_is_fixed()
    {
        using var ca = new MissingCaFile();
        using var provider = ca.Provider(health: true);
        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Single();

        await Assert.That(() => registration.Factory(provider)).Throws<OptionsValidationException>();

        ca.Fix(provider);

        await Assert.That(registration.Factory(provider)).IsNotNull();
    }

    [Test]
    public async Task Concurrent_first_requests_share_one_client()
    {
        var services = new ServiceCollection();
        services.AddOpenShell("gw", o => o.Address = new Uri("http://localhost:1"));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IOpenShellClientFactory>();

        var clients = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => factory.Create("gw"))));

        await Assert.That(clients.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>A gateway configured with a CA file that does not exist yet.</summary>
    private sealed class MissingCaFile : IDisposable
    {
        private readonly string _dir = Path.Join(Path.GetTempPath(), "openshell-di-" + Guid.NewGuid().ToString("N"));

        public MissingCaFile()
        {
            Directory.CreateDirectory(_dir);
        }

        private string CaPath => Path.Join(_dir, "ca.pem");

        public ServiceProvider Provider(bool health)
        {
            var services = new ServiceCollection();
            services.AddOpenShell("gw", o => { o.Address = new Uri("https://gateway.example.com"); o.CaCertificatePath = CaPath; });
            if (health)
            {
                services.AddHealthChecks().AddOpenShellHealthCheck(gatewayName: "gw");
            }

            return services.BuildServiceProvider();
        }

        /// <summary>Writes the CA file and drops the cached options, as a configuration reload does.</summary>
        public void Fix(IServiceProvider provider)
        {
            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=ca", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(CaPath, cert.ExportCertificatePem());
            provider.GetRequiredService<IOptionsMonitorCache<OpenShellOptions>>().TryRemove("gw");
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }
}
