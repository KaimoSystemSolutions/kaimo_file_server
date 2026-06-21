using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Search;

public static class SearchServiceExtensions
{
    /// <summary>
    /// Registers the search stack: the Elasticsearch client, both search backends
    /// (Elasticsearch + filename fallback) and the router that picks between them.
    /// The router is exposed as <see cref="ISearchService"/> (indexing + search)
    /// and <see cref="ISearchAdminService"/> (settings page toggle + reindex).
    ///
    /// Requires <c>ISearchConfigStore</c> from AddInfrastructure — call AddInfrastructure first.
    /// Must be registered BEFORE AddCoreServices so the router wins over the NoOp
    /// fallback (AddCoreServices uses TryAddSingleton).
    /// </summary>
    public static IServiceCollection AddSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var url = configuration["Elasticsearch:Url"] ?? "http://elasticsearch:9200";

        var settings = new ElasticsearchClientSettings(new Uri(url))
            .DefaultIndex("kaimo-files-v2");

        var client = new ElasticsearchClient(settings);

        services.AddSingleton(client);
        services.AddSingleton<SearchAclFilter>();
        services.AddSingleton<ElasticSearchService>();
        services.AddSingleton<FilenameSearchService>();
        services.AddSingleton<SearchServiceRouter>();

        // Same singleton instance behind both seams.
        services.AddSingleton<ISearchService>(sp => sp.GetRequiredService<SearchServiceRouter>());
        services.AddSingleton<ISearchAdminService>(sp => sp.GetRequiredService<SearchServiceRouter>());

        return services;
    }

    /// <summary>Backward-compatible alias for <see cref="AddSearch"/>.</summary>
    public static IServiceCollection AddElasticSearch(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddSearch(configuration);
}
