using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace Bluetask.Services;

public static class SettingsServiceHelper
{
    private static ApplicationDataContainer? _localSettings;
    private static readonly object _lock = new object();
    private static readonly string _settingsFilePath;
    private static Dictionary<string, object> _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    static SettingsServiceHelper()
    {
        try
        {
            _localSettings = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            _localSettings = null;
        }

        try
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bluetask");
            Directory.CreateDirectory(baseDir);
            _settingsFilePath = Path.Combine(baseDir, "settings.json");
        }
        catch
        {
            _settingsFilePath = Path.Combine(Path.GetTempPath(), "Bluetask.settings.json");
        }

        if (_localSettings == null)
        {
            LoadFromFile();
        }
    }

    public static T GetValue<T>(string key, T defaultValue)
    {
        try
        {
            if (_localSettings != null)
            {
                if (_localSettings.Values.TryGetValue(key, out var value))
                {
                    return ConvertTo<T>(value, defaultValue);
                }
                return defaultValue;
            }
            else
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var value))
                    {
                        return ConvertTo<T>(value, defaultValue);
                    }
                }
                return defaultValue;
            }
        }
        catch { return defaultValue; }
    }

    public static void SetValue<T>(string key, T value)
    {
        try
        {
            if (_localSettings != null)
            {
                _localSettings.Values[key] = value!;
            }
            else
            {
                lock (_lock)
                {
                    _cache[key] = value!;
                    SaveToFile();
                }
            }
        }
        catch { }
    }

    private static T ConvertTo<T>(object value, T defaultValue)
    {
        try
        {
            if (value is T t) return t;

            var targetType = typeof(T);
            if (targetType.IsEnum)
            {
                try
                {
                    if (value is string se && int.TryParse(se, out var siv))
                        return (T)Enum.ToObject(targetType, siv);
                    var iv = System.Convert.ToInt32(value);
                    return (T)Enum.ToObject(targetType, iv);
                }
                catch { return defaultValue; }
            }

            // Handle JsonElement from deserialization
            if (value is JsonElement je)
            {
                try
                {
                    var converted = je.Deserialize<T>();
                    if (converted != null) return converted;
                }
                catch { }
                try
                {
                    if (targetType == typeof(int)) return (T)(object)(je.GetInt32());
                    if (targetType == typeof(bool)) return (T)(object)(je.GetBoolean());
                    if (targetType == typeof(double)) return (T)(object)(je.GetDouble());
                    if (targetType == typeof(string)) return (T)(object)(je.GetString() ?? string.Empty);
                }
                catch { }
                return defaultValue;
            }

            return (T)System.Convert.ChangeType(value, targetType);
        }
        catch { return defaultValue; }
    }

    private static void LoadFromFile()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (dict != null) _cache = dict;
            }
        }
        catch { _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveToFile()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch { }
    }
}
