using System.Text.Json;
using System.Text.Json.Serialization;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class RuntimeSettingsStore(IWebHostEnvironment environment, IConfiguration configuration) : IRuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    public RuntimeSettings Load()
    {
        lock (_gate)
        {
            var path = GetPath();
            if (!File.Exists(path))
            {
                return LoadFromConfiguration();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuntimeSettings>(json, JsonOptions) ?? new RuntimeSettings();
        }
    }

    public async Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private RuntimeSettings LoadFromConfiguration()
    {
        var settings = new RuntimeSettings();
        configuration.GetSection("Kimai").Bind(settings);
        configuration.GetSection("Admin").Bind(settings);
        return settings;
    }

    private string GetPath()
    {
        var configuredPath = configuration["Stempeluhr:SettingsPath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data", "settings.json")
            : configuredPath;
    }
}
