using System.Text.Json;
using EmailToDiscord.Configuration;

namespace EmailToDiscord.Services;

public class StateStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private State _state;

    public StateStore(AppConfig config)
    {
        _path = config.State.FilePath;
        _state = Load();
    }

    public uint? GetLastUid(string mailboxKey)
    {
        lock (_lock)
        {
            return _state.LastUidByMailbox.TryGetValue(mailboxKey, out var uid) ? uid : null;
        }
    }

    public void SetLastUid(string mailboxKey, uint uid)
    {
        lock (_lock)
        {
            _state.LastUidByMailbox[mailboxKey] = uid;
            Save();
        }
    }

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private class State
    {
        public Dictionary<string, uint> LastUidByMailbox { get; set; } = new();
    }
}
