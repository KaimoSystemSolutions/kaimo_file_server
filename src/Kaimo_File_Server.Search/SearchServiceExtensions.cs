using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Search;

public static class SearchServiceExtensions
{
    public static IServiceCollection AddElasticSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var url = configuration["Elasticsearch:Url"] ?? "http://elasticsearch:9200";

        var settings = new ElasticsearchClientSettings(new Uri(url))
            .DefaultIndex("kaimo-files");
            
        var client = new ElasticsearchClient(settings);

        services.AddSingleton(client);
        services.AddSingleton<ISearchService, ElasticSearchService>();

        return services;
    }
}
