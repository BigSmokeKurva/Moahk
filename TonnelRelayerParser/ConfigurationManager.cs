using Microsoft.Extensions.Configuration;
using NLog;

namespace Moahk;

public static class ConfigurationManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly IConfigurationRoot Configuration;

    static ConfigurationManager()
    {
        try
        {
            Configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", true, true)
                .Build();

            Logger.Info("Конфигурация успешно загружена");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка при загрузке конфигурации");
            Configuration = new ConfigurationBuilder().Build();
        }
    }

    public static string? GetString(string key, string? defaultValue = null)
    {
        return Configuration[key] ?? defaultValue;
    }

    public static int GetInt(string key, int defaultValue = 0)
    {
        return int.TryParse(Configuration[key], out var value) ? value : defaultValue;
    }

    public static long GetLong(string key, long defaultValue = 0)
    {
        return long.TryParse(Configuration[key], out var value) ? value : defaultValue;
    }

    public static bool GetBool(string key, bool defaultValue = false)
    {
        return bool.TryParse(Configuration[key], out var value) ? value : defaultValue;
    }

    public static double GetDouble(string key, double defaultValue = 0.0)
    {
        return double.TryParse(Configuration[key], out var value) ? value : defaultValue;
    }

    public static string? GetConnectionString(string name = "DefaultConnection")
    {
        return Configuration.GetConnectionString(name);
    }

    public static long[] GetLongArray(string key, char separator = ',')
    {
        var value = Configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator).Select(long.Parse).ToArray();
    }

    public static T? GetSection<T>(string sectionName) where T : class, new()
    {
        var section = new T();
        Configuration.GetSection(sectionName).Bind(section);
        return section;
    }
}