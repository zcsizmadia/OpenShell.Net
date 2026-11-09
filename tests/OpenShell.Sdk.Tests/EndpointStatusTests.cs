// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using OpenShell.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class EndpointStatusTests
{
    private static Wire.EndpointFailure WireFailure(uint? upstreamStatus = 401)
    {
        var failure = new Wire.EndpointFailure
        {
            Domain = Wire.EndpointFailureDomain.TokenGrant,
            Code = "provider_authorization_required",
            RecoveryAction = Wire.ProviderCredentialRefreshRecoveryAction.Reauthorize,
            Provider = "github",
            CredentialKey = "GITHUB_TOKEN",
            Stage = "gateway_rpc",
            RequestId = "req-1",
        };
        if (upstreamStatus is { } status)
        {
            failure.UpstreamHttpStatus = status;
        }

        return failure;
    }

    private static async Task<Sandbox> GetAsync(Wire.SandboxResponse response)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => response);
        using var client = ClientTests.NewClient(invoker);
        return await client.Sandboxes.GetAsync("sb");
    }

    [Test]
    public async Task Endpoint_statuses_and_token_grant_failures_are_mapped()
    {
        var response = ClientTests.SandboxReply("sb");
        var reported = DateTimeOffset.UnixEpoch.AddDays(10);
        response.Sandbox.Status.EndpointStatuses.Add(new Wire.EndpointStatus
        {
            EndpointId = "api",
            Host = "api.github.com",
            Ports = { 443, 8443 },
            Path = "/repos",
            LastResult = Wire.EndpointResult.CredentialUnavailable,
            LastReportedTime = Timestamp.FromDateTimeOffset(reported),
            LastFailure = WireFailure(),
        });
        response.Sandbox.Status.EndpointStatuses.Add(new Wire.EndpointStatus { EndpointId = "plain", Host = "example.com" });
        response.Sandbox.Status.LastTokenGrantFailure = new Wire.TokenGrantFailureObservation
        {
            DestinationHost = "api.github.com",
            DestinationPort = 443,
            Failure = WireFailure(upstreamStatus: null),
        };

        var sandbox = await GetAsync(response);

        var api = sandbox.EndpointStatuses[0];
        await Assert.That(api.EndpointId).IsEqualTo("api");
        await Assert.That(api.Host).IsEqualTo("api.github.com");
        await Assert.That(api.Ports).IsEquivalentTo(new uint[] { 443, 8443 });
        await Assert.That(api.Path).IsEqualTo("/repos");
        await Assert.That(api.LastResult).IsEqualTo(EndpointResult.CredentialUnavailable);
        await Assert.That(api.LastReportedAt).IsEqualTo(reported);
        await Assert.That(api.LastFailure).IsEqualTo(new EndpointFailure
        {
            Domain = EndpointFailureDomain.TokenGrant,
            Code = "provider_authorization_required",
            RecoveryAction = RefreshRecoveryAction.Reauthorize,
            Provider = "github",
            CredentialKey = "GITHUB_TOKEN",
            Stage = "gateway_rpc",
            UpstreamHttpStatus = 401,
            RequestId = "req-1",
        });

        var plain = sandbox.EndpointStatuses[1];
        await Assert.That(plain.LastResult).IsEqualTo(EndpointResult.Unspecified);
        await Assert.That(plain.LastReportedAt).IsNull();
        await Assert.That(plain.LastFailure).IsNull();

        var grant = sandbox.LastTokenGrantFailure!;
        await Assert.That(grant.DestinationHost).IsEqualTo("api.github.com");
        await Assert.That(grant.DestinationPort).IsEqualTo(443u);
        await Assert.That(grant.Failure!.UpstreamHttpStatus).IsNull();
        await Assert.That(grant.Failure.Code).IsEqualTo("provider_authorization_required");
    }

    [Test]
    public async Task A_sandbox_without_reports_has_none()
    {
        var sandbox = await GetAsync(ClientTests.SandboxReply("sb"));
        var noStatus = await GetAsync(new Wire.SandboxResponse { Sandbox = new Wire.Sandbox { Metadata = new() { Name = "sb" } } });

        await Assert.That(sandbox.EndpointStatuses).IsEmpty();
        await Assert.That(sandbox.LastTokenGrantFailure).IsNull();
        await Assert.That(noStatus.EndpointStatuses).IsEmpty();
        await Assert.That(noStatus.LastTokenGrantFailure).IsNull();
    }

    [Test]
    public async Task A_token_grant_observation_without_a_classified_cause_has_no_failure()
    {
        var response = ClientTests.SandboxReply("sb");
        response.Sandbox.Status.LastTokenGrantFailure = new Wire.TokenGrantFailureObservation { DestinationHost = "h", DestinationPort = 1 };

        var sandbox = await GetAsync(response);

        await Assert.That(sandbox.LastTokenGrantFailure!.Failure).IsNull();
    }

    [Test]
    public async Task Enum_values_from_a_newer_gateway_map_to_unspecified()
    {
        var response = ClientTests.SandboxReply("sb");
        var failure = WireFailure();
        failure.Domain = (Wire.EndpointFailureDomain)42;
        failure.RecoveryAction = (Wire.ProviderCredentialRefreshRecoveryAction)42;
        response.Sandbox.Status.EndpointStatuses.Add(new Wire.EndpointStatus { LastResult = (Wire.EndpointResult)42, LastFailure = failure });

        var status = (await GetAsync(response)).EndpointStatuses.Single();

        await Assert.That(status.LastResult).IsEqualTo(EndpointResult.Unspecified);
        await Assert.That(status.LastFailure!.Domain).IsEqualTo(EndpointFailureDomain.Unspecified);
        await Assert.That(status.LastFailure.RecoveryAction).IsEqualTo(RefreshRecoveryAction.Unspecified);
    }

    [Test]
    public async Task The_fake_reports_endpoint_statuses_and_copies_the_ports()
    {
        using var client = new FakeOpenShellClient();
        var created = await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img" });
        var ports = new List<uint> { 443 };
        var failure = new EndpointFailure { Domain = EndpointFailureDomain.TokenGrant, Code = "token_exchange_failed" };

        client.Gateway.ReportEndpointStatuses(
            "sb",
            [new EndpointStatus { EndpointId = "api", Host = "api.example.com", Ports = ports, LastResult = EndpointResult.CredentialUnavailable, LastFailure = failure }],
            new TokenGrantFailureObservation { DestinationHost = "api.example.com", DestinationPort = 443, Failure = failure });
        ports.Add(8443);

        var sandbox = await client.Sandboxes.GetAsync("sb");

        await Assert.That(sandbox.EndpointStatuses.Single().Ports).IsEquivalentTo(new uint[] { 443 });
        await Assert.That(sandbox.EndpointStatuses.Single().LastFailure).IsEqualTo(failure);
        await Assert.That(sandbox.LastTokenGrantFailure!.Failure!.Code).IsEqualTo("token_exchange_failed");
        await Assert.That(sandbox.ResourceVersion).IsGreaterThan(created.ResourceVersion);

        client.Gateway.ReportEndpointStatuses("sb", []);
        var cleared = await client.Sandboxes.GetAsync("sb");
        await Assert.That(cleared.EndpointStatuses).IsEmpty();
        await Assert.That(cleared.LastTokenGrantFailure).IsNull();
    }
}
