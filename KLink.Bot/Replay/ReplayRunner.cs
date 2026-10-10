using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Replay;

/// <summary>
/// 真实对局重放器 —— 内核验证的主入口。
///
/// 做的事：用开局快照还原卡池，按真实动作流驱动内核，**每一步都比对
/// <c>action_data["84"]</c>（**对手**的 HQ 防御力，动作结算前的采样）**。
///
/// 为什么这个比对有意义：
/// - `84` 已被 21 回合 / 94 条动作的回放确认是「行动方对手的 HQ 当前防御力」，
///   而且整条轨迹与终局的 `Victory_DestroyHQ` 完全闭合
/// - HQ 防御只在「受到伤害」时变化，所以它对**攻击结算、伤害数值、摧毁判定**
///   高度敏感 —— 对不上就说明这些地方理解有误
/// - 它是动作流里唯一携带的状态量，不需要额外的状态导出
///
/// 首次对不上的位置就是最有价值的线索：那里就是规则理解第一个出错的地方。
///
/// ⚠️ **已知保真度缺口**（会污染结论，必须一起看）：
/// 1. 起始手牌不是快照给的那个。实测快照判为「牌库」的牌会被客户端直接打出，
///    原因是 fyserver 自己洗牌发牌，而真实客户端并不按它发。
///    → 本器用「按需注入手牌」对冲（记 `InjectedToHand`），
///    只保证**棋盘与 HQ 的演化**可比，不保证手牌经济完全一致。
/// 2. 动作流里有**重复记录**（同一 cardID 连续被 `PC` 4 次），按重复丢弃。
/// 3. 对局中生成的卡（如 cardID 11001）不在快照里，用动作自带的卡组码补建。
/// </summary>
public sealed class ReplayRunner
{
    private readonly CardDatabase _db;
    private readonly Dictionary<int, CardInstance> _cardAliases = new();

    public ReplayRunner(CardDatabase db) => _db = db;

    /// <summary>
    /// 可选的**开局分区覆盖**：cardID → 该卡开局在哪个位置。
    ///
    /// 不设时所有非棋盘卡一律进牌库，打出时再按需注入手牌 —— 这样牌库/手牌的分界
    /// 是假的，任何「区域」对拍都会被这条人为缺口污染（实测区域准确率只有 43%，
    /// 而真实开局是 4/5 张手牌 + 35/34 张牌库）。用真实快照的开局分区做种子之后，
    /// 区域这条指标衡量的才是**动力学**，而不是种子。
    /// </summary>
    public IReadOnlyDictionary<int, CardLocation>? InitialLocations { get; set; }

    /// <summary>
    /// 开局的 `cardID → locationNumber`（区**内**顺序）。
    ///
    /// 为什么必须和 <see cref="InitialLocations"/> 一起给：`MatchEngine.DrawCard` 取
    /// `State.Deck(side)` 的第一张，而 `GameState.Cards()` **按 `locationNumber` 排序**。
    /// 只播种区域、不播种顺序的话，牌库顺序会退化成 `CreateWithId` 的插入顺序
    /// （= fyserver `Random.Shared` 的洗牌序），**不是客户端的牌库序** ⇒
    /// 每次抽牌抽到的是"另一个顺序下的第一张" ⇒ 逐卡区域对不上。
    ///
    /// 客户端洗出来的顺序**就在采集快照里**：`starting_hand_*` 是 0..n-1、
    /// `deck_*` 接着往下编号（实测 `deck_left` 是 4..38、`deck_right` 是 5..38），
    /// `locationNumber` 跨手牌与牌库**连续**，正好等于"第几张被摸到"。
    /// </summary>
    public IReadOnlyDictionary<int, int>? InitialLocationNumbers { get; set; }

    /// <summary>诊断开关：打开后把每个失败动作的完整堆栈打到 stderr。</summary>
    public static bool TraceExceptions { get; set; }

    /// <summary>
    /// **身份校正**开关（**默认关**）。
    ///
    /// ⚠️ 2026-10-02 方向变更后它从"主修复"降级为"可选兜底"：
    /// 真正的修法是让内核**复刻客户端的 `cardsRandomStream`**（见 `UeRandomStream`），
    /// 随机效果本来就该选对，不需要事后校正。
    /// 实测（`out/audit/audit-idfix-compare.ps1`）在 RNG 修好之后：
    /// `773639` 关掉校正 = **0 条人类失败**；打开校正反而变成 11 条 ——
    /// 因为校正会把内核**已经正确**的身份按动作码覆盖掉，而动作码只保证
    /// "客户端认为这张卡是什么"，一旦我们的 cardID 分配也漂了，
    /// 用码去覆盖就是**用一个错换另一个错**。
    ///
    /// 所以默认关，只在做 A/B 归因或临时兜底时打开。
    /// </summary>
    public bool IdentityCorrection { get; set; }

    /// <summary>
    /// **单卡门控**（默认 null = 不门控，全部校正）。
    ///
    /// 非 null 时，只有「内核那一张」或「客户端说的那一张」的卡名落在这个集合里才校正。
    /// 用途：把一次改动拆成"只对某一张卡生效"，看整体差异是不是**全部**来自它 ——
    /// 随机效果参与时「人类失败总数」本来就会变（可能变好也可能变差），
    /// 只有门控实验才能把因果钉死。
    /// </summary>
    public HashSet<string>? IdentityCorrectionOnly { get; set; }

    /// <summary>
    /// **归因实验开关**（默认关）：连续同侧的 `StartOfTurn`（中间没有 `EndOfTurn`）
    /// 除了"不算新的发号回合"之外，**还要不要补一次 kredit 槽自然增长**。
    ///
    /// ## 为什么会有这个开关（2026-10-02，对局 773639 的 #74/#75）
    ///
    /// 那两条记录是同侧连续 `StartOfTurn`（`#74 t17 left` → `#75 t18 left`），
    /// 而它们**同时**给出两条互相矛盾的硬证据：
    /// <list type="bullet">
    /// <item>**发号说它不是新回合**：客户端在这一段里发出来的号是 `17001`/`17002`
    ///   （`#88 t20 PC 17001 code=yJ`=meteor、`#80 t18 PC 17002 code=gu`=radar），
    ///   即 `GetTurnNumber()` 仍是 **17**。所以 `clientTurn` 不能 +1（这就是
    ///   <see cref="ClientIdTurnOverride"/> 那条去重规则的由来）。</item>
    /// <item>**合法性说它必须补一格 kredit**：这一段人类打出的牌按**客户端真实的卡**
    ///   算，支出正好是 `meteor 4 + 攻击 1 + royal_research 3 + honey_desert 2 +
    ///   radar 0 + 2nd_west_africa 1 + chain_home 0 + 移动 1 = 12`，
    ///   而我们这一步的槽上限只有 **11**（9 个自然回合 + `war_bonds` +2）。
    ///   人类那条 `#83 t18 ML` 真实成立了 ⇒ 客户端的槽 ≥ 12。</item>
    /// </list>
    ///
    /// 两条证据只有在「**发号的回合号**与**kredit 槽的自然增长**由两个不同的计数器驱动」
    /// 时才同时成立：`CurrentTurnNumberInBattle`（蓝图 `GetTurnNumber()`，
    /// `BP_GameState_Battle.g.cs:2598`，由服务端/在线蓝图推进）没动，
    /// 而 `StartTurnBySide` 那边的槽位增长照常。打开这个开关就是验证这个假设。
    /// </summary>
    public bool KreditSlotOnDuplicateStart { get; set; }

    /// <summary>
    /// 是否收集 **RNG 游标流水账**（`GameState.RandomTrace`）。
    /// 打开会给每个随机消费点记一笔，供 `--rng-trace` 打印。
    /// </summary>
    public bool CollectRandomTrace { get; set; }

    /// <summary>
    /// 当前这次 <see cref="Run"/> 用的引擎实例（`Run` 一开始就赋值）。
    ///
    /// 用途：`onStepped` 回调是在 `Run` **内部**触发的，那时 `Report` 还没返回，
    /// 所以想看「内核这一条动作到底做了什么」（`MatchEngine.Log`）就必须从这里拿。
    /// 对局 389594 `#72 t15 AC` 那 1 点 HQ 差就是靠它定位的。
    /// </summary>
    public MatchEngine? Engine { get; private set; }

    /// <summary>内核日志当前条数（`Engine` 为 null 时 0）。</summary>
    public int EngineLogCount => Engine?.Log.Count ?? 0;

    /// <summary>读内核日志的第 <paramref name="i"/> 条。</summary>
    public string EngineLog(int i) => Engine!.Log[i];

    /// <summary>
    /// 逐条记录「动作自带的卡组码 ≠ 内核里那张卡的卡组码」。
    ///
    /// ⚠️ 与 <see cref="IdentityCorrection"/> 无关，**总是**记录 —— 量化影响面要的是
    /// "发现了多少条"，而不是"改了多少条"。
    /// </summary>
    public List<IdentityEvent> IdentityEvents { get; } = new();

    /// <summary>
    /// 逐条记录「这条 `PC` 动作的目标**过不了客户端的门**」。
    ///
    /// ## 为什么需要它（2026-10-02，目标合法性门）
    ///
    /// 客户端选目标要过**两道门**（枚举主循环 `_deps/BP_Logic.g.cs:1235-1355`）：
    /// <list type="number">
    /// <item>卡自己的 `CanPlayFromHand`（「只能指定空军 / 老兵 / 敌方 / 友方」在这里）；</item>
    /// <item>规则库的 `CanSelectAsTarget`（隐蔽 / 敌方指令 / 费用 / 被指方自身）。</item>
    /// </list>
    /// 动作流里若记着一个**客户端认为非法**的目标，说明发出这条动作的那一侧
    /// （通常是**旧内核**的 bot）选了非法目标 —— 客户端会**静默不执行**
    /// （记牌器 +1、场上无变化，玩家报告的「虚空牌」），而我们这边会把效果
    /// 落到那个目标上 ⇒ **状态从这一刻起漂开**。
    ///
    /// ⚠️ 这里**只报告、不改行为**。为什么不直接"跳过效果"：那需要先证明
    /// 客户端在那一步确实什么都不做（卡是否仍被消耗、记牌器是否 +1 都还没有直接证据）。
    /// 用一个未验证的假设去替换另一个，只会把漂开点挪到别处、更难归因。
    /// 报告出来后，逐条对着客户端状态看，再决定要不要改成"跳过"。
    /// </summary>
    public List<TargetGateEvent> TargetGateEvents { get; } = new();

    /// <summary>一次「目标过不了门」的完整记录。</summary>
    public sealed record TargetGateEvent(
        int ActionId,
        int Turn,
        string PlayerSide,
        int CardId,
        string CardName,
        int TargetId,
        string TargetName,
        bool Missing,
        string Reason);

    /// <summary>一次身份不一致的完整记录（供审计与量化）。</summary>
    public sealed record IdentityEvent(
        int ActionId,
        int Turn,
        string ActionType,
        int CardId,
        string KernelName,
        string ActionName,
        string ActionCode,
        string? KernelCode,
        bool Corrected,
        string Note);

    /// <summary>单个动作的重放结果。</summary>
    public sealed record StepResult(
        int ActionId,
        int Turn,
        string ActionType,
        string PlayerSide,
        bool Applied,
        string? Failure,
        int ExpectedHq,
        int ActualHq,
        bool HqMatches,
        int InjectedToHand,
        bool Duplicate);

    public sealed class Report
    {
        public required int MatchId { get; init; }
        public required int Turns { get; init; }
        public required string WinnerSide { get; init; }
        public required IReadOnlyList<StepResult> Steps { get; init; }
        public required int TotalActions { get; init; }

        /// <summary>快照 cardID → 卡名 与动作自带卡组码不一致的次数（应为 0）。</summary>
        public required int IdentityConflicts { get; init; }

        /// <summary>
        /// 「动作自带的卡组码 ≠ 内核里那张卡的卡组码」的逐条记录
        /// （= 效果随机/复制出来的卡，内核与客户端选中的不是同一张）。
        /// </summary>
        public required IReadOnlyList<IdentityEvent> IdentityEvents { get; init; }

        /// <summary>其中被就地校正过来的条数。</summary>
        public int IdentityCorrectedCount => IdentityEvents.Count(e => e.Corrected);

        /// <summary>
        /// 「这条 PC 动作的目标过不了客户端的门」的逐条记录（见 <see cref="TargetGateEvents"/>）。
        /// </summary>
        public required IReadOnlyList<TargetGateEvent> TargetGateEvents { get; init; }

        /// <summary>
        /// 整局**消耗了多少个随机数**（= 客户端 `cardsRandomStream` 的游标终点）。
        /// 用来和客户端对账：漏一个消费点就少、多一个就多。
        /// </summary>
        public required long RandomConsumed { get; init; }

        /// <summary>RNG 游标流水账（只在 <c>CollectRandomTrace</c> 打开时有内容）。</summary>
        public required IReadOnlyList<string> RandomTrace { get; init; }

        /// <summary>卡组码解析不出来的次数。</summary>
        public required int UnknownCodes { get; init; }

        /// <summary>
        /// 重放结束后算出的「客户端回合号」= 客户端 `GetTurnNumber()` 的复刻，
        /// 效果生成卡的编号前缀就取自它（见 <see cref="GameState.NextCardId"/>）。
        ///
        /// 为什么交出来：这条规则的唯一判据是「**连续同侧的 `StartOfTurn` 只算一个回合**」，
        /// 而它的正确性只能靠**动作流形状**验证（对局 `773639` 的 `#74/#75`）。
        /// 没有这个出口就只能靠"生成的卡号对不对"间接判，一旦卡号又因为别的原因错位，
        /// 就分不清是哪一层的问题。`tools/BotSim` 的
        /// 「回放发号：连续同侧 StartOfTurn」那条自测直接断言它。
        /// </summary>
        public required int ClientTurn { get; init; }

        /// <summary>推出来的「对手 HQ」字段下标。</summary>
        public required string? HqKey { get; init; }

        /// <summary>
        /// 重放结束后的**实时引擎**（含完整局面）。
        ///
        /// 为什么要交出来：接服务器时需要在「重放出的局面上」继续下棋
        /// （<see cref="Server.BotTurnService"/>）。以前调用方只能拿到
        /// `onStepped` 回调里的 <c>GameState</c> —— 那只是状态，**没法再调
        /// <c>PlayCard</c>/<c>Attack</c>/<c>MoveUnit</c>**，于是只能自己另建一个引擎，
        /// 而那个引擎是空的、与重放出来的局面无关（这个坑我踩过一次）。
        /// </summary>
        public MatchEngine? Engine { get; init; }

        /// <summary>回放里 XActionCheat 的条数（&gt;0 说明是开作弊打的测试局）。</summary>
        public required int CheatActions { get; init; }

        public int AppliedCount => Steps.Count(s => s.Applied);
        public int NotUnderstood => Steps.Count(s => !s.Applied && !s.Duplicate);
        public int Duplicates => Steps.Count(s => s.Duplicate);
        public int Injected => Steps.Sum(s => s.InjectedToHand);
        public int HqChecked => Steps.Count(s => s.ExpectedHq > 0);
        public int HqMatched => Steps.Count(s => s.ExpectedHq > 0 && s.HqMatches);

        /// <summary>第一次 HQ 对不上的位置 —— 最有价值的线索。</summary>
        public StepResult? FirstMismatch
            => Steps.FirstOrDefault(s => s.ExpectedHq > 0 && !s.HqMatches);

        public IEnumerable<IGrouping<string, StepResult>> NotUnderstoodByType
            => Steps.Where(s => !s.Applied && !s.Duplicate).GroupBy(s => s.ActionType);

        public int LastCheckedTurn => Steps.Where(s => s.ExpectedHq > 0).Select(s => s.Turn).DefaultIfEmpty(0).Max();

        /// <summary>重放过程中撞到的未实现原语调用（次数从多到少）。</summary>
        public required IReadOnlyList<KeyValuePair<string, int>> Unimplemented { get; init; }

        /// <summary>HQ 轨迹一直对到第几回合 —— 内核可信度的下界。</summary>
        public int HqCleanTurns
        {
            get
            {
                var first = FirstMismatch;
                return first is null ? LastCheckedTurn : first.Turn - 1;
            }
        }
    }

    /// <summary>把一个动作映射成内核操作所需的参数（用于诊断）。</summary>
    private static string Describe(WireAction a) => a.ActionType switch
    {
        "PC" => $"打出 cardID={a.CardId} 槽位={a.SecondId} 目标={a.TargetId}",
        "AC" => $"cardID={a.CardId} 攻击 cardID={a.SecondId}",
        "ML" => $"移动 cardID={a.CardId} 到槽位={a.SecondId}",
        "CS" => $"选牌答复：cardID={a.CardId} 候选下标={a.SecondId} " +
                $"选中码={a.Get(WireAction.KeyIndex.CodeSlotA)}",
        "XActionStartOfTurn" => "回合开始",
        "XActionEndOfTurn" => "回合结束",
        _ => a.ActionType,
    };

    public Report Run(ReplayData replay, bool verbose)
        => Run(replay, verbose, null);

    /// <param name="onStepped">
    /// 每个动作结算**之后**回调一次 <c>(动作, 当前状态)</c>。
    /// 这是给「和真实快照逐字段对拍」用的钩子 —— 对拍工具不需要重写一遍驱动逻辑，
    /// 也就不会漏掉这里已经踩过的坑（手牌注入、重复动作、XActionStartOfTurn 只算一次）。
    /// </param>
    public Report Run(ReplayData replay, bool verbose, Action<WireAction, GameState>? onStepped)
    {
        _cardAliases.Clear();
        // ---- 1) 卡池：快照里每个 cardID 都是唯一的，直接全建成「牌库」，
        //         真正需要时再按需注入手牌（见类注释的保真度缺口 1） ----
        var engine = new MatchEngine(_db, Array.Empty<string>(), Array.Empty<string>(),
                                     seed: (ulong)replay.MatchId);
        Engine = engine;
        var state = engine.State;

        // ★ 效果生成的卡按**客户端规则**发号（`回合号 × 1000 + 本回合第几张`）。
        //
        // 2026-10-02 修正：这条规则**对双方一致**，不再区分"官方客户端那一方"。
        // 依据是客户端的分配器本身 —— `GenerateNextCardID(turnNumber, out id)`
        // **没有 side 参数**（`ref/kards-sim/.../_deps/BP_GameState_Battle.g.cs:1545`），
        // 详见 `GameState.NextCardId` 的长注释。以前只给人类那一方用，导致
        // 我们 bot 生成出来的卡发的是顺序号（81/82…），客户端在本地执行同一个效果时
        // 用的是 `1000×回合+序号` ⇒ 我们发出去的 `PC {"0": 81}` 客户端认不出
        // ⇒ 记牌器 +1、场上什么都没有（真人玩家实测的「虚空部署」）。
        //
        // ⚠️ 播种快照（`CreateWithId`）**不经过** `NextCardId`，所以开局的 1..80 号不受影响。
        //
        // RNG 游标探针（只在诊断路径上开）
        state.CollectRandomTrace = CollectRandomTrace;

        // 生成卡身份可信度：**跟踪**（用来量化"我们手里有多少张客户端未必认得的卡"），
        // 但**不在回放路径上拦** —— 回放的是客户端自己发过的动作，动作流本身就是
        // "客户端认得这张卡"的证据，拒绝它只会让重建更差。见 `MatchEngine.EnforceGeneratedCardTrust`。
        state.TrackGeneratedCardTrust = true;
        engine.EnforceGeneratedCardTrust = false;

        // ---- 0) **HQ 卡必须先建**（2026-10-02 修正）----
        //
        // ★★ 为什么顺序要紧：客户端所有「遍历场上」的函数都走 `GetAllCardInBattle()`
        //    = `Map_Values(AllCardsInBattle)`（`_deps/BP_GameState_Battle.g.cs:1571`），
        //    而那个映射由 `AddCardToAllCardsInBattle`（`:301-319`）**只增不删**
        //    （全树 0 处 `Map_Remove`）⇒ **顺序 = 卡进入战斗的顺序**。
        //    客户端那边 **HQ 是开局就存在的** ⇒ 它在映射里排**最前**。
        //
        //    ⚠️ 旧实现把 HQ 放在**主循环之后**建（见下面那段的旧址）⇒ 内核的插入序里
        //    HQ 落到**最后** ⇒ 「随机挑一张敌方场上的卡」这类效果（`unitsOnly=false`
        //    含 HQ）**同一次消费、同一个下标会取到不同的卡**。
        //    实测（回放 854099 `#39`）：内核池 `[111th_indian_brigade, 5th_parachute_brigade,
        //    85_pioneer_company, card_location_london]`（HQ 在末），
        //    而客户端那一刻打的是 **HQ** ⇒ 两边下标语义不一致。
        foreach (var c in replay.Cards.Where(c => c.Location.IsBoard()))
        {
            state.CreateWithId(c.Name, c.Owner, c.CardId, c.Location, c.LocationNumber, c.IsGold);
        }

        int known = 0, skipped = 0;
        foreach (var c in replay.Cards)
        {
            if (c.Location.IsBoard())
            {
                continue;   // HQ 已在上面建过
            }

            if (_db.Find(c.Name) is null)
            {
                skipped++;
                continue;
            }

            // ⚠️ 必须用**分侧的**牌库枚举（DeckLeft/DeckRight）：`CardLocation.Deck`
            //    是另一个枚举值，`State.Deck(side)` 查的是 DeckOf()，用错的话牌库会被
            //    当成空的 —— 表现为每回合凭空吃疲劳伤害（HQ 20→19→17→14→10→5）。
            //
            // ★★ 2026-10-01：**必须连 `locationNumber` 一起播种**，否则区域逐卡只有 ~60%。
            //
            //    根因：`MatchEngine.DrawCard` 取的是 `State.Deck(side)` 的第一张，
            //    而 `GameState.Cards()` 按 `LocationNumber` 排序（同号再按 cardId）。
            //    以前这里所有卡都建成 `locationNumber = 0`，于是牌库顺序退化成
            //    `CreateWithId` 的**插入顺序**（= `replay.Cards` 的枚举顺序 = fyserver 的
            //    `Random.Shared` 洗牌顺序），**不是客户端真正的牌库顺序**。
            //    两边的牌库顺序不同 ⇒ 每次抽牌抽到的是"另一个顺序下的第一张" ⇒
            //    **具体哪张卡在哪个区域**全错位（区域大小仍然对得上，因为每回合
            //    抽几张、打几张是一致的 —— 这就是"大小吻合、逐卡只有 60%"的来源）。
            //
            //    客户端洗出来的顺序**就在快照里**：`starting_hand_*` 是 0..n-1、
            //    `deck_*` 接着往下编号（实测 `deck_left` 是 4..38、`deck_right` 是 5..38）。
            //    实测数据（`replay-989040`）：
            //      starting_hand_left  n=4  locNum 0..3
            //      deck_left           n=35 locNum 4..38
            //      starting_hand_right n=5  locNum 0..4
            //      deck_right          n=34 locNum 5..38
            //    `locationNumber` 是**跨手牌与牌库连续**的，正好等于"第几张被摸到"。
            //
            //    ⚠️ 但要分清**哪个源可信**（2026-10-01 查死）：
            //      · 区域 + `locationNumber` → **只有采集快照可信**。fyserver 的
            //        `starting_data` 用 `Random.Shared` 洗牌 + `Take(4/5)` 当手牌，
            //        实测和客户端直接矛盾：#4 `the_rock_of_gibraltar` 快照在牌库
            //        （locNum=37）、回放说起手；#9 `queens_own` 快照在手牌（locNum=0）、
            //        回放说牌库。
            //      · `cardID` ↔ 卡名 → 回放的**是对的**（实测 77/80 同名命中；
            //        不同的 3 个全是快照侧的 `None` 占位和回放的 `_bal` 变体）。
            //    所以两个源要**合起来用**：id/名字取回放，区域/顺序取快照
            //    （即 `InitialLocations` + `InitialLocationNumbers`，由调用方从快照填）。
            //    回放自带的值只当兜底（没有快照时），这时顺序必然是错的。
            int locNum = InitialLocationNumbers?.GetValueOrDefault(c.CardId, c.LocationNumber)
                         ?? c.LocationNumber;
            state.CreateWithId(c.Name, c.Owner, c.CardId,
                InitialLocations?.GetValueOrDefault(c.CardId) ?? c.Location,
                locNum, c.IsGold);
            known++;
        }

        // HQ 卡：**已在主循环之前建过**（见那段注释 —— 顺序要匹配客户端
        // `AllCardsInBattle` 的插入序，HQ 必须最先）。

        // HQ 初始防御 20（已由实测确认）
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            if (state.Cards(side).Any(c => c.IsHq))
            {
                state.SetHqDefense(side, MatchEngine.InitialHqDefense);
            }
        }

        // ---- 2) 逐步重放 ----
        var steps = new List<StepResult>();
        int identityConflicts = 0, unknownCodes = 0;
        bool turnStarted = false;

        // ⚠️ **bot 那一侧每个回合在动作流里有两条 `XActionEndOfTurn`** ——
        //    那是【我们】发的重复，不是协议。实测（`out/_server-replays/*.actions.json`，
        //    逐条统计见报告）：
        // <code>
        //     replay-214436  EndOfTurn  side={right:12, left:6}    StartOfTurn={left:7, right:6}
        //     replay-508065  EndOfTurn  side={right:24, left:12}   StartOfTurn={left:13, right:12}
        //     replay-542091  EndOfTurn  side={right:14, left:7}    StartOfTurn={left:8,  right:7}
        // </code>
        //    真人（left，player=900003/900005）每回合**只有一条**，`reason="endTurnButton"`；
        //    bot（right，player=-9178）每回合**两条且相邻**，一条 `reason="endTurnButton"`
        //    （= `MatchEngine.EndTurn` 自己 `RecordAction` 的那条）、一条 `reason` 缺失
        //    （= 服务端把我们发出去的那条线上动作也记了一遍）。
        //    所以 right 恒等于 left×2。
        //
        //    为什么必须去重：`EndTurn` 内部会 `State.Turn++` 且 `StartTurn(对手)`
        //    （`MatchEngine.cs:559-560`），连吃两条等于**一个回合推进两次** ——
        //    对手多抽一张、kredit 多刷一轮、`ActiveSide` 直接翻回自己。
        //    探针实测：左方槽位 `1,2,3,5,6,7,8,11,12,15…` = `2k-1`（偏高），右方 `1..7` 正确。
        //
        //    判据用「同一侧、且中间没有出现过 `XActionStartOfTurn`」—— 用「相邻」也能过
        //    （实测三条回放里 25 对重复全部相邻），但按回合语义写更稳：
        //    正常对局里同一个人不可能连着结束两次回合。
        Side? endedTurnSide = null;

        // ⚠️ 这里原有一个 `seenPlayedIds`（「曾打出过的 cardID」集合），用来把
        //    「PC 引用一张已经打出过的卡」判成重复记录。**已删除**（2026-10-02）：
        //    那条判据会误杀「退回手牌后再打出」这条合法路径（见下面 `PC` 分支的注释）。
        //    判重只保留「该 cardID **当前在场上**」这一条。
        string? hqKey = replay.InferHqKey();

        // 客户端发号用的回合号 —— 数 `XActionStartOfTurn`（见下面那段长注释）。
        int clientTurn = 0;

        // ★★ 2026-10-02：**连续同侧的 `XActionStartOfTurn` 只能算一个回合**。
        //
        // 症状（对局 `773639`，实测）：`#74 t17 left` 与 `#75 t18 left` 是**紧挨着的
        // 两条左侧 `StartOfTurn`，中间没有任何 `EndOfTurn`**。按"数条数"的旧规则，
        // 从 #75 起 `clientTurn` 比客户端自己的 `GetTurnNumber()` **多 1**，于是
        // **效果生成的卡号整体偏 1000**：
        // <code>
        //   我们的 CREATE 流水（`--rng-trace`）：#18001 #20001 #22001（流星副本）
        //                                      #26001..#26003 #28001/#28002
        //   动作流里人类真正引用的：             #17001 #19001
        //                                      #25001..#25003 #27001/#27002
        // </code>
        // ⇒ 人类 `#88 PC {"0":17001}` 引用的那张卡我们**根本没生成过**，
        //   只能走 `ResolveCard` 的"按码现建"兜底 —— 建出来的是一张**裸的 1/1 流星**，
        //   而客户端那张是 `OnAfterAttack` 翻倍出来的 **2/2**（`#19001` 是 4/4）。
        //   于是 `#89` 打右 HQ：客户端 19→**17**（2 点）、我们 19→**18**（1 点）
        //   ⇒ 从 `#90` 起 HQ 校验和全程差 1，`#98` 起差 4。
        //   **这就是"HQ 追踪漂开"在这一局里的真实来源 —— 不是伤害算错，是卡号错位。**
        //
        // 判据与 `endedTurnSide` 同形（那边去重 `EndOfTurn`，这边去重 `StartOfTurn`）：
        // **同一侧 + 中间没出现过 `EndOfTurn`** ⇒ 第二条是转发/重连留下的重复标记。
        // 五局回放逐条扫过：只有 `773639 #75` 命中这一条，其余四局**一条都没有**
        // ⇒ 这条规则对那四局是**恒等变换**（零回归面）。
        //
        // ⚠️ 为什么不用 `state.Turn` 当回合号：它虽然在这一局也对，但它是**内核自己**
        //    的计数（每 `EndTurn` +1），`EndOfTurn` 去重一旦失效它就会整体漂开；
        //    而"客户端数了几次 StartOfTurn"是**动作流本身**的性质，不依赖内核。
        Side? lastStartSide = null;
        bool sawEndTurnSinceStart = false;

        // 回放里可能带 XActionCheat（SetKredits / SpawnCard）—— 那几局是开作弊打的测试局，
        // kredit 对不上是预期的，不能让它们污染「规则错误」的结论。
        int cheats = replay.Actions.Count(a => a.ActionType == "XActionCheat");

        // ---- CS 答复队列：「从候选里挑一张」的玩家选择 ----
        //
        // `CS` = `XActionCardToDrawSelected`（依据见 WireAction.CompactToFull 的注释），
        // 是 `selectCardToDraw` 里那条 `NotifySelectCardToDrawPending` 分支的**答复**：
        //   `0` = 挑牌的那张卡的 cardID，`1` = 候选下标（0..2），`2` = 选中卡的卡组码。
        //
        // ⚠️ 时序：`CS` 排在触发它的 `PC` **之后**（真实对局里客户端要等玩家点完），
        //    而内核是在 `PC` 的效果里**同步**就把 `selectCardToDraw` 跑完了。
        //    所以这里**先整体扫一遍**，把答复按 cardID 排成队列预挂到引擎上，
        //    等效果调 `selectCardToDraw` 时按顺序取用。
        //    同一张卡连续答复多次是实测存在的（206428 的 #57/#58、#61/#62
        //    都是同一张卡连选两次），所以必须是队列、不能只存一个值。
        var csQueue = new Dictionary<int, Queue<(int Index, string? Code)>>();
        foreach (var act in replay.Actions)
        {
            if (act.ActionType != "CS")
            {
                continue;
            }

            if (!csQueue.TryGetValue(act.CardId, out var q))
            {
                csQueue[act.CardId] = q = new Queue<(int, string?)>();
            }

            q.Enqueue((act.SecondId, act.Get(WireAction.KeyIndex.CodeSlotA)));
        }

        // 每张卡"已被效果消费掉几条答复"的计数，供下面 `case "CS"` 判断是否需要补做。
        var csConsumed = new Dictionary<int, int>();

        // 每张卡"被效果取走、但解不出选中卡"的计数 —— 这些答复**没有落实**，
        // 必须在 `case "CS"` 里如实记成未应用，不能因为"效果跑过了"就算成功。
        var csUnresolved = new Dictionary<int, int>();

        // ---- `HT` = `XActionHandTargetSelected`：**「从手牌挑一张」的答复** ----
        //
        // 与 `CS` 是**并列的第二种选牌答复**（`CS` 是"从候选/牌库挑"，
        // `HT` 是"从手牌挑"）。用它的典型卡是 `card_unit_gordon_highlanders`：
        // 「Deployment: Choose an order in hand. Set its cost to 0 and put it on top of your deck.」
        //
        // 线格式（实测 781364 `#156 HT {"0":"10","1":"7"}`）：
        //   `0` = 挑牌的那张卡（gordon，cardID 10）
        //   `1` = **被选中的手牌**（cardID 7）
        //
        // ⚠️ 这条以前**完全没处理**：`selectTargetFromHand` 是空壳、`HT` 也没有分支
        //    ⇒ 人类选的手牌既没被设成 0 费、也没回牌库 ⇒ 状态从那里开始漂开
        //    （实测 t25 之后 t27 一片动作应用失败）。
        var htQueue = new Dictionary<int, Queue<int>>();
        foreach (var act in replay.Actions)
        {
            if (act.ActionType != "HT")
            {
                continue;
            }

            if (!htQueue.TryGetValue(act.CardId, out var hq))
            {
                htQueue[act.CardId] = hq = new Queue<int>();
            }

            hq.Enqueue(act.SecondId);
        }

        var htConsumed = new Dictionary<int, int>();

        engine.PickHandTarget = (selecting, candidates) =>
        {
            if (selecting is null
                || !htQueue.TryGetValue(selecting.CardId, out var hq)
                || hq.Count == 0)
            {
                return null;   // 没有答复 → 由调用方走兜底
            }

            int wantId = hq.Dequeue();
            htConsumed[selecting.CardId] = htConsumed.GetValueOrDefault(selecting.CardId) + 1;

            // 答复指向的必须是候选表里的卡；否则如实返回 null（不硬挑一张）
            return candidates.FirstOrDefault(x => x.CardId == wantId);
        };

        engine.PickCardToDraw = (selecting, fromTopOfDeck, _) =>
        {
            if (selecting is null
                || !csQueue.TryGetValue(selecting.CardId, out var q)
                || q.Count == 0)
            {
                return null;   // 没有答复 → 由 CardApi 走兜底（自对弈才用得上）
            }

            var (index, code) = q.Dequeue();
            csConsumed[selecting.CardId] = csConsumed.GetValueOrDefault(selecting.CardId) + 1;

            string? name = code is not null && _db.DeckCodeIds.TryGetValue(code, out var nm) ? nm : null;
            if (name is null)
            {
                csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                if (verbose)
                {
                    Console.WriteLine($"        CS 答复解不出卡名：cardID={selecting.CardId} 下标={index} 码={code}");
                }

                return null;
            }

            // ⚠️ 两条路的"选中物"不是同一种东西（见 MatchEngine.PickCardToDraw 的注释）：
            //   · 牌库/占卜族：候选是**牌库里的实例**，要在牌库里找到它。
            //   · Develop 族：候选是 `GetAllActiveStaticCards()` 过滤出的**卡池模板**，
            //     牌库里根本没有这张牌 —— 内核按**卡名**新生成一张
            //     （`CardApi.DevelopChosenCard`，出处见那边的注释）。
            //     这里只需要回一个"带正确卡名的模板实例"，`CardId` 不会被用到。
            if (fromTopOfDeck)
            {
                var deckCard = CardFromDeckCode(selecting, code, state);
                if (deckCard is null)
                {
                    csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                    if (verbose)
                    {
                        Console.WriteLine($"        CS 答复取不到牌库实例：cardID={selecting.CardId} " +
                                          $"下标={index} 码={code}");
                    }
                }

                return deckCard;
            }

            var def = _db.Find(name);
            if (def is null)
            {
                csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                if (verbose)
                {
                    Console.WriteLine($"        CS 答复的卡名不在卡库里：cardID={selecting.CardId} " +
                                      $"码={code} 名={name}");
                }

                return null;
            }

            return KLink.Bot.Effects.CardApi.TemplateInstance(def, selecting.Owner);
        };

        foreach (var a in replay.Actions)
        {
            int turn = a.TurnNumber;
            Side side = replay.SideOf(a.PlayerId);

            // 回合标记自带客户端写入的 side。它比服务端转发的 player_id 更可靠；
            // PC/ML/AC 等动作没有该字段时仍沿用 player_id 推断。
            if (a.ActionType is "XActionStartOfTurn" or "XActionEndOfTurn"
                && SideFromWire(a.Get("side")) is { } explicitSide
                && explicitSide != Side.NotAvailable)
            {
                side = explicitSide;
            }

            string sideStr = side.ToWire() is { Length: > 0 } s ? s : "?";

            // 动作结算**前**采样对手 HQ
            Side foe = side == Side.Left ? Side.Right : side == Side.Right ? Side.Left : Side.NotAvailable;
            int expected = a.OpponentHqOf(hqKey);
            int actual = foe != Side.NotAvailable && state.Cards(foe).Any(c => c.IsHq)
                ? state.HqDefense(foe)
                : -1;

            if (side == Side.NotAvailable)
            {
                // 服务端合成的动作（ActionEndMatch 的 player_id 是 0）
                steps.Add(new StepResult(a.ActionId, turn, a.ActionType, sideStr,
                    false, "无法确定行动方", -1, -1, true, 0, false));
                // 状态没变，但动作序号推进了 —— 对拍方需要这个序号才知道该比对了
                onStepped?.Invoke(a, state);
                continue;
            }

            bool applied = false;
            bool duplicate = false;
            int injected = 0;
            string? failure = null;

            // RNG 游标探针：给流水账打一条动作分隔线，才能把"第几次消费"归到哪条动作上。
            if (CollectRandomTrace && state.RandomTrace.Count < 20000)
            {
                state.RandomTrace.Add($"-- #{a.ActionId} t{turn} {a.ActionType} {sideStr} --");
            }

            // ★ 「客户端发号用的回合号」= **已经处理过的 `XActionStartOfTurn` 条数**。
            //
            // 为什么不能用内核自己的 `state.Turn`：动作流里**每个回合记了两条
            // `EndOfTurn`**（服务端转发时的重复/乱序记录，214436 的 #5 `t3 right`
            // 与 #6 `t2 right` 就是同一次结束），两条都结算的话一个回合会把回合号
            // 推两格 —— 实测人类的 t7 出牌在核心里已经是 `Turn=10`。
            // 客户端的发号是 `回合号 × 1000 + 本回合第几张`（见 `GameState.NextCardId`），
            // 回合号错了发出来的号就全错（7002/7003 会变成 10002/10003），
            // 人类后续引用这些卡的动作就解析不到。
            //
            // ⚠️ **为什么不用当前动作的 `turn_number`**（2026-10-02 修正）：
            //    客户端的回合号是**它自己数 StartOfTurn 得来的**（`GetTurnNumber()`），
            //    而服务端记录的 `turn_number` 在 **bot 那一侧会 +1** ——
            //    实测 773639：bot 的 `#31 t8 XActionStartOfTurn right` 之后，
            //    它的出牌却被记成 `#32..#38 t9`。用"当前动作的 turn_number"发号，
            //    bot 在 `#38 CS` 生成的那张 `no43_commando` 会拿到 **9001**，
            //    把人类 `#46 t9` 引用的 `9001`（= `iron_from_north`）挤成 9002 ⇒
            //    那一步就解析不到、直接判失败。
            //    改用"已处理过的 StartOfTurn 条数"后，bot 那张发 **8001**、
            //    人类那张仍是 9001，两边都对上（`#31` 是第 8 个 StartOfTurn）。
            //
            // 判据自洽性：人类那一侧两条规则**恒等**（它的 `turn_number` 就等于
            // StartOfTurn 条数，实测 773639 的 1001/3001-3004/5001/5002/9001/9002/
            // 17001/17002/19001/25001-25003/27001/27002 全部满足），
            // 所以这次改动只影响 bot 那一侧的生成卡号 —— 而那正是我们要修的地方。
            if (a.ActionType == "XActionStartOfTurn")
            {
                // 连续同侧的 `StartOfTurn`（中间没有 `EndOfTurn`）= 重复标记，不算新回合。
                // 见 `lastStartSide` 的长注释（对局 773639 #74/#75）。
                bool duplicateStart = side == lastStartSide && !sawEndTurnSinceStart;
                if (!duplicateStart)
                {
                    clientTurn++;
                }
                else if (KreditSlotOnDuplicateStart)
                {
                    // 归因实验（默认关）：见 `KreditSlotOnDuplicateStart` 的长注释 ——
                    // 据"客户端那一步确实付得起"这条硬证据，补一次槽位自然增长。
                    int slots = state.MaxKredits(side);
                    if (slots < MatchEngine.NaturalKreditCap)
                    {
                        slots++;
                    }

                    state.SetMaxKredits(side, Math.Min(MatchEngine.MaxKreditCap, slots));
                    state.SetKredits(side, state.MaxKredits(side));
                }

                lastStartSide = side;
                sawEndTurnSinceStart = false;
            }
            else if (a.ActionType == "XActionEndOfTurn" && side == lastStartSide)
            {
                sawEndTurnSinceStart = true;
            }

            state.ClientIdTurnOverride = clientTurn > 0 ? clientTurn : (a.TurnNumber > 0 ? a.TurnNumber : null);

            try
            {
                // ★★ 身份校正：动作流用**卡组码**把客户端真实选中的那张卡告诉我们。
                //    必须排在 `CheckIdentity` 与分派**之前** —— 校正后 `card.KreditCost`
                //    等数值才是客户端那边的值，`CanPlay`/`MoveUnit` 才会判对。
                //    （根因与设计见 `TryCorrectIdentity` 的注释。）
                TryCorrectIdentity(a, replay, state);

                // 卡牌身份自检：动作自带的卡组码 vs 快照卡名
                if (!CheckIdentity(a, replay, ref identityConflicts, ref unknownCodes, out string? idNote))
                {
                    failure = idNote;
                }
                else
                {
                    switch (a.ActionType)
                    {
                        case "XActionStartOfTurn":
                            // 新回合开始 ⇒ 允许下一次 `XActionEndOfTurn`（任何一方）。
                            // 见 `endedTurnSide` 的注释：去重不能跨回合，否则会把
                            // **下一个** 合法的回合结束也一起吞掉。
                            endedTurnSide = null;

                            // ⚠️ `EndTurn` 内部已经 `Turn++` 并 `StartTurn(对手)`，
                            //    所以动作流里的 `XActionStartOfTurn` 只有**第一条**是
                            //    「真正要开回合」，其余都是流里的标记，重复调用会把
                            //    kredit 上限和抽牌都翻倍（实测右方 T8 上限变成 8 而不是 4）。
                            if (!turnStarted)
                            {
                                // 不传 `draw`：本局的第一个 `StartTurn` 恰好是全局回合 1，
                                // `StartTurn` 会按 `BP_Logic.CanSideDrawCards` 的规则
                                // 自动跳过摸牌（先手第 1 回合不摸）。
                                //
                                // 这和本器自己的口径也一致：`InitialLocations` 是拿
                                // **开局快照**（act=1，先手 StartOfTurn 已结算之后的状态）
                                // 当种子的，再摸一张等于把同一个回合开始算两遍 ——
                                // 实测症状：act=1 真实手牌 9 张、内核 10 张（差正好 1）。
                                engine.StartTurn(side);
                                turnStarted = true;
                                applied = true;
                            }
                            else if (state.ActiveSide != side)
                            {
                                failure = $"回合归属不符：流里是 {sideStr}，内核当前行动方是 {state.ActiveSide.ToWire()}";
                            }
                            else
                            {
                                applied = true;   // 标记动作，无需再做
                            }

                            break;

                        case "XActionEndOfTurn":
                            if (endedTurnSide == side)
                            {
                                // 同一侧的**重复**结束回合（bot 那一侧每回合两条，见上面
                                // `endedTurnSide` 的注释）。当成标记动作吃掉，**不再调
                                // `EndTurn`** —— 否则 `Turn++` 与 `StartTurn(对手)`
                                // 会各跑两遍。
                                applied = true;
                            }
                            else
                            {
                                endedTurnSide = side;
                                engine.EndTurn(side);
                                applied = true;
                            }

                            break;

                        case "PC":
                        {
                            var card = ResolveCard(a, replay, side, foe, state);
                            if (card is null)
                            {
                                failure = $"找不到/建不出 cardID={a.CardId}（码 {a.Get("4")}）";
                                break;
                            }

                            // ⚠️ **只判「当前在场上」**，**不再判「曾打出过」**（2026-10-02 修正）。
                            //
                            // 旧判据是 `card.Location.IsBoard() || seenPlayedIds.Contains(card.CardId)`，
                            // 第二条是错的：**「退回手牌后再打出」是一条合法路径**
                            //（`MoveUnitFromBoardToOwnersHand` 把单位退回手牌，之后再 `PC` 打出来），
                            // 而「曾打出过就永远算重复」会把它误判成重复动作 ⇒ 那条 `PC` 被拒
                            // ⇒ 内核手里没有那张卡 ⇒ 之后所有引用它的动作全失败。
                            //
                            // 实测（回放 310284，服务端记录里那一局）：
                            //   `#64 t16` 左方 `card_unit_ace_of_spades` 的 `OnStartOfTurn`
                            //   把**所有单位**退回手牌（IR `i=815 GetAllUnitsOnBoard` → `i=208/i=475`
                            //   `MoveUnitFromBoardToOwnersHand`）；人类随后在
                            //   `#66 t17 PC 5` / `#67 t17 PC 38` / `#93 t21 PC 32` 把其中三张**再打出来**。
                            //   旧判据把这三条判成「重复记录」⇒ 内核里它们留在手上
                            //   ⇒ 后面 `#80/#81 t19 AC`、`#87/#89 t20 ML`、`#92 t21 AC`
                            //   一连串「攻击者/目标不在场上」。
                            //
                            // 「已经在场上还收到 PC」仍然要拦（那确实是重复记录）。
                            if (card.Location.IsBoard())
                            {
                                duplicate = true;
                                failure = "重复记录（该 cardID 已在场）";
                                break;
                            }

                            if (card.Location != side.HandOf())
                            {
                                bool cameFromDeck = card.Location is CardLocation.DeckLeft
                                    or CardLocation.DeckRight;
                                state.Move(card, side.HandOf());
                                injected++;

                                // 动作流引用的牌可能已经在客户端正常抽到手里，而本地牌序
                                // 不同导致它仍在牌库。补进手牌时也要补发抽牌触发，
                                // 否则光环减费与 OnCardDrawnFromDeck 效果会整条缺失。
                                if (cameFromDeck)
                                {
                                    engine.FireEnteredHandFromDeckEvents(card, side,
                                        startOfTurnDraw: false);
                                }
                            }

                            var target = a.TargetId > 0 ? state.ById(a.TargetId) : null;
                            if (!engine.CanPlay(card, out string why))
                            {
                                failure = $"打不出：{why}（kredits={state.Kredits(side)}，费用={card.KreditCost}）";
                                break;
                            }

                            // 三选一（Choose One）的分支就藏在 `PC` 的 `3` 号槽里 ——
                            // 对应 `ZActionPlayCardFromHand` 参数表的 `Int:chooseOneIndex`。
                            // 判据：6 局 295 条 PC 里 `3` 只有 1 条非 0（634651 #122 的
                            // `card_event_planned_attack`，卡面「Choose One - … OR …」、
                            // 蓝图走 `WhichChooseOne` 的 0/1 分支）。
                            // 不写进去的话 `WhichChooseOne` 只能返回默认 0，
                            // 选 1 分支的那次出牌会走错分支。
                            card.ChooseOne = WireAction.ParseInt(a.Get(WireAction.KeyIndex.ChooseOneIndex));

                            // ★★ **目标校验**（2026-10-02）：这条动作的目标过得了客户端的门吗？
                            // 见 `TargetGateEvents` 的注释 —— 只报告、不改行为。
                            if (NeedsTarget(card))
                            {
                                if (target is null)
                                {
                                    TargetGateEvents.Add(new TargetGateEvent(
                                        a.ActionId, turn, sideStr,
                                        card.CardId, card.Name, a.TargetId, "", true, "no_target"));
                                }
                                else
                                {
                                    var chk = engine.Api.CanTarget(card, target);
                                    if (!chk.Can)
                                    {
                                        TargetGateEvents.Add(new TargetGateEvent(
                                            a.ActionId, turn, sideStr,
                                            card.CardId, card.Name, target.CardId, target.Name,
                                            false, chk.Describe()));
                                    }
                                }
                            }

                            engine.PlayCard(card, target);
                            applied = true;
                            break;
                        }

                        case "ML":
                        {
                            var card = ResolveCard(a, replay, side, foe, state);
                            if (card is null)
                            {
                                failure = $"找不到 cardID={a.CardId}";
                                break;
                            }

                            applied = engine.MoveUnit(card, a.SecondId, out string mvWhy);
                            if (!applied)
                            {
                                // ⚠️ 带上**真实原因**（7 道门：行动方/已死/压制/召唤失调/
                                //    油费/对面占前线/我方前线满）。以前只写"移动被拒（当前 X）"，
                                //    而 X 常常是**对的** —— 实测 773639 `#45 t9 ML` 就报
                                //    「当前 BoardHqLeft」，让人误以为"落点放错"。
                                failure = $"移动被拒：{mvWhy}（当前 {card.Location}）";
                            }

                            break;
                        }

                        case "AC":
                        {
                            var attacker = ResolveCard(a, replay, side, foe, state);
                            var defender = ResolveTarget(a, replay, side, foe, state);
                            if (attacker is null || defender is null)
                            {
                                failure = $"找不到攻击者({a.CardId})或防御者({a.SecondId})";
                                break;
                            }

                            applied = engine.Attack(attacker, defender, out string atkWhy);
                            if (!applied)
                            {
                                // ⚠️ 带上**真实原因**（射程/守护/烟幕/压制/油费）。
                                //    以前只写"攻击被拒"，实测对局 508065 在 t19
                                //    有 4 起被拒却分不清是哪道门。
                                failure = $"攻击被拒：{atkWhy}";
                            }

                            break;
                        }

                        // `HT` = XActionHandTargetSelected：「**从手牌挑一张**」的答复。
                        //
                        // 正常路径上，答复已经在 `PC` 的效果结算时被
                        // `selectTargetFromHand` 通过 `engine.PickHandTarget` 取走并落实了
                        // （并广播了 `OnHandTargetSelected`），这里只需记成"已应用"。
                        // 若那张卡的效果**没**走到 `selectTargetFromHand`，
                        // 这条答复就没人取 —— 如实记成未应用，不硬做。
                        case "HT":
                        {
                            var selecting = state.ById(a.CardId);
                            if (selecting is null)
                            {
                                failure = $"找不到挑手牌的卡 cardID={a.CardId}";
                                break;
                            }

                            if (htConsumed.GetValueOrDefault(a.CardId) > 0)
                            {
                                htConsumed[a.CardId]--;
                                applied = true;   // 已被效果消费并落实
                                break;
                            }

                            failure = $"HT 没人取（卡={selecting.Definition.Name}，" +
                                      $"选中的手牌 cardID={a.SecondId}）—— 该卡的效果没走到 selectTargetFromHand";
                            break;
                        }

                        // `CS` = XActionCardToDrawSelected：「从候选里挑一张」的答复。
                        //
                        // 正常路径上，答复已经在 PC 的效果结算时被 `selectCardToDraw`
                        // 通过 `engine.PickCardToDraw` 取走并落实了，这里只需记成"已应用"。
                        // 但如果那张卡的效果**没**走到 `selectCardToDraw`
                        // （PC 被判失败、或该卡的效果脚本缺失），队列里那条答复就没人取 ——
                        // 那种情况在这里补做一次抽取，免得白丢一条真实动作。
                        case "CS":
                        {
                            var selecting = state.ById(a.CardId);
                            if (selecting is null)
                            {
                                failure = $"找不到挑牌的卡 cardID={a.CardId}";
                                break;
                            }

                            // 效果已经把答复取走了、但选中卡解不出来 —— 效果**没落实**，
                            // 如实记未应用（原因见 CardFromDeckCode 的注释）。
                            if (csUnresolved.GetValueOrDefault(a.CardId) > 0)
                            {
                                csUnresolved[a.CardId]--;
                                // ⚠️ 四处失败原因曾经共用一句模糊的话（UnresolvedCsReason），
                                //    查不出到底卡在哪一步 —— 这里各自带上可区分的前缀。
                                failure = "CS①效果取答复时解不出：" +
                                          $"卡={selecting.Definition.Name} " +
                                          $"码={a.Get(WireAction.KeyIndex.CodeSlotA)}";
                                break;
                            }

                            if (csConsumed.GetValueOrDefault(a.CardId) > 0)
                            {
                                csConsumed[a.CardId]--;   // 已被效果消费并落实
                                applied = true;
                                break;
                            }

                            // 没人取的答复：把它从队列里摘掉（否则会被同一张卡的下一次
                            // `selectCardToDraw` 误用），然后在这里自己落实。
                            if (csQueue.TryGetValue(a.CardId, out var q) && q.Count > 0)
                            {
                                q.Dequeue();
                            }

                            string? code = a.Get(WireAction.KeyIndex.CodeSlotA);
                            string? chosenName = code is not null
                                                 && _db.DeckCodeIds.TryGetValue(code, out var nm)
                                ? nm : null;
                            if (chosenName is null)
                            {
                                failure = "CS②选中码不在 deck_code_ids 表里：" +
                                          $"卡={selecting.Definition.Name} 码={code}";
                                break;
                            }

                            // 走哪一族看**这张卡自己有没有 GetChooseSpawnCards**
                            // （有 = Develop 族，候选是卡池模板；没有 = 牌库/占卜族）。
                            bool developFamily = Effects.Blueprint.KismetLibrary.Default?
                                .FindLocalProgram(selecting.Definition.Name, "GetChooseSpawnCards") is not null;

                            if (developFamily)
                            {
                                if (engine.Api.DevelopChosenCard(selecting, chosenName) is null)
                                {
                                    failure = $"CS③Develop 新建失败：卡={selecting.Definition.Name} " +
                                              $"码={code} 名={chosenName}";
                                    break;
                                }

                                applied = true;
                                break;
                            }

                            var picked = CardFromDeckCode(selecting, code, state);
                            if (picked is null)
                            {
                                failure = $"CS④牌库族：{chosenName} 不在 {selecting.Owner} 的牌库里" +
                                          $"（牌库 {state.Deck(selecting.Owner).Count()} 张，码={code}）" +
                                          $" [挑牌的卡={selecting.Definition.Name}#{selecting.CardId}" +
                                          $" 有无GetChooseSpawnCards={developFamily}]";
                                break;
                            }

                            engine.Api.DrawChosenCardToHand(selecting, picked);
                            applied = true;
                            break;
                        }

                        case "ActionEndMatch":
                            applied = true;   // 不影响棋盘
                            break;

                        // 客户端调试作弊（实测两条）：
                        //   {"0":"SetKredits","1":"right","2":"12"}
                        //   {"0":"SpawnCard","1":"right","2":"dy","3":"Hand_Right","4":"0"}
                        // 回放要保真就必须照做，否则 165924 那局连「打不出的 5 费牌」都对不上。
                        case "XActionCheat":
                            applied = ApplyCheat(a, side, state, ref failure);
                            break;

                        default:
                            failure = $"未处理的动作类型 {a.ActionType}";
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                if (TraceExceptions)
                {
                    Console.Error.WriteLine($"--- action {a.ActionId} {a.ActionType} T{turn} ---");
                    Console.Error.WriteLine(ex);
                }
            }

            bool hqMatches = expected <= 0 || actual < 0 || expected == actual;

            steps.Add(new StepResult(a.ActionId, turn, a.ActionType, sideStr,
                applied, failure, expected, actual, hqMatches, injected, duplicate));

            onStepped?.Invoke(a, state);

            if (verbose && (!applied && !duplicate || !hqMatches))
            {
                Console.WriteLine($"  [{a.ActionId,3}] T{turn,-3} {sideStr,-6} {a.ActionType,-20} " +
                                  $"{(applied ? "" : "未应用: " + failure)}" +
                                  $"{(hqMatches ? "" : $"  ⚠ 对手HQ 期望 {expected} 实际 {actual}")}");
                Console.WriteLine($"        {Describe(a)}");

                if (!hqMatches)
                {
                    Console.WriteLine($"        状态: 左HQ={Hq(state, Side.Left)} 右HQ={Hq(state, Side.Right)} " +
                                      $"左k={state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)} " +
                                      $"右k={state.Kredits(Side.Right)}/{state.MaxKredits(Side.Right)}");
                    Console.WriteLine($"        左场: {Board(state, Side.Left)}");
                    Console.WriteLine($"        右场: {Board(state, Side.Right)}");
                    foreach (var act in state.ActionLog.TakeLast(10))
                    {
                        Console.WriteLine($"        · [{act.ActionId}] {act.ActionType} " +
                                          $"{string.Join(" ", act.ActionData.Select(kv => $"{kv.Key}={kv.Value}"))}");

                        foreach (var sub in act.SubActions)
                        {
                            Console.WriteLine($"            └ {sub.Name} " +
                                              $"{string.Join(" ", sub.Values.Select(v => $"{v.Name}={v.Text}{v.Value}"))}");
                        }
                    }
                }
            }
        }

        if (verbose)
        {
            Console.WriteLine($"\n  卡池 {known} 张（跳过 {skipped} 张不在卡库）");
            Console.WriteLine($"  身份自检：冲突 {identityConflicts}，未知卡组码 {unknownCodes}");
        }

        // 回放循环结束就把「客户端发号用的回合号」这个覆盖撤掉。
        // 它只在**重放已有动作**时有意义（那时候只有动作流知道客户端的回合号）；
        // 重放完之后调用方会拿这个引擎继续下棋（`BotTurnService`），
        // 那时候留在引擎上的覆盖值就是**过期**的，不该再影响发号。
        state.ClientIdTurnOverride = null;

        var unimplemented = state.UnimplementedCalls
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        return new Report
        {
            MatchId = replay.MatchId,
            Turns = replay.Turns,
            WinnerSide = replay.WinnerSide,
            Steps = steps,
            TotalActions = replay.Actions.Count,
            IdentityConflicts = identityConflicts,
            IdentityEvents = IdentityEvents.ToList(),
            TargetGateEvents = TargetGateEvents.ToList(),
            RandomConsumed = state.Random.ConsumedCount,
            RandomTrace = CollectRandomTrace ? state.RandomTrace.ToList() : Array.Empty<string>(),
            UnknownCodes = unknownCodes,
            ClientTurn = clientTurn,
            HqKey = hqKey,
            CheatActions = cheats,
            Unimplemented = unimplemented,
            Engine = engine,
        };
    }

    private static int Hq(GameState s, Side side)
        => s.Cards(side).Any(c => c.IsHq) ? s.HqDefense(side) : -1;

    /// <summary>
    /// 执行客户端的调试作弊动作（<c>XActionCheat</c>）。
    ///
    /// 键位：<c>0</c>=命令名，<c>1</c>=目标阵营，<c>2</c>=参数，<c>3</c>=目标区域。
    /// 目前实测到 <c>SetKredits</c> 与 <c>SpawnCard</c> 两条。
    /// </summary>
    private static Side SideFromWire(string? raw) => raw switch
    {
        "left" => Side.Left,
        "right" => Side.Right,
        _ => Side.NotAvailable,
    };

    private bool ApplyCheat(WireAction a, Side fallbackSide, GameState state, ref string? failure)
    {
        string op = a.Get("0") ?? "";
        string? sideRaw = a.Get("1");
        Side side = sideRaw switch
        {
            "left" => Side.Left,
            "right" => Side.Right,
            _ => fallbackSide,
        };

        switch (op)
        {
            case "SetKredits":
                if (!int.TryParse(a.Get("2"), out int amount))
                {
                    failure = $"SetKredits 参数不是整数：{a.Get("2")}";
                    return false;
                }

                state.SetKredits(side, amount);
                state.SetMaxKredits(side, Math.Max(state.MaxKredits(side), amount));
                return true;

            case "SpawnCard":
            {
                string code = a.Get("2") ?? "";
                if (!_db.DeckCodeIds.TryGetValue(code, out string? name))
                {
                    failure = $"SpawnCard 卡组码 {code} 不在 deckCodeIDsTable2 里";
                    return false;
                }

                // 区域名（实测 "Hand_Right"）→ 分侧枚举
                string loc = a.Get("3") ?? "";
                CardLocation target = loc switch
                {
                    "Hand_Left" => CardLocation.HandLeft,
                    "Hand_Right" => CardLocation.HandRight,
                    "Deck_Left" => CardLocation.DeckLeft,
                    "Deck_Right" => CardLocation.DeckRight,
                    "Board_Frontline" => CardLocation.BoardFrontline,
                    _ => side.HandOf(),
                };

                int cardId = int.TryParse(a.Get("4"), out int explicitId) && explicitId > 0
                    ? explicitId
                    : state.NextCardId(side);
                state.CreateWithId(name, side, cardId, target, state.NextLocationNumber(side, target));
                return true;
            }

            // 其它作弊命令先当无害动作放行，别让它污染「规则错误」的统计
            default:
                return true;
        }
    }

    private static string Board(GameState s, Side side)
    {
        var cards = s.Board(side);
        return cards.Count == 0
            ? "（空）"
            : string.Join("  ", cards.Select(c => $"{c.Name}#{c.CardId}(槽{c.LocationNumber} {c.Attack}/{c.Defense})"));
    }

    /// <summary>
    /// 自检：动作自带的卡组码解析出的卡名，必须与快照里同 cardID 的卡名一致。
    /// 5 局真实回放实测 0 冲突 —— 这是「快照 cardID 编号空间可信」的直接证据。
    /// </summary>
    private bool CheckIdentity(WireAction a, ReplayData replay,
                               ref int conflicts, ref int unknownCodes, out string? note)
    {
        note = null;

        // 只有 PC/AC/ML 的 action_data 里带卡组码；其它动作（XActionCheat 等）的
        // 下标语义完全不同（实测 XActionCheat 的键里会出现 "Hand_Right" 这种区名），
        // 硬套 CardCodes 会得到假冲突。
        if (a.ActionType is not ("PC" or "AC" or "ML"))
        {
            return true;
        }

        var codes = a.CardCodes;
        if (codes.Count == 0)
        {
            return true;
        }

        int primaryId = a.ActionType == "AC" ? a.CardId : a.CardId;
        for (int i = 0; i < codes.Count; i++)
        {
            string code = codes[i];
            if (!_db.DeckCodeIds.TryGetValue(code, out string? name))
            {
                unknownCodes++;
                note = $"卡组码 {code} 不在 deckCodeIDsTable2 里";
                return false;
            }

            // AC 的第 2 个码属于防御者，跳过交叉核对
            if (a.ActionType == "AC" && i == 1)
            {
                continue;
            }

            if (!replay.ById.TryGetValue(primaryId, out var snap))
            {
                continue;   // 对局中生成的卡，快照里没有
            }

            string snapBase = CardDatabase.ResolveBaseName(snap.Name);
            string codeBase = CardDatabase.ResolveBaseName(name);
            if (!string.Equals(snapBase, codeBase, StringComparison.Ordinal))
            {
                conflicts++;
                note = $"身份冲突 cardID={primaryId}：快照={snap.Name} 码{code}={name}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// `CS` 答复**没能落实**时的失败原因（要写清楚是缺什么，别写成一句"不支持"）。
    ///
    /// 这一族是 **Develop** —— 候选来自卡自己的 <c>GetChooseSpawnCards()</c>，
    /// 它的实现是 <c>GetAllActiveStaticCards()</c> + 过滤（例：<c>card_event_pams</c> 取
    /// 「英国 + 指令 + 总费 &lt; 5」），也就是**卡池里的模板卡**，不是牌库里的实例。
    ///
    /// 现在这条路已经打通（见 <c>CardApi.DevelopChosenCard</c>），所以剩下的失败只有两类：
    /// ① 卡组码不在 <c>deck_code_ids</c> 表里；② 卡名不在卡库里。
    /// </summary>
    private static string UnresolvedCsReason(WireAction a)
        => $"Develop 选牌：选中码 {a.Get(WireAction.KeyIndex.CodeSlotA)} 解不出卡名" +
           "（不在 deck_code_ids 表里，或卡名不在卡库中）";

    /// <summary>
    /// 把 <c>CS</c> 答复里的卡组码解成「那张卡的实例」。
    ///
    /// 为什么用卡组码而不是用下标：内核**算不出候选表** —— 候选来自卡自己的
    /// <c>GetChooseSpawnCards()</c>，而 IR 生成器只编事件入口，卡内私有函数没有进
    /// <c>card-ir.json</c>。回放的答复自带「选中卡的卡组码」，直接查
    /// <c>deckCodeIDsTable2</c> 拿卡名更可靠（也顺带绕开了"下标指向哪张"这个未知量）。
    ///
    /// 只在挑牌方**自己的牌库**里找：蓝图里的候选就是牌库里的卡对象。
    /// </summary>
    private CardInstance? CardFromDeckCode(CardInstance selecting, string? code, GameState state)
    {
        if (code is null || !_db.DeckCodeIds.TryGetValue(code, out string? name))
        {
            return null;
        }

        return state.Deck(selecting.Owner)
                    .FirstOrDefault(x => x.Name == name || x.Definition.Name == name);
    }

    /// <summary>
    /// ★★ **身份校正** —— 用动作流自带的**卡组码**把内核里那张卡改成客户端真正选中的那张。
    ///
    /// ## 症状（这一类 bug 的统称：效果随机/复制出来的卡，内核选中的与客户端不一致）
    ///
    /// 锁步下效果是**各客户端本地结算**的，所以随机族（`card_event_atlantic_convoy`
    /// 的「Add one random US unit with cost 3 or less…」）与复制族
    /// （`card_event_seac` 的「…Duplicate it.」）在本内核里选中的那一张
    /// **几乎必然与官方客户端不同**：内核的流是
    /// `new MatchEngine(..., seed: (ulong)replay.MatchId)`（见 `Run` 开头），
    /// 客户端的流是它自己的。
    ///
    /// 实测两例：
    /// - **508065 t11**：`#44 ML {"0":"9002","1":"0","2":"DB"}`。内核把 9002 建成
    ///   `card_unit_1st_infantry_regiment_us`（油费 3），而码 `DB` =
    ///   `card_unit_fifth_ohio`（油费 1）。`ML` 扣的是**行动费用**，多扣 2 点
    ///   ⇒ t11 的 kredit 池 7 → 5（ML）→ 3（`#45` land_girls 2）→ 打 `#46` 的
    ///   3 费 `card_unit_p40_warhawk` 时只剩 2 ⇒ 「打不出：kredit 不足」。
    /// - **773639 t9**：`#46`/`#47` 客户端打的是 `card_event_iron_from_the_north`
    ///   （码 `32`，费 1），内核认成 `card_event_the_commonwealth`（费 12）⇒ 直接拒。
    ///
    /// ## 为什么内核不需要"猜对"
    ///
    /// **客户端会在后续动作里用卡组码把它真实选中的那张告诉我们**：每个
    /// `PC`/`ML`/`AC` 的 `action_data` 都带被引用卡的卡组码
    /// （`PC` 在 `4` 号槽、`ML` 在 `2` 号槽、`AC` 攻击者在 `2`、防御者在 `3`；
    /// 见 `WireAction.CardCodes` 与那张下标表）。所以校正点是
    /// **"动作引用了一张内核已经存在、但身份与动作自带的码不符的卡"**。
    ///
    /// ## 为什么必须用**卡组码**而不是卡名
    ///
    /// 审计里那些「当前 HandLeft」之类的假象，有一部分就是**卡号撞车**造成的
    /// （`desert_dust 7002/7003`、`atlantic_convoy 9002`）。拿卡名比会
    /// 把不同卡组码、同名变体的卡误判成同一张；码是客户端的权威标识。
    /// （唯一例外是 `_bal`/`_vet` 这类**数据变体**：基础名相同即视为同一张，
    /// 与 `CheckIdentity` 同口径，免得为了命名差异反复改身份。）
    ///
    /// ## 边界（**故意不校正**的三种情况）
    ///
    /// 1. **快照里的卡**（`replay.ById` 有它）：身份由客户端自己的开局快照定死，
    ///    实测 77/80 同名命中。它不一致是另一类问题，仍由 `CheckIdentity` 如实报失败，
    ///    不在这里悄悄改。
    /// 2. **内核那张卡的卡组码查不出来**（令牌/变体名不在 `deck_code_ids` 里）：
    ///    没有可比对的基准，凭动作码硬改的风险大于收益 —— 只记录，不动。
    /// 3. **基础名已经相同**（`xxx` vs `xxx_bal`）：不算身份不一致。
    /// </summary>
    private void TryCorrectIdentity(WireAction a, ReplayData replay, GameState state)
    {
        // 只有 PC/AC/ML 的 action_data 里带卡组码。其余动作的下标语义完全不同
        // （`XActionCheat` 的键里会出现 "Hand_Right" 这种区名），硬套会得到假冲突
        // —— 与 `CheckIdentity` 同一条判据。
        if (a.ActionType is not ("PC" or "AC" or "ML"))
        {
            return;
        }

        var codes = a.CardCodes;
        if (codes.Count == 0)
        {
            return;
        }

        // 引用关系：AC 的第 2 个码属于**防御者**（`a.SecondId`），其余都指 `a.CardId`。
        CorrectOneIdentity(a, replay, state, a.CardId, codes[0], isDefender: false);
        if (a.ActionType == "AC" && codes.Count > 1)
        {
            CorrectOneIdentity(a, replay, state, a.SecondId, codes[1], isDefender: true);
        }
    }

    /// <summary><see cref="TryCorrectIdentity"/> 的单卡实现（一条动作里可能引用两张卡）。</summary>
    private void CorrectOneIdentity(WireAction a, ReplayData replay, GameState state,
                                    int cardId, string code, bool isDefender)
    {
        if (cardId <= 0 || state.ById(cardId) is not { } card)
        {
            return;   // 内核还没有这张卡 ⇒ 下面 `ResolveCard` 会**直接按码**建出来，无需校正
        }

        if (replay.ById.ContainsKey(cardId))
        {
            return;   // 边界 1：快照卡，身份不由动作流决定
        }

        if (!_db.DeckCodeIds.TryGetValue(code, out string? actionName))
        {
            return;   // 码不在 deckCodeIDsTable2 里 —— `CheckIdentity` 会如实报出来
        }

        string kernelName = card.Name;
        string? kernelCode = _db.DeckCodeFor(kernelName) ?? _db.DeckCodeFor(card.Definition.Name);

        // 边界 3：基础名相同（`xxx` vs `xxx_bal`）不算身份不一致。
        // ⚠️ 但这也**算确认**：动作流说了它是什么，而且与内核一致 ⇒ 这张卡可信。
        if (kernelCode is not null
            && string.Equals(CardDatabase.ResolveBaseName(kernelName),
                             CardDatabase.ResolveBaseName(actionName), StringComparison.Ordinal))
        {
            state.MarkIdentityVerified(cardId);
            return;
        }

        // 边界 2：内核这张卡没有卡组码 ⇒ 无可比对基准
        if (kernelCode is null)
        {
            IdentityEvents.Add(new IdentityEvent(a.ActionId, a.TurnNumber, a.ActionType, cardId,
                kernelName, actionName, code, null, false, "内核卡组码未知（无基准，未校正）"));
            return;
        }

        bool gated = IdentityCorrectionOnly is { } only
                     && !only.Contains(kernelName)
                     && !only.Contains(actionName);

        bool corrected = false;
        string note;
        if (!IdentityCorrection)
        {
            note = "校正开关关闭";
        }
        else if (gated)
        {
            note = "被单卡门控过滤";
        }
        else if (_db.Find(actionName) is not { } def)
        {
            note = $"码 {code} 的卡名 {actionName} 不在卡库里";
        }
        else
        {
            card.Reidentify(actionName, def);
            corrected = true;
            note = isDefender ? "已校正（防御者）" : "已校正";
        }

        // 不管有没有校正，**动作流都已经用卡组码声明了这张卡是什么** ⇒ 身份可信，
        // 安全网可以放它过去（见 `GameState.IsIdentityTrusted`）。
        state.MarkIdentityVerified(cardId);

        IdentityEvents.Add(new IdentityEvent(a.ActionId, a.TurnNumber, a.ActionType, cardId,
            kernelName, actionName, code, kernelCode, corrected, note));

        // ★★ **游标失同步信号** —— 不能只当成"一条没应用的失败"。
        //
        // 判据：动作引用的 cardID 在内核里的身份与动作自带的卡组码不符。
        // 如果 RNG 复刻正确、消费点顺序也正确，内核选中的那张卡**本来就该**与客户端一致；
        // 不符只可能来自三种情况：
        //   ① 内核漏了/多了某个随机消费点 ⇒ `cardsRandomStream` 游标错位；
        //   ② 某个效果没实现（静默 no-op），它本该消耗的随机数没消耗；
        //   ③ 候选集的**顺序**与客户端不同（同一次消费、同一个下标，取到不同的卡）。
        // 三种都是"我们的执行路径与客户端不一致"的直接证据，所以写进
        // `UnimplementedCalls`，让审计的 ⑥ 段能看见它 —— 而不是静默继续。
        state.UnimplementedCalls[$"<rng-cursor-desync:{kernelName}->{actionName}:消费{state.Random.ConsumedCount}>"]
            = state.UnimplementedCalls.GetValueOrDefault(
                $"<rng-cursor-desync:{kernelName}->{actionName}:消费{state.Random.ConsumedCount}>") + 1;
    }

    /// <summary>
    /// 这张牌**需不需要目标** —— 判据是卡定义里调用了 `GetTargetedCard`
    /// （与 `NnPolicy.NeedsTarget` / `GreedyBot.NeedsTarget` 同源：那是卡数据的事实，
    /// 不是规则判据）。
    /// </summary>
    private static bool NeedsTarget(CardInstance card)
        => card.Definition.ExternalCalls.Contains("GetTargetedCard", StringComparer.Ordinal);

    /// <summary>拿到（必要时创建）动作引用的卡，归属 <paramref name="owner"/>。</summary>
    private CardInstance? ResolveCard(WireAction a, ReplayData replay, Side owner, Side foe, GameState state)
    {
        if (state.ById(a.CardId) is { } existing)
        {
            return existing;
        }

        if (_cardAliases.TryGetValue(a.CardId, out var aliased))
        {
            return aliased;
        }

        string? name = NameOf(a, replay, 0) ?? NameOf(a, replay, 1);
        if (name is null)
        {
            return null;
        }

        // Some replay streams renumber an effect-generated card between its creation and
        // later actions. Reuse only an unambiguous same-owner, same-definition instance.
        var aliases = state.Cards(owner)
            .Where(c => c.CardId != a.CardId
                && c.Name == name
                && state.GeneratedCardIds.Contains(c.CardId)
                && !_cardAliases.Values.Contains(c))
            .ToArray();
        if (aliases.Length == 1)
        {
            _cardAliases[a.CardId] = aliases[0];
            return aliases[0];
        }

        // 归属：PC/ML/AC 的 0 号键都是行动方自己的卡
        RecordPlaceholderCard(a, state, a.CardId, name, "ResolveCard");
        return state.CreateWithId(name, owner, a.CardId, owner.DeckOf(), 0);
    }

    /// <summary>
    /// **兜底造卡留痕** —— 动作引用了一个内核里**根本没有**的 cardID 时，这里记一笔。
    ///
    /// ## 为什么必须记（而不是静默造出来）
    ///
    /// 走到这里只有一种可能：**客户端用 <c>1000×回合+序号</c> 发了一张卡，我们没发**。
    /// 于是我们凭空在**牌库**里造一张占位卡去满足那条动作，而动作真正想操作的那张卡
    /// （客户端那边在**手牌 / 场上**）在我们这里不存在。后果有三层，而且全都伪装成
    /// 「别的 bug」：
    /// <list type="number">
    /// <item>这条动作看起来"应用成功"（不记失败），**漂开点被推后** ——
    ///   实测 773639 关掉身份校正时 `#80 t18 PC 17002`（客户端手里那张 `card_event_radar`）
    ///   就是这样被"造"进牌库的，审计 ⑤ 一条都不报。</item>
    /// <item>内存里多出一张卡 ⇒ 之后 `NextCardId` 的**避让循环**会跳号
    ///   （见 <see cref="GameState.NextCardId"/>），我们发出来的号与客户端越差越远。</item>
    /// <item>手牌/场面数不对 ⇒ 表现为"半场已满""攻击者不在场上"这类**次级**失败。</item>
    /// </list>
    ///
    /// 所以这里**照旧把卡造出来**（不造的话后面每条动作都会跟着炸，信号会被淹没），
    /// 但把它写进 <see cref="GameState.UnimplementedCalls"/>，让审计显式报出来 ——
    /// 这正是「发号漏了一张」的最直接证据。
    /// </summary>
    private static void RecordPlaceholderCard(WireAction a, GameState state, int cardId,
                                             string name, string where)
    {
        string key = $"<unresolved-cardid:{cardId}={name}>";
        state.UnimplementedCalls[key] = state.UnimplementedCalls.GetValueOrDefault(key) + 1;
        state.TraceRandom($"PLACEHOLDER #{cardId} {name} @{where} (动作 #{a.ActionId} t{a.TurnNumber} {a.ActionType})");
    }

    /// <summary>AC 的目标（1 号键 = 防御者 cardID，卡码在 3 号键）。</summary>
    private CardInstance? ResolveTarget(WireAction a, ReplayData replay, Side owner, Side foe, GameState state)
    {
        if (state.ById(a.SecondId) is { } existing)
        {
            return existing;
        }

        if (_cardAliases.TryGetValue(a.SecondId, out var aliased))
        {
            return aliased;
        }

        string? name = NameOf(a, replay, 1);
        if (name is null)
        {
            return null;
        }

        // 同上：AC 的防御者也不该是"我们没有的卡"，留痕（见 RecordPlaceholderCard）。
        RecordPlaceholderCard(a, state, a.SecondId, name, "ResolveTarget");
        return state.CreateWithId(name, foe, a.SecondId, foe.DeckOf(), 0);
    }

    private string? NameOf(WireAction a, ReplayData replay, int codeIndex)
    {
        var codes = a.CardCodes;
        if (codeIndex < codes.Count && _db.DeckCodeIds.TryGetValue(codes[codeIndex], out string? name))
        {
            return name;
        }

        return null;
    }
}
