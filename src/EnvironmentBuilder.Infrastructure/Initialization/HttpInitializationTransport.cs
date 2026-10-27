using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;

namespace EnvironmentBuilder.Infrastructure.Initialization;

public sealed class HttpInitializationTransport : IInitializationTransport, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<Guid, string, CancellationToken, Task<Uri>> endpoint;
    private readonly Func<Value, string> resolve;
    public HttpInitializationTransport(Func<Guid, string, Uri> endpoint, Func<Value, string> resolve)
        : this(CreateHandler(), endpoint, resolve) { }
    public HttpInitializationTransport(HttpMessageHandler handler, Func<Guid, string, Uri> endpoint, Func<Value, string> resolve)
        : this(handler, (component, id, _) => Task.FromResult(endpoint(component, id)), resolve) { }
    public HttpInitializationTransport(Func<Guid, string, CancellationToken, Task<Uri>> endpoint, Func<Value, string> resolve)
        : this(CreateHandler(), endpoint, resolve) { }
    public HttpInitializationTransport(HttpMessageHandler handler, Func<Guid, string, CancellationToken, Task<Uri>> endpoint, Func<Value, string> resolve)
    { client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; this.endpoint = endpoint; this.resolve = resolve; }
    public static SocketsHttpHandler CreateHandler() => new()
    { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null, PreAuthenticate = false, MaxResponseHeadersLength = 32, ConnectTimeout = TimeSpan.FromSeconds(10) };

    public async Task<InitializationResponse> SendAsync(Guid componentId, InitializationStep step, string idempotencyKey, Stream? body, CancellationToken token)
    {
        InitializationValidator.RequireRequestPath(step.Path, "/step/path");
        // Run the shared validator, including hop-by-hop headers, even for direct transport callers.
        InitializationValidator.Validate(new("initialization", 1, Guid.NewGuid(), "1.0.0", new("transport.contract", ["1.0.0"]), [step]));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
        Uri target = await endpoint(componentId, step.EndpointId, timeout.Token).ConfigureAwait(false);
        if (target.Host != "127.0.0.1" || target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
        { throw new InvalidOperationException("Only controlled loopback endpoints are allowed."); }
        InitializationValidator.RequireRequestPath(target.AbsolutePath.TrimEnd('/') is "" ? "/" : target.AbsolutePath.TrimEnd('/'), "/endpoint/basePath", true);
        var uri = new Uri(target.GetLeftPart(UriPartial.Authority) + target.AbsolutePath.TrimEnd('/') + step.Path);
        using var request = new HttpRequestMessage(new HttpMethod(step.Method), uri);
        foreach ((string name, Value value) in step.Headers)
        {
            string header = resolve(value);
            if (header.Any(char.IsControl) || !request.Headers.TryAddWithoutValidation(name, header)) { throw new InvalidOperationException("Invalid initialization header."); }
        }
        if (step.Retry.Mode == "key") { request.Headers.Add(step.Retry.IdempotencyHeader!, idempotencyKey); }
        if (step.Body.Kind == "inline") { request.Content = new StringContent(step.Body.Value!.Value.GetRawText(), Encoding.UTF8, "application/json"); }
        if (step.Body.Kind == "seedSection")
        {
            request.Content = new StreamContent(new BorrowedStream(body ?? throw new InvalidOperationException("Verified section stream required.")));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        }
        bool dispatched = false;
        try
        {
            token.ThrowIfCancellationRequested();
            dispatched = true;
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var retained = new MemoryStream();
            byte[] buffer = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (retained.Length + read > 1048576) { return new((int)response.StatusCode, false, false, "response-limit"); }
                retained.Write(buffer, 0, read);
            }
            bool passed = step.Expect.Statuses.Contains((int)response.StatusCode);
            if (passed && step.Expect.Assertions.Length > 0)
            {
                using JsonDocument json = JsonDocument.Parse(retained.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                passed = step.Expect.Assertions.All(a => TryPointer(json.RootElement, a.Pointer, out JsonElement value) && JsonElement.DeepEquals(value, a.ExpectedValue));
            }
            return new((int)response.StatusCode, passed, true, passed ? null : "status-or-assertion-mismatch")
            { RetryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : null) };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        { return new(null, false, !dispatched, error is OperationCanceledException ? "http-timeout-or-cancelled" : error is JsonException ? "invalid-json-response" : "http-transport-unavailable"); }
    }
    private static bool TryPointer(JsonElement root, string pointer, out JsonElement result)
    {
        result = root;
        if (pointer == "") { return true; }
        foreach (string token in pointer.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
        {
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(token, out JsonElement property)) { result = property; }
            else if (result.ValueKind == JsonValueKind.Array && (token == "0" || !token.StartsWith('0')) && int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index < result.GetArrayLength()) { result = result[index]; }
            else { return false; }
        }
        return true;
    }
    public void Dispose() => client.Dispose();
    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
