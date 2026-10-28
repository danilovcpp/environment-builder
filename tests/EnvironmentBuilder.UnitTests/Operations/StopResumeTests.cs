using System.Collections.Immutable;
using EnvironmentBuilder.Core.Environments;
using EnvironmentBuilder.Core.Operations;

namespace EnvironmentBuilder.UnitTests.Operations;

public sealed class StopResumeTests
{
    [Fact]
    public void Verified_empty_never_started_environment_is_quiescent()
    {
        var observation = Snapshot();
        Assert.True(QuiescenceGuard.Check(observation).ConfirmedQuiescent);
        Assert.False(QuiescenceGuard.Check(observation with { Freshness = "unknown" }).ConfirmedQuiescent);
        Assert.False(QuiescenceGuard.Check(observation with { Freshness = "stale" }).ConfirmedQuiescent);
        Assert.False(QuiescenceGuard.Check(observation, "Незавершённый результат требует сверки.").ConfirmedQuiescent);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("paused")]
    [InlineData("restarting")]
    [InlineData("unknown")]
    [InlineData("dead")]
    public void Unverified_or_active_containers_block_destructive_work_with_a_reason(string state)
    {
        var actual = new ComponentObservation(Guid.NewGuid(), "container", state, "unknown", [], ImmutableDictionary<string, Measurement>.Empty);
        var result = QuiescenceGuard.Check(Snapshot() with { Components = [actual] });
        Assert.False(result.ConfirmedQuiescent);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("exited")]
    public void Stopped_containers_are_separate_from_application_readiness(string state)
    {
        var actual = new ComponentObservation(Guid.NewGuid(), "container", state, "unknown", [], ImmutableDictionary<string, Measurement>.Empty);
        Assert.True(QuiescenceGuard.Check(Snapshot() with { Components = [actual] }).ConfirmedQuiescent);
        Assert.False(QuiescenceGuard.Check(Snapshot() with { Components = [actual with { Drift = "unexpected-owned" }] }).ConfirmedQuiescent);
    }

    [Fact]
    public async Task Repeated_start_retains_generation_and_success_tuple_without_reinitializing()
    {
        var fixture = new RuntimeFixture();
        await using var coordinator = fixture.Coordinator();
        var environment = fixture.Environment();
        var service = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
        var first = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await first.Completion;
        var key = fixture.Applications.Keys.Single();
        fixture.Events.Clear();
        var second = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await second.Completion;
        Assert.Equal(OperationState.Succeeded, second.Current.State);
        Assert.Equal(first.Current.Plan.DataGenerationId, second.Current.Plan.DataGenerationId);
        Assert.Equal(key, fixture.Applications.Keys.Single());
        Assert.DoesNotContain("http:bootstrap", fixture.Events);
        Assert.Equal(environment.Revision, second.Current.OriginalRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_replaced_volume_rejects_previous_generation_readiness(bool replaced)
    {
        var fixture = new RuntimeFixture(); var environment = fixture.Environment();
        var generation = new DataGeneration(Guid.NewGuid(), environment.Id, CanonicalJson.Hash(environment.Baseline), DateTimeOffset.UtcNow, GenerationValidity.Active);
        var volume = new OwnedResource(Guid.NewGuid(), Guid.NewGuid(), environment.Id, environment.Components[0].Id, ResourceKind.Volume, 1,
            generation.Id, "data", ImmutableDictionary<string, string>.Empty, "data", new("2026-10-23T00:00:00Z", "local", "local", ImmutableDictionary<string, string>.Empty), [], true, Guid.NewGuid(), false);
        var evidence = new ResourceEvidence(ResourceKind.Volume, "data", "data", ResourceOwnershipVerifier.Labels(volume), volume.Volume! with { CreatedAt = "2026-10-24T00:00:00Z" }, [], true, "available");
        var observation = new ResourceObservation(volume, replaced ? evidence : null, new(null, false, replaced ? "volume-incarnation-mismatch" : "resource-missing"), DateTimeOffset.UtcNow);
        Assert.NotNull(DataGenerationGuard.BlockReason(environment, generation, [observation]));
        Assert.NotNull(DataGenerationGuard.BlockReason(environment, generation with { Validity = GenerationValidity.Invalidated }, []));
    }

    private static EnvironmentObservation Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "current", [], [], null);

    [Fact]
    public async Task Loss_of_generation_blocks_reusing_success_and_certifying_a_new_http_result()
    {
        foreach (bool loseDuringSend in new[] { false, true })
        {
            var fixture = new RuntimeFixture { InvalidateAfterSend = loseDuringSend };
            await using var coordinator = fixture.Coordinator();
            var environment = fixture.Environment();
            var service = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
            var first = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await first.Completion;
            if (loseDuringSend)
            {
                Assert.Equal(OperationState.NeedsAttention, first.Current.State);
                Assert.DoesNotContain(fixture.Applications.Values, record => record.State == StepState.Succeeded);
            }
            else
            {
                Assert.Equal(OperationState.Succeeded, first.Current.State);
                fixture.GenerationInvalidated = true; fixture.Events.Clear();
                var second = service.Start(environment, null, (_, _, _) => Task.FromResult(true)); await second.Completion;
                Assert.Equal(OperationState.NeedsAttention, second.Current.State);
                Assert.Equal("data-generation-invalid", second.Current.ResultCode);
                Assert.Empty(fixture.Events);
            }
        }
    }

    [Fact]
    public async Task Explicit_stop_after_failed_start_does_not_silently_clear_unresolved_recovery()
    {
        var fixture = new RuntimeFixture { LoseHttpResponse = true }; await using var coordinator = fixture.Coordinator();
        var environment = fixture.Environment();
        var start = new StartEnvironmentService(coordinator, fixture, new(fixture, fixture), fixture);
        var failed = start.Start(environment, null, (_, _, _) => Task.FromResult(true)); await failed.Completion;
        Assert.Equal(OperationState.NeedsAttention, failed.Current.State);
        var stopped = new StopEnvironmentService(coordinator, fixture, fixture).Stop(environment); await stopped.Completion;
        Assert.Equal(OperationState.Succeeded, stopped.Current.State);
        Assert.NotNull(coordinator.AdmissionBlockReason(environment.Id));
        Assert.Null(coordinator.AdmissionBlockReason(environment.Id, stopping: true));
    }
}
