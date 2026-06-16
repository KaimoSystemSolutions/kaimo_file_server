using System.Text.Json;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Infrastructure.Configuration;

public class ConfigRepository : IConfigRepository
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public ConfigRepository(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    // ── Typed Getters ──────────────────────────

    /// <summary>Returns a string value, or the fallback if the key doesn't exist.</summary>
    public async Task<string> GetStringAsync(string key, string fallback = "")
        => await GetAsync(key, fallback);

    /// <summary>Returns an int value, or the fallback if the key doesn't exist.</summary>
    public async Task<int> GetIntAsync(string key, int fallback = 0)
        => await GetAsync(key, fallback);

    /// <summary>Returns a bool value, or the fallback if the key doesn't exist.</summary>
    public async Task<bool> GetBoolAsync(string key, bool fallback = false)
        => await GetAsync(key, fallback);

    /// <summary>Returns a double value, or the fallback if the key doesn't exist.</summary>
    public async Task<double> GetDoubleAsync(string key, double fallback = 0d)
        => await GetAsync(key, fallback);

    /// <summary>Deserializes a JSON value into T, or returns the fallback.</summary>
    public async Task<T> GetAsync<T>(string key, T fallback)
    {
        var cacheKey = $"cfg:{key}";

        if (_cache.TryGetValue(cacheKey, out T? cached))
            return cached!;

        var setting = await _db.ConfigSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key);

        if (setting is null)
            return fallback;

        var result = Deserialize<T>(setting.Value);
        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    // ── Write ──────────────────────────────────

    /// <summary>Sets a single config value. Creates or updates.</summary>
    public async Task SetAsync<T>(string key, T value)
    {
        var serialized = Serialize(value);

        var setting = await _db.ConfigSettings.FindAsync(key);
        if (setting is null)
        {
            _db.ConfigSettings.Add(new ConfigSetting
            {
                Key = key,
                Value = serialized,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            setting.Value = serialized;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        _cache.Remove($"cfg:{key}");
    }

    /// <summary>Sets multiple config values in a single transaction.</summary>
    public async Task SetManyAsync(Dictionary<string, object> values)
    {
        var keys = values.Keys.ToList();
        var existing = await _db.ConfigSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key);

        foreach (var (key, value) in values)
        {
            var serialized = Serialize(value);

            if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = serialized;
                setting.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.ConfigSettings.Add(new ConfigSetting
                {
                    Key = key,
                    Value = serialized,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            _cache.Remove($"cfg:{key}");
        }

        await _db.SaveChangesAsync();
    }

    // ── Query ──────────────────────────────────

    /// <summary>Returns all config entries whose key starts with the given prefix.</summary>
    public async Task<Dictionary<string, string>> GetSectionAsync(string prefix)
    {
        return await _db.ConfigSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith(prefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    // ── Delete ─────────────────────────────────

    /// <summary>Removes a config entry. Returns false if the key didn't exist.</summary>
    public async Task<bool> DeleteAsync(string key)
    {
        var setting = await _db.ConfigSettings.FindAsync(key);
        if (setting is null) return false;

        _db.ConfigSettings.Remove(setting);
        await _db.SaveChangesAsync();
        _cache.Remove($"cfg:{key}");
        return true;
    }

    // ── Serialization ──────────────────────────

    private static T Deserialize<T>(string raw)
    {
        var type = typeof(T);

        if (type == typeof(string)) return (T)(object)raw;
        if (type == typeof(int) && int.TryParse(raw, out var i)) return (T)(object)i;
        if (type == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
        if (type == typeof(double) && double.TryParse(raw, out var d)) return (T)(object)d;

        return JsonSerializer.Deserialize<T>(raw)!;
    }

    private static string Serialize<T>(T value)
    {
        return value is string s ? s : JsonSerializer.Serialize(value);
    }
}