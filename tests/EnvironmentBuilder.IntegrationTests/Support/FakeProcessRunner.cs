namespace EnvironmentBuilder.IntegrationTests.Support;

public sealed record TestProcessRequest(string FileName, IReadOnlyList<string> Arguments);
public sealed record TestProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class FakeProcessRunner
{
    private readonly Queue<Func<TestProcessRequest, CancellationToken, Task<TestProcessResult>>> responses = [];
    private readonly List<TestProcessRequest> requests = [];

    public IReadOnlyList<TestProcessRequest> Requests => requests.AsReadOnly();

    public void Enqueue(TestProcessResult result) => Enqueue((_, _) => Task.FromResult(result));

    public void Enqueue(Func<TestProcessRequest, CancellationToken, Task<TestProcessResult>> response) => responses.Enqueue(response);

    public Task<TestProcessResult> RunAsync(TestProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        requests.Add(request with { Arguments = request.Arguments.ToArray() });
        if (!responses.TryDequeue(out Func<TestProcessRequest, CancellationToken, Task<TestProcessResult>>? response))
        {
            throw new InvalidOperationException("No process response was arranged; live processes are never a fallback.");
        }

        return response(request, cancellationToken);
    }
}
