// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Oidc;

namespace OpenShell.Gateway;

/// <summary>
/// Cloudflare Access credentials read lazily from <c>edge_token</c> (or legacy <c>cf_token</c>) on first use.
/// The token is sent as a bearer token and as the Access header and cookie, so it works whether the edge
/// terminates Access itself or forwards the JWT to the gateway.
/// </summary>
internal sealed class LazyEdgeAuth(string directory) : IAuthProvider
{
    private readonly Gate _gate = new();
    private IAuthProvider? _inner;

    public bool SendsCredentials => true;

    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Inner().ApplyAsync(request, cancellationToken);

    internal static string ReadToken(string directory)
    {
        var primary = Path.Join(directory, "edge_token");
        var legacy = Path.Join(directory, "cf_token");
        var path = (File.Exists(primary) ? primary : File.Exists(legacy) ? legacy : null) ?? throw new OpenShellException(OpenShellErrorKind.Auth, "neither edge_token nor cf_token found in gateway directory");

        string token;
        try
        {
            token = File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OpenShellException(OpenShellErrorKind.Auth, $"cannot read {Path.GetFileName(path)}", e);
        }

        return token.Length == 0
            ? throw new OpenShellException(OpenShellErrorKind.Auth, $"{Path.GetFileName(path)} file is empty")
            : token;
    }

    private IAuthProvider Inner()
    {
        lock (_gate)
        {
            if (_inner is null)
            {
                var token = ReadToken(directory);
                _inner = Auth.CloudflareAccess(token, Auth.StaticToken(token));
            }

            return _inner;
        }
    }
}

/// <summary>
/// OIDC bearer tokens read from the CLI's <c>oidc_token.json</c>. When the access token expires, or the gateway
/// rejects it, the provider re-reads the file (another process may have renewed it) and otherwise renews through the
/// stored refresh token, persisting rotation back to disk.
/// </summary>
internal sealed class DiskOidcAuth : IAuthProvider, IDisposable
{
    private readonly GatewayMetadata _config;
    private readonly GatewayConnectOptions _options;
    private readonly RefreshableToken _token;
    private readonly TimeProvider _time;
    private string? _rejected;
    private string? _served;

    public DiskOidcAuth(GatewayMetadata config, GatewayConnectOptions options)
    {
        _config = config;
        _options = options;
        _time = options.TimeProvider ?? TimeProvider.System;
        _token = new RefreshableToken(new AccessToken(string.Empty, DateTimeOffset.UnixEpoch), LoadAsync, timeProvider: _time);
    }

    public bool SendsCredentials => true;

    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var value = await _token.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", value);
    }

    public void Dispose() => _token.Dispose();

    public async ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken)
    {
        _rejected = Volatile.Read(ref _served);
        return await _token.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AccessToken> LoadAsync(CancellationToken cancellationToken)
    {
        var issuer = _config.OidcIssuer;
        var clientId = _config.OidcClientId;
        var stored = OidcTokenFile.Read(_config.StateDirectory);

        // A token issued for another issuer or client, or one that records neither, is not this gateway's token. When
        // the metadata names no issuer or client there is nothing to check against, and the token is served as is.
        var disk = stored is not null && (issuer is null || clientId is null || stored.IsFor(issuer, clientId)) ? stored.Tokens : null;
        if (disk is not null && IsUsable(disk) && !string.Equals(disk.AccessToken, _rejected, StringComparison.Ordinal))
        {
            return Serve(disk);
        }

        if (!_options.AutoRefresh || disk?.RefreshToken is null || issuer is null || clientId is null)
        {
            throw new OidcException(
                OidcFailure.Refresh,
                disk is null
                    ? $"no usable OIDC token for gateway '{_config.Name}'; run `openshell gateway login {_config.Name}`"
                    : $"OIDC token for gateway '{_config.Name}' is expired; run `openshell gateway login {_config.Name}`");
        }

        var renewed = await OidcLogin.RefreshAsync(
            new OidcOptions { Gateway = _config, HttpClient = _options.OidcHttpClient, TimeProvider = _options.TimeProvider },
            disk.RefreshToken,
            cancellationToken).ConfigureAwait(false);
        renewed = renewed with { RefreshToken = renewed.RefreshToken ?? disk.RefreshToken };

        if (_options.WriteBack)
        {
            try
            {
                OidcTokenFile.Write(_config.StateDirectory, renewed, issuer, clientId);
            }
            catch (OidcException)
            {
                // The renewed token is still valid for this process; a failed write-back must not fail the call.
            }
        }

        return Serve(renewed);
    }

    private bool IsUsable(OidcTokens tokens) =>
        tokens.ExpiresAt is not { } expiry || expiry - TimeSpan.FromSeconds(30) > _time.GetUtcNow();

    private AccessToken Serve(OidcTokens tokens)
    {
        Volatile.Write(ref _served, tokens.AccessToken);
        return tokens.ToAccessToken();
    }
}
