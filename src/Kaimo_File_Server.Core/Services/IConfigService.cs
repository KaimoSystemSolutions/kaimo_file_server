using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Services
{
    public interface IConfigService
    {
        /// <summary>
        /// Einzelnen Wert lesen. DB hat Vorrang, dann appsettings-Fallback.
        /// </summary>
        Task<T> GetAsync<T>(string key, T fallback);

        /// <summary>
        /// Einzelnen Wert lesen, ohne expliziten Fallback (default(T) wird verwendet).
        /// </summary>
        Task<T?> GetAsync<T>(string key);

        /// <summary>
        /// Wert in die DB schreiben (erstellt oder aktualisiert).
        /// </summary>
        Task SetAsync<T>(string key, T value, string? description = null, string? valueType = null);

        /// <summary>
        /// Mehrere Werte auf einmal lesen.
        /// </summary>
        Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys);

        /// <summary>
        /// Alle Config-Einträge mit einem bestimmten Prefix lesen.
        /// z.B. "app.user." liefert alle User-Defaults.
        /// </summary>
        Task<Dictionary<string, string>> GetByPrefixAsync(string prefix);

        /// <summary>
        /// Einen Eintrag löschen.
        /// </summary>
        Task<bool> DeleteAsync(string key);

        /// <summary>
        /// Alle Einträge auflisten (für Admin-UI).
        /// </summary>
        Task<IReadOnlyList<ConfigSettingDto>> GetAllAsync();

        /// <summary>
        /// Cache für einen bestimmten Key invalidieren.
        /// </summary>
        void InvalidateCache(string key);

        /// <summary>
        /// Gesamten Config-Cache leeren.
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
