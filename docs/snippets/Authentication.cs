// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using OpenShell;
using OpenShell.Edge;
using OpenShell.Gateway;
using OpenShell.Oidc;

namespace Snippets;

/// <summary>Snippets for docs/guides/authentication.md.</summary>
internal static class Authentication
{
    internal static OpenShellClient StaticToken()
    {
        #region StaticToken
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = Auth.StaticToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!),
        });
        #endregion
        return client;
    }

    internal static OpenShellClient BearerTokenSource(Func<CancellationToken, ValueTask<string>> getTokenFromCacheAsync)
    {
        #region BearerTokenSource
        // Called before every request: return a cached token, not a new one each time.
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = Auth.Bearer(getTokenFromCacheAsync),
        });
        #endregion
        return client;
    }

    internal static async Task<OpenShellClient> RefreshableTokenAsync()
    {
        #region RefreshableToken
        var oidc = new OidcOptions
        {
            Issuer = "https://idp.example.com/realms/openshell",
            ClientId = "openshell-cli",
            InMemory = true,
        };

        var tokens = await OidcLogin.LoginAsync(oidc);
        var refreshToken = tokens.RefreshToken
            ?? throw new InvalidOperationException("the identity provider issued no refresh token");

        // Renews single-flight shortly before expiry, and once after the gateway answers Unauthenticated.
        var token = new RefreshableToken(
            tokens.ToAccessToken(),
            async cancellationToken =>
            {
                var renewed = await OidcLogin.RefreshAsync(oidc, refreshToken, cancellationToken);
                refreshToken = renewed.RefreshToken ?? refreshToken;   // keep the old one when it is not rotated
                return renewed.ToAccessToken();
            },
            leeway: TimeSpan.FromMinutes(1));

        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = token.AsAuthProvider(),
        });
        #endregion
        return client;
    }

    internal static OpenShellClient ClientCredentials()
    {
        #region ClientCredentials
        // The provider owns the cached token: dispose it together with the client.
        var auth = ClientCredentialsAuth.Create(new OidcOptions
        {
            Issuer = "https://idp.example.com/realms/openshell",
            ClientId = "openshell-service",
            ClientSecretProvider = cancellationToken => ValueTask.FromResult(
                Environment.GetEnvironmentVariable("OPENSHELL_OIDC_CLIENT_SECRET")!),
            Audience = "openshell-gateway",
            Scopes = ["sandbox:read", "sandbox:write"],
        });

        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = auth,
        });
        #endregion
        return client;
    }

    internal static async Task BrowserLoginAsync()
    {
        #region BrowserLogin
        // Opens the browser and receives the code on a loopback port (8000, then 18000); falls back to
        // pasting the code when neither port is free. A valid cached token is returned without a prompt.
        var tokens = await OidcLogin.LoginAsync(new OidcOptions
        {
            Issuer = "https://idp.example.com/realms/openshell",
            ClientId = "openshell-cli",
            TokenCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "my-app"),
        });

        using var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = Auth.StaticToken(tokens.AccessToken),
        });
        #endregion
    }

    internal static async Task DeviceLoginAsync()
    {
        #region DeviceLogin
        // For machines without a browser: the user opens the URL on another device and enters the code.
        var tokens = await OidcLogin.DeviceLoginAsync(new OidcOptions
        {
            Issuer = "https://idp.example.com/realms/openshell",
            ClientId = "openshell-cli",
            Timeout = TimeSpan.FromMinutes(5),
            DisplayDeviceCode = (url, code) => Console.WriteLine($"Open {url} and enter {code}"),
        });
        #endregion
        Console.WriteLine(tokens.ExpiresAt);
    }

    internal static async Task GatewayLoginAsync()
    {
        #region GatewayLogin
        // Signs in to a registered OIDC gateway as `openshell gateway login` does: the issuer, client id,
        // audience and scopes come from its metadata.json, and the tokens are cached in its oidc_token.json.
        var gateway = new GatewayLocator().Load("prod");
        await OidcLogin.LoginAsync(new OidcOptions { Gateway = gateway });

        // GatewayClient finds the cached token and renews it with the refresh token when it expires.
        using var client = GatewayClient.Connect("prod");
        #endregion
    }

    internal static OpenShellClient MutualTls()
    {
        #region MutualTls
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://localhost:8080"),
            Tls = new TlsOptions
            {
                // A private CA for the gateway certificate; leave it null to use the system roots.
                CaCertificates = [X509Certificate2.CreateFromPemFile("ca.crt")],

                // The client certificate must include its private key.
                ClientCertificate = X509Certificate2.CreateFromPemFile("client.crt", "client.key"),
            },
        });
        #endregion
        return client;
    }

    internal static OpenShellClient InsecureLocal()
    {
        #region InsecureLocal
        // A plaintext gateway on loopback: credentials may be sent to it without any opt-in.
        var local = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("http://127.0.0.1:8080"),
            Auth = Auth.StaticToken("dev-token"),
        });

        // A gateway with a self-signed certificate: skip server certificate validation (development only).
        var selfSigned = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://dev-gateway.internal:8443"),
            Tls = new TlsOptions { InsecureSkipVerify = true },
        });
        #endregion
        local.Dispose();
        return selfSigned;
    }

    internal static OpenShellClient PlaintextOverride()
    {
        #region PlaintextOverride
        // Without AllowInsecureAuth this throws OpenShellException (InvalidConfig): the token would cross
        // the network in clear text.
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("http://gateway.lab.internal:8080"),
            Auth = Auth.StaticToken("lab-token"),
            AllowInsecureAuth = true,
        });
        #endregion
        return client;
    }

    internal static OpenShellClient ExtraHeaders()
    {
        #region ExtraHeaders
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = Auth.WithHeaders(
                Auth.StaticToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!),
                new Dictionary<string, string> { ["x-team"] = "platform" }),
        });
        #endregion
        return client;
    }

    internal static async Task EdgeTunnelAsync(string edgeToken)
    {
        #region EdgeTunnel
        // Cloudflare Access authenticates only GET requests, so gRPC goes through a local WebSocket tunnel.
        await using var proxy = EdgeTunnelProxy.Start("https://gateway.example.com", edgeToken);
        using var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = proxy.LocalUri,
            Auth = Auth.CloudflareAccess(edgeToken),
        });

        var health = await client.Health.CheckAsync();
        #endregion
        Console.WriteLine(health);
    }

    internal static void EdgeRegistered()
    {
        #region EdgeRegistered
        // A registered cloudflare_jwt gateway: GatewayClient starts the tunnel and disposes it with the client.
        using var tunneled = GatewayClient.Connect("edge-gateway");

        // Send the Access header and cookie on direct gRPC calls instead.
        using var direct = GatewayClient.Connect(
            "edge-gateway",
            new GatewayConnectOptions { EdgeTunnel = EdgeTunnelMode.Never });
        #endregion
    }
}
