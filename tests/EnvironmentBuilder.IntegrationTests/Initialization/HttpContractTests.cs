using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Initialization;

namespace EnvironmentBuilder.IntegrationTests.Initialization;

public sealed class HttpContractTests
{
    [Fact]
    public void Default_handler_does_not_follow_redirects_or_inherit_network_credentials()
    {
        using SocketsHttpHandler handler = HttpInitializationTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials); Assert.False(handler.PreAuthenticate); Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task Endpoint_ownership_is_resolved_again_for_each_dispatch()
    {
        int resolutions = 0; var addresses = new List<int>();
        using var handler = new StreamingHandler((request, _) =>
        { addresses.Add(request.RequestUri!.Port); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); });
        using var transport = new HttpInitializationTransport(handler, (_, _, _) => Task.FromResult(new Uri("http://127.0.0.1:" + (49152 + resolutions++))), _ => "");
        await transport.SendAsync(Guid.NewGuid(), Step("POST", "/seed"), "stable", null, TestContext.Current.CancellationToken);
        await transport.SendAsync(Guid.NewGuid(), Step("POST", "/seed"), "stable", null, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 49152, 49153 }, addresses); Assert.Equal(2, resolutions);
    }

    [Fact]
    public async Task Section_body_is_streamed_and_caller_keeps_ownership_with_a_stable_idempotency_header()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"id\":1}\n");
        await using var body = new MemoryStream(bytes);
        using var handler = new StreamingHandler(async (request, token) =>
        {
            Assert.Equal("stable", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            Assert.Equal("application/x-ndjson", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var transport = new HttpInitializationTransport(handler, (_, _) => new Uri("http://127.0.0.1:49152/v1"), value => value.Literal!);
        var step = Step("POST", "/seed") with { Body = new("seedSection") { SectionId = "records" }, Retry = new("key", 3) { IdempotencyHeader = "Idempotency-Key", SafetyEvidence = "server durable deduplication" } };
        Assert.True((await transport.SendAsync(Guid.NewGuid(), step, "stable", body, TestContext.Current.CancellationToken)).AssertionsPassed);
        Assert.True(body.CanRead);
    }

    [Fact]
    public async Task HEAD_without_assertions_and_redirect_response_do_not_trigger_a_second_request()
    {
        int sends = 0;
        using var handler = new StreamingHandler((request, _) =>
        {
            sends++; Assert.Equal(HttpMethod.Head, request.Method); Assert.Null(request.Content);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://foreign.invalid");
            return Task.FromResult(response);
        });
        using var transport = new HttpInitializationTransport(handler, (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        var response = await transport.SendAsync(Guid.NewGuid(), Step("HEAD", "/") with { Expect = new([302], []) }, "", null, TestContext.Current.CancellationToken);
        Assert.True(response.AssertionsPassed); Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Secret_header_controls_are_rejected_before_dispatch()
    {
        var handler = new RecordingHandler(_ => Response("{}"));
        using var transport = new HttpInitializationTransport(handler, (_, _) => new Uri("http://127.0.0.1:49152"), _ => "value\r\nHost: foreign");
        var step = Step("GET", "/") with { Headers = ImmutableDictionary<string, Value>.Empty.Add("Authorization", new() { SecretRef = "secret://" + Guid.NewGuid() }) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Guid.NewGuid(), step, "", null, TestContext.Current.CancellationToken));
        Assert.Null(handler.RequestUri);
    }
    [Fact]
    public async Task Base_path_is_preserved_and_empty_assertions_accept_204_without_json()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = new HttpInitializationTransport(handler, (_, _) => new Uri("http://127.0.0.1:49152/v1"), _ => "resolved");
        var result = await transport.SendAsync(Guid.NewGuid(), Step("POST", "/admin"), "stable", null, TestContext.Current.CancellationToken);
        Assert.Equal("http://127.0.0.1:49152/v1/admin", handler.RequestUri);
        Assert.True(result.AssertionsPassed && result.OutcomeKnown);
        Assert.Equal(204, result.Status);
    }

    [Fact]
    public async Task Assertions_use_structural_equality_and_response_is_bounded()
    {
        InitializationStep step = Step("GET", "/counts") with
        { Expect = new([200], [new JsonAssertion("/a~1b/0", JsonSerializer.SerializeToElement(1))]) };
        using var valid = new HttpInitializationTransport(new RecordingHandler(_ => Response("{\"a/b\":[1]}")), (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        Assert.True((await valid.SendAsync(Guid.NewGuid(), step, "key", null, TestContext.Current.CancellationToken)).AssertionsPassed);
        using var mismatch = new HttpInitializationTransport(new RecordingHandler(_ => Response("{\"a/b\":[\"1\"]}")), (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        Assert.False((await mismatch.SendAsync(Guid.NewGuid(), step, "key", null, TestContext.Current.CancellationToken)).AssertionsPassed);
        using var huge = new HttpInitializationTransport(new RecordingHandler(_ => Response(new string('x', 1048577))), (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        Assert.False((await huge.SendAsync(Guid.NewGuid(), step, "key", null, TestContext.Current.CancellationToken)).OutcomeKnown);
    }

    [Fact]
    public async Task Lost_response_after_dispatch_is_unknown_and_has_no_raw_error()
    {
        using var transport = new HttpInitializationTransport(new RecordingHandler(_ => throw new HttpRequestException("secret body")), (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        var result = await transport.SendAsync(Guid.NewGuid(), Step("POST", "/seed"), "stable", null, TestContext.Current.CancellationToken);
        Assert.False(result.OutcomeKnown);
        Assert.DoesNotContain("secret", result.ErrorCode!);
        Assert.False(InitializationRetryPolicy.CanRetry(Step("POST", "/seed").Retry, 1, result));
    }

    [Theory]
    [InlineData("/%252e%252e/seed")]
    [InlineData("//foreign/seed")]
    [InlineData("/%0d%0aHost:foreign")]
    public async Task Unsafe_request_targets_never_dispatch(string path)
    {
        var handler = new RecordingHandler(_ => Response("{}"));
        using var transport = new HttpInitializationTransport(handler, (_, _) => new Uri("http://127.0.0.1:49152"), _ => "");
        await Assert.ThrowsAsync<DocumentException>(() => transport.SendAsync(Guid.NewGuid(), Step("GET", path), "key", null, TestContext.Current.CancellationToken));
        Assert.Null(handler.RequestUri);
    }

    internal static InitializationStep Step(string method, string path) => new("step", "Step", "control", method, path,
        ImmutableDictionary<string, Value>.Empty, new("none"), new([200, 204], []), 5, new("none", 1));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { RequestUri = request.RequestUri!.AbsoluteUri; return Task.FromResult(response(request)); }
    }
    private sealed class StreamingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
