using System.Collections;

namespace KLink.Bot.Engine;

// Unreal EJson: String=2, Number=3, Boolean=4, Array=5. Keep the string
// projection for diagnostics, but never infer a JSON type from its contents.
public enum CardJsonType { String = 2, Number = 3, Boolean = 4, IntArray = 5 }

public sealed class CardCustomJson : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardJsonType> _types = new(StringComparer.Ordinal);

    public string this[string key]
    {
        get => _values[key];
        set => Set(key, value, CardJsonType.String);
    }
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<string> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    public bool HasType(string key, CardJsonType type) => _types.TryGetValue(key, out var actual) && actual == type;
    public CardJsonType TypeOf(string key) => _types[key];

    public void Set(string key, string value, CardJsonType type)
    {
        _values[key] = value;
        _types[key] = type;
    }
    public bool Remove(string key)
    {
        _types.Remove(key);
        return _values.Remove(key);
    }
    public void Clear() { _values.Clear(); _types.Clear(); }
    public void CopyFrom(CardCustomJson source)
    {
        foreach (var pair in source) Set(pair.Key, pair.Value, source.TypeOf(pair.Key));
    }
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
