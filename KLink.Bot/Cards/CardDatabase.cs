using System.Text.Json;
using System.Text.Json.Serialization;

namespace KLink.Bot.Cards;

/// <summary>
/// 一张卡的静态定义。
///
/// 数据来源（全部离线提取，见 klink bot/docs/）：
/// - <c>cards-from-fmodel.json</c>  → 数值与英文描述
/// - <c>card-effects.json</c>       → 反编译出的效果调用序列
///
/// 注意：**没有结构化效果字段**。效果就是 <see cref="ExternalCalls"/> 这串函数名，
/// 由规则内核在执行到该卡时逐个调用（见 Effects/CardApi.cs）。
/// </summary>
public sealed class CardDefinition
{
    /// <summary>卡名，如 <c>card_event_aans</c>。</summary>
    public string Name { get; init; } = "";

    public string? Title { get; init; }
    public string? Text { get; init; }
    public string? Type { get; init; }          // order / infantry / tank / ...
    public string? Faction { get; init; }
    public string? Rarity { get; init; }
    public string? CardSet { get; init; }

    public int Kredits { get; init; }           // 费用
    public int OperationCost { get; init; }     // 行动成本（单位）
    public int Attack { get; init; }
    public int Defense { get; init; }
    public int Range { get; init; }
    public int EffectType { get; init; }
    public int ChooseOneOptionCount { get; init; }
    public IReadOnlyList<bool> ChooseOneSelectTargets { get; init; } = [];
    public bool SelectTargetOnPlayedFromHand { get; init; }
    public string CustomName1 { get; init; } = "None";
    public string CustomName2 { get; init; } = "None";
    public int Cipher { get; init; }
    public int TargetTax { get; init; }
    /// <summary>CDO: defer played/entry observers until OnHandTargetSelected completes.</summary>
    public bool WaitForHandTarget { get; init; }
    public IReadOnlyDictionary<string, int[]> InnateAbilities { get; init; }
        = new Dictionary<string, int[]>(StringComparer.Ordinal);
    public IReadOnlyList<string> SuppressionExceptionTriggers { get; init; } = [];
    /// <summary>CDO usedTriggers, registered by CreateCardObject in the event map.</summary>
    public IReadOnlyList<string> UsedTriggers { get; init; } = [];

    // The SDK enum names T33 OnOtherCardBlitzChange; its callback UFunction
    // is named OnOtherCardBlitzChanged. It is distinct from T29.
    public static string RegisteredTriggerName(string programName)
        => programName == "OnOtherCardBlitzChanged" ? "OnOtherCardBlitzChange" : programName;

    public bool IsRegisteredFor(string programName)
        => UsedTriggers.Contains(RegisteredTriggerName(programName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 卡面自带的重甲点数（CDO 的 <c>heavyArmor</c>，全部 2019 张里只有 52 张有）。
    ///
    /// 之前没有这个字段，`ChangeHeavyArmor` 就只能靠关键字表示「有没有重甲」，
    /// 而它是**可叠加的数值**（`card_unit_214th_amur` 的「你的 T-34 +1 重甲」）。
    ///
    /// ⚠️ 数值不来自 `cards.live.json`（那里**没有** `heavyArmor` 字段，读出来恒 0），
    /// 而是从 `CardInnateTable`（pak CDO）填。见 <see cref="Keywords"/> 的注释。
    /// </summary>
    public int HeavyArmor { get; init; }

    /// <summary>
    /// **卡面自带**的关键字（Blitz / Guard / Ambush / Fury / Smokescreen / Alpine /
    /// Mobilize / Salvage / Shock），建卡时灌进 `CardInstance.Keywords`。
    ///
    /// ⚠️ 来源是 `CardInnateTable`（从 pak 的 CDO 抽的），**不是 `cards.live.json`** ——
    /// 那份文件里只有 `hasGuard` 一个布尔字段。实测后果：**所有卡的
    /// `CardInstance.Keywords` 建出来都是空的**，包括 205 张天生 Blitz。
    /// 这在实现召唤失调时会直接致命 —— 判据是
    /// `enterPlayOnTurn == currentTurn &amp;&amp; !getHasBlitz()`，Blitz 读不到
    /// ⇒ 所有 Blitz 单位都被误判成"刚部署、不能动"。
    /// 同时它也修好了 128 张 Guard 卡的嘲讽（`MatchEngine.LegalTargets` 读 `Keyword.Guard`）。
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 阵营的**枚举值**，对应 `/Script/KardsCore.EFactionEnum`
    /// （从 kards1.60_No_UE4SS.jmap 读出：Germany=1, Britain=2, Japan=3, Soviet=4,
    /// USA=5, France=6, Italy=7, Poland=8, Finland=9, Anzac=10, Allies=11, Neutral=12）。
    ///
    /// 为什么不能只留字符串：卡蓝图里的「阵营判定」是
    /// `EnumCompareFaction(卡.faction, 1, out …)` —— **拿枚举值和数字比**。
    /// 只存字符串的话这个比较永远拿不到正确的一侧。
    /// </summary>
    public int FactionId { get; init; }

    // Verified against all current pak labels and read-only card CDO scalars.
    public int TypeId => Type switch
    {
        "location" => 1, "order" => 2, "tank" => 3, "fighter" => 4,
        "bomber" => 5, "infantry" => 6, "artillery" => 7,
        "gotcha" => 11, "wildcard" => 12, _ => 0,
    };
    public int RarityId => Rarity switch
    {
        "Common" => 1, "Uncommon" => 2, "Rare" => 3, "Unique" => 4, _ => 0,
    };

    /// <summary>该卡在反编译里用到的所有「外部」调用名（去掉 BP 内置与 BP_OnlineMatch 内部函数）。</summary>
    public IReadOnlyList<string> ExternalCalls { get; init; } = Array.Empty<string>();

    /// <summary>函数名 → 该函数调用的外部函数。用于区分触发时机（OnPlayedFromHand / ExecuteUbergraph_* 等）。</summary>
    public IReadOnlyDictionary<string, string[]> FunctionCalls { get; init; }
        = new Dictionary<string, string[]>();

    public bool IsUnit => Type is "infantry" or "tank" or "artillery" or "fighter" or "bomber" or "antiair";
    public bool IsOrder => Type == "order";
    public bool IsLocationCard => Type == "location";

    /// <summary>同名函数里挑出「从手牌打出时」的逻辑。卡蓝图里是 OnPlayedFromHand + ExecuteUbergraph。</summary>
    public IEnumerable<string> PlayFromHandCalls()
    {
        foreach (var (fn, calls) in FunctionCalls)
        {
            if (fn.StartsWith("OnPlayedFromHand", StringComparison.Ordinal) ||
                fn.StartsWith("ExecuteUbergraph", StringComparison.Ordinal))
            {
                foreach (string c in calls)
                {
                    yield return c;
                }
            }
        }
    }

    public override string ToString() => $"{Name}({Kredits}{(IsUnit ? $" {Attack}/{Defense}" : "")})";
}

/// <summary>加载卡牌静态数据。<see cref="CardDatabase.Load"/> 只做一次，之后按名字查。</summary>
public sealed class CardDatabase
{
    private readonly Dictionary<string, CardDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>2 字符卡组码 → 卡名。</summary>
    public IReadOnlyDictionary<string, string> DeckCodeIds { get; }

    /// <summary>
    /// 卡名 → 卡组码（<see cref="DeckCodeIds"/> 的反查）。
    ///
    /// 用途：`CS`（`XActionCardToDrawSelected`）动作的槽位 2 要填
    /// **选中那张卡的 deck code**，不是 cardID（cardID 是对局内编号，
    /// 而"开发"选中的是一张**卡池模板**，根本没有对局内编号）。
    ///
    /// 惰性构建：反查表只在真的选过牌时才用到，绝大多数对局用不上。
    /// </summary>
    private Dictionary<string, string>? _codeByName;

    public string? DeckCodeFor(string cardName)
    {
        if (_codeByName is null)
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (code, name) in DeckCodeIds)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    m[name] = code;     // 同名多码时取第一个（实测无此情况）
                }
            }

            _codeByName = m;
        }

        return _codeByName.GetValueOrDefault(cardName);
    }

    public int Count => _byName.Count;

    public IReadOnlyCollection<CardDefinition> All => _byName.Values;

    private CardDatabase(Dictionary<string, CardDefinition> byName, Dictionary<string, string> deckCodeIds)
    {
        _byName = byName;
        DeckCodeIds = deckCodeIds;
    }

    /// <summary>
    /// 卡名归一化：卡组码表里会出现 <c>xxx_bal</c>（平衡变体）和 <c>xxx_vet</c>（老兵变体），
    /// 但蓝图里只有基础卡 <c>xxx</c> —— 这些变体是**数据变体**，逻辑复用基础卡。
    /// 依次剥掉后缀再查。
    /// </summary>
    private static readonly string[] VariantSuffixes = { "_bal", "_vet", "_alt", "_gold" };

    public CardDefinition? Find(string name)
    {
        if (_byName.TryGetValue(name, out var direct))
        {
            return direct;
        }

        foreach (string suffix in VariantSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)
                && _byName.TryGetValue(name[..^suffix.Length], out var baseCard))
            {
                VariantFallbacks++;
                return baseCard;
            }
        }

        return null;
    }

    /// <summary>有多少次查询是靠变体回退命中的（用于发现数值缺口）。</summary>
    public int VariantFallbacks { get; private set; }

    /// <summary>
    /// 解析出实际有蓝图逻辑的卡名（供 Kismet 解释器用）。
    /// <c>card_unit_x_bal</c> → <c>card_unit_x</c>
    /// </summary>
    public static string ResolveBaseName(string name)
    {
        foreach (string suffix in VariantSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>
    /// 阵营名 → <c>EFactionEnum</c> 枚举值。
    ///
    /// 取值来自 `/Script/KardsCore.EFactionEnum`（kards1.60_No_UE4SS.jmap）：
    /// NotAvailable=0, Germany=1, Britain=2, Japan=3, Soviet=4, USA=5,
    /// France=6, Italy=7, Poland=8, Finland=9, Anzac=10, Allies=11, Neutral=12。
    ///
    /// ⚠️ 顺序**不是**字母序、也不是 kardsim 里那份枚举的顺序（那份把 Soviet 放在 2），
    /// 认错了会让「德国单位」这类判据匹配到别的阵营。认不出来一律 0（NotAvailable），
    /// 宁可判据不成立，也不要猜一个阵营出来。
    /// </summary>
    public static int FactionIdOf(string? faction) => faction switch
    {
        "Germany" => 1,
        "Britain" => 2,
        "Japan" => 3,
        "Soviet" => 4,
        "USA" => 5,
        "France" => 6,
        "Italy" => 7,
        "Poland" => 8,
        "Finland" => 9,
        "Anzac" => 10,
        "Allies" => 11,
        "Neutral" => 12,
        _ => 0,
    };

    public CardDefinition Require(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"卡牌数据里没有 '{name}'");

    public static CardDatabase Load(string dataDirectory)
    {
        string fmodelPath = Path.Combine(dataDirectory, "cards.json");
        string effectsPath = Path.Combine(dataDirectory, "card-effects.json");
        string idsPath = Path.Combine(dataDirectory, "deck_code_ids.json");

        foreach (string p in new[] { fmodelPath, effectsPath, idsPath })
        {
            if (!File.Exists(p))
            {
                throw new FileNotFoundException(
                    $"缺少数据文件 '{p}'。请先运行 klink bot/tools/ 下的生成脚本，或检查 csproj 的 CopyToOutputDirectory。");
            }
        }

        // 注意：必须用**大小写敏感**匹配。
        // cards-from-fmodel.json 里同时有 cardSet 和 cardset 两个兼容键，
        // 大小写不敏感会直接抛 "property name collides"。
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            // ⚠️⚠️ 必须**显式**指定反射式解析器。
            //
            // 宿主可能把反射式序列化关掉 —— fyserver 就是：它 csproj 里写着
            // `JsonSerializerIsReflectionEnabledByDefault=false`（为了 AOT 兼容）。
            // 那个开关是**进程级**的，会连累所有 `JsonSerializer.Deserialize<T>`，
            // 包括本类 ⇒ **内核整个加载失败**。
            //
            // 实测症状极具误导性：服务器照常启动（`ServerBotService` 是懒加载的），
            // 只在真对局里表现成「bot 只会跳过回合」——
            // 真正的异常藏在服务端日志的 `bot 内核加载失败` 那一行。
            //
            // 显式给 `DefaultJsonTypeInfoResolver` 只影响本类，
            // 不改宿主的全局策略（fyserver 自己的那份仍走源生成）。
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        // ---- 数值（FModel 导出）----
        var raw = JsonSerializer.Deserialize<Dictionary<string, FmodelCard>>(
            File.ReadAllText(fmodelPath), options) ?? new();

        // ---- 效果调用（反编译）----
        var effects = JsonSerializer.Deserialize<Dictionary<string, EffectEntry>>(
            File.ReadAllText(effectsPath), options) ?? new();

        var byName = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);

        foreach (var (name, c) in raw)
        {
            effects.TryGetValue(name, out var e);

            var funcCalls = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (e?.Functions is not null)
            {
                foreach (var (fn, calls) in e.Functions)
                {
                    funcCalls[fn] = calls ?? Array.Empty<string>();
                }
            }

            // 卡面自带的「关键字 + 重甲」取自 pak CDO 抽出来的表 ——
            // cards.live.json 里这两项都缺（只有 hasGuard，heavyArmor 完全没有）。
            var (innateKeywords, innateArmor) = CardInnateTable.Get(name);

            byName[name] = new CardDefinition
            {
                Name = name,
                Title = c.Title,
                Text = c.Text,
                Type = c.Type,
                Faction = c.Faction,
                Rarity = c.Rarity,
                CardSet = c.CardSet ?? c.Cardset,
                Kredits = c.Kredits ?? 0,
                OperationCost = c.OperationCost ?? 0,
                Attack = c.Attack ?? 0,
                Defense = c.Defense ?? 0,
                Range = c.Range ?? 0,
                // The pak exporter retains nonzero enum ordinals in this form.
                EffectType = int.TryParse(c.EffectType?.Replace("UASSETAPI_INVALID_ENUM_IDX_", "", StringComparison.Ordinal),
                    out int effectType) ? effectType : 0,
                ChooseOneOptionCount = c.ChooseOneOptionCount,
                ChooseOneSelectTargets = c.ChooseOneSelectTargets ?? [],
                SelectTargetOnPlayedFromHand = c.SelectTargetOnPlayedFromHand,
                CustomName1 = c.CustomName1 ?? "None",
                CustomName2 = c.CustomName2 ?? "None",
                Cipher = c.Cipher ?? 0,
                TargetTax = c.TargetTax ?? 0,
                WaitForHandTarget = c.WaitForHandTarget,
                InnateAbilities = c.InnateAbilities ?? new Dictionary<string, int[]>(StringComparer.Ordinal),
                SuppressionExceptionTriggers = c.SuppressionExceptionTriggers ?? [],
                UsedTriggers = c.UsedTriggers ?? [],
                // 优先用 CDO 表的值；表里没有才退回 cards 文件（后者目前恒 0）
                HeavyArmor = innateArmor > 0 ? innateArmor : c.HeavyArmor ?? 0,
                Keywords = innateKeywords,
                FactionId = FactionIdOf(c.Faction),
                ExternalCalls = e?.ExternalCalls ?? Array.Empty<string>(),
                FunctionCalls = funcCalls,
            };
        }

        // ---- 卡组码映射 ----
        // 兼容两种形状：
        //   {"01": "card_unit_xxx"}                    ← tools/convert-deckcode-table.py（线上 pak 权威表）
        //   {"01": {"card": "card_unit_xxx", ...}}     ← Assets/kards-server/deck_code_ids.json（旧格式）
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllText(idsPath)))
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        ids[prop.Name] = prop.Value.GetString()!;
                        break;
                    case JsonValueKind.Object when prop.Value.TryGetProperty("card", out var cardEl)
                                                   && cardEl.ValueKind == JsonValueKind.String:
                        ids[prop.Name] = cardEl.GetString()!;
                        break;
                }
            }
        }

        return new CardDatabase(byName, ids);
    }

    // ---- 反序列化用的中间类型 ----

    private sealed class FmodelCard
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("faction")] public string? Faction { get; set; }
        [JsonPropertyName("rarity")] public string? Rarity { get; set; }
        [JsonPropertyName("cardSet")] public string? CardSet { get; set; }
        [JsonPropertyName("cardset")] public string? Cardset { get; set; }
        [JsonPropertyName("kredits")] public int? Kredits { get; set; }
        [JsonPropertyName("operationcost")] public int? OperationCost { get; set; }
        [JsonPropertyName("attack")] public int? Attack { get; set; }
        [JsonPropertyName("defense")] public int? Defense { get; set; }
        [JsonPropertyName("range")] public int? Range { get; set; }
        [JsonPropertyName("effectType")] public string? EffectType { get; set; }
        [JsonPropertyName("heavyArmor")] public int? HeavyArmor { get; set; }
        [JsonPropertyName("chooseOneOptionCount")] public int ChooseOneOptionCount { get; set; }
        [JsonPropertyName("chooseOneSelectTargets")] public bool[]? ChooseOneSelectTargets { get; set; }
        [JsonPropertyName("selectTargetOnPlayedFromHand")] public bool SelectTargetOnPlayedFromHand { get; set; }
        [JsonPropertyName("customName1")] public string? CustomName1 { get; set; }
        [JsonPropertyName("customName2")] public string? CustomName2 { get; set; }
        [JsonPropertyName("cipher")] public int? Cipher { get; set; }
        [JsonPropertyName("targetTax")] public int? TargetTax { get; set; }
        [JsonPropertyName("waitForHandTarget")] public bool WaitForHandTarget { get; set; }
        [JsonPropertyName("innateAbilities")] public Dictionary<string, int[]>? InnateAbilities { get; set; }
        [JsonPropertyName("suppressionExceptionTriggers")] public string[]? SuppressionExceptionTriggers { get; set; }
        [JsonPropertyName("usedTriggers")] public string[]? UsedTriggers { get; set; }
    }

    private sealed class EffectEntry
    {
        [JsonPropertyName("external_calls")] public string[]? ExternalCalls { get; set; }

        /// <summary>函数名 → 该函数调用的外部函数名列表（gen-card-effects.py 的产物形状）。</summary>
        [JsonPropertyName("functions")] public Dictionary<string, string[]>? Functions { get; set; }
    }

}
