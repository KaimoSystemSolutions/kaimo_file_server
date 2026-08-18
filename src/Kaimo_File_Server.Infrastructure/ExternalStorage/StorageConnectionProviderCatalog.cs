using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class StorageConnectionProviderCatalog : IStorageConnectionProviderCatalog
{
    private readonly IReadOnlyDictionary<string, IStorageConnectionProvider> _providers;

    public StorageConnectionProviderCatalog(IEnumerable<IStorageConnectionProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IStorageConnectionProvider> Providers => _providers.Values.ToArray();

    public bool TryGet(string providerId, out IStorageConnectionProvider provider)
        => _providers.TryGetValue(providerId, out provider!);

    public IStorageConnectionProvider GetRequired(string providerId)
        => TryGet(providerId, out var provider)
            ? provider
            : throw new NotSupportedException($"Storage provider '{providerId}' is not registered.");
}
