using System.Text.Json;

namespace EssentiaDLP.Services;

public sealed class JsonFileStore
{
    private readonly string _dir;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public JsonFileStore(IConfiguration config)
    {
        _dir = Path.GetFullPath(config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(_dir);
    }

    public string DirectoryPath => _dir;

    public T Load<T>(string name, T fallback)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
            return fallback;
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, _json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, _json));
        File.Move(tmp, path, overwrite: true);
    }
}
