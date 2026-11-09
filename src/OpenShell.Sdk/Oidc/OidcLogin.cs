// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.Oidc;

/// <summary>Entry points for the OIDC flows: interactive login, device login, refresh and client credentials.</summary>
public static class OidcLogin
{
    private static readonly int[] DefaultCallbackPorts = [8000, 18000];

    /// <summary>
    /// Authorization-code login with PKCE (when advertised). Opens a browser and listens on a loopback port,
    /// falling back to the paste-the-code flow. A valid cached token issued for the same issuer and client id is returned without any prompt.
    /// </summary>
    public static async Task<OidcTokens> LoginAsync(OidcOptions options, CancellationToken cancellationToken = default)
    {
        var cfg = OidcResolved.Create(options, requireSecret: false);
        var useCache = !options.InMemory && cfg.TokenDirectory is not null;
        if (useCache)
        {
            try
            {
                if (OidcTokenFile.ReadValid(cfg.TokenDirectory!, cfg.Issuer, cfg.ClientId, cfg.Time) is { } cached)
                {
                    return cached;
                }
            }
            catch (OidcException)
            {
                // An unreadable cache must not block a fresh login.
            }
        }

        using var deadline = new Deadline(cfg.Time, options.Timeout, cancellationToken);
        try
        {
            var tokens = await LoginCoreAsync(cfg, deadline.Token).ConfigureAwait(false);
            if (useCache)
            {
                OidcTokenFile.Write(cfg.TokenDirectory!, tokens, cfg.Issuer, cfg.ClientId);
            }

            return tokens;
        }
        catch (OperationCanceledException) when (deadline.TimedOut)
        {
            throw new OidcException(OidcFailure.Timeout, "login timed out");
        }
    }

    /// <summary>RFC 8628 device-code login.</summary>
    public static async Task<OidcTokens> DeviceLoginAsync(OidcOptions options, CancellationToken cancellationToken = default)
    {
        var cfg = OidcResolved.Create(options, requireSecret: false);
        using var deadline = new Deadline(cfg.Time, options.Timeout, cancellationToken);
        try
        {
            return await OidcFlows.DeviceLoginAsync(cfg, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.TimedOut)
        {
            throw new OidcException(OidcFailure.Timeout, "login timed out");
        }
    }

    /// <summary>Exchanges a refresh token for new tokens. The response may omit a new refresh token; keep the old one then.</summary>
    public static async Task<OidcTokens> RefreshAsync(OidcOptions options, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var cfg = OidcResolved.Create(options, requireSecret: false);
        using var deadline = new Deadline(cfg.Time, options.Timeout, cancellationToken);
        try
        {
            return await OidcFlows.RefreshAsync(cfg, refreshToken, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.TimedOut)
        {
            throw new OidcException(OidcFailure.Timeout, "token refresh timed out");
        }
    }

    /// <summary>
    /// One-shot client-credentials exchange. A missing <c>expires_in</c> is tolerated here (the token has no expiry);
    /// use <see cref="ClientCredentialsAuth"/> for a renewable provider.
    /// </summary>
    public static async Task<OidcTokens> ClientCredentialsAsync(OidcOptions options, CancellationToken cancellationToken = default)
    {
        var cfg = OidcResolved.Create(options, requireSecret: true);
        using var deadline = new Deadline(cfg.Time, options.Timeout, cancellationToken);
        try
        {
            return await OidcFlows.ExchangeClientCredentialsAsync(cfg, requirePositiveExpiry: false, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.TimedOut)
        {
            throw new OidcException(OidcFailure.Timeout, "client credentials exchange timed out");
        }
    }

    private static async Task<OidcTokens> LoginCoreAsync(OidcResolved cfg, CancellationToken ct)
    {
        var metadata = await cfg.Discovery.DiscoverAsync(cfg.Issuer, ct).ConfigureAwait(false);

        string? verifier = null;
        string? challenge = null;
        if (metadata.SupportsS256)
        {
            verifier = OidcFlows.RandomToken(32);
            challenge = OidcFlows.CodeChallengeS256(verifier);
        }

        var state = OidcFlows.RandomToken(16);
        var (code, redirectUri) = cfg.Options.UseKeyboardFlow
            ? await KeyboardAsync(cfg, metadata, state, challenge, ct).ConfigureAwait(false)
            : await BrowserAsync(cfg, metadata, state, challenge, ct).ConfigureAwait(false);

        return await OidcFlows.ExchangeCodeAsync(cfg, metadata.TokenEndpoint, code, redirectUri, verifier, ct).ConfigureAwait(false);
    }

    /// <summary>The loopback ports to try: the configured one, or the defaults when it is 0.</summary>
    internal static IReadOnlyList<int> CallbackPorts(int configured) => configured != 0 ? [configured] : DefaultCallbackPorts;

    /// <summary>Starts the callback server on the first port that is free, or returns null when none is.</summary>
    internal static CallbackServer? StartCallbackServer(IReadOnlyList<int> ports, string state)
    {
        foreach (var port in ports)
        {
            if (CallbackServer.TryStart(port, state) is { } server)
            {
                return server;
            }
        }

        return null;
    }

    private static async Task<(string Code, string RedirectUri)> BrowserAsync(
        OidcResolved cfg, OidcProviderMetadata metadata, string state, string? challenge, CancellationToken ct)
    {
        var server = StartCallbackServer(CallbackPorts(cfg.Options.CallbackPort), state);
        if (server is null)
        {
            return await KeyboardAsync(cfg, metadata, state, challenge, ct).ConfigureAwait(false);
        }

        await using (server.ConfigureAwait(false))
        {
            var redirectUri = $"http://localhost:{OidcFlows.FormatPort(server.Port)}/callback";
            var url = OidcFlows.BuildAuthorizationUrl(metadata.AuthorizationEndpoint, cfg.ClientId, redirectUri, state, challenge, cfg.InteractiveScopes);
            try
            {
                // Stryker disable once all : a mutant here makes the tests launch the real system browser.
                await (cfg.Options.BrowserLauncher ?? OidcBrowser.OpenAsync)(url, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return await KeyboardAsync(cfg, metadata, state, challenge, ct).ConfigureAwait(false);
            }

            return (await server.WaitForCodeAsync().WaitAsync(ct).ConfigureAwait(false), redirectUri);
        }
    }

    /// <summary>The paste-the-code flow; returns the code and the out-of-band redirect URI it was issued for.</summary>
    private static async Task<(string Code, string RedirectUri)> KeyboardAsync(
        OidcResolved cfg, OidcProviderMetadata metadata, string state, string? challenge, CancellationToken ct)
    {
        var url = OidcFlows.BuildAuthorizationUrl(metadata.AuthorizationEndpoint, cfg.ClientId, OidcFlows.OutOfBandRedirect, state, challenge, cfg.InteractiveScopes);
        var input = cfg.Options.KeyboardInput ?? Console.In;
        var output = cfg.Options.KeyboardOutput ?? Console.Error;

        var prompt = $"{Environment.NewLine}Open the following URL in your browser to authenticate:{Environment.NewLine}{Environment.NewLine}  {url}{Environment.NewLine}{Environment.NewLine}Paste the authorization code here and press Enter: ";
        await output.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        var line = await ReadLineAsync(input, ct).ConfigureAwait(false)
            ?? throw new OidcException(OidcFailure.AuthCode, "no authorization code received (EOF)");

        var code = line.Trim();
        return code.Length == 0
            ? throw new OidcException(OidcFailure.AuthCode, "empty authorization code")
            : (code, OidcFlows.OutOfBandRedirect);
    }

    private static async Task<string?> ReadLineAsync(TextReader input, CancellationToken ct)
    {
        try
        {
            return await input.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new OidcException(OidcFailure.AuthCode, "failed to read authorization code");
        }
    }
}
