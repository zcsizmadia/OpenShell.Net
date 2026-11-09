// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class CallbackServerTests
{
    private static async Task<string> SendRaw(int port, byte[] request, bool shutdownSend = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(request);
        if (shutdownSend)
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }

        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadToEndAsync(cts.Token);
    }

    private static Task<string> Get(int port, string target) =>
        SendRaw(port, Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: localhost\r\n\r\n"));

    [Test]
    public async Task Delivers_the_code_and_percent_decodes_parameters()
    {
        await using var server = CallbackServer.TryStart(0, "st ate")!;

        var response = await Get(server.Port, "/callback?code=a%2Fb%3D%3D&state=st%20ate&junk&state=ignored-duplicate");

        await Assert.That(response).Contains("200 OK");
        await Assert.That(await server.WaitForCodeAsync().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("a/b==");
    }

    [Test]
    public async Task Responses_are_not_cacheable_and_close_the_connection()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        var response = await Get(server.Port, "/callback?code=c&state=s");

        await Assert.That(response).Contains("Cache-Control: no-store");
        await Assert.That(response).Contains("Connection: close");
    }

    [Test]
    public async Task Malformed_requests_get_405_and_do_not_complete_the_login()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        await Assert.That(await SendRaw(server.Port, "GARBAGE\r\n"u8.ToArray())).Contains("405");
        await Assert.That(await SendRaw(server.Port, "POST /callback HTTP/1.1\r\n\r\n"u8.ToArray())).Contains("405");
        await Assert.That(await SendRaw(server.Port, "GET\r\n"u8.ToArray())).Contains("405");

        await Assert.That(server.WaitForCodeAsync().IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_client_that_disconnects_without_a_request_is_tolerated()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
        }

        var response = await SendRaw(server.Port, [], shutdownSend: true);
        await Assert.That(response).Contains("405");

        // The listener is still serving.
        await Assert.That(await Get(server.Port, "/callback?code=c&state=s")).Contains("200 OK");
    }

    [Test]
    public async Task A_request_head_without_a_newline_is_cut_off_at_the_limit()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        try
        {
            // The server answers 405 and may reset the connection because unread bytes remain.
            var response = await SendRaw(server.Port, new byte[20_000]);
            await Assert.That(response).Contains("405");
        }
        catch (IOException)
        {
            // A reset is an equally acceptable way to refuse an oversized head.
        }

        await Assert.That(await Get(server.Port, "/callback?code=c&state=s")).Contains("200 OK");
    }

    [Test]
    public async Task Provider_errors_with_a_matching_state_fault_the_wait_but_wrong_state_errors_are_ignored()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        await Assert.That(await Get(server.Port, "/callback?error=access_denied&state=wrong")).Contains("400");
        await Assert.That(server.WaitForCodeAsync().IsCompleted).IsFalse();

        await Get(server.Port, "/callback?error=access_denied&state=s");
        await Assert.That(async () => await server.WaitForCodeAsync()).Throws<OidcException>();
    }

    [Test]
    public async Task An_empty_error_parameter_is_not_an_error()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        await Get(server.Port, "/callback?error=&code=c&state=s");

        await Assert.That(await server.WaitForCodeAsync().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("c");
    }

    [Test]
    public async Task The_first_valid_code_wins()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        await Get(server.Port, "/callback?code=first&state=s");
        await Get(server.Port, "/callback?code=second&state=s");

        await Assert.That(await server.WaitForCodeAsync()).IsEqualTo("first");
    }

    [Test]
    public async Task Disposing_with_a_stalled_connection_open_returns_promptly()
    {
        var server = CallbackServer.TryStart(0, "s")!;
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Loopback, server.Port);
        await stalled.GetStream().WriteAsync("GET /callba"u8.ToArray());
        await Task.Delay(50);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(server.WaitForCodeAsync().IsCompleted).IsFalse();
    }

    [Test]
    public async Task Binding_a_busy_port_returns_null()
    {
        await using var first = CallbackServer.TryStart(0, "s")!;

        await Assert.That(CallbackServer.TryStart(first.Port, "s")).IsNull();
    }

    [Test]
    public async Task Parses_queries_without_values_and_empty_input()
    {
        var parsed = CallbackServer.ParseQuery("a&b=&c=1&c=2&=v");

        await Assert.That(parsed["a"]).IsEqualTo("");
        await Assert.That(parsed["b"]).IsEqualTo("");
        await Assert.That(parsed["c"]).IsEqualTo("1");
        await Assert.That(parsed[""]).IsEqualTo("v");
        await Assert.That(CallbackServer.ParseQuery("").Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("/elsewhere?code=c&state=s", "404 Not Found", "not found")]
    [Arguments("/callback?code=c&state=wrong", "400 Bad Request", "state mismatch")]
    [Arguments("/callback?state=s", "400 Bad Request", "missing authorization code")]
    [Arguments("/callback?code=&state=s", "400 Bad Request", "missing authorization code")]
    [Arguments("/callback?error=access_denied&state=s", "400 Bad Request", "authorization failed")]
    [Arguments("/callback?code=c&state=s", "200 OK", "<html><body><h1>Login successful</h1><p>You can close this window.</p></body></html>")]
    public async Task Each_outcome_has_its_own_status_and_body(string target, string status, string body)
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        var response = await Get(server.Port, target);

        await Assert.That(response).StartsWith($"HTTP/1.1 {status}\r\n");
        await Assert.That(response).EndsWith($"\r\n\r\n{body}");
    }

    [Test]
    public async Task A_rejected_method_names_the_reason()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        var response = await SendRaw(server.Port, "POST /callback?code=c&state=s HTTP/1.1\r\n\r\n"u8.ToArray());

        await Assert.That(response).StartsWith("HTTP/1.1 405 Method Not Allowed\r\n");
        await Assert.That(response).EndsWith("\r\n\r\nmethod not allowed");
    }

    [Test]
    public async Task A_provider_error_faults_the_wait_with_an_auth_code_failure()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        await Get(server.Port, "/callback?error=access_denied&state=s");

        var ex = await Assert.That(async () => await server.WaitForCodeAsync()).Throws<OidcException>();
        await Assert.That(ex!.Message).Contains("provider returned an authorization error");
    }

    [Test]
    public async Task A_request_line_without_a_protocol_version_is_accepted()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        var response = await SendRaw(server.Port, "GET /callback?code=c&state=s\r\n"u8.ToArray());

        await Assert.That(response).Contains("200 OK");
        await Assert.That(await server.WaitForCodeAsync().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("c");
    }

    [Test]
    public async Task An_empty_first_line_is_answered_at_once()
    {
        await using var server = CallbackServer.TryStart(0, "s")!;

        // No shutdown: the server must answer on the newline rather than wait for more bytes or the timeout.
        var response = await SendRaw(server.Port, "\nGET /callback?code=c&state=s HTTP/1.1\r\n"u8.ToArray());

        await Assert.That(response).Contains("405");
        await Assert.That(server.WaitForCodeAsync().IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_client_that_never_finishes_its_request_line_is_dropped_after_the_timeout()
    {
        await using var server = CallbackServer.TryStart(0, "s", TimeSpan.FromMilliseconds(200))!;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync("GET /callba"u8.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(new byte[16], cts.Token);

        // The server closed the connection without answering.
        await Assert.That(read).IsEqualTo(0);
        await Assert.That(await Get(server.Port, "/callback?code=c&state=s")).Contains("200 OK");
    }
}

public class OidcEdgeCaseTests
{
    private const string Issuer = "https://issuer.example.com";

    [Test]
    public async Task Discovery_without_an_issuer_field_is_rejected()
    {
        var handler = new StubHandler { Respond = _ => StubHandler.Json($$"""{"token_endpoint":"{{Issuer}}/t","authorization_endpoint":"{{Issuer}}/a"}""") };

        var ex = await Assert.That(async () => await new OidcDiscovery(new HttpClient(handler)).DiscoverAsync(Issuer)).Throws<OidcException>();

        await Assert.That(ex!.Message).Contains("does not match");
    }

    [Test]
    public async Task Connect_with_the_default_locator_reports_unknown_gateways()
    {
        var ex = await Assert.That(() => GatewayClient.Connect("definitely-not-registered-zz9")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
    }

    [Test]
    public async Task Gateway_audience_and_empty_values_resolve_as_documented()
    {
        var gateway = new GatewayMetadata
        {
            Name = "gw",
            Endpoint = new Uri("https://gw.example.com"),
            Directory = "/unused",
            OidcIssuer = Issuer,
            OidcClientId = "gw-client",
            OidcAudience = "gw-aud",
        };

        var resolved = OidcResolved.Create(new OidcOptions { Gateway = gateway, Audience = "" }, requireSecret: false);

        // An explicit empty audience overrides the gateway's and means "none".
        await Assert.That(resolved.Audience).IsNull();
        await Assert.That(resolved.ClientId).IsEqualTo("gw-client");
        await Assert.That(resolved.InteractiveScopes.Count).IsEqualTo(3);
        await Assert.That(resolved.TokenDirectory).IsEqualTo("/unused");
    }

    [Test]
    public async Task Authorization_url_lists_the_parameters_in_order_without_a_stray_separator()
    {
        var url = OidcFlows.BuildAuthorizationUrl("https://idp.example.com/auth", "cli", "http://localhost:1/callback", "st", "ch", ["openid", "email"]);

        await Assert.That(url.Query).IsEqualTo("?response_type=code&client_id=cli&redirect_uri=http%3A%2F%2Flocalhost%3A1%2Fcallback&state=st&scope=openid%20email&code_challenge=ch&code_challenge_method=S256");
    }

    [Test]
    public async Task Existing_query_parameters_on_the_authorization_endpoint_survive()
    {
        var url = OidcFlows.BuildAuthorizationUrl("https://idp.example.com/auth?tenant=a", "cli", "http://localhost:1/callback", "st", null, ["openid"]);

        var query = CallbackServer.ParseQuery(url.Query.TrimStart('?'));
        await Assert.That(query["tenant"]).IsEqualTo("a");
        await Assert.That(query["client_id"]).IsEqualTo("cli");
        await Assert.That(query.ContainsKey("code_challenge")).IsFalse();
    }
}
