using System.Diagnostics;
using System.Text;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Docker;

public static class DockerEndpoint
{
    public const string Default = "npipe:////./pipe/dockerDesktopLinuxEngine";
    public static void RequireLocal(string endpoint)
    {
        if (endpoint is not (Default or "npipe:////./pipe/docker_engine")) { throw new ArgumentException("Only an explicitly selected local Docker named pipe is supported.", nameof(endpoint)); }
    }
}

// Actual process boundary. Fakes return finite sanitized output, never launch a fallback command.
public interface IDockerProcess
{
    string Endpoint { get; }
    Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Func<string, bool, Task>? output = null);
}
public sealed record DockerResult(int ExitCode, string Output, string Error, bool Truncated = false);

public sealed class DockerProcessRunner(string executable, string endpoint, Func<Task<SecretContext>> secrets) : IDockerProcess
{
    public string Endpoint { get; } = endpoint;
    public static string DefaultExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");
    public static ProcessStartInfo CreateStartInfo(string executable, string endpoint, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        DockerEndpoint.RequireLocal(endpoint);
        if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("docker.exe", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("An absolute docker.exe path is required.", nameof(executable)); }
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var inherited = environment ?? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        info.Environment.Clear();
        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)" })
        { if (inherited.TryGetValue(name, out string? value)) { info.Environment[name] = value; } }
        info.ArgumentList.Add("--host"); info.ArgumentList.Add(endpoint);
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        return info;
    }

    public async Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Func<string, bool, Task>? output = null)
    {
        LocalPathResolver.RejectReparsePoints(executable);
        if (!File.Exists(executable)) { throw new FileNotFoundException("Docker CLI is unavailable."); }
        // Independent streaming redactors prevent stdout/stderr chunk interleaving from losing a secret prefix.
        using var stdout = new SecretRedactor(await secrets().ConfigureAwait(false));
        using var stderr = new SecretRedactor(await secrets().ConfigureAwait(false));
        using var process = new Process { StartInfo = CreateStartInfo(executable, Endpoint, arguments) };
        process.Start();
        bool truncated = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool following = arguments.Count > 0 && arguments[0] == "logs" && arguments.Contains("--follow");
        if (!following) { deadline.CancelAfter(arguments.Count > 0 && arguments[0] is "build" or "pull" ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(2)); }
        using CancellationTokenRegistration stop = deadline.Token.Register(StopClient);
        Task<string> readOut = ReadAsync(process.StandardOutput, stdout, false);
        Task<string> readError = ReadAsync(process.StandardError, stderr, true);
        await Task.WhenAll(readOut, readError, process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) { throw new TimeoutException("Docker client timed out; an accepted effect may require reconciliation."); }
        return new(process.ExitCode, await readOut.ConfigureAwait(false), await readError.ConfigureAwait(false), truncated);

        async Task<string> ReadAsync(StreamReader stream, SecretRedactor redactor, bool error)
        {
            var retained = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            try
            {
                while ((count = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) != 0)
                { await PublishAsync(redactor.Append(new string(buffer, 0, count)).Value).ConfigureAwait(false); }
                await PublishAsync(redactor.Append("", final: true).Value).ConfigureAwait(false);
            }
            catch { StopClient(); throw; }
            return retained.ToString();
            async Task PublishAsync(string value)
            {
                if (output is not null) { await output(value, error).ConfigureAwait(false); }
                else if (retained.Length < 4 * 1024 * 1024)
                {
                    int take = Math.Min(value.Length, 4 * 1024 * 1024 - retained.Length);
                    retained.Append(value.AsSpan(0, take)); if (take < value.Length) { truncated = true; }
                }
                else { truncated = true; }
            }
        }
        void StopClient()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException) { }
        }
    }
}
