// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenShell.Transport;

namespace OpenShell.Tests;

public class TransportTests
{
    private static OpenShellClientOptions Options(string address, IAuthProvider? auth = null, bool allowInsecure = false, TlsOptions? tls = null) =>
        new()
        {
            Address = new Uri(address),
            Auth = auth ?? Auth.None,
            AllowInsecureAuth = allowInsecure,
            Tls = tls,
        };

    [Test]
    [Arguments("http://localhost:8080")]
    [Arguments("http://127.0.0.1:8080")]
    [Arguments("http://[::1]:8080")]
    public async Task Token_over_plaintext_loopback_is_allowed(string address)
    {
        ChannelFactory.Validate(Options(address, Auth.StaticToken("t")));
        await Assert.That(ChannelFactory.IsLoopback(new Uri(address))).IsTrue();
    }

    [Test]
    public async Task Token_over_plaintext_remote_is_refused()
    {
        var ex = await Assert.That(() => ChannelFactory.Validate(Options("http://gateway.example.com", Auth.StaticToken("t"))))
            .Throws<OpenShellException>();
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        await Assert.That(ex.Message).Contains("gateway.example.com");
    }

    [Test]
    public async Task Token_over_plaintext_remote_can_be_opted_in()
    {
        ChannelFactory.Validate(Options("http://gateway.example.com", Auth.StaticToken("t"), allowInsecure: true));
        await Task.CompletedTask;
    }

    [Test]
    public async Task No_credentials_over_plaintext_remote_is_fine()
    {
        ChannelFactory.Validate(Options("http://gateway.example.com"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Https_with_token_is_fine()
    {
        ChannelFactory.Validate(Options("https://gateway.example.com", Auth.StaticToken("t")));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("ftp://gateway.example.com")]
    [Arguments("grpc://gateway.example.com")]
    public async Task Unsupported_scheme_is_rejected(string address)
    {
        var ex = await Assert.That(() => ChannelFactory.Validate(Options(address))).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("gateway address must be an absolute http:// or https:// URL");
        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
    }

    [Test]
    public async Task Relative_address_is_rejected()
    {
        var options = new OpenShellClientOptions { Address = new Uri("/relative", UriKind.Relative) };
        var ex = await Assert.That(() => ChannelFactory.Validate(options)).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("gateway address must be an absolute http:// or https:// URL");
    }

    [Test]
    public async Task Missing_address_is_rejected()
    {
        var ex = await Assert.That(() => ChannelFactory.Validate(new OpenShellClientOptions { Address = null! })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        await Assert.That(ex.Message).IsEqualTo("gateway address is required");
    }

    [Test]
    public async Task Blank_default_workspace_is_rejected()
    {
        var options = new OpenShellClientOptions { Address = new Uri("http://localhost"), DefaultWorkspace = " " };
        var ex = await Assert.That(() => ChannelFactory.Validate(options)).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("default workspace must not be empty");
    }

    [Test]
    public async Task Client_certificate_without_private_key_is_rejected()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
        using var withKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#if NET9_0_OR_GREATER
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
#else
        using var publicOnly = new X509Certificate2(withKey.RawData);
#endif

        var options = Options("https://gateway.example.com", tls: new TlsOptions { ClientCertificate = publicOnly });
        var ex = await Assert.That(() => ChannelFactory.Validate(options)).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("mTLS client certificate must include its private key");
    }

    [Test]
    public async Task Null_options_throw()
    {
        await Assert.That(() => ChannelFactory.Validate(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Create_builds_channel_for_valid_options()
    {
        using var channel = ChannelFactory.Create(Options("http://localhost:9"));
        await Assert.That(channel.Target).Contains("localhost");
    }

    [Test]
    public async Task Create_builds_https_channel_with_tls_options()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=ca", key, HashAlgorithmName.SHA256);
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var roots = new X509Certificate2Collection(ca);

        using var pinned = ChannelFactory.Create(Options("https://gateway.example.com", tls: new TlsOptions { CaCertificates = roots }));
        using var insecure = ChannelFactory.Create(Options("https://gateway.example.com", tls: new TlsOptions { InsecureSkipVerify = true }));
        using var mtls = ChannelFactory.Create(Options("https://gateway.example.com", tls: new TlsOptions { ClientCertificate = ca }));
        await Assert.That(pinned.Target).Contains("gateway.example.com");
    }

    [Test]
    public async Task Sockets_handler_keeps_long_lived_http2_connections()
    {
        using var handler = ChannelFactory.BuildSocketsHandler(Options("http://localhost:9"));

        await Assert.That(handler.PooledConnectionIdleTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
        await Assert.That(handler.KeepAlivePingDelay).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(handler.KeepAlivePingTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(handler.EnableMultipleHttp2Connections).IsTrue();
    }

    [Test]
    public async Task Sockets_handler_applies_tls_options_only_to_https()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=ca", key, HashAlgorithmName.SHA256);
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var roots = new X509Certificate2Collection(ca);
        var sender = new object();

        using var plain = ChannelFactory.BuildSocketsHandler(Options("http://localhost:9", tls: new TlsOptions { InsecureSkipVerify = true, ClientCertificate = ca }));
        using var noTls = ChannelFactory.BuildSocketsHandler(Options("https://gateway.example.com"));
        using var insecure = ChannelFactory.BuildSocketsHandler(Options("https://gateway.example.com", tls: new TlsOptions { InsecureSkipVerify = true, CaCertificates = roots }));
        using var pinned = ChannelFactory.BuildSocketsHandler(Options("https://gateway.example.com", tls: new TlsOptions { CaCertificates = roots }));
        using var mtls = ChannelFactory.BuildSocketsHandler(Options("https://gateway.example.com", tls: new TlsOptions { ClientCertificate = ca }));

        await Assert.That(plain.SslOptions.RemoteCertificateValidationCallback).IsNull();
        await Assert.That(plain.SslOptions.ClientCertificates).IsNull();
        await Assert.That(noTls.SslOptions.RemoteCertificateValidationCallback).IsNull();

        // Skip-verify wins over pinned roots and accepts anything; pinned roots still reject a name mismatch.
        await Assert.That(insecure.SslOptions.RemoteCertificateValidationCallback!(sender, ca, null, SslPolicyErrors.RemoteCertificateNameMismatch)).IsTrue();
        await Assert.That(pinned.SslOptions.RemoteCertificateValidationCallback!(sender, ca, null, SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
        await Assert.That(pinned.SslOptions.RemoteCertificateValidationCallback!(sender, ca, null, SslPolicyErrors.None)).IsTrue();

        await Assert.That(mtls.SslOptions.ClientCertificates!.Cast<X509Certificate>().Single()).IsSameReferenceAs(ca);
        await Assert.That(mtls.SslOptions.RemoteCertificateValidationCallback).IsNull();
    }

    [Test]
    public async Task Supplied_http_handler_is_wrapped_and_disposed_with_the_channel()
    {
        var supplied = new CapturingHandler();
        var options = Options("http://localhost:9") with { HttpHandler = supplied };

        using (var handler = ChannelFactory.CreateHandler(Options("http://localhost:9")))
        {
            await Assert.That(handler.InnerHandler).IsTypeOf<SocketsHttpHandler>();
        }

        await Assert.That(ChannelFactory.CreateHandler(options).InnerHandler).IsSameReferenceAs(supplied);

        using var channel = ChannelFactory.Create(options);
        await Assert.That(supplied.Disposed).IsFalse();
        channel.Dispose(); // Explicit, to assert the handler is disposed with the channel; the using covers a failed assertion.
        await Assert.That(supplied.Disposed).IsTrue();
    }

    [Test]
    public async Task AuthHandler_applies_provider_to_requests()
    {
        var inner = new CapturingHandler();
        using var handler = new AuthHandler(Auth.StaticToken("tok"), inner);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/");
        using var _ = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(inner.Seen!.Headers.Authorization!.Parameter).IsEqualTo("tok");
    }

    [Test]
    public async Task Custom_ca_validation_rejects_name_mismatch_and_missing_cert()
    {
        var roots = new X509Certificate2Collection();
        await Assert.That(ChannelFactory.ValidateAgainst(roots, null, null, SslPolicyErrors.None)).IsFalse();

        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=leaf", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        await Assert.That(ChannelFactory.ValidateAgainst(roots, cert, null, SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
        // The platform's verdict (here: trusted) never overrides the pins; this certificate chains to none of them.
        await Assert.That(ChannelFactory.ValidateAgainst(roots, cert, null, SslPolicyErrors.None)).IsFalse();
    }

    [Test]
    public async Task Custom_ca_validation_accepts_chain_to_pinned_root_and_rejects_unknown()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=root", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var otherKey = ECDsa.Create();
        var otherReq = new CertificateRequest("CN=other", otherKey, HashAlgorithmName.SHA256);
        otherReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var other = otherReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var pinned = new X509Certificate2Collection(root);
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, root, null, SslPolicyErrors.RemoteCertificateChainErrors)).IsTrue();
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, other, null, SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();

        // A missing certificate or a name mismatch is never rescued by the pinned roots, alone or with chain errors.
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, root, null, SslPolicyErrors.RemoteCertificateNotAvailable)).IsFalse();
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, root, null, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
