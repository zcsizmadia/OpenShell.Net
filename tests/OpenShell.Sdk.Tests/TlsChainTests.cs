// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenShell.Transport;

namespace OpenShell.Tests;

/// <summary>Pinned-root validation of a server that presents leaf, intermediate, (root).</summary>
public class TlsChainTests
{
    [Test]
    public async Task Pinned_roots_accept_a_leaf_through_the_intermediate_the_server_sent()
    {
        using var pki = TestPki.Create("pinned");
        var pinned = new X509Certificate2Collection(pki.Root);

        // What SslStream hands the callback: the server-sent intermediates in ExtraStore.
        using var sent = new X509Chain();
        sent.ChainPolicy.ExtraStore.Add(pki.Intermediate);

        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pki.Leaf, sent, SslPolicyErrors.RemoteCertificateChainErrors)).IsTrue();
    }

    [Test]
    public async Task Pinned_roots_still_reject_a_leaf_whose_intermediate_is_missing()
    {
        using var pki = TestPki.Create("pinned");
        var pinned = new X509Certificate2Collection(pki.Root);
        using var empty = new X509Chain();

        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pki.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pki.Leaf, empty, SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
    }

    [Test]
    public async Task Pinned_roots_accept_the_chain_the_platform_assembled()
    {
        using var pki = TestPki.Create("pinned");
        var pinned = new X509Certificate2Collection(pki.Root);

        // The platform's own build fails (the root is not system-trusted) but keeps the elements it found.
        using var built = Built(pki.Leaf, pki.Intermediate);
        built.ChainPolicy.ExtraStore.Clear();

        await Assert.That(built.ChainElements.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pki.Leaf, built, SslPolicyErrors.RemoteCertificateChainErrors)).IsTrue();
    }

    [Test]
    public async Task Pinned_roots_replace_the_system_roots()
    {
        using var pinnedPki = TestPki.Create("pinned");
        using var publicPki = TestPki.Create("public");
        var pinned = new X509Certificate2Collection(pinnedPki.Root);

        // The platform reports no errors, as it would for a certificate from a publicly trusted CA. With pins
        // configured that verdict must not be enough: the certificate has to chain to a pinned root.
        using var publicChain = Built(publicPki.Leaf, publicPki.Intermediate, publicPki.Root);
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, publicPki.Leaf, publicChain, SslPolicyErrors.None)).IsFalse();

        using var pinnedChain = Built(pinnedPki.Leaf, pinnedPki.Intermediate, pinnedPki.Root);
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pinnedPki.Leaf, pinnedChain, SslPolicyErrors.None)).IsTrue();
    }

    [Test]
    public async Task A_root_the_server_sent_is_never_trusted()
    {
        using var pinnedPki = TestPki.Create("pinned");
        using var otherPki = TestPki.Create("other");
        var pinned = new X509Certificate2Collection(pinnedPki.Root);

        using var sent = new X509Chain();
        sent.ChainPolicy.ExtraStore.Add(otherPki.Intermediate);
        sent.ChainPolicy.ExtraStore.Add(otherPki.Root);
        using var built = Built(otherPki.Leaf, otherPki.Intermediate, otherPki.Root);

        await Assert.That(ChannelFactory.ValidateAgainst(pinned, otherPki.Leaf, sent, SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, otherPki.Leaf, built, SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
    }

    [Test]
    public async Task A_name_mismatch_fails_even_with_a_complete_chain_to_a_pinned_root()
    {
        using var pki = TestPki.Create("pinned");
        var pinned = new X509Certificate2Collection(pki.Root);
        using var sent = new X509Chain();
        sent.ChainPolicy.ExtraStore.Add(pki.Intermediate);

        await Assert.That(ChannelFactory.ValidateAgainst(
            pinned, pki.Leaf, sent, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)).IsFalse();
        await Assert.That(ChannelFactory.ValidateAgainst(pinned, pki.Leaf, sent, SslPolicyErrors.RemoteCertificateNameMismatch)).IsFalse();
    }

    [Test]
    public async Task The_handler_callback_passes_the_server_chain_through()
    {
        using var pki = TestPki.Create("pinned");
        using var handler = ChannelFactory.BuildSocketsHandler(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Tls = new TlsOptions { CaCertificates = new X509Certificate2Collection(pki.Root) },
        });
        using var sent = new X509Chain();
        sent.ChainPolicy.ExtraStore.Add(pki.Intermediate);

        var callback = handler.SslOptions.RemoteCertificateValidationCallback!;

        await Assert.That(callback(new object(), pki.Leaf, sent, SslPolicyErrors.RemoteCertificateChainErrors)).IsTrue();
    }

    private static X509Chain Built(X509Certificate2 leaf, params X509Certificate2[] extra)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        _ = chain.Build(leaf); // Fails: the generated root is not trusted by the system.
        return chain;
    }
}

/// <summary>A generated root, intermediate, and leaf (for <c>localhost</c>).</summary>
internal sealed class TestPki : IDisposable
{
    private TestPki(X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf)
    {
        Root = root;
        Intermediate = intermediate;
        Leaf = leaf;
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public X509Certificate2 Leaf { get; }

    public static TestPki Create(string name)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = Ca($"CN={name} root", rootKey);
        var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = Ca($"CN={name} intermediate", intermediateKey);
        using var intermediatePublic = intermediateRequest.Create(root, notBefore, notAfter, Serial());
        var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddDnsName("localhost");
        leafRequest.CertificateExtensions.Add(sans.Build());
        var leaf = leafRequest.Create(intermediate, notBefore, notAfter, Serial());

        return new TestPki(root, intermediate, leaf);
    }

    public void Dispose()
    {
        Leaf.Dispose();
        Intermediate.Dispose();
        Root.Dispose();
    }

    private static CertificateRequest Ca(string subject, ECDsa key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static byte[] Serial() => RandomNumberGenerator.GetBytes(16);
}
