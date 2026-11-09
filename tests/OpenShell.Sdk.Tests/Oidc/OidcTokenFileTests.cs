// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class OidcTokenFileTests
{
    private const string Issuer = "https://issuer.example.com";
    private const string Client = "openshell-cli";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Reads_the_shared_conformance_bundle()
    {
        using var dir = new TempDirectory();
        File.Copy(ConformanceFixture.PathOf("oidc-token-bundle.json"), Path.Join(dir.Path, "oidc_token.json"));

        var stored = OidcTokenFile.Read(dir.Path)!;

        await Assert.That(stored.Tokens.AccessToken).IsEqualTo("conformance-access-token");
        await Assert.That(stored.Tokens.RefreshToken).IsEqualTo("conformance-refresh-token");
        await Assert.That(stored.Tokens.ExpiresAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(4102444800));
        await Assert.That(stored.Issuer).IsEqualTo(Issuer);
        await Assert.That(stored.ClientId).IsEqualTo(Client);
        await Assert.That(stored.IsFor(Issuer, Client)).IsTrue();
    }

    [Test]
    public async Task Writes_the_shared_conformance_bundle_field_for_field()
    {
        using var dir = new TempDirectory();
        var tokens = new OidcTokens("conformance-access-token", "conformance-refresh-token", "Bearer", DateTimeOffset.FromUnixTimeSeconds(4102444800));

        OidcTokenFile.Write(dir.Path, tokens, Issuer, Client);

        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(dir.Path, "oidc_token.json")));
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(ConformanceFixture.PathOf("oidc-token-bundle.json")));
        await Assert.That(Describe(written.RootElement)).IsEqualTo(Describe(expected.RootElement));
    }

    [Test]
    public async Task Tokens_without_expiry_or_refresh_are_written_minimally_and_never_expire()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc"), Issuer, Client);

        var json = await File.ReadAllTextAsync(Path.Join(dir.Path, "oidc_token.json"));
        await Assert.That(json).IsEqualTo("""{"access_token":"acc","issuer":"https://issuer.example.com","client_id":"openshell-cli"}""");

        time.Advance(TimeSpan.FromDays(3650));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, Client, time)!.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task Expiry_is_stored_in_whole_unix_seconds_and_clamped_at_the_epoch()
    {
        using var dir = new TempDirectory();
        var path = Path.Join(dir.Path, "oidc_token.json");

        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", ExpiresAt: Now.AddMilliseconds(900)), Issuer, Client);
        await Assert.That(await File.ReadAllTextAsync(path)).Contains($"\"expires_at\":{Now.ToUnixTimeSeconds()}");

        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", ExpiresAt: DateTimeOffset.UnixEpoch.AddDays(-1)), Issuer, Client);
        await Assert.That(await File.ReadAllTextAsync(path)).Contains("\"expires_at\":0");
    }

    [Test]
    public async Task ReadValid_applies_a_ten_second_leeway_but_Read_does_not()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc", ExpiresAt: Now.AddSeconds(60)), Issuer, Client);

        time.Advance(TimeSpan.FromSeconds(49));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, Client, time)).IsNotNull();
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, Client, time)).IsNull();
        await Assert.That(OidcTokenFile.Read(dir.Path)).IsNotNull();
    }

    [Test]
    public async Task ReadValid_only_returns_tokens_issued_for_the_same_issuer_and_client()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Now);
        OidcTokenFile.Write(dir.Path, new OidcTokens("acc"), Issuer + "/", Client);

        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, Client, time)).IsNotNull();
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, "https://other.example.com", Client, time)).IsNull();
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, "other-client", time)).IsNull();
    }

    [Test]
    public async Task Files_from_early_builds_without_issuer_or_client_match_nothing()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Join(dir.Path, "oidc_token.json"),
            """{"access_token":"old","refresh_token":"r","expiry":"2099-01-01T00:00:00Z","expires_in":3600}""");

        var stored = OidcTokenFile.Read(dir.Path)!;

        await Assert.That(stored.Tokens.AccessToken).IsEqualTo("old");
        await Assert.That(stored.Tokens.ExpiresAt).IsNull();
        await Assert.That(stored.Issuer).IsNull();
        await Assert.That(stored.IsFor(Issuer, Client)).IsFalse();
        await Assert.That(OidcTokenFile.ReadValid(dir.Path, Issuer, Client, TimeProvider.System)).IsNull();
    }

    [Test]
    public async Task Missing_file_directory_and_empty_access_token_read_as_null()
    {
        using var dir = new TempDirectory();
        await Assert.That(OidcTokenFile.Read(dir.Path)).IsNull();
        await Assert.That(OidcTokenFile.Read(dir.Combine("nope"))).IsNull();

        await File.WriteAllTextAsync(Path.Join(dir.Path, "oidc_token.json"), """{"access_token":""}""");
        await Assert.That(OidcTokenFile.Read(dir.Path)).IsNull();

        await File.WriteAllTextAsync(Path.Join(dir.Path, "oidc_token.json"), "null");
        await Assert.That(OidcTokenFile.Read(dir.Path)).IsNull();
    }

    [Test]
    public async Task Corrupt_files_are_reported()
    {
        using var dir = new TempDirectory();
        var path = Path.Join(dir.Path, "oidc_token.json");

        await File.WriteAllTextAsync(path, "{nope");
        var ex1 = await Assert.That(() => OidcTokenFile.Read(dir.Path)).Throws<OidcException>();
        await Assert.That(ex1!.Failure).IsEqualTo(OidcFailure.TokenPersist);

        await File.WriteAllTextAsync(path, """{"access_token":"a","expires_at":"soon"}""");
        var ex2 = await Assert.That(() => OidcTokenFile.Read(dir.Path)).Throws<OidcException>();
        await Assert.That(ex2!.Failure).IsEqualTo(OidcFailure.TokenPersist);

        await File.WriteAllTextAsync(path, """{"access_token":"a","expires_at":18446744073709551615}""");
        var ex3 = await Assert.That(() => OidcTokenFile.Read(dir.Path)).Throws<OidcException>();
        await Assert.That(ex3!.Message).Contains("expires_at");
    }

    [Test]
    public async Task Unreadable_path_is_reported()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Join(dir.Path, "oidc_token.json"));

        var ex = await Assert.That(() => OidcTokenFile.Read(dir.Path)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.TokenPersist);
    }

    [Test]
    public async Task Write_failures_are_reported_and_leave_no_temp_files()
    {
        using var dir = new TempDirectory();
        var blocker = Path.Join(dir.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "x");

        var ex = await Assert.That(() => OidcTokenFile.Write(Path.Join(blocker, "sub"), new OidcTokens("a"), Issuer, Client)).Throws<OidcException>();

        await Assert.That(ex!.Failure).IsEqualTo(OidcFailure.TokenPersist);
        await Assert.That(Directory.GetFiles(dir.Path).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Write_replaces_atomically_and_creates_the_directory()
    {
        using var dir = new TempDirectory();
        var nested = dir.Combine("a", "b");

        OidcTokenFile.Write(nested, new OidcTokens("one"), Issuer, Client);
        OidcTokenFile.Write(nested, new OidcTokens("two"), Issuer, Client);

        await Assert.That(OidcTokenFile.Read(nested)!.Tokens.AccessToken).IsEqualTo("two");
        await Assert.That(Directory.GetFiles(nested).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        OidcTokenFile.Write(dir.Path, new OidcTokens("a"), Issuer, Client);

        await Assert.That(File.GetUnixFileMode(Path.Join(dir.Path, "oidc_token.json"))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The properties of a JSON object in name order, so two bundles compare regardless of layout.</summary>
    private static string Describe(JsonElement element) =>
        string.Join(", ", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetRawText()}"));
}

public class OidcBrowserTests
{
    [Test]
    [Arguments("macos", "open")]
    [Arguments("linux", "xdg-open")]
    [Arguments("freebsd", "xdg-open")]
    public async Task Unix_like_commands(string os, string expected)
    {
        var (file, args) = OidcBrowser.CommandFor(os, "https://x.example.com/a?b=c");

        await Assert.That(file).IsEqualTo(expected);
        await Assert.That(args).IsEquivalentTo(new[] { "https://x.example.com/a?b=c" });
    }

    [Test]
    public async Task Windows_uses_the_url_protocol_handler_without_a_shell()
    {
        var (file, args) = OidcBrowser.CommandFor("windows", "https://x.example.com/a?b=c&d=e");

        await Assert.That(file).IsEqualTo("rundll32");
        await Assert.That(args).IsEquivalentTo(new[] { "url.dll,FileProtocolHandler", "https://x.example.com/a?b=c&d=e" });
    }

    [Test]
    public async Task Only_http_and_https_urls_may_be_opened()
    {
        OidcBrowser.Require(new Uri("https://ok.example.com"));
        OidcBrowser.Require(new Uri("http://localhost:1"));

        await Assert.That(() => OidcBrowser.Require(new Uri("file:///etc/passwd"))).Throws<InvalidOperationException>();
        await Assert.That(() => OidcBrowser.Require(new Uri("javascript:alert(1)"))).Throws<InvalidOperationException>();
        await Assert.That(OidcBrowser.CurrentOs()).IsNotNull();
    }
}
