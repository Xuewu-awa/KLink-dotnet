using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>卡牌库、物品与卡组编码表（替代原 PlayerLibrary 静态类）。启动时初始化一次。</summary>
public class PlayerLibraryService
{
    public LibraryResponse Library { get; } = new(
        new List<LibraryItem>(),
        new List<object>()
    );

    public List<Item> Items { get; } = new();

    public Dictionary<string, CardLookup> DeckCodeTable { get; } = new();

    /// <summary>
    /// 载入卡牌库、卡组编码表，以及"默认对所有人开放"的物品表。
    ///
    /// PATCH(KLink): 上游只加载了表情与卡背两个 lib，而 library/ 下的
    /// avatarLib.json（头像，67 项）与 equipmentLib.json（饰品，86 项）**根本没被读** ——
    /// 结果就是客户端里表情、卡背是全的，头像和饰品却是空的（实测 /items 只返回 513 项）。
    /// 这几个 lib 在 KLink 的定位就是"全解锁清单"，所以四个都加载。
    /// 想关掉某一类，把对应文件清成空数组即可（/items 返回的是
    /// playerLibrary.Items 与玩家自带物品的并集，所以每个账号都会拿到全部）。
    /// </summary>
    public void InitLibrary(string deckCodePath, string emojiPath, string cardbackPath)
    {
        List<DeckCodeCard> cs = JsonSerializer.Deserialize(File.ReadAllText(deckCodePath), ConfigJsonContext.Default.ListDeckCodeCard) ?? new();
        List<string> emojis = JsonSerializer.Deserialize(File.ReadAllText(emojiPath), ConfigJsonContext.Default.ListString) ?? new();
        List<string> cbs = JsonSerializer.Deserialize(File.ReadAllText(cardbackPath), ConfigJsonContext.Default.ListString) ?? new();
        List<string> avatars = LoadStringList("./library/avatarLib.json");
        List<string> equipment = LoadStringList("./library/equipmentLib.json");

        var botCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        string botCodePath = Path.Combine(AppContext.BaseDirectory, "BotData", "deck_code_ids.json");
        if (File.Exists(botCodePath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(botCodePath));
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } name)
                    botCodes[entry.Name] = name;
            }
        }

        foreach (var c in cs)
        {
            string name = botCodes.GetValueOrDefault(c.deck_code_id, c.card);
            Library.Cards.Add(new LibraryItem(name, 40, 0, c.ID, 0));
            if (!DeckCodeTable.ContainsKey(c.deck_code_id))
                DeckCodeTable.Add(c.deck_code_id, new CardLookup(name, c.deck_code_id, c.ID));
        }
        foreach (var (code, name) in botCodes)
            DeckCodeTable.TryAdd(code, new CardLookup(name, code, 0));

        foreach (var e in emojis)
            Items.Add(new Item("{}", e, 0));

        foreach (var c in cbs)
            Items.Add(new Item("{}", c, 0));

        foreach (var a in avatars)
            Items.Add(new Item("{}", a, 0));

        foreach (var e in equipment)
            Items.Add(new Item("{}", e, 0));
    }

    /// <summary>读取一个字符串数组 lib；文件缺失或格式不对时返回空表，不阻断启动。</summary>
    private static List<string> LoadStringList(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<string>();
            return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.ListString) ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[library] 读取 {path} 失败：{ex.Message}");
            return new List<string>();
        }
    }
}
