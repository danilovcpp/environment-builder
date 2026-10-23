using System.Collections.Immutable;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Authoring;

public sealed class OfflineAuthoringTests
{
    [Fact]
    public async Task Failed_create_preserves_the_draft_and_does_not_publish_a_partial_environment()
    {
        bool fail = true;
        await using FoundationStore fixture = await FoundationStore.CreateAsync(boundary =>
        { if (fail && boundary == "environment:beforeCommit") { throw new IOException("disk full"); } });
        var draft = EnvironmentDraft.Create();
        var definition = fixture.Example().Components[0].DefinitionSnapshot;
        draft.AddComponent(definition, [definition]); draft.Update(draft.Snapshot with { Name = "Preserved draft" });
        var create = new CreateEnvironmentService(fixture.Environments, new Inputs(), new Packs());
        string hash = CanonicalJson.Hash(draft.Snapshot);
        await Assert.ThrowsAsync<IOException>(() => create.CreateAsync(draft, TestContext.Current.CancellationToken));
        Assert.True(draft.IsDirty); Assert.False(draft.IsPersisted);
        Assert.Equal(hash, CanonicalJson.Hash(draft.Snapshot));
        Assert.Empty(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountAsync("DataGenerations"));
        fail = false;
        await create.CreateAsync(draft, TestContext.Current.CancellationToken);
        Assert.False(draft.IsDirty);
        Assert.Equal(hash, CanonicalJson.Hash(Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Document));
    }
    [Fact]
    public async Task Preview_captures_bytes_without_executing_files_or_looking_up_images_and_detects_content_delta()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        string source = fixture.Directory.Child("source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Dockerfile"), "RUN do-not-execute-this", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, ".dockerignore"), "ignored", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "one", TestContext.Current.CancellationToken);
        var mappings = new LocalPathMappings(fixture.Worker, fixture.Clock);
        await mappings.MapAsync("local-path://source", source, TestContext.Current.CancellationToken);
        var environment = fixture.Example();
        environment = environment with
        {
            Components = [environment.Components[0] with { Source = new BuildSource("local-path://source", "Dockerfile", ".", ImmutableDictionary<string,string>.Empty,
            ImmutableDictionary<string,string>.Empty.Add("AUTH", "secret://" + Guid.NewGuid())) }]
        };
        var capture = new PreviewInputCapture(mappings, new LocalPathResolver());
        PreviewSnapshot first = await capture.CaptureAsync(environment, TestContext.Current.CancellationToken);
        Assert.All(first.Inputs, input => Assert.Equal("known", input.State));
        Assert.Equal(2, first.SecretReferences.Length);
        Assert.Equal(first.Token, (await capture.CaptureAsync(environment, TestContext.Current.CancellationToken)).Token);
        await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "two", TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Token, (await capture.CaptureAsync(environment, TestContext.Current.CancellationToken)).Token);
        await File.WriteAllTextAsync(Path.Combine(source, ".dockerignore"), "different", TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Token, (await capture.CaptureAsync(environment, TestContext.Current.CancellationToken)).Token);
        var unknown = environment with { Components = [environment.Components[0] with { Source = new ImageSource("fixture/image:latest") }] };
        Assert.Equal("unknown", Assert.Single((await capture.CaptureAsync(unknown, TestContext.Current.CancellationToken)).Inputs).State);
        var known = await capture.CaptureAsync(unknown, new Dictionary<string, string> { ["fixture/image:latest"] = "sha256:known" }, TestContext.Current.CancellationToken);
        Assert.Equal("sha256:known", Assert.Single(known.Inputs).ImageIdentity);
        Assert.Equal(0, await fixture.CountAsync("Operations"));
    }

    [Fact]
    public async Task Independent_external_fixture_saves_missing_paths_secrets_images_and_packages_and_survives_reopen()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        await FixtureStoreBuilder.SeedAsync(fixture, 1, TestContext.Current.CancellationToken);
        var item = Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken));
        var inputs = new InputResolver(fixture.Worker, new LocalPathResolver(), new DpapiSecretVault(fixture.Worker, fixture.Clock), fixture.Clock);
        var environment = item.Document!;
        var initialization = environment.Baseline.Applications[0].Initializer;
        var missingPack = new BaselineApplication(environment.Components[0].Id, initialization, new SeedPackReference(Guid.NewGuid(), "1.0.0", new string('a', 64)));
        var draft = EnvironmentDraft.Create();
        draft.Update(draft.Snapshot with { Name = "Missing inputs", Components = environment.Components, Dependencies = environment.Dependencies, Baseline = new DataBaseline([.. environment.Baseline.Applications, missingPack]) });
        var result = await new CreateEnvironmentService(fixture.Environments, inputs, new Packs()).CreateAsync(draft, TestContext.Current.CancellationToken);
        Assert.Contains(result.ReadinessIssues, issue => issue.Code == "missing-or-unsafe-path");
        Assert.Contains(result.ReadinessIssues, issue => issue.Code == "missing-secret");
        Assert.Contains(result.ReadinessIssues, issue => issue.Code == "missing-pack");
        Assert.Contains(result.ReadinessIssues, issue => issue.Code == "image-identity-unknown");
        var reopened = (await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Single(i => i.Id == draft.Snapshot.Id).Document!;
        Assert.Equal(CanonicalJson.Hash(draft.Snapshot), CanonicalJson.Hash(reopened));
        Assert.Equal(0, await fixture.CountAsync("Operations"));
        Assert.Equal(0, await fixture.CountAsync("ResourceLedger"));
    }

    [Fact]
    public async Task Thousand_row_store_can_be_searched_and_preserves_unknown_measurements()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        await FixtureStoreBuilder.SeedAsync(fixture, 1000, TestContext.Current.CancellationToken);
        var list = new App.Features.Environments.EnvironmentListViewModel(fixture.Environments);
        await list.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1000, list.VisibleItems.Count);
        list.Search = "описанию:";
        Assert.Equal(500, list.VisibleItems.Count);
        Assert.All(list.VisibleItems, row => Assert.Equal("Метрики не получены", row.Measurement));
    }
    [Fact]
    public async Task Creating_an_environment_persists_revision_one_and_missing_inputs_without_runtime_effects()
    {
        await using FoundationStore fixture = await FoundationStore.CreateAsync();
        var draft = EnvironmentDraft.Create();
        var definition = fixture.Example().Components[0].DefinitionSnapshot;
        draft.AddComponent(definition, [definition]);
        draft.Update(draft.Snapshot with { Name = "Offline fixture" });
        var service = new CreateEnvironmentService(fixture.Environments, new Inputs(), new Packs());
        var result = await service.CreateAsync(draft, TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.ReadinessIssues);
        Assert.Equal(1, Assert.Single(await fixture.Environments.ListAsync(TestContext.Current.CancellationToken)).Revision);
        Assert.Equal(0, await fixture.CountAsync("Operations"));
        Assert.Equal(0, await fixture.CountAsync("ResourceLedger"));
    }
    private sealed class Inputs : IInputResolver
    {
        public Task<IReadOnlyList<ReadinessIssue>> CheckAsync(IReadOnlyList<InputReference> references, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadinessIssue>>([new("image-identity-unknown", "/source", "Offline")]);
    }
    private sealed class Packs : ISeedPackStore
    {
        public Task<VerifiedSeedPack?> FindAsync(Core.SeedPacks.SeedPackReference reference, CancellationToken cancellationToken) => Task.FromResult<VerifiedSeedPack?>(null);
    }
}
