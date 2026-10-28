namespace EnvironmentBuilder.Core.Abstractions;

public sealed record DockerHubRepository(string Name, string Description, bool IsOfficial, string? LogoUrl = null);
public sealed record DockerHubTagPage(IReadOnlyList<string> Tags, bool HasMore);

public interface IDockerHubCatalog
{
    Task<IReadOnlyList<DockerHubRepository>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<DockerHubRepository> GetRepositoryAsync(string name, CancellationToken cancellationToken);
    Task<DockerHubTagPage> GetTagsAsync(string name, int page, CancellationToken cancellationToken);
}
