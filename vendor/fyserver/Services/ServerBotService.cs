using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.NN;
using KLink.Bot.Server;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// **把 fyserver 的对局接到规则内核上** —— 这一层只做「翻译」，
/// 决策全在 <see cref="BotTurnService"/> 里。
///
/// <code>
///   MatchInfo ──ToSnapshot──▶ ServerMatchSnapshot ──▶ BotTurnService.DecideTurn
///                                                        │
///                                    IReadOnlyList&lt;ServerAction&gt; ◀──┘
///                                                        │
///                                       ToMatchActions ──▶ MatchAction[]（append 进队列）
/// </code>
///
/// ## 为什么服务端能算出局面
///
/// 服务端**没有棋盘状态**，也不需要：游戏是确定性锁步，效果由双方各自本地结算。
/// 「双方牌库 + 完整动作流」= 全部信息，重放一遍就得到与客户端一致的局面。
///
/// ## 单例 + 懒加载
///
/// `CardDatabase` / 蓝图 IR / 神经网络模型都只加载一次（它们很大：
/// `card-ir.json` 8.7 MB、`cards.json` 1 MB）。所以本类是单例，
/// 第一次真正要用 bot 时才加载 —— **不让 fyserver 的启动变慢**，
/// 也不影响「没开 bot 的部署」。
/// </summary>
public sealed class ServerBotService
{
    private readonly ILogger<ServerBotService> _log;
    private readonly IWebHostEnvironment _env;
    private readonly ServerOptions _options;
    private readonly object _initLock = new();
    private readonly object _decisionLock = new();

    private bool _tried;
    private string? _loadError;
    private CardDatabase? _db;
    private BotTurnService? _service;

    public ServerBotService(ILogger<ServerBotService> log, IWebHostEnvironment env, ServerOptions options)
    {
        _log = log;
        _env = env;
        _options = options;
    }

    /// <summary>
    /// **对手（人类）HQ 的血量** —— 上一次决策重建出来的真实值。
    ///
    /// 为什么要有它：`MatchEndpoints` 给 bot 的回合边界动作
    /// （`XActionStartOfTurn` / `XActionEndOfTurn`）要填一个 HQ 血量，
    /// 而**服务端没有棋盘状态**。旧实现写死 `"20"` ——
    /// 实测（`out/audit` 的 HQ 对拍）一局里有 **22 条动作**的 HQ 值对不上，
    /// 其中多数就是它：人类 HQ 涨到 38 了，我们还告诉客户端 "20"。
    ///
    /// 初值 20 是安全的：bot 第一次行动时人类 HQ 必然是 20（开局值）。
    /// </summary>
    public int LastOpponentHq { get; private set; } = 20;

    /// <summary>bot 自己那一方 HQ 的血量（同样来自上一次重建）。</summary>
    public int LastBotHq { get; private set; } = 20;

    // ==================== 漂开闸门 ====================
    //
    // 为什么要有：内核有保真度缺口（未实现的原语、写错的规则）⇒ 重建出来的局面会
    // **逐渐**与真人客户端漂开。漂开之后继续下棋 = 在错误局面上做非法操作，
    // 客户端表现成「卡牌悬空」「AI 移动虚空单位」。
    //
    // 修保真度是长期活，**先保证漂开时别再把对局搞坏**：
    //   · 拒绝下棋 —— `DecideBotTurn` 返回空列表（`MatchEndpoints` 仍会照常发
    //     回合边界动作，所以对局不会卡死，只是 bot 跳过这一回合）
    //   · 大声记日志（`ILogger.LogError` + `bot-log`）
    //   · 在 `/spectate/bot` 暴露（见 `MatchHistoryEndpoints`）

    /// <summary>
    /// 一次决策的漂开判定（**按对局**记，见 <see cref="InfoFor"/>）。
    ///
    /// 为什么按对局而不是只留"最近一次"：`ServerBotService` 是单例，同时可能有好几局
    /// 在跑。`LastOpponentHq` 那种全局值会被别的对局覆盖 —— 而它要拿去填**本局**的
    /// 回合边界动作，所以必须能按 matchId 取回来。
    /// </summary>
    public sealed record DivergenceInfo(
        int MatchId,
        /// <summary>是否已判定漂开（= 这一回合有没有拒绝下棋）。</summary>
        bool Detected,
        /// <summary>一句话原因（没漂开时是空串）。</summary>
        string Reason,
        /// <summary>人类动作里内核应用不了的条数。</summary>
        int UnappliedHumanActions,
        /// <summary>人类动作里 HQ 校验和对不上的条数。</summary>
        int HqMismatches,
        /// <summary>第一条 HQ 对不上的动作里，**我们算出来**的对手 HQ。</summary>
        int HqOur,
        /// <summary>同一条动作里，**客户端写的**值。</summary>
        int HqClient,
        /// <summary>第一条被拒的人类动作（可读）。</summary>
        string? FirstUnapplied,
        /// <summary>第一条 HQ 对不上的动作（可读）。</summary>
        string? FirstHqMismatch,
        /// <summary>决策**前**对手 HQ（填 `XActionStartOfTurn`）。</summary>
        int HqOpponentBefore,
        /// <summary>决策**后**对手 HQ（填 `XActionEndOfTurn`）。</summary>
        int HqOpponentAfter,
        /// <summary>本回合真的产出了几条操作（被闸门拦下时是 0）。</summary>
        int ProducedActions,
        DateTime AtUtc);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DivergenceInfo> _divergence = new();
    private volatile DivergenceInfo? _lastDivergence;

    /// <summary>最近一次决策的漂开判定（给 `/spectate/bot`）。</summary>
    public DivergenceInfo? LastDivergence => _lastDivergence;

    /// <summary>按对局取漂开判定（`/spectate/bot` 与边界动作取值用）。</summary>
    public DivergenceInfo? InfoFor(int matchId)
        => _divergence.TryGetValue(matchId, out var v) ? v : null;

    /// <summary>所有对局的漂开判定（给 `/spectate/bot`）。</summary>
    public IReadOnlyDictionary<int, DivergenceInfo> DivergenceByMatch => _divergence;

    /// <summary>
    /// bot 回合边界动作要填的「对手（人类）HQ」—— **决策前 / 决策后各一个**。
    ///
    /// 为什么要两个时刻：动作里那个字段是**采样当时**的值，不是回合内的常量。
    /// 实测（214436）：人类自己的 `XActionStartOfTurn` 写 22、同回合 `XActionEndOfTurn`
    /// 写 20 —— 因为人类在这回合把 bot HQ 打掉了 2。所以：
    ///   · `XActionStartOfTurn` 要用「bot 还没动手时」的值（决策**前**）
    ///   · `XActionEndOfTurn` 要用「bot 打完」的值（决策**后**，bot 本回合可能攻击过人类 HQ）
    ///
    /// ⚠️ 旧实现两个都用 `LastOpponentHq`，那是**上一次**决策后的值 —— 过期了整整一个
    /// 人机来回（人类自己的回合也会改变自己的 HQ：疲劳、治疗）。
    /// 实测 542091：#65（StartOfTurn）写 19、当时其实是 21；#49（EndOfTurn）写 20、
    /// 当时其实是 19（bot 在那一回合把人类 HQ 打掉了 1）。
    ///
    /// 没有记录时退回 <see cref="LastOpponentHq"/>（= 旧行为，保证不会更差）。
    /// </summary>
    public (int Before, int After) HqFor(int matchId)
        => _divergence.TryGetValue(matchId, out var v) && v.HqOpponentBefore >= 0
            ? (v.HqOpponentBefore, v.HqOpponentAfter)
            : (20, 20);

    /// <summary>内核是否可用（加载失败时为 false，此时不产动作、只走原来那套空回合）。</summary>
    public bool IsReady
    {
        get { EnsureLoaded(); return _service is not null; }
    }

    public string? LoadError
    {
        get { EnsureLoaded(); return _loadError; }
    }

    /// <summary>策略来源说明（给后台/日志看）。</summary>
    public string PolicyDescription
    {
        get
        {
            EnsureLoaded();
            if (_service is null) return "未加载";
            return _service.HasNeuralPolicy ? "神经网络" : "贪心兜底（未找到模型）";
        }
    }

    private void EnsureLoaded()
    {
        if (_tried) return;
        lock (_initLock)
        {
            if (_tried) return;
            try
            {
                Load();
            }
            catch (Exception ex)
            {
                _loadError = $"{ex.GetType().Name}: {ex.Message}";
                _log.LogError(ex, "bot 内核加载失败，本局将不产出 bot 动作");
                _db = null;
                _service = null;
            }
            finally
            {
                _tried = true;
            }
        }
    }

    private void Load()
    {
        string dataDir = Path.Combine(_env.ContentRootPath, "BotData");
        if (!Directory.Exists(dataDir))
        {
            dataDir = Path.Combine(AppContext.BaseDirectory, "BotData");
        }

        _db = CardDatabase.Load(dataDir);

        // 蓝图 IR：**必须加载** —— 否则卡牌效果不执行，与训练/对拍不同分布。
        string irPath = Path.Combine(dataDir, "card-ir.json");
        if (!File.Exists(irPath))
        {
            throw new FileNotFoundException("Bot 规则数据缺少 card-ir.json", irPath);
        }
        KismetLibrary.Initialize(irPath);
        if (KismetLibrary.Default is null)
            throw new InvalidDataException($"Bot 规则数据无法加载：{KismetLibrary.LoadError}");

        _log.LogInformation("bot 卡库 {Count} 张，IR {Ir}", _db.Count,
                            KismetLibrary.Default is null ? "未加载" : $"{KismetLibrary.Default.CardCount} 张可解释");

        // 神经网络（可选）：找不到模型就退回贪心，**不阻止接线**
        NnModel? model = null;
        StateEncoder.CardVecs? vecs = null;
        string? modelPath = ResolveModelPath();
        if (modelPath is not null && File.Exists(Path.Combine(dataDir, "card-vectors.json")))
        {
            try
            {
                model = NnModel.Load(modelPath);
                vecs = StateEncoder.LoadCardVectors(dataDir);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "神经网络模型加载失败（{Path}），改用贪心兜底", modelPath);
                model = null;
                vecs = null;
            }
        }

        // bot 坐哪边由调用方在 DecideTurn 时给，但 side/playerId 是构造参数 ——
        // 这里先按「训练局 = 右侧」建，`DecideTurn` 里会按实际对局核对。
        _service = new BotTurnService(_db, Side.Right, User.BotPlayerId, model, vecs);
        var codes = DeckCodeTable().ToList();
        _deckCodeCount = codes.Count;
        _service.LoadDeckCodeTable(codes);
        _policyReady = _service.HasNeuralPolicy;
    }

    // ==================== 诊断（挂给 /spectate/bot） ====================
    //
    // 为什么要有：`ServerBotService` 的失败**都是静默的**（只写日志），
    // 而「bot 只会跳过回合」这个现象有**至少四种**互不相同的成因：
    //   ① 内核没加载（数据/模型缺失）
    //   ② `MatchStartingInfo` 还没生成 → 拿不到双方牌库 → 干脆不决策
    //   ③ 重建出来的局面不对（牌库空 / 行动方不是 bot）
    //   ④ 决策真没得做
    // 光看「只跳过」分不出来。这里把每次决策的关键数字留下来，一条 HTTP 请求就能分辨。

    private int _deckCodeCount;
    private bool _policyReady;

    /// <summary>反向索引里有多少条。</summary>
    public int DeckCodeCount => _deckCodeCount;

    /// <summary>是否真的用上了神经网络（false = 贪心兜底）。</summary>
    public bool PolicyReady => _policyReady;

    /// <summary>最近一次决策的摘要。</summary>
    public sealed record LastDecision(
        int MatchId,
        string Stage,
        string Detail,
        int CardsInSnapshot,
        int ActionsInSnapshot,
        int BotHandCount,
        int ProducedActions,
        DateTime AtUtc);

    private volatile LastDecision? _last;

    public LastDecision? Last => _last;

    /// <summary>决策阶段累计计数（按 Stage 分桶）——能看出是「从没走到」还是「走到了但没产出」。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _stages = new();

    public IReadOnlyDictionary<string, int> Stages => _stages;

    private void Note(string stage, string detail, ServerMatchSnapshot? snap, int handCount, int produced, int matchId)
    {
        _stages.AddOrUpdate(stage, 1, (_, v) => v + 1);
        _last = new LastDecision(matchId, stage, detail,
                                 snap?.Cards.Count ?? 0,
                                 snap?.Actions.Count ?? 0,
                                 handCount, produced, DateTime.UtcNow);
        Trace($"NOTE stage={stage} match={matchId} hand={handCount} produced={produced} :: {detail}");
    }

    // ==================== 落盘诊断 ====================
    //
    // 为什么需要：本地模式下对局记录在 `FasterKv`（`match:document:`），
    // **服务端一停就读不到了**（实测：重启后 `/replays` 返回空）。
    // 而接服务器阶段最需要的就是「那一局到底发生了什么」。
    //
    // 所以 bot 自己做一份**追加式**、**进程退出后仍在**的日志。
    // 它比回放更有用：回放只给结果，这里给的是**决策当时的候选与选择**。
    //
    // 位置：`<内容根>/bot-log/bot-<日期>.log`

    private string? _tracePath;

    private void Trace(string line)
    {
        if (_tracePath is null)
        {
            try
            {
                string dir = Path.Combine(_env.ContentRootPath, "bot-log");
                Directory.CreateDirectory(dir);
                _tracePath = Path.Combine(dir, $"bot-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
                return;     // 落盘失败不影响对局
            }
        }

        try
        {
            File.AppendAllText(_tracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志写不进去不该影响对局
        }
    }

    private string? ResolveModelPath()
    {
        // Resolve only explicit or deployed models, using the same roots as BotData.
        string dataDir = Path.Combine(_env.ContentRootPath, "BotData");
        foreach (string? candidate in new[]
                 {
                     _options.BotModelPath,
                      Path.Combine(dataDir, "nn-model.bin"),
                      Path.Combine(AppContext.BaseDirectory, "BotData", "nn-model.bin"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// 卡组码表：码 → 卡名（动作的槽 4 要填这张牌的码）。
    ///
    /// 两个来源形状不同，都要认：
    ///   · `BotData/deck_code_ids.json`（= `klink bot/docs/deck_code_ids.live.json`，2499 条）
    ///     → `{ "gt": "card_xxx", … }`  **dict**
    ///   · `library/deckCodeIDsTable2.json`（fyserver 自己的那份，2516 条）
    ///     → `[ { "ID":…, "card":"card_xxx", "deck_code_id":"gt" }, … ]`  **list**
    ///
    /// ⚠️ 优先用 BotData 那份 —— 它是**内核正在用的同一份**，
    /// 而 fyserver 那份可能旧（缺 E*/D* 新卡）。两者不一致时会让槽 4 填出
    /// 客户端不认识的码。
    /// </summary>
    private IEnumerable<KeyValuePair<string, string>> DeckCodeTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string path in new[]
                 {
                     Path.Combine(_env.ContentRootPath, "BotData", "deck_code_ids.json"),
                     Path.Combine(AppContext.BaseDirectory, "BotData", "deck_code_ids.json"),
                     Path.Combine(_env.ContentRootPath, "library", "deckCodeIDsTable2.json"),
                     Path.Combine(AppContext.BaseDirectory, "library", "deckCodeIDsTable2.json"),
                 })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                switch (doc.RootElement.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            string? name = prop.Value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                                System.Text.Json.JsonValueKind.Object => GetStr(prop.Value, "card", "Card"),
                                _ => null,
                            };
                            if (!string.IsNullOrEmpty(name))
                            {
                                table[prop.Name] = name;
                            }
                        }

                        break;

                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            string? code = GetStr(item, "deck_code_id", "deckCodeId", "DeckCodeId");
                            string? name = GetStr(item, "card", "Card");
                            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name))
                            {
                                table[code] = name;
                            }
                        }

                        break;
                }

                if (table.Count > 0)
                {
                    _log.LogInformation("bot 卡组码表 {Count} 条（{Path}）", table.Count, path);
                    break;      // 第一个成功的源就够
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "卡组码表读取失败：{Path}", path);
            }
        }

        if (table.Count == 0)
        {
            _log.LogWarning("没有可用的卡组码表，动作的槽 4 会填 0");
        }

        return table;
    }

    private static string? GetStr(System.Text.Json.JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (string n in names)
        {
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }

    // ==================== 对局接口 ====================

    /// <summary>
    /// 该不该由 bot 接管这一方的回合。
    ///
    /// 判据只有两条，都来自现有实现：**训练局（`Ex=="pw"`）** 且 **右侧是 bot**。
    /// </summary>
    public static bool ShouldDriveBot(MatchInfo match)
        => string.Equals(match.Ex, "pw", StringComparison.Ordinal)
           && match.Right?.PlayerId == User.BotPlayerId;

    /// <summary>
    /// 让 bot 下一整个回合，返回要 append 进 <c>match.MatchActions</c> 的动作。
    ///
    /// ⚠️ **调用方必须已经持有 `match.SyncRoot`** —— 本方法读 `MatchStartingInfo`
    /// 与 `MatchActions`，不自己加锁（避免与端点里那段锁嵌套）。
    /// </summary>
    public IReadOnlyList<MatchAction> DecideBotTurn(MatchInfo match, MatchAction triggeringAction)
    {
        EnsureLoaded();
        if (_service is null)
        {
            _log.LogWarning("bot 内核不可用（{Error}），本回合只发空回合", _loadError);
            Note("kernel-unavailable", _loadError ?? "未知", null, 0, 0, match.MatchId);
            return Array.Empty<MatchAction>();
        }

        var snapshot = ToSnapshot(match);
        if (snapshot is null)
        {
            // 这一步最常见的原因是 `MatchStartingInfo` 还没生成（拿不到双方牌库）。
            string why = match.MatchStartingInfo is null
                ? "MatchStartingInfo 为空（开局信息还没生成）"
                : "StartingData 为空";
            _log.LogWarning("bot[{Match}]: {Why} —— 无法重建局面", match.MatchId, why);
            Note("no-starting-info", why, null, 0, 0, match.MatchId);
            return Array.Empty<MatchAction>();
        }

        BotTurnService.TurnResult result;
        try
        {
            lock (_decisionLock)
            {
                result = _service.DecideTurn(snapshot);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot 决策抛异常，本回合只发空回合");
            Note("exception", $"{ex.GetType().Name}: {ex.Message}", snapshot, -1, 0, match.MatchId);
            return Array.Empty<MatchAction>();
        }

        foreach (string line in result.Log)
        {
            _log.LogInformation("bot[{Match}]: {Line}", match.MatchId, line);
            Trace($"DECIDE match={match.MatchId} :: {line}");
        }

        if (result.UnappliedActions > 0)
        {
            _log.LogWarning("bot[{Match}]: 局面重建有 {N} 条动作未应用（含 bot 自己的历史动作）",
                            match.MatchId, result.UnappliedActions);
        }

        if (result.State is null)
        {
            Note("no-state", "重放没产出引擎", snapshot, -1, 0, match.MatchId);
            return Array.Empty<MatchAction>();
        }

        int botSide = match.Right?.PlayerId == User.BotPlayerId ? 2 : 1;
        int hand = botSide == 2 ? result.State.Hand(Side.Right).Count : result.State.Hand(Side.Left).Count;

        // ★ 记下**真实 HQ 血量**，供 `MatchEndpoints` 填回合边界动作。
        //   旧实现写死 "20"，实测一局有 22 条动作的 HQ 值对不上。
        //   ⚠️ 决策前 / 决策后**各一个**：`result.State` 是决策后的局面，
        //      而 `XActionStartOfTurn` 要的是「bot 还没动手时」的值（见 HqFor 的注释）。
        int hqOpponentBefore = result.HqOpponentBefore >= 0 ? result.HqOpponentBefore : LastOpponentHq;
        int hqOpponentAfter = result.State.HqDefense(Side.Left);
        LastBotHq = result.State.HqDefense(Side.Right);
        LastOpponentHq = hqOpponentAfter;

        // ★★ 漂开闸门（见类顶部「漂开闸门」）—— 任一信号触发就**拒绝下棋**。
        var signals = result.Divergence ?? BotTurnService.DivergenceSignals.None;
        _divergence[match.MatchId] = new DivergenceInfo(
            match.MatchId,
            signals.Detected,
            signals.Reason,
            signals.UnappliedHumanActions,
            signals.HqMismatches,
            signals.HqOur,
            signals.HqClient,
            signals.FirstUnapplied,
            signals.FirstHqMismatch,
            hqOpponentBefore,
            hqOpponentAfter,
            signals.Detected ? 0 : result.Actions.Count,
            DateTime.UtcNow);
        _lastDivergence = _divergence[match.MatchId];

        if (signals.Detected)
        {
            _log.LogError(
                "bot[{Match}]: ⚠ 判定已与客户端漂开，**本回合拒绝下棋**（只发回合边界动作）。原因：{Reason}。" +
                "建议：① GET /spectate/bot 看 divergenceReason / hqOur / hqClient / firstUnapplied；" +
                "② 看 bot-log 里这条 DIVERGED；③ 对照 `out/audit` 的「人类失败」与「HQ 对不上」，" +
                "从第一条失败动作**往回**查是哪条规则没实现对。" +
                "（漂开是不可逆的：本局此后每回合都会重新判定并继续拒绝下棋。）",
                match.MatchId, signals.Reason);
            Trace($"DIVERGED match={match.MatchId} 拒绝下棋 :: {signals.Reason}");
            Note("divergence", signals.Reason, snapshot, hand, 0, match.MatchId);
            return Array.Empty<MatchAction>();
        }

        // 只产出「结束回合」= 决策没找到可做的动作，把现场数字留下来
        Note(result.Actions.Count <= 1 ? "no-action" : "ok",
             $"行动方={result.State.ActiveSide.ToWire()} 回合={result.State.Turn} " +
             $"手牌={hand} 未应用={result.UnappliedActions}",
             snapshot, hand, result.Actions.Count, match.MatchId);

        return result.Actions
            .Select(a => new MatchAction(
                ActionId: a.ActionId,
                ActionType: a.ActionType,
                PlayerId: a.PlayerId,
                ActionData: a.ActionData.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                sub_actions: [],
                turn_number: a.TurnNumber,
                SendActionId: triggeringAction.SendActionId))
            .ToArray();
    }

    /// <summary>
    /// `MatchInfo` → 内核要的中立快照。
    ///
    /// ⚠️ 卡**只取开局那批**（`LeftDeck/LeftHand/RightDeck/RightHand` + HQ）——
    /// 局中生成卡的归属由动作流里的 `location` 决定，内核重放时会自己摆。
    /// 这里多放反而会把位置弄错（它们已经不在开局位置了）。
    /// </summary>
    private ServerMatchSnapshot? ToSnapshot(MatchInfo match)
    {
        var starting = match.MatchStartingInfo?.MatchAndStartingData?.StartingData;
        if (starting is null)
        {
            _log.LogWarning("bot[{Match}]: 没有 MatchStartingInfo，无法重建局面", match.MatchId);
            return null;
        }

        var cards = new Dictionary<int, ServerCard>();

        void Add(IEnumerable<MatchCard>? list, string loc)
        {
            if (list is null) return;
            foreach (var c in list)
            {
                cards[c.CardId] = new ServerCard(c.CardId, c.IsGold, loc, c.LocationNumber, c.Name);
            }
        }

        void AddLocation(MatchLocation? loc, string wire)
        {
            if (loc is null) return;
            cards[loc.CardId] = new ServerCard(loc.CardId, loc.IsGold, wire, loc.LocationNumber, loc.Name);
        }

        AddLocation(starting.LocationCardLeft, "board_hqleft");
        AddLocation(starting.LocationCardRight, "board_hqright");
        Add(starting.StartingHandLeft, "hand_left");
        Add(starting.StartingHandRight, "hand_right");
        Add(starting.DeckLeft, "deck_left");
        Add(starting.DeckRight, "deck_right");

        // Mulligan returns cards to the deck; its post-swap cards override the opening layout.
        Add(match.LeftHand, "hand_left");
        Add(match.RightHand, "hand_right");
        Add(match.MulliganLeft?.Deck ?? match.LeftDeck, "deck_left");
        Add(match.MulliganRight?.Deck ?? match.RightDeck, "deck_right");
        Add(match.MulliganLeft?.ReplacementCards, "hand_left");
        Add(match.MulliganRight?.ReplacementCards, "hand_right");

        // 动作流：内核要的是「动作类型 + 行动方 + action_data 字符串字典」。
        // 注意**跳过 bot 自己刚产出的那些**由调用方保证 —— 这里按 `MatchActions` 原样给，
        // 因为 bot 的动作也已经 append 进去了（下一回合重建时需要它）。
        var actions = new List<ServerAction>(match.MatchActions.Count);
        foreach (var a in match.MatchActions)
        {
            if (string.IsNullOrEmpty(a.ActionType))
            {
                continue;
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            if (a.ActionData is not null)
            {
                foreach (var (k, v) in a.ActionData)
                {
                    data[k] = v?.ToString() ?? "";
                }
            }

            actions.Add(new ServerAction(a.ActionId, a.ActionType, a.PlayerId ?? 0, data, a.turn_number));
        }

        return new ServerMatchSnapshot(
            match.MatchId,
            match.Turns,
            match.Left?.PlayerId ?? 0,
            match.Right?.PlayerId ?? 0,
            cards.Values.ToArray(),
            actions)
        {
            NextActionId = match.currentActionId,
            SendActionId = match.MatchActions.LastOrDefault()?.SendActionId ?? 0,
            ActionSessionId = match.MatchActions.LastOrDefault()?.SendActionId ?? 0,
        };
    }
}
