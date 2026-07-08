using System.Security.Cryptography;
using System.Text.Json;

namespace Mangarr.Api.Configuration;

public class ConfigFile
{
    public int Port { get; set; } = 8990;
    public string ApiKey { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "Information";
    public string UrlBase { get; set; } = string.Empty;
}

/// <summary>Loads /config/config.json, generating it (with a fresh API key) on first run.</summary>
public class ConfigFileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigFileProvider(AppPaths paths)
    {
        if (File.Exists(paths.ConfigFile))
        {
            Config = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(paths.ConfigFile)) ?? new ConfigFile();
        }
        else
        {
            Config = new ConfigFile();
        }

        if (string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            Config.ApiKey = GenerateApiKey();
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(Config, JsonOptions));
        }
    }

    public ConfigFile Config { get; }

    private static string GenerateApiKey()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}
