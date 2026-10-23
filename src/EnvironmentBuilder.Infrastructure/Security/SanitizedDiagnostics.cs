using System.Text;
using EnvironmentBuilder.Infrastructure.Persistence;

namespace EnvironmentBuilder.Infrastructure.Security;

public sealed class SanitizedDiagnostics(AppDataDirectory directory) : IDisposable
{
    private const int FileLimit = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task WriteAsync(SanitizedText text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text.Value + "\n");
        if (bytes.Length > FileLimit) { bytes = Encoding.UTF8.GetBytes("diagnostic-message-too-large\n"); }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string current = directory.Child("diagnostics", "diagnostic.0.log");
            if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > FileLimit)
            {
                for (int i = 4; i >= 1; i--)
                {
                    string source = directory.Child("diagnostics", $"diagnostic.{i - 1}.log");
                    string target = directory.Child("diagnostics", $"diagnostic.{i}.log");
                    if (File.Exists(source)) { File.Move(source, target, overwrite: true); }
                }
            }
            await using var output = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
