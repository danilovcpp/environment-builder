using System.Collections.Immutable;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnvironmentBuilder.Core.Abstractions;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.Core.SeedPacks;
using EnvironmentBuilder.Infrastructure.Initialization;
using EnvironmentBuilder.Infrastructure.Persistence;
using EnvironmentBuilder.Infrastructure.Security;

namespace EnvironmentBuilder.Infrastructure.Docker;

public sealed class DockerProcessFactory(ISecretVault vault)
{
    public IDockerProcess Create(IsolatedEnvironment environment)
    {
        IReadOnlyList<string> references = References(environment);
        return new DockerProcessRunner(DockerProcessRunner.DefaultExecutable, DockerEndpoint.Default,
            () => SecretContext.ResolveAsync(vault, references, CancellationToken.None));
    }
    public static IReadOnlyList<string> References(IsolatedEnvironment environment) => EnvironmentSecrets.References(environment);
}

public sealed class DockerRuntimeAdapter(AppDataDirectory directory, SqliteWorker worker, IEnvironmentStore environments, ResourceLedgerStore ledger,
    DataApplicationStore applications, RuntimeInputCapture capture, IInputResolver inputs, ISeedPackStore packs, ISecretVault vault,
    ContentBlobStore blobs, DockerProcessFactory processes) : IStartRuntime
{
    public async Task<IStartSession> PrepareAsync(IsolatedEnvironment environment, Guid operationId, CancellationToken token)
    {
        EnvironmentListItem saved = (await environments.ListAsync(token).ConfigureAwait(false)).Single(i => i.Id == environment.Id);
        if (saved.Document is null || CanonicalJson.Hash(saved.Document) != CanonicalJson.Hash(environment)) { throw new InvalidOperationException("Saved configuration changed."); }
        IReadOnlyList<ReadinessIssue> issues = await EnvironmentReadiness.CheckAsync(environment, inputs, packs, token).ConfigureAwait(false);
        if (issues.Any(i => i.Code != "image-identity-unknown")) { throw new InvalidOperationException("Required input is unavailable: " + issues.First(i => i.Code != "image-identity-unknown").Code); }
        (Guid installation, Guid generation) = await worker.ReadAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT (SELECT value FROM AppMetadata WHERE key='installationId'),g.id FROM DataGenerations g JOIN Environments e ON e.generationId=g.id WHERE e.id=$id AND g.validity='active' AND g.baselineHash=$baseline",
                ("$id", environment.Id.ToString()), ("$baseline", CanonicalJson.Hash(environment.Baseline)));
            using var row = command.ExecuteReader();
            if (!row.Read()) { throw new PreflightBlockedException("data-generation-invalid"); }
            return (Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)));
        }, token).ConfigureAwait(false);
        IDockerProcess docker = processes.Create(environment);
        var probe = new DockerCapabilityProbe(docker, directory);
        DockerCapabilities capability = await probe.ProbeAsync(token).ConfigureAwait(false);
        var reader = new ResourceConsumerReader(docker);
        ImmutableArray<JsonElement> all = await reader.AllContainersAsync(token).ConfigureAwait(false);
        IReadOnlyList<OwnedResource> existing = await ledger.ReadAsync(environment.Id, token).ConfigureAwait(false);
        string[] ownedIds = existing.Where(r => r.Kind == ResourceKind.Container && r.ActualDockerId is not null).Select(r => r.ActualDockerId!).ToArray();
        var generationResources = new List<ResourceObservation>();
        foreach (OwnedResource resource in existing)
        {
            if (!resource.DesiredAttributes.TryGetValue("engineId", out string? authority) || authority != capability.EngineId)
            { throw new InvalidOperationException("Resource ledger belongs to an unknown or changed Docker authority."); }
            DockerResourceRead? actual = await reader.ReadAsync(resource, all, token).ConfigureAwait(false);
            generationResources.Add(new(resource, actual?.Evidence, actual is null ? new(null, false, "resource-missing") : ResourceOwnershipVerifier.Verify(resource, actual.Evidence, ownedIds), DateTimeOffset.UtcNow));
        }
        var activeGeneration = new DataGeneration(generation, environment.Id, CanonicalJson.Hash(environment.Baseline), DateTimeOffset.MinValue, GenerationValidity.Active);
        string? generationBlock = DataGenerationGuard.BlockReason(environment, activeGeneration, generationResources);
        if (generationBlock is not null)
        {
            await applications.InvalidateAsync(generation, operationId, generationBlock, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            throw new PreflightBlockedException(generationBlock);
        }
        if (generationResources.Any(r => !r.Ownership.Verified)) { throw new PreflightBlockedException("existing-resource-unverified"); }
        foreach (EnvironmentComponent component in environment.Components)
        {
            string containerName = ComposeCompiler.Compile(environment).ProjectName + "-c-" + component.Id.ToString("N");
            if (all.Any(c => c.GetProperty("Name").GetString()?.TrimStart('/') == containerName && !ownedIds.Contains(c.GetProperty("Id").GetString())))
            { throw new InvalidOperationException("Foreign container name collision."); }
        }
        var occupiedTcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        var occupiedUdp = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(e => e.Port).ToHashSet();
        foreach (Port port in environment.Components.SelectMany(c => c.Ports).Where(p => p.HostPort.HasValue))
        {
            bool ownedPort = all.Where(c => ownedIds.Contains(c.GetProperty("Id").GetString())).Any(c => c.GetProperty("NetworkSettings").GetProperty("Ports").EnumerateObject()
                .Any(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(m => m.GetProperty("HostPort").GetString() == port.HostPort!.Value.ToString(CultureInfo.InvariantCulture))));
            if (!ownedPort && (port.Protocol == "tcp" ? occupiedTcp : occupiedUdp).Contains(port.HostPort!.Value)) { throw new InvalidOperationException("Requested host port is occupied."); }
        }
        var imagePreparation = new ImagePreparation(docker);
        var images = new Dictionary<Guid, ResolvedImage>();
        foreach (EnvironmentComponent component in environment.Components.Where(c => c.Source is ImageSource))
        { images.Add(component.Id, await imagePreparation.ResolveAsync(((ImageSource)component.Source).Reference, token).ConfigureAwait(false)); }
        RuntimeInputs captured = await capture.CaptureAsync(environment, images.Values.GroupBy(i => i.Reference, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Identity, StringComparer.Ordinal), token).ConfigureAwait(false);
        var values = new Dictionary<string, SecretValue>(StringComparer.Ordinal);
        try
        {
            foreach (string reference in captured.Snapshot.SecretReferences)
            { values.Add(reference, await vault.ResolveAsync(reference, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Secret reference is unavailable.")); }
            foreach (EnvironmentComponent component in environment.Components)
            {
                foreach (ParameterDefinition parameter in component.DefinitionSnapshot.ParameterDefinitions)
                {
                    if (component.Parameters.TryGetValue(parameter.Name, out Value? value) && value.SecretRef is not null)
                    { EnvironmentValidator.ValidateLiteral(parameter, new string(values[value.SecretRef].Value.Span)); }
                }
            }
            foreach (Value value in environment.Baseline.Applications.SelectMany(a => a.Initializer.Steps).SelectMany(s => s.Headers.Values))
            {
                string header = value.SecretRef is null ? value.Literal! : new string(values[value.SecretRef].Value.Span);
                if (header.Any(char.IsControl)) { throw new InvalidOperationException("Initialization header input is invalid."); }
            }
            var session = new Session(environment, operationId, installation, generation, directory, ledger, applications, blobs, packs, docker, probe, capability, reader, captured, images, values, existing);
            foreach (OwnedResource resource in session.Plan.Resources.Where(r => r.ActualDockerId is null))
            { if (await reader.ReadAsync(resource, all, token).ConfigureAwait(false) is not null) { await session.DisposeAsync().ConfigureAwait(false); throw new InvalidOperationException("Planned resource name is already occupied."); } }
            return session;
        }
        catch { foreach (SecretValue value in values.Values) { value.Dispose(); } await captured.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class Session : IStartSession
    {
        private readonly IsolatedEnvironment environment;
        private readonly Guid operation;
        private readonly AppDataDirectory directory;
        private readonly ResourceLedgerStore ledger;
        private readonly DataApplicationStore applications;
        private readonly ContentBlobStore blobs;
        private readonly ISeedPackStore packs;
        private readonly IDockerProcess docker;
        private readonly DockerCapabilityProbe probe;
        private readonly DockerCapabilities capabilities;
        private readonly ResourceConsumerReader reader;
        private readonly RuntimeInputs captured;
        private readonly Dictionary<Guid, ResolvedImage> images;
        private readonly Dictionary<string, SecretValue> secrets;
        private readonly Dictionary<Guid, string> pinned = [];
        private readonly Dictionary<Guid, ComponentObservation> observed = [];
        private readonly List<OwnedResource> resources;
        private PrivateRuntimeFiles? files;
        private bool networkPrepared;
        public PreviewSnapshot Snapshot => captured.Snapshot;
        public OperationPlan Plan { get; }
        public IInitializationTransport Transport { get; }

        public Session(IsolatedEnvironment environment, Guid operation, Guid installation, Guid generation, AppDataDirectory directory,
            ResourceLedgerStore ledger, DataApplicationStore applications, ContentBlobStore blobs, ISeedPackStore packs, IDockerProcess docker, DockerCapabilityProbe probe, DockerCapabilities capabilities,
            ResourceConsumerReader reader, RuntimeInputs captured, Dictionary<Guid, ResolvedImage> images, Dictionary<string, SecretValue> secrets, IReadOnlyList<OwnedResource> existing)
        {
            this.environment = environment; this.operation = operation; this.directory = directory; this.ledger = ledger; this.applications = applications; this.blobs = blobs; this.packs = packs;
            this.docker = docker; this.probe = probe; this.capabilities = capabilities; this.reader = reader; this.captured = captured; this.images = images; this.secrets = secrets;
            resources = existing.ToList();
            ComposeModel model = ComposeCompiler.Compile(environment);
            Add(ResourceKind.Network, null, model.ProjectName + "-network");
            foreach (EnvironmentComponent component in environment.Components)
            {
                Add(ResourceKind.Container, component.Id, model.ProjectName + "-c-" + component.Id.ToString("N"));
                foreach (ManagedStorage storage in component.Storage.OfType<ManagedStorage>()) { Add(ResourceKind.Volume, component.Id, model.ProjectName + "-v-" + component.Id.ToString("N") + "-" + storage.Id); }
            }
            Plan = new(Guid.NewGuid(), environment.Id, environment.Revision, Snapshot.ConfigurationHash, generation, Snapshot.BaselineHash,
                resources.ToImmutableArray(), Snapshot.Inputs.Select(i => new InputFingerprint(i.Reference, i.ContentHash, i.ImageIdentity)).ToImmutableArray(),
                ["images", "network", "volumes", "containers", "readiness", "baseline"], "Ошибка или отмена сохраняет созданные ресурсы и уже работающие компоненты. Автоматическая остановка и очистка не выполняются.")
            { Endpoint = capabilities.Endpoint, EngineId = capabilities.EngineId, Candidate = environment, Packs = environment.Baseline.Applications.Where(a => a.SeedPack is not null).Select(a => a.SeedPack!).ToImmutableArray() };
            Transport = new HttpInitializationTransport(ResolveEndpointAsync, Resolve);
            void Add(ResourceKind kind, Guid? component, string name)
            {
                if (!resources.Any(r => r.Kind == kind && r.Name == name))
                {
                    resources.Add(new(Guid.NewGuid(), installation, environment.Id, component, kind, environment.Revision, generation, name,
                    kind == ResourceKind.Container ? ImmutableDictionary<string, string>.Empty.Add("stagingLease", captured.Root).Add("engineId", capabilities.EngineId) : ImmutableDictionary<string, string>.Empty.Add("engineId", capabilities.EngineId), null, null, [], false, operation, true));
                }
            }
        }
        public async Task ValidateDataGenerationAsync(CancellationToken token)
        {
            await probe.RequireEngineAsync(capabilities, token).ConfigureAwait(false);
            var all = await reader.AllContainersAsync(token).ConfigureAwait(false);
            var actual = new List<ResourceObservation>();
            string[] allowed = resources.Where(r => r.Kind == ResourceKind.Container && r.ActualDockerId is not null).Select(r => r.ActualDockerId!).ToArray();
            foreach (OwnedResource volume in resources.Where(r => r.Kind == ResourceKind.Volume && r.ActualDockerId is not null))
            {
                DockerResourceRead? read = await reader.ReadAsync(volume, all, token).ConfigureAwait(false);
                actual.Add(new(volume, read?.Evidence, read is null ? new(null, false, "resource-missing") : ResourceOwnershipVerifier.Verify(volume, read.Evidence, allowed), DateTimeOffset.UtcNow));
            }
            var generation = new DataGeneration(Plan.DataGenerationId, environment.Id, Plan.BaselineHash, DateTimeOffset.MinValue, GenerationValidity.Active);
            string? blocker = DataGenerationGuard.BlockReason(environment, generation, actual);
            if (blocker is null && !await applications.IsActiveGenerationAsync(environment.Id, generation.Id, generation.BaselineHash, token).ConfigureAwait(false))
            { throw new UnresolvedEffectException("data-generation-invalid"); }
            if (blocker is not null)
            {
                await applications.InvalidateAsync(generation.Id, operation, blocker, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                throw new UnresolvedEffectException(blocker);
            }
        }
        private string Resolve(Value value) => value.SecretRef is null ? value.Literal! : new string(secrets[value.SecretRef].Value.Span);
        private async Task<Uri> ResolveEndpointAsync(Guid component, string endpoint, CancellationToken token)
        {
            await probe.RequireEngineAsync(capabilities, token).ConfigureAwait(false);
            OwnedResource resource = resources.Single(r => r.Kind == ResourceKind.Container && r.ComponentId == component);
            DockerResourceRead? current = await reader.ReadAsync(resource, await reader.AllContainersAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (current?.Component is null || !ResourceOwnershipVerifier.Verify(resource, current.Evidence).Verified)
            { throw new InvalidOperationException("Initialization mapping ownership changed before dispatch."); }
            observed[component] = current.Component;
            return LoopbackEndpointResolver.Resolve(environment.Components.Single(c => c.Id == component).DefinitionSnapshot.Endpoints.Single(e => e.Id == endpoint), current.Component);
        }

        public async Task PrepareImagesAsync(OperationExecution execution, CancellationToken token)
        {
            await probe.RequireEngineAsync(capabilities, token).ConfigureAwait(false);
            files = new PrivateRuntimeFiles(directory, operation);
            await files.WriteAsync("empty.env", "", token).ConfigureAwait(false);
            var secretPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var preparation = new ImagePreparation(docker);
            foreach (EnvironmentComponent component in environment.Components)
            {
                token.ThrowIfCancellationRequested();
                string? identity = null;
                if (component.Source is ImageSource)
                {
                    ResolvedImage image = images[component.Id];
                    if (image.Local) { pinned[component.Id] = await preparation.InspectPinnedAsync(image.Identity, token).ConfigureAwait(false); continue; }
                    await execution.RunStepAsync(new(operation, "pull-" + component.Id, "image-pull", image.Identity), async safe =>
                    { ResourceConsumerReader.Require(await docker.RunAsync(["pull", "--platform", "linux/amd64", image.Identity], safe).ConfigureAwait(false)); }, async safe =>
                    { identity = await preparation.InspectPinnedAsync(image.Identity, safe).ConfigureAwait(false); return new(identity, true, null); }).ConfigureAwait(false);
                }
                else
                {
                    BuildSource build = (BuildSource)component.Source;
                    foreach (string reference in build.BuildSecrets.Values.Distinct(StringComparer.Ordinal))
                    { secretPaths[reference] = await files.WriteAsync("secret-" + Guid.Parse(reference["secret://".Length..]).ToString("N"), new string(secrets[reference].Value.Span), token).ConfigureAwait(false); }
                    string context = captured.Paths[component.Id + ":context:" + build.PathRef + "/" + build.ContextSubdirectory];
                    string iid = Path.Combine(files.Root, "image-" + component.Id.ToString("N"));
                    var args = new List<string> { "build", "--platform", "linux/amd64", "--progress", "plain", "--iidfile", iid, "--file", Path.Combine(context, build.Dockerfile) };
                    foreach ((string key, string value) in build.BuildArguments) { args.AddRange(["--build-arg", key + "=" + value]); }
                    foreach ((string key, string reference) in build.BuildSecrets) { args.AddRange(["--secret", "id=" + key + ",src=" + secretPaths[reference]]); }
                    args.Add(context);
                    try
                    {
                        await execution.RunStepAsync(new(operation, "build-" + component.Id, "image-build", component.Id.ToString()), async safe =>
                        { ResourceConsumerReader.Require(await docker.RunAsync(args, safe).ConfigureAwait(false)); }, async safe =>
                        { identity = await preparation.InspectPinnedAsync(ImagePreparation.RequireIdentity((await File.ReadAllTextAsync(iid, safe).ConfigureAwait(false)).Trim()), safe).ConfigureAwait(false); return new(identity, true, null); }).ConfigureAwait(false);
                    }
                    finally { foreach (string reference in build.BuildSecrets.Values.Distinct(StringComparer.Ordinal)) { File.Delete(secretPaths[reference]); secretPaths.Remove(reference); } }
                }
                pinned[component.Id] = identity!;
            }
            string compose = ComposeCompiler.Compile(environment).Render(Resolve, key => captured.Paths[key], reference => secretPaths[reference], pinned);
            JsonNode root = JsonNode.Parse(compose)!;
            foreach (EnvironmentComponent component in environment.Components)
            {
                JsonNode service = root["services"]!["c-" + component.Id.ToString("N")]!;
                OwnedResource resource = resources.Single(r => r.Kind == ResourceKind.Container && r.ComponentId == component.Id);
                service["container_name"] = resource.Name;
                service["labels"] = JsonSerializer.SerializeToNode(ResourceOwnershipVerifier.Labels(resource));
                service["depends_on"] = new JsonObject();
            }
            foreach (var volume in root["volumes"]!.AsObject().ToArray())
            { string name = volume.Value!["name"]!.GetValue<string>(); root["volumes"]![volume.Key] = new JsonObject { ["name"] = name, ["external"] = true }; }
            string networkName = resources.Single(r => r.Kind == ResourceKind.Network).Name;
            root["networks"]!["private"] = new JsonObject { ["name"] = networkName, ["external"] = true };
            await files.WriteAsync("compose.json", root.ToJsonString(), token).ConfigureAwait(false);
        }

        public async Task<ComponentObservation> StartComponentAsync(EnvironmentComponent component, OperationExecution execution, CancellationToken token)
        {
            if (files is null) { throw new InvalidOperationException("Images must be pinned before launch."); }
            if (!networkPrepared)
            { await CreateAsync(resources.Single(r => r.Kind == ResourceKind.Network), execution, token).ConfigureAwait(false); networkPrepared = true; }
            foreach (OwnedResource volume in resources.Where(r => r.Kind == ResourceKind.Volume && r.ComponentId == component.Id).ToArray())
            { await CreateAsync(volume, execution, token).ConfigureAwait(false); }
            OwnedResource container = resources.Single(r => r.Kind == ResourceKind.Container && r.ComponentId == component.Id);
            await CreateAsync(container, execution, token).ConfigureAwait(false);
            return observed[component.Id];
        }
        private async Task CreateAsync(OwnedResource resource, OperationExecution execution, CancellationToken token)
        {
            await probe.RequireEngineAsync(capabilities, token).ConfigureAwait(false);
            ImmutableArray<JsonElement> all = await reader.AllContainersAsync(token).ConfigureAwait(false);
            DockerResourceRead? before = await reader.ReadAsync(resource, all, token).ConfigureAwait(false);
            if (resource.ActualDockerId is not null)
            {
                if (before is null || !ResourceOwnershipVerifier.Verify(resource, before.Evidence, resources.Where(r => r.Kind == ResourceKind.Container).Select(r => r.ActualDockerId!).Where(id => id is not null)).Verified)
                { throw new InvalidOperationException("Existing resource changed after review."); }
                if (before.Component is not null) { observed[before.Component.ComponentId] = before.Component; }
                if (resource.Kind != ResourceKind.Container || before.Component?.Status == "running") { return; }
            }
            else if (before is not null) { throw new InvalidOperationException("Foreign resource name collision."); }
            await ledger.SaveAsync(resource, "create-" + resource.ResourceId, null, token).ConfigureAwait(false);
            var args = new List<string>();
            if (resource.Kind == ResourceKind.Container)
            {
                if (resource.ActualDockerId is not null) { args.AddRange(["container", "start", resource.ActualDockerId]); }
                else
                {
                    args.AddRange(["compose", "--ansi", "never", "--progress", "plain", "--project-directory", files!.Root, "--env-file", files.EnvironmentFile,
                    "-f", files.ComposeFile, "-p", "eb-" + environment.Id.ToString("N"), "up", "-d", "--no-deps", "--no-build", "--pull", "never", "c-" + resource.ComponentId!.Value.ToString("N")]);
                    captured.RetainForContainers();
                }
            }
            else
            {
                args.AddRange([resource.Kind.ToString().ToLowerInvariant(), "create", "--driver", resource.Kind == ResourceKind.Network ? "bridge" : "local"]);
                foreach ((string key, string value) in ResourceOwnershipVerifier.Labels(resource)) { args.AddRange(["--label", key + "=" + value]); }
                args.Add(resource.Name);
            }
            bool resuming = resource.Kind == ResourceKind.Container && resource.ActualDockerId is not null;
            await execution.RunStepAsync(new(operation, (resuming ? "start-" : "create-") + resource.ResourceId, resuming ? "start-container" : "create-" + resource.Kind.ToString().ToLowerInvariant(), resuming ? resource.ActualDockerId! : resource.Name), async safe =>
            { ResourceConsumerReader.Require(await docker.RunAsync(args, safe).ConfigureAwait(false)); }, async safe =>
            {
                var current = await reader.ReadAsync(resource, await reader.AllContainersAsync(safe).ConfigureAwait(false), safe).ConfigureAwait(false) ?? throw new IOException("Accepted resource not found.");
                if (ResourceOwnershipVerifier.Labels(resource).Any(p => !current.Evidence.Labels.TryGetValue(p.Key, out string? value) || p.Value != value)) { return new(null, false, "created-label-mismatch"); }
                OwnedResource verified = resource with { ActualDockerId = current.Evidence.Identity, Volume = current.Evidence.Volume, Consumers = current.Evidence.Consumers, ConsumersKnown = true, Unresolved = false };
                if (verified.Kind == ResourceKind.Volume && (verified.Volume?.Driver != "local" || verified.Volume.Scope != "local" || verified.Volume.Options.Count != 0)) { return new(null, false, "unsupported-volume"); }
                await ledger.SaveAsync(verified, null, null, safe).ConfigureAwait(false);
                resources[resources.FindIndex(r => r.ResourceId == resource.ResourceId)] = verified;
                if (current.Component is not null) { observed[current.Component.ComponentId] = current.Component; }
                return new(current.Evidence.Identity, true, null);
            }).ConfigureAwait(false);
        }

        public async Task<ComponentObservation> WaitAsync(EnvironmentComponent component, string condition, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(component.DefinitionSnapshot.Readiness.TimeoutSeconds));
            while (true)
            {
                await probe.RequireEngineAsync(capabilities, timeout.Token).ConfigureAwait(false);
                OwnedResource resource = resources.Single(r => r.Kind == ResourceKind.Container && r.ComponentId == component.Id);
                DockerResourceRead? current = await reader.ReadAsync(resource, await reader.AllContainersAsync(timeout.Token).ConfigureAwait(false), timeout.Token).ConfigureAwait(false);
                if (current?.Component is null || !ResourceOwnershipVerifier.Verify(resource, current.Evidence).Verified) { throw new InvalidOperationException("Readiness container ownership changed."); }
                observed[component.Id] = current.Component;
                bool ready = condition switch
                {
                    "running" => current.Component.Status == "running",
                    "healthy" or "dockerHealth" => current.Component.Status == "running" && current.Component.Health == "healthy",
                    "http" => current.Component.Status == "running" && await HttpReadyAsync(component, timeout.Token).ConfigureAwait(false),
                    _ => false
                };
                if (ready) { return current.Component with { RuntimeReady = true }; }
                if (current.Component.Status is "exited" or "dead" || (condition is "healthy" or "dockerHealth" && current.Component.Health == "notConfigured")) { throw new InvalidOperationException("Declared readiness cannot be satisfied."); }
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
            }
        }
        private async Task<bool> HttpReadyAsync(EnvironmentComponent component, CancellationToken token)
        {
            Readiness readiness = component.DefinitionSnapshot.Readiness;
            var step = new InitializationStep("readiness", "Readiness", readiness.EndpointId!, "GET", readiness.Path!, ImmutableDictionary<string, Value>.Empty,
                new("none"), new(Enumerable.Range(200, 100).ToImmutableArray(), []), Math.Min(5, readiness.TimeoutSeconds), new("none", 1));
            InitializationResponse response = await Transport.SendAsync(component.Id, step, "", null, token).ConfigureAwait(false);
            return response.OutcomeKnown && response.AssertionsPassed;
        }
        public async Task<Stream?> OpenBodyAsync(BaselineApplication application, InitializationStep step, CancellationToken token)
        {
            if (step.Body.Kind != "seedSection") { return null; }
            SeedPackReference reference = application.SeedPack ?? throw new InvalidOperationException("Section requires exact pack.");
            VerifiedSeedPack pack = await packs.FindAsync(reference, token).ConfigureAwait(false) ?? throw new IOException("Exact pack is unavailable.");
            SeedSection section = pack.Manifest.Sections.Single(s => s.Id == step.Body.SectionId);
            BlobLease lease = await blobs.AcquireAsync(section.Sha256, token).ConfigureAwait(false);
            return new BlobBodyStream(lease);
        }
        public async ValueTask DisposeAsync()
        {
            ((IDisposable)Transport).Dispose();
            foreach (SecretValue value in secrets.Values) { value.Dispose(); }
            try { if (files is not null) { await files.DisposeAsync().ConfigureAwait(false); } }
            finally { await captured.DisposeAsync().ConfigureAwait(false); }
        }
        private sealed class BlobBodyStream(BlobLease lease) : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => lease.Bytes; public override long Position { get => lease.Stream.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => lease.Stream.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => lease.Stream.ReadAsync(buffer, cancellationToken);
            public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) { lease.Dispose(); } base.Dispose(disposing); }
        }
    }
}
