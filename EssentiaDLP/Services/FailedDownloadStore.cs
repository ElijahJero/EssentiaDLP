using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class FailedDownloadStore
{
    private const string FileName = "failed_downloads.json";
    private readonly JsonFileStore _store;
    private readonly object _gate = new();

    public FailedDownloadStore(JsonFileStore store) => _store = store;

    public List<FailedItem> List()
    {
        lock (_gate)
            return _store.Load(FileName, new List<FailedItem>());
    }

    public FailedItem Upsert(FailedItem item)
    {
        lock (_gate)
        {
            var items = _store.Load(FileName, new List<FailedItem>());
            var idx = items.FindIndex(x => x.Id == item.Id);
            if (idx >= 0)
                items[idx] = item;
            else
                items.Add(item);
            _store.Save(FileName, items);
            return item;
        }
    }

    public FailedItem? Get(string id)
    {
        lock (_gate)
            return _store.Load(FileName, new List<FailedItem>()).FirstOrDefault(x => x.Id == id);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var items = _store.Load(FileName, new List<FailedItem>());
            var n = items.RemoveAll(x => x.Id == id);
            if (n > 0)
                _store.Save(FileName, items);
            return n > 0;
        }
    }
}
