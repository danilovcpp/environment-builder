using System.Text.Json;
using EnvironmentBuilder.Infrastructure.Security;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Security;

public sealed class SecretBoundaryTests
{
    private const string Sentinel = "SeCrEt+/\"Привет";

    [Fact]
    public async Task Dpapi_persists_ciphertext_and_redacts_escaped_and_chunked_values_before_sinks()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var vault = new DpapiSecretVault(fixture.Worker, fixture.Clock);
        string reference = await vault.SaveAsync("fixture credential", Sentinel.AsMemory(), TestContext.Current.CancellationToken);
        using (var secret = await vault.ResolveAsync(reference, TestContext.Current.CancellationToken))
        { Assert.Equal(Sentinel, new string(secret!.Value.Span)); }
        using var redactor = new SecretRedactor(await SecretContext.ResolveAsync(vault, [reference], TestContext.Current.CancellationToken));
        string[] forms = [Sentinel, Uri.EscapeDataString(Sentinel), JsonSerializer.Serialize(Sentinel)[1..^1]];
        foreach (string form in forms)
        {
            for (int split = 0; split <= form.Length; split++)
            {
                string output = redactor.Append("prefix " + form[..split]).Value + redactor.Append(form[split..] + " suffix", final: true).Value;
                Assert.Equal("prefix [REDACTED] suffix", output);
            }
        }
        Assert.Equal("[REDACTED]", redactor.Redact("unknown-sensitive-value", "authorization").Value);
        using var diagnostics = new SanitizedDiagnostics(fixture.Directory);
        await diagnostics.WriteAsync(redactor.Redact(Sentinel), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Sentinel, await File.ReadAllTextAsync(fixture.Directory.Child("diagnostics", "diagnostic.0.log"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SecretContext.ResolveAsync(vault, ["secret://" + Guid.NewGuid()], TestContext.Current.CancellationToken));
        await fixture.Worker.DisposeAsync();
        byte[] database = await File.ReadAllBytesAsync(fixture.Directory.DatabasePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Sentinel, System.Text.Encoding.UTF8.GetString(database));
    }

    [Fact]
    public async Task Readonly_mapping_rejects_escapes_and_never_uses_an_unmapped_folder()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var resolver = new LocalPathResolver();
        var mapping = new Core.Environments.LocalPathMapping("source", fixture.Directory.Root, null);
        var reference = new Core.Environments.LocalPathReference("local-path://source");
        Assert.Equal(fixture.Directory.Root, resolver.Resolve(reference, [mapping]));
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve(reference, []));
        foreach (string path in new[] { "../escape", "C:/escape", "a:stream", "//foreign/share", "a\\b" })
        { Assert.Throws<Core.Environments.DocumentException>(() => resolver.Resolve(reference, [mapping], path)); }
    }
}
