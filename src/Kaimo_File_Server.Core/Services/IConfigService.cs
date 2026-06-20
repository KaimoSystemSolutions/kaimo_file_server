using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Services
{
    public interface IConfigService
    {
        /// <summary>
        /// Reads a single value. The DB takes precedence, then the appsettings fallback.
        /// </summary>
        Task<T> GetAsync<T>(string key, T fallback);

        /// <summary>
        /// Reads a single value without an explicit fallback (default(T) is used).
        /// </summary>
        Task<T?> GetAsync<T>(string key);

        /// <summary>
        /// Writes a value to the DB (creates or updates).
        /// </summary>
        Task SetAsync<T>(string key, T value, string? description = null, string? valueType = null);

        /// <summary>
        /// Reads multiple values at once.
        /// </summary>
        Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys);

        /// <summary>
        /// Reads all config entries with a given prefix.
        /// e.g. "app.user." returns all user defaults.
        /// </summary>
        Task<Dictionary<string, string>> GetByPrefixAsync(string prefix);

        /// <summary>
        /// Deletes a single entry.
        /// </summary>
        Task<bool> DeleteAsync(string key);

        /// <summary>
        /// Lists all entries (for the admin UI).
        /// </summary>
        Task<IReadOnlyList<ConfigSettingDto>> GetAllAsync();

        /// <summary>
        /// Invalidates the cache for a specific key.
        /// </summary>
        void InvalidateCache(string key);

        /// <summary>
        /// Clears the entire config cache.
        /// </summary>
        void InvalidateAllCache();
    }

    public record ConfigSettingDto(
        string Key,
        string Value,
        string? Description,
        string ValueType,
        DateTime UpdatedAt
    );
}
