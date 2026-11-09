// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenShell.Oidc;

namespace OpenShell.Tests.Support;

/// <summary>A request captured by <see cref="StubHandler"/>, with its body read eagerly.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Url, string Body, HttpRequestHeaders Headers)
{
    public Dictionary<string, string> Form => CallbackServer.ParseQuery(Body);
}

/// <summary>An <see cref="HttpMessageHandler"/> that records requests and answers them from a delegate.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public int CountTo(string pathSuffix)
    {
        lock (Requests)
        {
            return Requests.Count(r => r.Url.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> requests to a path ending in <paramref name="pathSuffix"/> have
    /// arrived (bounded by <see cref="Waits.Budget"/>). Signals when a request arrives, before it is answered.
    /// </summary>
    public async Task WaitForRequestsAsync(string pathSuffix, int count)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task arrived;
            lock (Requests)
            {
                if (Requests.Count(r => r.Url.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal)) >= count)
                {
                    return;
                }

                arrived = _arrived.Task;
            }

            await Waits.SignalWithin(
                arrived,
                Waits.Budget - clock.Elapsed,
                $"{count} request(s) to '*{pathSuffix}' did not arrive within {Waits.Budget.TotalSeconds:0} s of real time.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers);
        TaskCompletionSource arrived;
        lock (Requests)
        {
            Requests.Add(recorded);
            arrived = _arrived;
            _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        arrived.TrySetResult();
        return Respond(recorded);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A stub provider serving discovery plus caller-defined token and device endpoints.</summary>
    public static StubHandler Provider(
        string issuer,
        Func<RecordedRequest, HttpResponseMessage>? token = null,
        Func<RecordedRequest, HttpResponseMessage>? device = null,
        bool pkce = true,
        bool deviceEndpoint = false,
        Func<string, string>? discoveryTransform = null)
    {
        string DiscoveryDocument()
        {
            var trimmed = issuer.TrimEnd('/');
            var doc = $$"""
            {"issuer":"{{issuer}}","authorization_endpoint":"{{trimmed}}/authorize","token_endpoint":"{{trimmed}}/token"
            {{(deviceEndpoint ? $",\"device_authorization_endpoint\":\"{trimmed}/device\"" : "")}}
            {{(pkce ? ",\"code_challenge_methods_supported\":[\"plain\",\"S256\"]" : "")}}}
            """;
            return discoveryTransform?.Invoke(doc) ?? doc;
        }

        var handler = new StubHandler
        {
            Respond = request =>
            {
                var path = request.Url.AbsolutePath;
                return path switch
                {
                    _ when path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) => Json(DiscoveryDocument()),
                    _ when path.EndsWith("/token", StringComparison.Ordinal) && token is not null => token(request),
                    _ when path.EndsWith("/device", StringComparison.Ordinal) && device is not null => device(request),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }
        };
        return handler;
    }
}
