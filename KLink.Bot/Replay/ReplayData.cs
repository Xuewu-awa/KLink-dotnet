using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.Engine;
namespace KLink.Bot.Replay;

/// <summary>
/// 一局真实对局的回放数据 —— 从 fyserver 的只读接口导出：
/// <code>
/// GET /replays/{id}                        开局快照（双方手牌/牌库/HQ，带 cardID）
/// GET /replays/{id}/actions?limit=1000     全部动作（服务端已解密）
/// </code>
///
/// 这是**目前唯一能证伪规则内核的东西**：它给了完全确定的初始状态
/// 和一条真实的动作流，而动作流里还带着一个关键状态量
/// （<c>action_data["84"]</c> = 行动方自己的 HQ 当前防御力）。
/// </summary>
public sealed class ReplayData
{
    public required int MatchId { get; init; }
    public required int Turns { get; init; }
    public required int LeftPlayerId { get; init; }
    public required int RightPlayerId { get; init; }
    public required string WinnerSide { get; init; }

    /// <summary>
    /// **这一局里「官方客户端」是哪一方**。
    ///
    /// ⚠️ **2026-10-02 起它不再参与发号**：生成卡的编号规则
    /// （`回合号 × 1000 + 本回合第几张`）**对双方一致** —— 客户端的分配器
    /// `GenerateNextCardID(turnNumber, out id)` 没有 side 参数
    /// （`ref/kards-sim/.../_deps/BP_GameState_Battle.g.cs:1545`），
    /// 详见 `GameState.NextCardId`。以前只给"客户端那一方"用是错的，
    /// 会导致 bot 生成卡发顺序号 ⇒ 客户端认不出 ⇒ **虚空部署**。
    ///
    /// 这个字段同时用于回放时初始化 `GameState.ClientSide`，并保留为
    /// 诊断信息（"哪一方是真人在打"）。判据如下：
    /// <list type="bullet">
    /// <item>fyserver 给 bot 的占位 id 是**负数**（`-9178` = KLink，见 `tools/BotNameTest`），
    ///   7 局服务端回放全是「左=人类、右=bot」。</item>
    /// <item>两边都不是 bot（人人局 / 单边客户端日志）时，取**有动作的那一方**：
    ///   实测 12 份 fresh/live 回放全是单边日志（右方只有 1 条空动作），
    ///   录制方就是左方。两边都有动作且分不出时退回左方 —— 这个默认与
    ///   所有实测数据一致，但**人人局的对局记录方本质上是不可判的**，
    ///   真要精确就必须由调用方显式告知。</item>
    /// </list>
    /// </summary>
    public required Side ClientSide { get; init; }

    /// <summary>开局快照里的全部卡（含 HQ）。</summary>
    public required IReadOnlyList<SnapshotCard> Cards { get; init; }

    public required IReadOnlyList<WireAction> Actions { get; init; }

    private Dictionary<int, SnapshotCard>? _byId;

    /// <summary>
    /// 开局快照里 id → 卡。
    ///
    /// 这个映射是**可信的**：5 局回放里，动作流自带的卡组码与快照里的卡名
    /// **零冲突**（见 tools/check-id-semantics.py）。也就是说 `action_data["0"]`
    /// 的编号空间和快照的 `card_id` 是同一个。
    ///
    /// ⚠️ 但**手牌/牌库的归属不可信**：fyserver 用 `Random.Shared` 洗牌后
    /// `Take(4/5)` 当手牌（MatchManagerService.GetCardsFromDeck），而真实客户端
    /// 会打出快照里被判为「在牌库」的牌。所以回放驱动只能把快照当**卡池**用。
    /// </summary>
    public IReadOnlyDictionary<int, SnapshotCard> ById
        => _byId ??= Cards.GroupBy(c => c.CardId).ToDictionary(g => g.Key, g => g.First());

    public Side SideOf(int playerId) => playerId == LeftPlayerId ? Side.Left
        : playerId == RightPlayerId ? Side.Right
        : Side.NotAvailable;

    private string? _hqKey;
    private bool _hqKeyResolved;

    /// <summary>
    /// 推断「对手 HQ 防御力」这一字段在 <c>action_data</c> 里的下标。
    ///
    /// 为什么需要推断：客户端发的是「下标 → 值」，下标来自一张**每局重新登记**的
    /// 字段名表，所以 HQ 字段的下标每局都不同。实测：
    /// <code>
    /// 310284 → 84   165924 → 24   955337 → 37   130691 → 91   563868 → 68
    /// </code>
    ///
    /// 判据（三条同时满足）：
    /// 1. 键名是纯数字，且不是动作参数 0..4
    /// 2. 出现次数最多（HQ 是每个动作都会带的场上状态）
    /// 3. 首个取值是 20（HQ 初始防御）
    /// </summary>
    public string? InferHqKey()
    {
        if (_hqKeyResolved)
        {
            return _hqKey;
        }

        _hqKeyResolved = true;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Actions)
        {
            foreach (string k in a.ExtraStateKeys)
            {
                counts[k] = counts.GetValueOrDefault(k) + 1;
                first.TryAdd(k, a.Get(k) ?? "");
            }
        }

        _hqKey = counts
            .Where(kv => first[kv.Key] == MatchEngine.InitialHqDefense.ToString())
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Length)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        return _hqKey;
    }

    public sealed record SnapshotCard(int CardId, string Name, CardLocation Location, int LocationNumber,
                                      Side Owner, bool IsGold, string? Faction);

    // ==================== 加载 ====================

    public static ReplayData Load(string snapshotPath, string actionsPath)
    {
        var snap = JsonNode.Parse(File.ReadAllText(snapshotPath))?.AsObject()
                   ?? throw new FormatException("快照不是 JSON 对象");
        var summary = snap["summary"]?.AsObject() ?? throw new FormatException("快照缺少 summary");
        var startingData = snap["starting_info"]?["match_and_starting_data"]?["starting_data"]?.AsObject()
                           ?? throw new FormatException("快照缺少 starting_data");

        int leftId = summary["left_player_id"]?.GetValue<int>() ?? 0;
        int rightId = summary["right_player_id"]?.GetValue<int>() ?? 0;

        var cards = new List<SnapshotCard>();

        void AddCard(JsonNode? node, Side owner)
        {
            if (node is not JsonObject c)
            {
                return;
            }

            string? loc = c["location"]?.GetValue<string>();
            if (loc is null || !TryParseLocation(loc, out CardLocation location))
            {
                return;
            }

            cards.Add(new SnapshotCard(
                c["card_id"]?.GetValue<int>() ?? 0,
                c["name"]?.GetValue<string>() ?? "",
                location,
                c["location_number"]?.GetValue<int>() ?? 0,
                owner,
                c["is_gold"]?.GetValue<bool>() ?? false,
                c["faction"]?.GetValue<string>()));
        }

        AddCard(startingData["location_card_left"], Side.Left);
        AddCard(startingData["location_card_right"], Side.Right);

        foreach (string field in new[] { "starting_hand_left", "deck_left" })
        {
            if (startingData[field] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    AddCard(c, Side.Left);
                }
            }
        }

        foreach (string field in new[] { "starting_hand_right", "deck_right" })
        {
            if (startingData[field] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    AddCard(c, Side.Right);
                }
            }
        }

        var actionsDoc = JsonNode.Parse(File.ReadAllText(actionsPath))?.AsObject();
        var actions = new List<WireAction>();
        if (actionsDoc?["actions"] is JsonArray actionArr)
        {
            foreach (var a in actionArr)
            {
                if (a is not null)
                {
                    actions.Add(WireAction.Parse(a));
                }
            }
        }

        return new ReplayData
        {
            MatchId = summary["match_id"]?.GetValue<int>() ?? 0,
            Turns = summary["turns"]?.GetValue<int>() ?? 0,
            LeftPlayerId = leftId,
            RightPlayerId = rightId,
            WinnerSide = summary["winner_side"]?.GetValue<string>() ?? "",
            ClientSide = InferClientSide(leftId, rightId, actions),
            Cards = cards,
            Actions = actions,
        };
    }

    /// <summary>推断「官方客户端」那一方 —— 判据见 <see cref="ClientSide"/>。</summary>
    public static Side InferClientSide(int leftId, int rightId, IReadOnlyList<WireAction> actions)
    {
        if (leftId < 0)
        {
            return Side.Right;
        }

        if (rightId < 0)
        {
            return Side.Left;
        }

        // 两边都不是 bot：录制方 = 有动作的那一方（实测单边日志右方只有 1 条空动作）。
        int leftActions = actions.Count(a => a.PlayerId == leftId);
        int rightActions = actions.Count(a => a.PlayerId == rightId);
        return rightActions > leftActions ? Side.Right : Side.Left;
    }

    /// <summary>
    /// 这一局某一方的 HQ 卡名（例如 `card_location_london`）。
    ///
    /// 用途：**配对采集快照与回放时的内容指纹**。
    /// 以前是靠「快照的 act 有多大比例落在 actionId 里」配的，而 `actionId`
    /// 每局都从 1 顺序编号 ⇒ 那个判据对任何足够长的局都成立，
    /// 实测 7 对全配错（详见 `tools/BoardCompare/Program.cs` 里那段注释）。
    ///
    /// `CardId` 1 / 41 是固定的 HQ 槽位（快照与 `starting_data` 同一编号空间），
    /// 所以按 id + side 取就是这一方的总部。
    /// </summary>
    public string HqName(Side side)
        => Cards.FirstOrDefault(c => c.CardId == (side == Side.Left ? 1 : 41)
                                     && c.Owner == side)?.Name
           ?? Cards.FirstOrDefault(c => c.CardId == (side == Side.Left ? 1 : 41))?.Name
           ?? "";

    public static bool TryParseLocation(string wire, out CardLocation location)
    {
        location = wire switch
        {
            "deck_left" => CardLocation.DeckLeft,
            "deck_right" => CardLocation.DeckRight,
            "hand_left" => CardLocation.HandLeft,
            "hand_right" => CardLocation.HandRight,
            "board_hqleft" => CardLocation.BoardHqLeft,
            "board_hqright" => CardLocation.BoardHqRight,
            "board_frontline" => CardLocation.BoardFrontline,
            "board_left" => CardLocation.BoardFrontline,
            "board_right" => CardLocation.BoardFrontline,
            "discard" => CardLocation.Discard,
            "deck" => CardLocation.Deck,
            var _ => CardLocation.NotAvailable,
        };

        return location != CardLocation.NotAvailable;
    }
}
