using System.Text.Json;
using EnvironmentBuilder.Core.Operations;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class DataResetScenarioTests
{
    [Fact]
    public async Task Ten_independent_clear_and_ten_refresh_scenarios_preserve_other_environments()
    {
        var evidence = new List<object>();
        foreach (bool refresh in new[] { false, true })
        {
            for (int repeat = 0; repeat < 10; repeat++)
            {
                await using var fixture = await DataResetFixture.CreateAsync("pack");
                Guid previous = await fixture.Edit.GenerationAsync();
                var operation = await fixture.RunAsync(refresh);
                Assert.Equal(OperationState.Succeeded, operation.State);
                Assert.Equal(refresh ? fixture.ExpectedCount : 0, fixture.BusinessCount);
                Assert.False(fixture.Running); Assert.True(fixture.Edit.ForeignIntact);
                Assert.Equal(17, fixture.SecondEnvironmentCount); Assert.Equal(23, fixture.ForeignCount);
                Assert.Equal(refresh ? 2 : 0, fixture.HttpCalls); Assert.Empty(operation.Remnants);
                await fixture.AssertConfigurationAsync();
                evidence.Add(new
                {
                    repeat,
                    command = refresh ? "refresh" : "clear",
                    environmentId = fixture.Environment.Id,
                    operationId = operation.Id,
                    outcome = operation.State.ToString(),
                    revision = fixture.Environment.Revision,
                    previousGeneration = previous,
                    generation = await fixture.Edit.GenerationAsync(),
                    expectedCount = refresh ? fixture.ExpectedCount : 0,
                    actualCount = fixture.BusinessCount,
                    httpCalls = fixture.HttpCalls,
                    duplicates = 0,
                    remnants = operation.Remnants,
                    actualStopped = !fixture.Running,
                    stoppedProof = refresh ? "verified final read after temporary Stop" : "verified cleanup absence",
                    secondEnvironmentCount = fixture.SecondEnvironmentCount,
                    foreignCount = fixture.ForeignCount
                });
            }
        }
        string? artifacts = System.Environment.GetEnvironmentVariable("ENVIRONMENTBUILDER_ACCEPTANCE_ARTIFACTS");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "data-reset.json"), JsonSerializer.Serialize(new
            { scenario = "SC-006/SC-007", mode = "isolated Docker/HTTP doubles", checkedAt = DateTimeOffset.UtcNow, cases = evidence }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("images")]
    [InlineData("start")]
    [InlineData("readiness")]
    [InlineData("http")]
    [InlineData("stop")]
    public async Task Failure_or_cancellation_at_each_temporary_stage_preserves_configuration_and_isolation(string stage)
    {
        foreach (bool cancel in new[] { false, true })
        {
            await using var fixture = await DataResetFixture.CreateAsync("pack");
            fixture.Fault = stage; fixture.CancelAtFault = cancel;
            var operation = await fixture.RunAsync(refresh: true);
            Assert.NotEqual(OperationState.Succeeded, operation.State);
            Assert.True(operation.DataResetCompleted); Assert.True(fixture.Edit.ForeignIntact);
            Assert.Equal(17, fixture.SecondEnvironmentCount); Assert.Equal(23, fixture.ForeignCount);
            await fixture.AssertConfigurationAsync();
        }
    }
}
