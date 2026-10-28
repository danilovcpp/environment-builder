using System.Net;
using EnvironmentBuilder.Infrastructure.Docker;
using EnvironmentBuilder.IntegrationTests.Support;

namespace EnvironmentBuilder.IntegrationTests.Docker;

public sealed class DockerHubCatalogTests
{
    [Fact]
    public async Task Public_search_repository_metadata_and_paginated_tags_use_the_hub_contract()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue((request, _) =>
        {
            Assert.Equal("https://hub.docker.com/v2/search/repositories/?query=redis%20cache&page_size=25", request.RequestUri!.AbsoluteUri);
            return Response("""{"results":[{"repo_name":"vendor/redis","short_description":"Community","is_official":false},{"repo_name":"redis","short_description":"Redis","is_official":true}]}""");
        });
        handler.Enqueue((request, _) =>
        {
            Assert.Equal("/v2/namespaces/library/repositories/redis", request.RequestUri!.AbsolutePath);
            return Response("""{"namespace":"library","description":"Redis","full_description":"# Redis\n![logo](https://raw.githubusercontent.com/docker-library/docs/main/redis/logo.png)"}""");
        });
        handler.Enqueue((request, _) =>
        {
            Assert.Equal("/v2/namespaces/vendor/repositories/redis/tags", request.RequestUri!.AbsolutePath);
            Assert.Contains("page=2", request.RequestUri.Query);
            return Response("""{"next":"https://hub.docker.com/next","results":[{"name":"7.2"},{"name":"latest"}]}""");
        });
        using var catalog = new DockerHubCatalog(new HttpClient(handler));
        var results = await catalog.SearchAsync("redis cache", TestContext.Current.CancellationToken);
        Assert.Equal("redis", results[0].Name); Assert.True(results[0].IsOfficial);
        var repository = await catalog.GetRepositoryAsync("redis", TestContext.Current.CancellationToken);
        Assert.Equal("https://raw.githubusercontent.com/docker-library/docs/main/redis/logo.png", repository.LogoUrl);
        var tags = await catalog.GetTagsAsync("vendor/redis", 2, TestContext.Current.CancellationToken);
        Assert.True(tags.HasMore); Assert.Equal(new[] { "7.2", "latest" }, tags.Tags);
        Assert.Equal(3, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://cdn.example.com/logo.png", "https://cdn.example.com/logo.png")]
    [InlineData("file:///C:/logo.png", null)]
    [InlineData("https://cdn.example.com/logo.svg", null)]
    public async Task Logo_metadata_supports_raster_images_and_falls_back_when_unavailable(string url, string? expected)
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) => Response(System.Text.Json.JsonSerializer.Serialize(new { logo_url = url })));
        using var catalog = new DockerHubCatalog(new HttpClient(handler));
        Assert.Equal(expected, (await catalog.GetRepositoryAsync("vendor/cache", TestContext.Current.CancellationToken)).LogoUrl);
    }
    private static Task<HttpResponseMessage> Response(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
}
