namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses = [];

    public int RequestCount { get; private set; }

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        if (!responses.TryDequeue(out Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response))
        {
            throw new InvalidOperationException("No HTTP response was arranged; live HTTP is never a fallback.");
        }

        return response(request, cancellationToken);
    }
}
