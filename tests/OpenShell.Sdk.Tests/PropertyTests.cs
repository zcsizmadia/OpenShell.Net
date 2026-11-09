// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using CsCheck;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;
using GoogleRpc = Google.Rpc;

namespace OpenShell.Tests;

/// <summary>
/// Property-based tests for the parsers and validators that see input the SDK does not control: gateway names
/// and files written by the CLI, the OIDC loopback callback, identity-provider responses, gateway error details,
/// and edge tokens. Each property holds for every generated input; CsCheck shrinks a failure to a minimal case.
/// </summary>
public class PropertyTests
{
    // Characters that matter to the validators: path separators, dots, NUL, colons, spaces, controls, non-ASCII.
    private const string Tricky = "abcXYZ019-_./\\:\0 \t\r\né日‮";

    private static readonly Gen<string> NameLike = Gen.OneOf(
        Gen.String[Gen.Char["abcXYZ019-_."], 0, 12],
        Gen.String[Gen.Char[Tricky], 0, 12],
        Gen.OneOf(Gen.Const("CON"), Gen.Const("nul.txt"), Gen.Const("COM1.example.com"), Gen.Const("lpt9"), Gen.Const("..")),
        Gen.String);

    private static readonly string[] DeviceStems = ["CON", "PRN", "AUX", "NUL"];

    [Test]
    public Task Gateway_names_are_either_rejected_or_a_single_safe_path_component()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "openshell-property-root"));

        NameLike.Sample(name =>
        {
            try
            {
                GatewayLocator.ValidateName(name);
            }
            catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.InvalidConfig)
            {
                return;
            }

            Require(name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'), $"accepted unsafe characters in '{name}'");
            Require(!name.StartsWith('.') && !name.EndsWith('.') && !name.Contains("..", StringComparison.Ordinal), $"accepted bad dots in '{name}'");

            var stem = name.Split('.')[0].ToUpperInvariant();
            var device = DeviceStems.Contains(stem)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
            Require(!device, $"accepted the device name '{name}'");

            var full = Path.GetFullPath(Path.Join(root, name));
            Require(string.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal), $"'{name}' escapes the gateway directory");
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Callback_query_parsing_never_throws_and_round_trips_encoded_pairs()
    {
        Gen.String.Sample(query => _ = CallbackServer.ParseQuery(query));

        var key = Gen.String[Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789_"], 1, 8];
        var value = Gen.String[Gen.Char["aZ09 &=%+?#/;é日 "], 0, 16];
        Gen.Select(key, value).Array[0, 6].Sample(pairs =>
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in pairs)
            {
                expected.TryAdd(k, v);
            }

            var query = string.Join('&', pairs.Select(p => WebUtility.UrlEncode(p.Item1) + "=" + WebUtility.UrlEncode(p.Item2)));
            var parsed = CallbackServer.ParseQuery(query);

            Require(parsed.Count == expected.Count, $"'{query}' parsed into {parsed.Count} pairs, expected {expected.Count}");
            foreach (var (k, v) in expected)
            {
                Require(parsed.TryGetValue(k, out var actual) && actual == v, $"'{query}': {k} was '{actual}', expected '{v}'");
            }
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Token_responses_fail_only_with_OidcException_and_never_echo_the_token()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Gen.Byte.Array[0, 64].Sample(body =>
        {
            try
            {
                OidcHttp.ParseTokens(HttpStatusCode.OK, body, OidcFailure.Refresh, requirePositiveExpiry: false, time);
            }
            catch (OidcException)
            {
                // Rejected, as it should be for garbage.
            }
        });

        var token = Gen.String[Gen.Char["abcdefXYZ0123456789-_."], 8, 24].Select(t => "secret-" + t);
        var expiresIn = Gen.OneOf(
            Gen.Double.Where(double.IsFinite).Select(d => d.ToString("R", CultureInfo.InvariantCulture)),
            Gen.Long.Select(l => l.ToString(CultureInfo.InvariantCulture)),
            Gen.OneOf(Gen.Const("1e400"), Gen.Const("-0"), Gen.Const("0"), Gen.Const("\"3600\""), Gen.Const("null"), Gen.Const("true")));
        Gen.Select(token, expiresIn, Gen.Bool).Sample((accessToken, expiry, requireExpiry) =>
        {
            var json = $$"""{"access_token":"{{accessToken}}","expires_in":{{expiry}}}""";
            try
            {
                var tokens = OidcHttp.ParseTokens(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(json), OidcFailure.Refresh, requireExpiry, time);
                Require(tokens.AccessToken == accessToken, "access token changed");
                Require(tokens.ExpiresAt is { } at && at >= time.GetUtcNow() && at <= time.GetUtcNow().AddYears(101), $"expires_in {expiry} gave expiry {tokens.ExpiresAt}");
            }
            catch (OidcException e)
            {
                Require(!e.Message.Contains(accessToken, StringComparison.Ordinal), "the error message echoes the access token");
            }
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Token_files_round_trip_and_unreadable_ones_fail_only_with_OidcException()
    {
        using var dir = new TempDirectory();
        var text = Gen.String[Gen.Char[Tricky + "\"{}[],"], 1, 24];
        var expiry = Gen.Long[0, 253402300799].Select(s => (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(s));

        Gen.Select(text, Gen.OneOf(text.Select(t => (string?)t), Gen.Const((string?)null)), Gen.OneOf(expiry, Gen.Const((DateTimeOffset?)null)), text, text)
            .Sample((access, refresh, expiresAt, issuer, clientId) =>
            {
                OidcTokenFile.Write(dir.Path, new OidcTokens(access, refresh, "Bearer", expiresAt), issuer, clientId);
                var stored = OidcTokenFile.Read(dir.Path)!;
                Require(stored.Tokens.AccessToken == access && stored.Tokens.RefreshToken == refresh && stored.Tokens.ExpiresAt == expiresAt, "tokens did not round-trip");
                Require(stored.Issuer == issuer && stored.ClientId == clientId && stored.IsFor(issuer, clientId), "issuer or client id did not round-trip");
            }, iter: 200, threads: 1);

        var path = Path.Join(dir.Path, OidcTokenFile.FileName);
        Gen.OneOf(Gen.String, Gen.String[Gen.Char["{}[]\":,0123456789.-eE ntrufals_abcikx"], 0, 40]).Sample(content =>
        {
            File.WriteAllText(path, content);
            try
            {
                _ = OidcTokenFile.Read(dir.Path);
            }
            catch (OidcException)
            {
                // Reported as an unreadable token file.
            }
        }, iter: 200, threads: 1);

        return Task.CompletedTask;
    }

    [Test]
    public Task Provider_urls_are_accepted_only_over_https_or_loopback_http_without_userinfo_or_fragment()
    {
        var url = Gen.Select(
            Gen.OneOf(Gen.Const("https"), Gen.Const("http"), Gen.Const("HTTPS"), Gen.Const("ftp"), Gen.Const("file")),
            Gen.OneOf(Gen.Const(""), Gen.Const("user@"), Gen.Const("u:p@")),
            Gen.OneOf(Gen.Const("example.com"), Gen.Const("127.0.0.1"), Gen.Const("localhost"), Gen.Const("[::1]"), Gen.Const("10.0.0.1"), Gen.Const("")),
            Gen.OneOf(Gen.Const(""), Gen.Const(":8443"), Gen.Const("/x"), Gen.Const("/a?b=c")),
            Gen.OneOf(Gen.Const(""), Gen.Const("#f"), Gen.Const("#")),
            (scheme, user, host, path, fragment) => $"{scheme}://{user}{host}{path}{fragment}");

        Gen.OneOf(url, Gen.String).Sample(raw =>
        {
            try
            {
                OidcUrl.Validate("issuer", raw);
            }
            catch (OidcException)
            {
                return;
            }

            var uri = new Uri(raw);
            var loopback = uri.IsLoopback;
            Require(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && loopback), $"accepted '{raw}'");
            Require(uri.UserInfo.Length == 0 && !raw.Contains('#', StringComparison.Ordinal), $"accepted userinfo or a fragment in '{raw}'");
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Error_details_never_throw_and_retry_delays_stay_in_range()
    {
        static RpcException WithTrailer(byte[] trailer) =>
            new(new Status(StatusCode.Unavailable, "busy"), new Metadata { { "grpc-status-details-bin", trailer } });

        Gen.Byte.Array[0, 64].Sample(bytes => _ = ErrorDetails.From(WithTrailer(bytes)));

        Gen.Select(Gen.Long, Gen.Int).Sample((seconds, nanos) =>
        {
            var envelope = new GoogleRpc.Status { Code = (int)StatusCode.Unavailable, Message = "busy" };
            envelope.Details.Add(Any.Pack(new GoogleRpc.RetryInfo { RetryDelay = new Duration { Seconds = seconds, Nanos = nanos } }));

            var delay = ErrorDetails.From(WithTrailer(envelope.ToByteArray())).RetryDelay;

            Require(delay is null || (delay >= TimeSpan.Zero && delay <= TimeSpan.FromSeconds(315_576_000_001)), $"{seconds}s {nanos}ns gave {delay}");
            var valid = seconds is >= 0 and <= 315_576_000_000 && nanos is >= 0 and < 1_000_000_000;
            Require(valid == delay.HasValue, $"{seconds}s {nanos}ns: valid={valid} but delay={delay}");
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Edge_tokens_are_accepted_only_when_every_character_is_cookie_safe()
    {
        Gen.OneOf(
            Gen.String[Gen.Char["abcXYZ019._~-"], 1, 16],
            Gen.String[Gen.Char["abc._~-;= ,\r\n\t\"é"], 1, 16],
            Gen.String[Gen.Char["abcXYZ019._~-"], 1, 16].Select(t => t + "\n"),
            Gen.String).Sample(token =>
        {
            try
            {
                Auth.CloudflareAccess(token);
            }
            catch (Exception e) when (e is OpenShellException or ArgumentException)
            {
                return;
            }

            Require(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '-'), $"accepted '{Escape(token)}'");
        });

        return Task.CompletedTask;
    }

    [Test]
    public Task Provider_error_codes_and_fake_dns_labels_reject_a_trailing_newline()
    {
        // In .NET, `$` also matches before a final "\n"; every validator anchors with `\z` instead.
        var code = Gen.OneOf(
            Gen.String[Gen.Char["abcXYZ019_.-"], 1, 70],
            Gen.String[Gen.Char["abcXYZ019_.-"], 1, 16].Select(c => c + "\n"),
            Gen.String[Gen.Char["abc_.- \r\n\t:é"], 1, 16]);
        code.Sample(value =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = value }));
            var safe = value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
            Require((OidcHttp.ErrorCode(doc.RootElement) is not null) == safe, $"error code '{Escape(value)}' handled wrongly");
        });

        var label = Gen.OneOf(
            Gen.String[Gen.Char["abc019-"], 1, 12],
            Gen.String[Gen.Char["abc019"], 1, 12].Select(l => l + "\n"));
        label.Sample(value =>
        {
            var valid = value[0] is not '-' && value[^1] is not '-' && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
            Require(OpenShell.Testing.FakeGateway.DnsLabel().IsMatch(value) == valid, $"DNS label '{Escape(value)}' handled wrongly");
        });

        return Task.CompletedTask;
    }

    private static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
