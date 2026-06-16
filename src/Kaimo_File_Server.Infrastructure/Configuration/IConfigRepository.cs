namespace Kaimo_File_Server.Infrastructure.Configuration;

public interface IConfigRepository
{
    // Typed getters
    Task<string> GetStringAsync(string key, string fallback = "");
    Task<int> GetIntAsync(string key, int fallback = 0);
    Task<bool> GetBoolAsync(string key, bool fallback = false);
    Task<double> GetDoubleAsync(string key, double fallback = 0d);

    // Generic getter (for complex types / JSON objects)
    Task<T> GetAsync<T>(string key, T fallback);

    // Write
    Task SetAsync<T>(string key, T value);
    Task SetManyAsync(Dictionary<string, object> values);

    // Query
    Task<Dictionary<string, string>> GetSectionAsync(string prefix);

    // Delete
    Task<bool> DeleteAsync(string key);
}