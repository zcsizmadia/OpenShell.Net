// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell;

/// <summary>Outcome of the latest observed exchange with a sandbox endpoint. Values match the wire enum.</summary>
public enum EndpointResult
{
    /// <summary>Not reported, or a value this SDK version does not know.</summary>
    Unspecified = 0,
    /// <summary>No exchange with the endpoint has been observed yet.</summary>
    NoObservedExchange = 1,
    /// <summary>The upstream answered with an HTTP response.</summary>
    HttpResponseReceived = 2,
    /// <summary>The sandbox policy denied the request.</summary>
    PolicyDenied = 3,
    /// <summary>A credential the request needed was unavailable; see <see cref="EndpointStatus.LastFailure"/>.</summary>
    CredentialUnavailable = 4,
    /// <summary>The TLS handshake with the upstream failed.</summary>
    TlsFailed = 5,
    /// <summary>The connection to the upstream failed.</summary>
    TransportFailed = 6,
    /// <summary>The upstream rejected the request.</summary>
    UpstreamRejected = 7,
}

/// <summary>Which subsystem classified an <see cref="EndpointFailure"/>. Values match the wire enum.</summary>
public enum EndpointFailureDomain
{
    /// <summary>Not reported, or a value this SDK version does not know.</summary>
    Unspecified = 0,
    /// <summary>A gateway-managed token grant for the request failed.</summary>
    TokenGrant = 1,
}

/// <summary>A classified cause of an endpoint failure, such as a token grant that could not be completed.</summary>
public sealed record EndpointFailure
{
    /// <summary>The classifying subsystem.</summary>
    public EndpointFailureDomain Domain { get; init; }

    /// <summary>A stable lower_snake_case cause within the domain, for example <c>provider_authorization_required</c>.</summary>
    public string Code { get; init; } = "";

    /// <summary>What an operator should do about it.</summary>
    public RefreshRecoveryAction RecoveryAction { get; init; }

    /// <summary>The provider whose credential was involved, when known.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The credential key within the provider, when known.</summary>
    public string CredentialKey { get; init; } = "";

    /// <summary>A stable lower_snake_case stage where it failed, for example <c>gateway_rpc</c>.</summary>
    public string Stage { get; init; } = "";

    /// <summary>The HTTP status the upstream returned, when one was received.</summary>
    public uint? UpstreamHttpStatus { get; init; }

    /// <summary>The <c>x-openshell-request-id</c> the workload saw on the failed request, for correlation.</summary>
    public string RequestId { get; init; } = "";
}

/// <summary>What the sandbox supervisor last observed for one declared endpoint.</summary>
public sealed record EndpointStatus
{
    /// <summary>The endpoint identifier.</summary>
    public string EndpointId { get; init; } = "";

    /// <summary>The endpoint host.</summary>
    public string Host { get; init; } = "";

    /// <summary>The endpoint ports.</summary>
    public IReadOnlyList<uint> Ports { get; init; } = [];

    /// <summary>The endpoint path, when the endpoint is path-scoped.</summary>
    public string Path { get; init; } = "";

    /// <summary>Outcome of the latest observed exchange.</summary>
    public EndpointResult LastResult { get; init; }

    /// <summary>When the supervisor reported <see cref="LastResult"/>.</summary>
    public DateTimeOffset? LastReportedAt { get; init; }

    /// <summary>The classified cause of the latest failure; null when there is none.</summary>
    public EndpointFailure? LastFailure { get; init; }
}

/// <summary>The latest token-grant failure observed in a sandbox, with the destination it was for.</summary>
public sealed record TokenGrantFailureObservation
{
    /// <summary>Destination host of the request that needed the token.</summary>
    public string DestinationHost { get; init; } = "";

    /// <summary>Destination port of the request that needed the token.</summary>
    public uint DestinationPort { get; init; }

    /// <summary>The classified failure; null when the gateway gave no cause.</summary>
    public EndpointFailure? Failure { get; init; }
}
