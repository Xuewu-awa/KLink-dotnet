using System.Text.Json.Nodes;
using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.Server;
using KLink.Server.Data;
using KLink.Server.Model;
using KLink.Server.Util;

namespace KLink.Server;

/// <summary>
/// 匹配队列 + Bot 对战 + 动作存储（MatchManager）。
/// </summary>
public sealed class MatchManager
{
    private readonly AppDatabase _database;
    private readonly DeckCodeManager _deckCodeManager;
    private readonly ActionCipher _actionCipher = new();
    private readonly object _lock = new();

    private readonly HashSet<int> _onlinePlayers = new();
    private readonly Queue<int> _rankedQueue = new();
    private readonly Queue<int> _casualQueue = new();
    private readonly Queue<int> _brawlQueue = new();
    private readonly Queue<int> _diyQueue = new();
    private readonly Dictionary<string, Queue<int>> _codeQueues = new();
    private readonly Dictionary<int, string> _playerCodes = new();
    private readonly Dictionary<int, int> _playerDecks = new();
    private readonly Dictionary<int, int> _playing = new();
    private readonly Dictionary<int, MatchState> _matches = new();
    private readonly HashSet<int> _kickedPlayers = new();
    private int _nextMatchId = 1;
    private BotTurnService? _botService;
    private string? _botLoadError;

    public MatchManager(AppDatabase database, AssetStore assets)
    {
        _database = database;
        _deckCodeManager = new DeckCodeManager(assets);
    }

    public void SetOnline(int userId, bool online)
    {
        lock (_lock)
        {
            if (online)
            {
                _onlinePlayers.Add(userId);
            }
            else
            {
                _onlinePlayers.Remove(userId);
                RemoveFromQueues(userId);
                if (_playing.TryGetValue(userId, out int matchId) && _matches.TryGetValue(matchId, out var match))
                {
                    if (match.PlayerLeft == userId) match.LeftOnline = false;
                    if (match.PlayerRight == userId) match.RightOnline = false;
                    // 已结束的对局在玩家离线时清理
                    if (match.Status == "finished")
                        CleanupMatch(matchId);
                }
            }
            _database.SetOnline(userId, online);
        }
    }

    public bool IsOnline(int userId) { lock (_lock) { return _onlinePlayers.Contains(userId); } }

    public void KickPlayer(int userId)
    {
        lock (_lock)
        {
            _kickedPlayers.Add(userId);
            SetOnline(userId, false);
        }
    }

    public bool IsKicked(int userId) { lock (_lock) { return _kickedPlayers.Contains(userId); } }

    public int MatchCount() { lock (_lock) { return _matches.Count; } }

    public bool AddToQueue(int playerId, int deckId, string? extraData)
    {
        lock (_lock)
        {
            if (_playing.TryGetValue(playerId, out int existingMatchId))
            {
                if (!_matches.ContainsKey(existingMatchId))
                {
                    _playing.Remove(playerId);
                    _playerDecks.Remove(playerId);
                }
                else
                {
                    // 如果旧对局已结束，先清理再允许排新队
                    var existing = _matches[existingMatchId];
                    if (existing.Status == "finished")
                        CleanupMatch(existingMatchId);
                    else
                        return false;
                }
            }
            if (_playerDecks.ContainsKey(playerId) || _playerCodes.ContainsKey(playerId)
                || _rankedQueue.Contains(playerId) || _casualQueue.Contains(playerId)
                || _brawlQueue.Contains(playerId) || _diyQueue.Contains(playerId))
            {
                RemoveFromQueues(playerId);
            }
            if (_playerDecks.ContainsKey(playerId))
                return false;
            _playerDecks[playerId] = deckId;
            string matchType = extraData ?? "";
            if (matchType.StartsWith("battle_code:"))
            {
                string code = matchType["battle_code:".Length..].Trim();
                if (code.Length == 0)
                    return false;
                if (!_codeQueues.TryGetValue(code, out var queue))
                {
                    queue = new Queue<int>();
                    _codeQueues[code] = queue;
                }
                _playerCodes[playerId] = code;
                queue.Enqueue(playerId);
                if (queue.Count >= 2)
                {
                    int left = queue.Dequeue();
                    int right = queue.Dequeue();
                    _playerCodes.Remove(left);
                    _playerCodes.Remove(right);
                    CreateMatch(left, right, "battle");
                    return true;
                }
                return false;
            }
            Queue<int> targetQueue;
            string createdType;
            if (matchType == "training")
            {
                // 人机模式：直接创建 Bot 对局
                CreateBotMatch(playerId, deckId);
                return true;
            }
            else if (matchType == "brawl")
            {
                targetQueue = _brawlQueue;
                createdType = "brawl";
            }
            else if (matchType.Length == 0)
            {
                targetQueue = _rankedQueue;
                createdType = "battle";
            }
            else
            {
                targetQueue = _casualQueue;
                createdType = "classic";
            }
            targetQueue.Enqueue(playerId);
            if (targetQueue.Count >= 2)
            {
                int left = targetQueue.Dequeue();
                int right = targetQueue.Dequeue();
                CreateMatch(left, right, createdType);
                return true;
            }
            return false;
        }
    }

    public bool RemoveFromQueues(int playerId)
    {
        lock (_lock)
        {
            bool removed = RemoveFrom(_rankedQueue, playerId) | RemoveFrom(_casualQueue, playerId)
                | RemoveFrom(_brawlQueue, playerId) | RemoveFrom(_diyQueue, playerId);
            if (_playerCodes.Remove(playerId, out string? code))
            {
                if (_codeQueues.TryGetValue(code, out var queue))
                {
                    removed |= RemoveFrom(queue, playerId);
                    if (queue.Count == 0)
                        _codeQueues.Remove(code);
                }
            }
            _playerDecks.Remove(playerId);
            return removed;
        }
    }

    private static bool RemoveFrom(Queue<int> queue, int playerId)
    {
        // 用重建方式移除（保持顺序），与 Java ArrayDeque.remove 语义一致
        int count = queue.Count;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            int value = queue.Dequeue();
            if (value == playerId && !found)
                found = true;
            else
                queue.Enqueue(value);
        }
        return found;
    }

    public bool IsWaiting(int playerId) { lock (_lock) { return _playerDecks.ContainsKey(playerId) && !_playing.ContainsKey(playerId); } }

    public MatchState? MatchForPlayer(int playerId)
    {
        lock (_lock)
        {
            return _playing.TryGetValue(playerId, out int matchId) ? _matches.GetValueOrDefault(matchId) : null;
        }
    }

    public MatchState? MatchById(int matchId) { lock (_lock) { return _matches.GetValueOrDefault(matchId); } }

    public void AppendAction(MatchState match, string encryptedAction)
    {
        lock (_lock)
        {
            match.CurrentActionId++;
            match.Actions.Add(match.CurrentActionId);
            match.ActionsData[match.CurrentActionId] = encryptedAction;
        }
    }

    public JsonArray ActionsSince(MatchState match, int minActionId)
    {
        lock (_lock)
        {
            var output = new JsonArray();
            for (int i = 0; i < match.Actions.Count; i++)
            {
                int actionId = match.Actions[i]!.GetValue<int>();
                if (actionId >= minActionId && match.ActionsData.TryGetValue(actionId, out string? data))
                    output.Add(data);
            }
            return output;
        }
    }

    private void CreateMatch(int leftPlayer, int rightPlayer, string matchType)
    {
        int matchId = ++_nextMatchId;
        _playing[leftPlayer] = matchId;
        _playing[rightPlayer] = matchId;
        var match = new MatchState
        {
            MatchId = matchId,
            PlayerLeft = leftPlayer,
            PlayerRight = rightPlayer,
            ActionPlayerId = leftPlayer,
            DeckIdLeft = _playerDecks.GetValueOrDefault(leftPlayer),
            DeckIdRight = _playerDecks.GetValueOrDefault(rightPlayer),
            MatchType = matchType,
        };
        var leftDeck = _database.FindDeckById(match.DeckIdLeft);
        var rightDeck = _database.FindDeckById(match.DeckIdRight);
        match.LeftDeckData = _deckCodeManager.ParseDeckCode(leftDeck?.DeckCode ?? "");
        match.RightDeckData = _deckCodeManager.ParseDeckCode(rightDeck?.DeckCode ?? "");
        if (match.LeftDeckData["success"]?.GetValue<bool>() != true || match.RightDeckData["success"]?.GetValue<bool>() != true)
        {
            _playing.Remove(leftPlayer);
            _playing.Remove(rightPlayer);
            _playerDecks.Remove(leftPlayer);
            _playerDecks.Remove(rightPlayer);
            return;
        }
        match.LeftCardsData = _deckCodeManager.CreateMatchCards("left", match.LeftDeckData);
        match.RightCardsData = _deckCodeManager.CreateMatchCards("right", match.RightDeckData);
        ShuffleCards(match.LeftCardsData);
        ShuffleCards(match.RightCardsData);
        MarkHands(match);
        _matches[matchId] = match;
        _playerDecks.Remove(leftPlayer);
        _playerDecks.Remove(rightPlayer);
    }

    public void CleanupMatch(int matchId)
    {
        lock (_lock)
        {
            if (!_matches.Remove(matchId, out var match))
                return;
            CleanupPlayerAfterMatch(match.PlayerLeft);
            CleanupPlayerAfterMatch(match.PlayerRight);
        }
    }

    private void CleanupPlayerAfterMatch(int playerId)
    {
        _playing.Remove(playerId);
        _playerDecks.Remove(playerId);
        if (_playerCodes.Remove(playerId, out string? code))
        {
            if (_codeQueues.TryGetValue(code, out var queue))
            {
                RemoveFrom(queue, playerId);
                if (queue.Count == 0)
                    _codeQueues.Remove(code);
            }
        }
        RemoveFrom(_rankedQueue, playerId);
        RemoveFrom(_casualQueue, playerId);
        RemoveFrom(_brawlQueue, playerId);
        RemoveFrom(_diyQueue, playerId);
    }

    private static void ShuffleCards(JsonArray cards)
    {
        if (cards.Count <= 2)
            return;
        var hq = cards[0]!.DeepClone();
        var rest = new List<JsonNode>();
        for (int i = 1; i < cards.Count; i++)
            rest.Add(cards[i]!.DeepClone());
        // 洗牌（Fisher-Yates）
        var random = new Random();
        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        cards.Clear();
        cards.Add(hq);
        for (int i = 0; i < rest.Count; i++)
        {
            rest[i]["location_number"] = i;
            cards.Add(rest[i]);
        }
    }

    private static void MarkHands(MatchState match)
    {
        for (int i = 1; i < match.LeftCardsData.Count; i++)
        {
            // 注意：JsonNode 是树结构，挂到新数组必须先 DeepClone（Java 版允许引用共享）
            var card = match.LeftCardsData[i]!.DeepClone();
            if (i < 5)
            {
                card["location"] = "hand_left";
                match.LeftHandCards.Add(card);
            }
            else
            {
                match.LeftDeckCards.Add(card);
            }
        }
        for (int i = 1; i < match.RightCardsData.Count; i++)
        {
            var card = match.RightCardsData[i]!.DeepClone();
            if (i < 6)
            {
                card["location"] = "hand_right";
                match.RightHandCards.Add(card);
            }
            else
            {
                match.RightDeckCards.Add(card);
            }
        }
    }

    // ==================== Bot 匹配 ====================

    public void CreateBotMatch(int playerId, int deckId)
    {
        lock (_lock)
        {
            // 已有对局则复用
            if (_playing.TryGetValue(playerId, out int existingMatchId))
            {
                var existing = _matches.GetValueOrDefault(existingMatchId);
                if (existing is not null && existing.BotEnabled)
                {
                    PrepareBotMulligan(existing);
                    return;
                }
            }

            var playerDeck = _database.FindDeckById(deckId)
                ?? throw new InvalidOperationException("deck not found");
            int matchId = ++_nextMatchId;
            _playing[playerId] = matchId;

            var match = new MatchState
            {
                MatchId = matchId,
                PlayerLeft = playerId,
                PlayerRight = MatchState.BotPlayerId,
                DeckIdLeft = deckId,
                DeckIdRight = deckId,
                MatchType = "training",
                Status = "pending",
            };

            match.LeftDeckData = _deckCodeManager.ParseDeckCode(playerDeck.DeckCode);
            match.RightDeckData = _deckCodeManager.ParseDeckCode(playerDeck.DeckCode);
            if (match.LeftDeckData["success"]?.GetValue<bool>() != true || match.RightDeckData["success"]?.GetValue<bool>() != true)
            {
                ServerEvents.Log($"Bot 对局创建失败：卡组 {deckId} 解析失败（deck_code='{playerDeck.DeckCode}'）");
                _playing.Remove(playerId);
                return;
            }
            match.LeftCardsData = _deckCodeManager.CreateMatchCards("left", match.LeftDeckData);
            match.RightCardsData = _deckCodeManager.CreateMatchCards("right", match.RightDeckData);
            ShuffleCards(match.LeftCardsData);
            ShuffleCards(match.RightCardsData);

            // 分牌：玩家 4 张手牌，Bot 5 张手牌（DeepClone 避免节点多父异常）
            for (int i = 1; i < match.LeftCardsData.Count; i++)
            {
                var card = match.LeftCardsData[i]!.DeepClone();
                if (i < 5)
                {
                    card["location"] = "hand_left";
                    match.LeftHandCards.Add(card);
                }
                else
                {
                    card["location"] = "deck_left";
                    match.LeftDeckCards.Add(card);
                }
            }
            for (int i = 1; i < match.RightCardsData.Count; i++)
            {
                var card = match.RightCardsData[i]!.DeepClone();
                if (i < 6)
                {
                    card["location"] = "hand_right";
                    match.RightHandCards.Add(card);
                }
                else
                {
                    card["location"] = "deck_right";
                    match.RightDeckCards.Add(card);
                }
            }

            match.BotEnabled = true;
            match.BotSide = "right";
            match.PlayerStatusRight = "mulligan_done";
            match.LevelLoadedRight = 1;
            match.RightOnline = true;

            PrepareBotMulligan(match);

            _matches[matchId] = match;
            _playerDecks.Remove(playerId);
        }
    }

    /// <summary>
    /// Bot 回合 Tick — 在 poll/get 时调用。
    /// Bot 仅做：开始回合 → 结束回合 → 回合+1，延迟 BotTurnDelayMs。
    /// </summary>
    public void TickBot(MatchState? match)
    {
        if (match is null || !match.BotEnabled) return;
        lock (_lock)
        {
            if (match.Status != "running" || match.ActionSessionId == 0) return;
            if (match.CurrentTurn % 2 != 0) return; // 玩家回合，Bot 不动作
            if (match.BotLastEndedTurn == match.CurrentTurn) return;

            if (match.BotPendingTurn != match.CurrentTurn)
            {
                match.BotPendingTurn = match.CurrentTurn;
                match.BotTurnReadyAt = Environment.TickCount64 + MatchState.BotTurnDelayMs;
                return;
            }
            if (Environment.TickCount64 < match.BotTurnReadyAt) return;

            try
            {
                if (EnsureBotService() is { } bot)
                {
                    var snapshot = BuildBotSnapshot(match);
                    var result = bot.DecideTurn(snapshot);
                    string hqKey = FindHqKey(snapshot);
                    var startData = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["side"] = "right",
                    };
                    if (result.HqOpponentBefore > 0)
                        startData[hqKey] = result.HqOpponentBefore.ToString();
                    AppendBotAction(match, new ServerAction(match.CurrentActionId + 1,
                        "XActionStartOfTurn", match.PlayerRight, startData, result.State?.Turn ?? match.CurrentTurn));
                    foreach (var action in result.Actions)
                        AppendBotAction(match, action);
                }
                else
                {
                    AppendLegacyBotTurn(match);
                }

                match.CurrentTurn++;
            }
            catch (Exception ex)
            {
                _botLoadError ??= ex.ToString();
                ServerEvents.Log("Bot 回合失败，退回仅结束回合：" + ex.Message);
                AppendLegacyBotTurn(match);
                match.CurrentTurn++;
            }

            match.BotLastEndedTurn = match.CurrentTurn - 1;
            match.BotPendingTurn = 0;
            match.BotTurnReadyAt = 0;
        }
    }

    private void AppendBotAction(MatchState match, ServerAction action)
    {
        match.CurrentActionId++;
        var fullAction = new JsonObject
        {
            ["action_id"] = match.CurrentActionId,
            ["action_type"] = action.ActionType,
            ["player_id"] = action.PlayerId,
            ["action_data"] = new JsonObject(action.ActionData.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
            ["sub_actions"] = new JsonArray(),
            ["turn_number"] = action.TurnNumber,
        };
        match.Actions.Add(match.CurrentActionId);

        string encrypted = _actionCipher.Encode(match.ActionSessionId, fullAction);
        match.ActionsData[match.CurrentActionId] = encrypted;
    }

    private void AppendLegacyBotTurn(MatchState match)
    {
        AppendBotAction(match, new ServerAction(match.CurrentActionId + 1, "XActionStartOfTurn",
            match.PlayerRight, new Dictionary<string, string> { ["side"] = "right" }, match.CurrentTurn));
        AppendBotAction(match, new ServerAction(match.CurrentActionId + 1, "XActionEndOfTurn",
            match.PlayerRight, new Dictionary<string, string> { ["reason"] = "endTurnButton", ["side"] = "right" }, match.CurrentTurn));
    }

    private BotTurnService? EnsureBotService()
    {
        if (_botService is not null) return _botService;
        if (_botLoadError is not null) return null;
        try
        {
            string dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            var db = CardDatabase.Load(dataDir);
            KismetLibrary.Initialize(Path.Combine(dataDir, "card-ir.json"));
            var service = new BotTurnService(db, Side.Right, MatchState.BotPlayerId);
            string codesPath = Path.Combine(dataDir, "deck_code_ids.json");
            var codes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(codesPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(codesPath)) as JsonObject;
                if (node is not null)
                    foreach (var (code, value) in node)
                    {
                        string? name = value switch
                        {
                            JsonValue v when v.TryGetValue<string>(out var s) => s,
                            JsonObject o => o["card"]?.GetValue<string>() ?? o["name"]?.GetValue<string>(),
                            _ => null,
                        };
                        if (!string.IsNullOrEmpty(name)) codes[code] = name;
                    }
            }
            service.LoadDeckCodeTable(codes);
            return _botService = service;
        }
        catch (Exception ex)
        {
            _botLoadError = ex.Message;
            ServerEvents.Log("Bot 内核加载失败：" + ex.Message);
            return null;
        }
    }

    private ServerMatchSnapshot BuildBotSnapshot(MatchState match)
    {
        var cards = new Dictionary<int, ServerCard>();
        AddCards(cards, match.LeftCardsData);
        AddCards(cards, match.RightCardsData);
        AddCards(cards, match.LeftHandCards);
        AddCards(cards, match.RightHandCards);
        AddCards(cards, match.LeftDeckCards);
        AddCards(cards, match.RightDeckCards);
        // Mulligan 弃牌不会进入动作流；必须把它们以 Discard 位置覆盖
        // 初始卡池中的同一张卡，否则 ReplayRunner 会把弃掉的起手牌再次当作牌库卡。
        AddDiscardedCards(cards, match.LeftDiscardedCards, Side.Left);
        AddDiscardedCards(cards, match.RightDiscardedCards, Side.Right);

        var actions = new List<ServerAction>();
        foreach (var idNode in match.Actions)
        {
            int id = idNode?.GetValue<int>() ?? 0;
            if (id == 0 || !match.ActionsData.TryGetValue(id, out var packet)) continue;
            try
            {
                var payload = _actionCipher.Decode(packet).Payload;
                var wire = WireAction.Parse(payload);
                actions.Add(new ServerAction(wire.ActionId == 0 ? id : wire.ActionId,
                    wire.ActionType, wire.PlayerId, wire.ActionData, wire.TurnNumber));
            }
            catch (Exception ex)
            {
                ServerEvents.Log($"Bot 快照跳过无法解密动作 #{id}：{ex.Message}");
            }
        }

        return new ServerMatchSnapshot(match.MatchId, match.CurrentTurn, match.PlayerLeft,
            match.PlayerRight, cards.Values.ToArray(), actions)
        {
            NextActionId = match.CurrentActionId + 1,
            SendActionId = match.CurrentActionId,
        };
    }

    private static void AddCards(Dictionary<int, ServerCard> cards, JsonArray array)
    {
        foreach (var node in array)
        {
            if (node is not JsonObject c) continue;
            int id = c["card_id"]?.GetValue<int>() ?? 0;
            if (id <= 0) continue;
            cards[id] = new ServerCard(id, c["is_gold"]?.GetValue<bool>() ?? false,
                c["location"]?.GetValue<string>() ?? "", c["location_number"]?.GetValue<int>() ?? 0,
                c["name"]?.GetValue<string>() ?? "");
        }
    }

    private static void AddDiscardedCards(Dictionary<int, ServerCard> cards, JsonArray array, Side side)
    {
        string location = side == Side.Left ? "discard_left" : "discard_right";
        foreach (var node in array)
        {
            if (node is not JsonObject card)
            {
                continue;
            }

            int id = card["card_id"]?.GetValue<int>() ?? 0;
            if (id <= 0)
            {
                continue;
            }

            cards[id] = new ServerCard(id,
                card["is_gold"]?.GetValue<bool>() ?? false,
                location,
                card["location_number"]?.GetValue<int>() ?? 0,
                card["name"]?.GetValue<string>() ?? "");
        }
    }

    private static string FindHqKey(ServerMatchSnapshot snapshot)
    {
        foreach (var action in snapshot.Actions)
            foreach (var key in action.ActionData.Keys)
                if (key.Length > 0 && key.All(char.IsAsciiDigit) && key is not ("0" or "1" or "2" or "3" or "4"))
                    return key;
        return "40";
    }

    private void PrepareBotMulligan(MatchState match)
    {
        if (match.RightHandCards.Count == 0 || match.RightDeckCards.Count == 0)
        {
            match.PlayerStatusRight = "mulligan_done";
            return;
        }
        match.RightReplacementCards = new JsonArray();
        int replacements = Math.Min(2, match.RightHandCards.Count);
        var random = new Random();
        for (int i = 0; i < replacements; i++)
        {
            if (match.RightDeckCards.Count == 0) break;
            try
            {
                int deckIdx = random.Next(match.RightDeckCards.Count);
                var oldCard = match.RightHandCards[i]!;
                var newCard = match.RightDeckCards[deckIdx]!;
                oldCard["location"] = "deck_right";
                newCard["location"] = "hand_right";
                int tmpNum = oldCard["location_number"]?.GetValue<int>() ?? 0;
                oldCard["location_number"] = newCard["location_number"]?.GetValue<int>() ?? 0;
                newCard["location_number"] = tmpNum;
                match.RightHandCards[i] = newCard;
                match.RightDeckCards[deckIdx] = oldCard;
                match.RightReplacementCards.Add(newCard.DeepClone());
            }
            catch
            {
                // 忽略单张换牌失败
            }
        }
        ShuffleCards(match.RightDeckCards);
        match.PlayerStatusRight = "mulligan_done";
    }
}
