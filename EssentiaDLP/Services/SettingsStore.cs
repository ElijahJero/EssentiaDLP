using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class SettingsStore
{
    private const string FileName = "galaxy.json";
    private readonly JsonFileStore _store;
    private readonly object _gate = new();

    public SettingsStore(JsonFileStore store) => _store = store;

    public AppSettings Get()
    {
        lock (_gate)
            return _store.Load(FileName, new AppSettings());
    }

    public AppSettings Update(AppSettingsUpdate patch)
    {
        lock (_gate)
        {
            var current = _store.Load(FileName, new AppSettings());
            if (patch.Url is not null)
                current.Url = patch.Url.Trim().TrimEnd('/');
            if (patch.ApiKey is not null && patch.ApiKey.Length > 0)
                current.ApiKey = patch.ApiKey;
            if (patch.MaxSongs is not null)
                current.MaxSongs = patch.MaxSongs.Value;
            if (patch.MaxSongsPerMinute is not null)
                current.MaxSongsPerMinute = patch.MaxSongsPerMinute.Value;
            if (patch.Cron is not null)
                current.Cron = patch.Cron.Trim();
            _store.Save(FileName, current);
            return current;
        }
    }
}
