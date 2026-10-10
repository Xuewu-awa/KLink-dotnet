using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// <see cref="CardApi"/> 的**名字派发层**：把 Blueprint 字节码里的调用名 + 位置参数，
/// 适配到原语方法上。
///
/// 参数形状不是猜的，是从全部 35,360 个调用点统计出来的
/// （见 <c>klink bot/tools/analyze-call-shapes.py</c>）。例如：
///
/// <code>
/// ChangeDefense   (targetCard, instigatorID, 数值, changeType, silent, out)  ← 数值恒在 index 2
/// DamageCard      (targetCard, 伤害,    attackerID, bool, bool, bool, out)
/// DrawCardsFromDeckBySide (instigatorID, side, 张数, bool, bool, out, ?)     ← side=1, 张数=2
/// GetCardsOnBoardBySide   (side, bool, bool, out cards)                      ← 输出在 index 3
/// JSON_SetBool    (card, key, value, out)
/// </code>
///
/// 没实现的名字返回 <c>handled=false</c>，由调用方计入未实现统计 —— 这就是进度指标。
/// </summary>
public sealed partial class CardApi
{
    /// <summary>调用名 → 处理函数。返回 null 表示「这个调用没有返回值」。</summary>
    private readonly Dictionary<string, Func<EffectContext, object?, object?[], object?>> _dispatch;

    private Dictionary<string, Func<EffectContext, object?, object?[], object?>> BuildDispatch() =>
        new(StringComparer.Ordinal)
        {
            // ---------------- 查询 / 判定（接收者是主语）----------------
            // ⚠️ 这一族必须走 `SelfArg`，不能只看 `receiver`：
            //    Blueprint 里 `self.IsXxx()` 这种**隐式 self** 调用编译出来是
            //    `{"Inst":"FinalFunction","Function":"IsXxx"}` —— **没有 `Context` 包装**，
            //    所以 IR 里根本没有 `recv`，VM 传进来的 receiver 是 null。
            //    实测 `card_unit_214th_amur` 的 i=848 正是这种形状
            //    （紧挨着的 i=877 `GetAllCardsOnBoard` 反而带 `recv=cardFunction`）。
            //    旧写法 `AsCard(r) is {} && …` 直接返回 false，把整条
            //    `OnEnterPlay` 的判据短路掉 —— 光环看起来「什么都没做」。
            ["IsUnit"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsUnit(x),
            ["IsInfantry"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsInfantry(x),
            ["IsTank"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsTank(x),
            ["IsArtillery"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsArtillery(x),
            ["IsAirUnit"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsAirUnit(x),
            ["IsOrder"] = (c, r, a) =>
            {
                var t = SelfArg(c, r, a);

                // 诊断开关关闭时不要插值（IsOrder 是极高频谓词）
                if (AuraTrace is not null)
                {
                    AuraTrace.Add($"      IsOrder(recv={(r as CardInstance)?.Name ?? "null"}, " +
                                  $"self={c.Self?.Name}, target={c.Target?.Name ?? "null"}, " +
                                  $"trigger={c.Trigger?.Name ?? "null"}) → {t is not null && IsOrder(t)}");
                }

                return t is not null && IsOrder(t);
            },
            ["IsLocation"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocationCard(x),
            ["IsLocatedOnBoard"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedOnBoard(x),
            ["IsLocatedInHand"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedInHand(x),
            ["IsLocatedInDeck"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedInDeck(x),
            ["IsSideActive"] = (c, r, a) => IsSideActive(SideArg(r, a, 0)),
            // `IsTopDeckNavy(side, out isNavy)`：蓝图先取指定阵营牌库的第 0 张，
            // 再用 `getHasGameplayTag(subtype.navy)` 判定。空牌库必须返回 false，
            // 且 side 是显式参数，不能用 receiver 的阵营替代。
            ["IsTopDeckNavy"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                var top = c.State.Deck(side).FirstOrDefault();
                return top is not null && HasGameplayTag(top, "subtype.navy");
            },
            ["IsValid"] = (c, r, a) => AsCard(a.FirstOrDefault()) is not null || SelfArg(c, r, a) is not null,
            // `IsCardReserved(InCardName, out IsReserved)` ——
            // `BP_CardFunctions.g.cs:24131-24151` 只是把
            // `NotifyCheckCardReserved(CardFunctionsNotifier, name)` 的答复原样转出；
            // 真正的判据在 `BP_Logic.HandleCheckCardReserved` → `UtilityFunctions.isCardReserved`：
            // 先扫服务端下发的 `DSession.cards_reserve_changes` 调档表，**没命中就回退到
            // 卡自己的 CDO `isReserved` 字段**（`_deps/UtilityFunctions.g.cs:6651-6672`）。
            // 本内核没有 DSession ⇒ 整表按 CDO 字段算，出处与偏差面见 `CardPoolTable.Reserved`。
            //
            // ⚠️ 这个值有**两个**用途，别只看第一个：
            //   ① 卡自己的效果问"我这张卡被预备了吗"；
            //   ② `GetAllActiveStaticCards(false, IsCardReserved(self.name), out cards)`
            //      —— 它的返回值就是候选池的 `includeReserved` 开关
            //      （例：`card_event_atlantic_convoy.g.cs:182-184`）。
            //      原来恒 false ⇒ 预备卡永远进候选池（见 `StaticCardPool`）。
            ["IsCardReserved"] = (c, r, a) => CardPoolTable.IsReserved(StrArgOrNull(a, 0)),
            // `IsForecastCard` is the weather-card predicate, not the
            // `ability.forecast` keyword carried by cards that trigger Forecast.
            // The Blueprint `GetAllForecastCards` implementation uses these same
            // three subtype tags: rain, storm, and sunny.
            ["IsForecastCard"] = (c, r, a) => SelfArg(c, r, a) is { } forecast
                && IsForecastCard(forecast),
            ["HasIntel"] = (c, r, a) => SelfArg(c, r, a) is { } x && JsonGetBool(x, "intel"),

            // ⚠️ 这两个是**同形参数位错**（审计 §5.2），一起修：
            // 权威签名（`BaseCardObject.h:946/943`）：
            //   `HasCustomAbility(const FString& ability, bool& doesIt)`          —— 被检查的卡 = **接收者**
            //   `HasCustomAbilityFromCard(const FString& ability, int32 giverID, bool& doesIt)` —— 同上
            // 旧实现的两处错：
            //   1. `HasCustomAbility` 没把能力名传下去（`CardApi.HasCustomAbility` 的
            //      `ability` 默认 null）⇒ 变成"这张卡有**任意**自定义能力吗"，
            //      11 个调用点里 `cantRetreat`/`cantBePinned`/`lethal`/`alpine`/
            //      `destroyEndOfTurn` 混用时会互相误判。
            //   2. `HasCustomAbilityFromCard` 拿 `a[0]` 当卡 —— 而 `a[0]` 是**能力名字符串**。
            //      IR 实测（`out/audit/p0-argshapes.py`，52 个调用点，49 有 recv / 3 隐式）：
            //      `a[0]` = `"trigger"`×20、`"destruction"`×13、`"passive"`×8、
            //      `"ignoreCantAttack_location"`×4、`"targetAbility"`×3、`"excess"`×2 …
            //      `AsCard("trigger")` = null ⇒ **恒 false**。
            //      它的调用点之一是 `ExecuteOnCardDestroyedFunction` 的
            //      `BooleanOR(hasDestruction, HasCustomAbilityFromCard("destruction", …))`
            //      ⇒ 靠效果"获得摧毁能力"的卡全废。
            //      `a[1]` 是 `giverID`（IR 里是 `cardID` 变量）—— "谁给的"这层语义
            //      内核的 `CustomAbility` 是单值字段、没有来源，**不实现**（不猜）。
            ["HasCustomAbility"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && HasCustomAbility(x, StrArgOrNull(a, 0)),
            ["HasCustomAbilityFromCard"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && HasCustomAbility(x, StrArgOrNull(a, 0)),
            ["DoesSideControlTheFrontline"] = (c, r, a) => DoesSideControlTheFrontline(SideArg(r, a, 0)),
            // BP_CardFunctions::GetSupportLineLocationBySide L_0033/L_0075.
            // Invalid sides must not fall back to the controller or right-hand side.
            ["GetSupportLineLocationBySide"] = (c, r, a) => IntArg(a, 0) switch
            {
                (int)Side.Left => (int)CardLocation.BoardHqLeft,
                (int)Side.Right => (int)CardLocation.BoardHqRight,
                _ => (int)CardLocation.NotAvailable,
            },
            // `GetDeckLocationBySide(side, out deckLocation)` is a pure Blueprint
            // enum mapping: Left -> DeckLeft, every other supported side -> DeckRight.
            ["GetDeckLocationBySide"] = (c, r, a) =>
                SideArgOrNull(a, 0) is Side.Left
                    ? (int)CardLocation.DeckLeft
                    : (int)CardLocation.DeckRight,
            // ⚠️⚠️ 形状修正（2026-10-02）：`IsSameSideUnit` 是 **BaseCardObject 的成员函数**，
            //     权威形状是 `Context{卡}.IsSameSideUnit(side)` —— **接收者才是被查的那张卡**，
            //     唯一的实参是 `side`（int），第 2 项是 out 槽。
            //
            // 三层互相独立的证据：
            // ① IR：19 个调用点**全部**是这个形状 —— `a[0]` = `{"var":"side"}`×14 /
            //    `GetOppositeSide(...)`×5，`a[1]` = out 槽 `CallFunc_IsSameSideUnit_isIt`；
            //    `recv` 全是卡变量（`K2Node_Event_cardPlayed`×7 / `CallFunc_GetCardFromID_card`×7 /
            //    `K2Node_Event_cardDestroyed`×4 / `tempCard`×1）。
            //    取证：`card-ir.json` 的 `card_location_british_scen4` i=4008/4027/4152。
            // ② 直译产物：`out/Generated-gap/_deps/*.g.cs` 里 17 种不同的调用形状
            //    **无一例外**是 `H.Call("IsSameSideUnit", [卡, side, out])`（直译器把接收者
            //    前置成 args[0]）—— 全卡池**不存在** `(卡, 卡)` 形状。
            // ③ 语义：`card_event_air_corps_ferrying.CanPlayFromHand` 的全部判据只有
            //    `GetTargetedCard` + `IsSameSideUnit(target, side)`，失败时 reason 是
            //    `"friendly_unit"`（卡面「Give a friendly unit +1+1」）⇒ 必须**同时**含
            //    「同阵营」与「是单位」，只判阵营会放行非单位目标。
            //
            // 旧实现 `a.Length >= 2 && AsCard(a[0]) is {} p && AsCard(a[1]) is {} q && IsSameSideUnit(p, q)`
            // 把两个实参都当卡读 ⇒ `a[0]` 是 int、`a[1]` 是 out 槽 ⇒ **19/19 恒 false**。
            //
            // 影响面（分「已生效」和「潜伏」两档，不要混为一谈）：
            // · **已生效 19 个 IR 调用点 / 17 张卡**（`card-ir.json` 里能跑到的）——
            //   `OnOtherCardEnterPlay`×6 / `OnOtherCardDestroyed`×4 / `OnOtherCardPlayedFromHand`×2 /
            //   `OnEndOfTurn`×1 / 卡自己的函数体×6。例：`card_location_british_scen4` 的
            //   `OnOtherCardEnterPlay` 两条改行动费分支恒跳过（i=4008→4027 / 4152）。
            // · **潜伏 131 个调用点**：全字节码统计（`decompiled/cards.all.json`）
            //   `IsSameSideUnit` 共 148 张卡 / 152 个调用点，其中 **131 个在 `CanPlayFromHand`**、
            //   3 个在 `ShouldHighlightInHand` —— 而**本内核目前根本没有实现 `CanPlayFromHand`
            //   这道客户端出牌合法性门**（全仓 grep 只有注释提到它）。所以那 131 处
            //   现在跑不到；等哪天实现了那道门，这个修好之前它们会**一律拒绝**
            //   （`card_event_air_corps_ferrying` 的卡面是「Give a friendly unit +1+1」，
            //   门恒假 ⇒ 那张牌永远打不出去）。**修在这里，是为了那道门落地时不用再翻一遍。**
            //
            // ⚠️ 阵营**不做兜底**（不用 `SideArg`）：`SideArg` 读不到时会退回
            //    `receiver.Owner`，那正好等于"卡属于它自己的阵营"⇒ 判据**恒真**。
            //    "查 A 是否等于 B"这类原语，兜底值比没有值更危险。
            ["IsSameSideUnit"] = (c, r, a)
                => SideArgOrNull(a, 0) is { } side
                   && SelfArg(c, r, a) is { } card
                   && IsSameSideUnit(card, side),

            // ⚠️ **隐式 self**（审计 §5.1）。权威签名 `BaseCardObject.h:841`：
            //     `IsVeteran(bool ignoreSuppress, bool& isIt)` —— 零个"是哪张卡"的入参。
            // IR 实测：75 个调用点里 **70 个没有 recv**（`{"bool": false}` + out 槽），
            // 旧实现读 `AsCard(r)` ⇒ r 为 null ⇒ **恒 false**（41 张卡的老兵判据全反）。
            // 正确写法与 `getTotalAttack`/`getTotalDefense` 同形：`SelfArg`（兜底 c.Self）。
            // `ignoreSuppress`（a[0]）的语义**读不出来**（`BaseCardObject.h` 只有签名），
            // 不实现、也不假装实现。
            ["IsVeteran"] = (c, r, a) => SelfArg(c, r, a) is { } v && v.Keywords.Contains(Keyword.Veteran),
            // The veteran upgrade queries are backed by the card data's explicit
            // `_vet` variants.  `CardDatabase.Find` intentionally falls back from
            // `foo_vet` to `foo`, so use an exact-name scan here; otherwise every
            // card would appear to have a veteran upgrade.
            ["getHasVeteranUpgrade"] = (c, r, a) =>
            {
                var card = SelfArg(c, r, a);
                return card is not null && FindVeteranDefinition(c.State.Database, card) is not null;
            },
            ["getStaticVeteranUpgrade"] = (c, r, a) =>
            {
                var card = SelfArg(c, r, a);
                if (card is null)
                {
                    return null;
                }

                // Cards without a distinct `_vet` definition keep their base
                // static values when an external effect has already made them
                // Veteran (for example 37mm M1 AA GUN).
                CardDefinition definition = FindVeteranDefinition(c.State.Database, card)
                    ?? card.Definition;
                return TemplateInstance(definition, card.Owner);
            },
            ["GetKreditsBySide"] = (c, r, a) => c.State.Kredits(SideArg(r, a, 0, c.Controller)),
            ["GetMaxKreditsBySide"] = (c, r, a) => c.State.MaxKredits(SideArg(r, a, 0, c.Controller)),
            // BP_CardFunctions forwards these pure queries to the
            // BP_GameState_Battle counters. Invalid enum values leave the
            // Blueprint out parameter at its default zero.
            ["GetHQ_DamagedAmountThisTurnBySide"] = (c, r, a) =>
                c.State.GetHQDamagedAmountThisTurn(SideArg(r, a, 0, c.Controller)),
            ["GetOperationKreditsSpentThisTurn"] = (c, r, a) =>
                c.State.OperationKreditsSpentThisTurn,
            // `card_unit_seagull.GetReducedDamage(damage, out DamageReduction)`。
            // 蓝图函数体：Clamp(4 - GetHQ_DamagedAmountThisTurnBySide(self.side), 0, damage)。
            // 这是卡自己的局部函数，但其语义只依赖本方 HQ 的回合计数和输入伤害，
            // 因此可以忠实地作为通用派发键实现，不需要猜测调用方的 VM 局部槽。
            ["GetReducedDamage"] = (c, r, a) =>
            {
                int damage = IntArg(a, 0);
                Side side = c.Self?.Owner is Side owner and (Side.Left or Side.Right)
                    ? owner : c.Controller;
                int remaining = 4 - c.State.GetHQDamagedAmountThisTurn(side);
                return Math.Clamp(remaining, 0, damage);
            },
            // Despite its historical name, the Blueprint function returns
            // the boolean IsFrontlineLimited flag, not the numeric capacity.
            ["getFrontlineLimit"] = (c, r, a) => c.State.IsFrontlineLimited,

            // ---------------- 取值 / 选择器 ----------------
            // ⚠️ `GetOppositeSide` **没有入参** —— 它的"我方"取自卡本身。
            //    证据：扫描全部 2053 张卡的 Kismet 字节码，
            //    `GetOppositeSide` 出现 756 次，**每一次的参数列表都只有 1 项**，
            //    而且那一项是 out 槽 `CallFunc_GetOppositeSide_oppositeSide`
            //    （对比 `GetLocationCardBySide` 3 项、`GetCardsOnBoardBySide` 4 项，
            //    入参都老老实实列在参数表里）。见 tools/analyze-call-arity.py。
            //
            //    原先实现读 `a[0]`，拿到的是**尚未赋值的 out 槽** → `Side.NotAvailable`
            //    → 后续 `GetLocationCardBySide(NotAvailable)` 返回 null →
            //    `DamageCard(null)` 静默无操作。影响面很大（463 张卡用到它，
            //    是第 3 常用的外部调用）。实测症状：card_unit_10_5_cm_lefh 的
            //    「Deployment: 对敌方 HQ 造成 2 点伤害」完全不生效。
            ["GetOppositeSide"] = (c, r, a) => (int)SelfSide(c).Opposite(),
            // The Blueprint signature takes an integer card ID, but a few local
            // event paths receive the freshly spawned CardInstance directly from
            // a simplified spawn primitive.  Accept both representations here;
            // the native path remains the integer-ID case.
            ["GetCardFromID"] = (c, r, a) => AsCardOrId(c, a.ElementAtOrDefault(0)),
            // `Get{Left,Right}MostCardInHand(self, out found, out card)`。
            // GameState.Hand uses the same location-number ordering as the client.
            ["GetLeftMostCardInHand"] = (c, r, a) => GetEdgeCardInHand(c, r, a, rightMost: false),
            ["GetRightMostCardInHand"] = (c, r, a) => GetEdgeCardInHand(c, r, a, rightMost: true),

            // ⚠️ `GetStaticCard` **不在这里** —— 它是
            // `/Script/kards.FunctionLibrary` 的原生函数，在 IR 里是 `CallMath` 形状，
            // `KismetVm.Eval` 先判 `expr.Math` 就转进 `EvalMath` 了，
            // 所以放进本表**永远不会被调用到**（那是「看起来实现了、其实没有」的坑）。
            // 实现落点：`KismetVm.EvalMath` 的 `case "GetStaticCard"`。

            ["GetLocationCardBySide"] = (c, r, a) => GetLocationCardBySide(SideArg(r, a, 2)),

            // ⚠️ **可选参数必须读**。权威签名（`CardFunctionsStub.h:437`）：
            //     `GetCardsOnBoardBySide(ESideEnum side, bool unitsOnly, bool includeCovertCards, TArray& Cards)`
            // 旧实现只读 `a[0]`（side），把 `unitsOnly` / `includeCovertCards` 整个丢掉 ——
            // 252 个调用点全部按"所有卡"返回。IR 实测（`out/audit/p0-argshapes.py`）：
            // `a[1]` = `true`×213 / `false`×39，`a[2]` = `false`×243 / `true`×9。
            //
            // `unitsOnly=false` 那一支**必须包含非单位卡**，证据（`card_unit_3rd_maizuru_snlf`
            // 的 `OnPlayedFromHand`，card-ir.json 的 steps 0-6）：
            // <code>
            // step 1  GetCardsOnBoardBySide(oppositeSide, unitsOnly: false, false, out cards)
            // step 2  GetRandomCard(cards, false, out randomCard)
            // step 3  DamageCard(randomCard, 1, self, …)
            // step 4  IsUnit(recv = randomCard)  →  step 5  jumpIfNot  →  step 6  PinUnit(randomCard)
            // </code>
            // 也就是说这张卡是"随机打敌方场上一张牌 1 点，**如果它是单位**再钉住它" ——
            // 这个 `IsUnit` 分支只有在结果集**可能含非单位**时才有意义。
            // 棋盘上唯一的非单位卡就是 HQ（位置卡），所以 `unitsOnly=false` ⇒ 含该方 HQ。
            //
            // `includeCovertCards` 读进来但**当前无效果**：本内核还没有建模 Covert
            // （P1，见审计 §4「隐蔽 Covert」），没有"未揭示的隐蔽卡"这个状态可过滤。
            // 记一笔未实现，别让它静默（数字小，不会淹没别的东西）。
            ["GetCardsOnBoardBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                if (TruthyArg(a, 2))
                {
                    c.State.UnimplementedCalls["GetCardsOnBoardBySide<includeCovertCards>"] =
                        c.State.UnimplementedCalls.GetValueOrDefault("GetCardsOnBoardBySide<includeCovertCards>") + 1;
                }

                // ⚠️ `unitsOnly=false` 时 **HQ 要留在它自己的插入位置（= 最前）**，
                //    **不能**追加到末尾：客户端 `AllCardsInBattle` 是「只增不删」的映射，
                //    而 HQ 是**开局就创建**的 ⇒ 它排在**最前**。
                //    旧实现是 `Board(side).Concat([HQ])` ⇒ HQ 落到最后一位
                //    ⇒ 同一次消费、同一个下标会取到不同的卡。
                //    见 `GameState.BattleCardsInOrder` 与 `BoardInBattleOrder` 里的蓝图依据。
                return TruthyArg(a, 1)
                    ? GetCardsOnBoardBySide(side).ToList()
                    : c.State.BattleCardsInOrder(side).ToList();
            },
            ["GetCardsInHandBySide"] = (c, r, a) => GetCardsInHandBySide(SideArg(r, a, 0)).ToList(),
            // ⚠️ 出参是 `TArray<int> deckCardIDs`（卡 **ID**），不是卡实例 ——
            //    46 张调用它的卡的用法清单见 `CardApi.GetDeckBySide` 的注释。
            ["GetDeckByside"] = (c, r, a) => GetDeckBySide(SideArg(r, a, 0)),

            // 权威签名（`CardFunctionsStub.h:455/464`）：
            //   `GetAllUnitsOnBoard(bool includeCovertCards, TArray& Cards)`
            //   `GetAllCardsOnBoard(bool includeCovertCards, TArray& Cards)`
            // 两个函数的**唯一**区别就是"单位"与"卡" —— 说明"场上所有卡"**严格多于**
            // "场上所有单位"，多出来的只能是 HQ（棋盘上唯一的非单位卡）。
            // 旧实现把 `a[0]` 当成 `includeHq` 解释（注释里还写了推理），
            // 于是 90 个传 `false` 的调用点拿到的是"只有单位"，少了 HQ。
            ["GetAllUnitsOnBoard"] = (c, r, a) => GetAllUnitsOnBoard().ToList(),
            ["GetAllCardsOnBoard"] = (c, r, a) => GetAllCardsOnBoard().ToList(),
            ["GetAllCardsInFrontline"] = (c, r, a) => GetAllCardsInFrontline().ToList(),
            ["GetAllCards"] = (c, r, a) => GetAllCards().ToList(),
            // SDF's private ApplyAndCorrectBuff counts the controller's board Guards.
            ["CountFriendlyGuardUnits"] = (c, r, a) => c.State.Board(c.Controller)
                .Count(card => IsUnit(card) && card.Keywords.Contains(Keyword.Guard)),
            // `Get_X_AndMoreAttackCardsOnBoard(side, out cardsIDs, attack, includeCovert)`。
            // 蓝图过滤顺序是单位、存活防御、在场、阵营、隐蔽可见性和攻击阈值，
            // 最终回写的是 cardID。`a[1]` 是 out 槽，故 attack/includeCovert 在 2/3。
            ["Get_X_AndMoreAttackCardsOnBoard"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                int attack = IntArg(a, 2);
                bool includeCovert = TruthyArg(a, 3);
                if (includeCovert)
                {
                    c.State.UnimplementedCalls["Get_X_AndMoreAttackCardsOnBoard<includeCovert>"] =
                        c.State.UnimplementedCalls.GetValueOrDefault("Get_X_AndMoreAttackCardsOnBoard<includeCovert>") + 1;
                }

                return GetXAndMoreAttackCardsOnBoard(side, attack, includeCovert);
            },
            // ⚠️ 这两个是 **`BaseCardObject` 的原生成员函数**（UHT 签名
            //    `void getTotalAttack(int32& totalAttack)` / `void getTotalDefense(int32& totalDefense)`），
            //    语义是「**接收者那张卡自己的**总攻击 / 总防御」，
            //    **不是**「某个阵营场上所有单位之和」。
            //
            //    证据三条（`getTotalAttack` 与 `getTotalDefense` **逐条同形**，下面两个数字
            //    各自独立统计，不是照抄）：
            //    1. 参照实现：`ref/kards-sim/KardsSim/Bridge/EngineHost.cs`
            //       `["getTotalDefense"] = (h, a) => … h.Card_(a[0]).TotalDefense`（:1660-1664）、
            //       `["getTotalAttack"]  = (h, a) => Out(a, h.Card_(a[0]) is { } tc ? tc.TotalAttack : 0)`
            //       （:1099）—— 两条的主语都是**接收者那张卡**。
            //    2. UHT 签名（`<kards-src>\Source\kards\Public\BaseCardObject.h`）：
            //       `:982 void getTotalDefense(int32& totalDefense);`
            //       `:985 void getTotalAttack(int32& totalAttack);`
            //       两条同为 `UFUNCTION(BlueprintCallable, BlueprintPure)` 的 **`UBaseCardObject`
            //       成员函数**、**零个形式参数**（`int32&` 是 out 参数，不进参数表）。
            //    3. 全卡池 IR 调用点的接收者统计（`out/gta/analyze-gta.py`）：
            //       `getTotalDefense` ubergraph 113 个调用点 / 87 个带明确的卡接收者 / 26 个隐式 self；
            //       `getTotalAttack`  ubergraph  46 个调用点 / 34 个带明确的卡接收者 / 12 个隐式 self
            //       （接收者形态：`tempCard` 8、`K2Node_Event_targetCard` 6、数组元素 11 …）。
            //       没有任何一个调用点的语义是「己方场上单位攻击/防御之和」。
            //
            //    旧实现 `GetTotalDefense(SelfSide(c))` / `GetTotalAttack(SelfSide(c))` 丢掉接收者，
            //    返回 `State.Board(side).Sum(...)` —— 而 `Board(s)` **还排除了 HQ**。
            //    于是「读 HQ 防御」被读成「己方场上单位防御之和」：英联邦的
            //    `己方 HQ 防御 >= 30` 在空场时恒等于 `0 >= 30` = 假 → 伤害恒为 0。
            //    原先那条「零入参 ⇒ 自己的场面总和」的注释是**只按参数个数统计**得出的
            //    （见 tools/check-zero-input-dispatch.py）—— 接收者不在参数表里，
            //    所以那次统计看不见它。
            //
            //    `getTotalAttack` 的旧实现同样是「己方场面攻击之和」：
            //    `card_unit_red_bull`（卡面 "double the attack of this unit."）的
            //    `OnStartOfTurn` 是 `ChangeAttack(self, getTotalAttack(), +)`，
            //    旧实现会把**整条己方场面的攻击之和**加到它自己头上。
            //    回归用例见 tools/BotSim/SelfTest.cs 的 RedBullDoublesOwnAttack。
            ["getTotalAttack"] = (c, r, a) => SelfArg(c, r, a)?.Attack ?? 0,
            ["getTotalDefense"] = (c, r, a) => SelfArg(c, r, a)?.Defense ?? 0,
            ["getCardsBuffedByThisCard"] = (c, r, a) => GetCardsBuffedByThisCard(c, r, a),
            ["GetTurnNumber"] = (c, r, a) => GetTurnNumber(),
            ["GetRandomCard"] = (c, r, a) => GetRandomCard(AsList(a.FirstOrDefault())),
            // ⚠️⚠️ **两个出参，不是返回值**（2026-10-02，目标合法性门落地时发现）。
            //
            // 权威签名（调用点形态，全卡池 **431 处全部同形**）：
            //   `GetTargetedCard(out bool hasTarget, out UBaseCardObject* card)`
            // IR 形状（`card-ir.json` 的 438 个 `CanPlayFromHand` 里逐个核对过）：
            //   `{"op":"call","fn":"GetTargetedCard","recv":{"var":"cardFunction"},
            //     "args":[{"self":true}, {"var":"CallFunc_GetTargetedCard_hasTarget"},
            //                          {"var":"CallFunc_GetTargetedCard_card"}],
            //     "outs":[{"param":1,...},{"param":2,...}]}`
            // —— 两个 out 槽，**没有**返回值。
            //
            // 旧写法 `=> c.Target` 只写第一个槽（`KismetVm.cs:650` 的单值约定）：
            //   · `hasTarget` 拿到的是**卡对象**（truthy 恰好也对，掩盖了问题）；
            //   · `card` 永远是 **null** ⇒ 紧接着的 `IsAirUnit(recv=card)` /
            //     `IsVeteran(recv=card)` / `IsSameSideUnit(recv=card)` **一律恒假**。
            // 因为 `GetTargetedCard` 只出现在 `CanPlayFromHand` 里，而那道门在本内核
            // 此前**根本没实现**（`card-ir.json` 里 0 个调用点），所以这个 bug 一直没暴露；
            // 一旦接上目标门，它的症状会是「**所有需要目标的牌都指不了任何目标**」。
            // 多输出约定见 `KismetVm.cs:621-647`：返回 `object?[]` 即按下标写回各 out 槽。
            ["GetTargetedCard"] = (c, r, a) => new object?[] { c.Target is not null, c.Target },
            ["GetCard"] = (c, r, a) => AsCard(r) ?? c.Target,

            // ── ★★ `CanSelectAsTarget`（规则库的目标合法性门，2026-10-02）──────────
            // 出处：`ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs:1052-1219`
            //（同一份规则库里还有 `CanAttack`，内核早已实现，两者共用 `CanBeTargetted`
            //  这一族子门）。完整语义、以及**它管不到什么**见
            //  `CardApi.CanSelectAsTarget` 的注释。
            //
            // 为什么它**不是**「目标类型」那道门：玩家报告的「只能指定空军 / 老兵」
            // 不在这个函数里 —— 那在**每张卡自己的 `CanPlayFromHand`** 里
            //（客户端选目标的主循环 `_deps/BP_Logic.g.cs:1235-1355` 就是
            //  `targetOverride=候选 → CanPlayFromHand → CanSelectAsTarget`）。
            // 两道门都要过，见 `CardApi.CanTarget`。
            //
            // ⚠️ 注册它**不会**改变派发表缺口的指纹：IR 里 `CanSelectAsTarget`
            //    的调用点 = **0**（`card-ir.json` 全文 grep），所以 `DispatchGap.Compute`
            //    的 `implemented.Contains(fn)` 分支根本不会碰到它。注册是为了让
            //    「名字 → 实现」这件事在表里可查，不是为了让数字好看。
            ["CanSelectAsTarget"] = (c, r, a) => InvokeCanSelectAsTarget(c, a),

            // ── `AddKreditsTax(card, costToAdd, instigatorID, out qqq)`（3 张卡）──
            // 出处：直译产物 `_deps/BP_CardFunctions.g.cs:403-438`（函数体全文）：
            //   `IsValid(card)` → `Max(0, card.KreditsTax_AsEnemyTarget + costToAdd)`
            //   → 写回 `card.KreditsTax_AsEnemyTarget` → `IsActionProcess` 为真时
            //   `NotifyAddKreditsTax`（**纯客户端表现**，本内核没有 notifier，不实现）
            //   → `qqq = False`（两个分支都写 False；调用点从不读它）。
            //
            // 为什么这个键属于本次修复：`CanSelectAsTarget` 的
            // `cost_extra_to_target` 分支**读的就是这个字段**（`g.cs:1142/1180`）。
            // 没有写方 ⇒ 那个分支恒不触发、字段恒 0 ⇒ 门是**死代码**。
            // 调用它的 3 张卡：`card_event_order_of_the_day`(+1) /
            // `card_event_grim_day`(+2/−2) / `card_unit_tupolev_sb_2`(+2/−2)。
            ["AddKreditsTax"] = (c, r, a) => DoAddKreditsTax(c, a),

            // ⚠️ `GetPlayFromHandDamage` **不是**引擎的通用函数，它是**每张卡蓝图
            //    各自实现**的普通函数（编译成独立 export，不在 ubergraph 里）。
            //    全卡池 129 张卡定义了它，函数体各不相同 —— 78 张就是
            //    `damage = <字面量>`（英联邦 20、Z SPECIAL UNIT 4、M4 FIREFLY 5…），
            //    其余是真正的判据（读 HQ 防御、数手牌、循环场面求和…）。
            //    所以这里**没有也不可能有「一个公式」**；唯一忠实的做法是
            //    执行那张卡自己的函数体（IR 里的 `locals`）。
            //    证据见 `out/gpfd/commonwealth.bpasm` 的 `.export 4`。
            ["GetPlayFromHandDamage"] = (c, r, a) => DoGetPlayFromHandDamage(c, AsCard(a.FirstOrDefault())),

            // ---- Develop 一族（见文件下半部「Develop（GetChooseSpawnCards 一族）」的注释）----
            ["GetAllActiveStaticCards"] = (c, r, a) => StaticCardPool(
                c,
                includeNotAttainable: TruthyArg(a, 0),
                includeReserved: TruthyArg(a, 1)),
            // `CheckHasUnitToSpawn(card, out hasUnitToSpawn)` is a private
            // legality helper duplicated by four Soviet event blueprints.
            // It must use the same static-card pool and predicates as the
            // corresponding random-spawn local function; returning a generic
            // "any unit exists" answer would make CanPlayFromHand disagree
            // with the card's actual spawn path.
            ["CheckHasUnitToSpawn"] = (c, r, a) => HasUnitToSpawn(c, a),
            ["GetRandomBritishAir"] = (c, r, a) =>
            {
                int kredit = IntArg(a, 0);
                bool includeReserved = c.Self is not null && CardPoolTable.IsReserved(c.Self.Name);
                var pool = StaticCardPool(c, includeNotAttainable: false, includeReserved)
                    .Where(x => x.Definition.FactionId == 2
                        && IsAirUnit(x)
                        && x.Definition.Kredits == kredit)
                    .ToList();
                return c.Engine.Api.GetRandomCard(pool);
            },
            // 三个出参按调用点的顺序返回：[cards, markAsSeen, keepOrder]
            // （`BP_CardFunctions.selectCardToDraw` 的 L_0680 就是这个顺序）。
            // 多出参约定见 `KismetVm.ExecuteCall`：返回 object?[] 即按下标对应各 out 槽。
            ["GetChooseSpawnCards"] = (c, r, a) =>
            {
                var selecting = SelfArg(c, r, a);
                if (selecting is null)
                {
                    return new object?[] { new List<CardInstance>(), false, false };
                }

                var cards = GetChooseSpawnCards(c, selecting, out bool markAsSeen, out bool keepOrder);
                return new object?[] { cards, markAsSeen, keepOrder };
            },
            ["MoveCardToTopOfOwnersDeck"] = (c, r, a) => DoMoveCardToTopOfOwnersDeck(c, a),
            ["MoveMultipleCardsToTopOfOwnersDeck"] = (c, r, a) => DoMoveMultipleCardsToTopOfOwnersDeck(c, a),
            ["AdjustCardPositionInDeck"] = (c, r, a) => DoAdjustCardPositionInDeck(c, r, a),
            ["StealCardFromBoardToDeck"] = (c, r, a) => DoStealCardFromBoardToDeck(c, r, a),
            ["PlayCardDirectlyFromHand"] = (c, r, a) =>
            {
                var card = AsCardOrId(c, a.ElementAtOrDefault(0));
                bool toFrontline = TruthyArg(a, 1);
                int instigatorID = IntArg(a, 2, c.Self?.CardId ?? 0);
                int locationNumber = IntArg(a, 4, -1);
                bool success = card is not null
                    && c.Engine.PlayCardDirectlyFromHand(card, toFrontline, instigatorID, locationNumber);
                return success;
            },
            ["ForceEndTurn"] = (c, r, a) =>
            {
                // BP_CardFunctions::ForceEndTurn has no parameters and only
                // forwards NotifyForceEndTurn(true). The protocol carries the
                // source card ID, so defer the actual turn transition until
                // the surrounding VM/trigger scope has returned.
                c.Engine.RequestForceEndTurn(c.State.ActiveSide, c.Self?.CardId ?? 0);
                return null;
            },
            ["TakeControlOfEnemyUnit"] = (c, r, a) =>
            {
                var card = AsCardOrId(c, a.ElementAtOrDefault(0));
                if (card is null)
                {
                    return null;
                }

                int instigatorId = IntArg(a, 1, c.Self?.CardId ?? 0);
                c.Engine.ChangeUnitOwnership(card, instigatorId, card.Owner,
                    card.Owner.Opposite(), CardLocation.NotAvailable, releaseControl: false);
                return null;
            },
            ["ReleaseControlOfEnemyUnit"] = (c, r, a) =>
            {
                var card = AsCardOrId(c, a.ElementAtOrDefault(0));
                if (card is null)
                {
                    return null;
                }

                int instigatorId = IntArg(a, 1, c.Self?.CardId ?? 0);
                Side originalOwner = card.OriginalOwner is Side.Left or Side.Right
                    ? card.OriginalOwner : card.Owner.Opposite();
                var originalLocation = (CardLocation)IntArg(a, 2, (int)originalOwner.HqOf());
                c.Engine.ChangeUnitOwnership(card, instigatorId, card.Owner,
                    card.Owner.Opposite(), originalLocation, releaseControl: true);
                return null;
            },
            ["RandomIntFromRangeWithStream"] = (c, r, a) => DoRandomIntFromRange(c, a),
            ["GetPlayingSide"] = (c, r, a) => (int)c.Controller,
            ["GetStartingSide"] = (c, r, a) => (int)c.State.StartingSide,
            ["GetAllyNationForSide"] = (c, r, a) => "",
            ["GetEmptyText"] = (c, r, a) => "",

            // ---------------- 效果 / 状态修改 ----------------
            // Change* 系列：数值恒在 index 2，来源在 index 1
            ["ChangeAttack"] = (c, r, a) => DoChangeAttack(c, r, a),
            ["ChangeDefense"] = (c, r, a) => DoChangeDefense(c, r, a),
            ["GainAttack"] = (c, r, a) => DoChangeAttack(c, r, a),
            ["GainDefense"] = (c, r, a) => DoChangeDefense(c, r, a),
            ["LoseAttack"] = (c, r, a) => DoChangeAttack(c, r, a, invert: true),
            ["SetDefense"] = (c, r, a) => DoSetDefense(c, r, a),
            ["ChangeKreditCost"] = (c, r, a) => DoChangeKreditCost(c, r, a),
            ["SetKreditCost"] = (c, r, a) => DoSetKreditCost(c, r, a),
            ["ChangeOperationCost"] = (c, r, a) => DoChangeOperationCost(c, r, a),
            ["ChangeHeavyArmor"] = (c, r, a) => DoChangeHeavyArmor(c, r, a),
            // `BP_CardFunctions::ChangeFrontlineLimiter` 只维护
            // `BP_GameState_Battle.FrontlineLimiters`：Add 时限制前线为 2，Remove
            // 时恢复默认容量。`GameState` 已按蓝图建模为卡 ID 集合，而不是计数器。
            // 权威形状为 `(limiterCardID, remove, out qqq)`；这里的第一个参数
            // 可能是卡实例，也可能是整数 cardID。
            ["ChangeFrontlineLimiter"] = (c, r, a) =>
            {
                int limiterId = IdOf(a.ElementAtOrDefault(0));
                if (limiterId == 0)
                {
                    limiterId = c.Self?.CardId ?? AsCard(r)?.CardId ?? 0;
                }

                if (limiterId != 0)
                {
                    if (TruthyArg(a, 1))
                    {
                        c.State.FrontlineLimiters.Remove(limiterId);
                    }
                    else
                    {
                        c.State.FrontlineLimiters.Add(limiterId);
                    }
                }

                return null;
            },
            ["AddGameplayRestriction"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                var type = (GameplayRestrictionType)IntArg(a, 1);
                int source = IntArg(a, 2, c.Self?.CardId ?? 0);
                int turns = IntArg(a, 3, 1);
                c.State.AddGameplayRestriction(side, type, source, turns);
                return null;
            },
            ["RemoveGameplayRestriction"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                var type = (GameplayRestrictionType)IntArg(a, 1);
                int source = IntArg(a, 2, c.Self?.CardId ?? 0);
                c.State.RemoveGameplayRestriction(side, type, source, TruthyArg(a, 3));
                return false;
            },
            ["IsThereGameplayRestriction"] = (c, r, a) =>
                c.State.HasGameplayRestriction(SideArg(r, a, 0, c.Controller),
                    (GameplayRestrictionType)IntArg(a, 1)),
            ["ApplyGameplaySideEffect"] = (c, r, a) =>
            {
                string? tag = GameplayTagArg(a.ElementAtOrDefault(1));
                CardInstance? source = AsCardOrId(c, a.ElementAtOrDefault(2)) ?? c.Self;
                if (SideArgOrNull(a, 0) is { } side && tag is not null && source is not null)
                {
                    c.State.ApplyGameplaySideEffect(side, tag, source.CardId,
                        IntArg(a, 3), IntArg(a, 4), 0);
                }

                return 0;
            },
            ["RemoveGameplaySideEffect"] = (c, r, a) =>
            {
                string? tag = GameplayTagArg(a.ElementAtOrDefault(1));
                CardInstance? source = AsCardOrId(c, a.ElementAtOrDefault(2)) ?? c.Self;
                if (SideArgOrNull(a, 0) is { } side && tag is not null && source is not null)
                {
                    c.State.RemoveGameplaySideEffect(side, tag, source.CardId);
                }

                return 0;
            },
            ["DamageCard"] = (c, r, a) => DoDamageCard(c, r, a),
            ["DamageMultipleCards"] = (c, r, a) => DoDamageMultipleCards(c, r, a),

            // ★★ 2026-10-02 新增：`MakeCardsFight`（「让两个单位互斗」）—— 原先**表里没有这个键**。
            //
            // 为什么必须补：缺键 ⇒ `KismetVm.ExecuteCall` 什么都不做、只记一笔
            // `UnimplementedCalls`（`KismetVm.cs:606-612`）⇒ 「互斗」整段效果**静默不发生**
            // ⇒ 本该战死的单位没死 ⇒ **我方场上有客户端没有的单位**（玩家报的「虚空单位」）。
            //
            // 影响面：IR 里 12 个调用点 / 12 张卡（`card_event_chain_home`、
            // `card_event_claim_the_skies`、`card_event_german_counterattack`、
            // `card_event_hms_formidable`、`card_event_hull_down`、`card_event_sloped_armor`、
            // `card_unit_113_schutzen`、`card_unit_40_royal_marine`、`card_unit_57th_rifles`、
            // `card_unit_heinkel_he_219`、`card_unit_henschel_he_129`、`card_unit_panzer_iv_h`）。
            // 实测 `replay-773639` 的 ⑥ 里就是 `MakeCardsFight ×1`。
            ["MakeCardsFight"] = (c, r, a) => DoMakeCardsFight(c, a),
            ["AddAttackUntilEndOfTurn"] = (c, r, a) => DoAddAttackUntilEndOfTurn(c, a),
            ["HealCard"] = (c, r, a) => DoHealCard(c, r, a),
            ["DestroyCard"] = (c, r, a) => DoDestroyCard(c, r, a),
            ["TriggerDestruction"] = (c, r, a) => DoTriggerDestruction(c, r, a),
            ["TriggerDeployment"] = (c, r, a) => DoTriggerDeployment(c, r, a),
            // `ExecuteOnDeploymentTriggered(cardTriggered, instigatorID, out triggerMultiple)`
            // is the BP_CardFunctions helper used by trigger-queue paths.  Keep its
            // out value aligned with the same observer aggregation as TriggerDeployment.
            ["ExecuteOnDeploymentTriggered"] = (c, r, a) =>
            {
                var card = AsCardOrId(c, a.ElementAtOrDefault(0))
                           ?? c.Target
                           ?? AsCard(r)
                           ?? c.Self;
                if (card is null)
                {
                    return 0;
                }

                return c.Engine.Api.SumDeploymentTriggerMultiple(
                    card, IntArg(a, 1, c.Self?.CardId ?? card.CardId));
            },
            // ⚠️ 第 5 参 `cardsIDs` 是**出参**（权威签名 `BP_CardFunctions.g.cs:12298-12310`），
            //    蓝图体把循环里抽到的每张牌的 cardID 攒成 `drawnCards` 再 `Invoke` 出去
            //    （`g.cs:12386` / `g.cs:12400`）。旧实现 `DrawCards(...); return null;`
            //    ⇒ `KismetVm.cs:669` 不写出参 ⇒ 读它的 **9 张卡**整段恒空
            //    （`card_event_detailed_recon` 的 `Array_Length(cardsIDs)` 循环、
            //      `card_event_pact_of_steel` 的 `Array_Get(cardsIDs, 0)` …）。
            //    改成返回 `List<int>`（抽到的 cardID）⇒ VM 写进第一个 out 槽；
            //    先例见 `GetDeckByside`（同族 `TArray<int>` 出参，已有自测守着）。
            ["DrawCardsFromDeckBySide"] = (c, r, a) => DrawCardsAndCollect(SideArg(r, a, 1, c.Controller), IntArg(a, 2, 1)),
            ["DrawCardFromDeck"] = (c, r, a) => { DrawCards(SideArg(r, a, 0, c.Controller), IntArg(a, 1, 1)); return null; },
            ["SpawnCardInHandBySide"] = (c, r, a) => DoSpawnInHand(c, r, a),
            ["SpawnCardOnBattlefield"] = (c, r, a) => DoSpawnOnBattlefield(c, r, a),
            ["SpawnMultipleCardsOnBattlefield"] = (c, r, a) => DoSpawnMultipleOnBattlefield(c, r, a),
            ["SpawnNextToCard"] = (c, r, a) => DoSpawnNextToCard(c, a),
            ["ResetUnitOperations"] = (c, r, a) => DoResetUnitOperations(c, a),
            ["ChangeKredits"] = (c, r, a) => DoChangeKredits(c, r, a),
            // ⚠️ 实参是 `(卡, side)` —— side 在 **index 1**，不是 0。
            //    实测两种写法：`GainKreditSlot(self, side)`（47 次）
            //    和 `GainKreditSlot(K2Node_Event_cardDestroyed, side)`（1 次），
            //    第一个参数都是"哪张卡发起的"，第二个才是阵营。
            //    读 index 0 会拿到一张卡对象 → `SideArg` 退化成 receiver 的 owner，
            //    对"给对手加槽位"这类卡会加错边。
            ["GainKreditSlot"] = (c, r, a) =>
            {
                var giver = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r) ?? c.Self;
                GainKreditSlot(SideArg(r, a, 1, c.Controller), 1, giver);
                return null;
            },
            ["LoseKreditSlot"] = (c, r, a) =>
            {
                // BP_CardFunctions::LoseKreditSlot(side), L_0005:
                // ChangeKreditSlotsBySide(side, -1, 0). Only slots change;
                // the notifier carries the same current kredits before/after.
                Side side = SideArg(r, a, 0, c.Controller);
                c.State.AddMaxKredits(side, -1);
                // LoseKreditSlot L_0026 is unconditional, including at zero slots.
                c.State.RecordKreditSlotLoss(side);
                c.Engine.FireSubAction("ZActionChangeKredits", new[]
                {
                    ActionValue2.Str("side", side.ToWire()),
                    ActionValue2.Int("newMaxKredits", c.State.MaxKredits(side)),
                    ActionValue2.Int("newKredits", c.State.Kredits(side)),
                });
                FireExtraKreditSlotGain(side, -1, giver: null);
                return null;
            },
            ["GetTotalKreditsLostThisBattle"] = (c, r, a) =>
                c.State.KreditSlotsLost(SideArg(r, a, 0, c.Controller)),
            ["CustomAbilityAdd"] = (c, r, a) => DoCustomAbilityAdd(c, r, a),
            ["CustomAbilityRemove"] = (c, r, a) => DoCustomAbilityRemove(c, r, a),
            ["AddCustomGameplayTag"] = (c, r, a) => DoAddCustomGameplayTag(c, r, a),
            ["RemoveCustomGameplayTag"] = (c, r, a) => DoRemoveCustomGameplayTag(c, r, a),
            // ⚠️ 同形「接收者优先」bug（2026-10-03）：旧写法 `if (AsCard(r) is {} x) PersistCustomFields(x)`
            //    只认接收者，而 `r` 恒为 `cardFunction`（= `ctx.Self`，施法的那张牌自己）
            //    ⇒ **永远持久化施法者，`a[0]` 指的别人那张卡的临时字段从没被写出去**。
            //
            // 权威签名（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:27828-27849`）：
            //    `0: cardID`、`1: refreshEffectBar` —— 蓝图体把 `args[0]` 喂给
            //    `GetCardFromID`（`g.cs:27840`），再取那张卡的 `customJson` 通知客户端。
            //
            // IR 实测（`docs/card-ir.json` 扫描，2026-10-03）：489 个调用点，`recv` =
            //    `{"var":"cardFunction"}` **489/489**；`a[0]` =
            //      · 裸 `{"var":"cardID"}`（= `ctx.Self.CardId`）×459 —— 旧写法在这 459 处
            //        **碰巧等价**（`AsCardOrId(自己的 cardID)` ≡ 旧 `AsCard(r)`）；
            //      · `{"var":"cardID","ctx":{…}}` = **别人卡的 cardID** ×22
            //        （`K2Node_Event_targetCard` / `spawnedCard` / `CallFunc_Array_Get_Item` …）
            //      · 别的整数 cardID 形状（数组元素、`spawnedCardID` 变量…）×8
            //    ⇒ 那 **30 处 / 21 张卡**持久化的是**错卡**（`card_event_no_retreat`、
            //      `card_event_maginot_line`、`card_event_seize_the_initiative`、
            //      `card_unit_obice_da_75_13`、`card_event_carrier_battle` …）。
            //
            // 用 `AsCardOrId(c, a[0]) ?? AsCard(r)`（实参优先、接收者兜底）后，459 处 self
            // 形状解析结果**逐位不变**，30 处修正。同族先例见下面的 `MakeVeteran`。
            ["PersistCustomFields"] = (c, r, a) => { if ((AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r)) is { } x) PersistCustomFields(x); return null; },
            // ⚠️ 同形「接收者优先」bug（2026-10-02）：旧写法 `if (AsCard(r) is {} x) MakeVeteran(x)`
            //    只认接收者，而 `r` 恒为 `cardFunction`（= `ctx.Self`，施法的那张牌自己）
            //    ⇒ **永远把施法者自己变成老兵，目标从没被命中过**。
            //
            // IR 实测（`out/audit/target-shapes.py`）：45 个调用点，`a[0]` =
            //   `{"self":true}`×42 —— 这 42 处 `a[0]` 恰好**就是** `ctx.Self`
            //   （`KismetVm.Frame` 的 `_locals["self"]` 与 `_locals["cardFunction"]`
            //   都取 `ctx.Self`），所以旧写法在这 42 处**碰巧等价**，把 bug 掩盖住了；
            //   剩下 3 处 `a[0]` ≠ `ctx.Self`，全部静默打错卡：
            //   · `card_unit_266th_guards_rifles` i=1026（`OnOtherCardPlayedFromHand`）：
            //     `MakeVeteran(K2Node_Event_cardPlayed)` —— 该变老兵的是**被打出的那张牌**，
            //     旧实现把 266 近卫步兵团自己变成老兵。
            //   · `card_event_battle_valor` i=216（`OnPlayedFromHand`）：
            //     `MakeVeteran(tempCard)` —— `tempCard` = `ctx.Target`，该变的是**目标**。
            //   · `card_unit_179th_tomahawks` i=813（`OnStartOfTurn`）：
            //     `MakeVeteran(GetCardFromID(spawnedCardID))` —— 该变的是**新生成的那张牌**。
            //
            // 用 `TargetArg`（先实参、再 `c.Target`、最后接收者）后，42 处 self 形状
            // 解析结果**逐位不变**（`AsCardOrId(ctx.Self)` ≡ 旧 `AsCard(r)`），3 处修正。
            ["MakeVeteran"] = (c, r, a) => { if (TargetArg(c, r, a) is { } x) MakeVeteran(x); return null; },
            ["RevealCard"] = (c, r, a) => AsCardOrId(c, a.ElementAtOrDefault(0)) is { } target
                ? RevealCard(target, IntArg(a, 1, c.Self?.CardId ?? 0))
                : 0,

            // 关键字
            ["GiveBlitz"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Blitz),
            ["GiveAmbush"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Ambush),
            ["GiveFury"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Fury),
            ["GiveGuard"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Guard),
            ["GiveImmune"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Immune),
            ["GiveSmokescreen"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Smokescreen),
            ["GiveAlpine"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Alpine),
            ["GiveMobilize"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Mobilize),
            ["GiveSalvage"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Salvage),
            ["GiveShock"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Shock),
            ["GiveBond"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Bond),
            ["GiveRandomCombatKeyword"] = (c, r, a) => DoGiveRandomCombatKeyword(c, r, a),
            ["RemoveBlitz"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Blitz),
            ["RemoveAmbush"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Ambush),
            ["RemoveFury"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Fury),
            ["RemoveGuard"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Guard),
            ["RemoveImmune"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Immune),
            ["RemoveSmokescreen"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Smokescreen),
            ["RemoveAlpine"] = (c, r, a) => DoRemoveAlpine(c, r, a),
            ["RemoveSalvage"] = (c, r, a) => DoRemoveSalvage(c, r, a),
            // `RemoveBond(cardID, instigatorID, out qqq)` stores the same
            // `bond_removed` override as the Blueprint and clears the
            // dynamic `ability.bond` tag. Visual delegates are client-only.
            ["RemoveBond"] = (c, r, a) => DoRemoveBond(c, r, a),
            // ⚠️ `PinUnit` 不再直接走 `DoGiveKeyword` —— 它还要记**时长**
            //    （`BP_CardFunctions::PinUnit` i=955 `pinnedTurns = Max(…, 3或2)`）。
            //    走 `CardApi.PinUnit` 才能和 `UnpinUnit`/到期递减对上。
            //    **派发键没变**（还是 "PinUnit"），只是换了实现。
            ["PinUnit"] = (c, r, a) => DoPinUnit(c, r, a),
            ["UnpinUnit"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Pinned),
            // `RemovePin(card, out qqq)` 与 `UnpinUnit` 共享同一条蓝图落点：
            // 移除 Pinned，同时由 CardApi 清零 pinnedTurns 并广播解除事件。
            ["RemovePin"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Pinned),
            // `ChangedPinnedTurns(cardID, instigatorID, turnsToChange, out qqq)`：
            // 蓝图只允许对有效、在场的单位修改，并将结果夹在 [0, 5]。
            ["ChangedPinnedTurns"] = (c, r, a) => DoChangedPinnedTurns(c, a),
            ["SuppressUnit"] = (c, r, a) => DoSuppressUnit(c, r, a),

            // ★★ 2026-10-02 补：`SuppressMultipleUnits` **原来没有派发键** ⇒
            //    直接调用它的两张卡的「抑制」是**静默空转**，这正是玩家报的「抑制不生效」：
            //      · `card_event_white_death`（i=382）：「Suppress **all** enemy units.」
            //      · `card_unit_38th_independent`（i=590 / i=2808）
            //    证据：`klink bot/docs/card-ir.json` 里 `"fn":"SuppressMultipleUnits"`
            //    共 3 个调用点；而 `SuppressUnit`（16 个调用点）**会转发到它**
            //    —— `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:36484`
            //    `H.Call("SuppressMultipleUnits", {self, MakeArray([cardID]), instigatorID})`。
            //    ⇒ 蓝图里**只有一条**实现路径，我们却只注册了外壳那一半。
            ["SuppressMultipleUnits"] = (c, r, a) => DoSuppressMultipleUnits(c, r, a),
            ["AddHeavyArmor"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.HeavyArmor),

            // 卡牌私有 JSON
            //
            // ⚠️ `JSON_Get*` 是**双输出**原语：`(card, key, out value, out found)`。
            //    必须返回 `object?[] { value, found }`，VM 会按下标把两个槽都写回
            //    （见 KismetVm.ExecuteCall 里"多输出原语"那段）。
            //    只回 value 的话 `found` 永远是 null，而卡蓝图里判的是
            //    `BooleanAND(value, found)` —— 于是**所有** `JSON_Get*` 的结果都是假。
            //    `found` 的语义是"这个键存在吗"（客户端看的是 JSON 里有没有这个字段）。
            ["JSON_GetInt"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetInt(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { 0, false },
            ["JSON_SetInt"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetInt(x, StrArg(a, 1), IntArg(a, 2)); return null; },
            ["JSON_GetBool"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetBool(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { false, false },
            ["JSON_SetBool"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetBool(x, StrArg(a, 1), TruthyArg(a, 2)); return null; },
            ["JSON_GetString"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetString(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { "", false },
            ["JSON_SetString"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetString(x, StrArg(a, 1), StrArg(a, 2)); return null; },
            // ⚠️ 必须**返回非 null** —— VM 只在 `result is not null` 时写 out 槽
            //    （`KismetVm.cs:669`），而 `found` 是 `JSON_Clear` 的第 3 个实参（有 46 张卡读它）。
            //    语义见 `CardApi.JsonClear` 的注释（蓝图只删**指定那一个键**）。
            ["JSON_Clear"] = (c, r, a) => AsCard(r) is { } x && JsonClear(x, StrArg(a, 1)),

            // ---------------- 数组（卡牌蓝图里大量使用）----------------
            // ⚠️ 这一族全部走 `EvalList`（**元素类型无关**），不是 `EvalArray`。
            //    UE 的 `TArray<int>`（例 `GetDeckByside` 的 `deckCardIDs`）和
            //    `TArray<UObject*>`（例 `GetCardsOnBoardBySide` 的 `Cards`）
            //    在这里是同一个 VM 里的两种元素类型；只认后者的话，
            //    ID 数组上的 `Array_Length` 会返回 0（循环整段被跳过）。
            //    见 `CardApi.EvalList` / `GetDeckBySide` 的注释。
            ["Array_Length"] = (c, r, a) => EvalList(r, a).Count,
            ["Array_IsNotEmpty"] = (c, r, a) => EvalList(r, a).Count > 0,
            ["Array_IsEmpty"] = (c, r, a) => EvalList(r, a).Count == 0,
            ["Array_IsValidIndex"] = (c, r, a) => { int i = IntArg(a, 1); var arr = EvalList(r, a); return i >= 0 && i < arr.Count; },
            ["Array_Get"] = (c, r, a) => { int i = IntArg(a, 1); var arr = EvalList(r, a); return i >= 0 && i < arr.Count ? arr[i] : null; },
            // ⚠️ 必须按**值**比，不能按引用比（原来是 `ReferenceEquals`，永远 false）。
            //    实测形状是 `Array_Contains(MakeArray(5,6,7), goingToLocation)` ——
            //    `card_unit_214th_amur` 用它判「离场去向是不是 [半场,前线]」。
            //    `goingToLocation` 是事件入参（byte/int），`MakeArray` 里是 `IntConst`，
            //    两边都是装箱的 int，`ReferenceEquals` 必然不成立，于是那个判据恒为假、
            //    整条还原分支被跳过。这里退化成「先引用、再数值」两级比较。
            //
            //    ⚠️⚠️ 2026-10-02：**被比较的项在 `a[1]`，不是 `a[0]`**。
            //    `a[0]` 是**目标数组本身**（和 `Array_Get` 把下标放 `a[1]`、
            //    `Array_Add` 把项放 `a[^1]` 同一套约定；`KismetVm.ExecuteCall`
            //    明确不摘 `args[0]`，见那里的注释）。旧实现读 `a[0]`，
            //    于是比的是「每个元素 vs 数组」→ `AsInt(数组)=0` →
            //    对**卡实例数组**恒等于"数组非空就 true"、对**整数数组**恒等于"含 0 就 true"。
            //    全卡池 **30 个调用点 / 22 张卡**全部是这个形状
            //    （`card_unit_gordon_highlanders` i=114 `Array_Contains(AffectedCards, drawnCardID)`、
            //     `card_unit_31e_algiers` i=1347、`card_unit_214th_amur` …）。
            //    回归用例：`tools/BotSim/SelfTest.cs` 的 `GetDeckBySideReturnsCardIds`
            //    第二条断言（`Array_Contains(deckCardIDs, cardID)`）。
            //
            //    2026-10-02：比较挪到 `SameArrayValue`，因为元素现在有两种表示 ——
            //    卡实例与整数卡 ID。`Array_Contains(deckCardIDs, cardID)`
            //    （`DrawSpecificCardFromDeckBySide` 内联体，
            //      `out/Generated-gap/_deps/BP_CardFunctions.g.cs:12460`）
            //    就是「整数数组 vs 整数」，而 `AsCardOrId` 那一族是「实例数组 vs 整数 ID」，
            //    两种都必须命中。
            ["Array_Contains"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                var needle = a.Length > 1 ? a[1] : null;
                foreach (object? item in arr)
                {
                    if (SameArrayValue(item, needle))
                    {
                        return true;
                    }
                }

                return false;
            },
            ["Array_LastIndex"] = (c, r, a) => EvalList(r, a).Count - 1,
            ["Array_AddUnique"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                if (a.Length < 2) return -1;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (SameArrayValue(arr[i], a[1])) return i;
                }
                int before = arr.Count;
                ArrayAppendOne(c, arr, a[1]);
                return arr.Count > before ? before : -1;
            },
            // ⚠️ `Array_Add` / `Array_Clear` / `Array_Append` 在蓝图里是
            //    **原地修改目标数组**（目标按引用传进去，`Array_Add` 的返回值只是新元素下标）。
            //
            //    旧实现：`Array_Add` 返回一个**新数组的拷贝**、不动 `args[0]`；
            //    `Array_Clear` / `Array_Append` 直接返回 null（空操作）。
            //    后果：所有「先攒一个数组、再遍历它」的卡**累加出来永远是空表**。
            //    实测形状（全卡池扫描，见 `out/gcs/scan-arrayops.py`）：`Array_Add` 的
            //    第一个实参**全部**是成员数组变量（`cardsToDamage` / `possibleCards` /
            //    `possibleUnits` / `cardIDs` …），涉及 `card_event_anzac_spirit`(25)、
            //    `card_event_atlantic_convoy`(22)、`card_event_red_skies_skirm`(11) 等 30 张卡；
            //    而且**没有任何一个调用点读它的返回值**，所以"返回拷贝"这一支没有消费者。
            //
            //    证据（字节码原文）：`card_event_pams.GetChooseSpawnCards`
            //    —— `out/gcs/pams.bpasm:408-420`
            //    <code>
            //    let "CallFunc_Array_Add_ReturnValue" {
            //        Array_Add { instancevariable "PossibleCards"  localvariable "…_Item" }
            //    }
            //    </code>
            //    目标 `PossibleCards` 是卡的实例变量（`instancevariable @path(owner=1)`），
            //    VM 侧由 `KismetVm.SeedArrayTarget` 给它一个稳定的空数组。
            //
            //    2026-10-02：目标数组现在可能是 `List<int>`（整数 ID 数组，例
            //    `card_event_semper_fi` 的 `cardsToRandom = GetDeckByside(side)`）——
            //    那种情况必须原样存**整数**，不能走 `AsCardOrId` 解析成卡实例，
            //    否则元素类型和后续的 `Array_Get` / `RandomIntFromRangeWithStream` 对不上。
            ["Array_Add"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                if (a.Length > 0)
                {
                    ArrayAppendOne(c, arr, a[^1]);
                }

                return arr.Count - 1;
            },
            ["Array_Append"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                if (a.Length > 1 && a[1] is System.Collections.IList more && !ReferenceEquals(more, arr))
                {
                    foreach (object? v in more)
                    {
                        ArrayAppendOne(c, arr, v);
                    }
                }

                return null;
            },
            ["Array_Clear"] = (c, r, a) => { EvalList(r, a).Clear(); return null; },
            // UE 的 `Array_Remove(目标数组, 项)` 按**值**删掉**所有**匹配项（原地）。
            ["Array_Remove"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                if (a.Length > 1)
                {
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        if (SameArrayValue(arr[i], a[1]))
                        {
                            arr.RemoveAt(i);
                        }
                    }
                }

                return null;
            },
            // UE 的 `Array_RemoveItem(目标数组, 项)` 按**值**删掉第一处匹配（不是按下标）。
            // 之前没有这一项（进的是"未实现调用"榜，实测 4 次）。
            ["Array_RemoveItem"] = (c, r, a) =>
            {
                var arr = EvalList(r, a);
                if (a.Length > 1)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (SameArrayValue(arr[i], a[1]))
                        {
                            arr.RemoveAt(i);
                            break;
                        }
                    }
                }

                return null;
            },

            // ---------------- 集合（Blueprint TSet）----------------
            // Set nodes receive the target by reference.  KismetVm seeds a
            // missing local target with HashSet<object?>; values are compared
            // using the same card-ID-aware rule as Array_Contains.
            ["Set_Add"] = (c, r, a) =>
            {
                var set = EvalSet(r, a);
                if (a.Length > 1 && !set.Any(v => SameArrayValue(v, a[1])))
                {
                    set.Add(a[1]);
                }

                return null;
            },
            ["Set_Clear"] = (c, r, a) => { EvalSet(r, a).Clear(); return null; },
            ["Set_Contains"] = (c, r, a) =>
            {
                var set = EvalSet(r, a);
                return a.Length > 1 && set.Any(v => SameArrayValue(v, a[1]));
            },
            ["Set_Length"] = (c, r, a) => EvalSet(r, a).Count,
            ["Set_Remove"] = (c, r, a) =>
            {
                var set = EvalSet(r, a);
                if (a.Length < 2)
                {
                    return false;
                }

                object? found = set.FirstOrDefault(v => SameArrayValue(v, a[1]));
                if (found is null && !set.Any(v => v is null && a[1] is null))
                {
                    return false;
                }

                return set.Remove(found);
            },
            ["Set_RemoveItems"] = (c, r, a) =>
            {
                var set = EvalSet(r, a);
                if (a.Length > 1 && a[1] is System.Collections.IEnumerable values and not string)
                {
                    foreach (object? value in values.Cast<object?>().ToArray())
                    {
                        object? found = set.FirstOrDefault(v => SameArrayValue(v, value));
                        if (found is not null || set.Any(v => v is null && value is null))
                        {
                            set.Remove(found);
                        }
                    }
                }

                return null;
            },
            ["Set_ToArray"] = (c, r, a) => EvalSet(r, a).ToList(),

            // ---------------- 卡牌私有 JSON 的数组变体 ----------------
            ["JSON_GetIntArray"] = (c, r, a) => AsCard(r) is { } x ? JsonGetIntArray(x, StrArg(a, 1)) : new List<int>(),
            ["JSON_SetIntArray"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetIntArray(x, StrArg(a, 1), AsIntList(a.ElementAtOrDefault(2))); return null; },
            ["JSON_AddToIntArray"] = (c, r, a) => { if (AsCard(r) is { } x) JsonAddToIntArray(x, StrArg(a, 1), IntArg(a, 2)); return null; },
            // `JSON_RemoveFromIntArray(card, key, value, out found)`：调用点使用
            // 隐式 self，返回 bool 供唯一的 found 出参写回。
            ["JSON_RemoveFromIntArray"] = (c, r, a) =>
            {
                var card = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r) ?? c.Self;
                return card is not null && JsonRemoveFromIntArray(card, StrArg(a, 1), IntArg(a, 2));
            },

            // ---------------- 纯表现层调用：无头环境下直接吃掉 ----------------
            // 这些是 UI / 特效 / 音效，不改变规则状态。显式列出来是为了让
            // 「未实现」这个指标只反映**规则**缺口，不被表现层噪声淹没。
            ["ShowNotification"] = (c, r, a) => null,
            ["ShowCampaignMessage"] = (c, r, a) => null,
            ["PlaySoundEffect"] = (c, r, a) => null,
            ["PlayFromStart"] = (c, r, a) => null,
            ["Play"] = (c, r, a) => null,
            ["SetVisibility"] = (c, r, a) => null,
            ["SetScalarParameterValue"] = (c, r, a) => null,
            ["SetPlayRate"] = (c, r, a) => null,
            ["GetPlatformEnum"] = (c, r, a) => 0,
            ["GetCachedBPBoard"] = (c, r, a) => null,
            ["GetBoard"] = (c, r, a) => null,
            ["GetLogic"] = (c, r, a) => null,
            ["GetCardSound"] = (c, r, a) => null,
            ["destroyActorAndChildActors"] = (c, r, a) => null,
            ["UpdateCampaignStarStatus"] = (c, r, a) => null,

            // ---------------- 近似实现（语义未验证，保守处理）----------------
            ["GetDestroyedCardsCountBySide"] = (c, r, a) => c.State.Discard(SideArg(r, a, 0, c.Controller)).Count(),
            ["isBuffedByCard"] = (c, r, a) => IsBuffedByCard(c, r, a),
            ["GetUnitTypeCountOnBoard"] = (c, r, a) => c.State.Board(SideArg(r, a, 0, c.Controller)).Count(u => IsUnit(u)),
            ["updateCustomJsonIfNeeded"] = (c, r, a) => { if (AsCard(r) is { } x) PersistCustomFields(x); return null; },
            ["checkAndUpdateBuffOnCard"] = (c, r, a) => null,
            ["checkAndUpdateBuffOnAllCards"] = (c, r, a) => null,

            // ---------------- 玩家选择类（近似实现，语义待回放验证）----------------
            // 这两族是「让玩家从若干张里选一张」。无头自对弈里没有真人，
            // 用确定性随机代替 —— 行为上等价于一个随机决策的玩家。
            //
            // ⚠️ `selectCardToDraw` 已改为**按反编译蓝图实现**（见下面 SelectCardToDraw），
            //    不再是"随机挑一张"。旧实现返回的还是一张 CardInstance（蓝图的出参是 Int
            //    的 drawnCardID），而且**根本没把牌抽进手牌** —— 全卡池 38 张卡
            //    （pams / bpf / the_rock_of_gibraltar / hampshire_regiment /
            //    2nd_west_africa 这些 Develop 类）的选牌因此全部落空。
            ["selectCardToDraw"] = (c, r, a) => SelectCardToDraw(c, r, a),
            ["selectTargetFromHand"] = (c, r, a) => DoSelectTargetFromHand(c, r, a),
            ["AddToTriggerQueue"] = (c, r, a) =>
            {
                var card = AsCard(r) ?? c.Self;
                if (card is not null)
                {
                    AddToTriggerQueue(card);
                }

                return null;
            },
            ["ResolveTriggerQueue"] = (c, r, a) =>
            {
                ResolveTriggerQueue();
                return null;
            },

            // 三选一（`Choose One`）的分支：**读卡自己存的 `ChooseOne`**，
            // 由驱动在出牌时按动作流的 `PC[3]` 写进去（见 CardInstance.ChooseOne 的注释）。
            // 旧实现是 `Random.Next(2)` —— 回放里会和真实对局选到不同分支，
            // 而且同一个动作重放两次结果都不一样。默认 0（kardsim 的 ChooseOne 钩子默认也是 0）。
            ["WhichChooseOne"] = (c, r, a) => SelfArg(c, r, a)?.ChooseOne ?? 0,
            ["GetPossibleCardsFromStaticCards"] = (c, r, a) => EvalArray(r, a),
            ["getPossibleCardsFromStaticCards"] = (c, r, a) => EvalArray(r, a),
            ["getTwoCardsFromPossibleCards"] = (c, r, a) => EvalArray(r, a).Take(2).ToList(),
            ["findPairOfUnits"] = (c, r, a) => EvalArray(r, a).Take(2).ToList(),

            // ---------------- 卡牌移动 / 生成（meteor 等）----------------
            ["RemoveCardFromBoard"] = (c, r, a) =>
            {
                var target = TargetCard(c, r, a);
                if (target is not null && !target.IsHq)
                {
                    c.State.Move(target, CardLocation.Discard);
                }

                return null;
            },
            ["RemoveMultipleCardsFromBoard"] = (c, r, a) =>
            {
                foreach (var card in EvalArray(r, a))
                {
                    if (!card.IsHq)
                    {
                        c.State.Move(card, CardLocation.Discard);
                    }
                }

                return null;
            },
            ["SpawnCardInDeckBySide"] = (c, r, a) => DoSpawnInDeck(c, r, a),
            ["SpawnCardInDeck"] = (c, r, a) => DoSpawnInDeck(c, r, a),

            // 退回手牌 / 回牌库 —— 这两个是 `ResetCardInBattle`（→ OnCardReset 族）的**唯一**触发路径。
            ["MoveUnitFromBoardToOwnersHand"] = (c, r, a) => DoMoveUnitFromBoardToOwnersHand(c, r, a),
            ["MoveCardFromBoardToOwnersHand"] = (c, r, a) => DoMoveUnitFromBoardToOwnersHand(c, r, a),
            ["MoveUnitFromSupportToFrontLine"] = (c, r, a) =>
            {
                var unit = AsCardOrId(c, a.ElementAtOrDefault(0));
                int instigatorID = IntArg(a, 1, c.Self?.CardId ?? 0);
                return unit is not null
                    && c.Engine.MoveUnitFromSupportToFrontLine(unit, instigatorID, out _);
            },
            ["MakeCardRetreat"] = (c, r, a) => DoMakeCardRetreat(c, a),
            ["ConvertCard"] = (c, r, a) => DoConvertCard(c, a),
            ["ResetCardInBattle"] = (c, r, a) =>
            {
                if (AsCard(a.FirstOrDefault()) is { } rc)
                {
                    ResetCardInBattle(rc);
                }

                return null;
            },

            // ---------------- 文本修饰（只影响显示，无头下吃掉）----------------
            ["AddNumberToText"] = (c, r, a) => null,
            ["UpdateCardText"] = (c, r, a) => null,
            ["AppendNumberToCardText"] = (c, r, a) => null,
            ["GetEmptyText"] = (c, r, a) => "",

            // Forecast's client selection notification is intentionally kept out
            // of the headless rules layer. The pure candidate query is fully
            // specified by Blueprint and returns static weather-card templates.
            ["Forecast"] = (c, r, a) => null,
            ["GetAllForecastCards"] = (c, r, a) => StaticCardPool(
                    c, includeNotAttainable: TruthyArg(a, 0),
                    includeReserved: TruthyArg(a, 1))
                .Where(IsForecastCard)
                .ToList(),
            ["GetForecastedCards"] = (c, r, a) => new List<CardInstance>(),
            ["IsForecasted"] = (c, r, a) => false,

            // ---------------- 第二批补的原语（来自多卡组交叉验证）----------------
            ["IsGroundUnit"] = (c, r, a) => AsCard(r) is { } g && g.Definition.Type is "infantry" or "tank" or "artillery",

            // ⚠️ 同形接收者 bug（审计 §5.1）。权威签名 `BaseCardObject.h:904`：
            //     `IsDamaged(bool& isIt)` —— 没有"是哪张卡"的入参。
            // IR 实测：10 个调用点里 3 个无 recv（隐式 self），旧实现 `AsCard(r)` ⇒ 恒 false。
            ["IsDamaged"] = (c, r, a) => SelfArg(c, r, a) is { } d && d.Defense < d.MaxDefense,
            ["IsBuffed"] = (c, r, a) => AsCard(r) is { } b && b.BuffsBySource.Count > 0,
            ["GetDestroyedCardsIDsThisBattle"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                return c.State.Discard(side).Select(x => x.CardId).ToList();
            },
            ["GetDestroyedCardsIDsByTurn"] = (c, r, a) =>
                c.State.DestroyedCardsIdsByTurn(IntArg(a, 0, c.State.Turn)).ToList(),
            ["GetUnitDestroyedThisTurn"] = (c, r, a) => c.State.UnitDestroyedThisTurn,
            ["ShuffleDeckBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                var instigator = AsCard(r) ?? c.Self;
                ShuffleDeckBySide(c, side, instigator);
                return null;
            },
            // ⚠️ **数额在 `a[1]`，不在 `a[0]`**（审计 §5.2b）。
            // 权威签名（`CardFunctionsStub.h:317`）：
            //     `GiveKreditsBySide(ESideEnum side, int32 kredits, int32 instigatorID, bool& qqq)`
            // 旧实现 `a.Select(AsInt).FirstOrDefault(v => v != 0)` 会把 **`a[0]`（= side，
            // 帧里是 1 或 2）**当成数额 —— 于是"给 10 费"变成"给 1 费或 2 费"，
            // 而且**随左右方变化**。IR 实测（52 个调用点）：
            //     a[0] = `side`×46 / 其它×6      a[1] = 2×12、1×10、3×8、4×3、-2×3、-1×2、5×1…
            //     a[2] = `cardID`（instigatorID） a[3] = out 槽
            // 负数（-1/-2/-3/-7）扣费，`AddKredits` 自己会钳到 0。
            // ⚠️ 不再保留旧的"数额为 0 就送 1"兜底：那是把 bug 当兜底，
            //    会让"给 0 费"这种真实调用白送 1 费。
            ["GiveKreditsBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                c.State.AddKredits(side, IntArg(a, 1));
                return null;
            },
            // `GiveCredits()` is the Landwehr card's private helper.  Its
            // amount is supplied by the operation event frame, not by call
            // arguments: `opCost > 0` then `GiveKreditsBySide(self.side,
            // opCost, ...)`.  Missing event data must be a no-op rather than
            // accidentally refunding a default amount.
            ["GiveCredits"] = (c, r, a) =>
            {
                if (c.Self is null
                    || !c.NamedArgs.TryGetValue("kreditsSpent", out var spentValue))
                {
                    return null;
                }

                int amount = AsInt(spentValue);
                if (amount > 0)
                {
                    c.State.AddKredits(c.Self.Owner, amount);
                }

                return null;
            },
            // `GiveTwoKredits()` is 2nd Michigan's private helper.  Its
            // blueprint body calls GiveKreditsBySide(self.side, 2, self.cardID).
            ["GiveTwoKredits"] = (c, r, a) =>
            {
                if (c.Self is { } card)
                {
                    c.State.AddKredits(card.Owner, 2);
                }

                return null;
            },
            // `IncOpCountAndCheckVeteran()` is Landwehr's private helper:
            // count operations in custom JSON and promote after the third one.
            ["IncOpCountAndCheckVeteran"] = (c, r, a) =>
            {
                var card = SelfArg(c, r, a) ?? c.Self;
                if (card is null)
                {
                    return null;
                }

                int count = JsonGetInt(card, "opCount") + 1;
                JsonSetInt(card, "opCount", count);
                if (count > 2)
                {
                    MakeVeteran(card);
                }
                else
                {
                    PersistCustomFields(card);
                }

                return null;
            },
            ["AddToBattleLog"] = (c, r, a) => null,      // 纯日志
            ["ApplyPincerEffects"] = (c, r, a) =>
            {
                var played = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r) ?? c.Self;
                var targeted = AsCardOrId(c, a.ElementAtOrDefault(1));
                if (played is not null && targeted is not null)
                {
                    ApplyPincerEffects(played, targeted);
                }

                return null;
            },
            ["RemovePincerEffects"] = (c, r, a) =>
            {
                var leaving = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r) ?? c.Self;
                if (leaving is not null)
                {
                    RemovePincerEffects(leaving);
                }

                return null;
            },
            // BP_CardFunctions::DecrementCountdown(cardID, out countdownFinished,
            // out countdownFound): decrement only when countdown_timer exists, persist
            // the new value, and report finished when the decremented value is <= 0.
            ["DecrementCountdown"] = (c, r, a) =>
            {
                var target = AsCardOrId(c, a.ElementAtOrDefault(0));
                if (target is null || !target.CustomJson.ContainsKey("countdown_timer"))
                {
                    return new object?[] { false, false };
                }

                int remaining = JsonGetInt(target, "countdown_timer") - 1;
                JsonSetInt(target, "countdown_timer", remaining);
                PersistCustomFields(target);
                return new object?[] { remaining <= 0, true };
            },
            // `SetCountdown(cardID, countdown, out qqq)` 是 BP_CardFunctions 的
            // 纯包装：解析目标卡、写入卡牌私有 JSON 的 `countdown_timer`，再持久化。
            // 目标在 a[0]，不能退回 receiver（receiver 是施动卡的 cardFunction）。
            ["SetCountdown"] = (c, r, a) =>
            {
                var target = AsCardOrId(c, a.ElementAtOrDefault(0));
                if (target is null)
                {
                    return false;
                }

                JsonSetInt(target, "countdown_timer", IntArg(a, 1));
                PersistCustomFields(target);
                return true;
            },
            ["Array_Reverse"] = (c, r, a) =>
            {
                var copy = new List<CardInstance>(EvalArray(r, a));
                copy.Reverse();
                return copy;
            },

            // ---------------- 第三批（来自新控制流下的多卡组验证）----------------
            //
            // ⚠️ 这两个是「**这张卡自己**当前的费用 / 行动费」，不是"某一方手牌总费用"。
            //    实测实参只有一个 out 槽，主语在 `recv`：
            //        getTotalKreditCost(recv=GetCardFromID_card, out totalKreditCost)
            //        getTotalOperationCost(recv=GetCard_card_36,      out totalOperationCost)
            //    旧实现把它们当成"按阵营求和"（`State.Hand(side).Sum(...)` /
            //    `State.Board(side).Sum(...)`），语义完全不同 ——
            //    `card_unit_big_red_one` 的「手牌里的牌都是 4 费」判据就是
            //    `getTotalKreditCost(卡) == 4`，按整手牌求和永远不可能等于 4，
            //    于是它会把每张手牌都"设成 4 费"，把已经被别的来源改过的牌也算进去。
            ["getTotalKreditCost"] = (c, r, a) => SelfArg(c, r, a)?.KreditCost ?? 0,
            ["getTotalOperationCost"] = (c, r, a) => SelfArg(c, r, a)?.OperationCost ?? 0,
            // `getKreditTempBuffAmount(cardID, out amount)` —— 返回指定来源在
            // 目标卡上的改费偏移。蓝图把它用于 RemoveTheBuff：先确认该来源
            // 确实改过费用，再以 changeType=4 撤销；没有该来源时必须返回 0。
            // 当前内核的费用 buff 账本按 (来源, 临时标记) 分槽，但历史上的
            // changeType=0 改费统一落在永久槽，因此优先查临时槽、再查永久槽，
            // 同时保留两种已建模调用路径的可观测语义。
            ["getKreditTempBuffAmount"] = (c, r, a) =>
            {
                var target = SelfArg(c, r, a);
                if (target is null)
                {
                    return 0;
                }

                int sourceId = IntArg(a, 0);
                if (target.BuffsBySource.TryGetValue((sourceId, true), out var temporary))
                {
                    return temporary.KreditCost;
                }

                return target.BuffsBySource.TryGetValue((sourceId, false), out var permanent)
                    ? permanent.KreditCost
                    : 0;
            },

            // `getAttackTempBuffAmount(cardID, out amount)` has the same
            // receiver/source shape as the kredit query: read the amount
            // contributed by one source from the target card's buff ledger.
            ["getAttackTempBuffAmount"] = (c, r, a) =>
            {
                var target = SelfArg(c, r, a);
                if (target is null)
                {
                    return 0;
                }

                int sourceId = IntArg(a, 0);
                if (target.BuffsBySource.TryGetValue((sourceId, true), out var temporary))
                {
                    return temporary.Attack;
                }

                return target.BuffsBySource.TryGetValue((sourceId, false), out var permanent)
                    ? permanent.Attack
                    : 0;
            },

            // `getAndDecryptKredit` —— 「这张卡当前的费用」，和 `getTotalKreditCost` 同义。
            //
            // ## 为什么必须有它（2026-10-01，对局 508065 #36）
            //
            // 客户端把费用**加密**存在卡对象里，读的时候要"解密"，所以蓝图里到处是
            // `getAndDecryptKredit(卡)` 而不是直接读字段。权威实现见
            // `ref/kards-sim/KardsSim/Bridge/EngineHost.cs:1556`：
            //     ["getAndDecryptKredit"] = (h, a) => Out(a, h.Card_(a[0])?.KreditCost ?? 0)
            //
            // 旧派发表**没有这个键** ⇒ 每次调用都记一笔 Unimplemented 并返回 null
            // ⇒ 下游 `LessEqual_IntInt(null, 3)` 走 `ToInt(null) == 0` ⇒ **恒真**。
            // 于是"按费用过滤"的卡全部退化成"不过滤"：
            // `card_event_atlantic_convoy` 的候选池本该是「US 单位 + 总费 ≤ 3」，
            // 实际变成**全部 363 张 US 单位**，随机抽出了 6 费的 `card_unit_tigercat`
            // （卡面「At the end of your turn, add a SBD 3 DAUNTLESS to your support line.」）
            // —— 它每回合往支援线塞一张轰炸机，把半场塞满，
            // 连锁出 3 条「打不出：半场已满」（#55/#85/#123）。
            //
            // ## 调用形状（全 IR 52 个调用点 / 34 张卡，形状唯一）
            //
            // <code>
            // recv = 卡（15× Array_Get 元素 / 10× tmpLoopCard / 5× tmpTarget / … / 4× 隐式 self）
            // a[0] = 出参槽（`CallFunc_getAndDecryptKredit_decryptedKredit`）—— 不是实参
            // </code>
            // 也就是和 `getTotalKreditCost` **完全同形**，主语在接收者，走 `SelfArg`。
            ["getAndDecryptKredit"] = (c, r, a) => SelfArg(c, r, a)?.KreditCost ?? 0,
            // `GetCombatKeywords(out Keywords, out numberOfKeywords)` 是
            // `UBaseCardObject` 的成员函数。蓝图出参是 `ECombatKeyword[]`（字节枚举）
            // 加数量，**不是卡实例列表**；VM 用 object[] 按序写回两个 out 槽。
            ["GetCombatKeywords"] = (c, r, a) => DoGetCombatKeywords(c, r, a),
            ["GetCardsToTheLeft"] = (c, r, a) =>
            {
                // 同一战线上、位置编号比它小的卡
                var card = AsCard(r) ?? AsCard(a.FirstOrDefault()) ?? c.Self;
                if (card is null)
                {
                    return new List<CardInstance>();
                }

                return c.State.Board(card.Owner).Where(x => x.LocationNumber < card.LocationNumber).ToList();
            },
            ["GetAdjacentCards"] = (c, r, a) =>
            {
                // ⚠️ 必须用 `TargetArg`（**实参优先**）—— `r` 恒为 `cardFunction`
                //    （施法的那张牌自己），用它算相邻会得到"施法方自己槽位 1 的单位"，
                //    而不是目标周围。实测 `card_event_monty` 因此一个单位都没钉住。
                var card = TargetArg(c, r, a);
                if (card is null)
                {
                    return new List<CardInstance>();
                }

                return c.State.Board(card.Owner)
                    .Where(x => Math.Abs(x.LocationNumber - card.LocationNumber) == 1)
                    .ToList();
            },
            // `card_unit_48th_armored_infantry` counts adjacent units around itself.
            ["GetDefenseBuffFromAdjacentUnits"] = (c, r, a) =>
            {
                var card = c.Self ?? TargetArg(c, r, a);
                return card is null
                    ? 0
                    : 2 * c.State.Board(card.Owner)
                        .Count(x => Math.Abs(x.LocationNumber - card.LocationNumber) == 1
                            && IsUnit(x));
            },
            ["DrawSpecificCardFromDeckBySide"] = (c, r, a) => DoDrawSpecific(c, r, a),

            // ---------------- 光环（aura）原语：卡自带的私有函数 ----------------
            //
            // ⚠️ 这一族和别的不一样：`ApplyTheBuff` / `RemoveTheBuff` / `anyOrderPlayedThisTurn`
            //    / `_isBigRedOne` 是**卡蓝图自己的私有函数**，不是引擎 API。
            //    名字在 2053 张卡里会撞车（`card_unit_214th_amur` 和
            //    `card_event_committed_crew` 的 ApplyTheBuff 干的事完全不同），
            //    而且**不在 `card-ir.json` 里** —— IR 生成器只编 `ExecuteUbergraph_*`，
            //    私有函数体被丢掉了（`docs/内核补全队列.md` 说「6 个触发点 IR 全在，
            //    只缺原语」，这句对触发点成立，但对**私有函数体不成立**）。
            //
            //    所以这里的语义是**从 `ref/kards-sim` 的直译产物逐行读出来的**
            //    （`Generated/<阵营>/.../card_unit_85_pioneer_company.g.cs` 等），
            //    不是猜的。按卡名分派，因为同一个名字在不同卡上语义不同。
            ["ApplyTheBuff"] = (c, r, a) => DoApplyTheBuff(c, a),
            ["RemoveTheBuff"] = (c, r, a) => DoRemoveTheBuff(c, a),
            ["anyOrderPlayedThisTurn"] = (c, r, a) => AnyOrderPlayedThisTurn(c),
            ["_isBigRedOne"] = (c, r, a) => a.Length > 0 && AsCard(a[0]) is { } x
                                            && string.Equals(x.Name, c.Self?.Name, StringComparison.Ordinal),
            ["GetCardsPlayedThisTurn"] = (c, r, a) => c.State.CardsPlayedThisTurn.ToList(),

            // `getCardsPlayedFromHandByTurn(回合号)` —— 那一回合「从手牌打出」的卡 **ID** 列表。
            //
            // 蓝图 `ref/kards-sim/…/_deps/BP_GameState_Battle.g.cs:1718`：
            //   `Map_Find(cardsPlayedTurnMapped, turn, …)` → 取到值的 `CardIDs`
            // ⇒ 返回的是**卡 ID（int）**，**不是卡实例** —— 所以卡自己的程序会拿
            //   `GetCardFromID(元素)` 再解析（`didPlayBritishInfantryLastTurn` 就是这么写的）。
            ["getCardsPlayedFromHandByTurn"] = (c, r, a) =>
            {
                int turn = a.Length > 0 ? Blueprint.KismetVm.ToInt(a[0]) : c.State.Turn;
                return c.State.CardsPlayedFromHandByTurn.TryGetValue(turn, out var list)
                    ? list.Select(x => x.CardId).ToList()
                    : new List<int>();
            },

            // `GetCardsPlayedFromHandLastTurn()` = `getCardsPlayedFromHandByTurn(GetTurnNumber() - 1)`
            // —— 蓝图 `BP_CardFunctions.g.cs:20315` 逐字如此。而内核的 `GetTurnNumber()`
            // 就是 `State.Turn`（`Effects/CardApi.cs:2500`）。
            //
            // ★ **为什么需要它**：卡内私有函数 `didPlayBritishInfantryLastTurn`
            //   —— **5 张卡**（`card_event_forward_observers` / `card_unit_baltimore_mk_iii` /
            //   `card_unit_defiant_mk_i` / `card_unit_the_polar_bears` /
            //   `card_unit_valentine_mk_ii`）的体都调它（各自 29 步：
            //   `IsSideActive` + `GetCardsPlayedFromHandLastTurn` ×2 + `Array_Get` +
            //   `GetCardFromID` + `IsInfantry`）。
            //   以前内核**完全没有这个原语** ⇒ 那 5 张卡的本地程序兜底跑出来恒假
            //   ⇒ 「上回合打过英国步兵」分支永不执行（审计里那 4 局的
            //   `<local-ran:didPlayBritishInfantryLastTurn> ×1` 就是这条的留痕）。
            ["GetCardsPlayedFromHandLastTurn"] = (c, r, a) =>
                c.State.CardsPlayedFromHandByTurn.TryGetValue(c.State.Turn - 1, out var prev)
                    ? prev.Select(x => x.CardId).ToList()
                    : new List<int>(),

            // `hasPlayedOrderThisTurn(卡, side, out 有没有)` —— 本回合 **side 这一方**
            // 有没有打过指令牌。
            //
            // ## 为什么必须手写替身（不能让它走卡自己的 locals）
            //
            // 它是**卡内私有函数**，签名需要一个**真的 `side` 入参**。
            // `card_unit_10th_para_battalion` 的 IR 调用点形状是：
            // <code>
            // { op:"call", fn:"hasPlayedOrderThisTurn",
            //   args:[{var:"side"}, {var:"CallFunc_hasPlayedOrderThisTurn_hasPlayedOrderThisTurn"}],
            //   outs:[{param:1, slot:"CallFunc_…"}] }
            // </code>
            // 而 `KismetVm` 那条「本地程序兜底」**不 seed 入参**（见 `KismetVm.cs:586-601`
            // 的「三条取舍」第 3 条：dump 里没有参数名，需要真入参的私有函数会拿到 null）。
            // 于是它读到的 `side` 是帧默认值 ⇒ 判据恒假。
            //
            // ## 后果（回放 508065 `#71 t15 ML`，实测）
            //
            // `card_unit_10th_para_battalion#15001` 的卡面是
            // 「**Deployment: Gets +1+1 and Blitz if you have given an order this turn.**」
            // —— 那一回合人类确实打过指令，所以客户端那边它**有 Blitz** ⇒ **不是召唤失调**
            // ⇒ 允许移动。内核因为判据恒假没给 Blitz ⇒ `CanMoveThisTurn` 判召唤失调
            // ⇒ **拒移**（`移动被拒：单位本回合不能移动（召唤失调 … 进场回合=15 当前回合=15）`）
            // ⇒ 它留在半场 ⇒ `#85 t17` 假「半场已满」。
            //
            // ## 判据
            //
            // 与 `AnyOrderPlayedThisTurn` **同源**（同一个循环），区别只是这里用
            // **显式传入的 side** 而不是"自己那一方"：
            // 蓝图 `hasPlayedOrderThisTurn` 的体遍历 `GetCardsPlayedThisTurn()`，
            // 命中 `IsOrder(卡) && 卡.side == side` 就置 true
            //（`ref/kards-sim/…/Britain/CovertOp/units/card_unit_10th_para_battalion.g.cs:67-151`）。
            //
            // ⚠️ 这是一处**定向**修补：`KismetVm` 那条"不 seed 入参"的**通用**局限仍在，
            //    其它同样需要真入参的卡内私有函数依然会拿到 null（有 `<local-ran:…>` 留痕）。
            ["hasPlayedOrderThisTurn"] = (c, r, a) =>
            {
                Side want = SideArg(r, a, 0, SelfSide(c));
                foreach (var card in c.State.CardsPlayedThisTurn)
                {
                    if (IsOrder(card) && card.Owner == want)
                    {
                        return true;
                    }
                }

                return false;
            },
            ["getHasGameplayTag"] = (c, r, a) => HasGameplayTag(c, r, a),

            // `BP_CardFunctions::CanCardBeBuffed(Card)` 的实现（见 CardApi.CanCardBeBuffed）。
            // 卡蓝图本身不直接调它（调用点是 ChangeAttack/ChangeDefense/ChangeKreditCost/
            // CustomAbilityAdd/Give* 一族），但这里是它的忠实实现，放进来是为了
            // 万一有卡调它时不会静默返回 null。
            ["CanCardBeBuffed"] = (c, r, a) => SelfArg(c, r, a) is { } x && CanCardBeBuffed(x),

            // ⚠️ 同形接收者 bug（审计 §5.1）。IR 实测：3 个调用点里 2 个无 recv，
            //    旧实现 `AsCard(r)?.HeavyArmor ?? 0` ⇒ 恒 0（重甲查询失效）。
            ["getTotalHeavyArmor"] = (c, r, a) => SelfArg(c, r, a)?.HeavyArmor ?? 0,

            // ---- P1（2026-09-30）：`getHas*` 一族 ----------------------------------
            //
            // 出处：审计 §6 的 P1#27f ——「派发表里**没有任何 `getHas*` 键**」。
            // 卡蓝图里这些判据的形状是「如果这张卡有 X 就…」，读不到就**静默取假**，
            // 于是那些分支的方向是反的（该走的没走、不该走的走了）。
            //
            // IR 实测调用点（`klink bot/docs/card-ir.json`，脚本
            // `out/audit/p1-gethas-calls.py`）：
            //     getHasBlitz 9 点 / getHasAlpine 9 / getHasShock 7 / getHasGuard 5 /
            //     getHasAmbush 2 / getHasFury 1 / getHasSmokescreen 1
            //
            // 语义：和 `KismetVm.GetMember` 的成员读**完全同源**（都读 `Keyword` 集合），
            // 所以两处必须同步 —— 成员读走 `hasXxx`，函数调用走 `getHasXxx`。
            //
            // ⚠️ 接收者一律用 `SelfArg`（`c.Self ?? c.Target`），不用 `AsCard(r)`：
            //    这一族大量以**隐式 self** 出现（没有 recv），`AsCard(r)` 会恒 null。
            //    同族的前科见审计 §5.1 的 `IsVeteran` / `IsDamaged`。
            ["getHasBlitz"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Blitz),
            ["getHasGuard"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Guard),
            ["getHasAmbush"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Ambush),
            ["getHasFury"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Fury),
            ["getHasSmokescreen"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Smokescreen),
            ["getHasAlpine"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Alpine),
            ["getHasShock"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Shock),
            ["getHasMobilize"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Mobilize),
            ["getHasSalvage"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Salvage),
            ["getHasPincer"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Pincer),
            ["getHasDeployment"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Deployment),
            ["getHasDestruction"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Destruction),
            ["getHasCovert"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Covert),
            ["getHasScrying"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Scrying),
            // `getHasImmune` 读的是**运行时**授予的免疫（`cardsGivingImmunity` / `isImmune`），
            // 不是 CDO 字段（CDO 里 `isImmune` 出现 0 次）。内核把免疫建模成
            // `Keyword.Immune`（`MatchEngine.ApplyDamage` 就读它），所以这里读同一个集合 ——
            // 语义一致，不是拿近似值顶替。
            ["getHasImmune"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Immune),

            // ══════════════════════════════════════════════════════════════════════
            // 2026-10-02：补「IR 会调用、派发表里没有」的键（第一批）。
            //
            // 量化出处：`out/audit/missing-keys-classify2.py` / `.txt`（A/B/C 分类）。
            //   · IR 里被调用的函数 **760** 种，派发表 **195** 键 ⇒ **608** 种不在表里；
            //   · 其中 **63 种 / 148 调用点** 由 `KismetVm` 的 **locals 兜底**执行
            //     （调用它的那张卡自己在 IR 的 `locals` 里带了这个函数体，
            //      见 `KismetVm.cs:583` 那段与 `SelfTest.LocalFunctionActuallyRuns`）
            //     ⇒ **不是缺口**；
            //   · ⇒ **真缺口 545 种 / 3241 个调用点**。本段只补其中**语义有出处**的一批。
            //
            // ⚠️ 纪律：**语义不明的一律不补**。不补至少会在审计 ⑥
            //    （`GameState.UnimplementedCalls`）里报警；补到语义不对的实现上是
            //    **静默错**，比不补更糟。所以本段每条都带出处。
            // ══════════════════════════════════════════════════════════════════════

            // ── (B) 实现**已经在本仓库里**、只差一个键名 ──────────────────────
            // 分类出处：`out/audit/missing-keys-classify2.txt` 的 (B) 段。
            // B1：IR 名与我们的方法名不同 —— 出处见 `CardApi.DiscardCard` 的文档注释
            //     （`CardApi.cs:1653`：「对应 BP_CardFunctions::DiscardCardFromHand / DiscardCard」）。
            // 参数形状（IR 实测 39 个调用点，`out/audit/ir-callsites.py DiscardCardFromHand`）：
            //     recv=cardFunction, a[0]=被弃的卡（**有时是卡对象、有时是整数 cardID**）,
            //     a[1]=discarderID, a[2]/a[3]=bool（`skipTriggers`/`skipVisuals`）,
            //     a[4]=out success。
            ["DiscardCardFromHand"] = (c, r, a) => DoDiscardCardFromHand(c, r, a),
            // `DiscardRandomCardFromHand(side, instigatorID, out discardedCardID)`。
            ["DiscardRandomCardFromHand"] = (c, r, a) => DoDiscardRandomCardFromHand(c, r, a),

            // B2：`CardApi.IsUnrevealedCovertCard`（`CardApi.cs:1194`）与
            //     `MatchEngine.IsBomber`（`MatchEngine.cs:1529`）都是**同名现成方法**。
            //     `IsUnrevealedCovertCard` 读取卡面的 Covert 关键字与持久化揭示位。
            //     `IsBomber` 则是真修复：13 张卡用它做判据，以前 out 槽恒 null ⇒ 恒假。
            // 形状（`IsBomber` 19 点 / `IsUnrevealedCovertCard` 28 点，全部同一形状）：
            //     recv=被查的卡, a[0]=out —— 用 `SelfArg` 而不是 `AsCard(r)`，
            //     因为这一族里有隐式 self 的调用点（同 `getHas*` 一族的前科，见上面注释）。
            ["IsUnrevealedCovertCard"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && IsUnrevealedCovertCard(x),
            ["IsBomber"] = (c, r, a) => SelfArg(c, r, a) is { } x && MatchEngine.IsBomber(x),

            // ── 语义取自参考实现 `ref/kards-sim/KardsSim/Bridge/EngineHost.cs` ────
            // 这一族的形状**完全一致**：`recv` = 被查的那张卡，`a[0]` = out 槽
            // （逐个用 `out/audit/ir-callsites.py <名字>` 核过）。
            // 为什么这些是**真修复**而不是"把计数器刷绿"：VM 在未处理时
            // **什么都不写回 out 槽**（`KismetVm.cs:606-612`），布尔槽保持 null ⇒ 恒假、
            // 整数槽保持 null ⇒ 恒 0。对"这张卡是战斗机吗 / 它攻击力多少"这种问题，
            // 恒假/恒 0 是**错的**（`IsFighter` 15 点、`getAndDecryptAttack` 33 点）。
            ["IsFighter"] = (c, r, a) => SelfArg(c, r, a) is { } x && x.Definition.Type == "fighter",
            ["IsPinned"] = (c, r, a) => SelfArg(c, r, a) is { } x && x.Keywords.Contains(Keyword.Pinned),
            ["HasBond"] = (c, r, a) => SelfArg(c, r, a) is { } x && HasBond(x),
            // `GetIsGoldCard` is the native `isGoldCard` getter.  Its 24 IR call
            // sites use implicit self and expose only the boolean out slot.
            ["GetIsGoldCard"] = (c, r, a) => SelfArg(c, r, a)?.IsGold ?? false,
            // `WasRightMostCardWhenPlayedFromHand` is a Blueprint wrapper around
            // `JSON_GetBool(Card, "WasRightMostWhenPlayedFromHandKey", ...)`.
            // The first argument is either implicit self or an explicit cardPlayed.
            ["WasRightMostCardWhenPlayedFromHand"] = (c, r, a) =>
                (AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a)) is { } card
                && JsonGetBool(card, "WasRightMostWhenPlayedFromHandKey"),
            // 左侧对应的 Blueprint wrapper，读取同一组动作流标记的 left key。
            // 调用点既有显式 cardPlayed，也有隐式 self，形状与右侧完全一致。
            ["WasLeftMostCardWhenPlayedFromHand"] = (c, r, a) =>
                (AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a)) is { } card
                && JsonGetBool(card, "WasLeftMostWhenPlayedFromHandKey"),
            ["hasActivePincerEffect"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && x.Keywords.Contains(Keyword.Pincer),

            // `getAndDecryptAttack` / `getAndDecryptDefense`：C++ 基类
            // `UBaseCardObject::getAndDecryptAttack`（`BP_CardFunctions.g.cs:6647` 等 33 个调用点）。
            // 参考实现 `EngineHost.cs:1098/1361` 直接返回卡的实时攻/防
            // （"decrypt" 是隐蔽卡的显示解密，本内核没有独立揭示位，故等价于实时值）。
            ["getAndDecryptAttack"] = (c, r, a) => SelfArg(c, r, a)?.Attack ?? 0,
            ["getAndDecryptDefense"] = (c, r, a) => SelfArg(c, r, a)?.Defense ?? 0,

            // `HasAttackLeft(out doesIt)` —— 客户端是 `attackLeft > 0`
            // （`attackLeft` 回合开始被设成 `getHasFury() ? 2 : 1`，每次攻击 -1）。
            //
            // ⚠️ 旧实现写成 `!HasAttackedThisTurn`，并在注释里把它当"近似"记着
            //    （参考实现 `EngineHost.cs:1288` 那句「Fury 之类多次攻击的卡由字段驱动」
            //    被误读成了"不用建模"）。**那个近似是错的**：它让奋战的第二次攻击恒被拒。
            //    实测对局 389594 `#85/#86 t19`：`card_unit_queens_own`（Fury）
            //    同一回合打了两次右 HQ（动作流 19→12→5），内核拒了第二条。
            //    判据与出处见 `CardInstance.HasAttackLeft` 的注释。
            ["HasAttackLeft"] = (c, r, a) => SelfArg(c, r, a) is { } x && x.HasAttackLeft,

            // `IsExile` —— 参考实现 `EngineHost.cs:1106` 就是**无条件 false**
            // （注释：「放逐：引擎暂未建模，恒 false」）。本内核同样没建模 ⇒ 同语义。
            // 这条是**行为中性**的（补不补都是假），补上只是为了让缺口计数反映真实情况。
            ["IsExile"] = (c, r, a) => false,

            // `getKreditBySide(side, out kredit)` / `GetFrontlineOwnerSide(out side)`
            // 的接收者是 `GameStateRef` 而不是卡（IR 实测各 1 个调用点，都在 `BP_BoardCard`）。
            // 参考实现：`EngineHost.cs:1862` / `:1962`。
            ["getKreditBySide"] = (c, r, a) => c.State.Kredits(SideArgOrNull(a, 0) ?? SelfSide(c)),
            ["GetFrontlineOwnerSide"] = (c, r, a) => (int)c.State.FrontlineOwner,

            // ── ★ `SpawnCardInFrontline`（108 调用点 / 29 张卡）──────────────────
            // 出处：直译产物 `out/Generated-gap/_deps/BP_CardFunctions.g.cs:35201`
            //       （函数体只有一步）：
            //   SpawnCardToBoard(card_name, side, 7=BoardFrontline, locationNumber, 0,
            //                    gold, giveBlitz, spawnerID, 0, makeVeteran, out spawnedCardID)
            // 参数顺序出处：同一函数体开头的 `args[i]` 绑定
            //   （a[0]=card_name a[1]=side a[2]=spawnerID a[3]=out campaignName
            //    a[4]=giveBlitz a[5]=out spawnedCardID a[6]=salvageFaction
            //    a[7]=locationNumber a[8]=makeVeteran），与 IR 的 108 个调用点逐个吻合
            //   （`out/audit/ir-callsites.py SpawnCardInFrontline`，例 `card_event_airdrop` i=219）。
            // `gold` 那一支：函数体在 `campaignName` 为空且 `spawnerID > 0` 时取
            //   `spawner.isGoldCard`；本内核有 `CardInstance.IsGold`，照做。
            ["SpawnCardInFrontline"] = (c, r, a) => DoSpawnInFrontline(c, r, a),

            // ── ★ `CustomName1*` / `CustomName2*` 一族（267 调用点）──────────────
            // 语义出处：参考实现 `EngineHost.cs:1088/1307/1498-1500/1639`（含 `SuffixAdd`）。
            // 作用：给一张卡挂「自定义名后缀」标记，之后用 `HasAttribute` 查。
            // 例：`card_event_sea_embargo` / `card_unit_commando` 一族用它记
            //     「这张牌被改造成了什么」。
            // 本内核用 `CardInstance.CustomJson` 存（和客户端的私有 JSON 同一层），
            // 键 `customName1` / `customName2`，值用逗号分隔 —— 与参考实现一致。
            ["CustomName1Add"] = (c, r, a) => { SuffixAdd(c, r, a, "customName1"); return null; },
            ["CustomName1HasAttribute"] = (c, r, a) => SuffixHas(c, r, a, "customName1"),
            ["CustomName1Remove"] = (c, r, a) => { SuffixRemove(c, r, a, "customName1"); return null; },
            ["CustomName2Add"] = (c, r, a) => { SuffixAdd(c, r, a, "customName2"); return null; },
            ["CustomName2HasAttribute"] = (c, r, a) => SuffixHas(c, r, a, "customName2"),
            ["CustomName2Remove"] = (c, r, a) => { SuffixRemove(c, r, a, "customName2"); return null; },
            ["GetCustomName2Attributes"] = (c, r, a) => SuffixList(c, r, a, "customName2"),

            // ── `GetCardsInSupportLineBySide(side, unitsOnly, includeCovert, out cards)`
            //    （41 调用点 / 34 张卡）────────────────────────────────────────────
            // 出处：直译产物 `_deps/BP_CardFunctions.g.cs` 的同名函数体（完整循环）：
            //   遍历 `GetAllCardInBattle()`，
            //   ① `card.side == side` ② `card.location == GetSupportLineBySide(side)`
            //   ③ `!IsUnrevealedCovertCard(card) || includeCovertCards`
            //   ④ `unitsOnly` 为真时再要求 `IsUnit(card)`
            // 本内核里「半场」就是 `side.HqOf()`（`BoardHqLeft/Right`，见 `Enums.cs` 的注释），
            // 所以 ② 直接等价；③ 按 `IsUnrevealedCovertCard` 过滤；
            // ④ 按参数过滤。**HQ 不算**：`GetAllCardInBattle` 含 HQ，但 HQ 也是 location 卡、
            // 不是单位 ⇒ 只要 `unitsOnly` 为真就天然被 ④ 挡掉；为假时保留（与蓝图同）。
            ["GetCardsInSupportLineBySide"] = (c, r, a) => DoGetCardsInSupportLine(c, r, a),
            ["GetCardsInFrontlineBySide"] = (c, r, a) =>
            {
                Side side = SideArg(r, a, 0, c.Controller);
                bool unitsOnly = TruthyArg(a, 1);
                var cards = c.State.Cards(side, CardLocation.BoardFrontline);
                return unitsOnly ? cards.FindAll(IsUnit) : cards;
            },
            ["FullyHealCard"] = (c, r, a) =>
            {
                var target = TargetArg(c, r, a);
                if (target is null) return new object?[] { 0 };
                return new object?[] { c.Engine.Api.FullyHealCard(target) };
            },
            ["GetCardsPlayedFromHandThisTurn"] = (c, r, a) =>
                c.State.CardsPlayedThisTurn.Select(card => card.CardId).ToList(),

            // ── `IsLocationFull(location, out isFull)`（20 调用点 / 17 张卡）────
            // 出处：直译产物 `_deps/BP_CardFunctions.g.cs` 同名函数体 ——
            //   唯一一步是 `FetchCardsByLocation(location, out QtyInLocation,
            //   out isLocationFull, …)` 然后把 `isLocationFull` 转出去。
            // 容量规则（`out/Generated-gap` 的 `FetchCardsByLocation` 体）：
            //   前线 7 → `GameStateRef.FrontlineCapacity`（默认 5，有 limiter 时 2）；
            //   半场 5/6 → `HalfBoardCapacity`（5，**含 HQ 占 1 格**）；
            //   手牌 3/4 → `HandCapacity`（9）；牌库/弃牌堆 → 永不"满"。
            ["IsLocationFull"] = (c, r, a) => DoIsLocationFull(c, r, a),

            // ── `DestroyMultipleCards(cardsToDestroy, destroyerCardID, out)`（20 点）─
            // 出处：直译产物同名函数体 —— 数组非空就调
            //   `ApplyDestroyMultipleCards(self, destroyerCardID, out cardsToDestroy)`。
            // `ApplyDestroyMultipleCards` 也是缺失键，本内核没有；但它做的事就是
            // 「逐张 Destroy」。这里直接按顺序逐张 `DestroyCard`（同序，避免额外依赖）。
            ["DestroyMultipleCards"] = (c, r, a) => DoDestroyMultipleCards(c, r, a),
            ["SalvageMultipleUnits"] = (c, r, a) => DoSalvageMultipleUnits(c, r, a),

            // `AddDefenseToMultipleCards(receiverIDs, amount, giverCardID, out qqq)`
            // is the batch primitive used by Root Out the Enemy and 119 Grenadier.
            // The Blueprint walks an integer ID array in order, applies the same
            // relative defense change as `ChangeDefense`, then adjusts maxDefense
            // by the amount for surviving cards and destroys cards reduced to 0.
            ["AddDefenseToMultipleCards"] = (c, r, a) => DoAddDefenseToMultipleCards(c, r, a),

            // `ForceCardChangeLocation(cardID, instigatorID, newLocation,
            // newLocationNumber, out moved, out oldLocation, out oldLocationNumber)`.
            // The Blueprint rejects invalid / cantMove cards and treats an
            // identical location+slot as a no-op.  The actual transition goes
            // through GameState.Move so the normal location trigger remains the
            // single movement path.
            ["ForceCardChangeLocation"] = (c, r, a) => DoForceCardChangeLocation(c, a),

            // ── `DiscardCardFromDeck(cardID, discarderID, skipTriggers, skipVisuals, out)` ─
            // 出处：直译产物同名函数体（`_deps/BP_CardFunctions.g.cs`）：
            //   ① `cardID > 0` 且卡有效 ② 卡在牌库里（location 1/2）
            //   ③ `OnAttemptedDiscard` 没取消 ④ `RemoveCardFromDeckBySide` +
            //   `SetCardLocationAndLocNumber(cardID, 8=Discard, 0)` + `NotifyDiscardCard`。
            // 本内核的 `DiscardCard` 做的就是「移到 Discard + 广播 `OnOtherCardDiscarded`」，
            // 所以直接复用它（**不实现** `OnAttemptedDiscard` 的取消门 —— 本内核没有这个事件，
            // 见「没修的」清单；这会少一次取消机会，不会多弃牌）。
            ["DiscardCardFromDeck"] = (c, r, a) => DoDiscardCardFromDeck(c, r, a),

            // ── Gotcha（反制卡）子系统 ──
            // 按蓝图重写（`BP_CardFunctions.g.cs:23719-23912` 等五处，逐行依据见
            // `CardApi` 里同名方法的注释）。这一族在旧内核里**完全没有**，
            // 原先这里只留了一段「故意不注册」的注释 —— 那是**错的**：
            // 判据并不需要什么"gotcha 标记的写入方"，卡定义的类型就是权威
            // （52 张 `type == gotcha`，与 IR 里调用 `GotchaTriggered` 的 52 张集合相同）。
            //
            // ★★ `ShouldGotchaTrigger` 的 self 是**隐式 self**，不是 `a[0]`
            //    （53 个调用点的 IR 形状全部是 `args=[触发卡, out]` 且 `recv` 为空）。
            //    用 `SelfArg` 会**扫到 `a[0]`**（= 触发卡）⇒ 判据绑错卡。
            ["ShouldGotchaTrigger"] = (c, r, a)
                => ShouldGotchaTrigger(r as CardInstance ?? c.Self),
            // `IsGotcha(card, out isIt)` —— 16 个调用点，`a[0]` 恒为 out 槽，接收者才是被查的卡。
            ["IsGotcha"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsGotcha(x),
            // `GotchaTriggered(card, instigatorID, stopFurtherCardActions, skipDiscardingOrder, out qqq)`。
            // 实参形状（`docs/card-ir.json`，54 个调用点）：
            //   a[0] = `{self:true}` ×**54**（全部；`a[0]` 是 gotcha 卡本身）
            //   a[1] = `cardID`（触发卡的） ×52 / `K2Node_Event_instigator` ×1 / `{int:0}` ×1
            //   a[2] = bool（stopFurtherCardActions）  a[3] = bool（skipDiscardingOrder）
            // `recv` 恒为 `cardFunction`（= `ctx.Self`）⇒ 与 `a[0]` 同源，互为兜底。
            ["GotchaTriggered"] = (c, r, a) =>
            {
                var gotcha = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? AsCard(r) ?? c.Self;
                if (gotcha is not null)
                {
                    GotchaTriggered(gotcha, IntArg(a, 1), TruthyArg(a, 2), TruthyArg(a, 3));
                }

                return null;
            },
            // `GetActiveGotchasOrdered(out cardIDs)` —— 出参是**整数卡 ID 数组**。
            ["GetActiveGotchasOrdered"] = (c, r, a) => GetActiveGotchasOrdered(),
            // ── ⚠️ **故意不注册** `GetHandLocationBySide`（有实现，但只给内核内部用）──
            //
            // `CardApi.GetHandLocationBySide` 已按蓝图实现
            //（`BP_CardFunctions.g.cs:20904-20930`：`side == 1 → 3`，否则 `4`），
            // `GotchaTriggered` 内部就调它（`:23743`）。
            //
            // 但**注册成派发键**会让 IR 里 5 个既有调用点（`card_event_aerial_reconaissance`
            // / `card_event_night_raid` / `card_unit_me_410_hornisse` /
            // `card_event_orp_blyskawica` / `card_event_supply_chain`）从「out 槽不写 ⇒
            // 读成 null」变成**真实手牌位置 3/4**，而那 5 处全是**比较/门**：
            // <code>
            // aerial_reconaissance : handLocation == item.location          ; "这张牌在我手里吗"
            // me_410_hornisse      : card.location  == handLocation         ; 同上
            // night_raid / orp_blyskawica / supply_chain : IsLocationFull(handLocation)
            // </code>
            // 旧行为下第一族恒假（null → `EqualEqual_ByteByte(null, 3/4)` 假）、
            // 第二族问的是 `NotAvailable(0)` 满不满。
            //
            // **实测（A/B，命令见报告）**：注册它以后对局 `854099` 从 **106/118 掉到 100/118**
            // （其余 8 局逐位不变）。也就是说这里是典型的「**两个错抵消**」
            //（README §7.3）：门恒假掩盖了内核下游的另一个偏差，把门修对反而暴露出来。
            // 任务书硬性要求「任何一局应用率下降都算失败 ⇒ 回退」，
            // 所以**回退注册**、把 `GetHandLocationBySide` 留在缺口集合里（如实计数），
            // 只在 `GotchaTriggered` 内部用它 —— 那一处是蓝图明确要求的
            //（`:23743-23745`，且 0 个 IR 调用点，不影响上面那 5 张卡）。
            //
            // ["GetHandLocationBySide"] = (c, r, a) => (int)GetHandLocationBySide(SideArg(r, a, 0)),
            // `RearrangeLocation(location)` —— 只有 1 个实参，没有出参。
            // IR 里 0 个调用点（只被库函数 `GotchaTriggered` / `SuppressMultipleUnits` 调），
            // 注册它是为了让「名字 → 实现」可查。
            ["RearrangeLocation"] = (c, r, a) =>
            {
                RearrangeLocation((CardLocation)IntArg(a, 0));
                return null;
            },
            // `SetCardsSeenByCipher(numberOfCardsSeen, instigatorID, side, out qqq)`
            // 2 个调用点（`card_event_cruiser_scouts` a[0]=`{int:3}` /
            // `card_event_stretch_the_line` a[0]=`{var:count}`），`recv` 恒 `cardFunction`。
            ["SetCardsSeenByCipher"] = (c, r, a) =>
            {
                SetCardsSeenByCipher(IntArg(a, 0), IntArg(a, 1), SideArg(r, a, 2));
                return null;
            },
            // `SetCardSeen(cardID_Seen, instigatorID, out qqq)`。
            // 蓝图的目标是 a[0]，a[1] 只用于客户端通知；qqq 固定为 0。
            ["SetCardSeen"] = (c, r, a) => SetCardSeen(IntArg(a, 0), IntArg(a, 1)),
            // `AddIntelToCard(cardID, instigatorID, amount, out qqq)`
            // 3 个调用点（全在 `card_unit_lublin_r_xiii`），a[0] 是**整数 cardID**。
            ["AddIntelToCard"] = (c, r, a) =>
            {
                AddIntelToCard(IntArg(a, 0), IntArg(a, 2));
                return null;
            },
            // `SetStopFurtherActions(GameStateRef, bool)` / `GetStopFurtherActions(out bool)`。
            // IR 里 0 个调用点（只被库函数调），注册是为了名字可查 + 自测可直接断言。
            ["SetStopFurtherActions"] = (c, r, a) =>
            {
                SetStopFurtherActions(TruthyArg(a, 0));
                return null;
            },
            ["GetStopFurtherActions"] = (c, r, a) => GetStopFurtherActions(),
        };

    /// <summary>
    /// `getHasXxx()` 一族：这张卡现在有没有关键字 <paramref name="keyword"/>。
    ///
    /// 接收者用 <see cref="SelfArg"/>（隐式 self 兜底 `c.Self ?? c.Target`）。
    /// 卡拿不到（IR 参数位错等）时返回 false —— 和旧行为一致，不会凭空为真。
    /// </summary>
    private static bool HasKeyword(EffectContext c, object? receiver, object?[] args, string keyword)
        => SelfArg(c, receiver, args)?.Keywords.Contains(keyword) ?? false;

    /// <summary>
    /// 「从牌库里挑一张」—— 按反编译蓝图实现（<c>BP_CardFunctions.selectCardToDraw</c>）。
    ///
    /// 签名（<c>ref/kards-sim/KardsSim/Generated/_index.g.cs</c> 登记的就是它）：
    /// <code>
    /// selectCardToDraw(cardSelectingCardToDraw:Int, selectFromTopOfDeck:Bool, isEffect:Bool,
    ///                  out drawnCardID:Int)
    /// </code>
    /// 调用点实例：<c>card_event_pams.OnPlayedFromHand</c> 的 i=822
    /// （<c>args=[cardID, false, false, out]</c>）。
    ///
    /// 蓝图原逻辑（逐条对应 <c>BP_CardFunctions.g.cs</c> 的 L_xxxx 标号）：
    /// <list type="number">
    /// <item><c>L_064B</c>：<c>!isEffect &amp;&amp; IsLocationFull(手牌)</c> → 直接返回 0，
    ///   什么都不抽。（所以这里是"手牌满了就不抽"，而不是"抽了再弃"。）</item>
    /// <item><c>L_0680</c>：候选 = <c>cardSelecting.GetChooseSpawnCards()</c>
    ///   （出参 <c>cards / markAsSeen / keepOrder</c>）；<c>keepOrder</c> 为假时先
    ///   <c>Array_ShuffleFromStream(cardsRandomStream)</c> 洗一遍。</item>
    /// <item><c>L_0745</c>：<c>keepOrder</c> 分支下候选只取
    ///   <c>0..Min(2, LastIndex)</c> —— **最多 3 张**，这正好解释了回放里
    ///   <c>CS</c> 动作第 2 槽恒为 0/1/2。</item>
    /// <item><c>L_0B57</c>（只有 1 张候选）：直接
    ///   <c>DrawSpecificCardFromDeckBySide</c> + <c>OnHandTargetSelected</c>。</item>
    /// <item><c>L_0D61</c>（多张候选）：只发 <c>NotifySelectCardToDrawPending</c>，
    ///   **不在本函数里抽牌** —— 抽牌由答复（回放里的 <c>CS</c> 动作）触发。</item>
    /// </list>
    ///
    /// **本内核的落地方式（2026-09-27 打通）**：
    /// <list type="bullet">
    /// <item>候选表：执行那张卡自己的 <c>GetChooseSpawnCards</c>（已编进 IR 的 <c>locals</c>），
    ///   见 <see cref="GetChooseSpawnCards"/>。</item>
    /// <item>答复来源：回放路径走 <c>MatchEngine.PickCardToDraw</c>，选中的是
    ///   <c>CS</c> 动作第 3 槽的**卡组码 → 卡名**（比用下标猜候选表可靠）；
    ///   自对弈路径退化到"候选表第一张"（蓝图**没有** Develop 族的自动兜底）。</item>
    /// <item>落实：按卡名 <c>Create</c> 一张新卡（<see cref="DevelopChosenCard"/>）。</item>
    /// </list>
    ///
    /// ⚠️ 仍然没做到的：`keepOrder` 为假时蓝图会 `Array_ShuffleFromStream` 洗候选，
    ///    再取前 3 张 —— 回放路径不需要它（答复自带卡名），但**自对弈路径**的
    ///    "第一张"与真实客户端的"洗完第一张"不是同一个分布。这是近似，不是复刻。
    /// </summary>
    private object? SelectCardToDraw(EffectContext c, object? r, object?[] a)
    {
        int selectingId = IntArg(a, 0, c.Self?.CardId ?? 0);
        var selecting = c.State.ById(selectingId) ?? (AsCard(r) ?? c.Self);
        bool selectFromTopOfDeck = TruthyArg(a, 1);
        bool isEffect = TruthyArg(a, 2);

        // side 取自"挑牌的那张卡"自己：`selectCardToDraw` 的第一个参数就是它的 cardID，
        // 而它此刻**可能已经在弃牌堆里**（指令打出后立刻进弃牌堆），
        // 所以不能用 `IsLocatedOnBoard` 之类的在场判据。
        Side side = selecting?.Owner ?? c.Controller;

        // 蓝图 L_064B：非效果选择 + 手牌已满 → 不抽
        if (!isEffect && c.State.Hand(side).Count >= GameState.HandCapacity)
        {
            return 0;
        }

        if (selecting is null)
        {
            return 0;
        }

        // ⚠️ 蓝图里这是**两条完全不同的路**，候选的来源不一样：
        //   `selectFromTopOfDeck == true`  → `L_0005~L_05A0`：候选 = **牌库里的实例**
        //                                    （`FilterCardsToScry` 过一遍，取前 3 张）。
        //   `selectFromTopOfDeck == false` → `L_05A5` 起：候选 =
        //                                    `卡自己的 GetChooseSpawnCards()`，
        //                                    是 **`GetAllActiveStaticCards()` 过滤出的卡池模板**。
        //   旧实现把两条路混成一条（都当牌库实例），Develop 那一族的候选永远对不上。
        if (selectFromTopOfDeck)
        {
            // 牌库族的候选 = 牌库里的实例（蓝图 `FilterCardsToScry` 取前 3 张）。
            var deckCandidates = c.State.Deck(side).Take(3).ToList();

            CardInstance? chosen;
            if (c.Engine.ChooseSpawnCard is { } chooseDeck)
            {
                // ★ bot 自己决策：把**候选表**给它（见 `ChooseSpawnCard` 的注释）。
                chosen = chooseDeck(selecting, deckCandidates);
            }
            else if (c.Engine.PickCardToDraw is { } pickDeck)
            {
                chosen = pickDeck(selecting, true, isEffect);
            }
            else
            {
                // 无答复源（自对弈）：按蓝图自带兜底 —— `BP_Logic.autoPickCardToDraw`
                // 在没有选择界面时取 `GetDeckBySide(side).DeckCardIDs[0]`，即牌库第一张。
                chosen = c.State.Deck(side).FirstOrDefault();
            }

            return DrawChosenCardToHand(selecting, chosen)?.CardId ?? 0;
        }

        // ---- Develop 族 ----
        // 候选表由**那张卡自己**的 `GetChooseSpawnCards` 算（每卡过滤条件不同）。
        var candidates = GetChooseSpawnCards(c, selecting, out _, out bool keepOrder);

        // ⚠️ **`keepOrder` 为假时蓝图会先洗一遍候选表，本内核故意不做**。
        //    （2026-10-02 实测否决；2026-10-02 凌晨六 补上穷举否证与边界说明。）
        //
        //    `BP_CardFunctions::selectCardToDraw` 的 L_08C6
        //    （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:33783`）确实是
        //    `Array_ShuffleFromStream(possibleChooseCards, cardsRandomStream)`；
        //    我按"游标必须对齐"的理由实现过一次 —— **结果整体变差**：
        //    人类失败 13 → 22、应用 395/417 → 386/417，`773639` 更是从 0 条人类失败
        //    退回到 5 条。
        //
        //    ⚠️ **当年注释里那条推断（"我们的 `keepOrder` 判定与客户端不一致"）已被证伪**：
        //    pams 蓝图里 `keepOrder = false` 是**硬编码字面量**（`card_event_pams.g.cs:245`），
        //    内核 IR 里也是 `false`（`GetChooseSpawnCards` 的 `i=824`）⇒ **两边一致**。
        //    所以"锅在 keepOrder"这条要划掉 —— 它只是当年那个回归的现象解释，不是原因。
        //
        //    ⇒ 改用**穷举**去否证「补上这次洗牌」这个方案本身。模型 =「洗牌后取
        //      `shuffled[候选下标]`」，判据 = 6 局各自的第一次 pams 构成 **6 个独立约束**
        //      （对 1 个未知量 ⇒ **只有全中才算证据**，命中 1~2 个是噪声）。
        //      已全灭的维度：池口径(41/71/54/84) × 排序键(7 种) × 全局起始偏移 K(0..2000)
        //      × 洗牌消耗次数(n/n−1) × **洗牌方向**(前向/后向/后向含 i=0/前向全区间)
        //      × 不洗牌模型；消费顺序也已从蓝图定死（`OnPlayedFromHand` 只调
        //      `selectCardToDraw`，`OnHandTargetSelected` 才做那次 `RandRange`）⇒ 不是顺序造成的。
        //
        //    ⚠️ **边界**：上面那个否证把 `CS` 的字段 `1` 当成"洗牌后数组的下标"，
        //      而这一点**没有独立证据** —— `WireAction.cs:156` 里 `CS` 的 `CardId` 取自字段 `"2"`，
        //      字段 `"1"` 只被 `SecondId` 读出来记日志；参考实现的 `autoPickCardToDraw`
        //      也是用 `Map_Find` **按键**取待选项的。所以两种可能都存在：
        //      字段 `1` 是下标 ⇒ 已被上表否证；字段 `1` 不是下标 ⇒ 这个模型**离线不可验证**。
        //      **两种情况下都不该实现它。**
        //
        //    ⇒ 完整依据、复算配方与「哪些还没排除」：`README.md` §9.1.7
        //      与 `klink bot/docs/内核补全队列.md` 的「2026-10-02（凌晨六）」一节。
        //
        //    （回放路径本来也不需要它：答复自带卡名，不依赖候选表的顺序。）
        _ = keepOrder;

        CardInstance? picked;
        if (c.Engine.ChooseSpawnCard is { } chooseDevelop)
        {
            // ★ bot 自己决策：拿到**候选表**，可以真正择优（见 `ChooseSpawnCard` 的注释）。
            //
            // 这条分支原先**不存在** ⇒ bot 的开发牌走下面 `PickCardToDraw`，
            // 而那个答复源在 bot 自己回合里永远是空的 ⇒ `return 0`
            // ⇒ **整张开发牌什么都不做**（实测症状：客户端收不到 `CS`、
            //    手牌也没多出选中的牌）。
            picked = chooseDevelop(selecting, candidates);
        }
        else if (c.Engine.PickCardToDraw is { } pickDevelop)
        {
            // 有答复源（回放路径）：答复只可能来自动作流。
            // ⚠️ 取不到时**什么都不生成**，返回 0 —— 蓝图在"候选为空"时也是这条路径
            //    （L_095A 判 `Array_Length > 0`，为假就整段跳过）。
            //    绝不能拿候选表第一张顶替：那是**另一张牌**，而且是静默改错。
            //    取不到的答复由回放驱动那边如实记成"未应用"。
            picked = pickDevelop(selecting, false, isEffect);
        }
        else
        {
            // 自对弈：没有玩家可问。蓝图本身**没有** Develop 族的自动兜底
            // （`autoPickCardToDraw` 取的是牌库第一张，那是牌库族的东西），
            // 所以这里退化成"取候选表第一张"，并在下面注明这是近似。
            picked = candidates.Count > 0 ? candidates[0] : null;
        }

        if (picked is null)
        {
            return 0;
        }

        // ★ 留痕：把「哪张卡选、选了第几个候选、选中哪个码」记下来，
        //   好让 `BotTurnService` 产出一条 `CS` 动作发给客户端。
        //
        // 没有这一步，客户端**收不到任何选择** ——
        // 表现成用户报的「AI 不会选开发」（2026-10-02 从真回放查出来的）。
        {
            int idx = candidates.FindIndex(x => ReferenceEquals(x, picked));
            if (idx < 0)
            {
                idx = candidates.FindIndex(x => x.Name == picked.Name);
            }

            string? code = c.State.Database.DeckCodeFor(picked.Name);
            if (idx >= 0 && !string.IsNullOrEmpty(code))
            {
                c.Engine.RecordPick(selecting.CardId, idx, code, picked.Name);
            }
        }

        // 答复指向的必须是**卡池模板**（`CardId == 0`、不在对局里）——
        // 落实方式是"按卡名新生成一张"（见 DevelopChosenCard 的出处注释）。
        var created = DevelopChosenCard(selecting, picked.Name);
        return created?.CardId ?? 0;
    }

    /// <summary>
    /// 把"选中的那张牌"落实成状态变化：从牌库抽到手牌，并给挑牌的那张卡派发
    /// <c>OnHandTargetSelected(handTargetCardID, instigatorID)</c>。
    ///
    /// 依据：蓝图单候选分支（<c>BP_CardFunctions.g.cs</c> L_0B57~L_0C5B）就是
    /// <c>DrawSpecificCardFromDeckBySide(挑牌卡, 选中卡.cardID, side, false)</c>
    /// 紧跟 <c>OnHandTargetSelected(挑牌卡, 选中卡.cardID, 0)</c>。
    /// 事件契约（<c>docs/event-contracts.json</c>）是 <c>[Int handTargetCardID, Int instigatorID]</c>，
    /// 49 张卡订阅它 —— 例如 <c>card_event_pams</c> 的 <c>OnHandTargetSelected</c>
    /// 会把自己 develop 出来的那张牌塞回牌库的随机位置，漏掉这一步就少一次洗牌。
    ///
    /// 公开给回放驱动用：回放里的 <c>CS</c> 动作到达时，如果挑牌卡的效果**没**走到
    /// <c>selectCardToDraw</c>（例如那张牌根本没打出去），答复还得照样落实，
    /// 否则这条动作就白丢了。
    /// </summary>
    public CardInstance? DrawChosenCardToHand(CardInstance? selecting, CardInstance? chosen)
    {
        if (chosen is null || selecting is null)
        {
            return null;
        }

        Side side = selecting.Owner;
        if (chosen.Location != side.DeckOf())
        {
            // 选中的牌不在牌库里（已经被抽走/换走）—— 蓝图里候选表就是牌库里的卡，
            // 所以这种情况属于内核状态已经偏了，**什么都不做**比硬抽一张更安全。
            return null;
        }

        State.Move(chosen, side.HandOf());

        // 事件参数顺序按 docs/event-contracts.json 的 slots 声明：
        //   handTargetCardID 在前、instigatorID 在后。
        // VM 侧 `K2Node_Event_*CardID` 解析成 `ctx.Trigger/Target` 那张卡的 ID，
        // 所以 eventSubject 必须是**被选中的卡**；而跑哪张卡的程序由 subject 决定。
        FireTrigger("OnHandTargetSelected", selecting, side,
                    eventArgs: new object?[] { chosen.CardId, selecting.CardId },
                    eventSubject: chosen);

        ResolveTriggerQueue();

        return chosen;
    }

    /// <summary>
    /// `selectTargetFromHand` —— 「**从手牌里挑一张**」。
    ///
    /// 用它的典型卡是 `card_unit_gordon_highlanders`：
    /// 「Deployment: Choose an order in hand. Set its cost to 0 and put it on top of your deck.」
    ///
    /// ## ⚠️ 旧实现是空壳
    ///
    /// <code>
    /// ["selectTargetFromHand"] = (c, r, a) => c.Target,   // ← 什么都不做
    /// </code>
    ///
    /// 既不问玩家、也不触发 `OnHandTargetSelected`。而客户端那边会发一条
    /// `HT`（`XActionHandTargetSelected`）说明选了哪张 —— 我们的 `ReplayRunner`
    /// **也没处理 `HT`**。两条路都断 ⇒ 那张手牌**既没被设成 0 费、也没回牌库**。
    ///
    /// **实测**（雪雾 2026-10-01，对局 781364）：
    /// <code>
    /// #155 t25 L PC  {"0":"10", …}   card_unit_gordon_highlanders
    /// #156 t25 L HT  {"0":"10","1":"7"}      ← 选了手牌 7
    /// #157 t25 L PC  …                        ← 从 t25 起状态漂开
    /// </code>
    /// 之后 t27 一片动作应用失败（移动被拒、半场已满…）。
    ///
    /// ## 契约
    ///
    /// 与 `DrawChosenCardToHand` 一致：选中后对**被选中的卡**派发
    /// `OnHandTargetSelected(chosen.CardId, selecting.CardId)`
    /// （事件契约 `docs/event-contracts.json`：`[Int handTargetCardID, Int instigatorID]`）。
    /// `gordon_highlanders` 自己的 `OnHandTargetSelected` 程序就在这次广播里跑，
    /// 做 `ChangeKreditCost` + `MoveCardToTopOfOwnersDeck`。
    /// </summary>
    private object? DoSelectTargetFromHand(EffectContext c, object? r, object?[] a)
    {
        var selecting = AsCard(r) ?? c.Self;
        if (selecting is null)
        {
            return null;
        }

        // 候选 = 手牌里的**指令**。
        // 卡面明确写 "Choose an **order** in hand"；把单位也放进候选会让
        // 后续的 `ChangeKreditCost` / `MoveCardToTopOfOwnersDeck` 落到错的卡上。
        var hand = c.State.Hand(selecting.Owner)
            .Where(x => string.Equals(x.Definition.Type, "order", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hand.Count == 0)
        {
            return null;   // 手里没指令 —— 蓝图在这种情况下也是整段跳过
        }

        CardInstance? chosen;
        if (c.Engine.ChooseHandTarget is { } botPick)
        {
            chosen = botPick(selecting, hand);
        }
        else if (c.Engine.PickHandTarget is { } replayPick)
        {
            chosen = replayPick(selecting, hand);
        }
        else
        {
            chosen = hand[0];   // 兜底：第一张（自对弈路径）
        }

        if (chosen is null)
        {
            return null;
        }

        // 留痕 —— `BotTurnService` 靠它产出 `HT` 发给客户端。
        c.Engine.RecordHandTargetPick(selecting.CardId, chosen.CardId);

        FireTrigger("OnHandTargetSelected", selecting, selecting.Owner,
                    eventArgs: new object?[] { chosen.CardId, selecting.CardId },
                    eventSubject: chosen);

        return chosen;
    }

    /// <summary>从牌库中找出指定卡并抽到手牌（对应 DrawSpecificCardFromDeckBySide）。</summary>
    private object? DoDrawSpecific(EffectContext c, object? r, object?[] a)
    {
        // 实参形状（实测 46 个调用点，完全一致）：
        //     DrawSpecificCardFromDeckBySide(instigatorID, 目标卡.cardID, side, bool)
        //   → **side 在 index 2**，不是 0。index 0 是 instigatorID（整数），
        //     `SideArg` 只认 1/2，instigatorID 恰好是 1 或 2 时会**匹配到错误的阵营**，
        //     也就是"抽对手的牌库"。这种错误不报错、只会静默抽错人。
        //
        // ★ 2026-10-02 修：`a[1]` **永远是卡 ID（整数）或卡实例**，从来不是
        //   `"card_xxx"` 字符串字面量 —— IR 实测 46 个调用点的 `a[1]` 形状分布：
        //     `MEMBER(cardID)`（即 `某张卡.cardID`）×38、`VAR(cardID)`×5、
        //     `VAR(tmpCard)` / `VAR(cardFound)` / `VAR(cardToDraw)` 各 1 —— **0 个是名字字面量**。
        //   而旧实现只调 `FindCardNameArg`（判据是「以 `card_` 开头的**字符串**」）
        //   ⇒ **46 个调用点全部返回 null ⇒ 一个都不抽**。
        //   这就是"不报错、只是什么都不做"的语义错：它在派发表里 ⇒
        //   **不计入「未实现原语」**，烟雾测试只看到「零变化」，一直没被发现。
        //   实测症状（`out/audit/smoke-all-cards.tsv`）：调用它的 41 张卡里
        //   **27 张状态零变化**，含卡面极明确的
        //   `card_event_arctic_convoy`「Draw two random units from your deck.」
        //   （它确实调了 `GetRandomCard`、消耗了 2 次随机数，却什么都没抽上来）。
        var side = SideArg(r, a, 2, c.Controller);

        // 先按 a[1] 解析（ID / 实例 / 名字字面量都能认），认不出来再退回原来的全参数扫名。
        CardInstance? byId = a.ElementAtOrDefault(1) switch
        {
            CardInstance inst => inst,
            int id when id > 0 => c.State.ById(id),
            _ => null,
        };

        string? cardName = byId?.Definition.Name ?? FindCardNameArg(c, a);
        if (cardName is null)
        {
            return null;
        }

        // 能按 ID 定位就按 ID 定位（牌库里可能有同名多张，按名字取第一张会拿错实例）。
        CardInstance? match = byId is not null && c.State.Deck(side).Contains(byId)
            ? byId
            : c.State.Deck(side).FirstOrDefault(x => x.Name == cardName || x.Definition.Name == cardName);
        if (match is null)
        {
            return null;
        }

        // 线上函数不是“直接塞进手牌”：它先把目标卡放回牌库顶，
        // 再调用 DrawTopCardFromDeck。这样才能保留手牌满时烧牌、抽牌动作日志、
        // OnCardDrawnFromDeck / OnOtherCardDrawnFromDeck 以及位置号规范化。
        c.State.Move(match, side.DeckOf(), -1);
        return c.Engine.DrawCard(side);
    }

    /// <summary>
    /// 往牌库里生成卡。
    ///
    /// 权威签名（`ref/kards-sim/KardsSim/Generated/_index.g.cs`）：
    /// <code>
    /// SpawnCardInDeckBySide(side, card_name, spawnerID, numberOfCards, salvageFaction,
    ///                       HideFromOpponent, bottom, shuffle, SkipDrawAnimation,
    ///                       RandomWithoutShuffle, out spawnedCardIDs)
    /// </code>
    /// 蓝图体（同目录 `BP_CardFunctions.g.cs` 的 `SpawnCardInDeckBySide`）：
    /// <code>
    /// Temp_int_Variable = 1
    /// while (Temp_int_Variable &lt;= numberOfCards)      ← ★ **真的循环 numberOfCards 次**
    ///     CreateCard(side, card_name, 牌库, locNum=0, …)
    ///     AddCardToDeckBySide(side, cardID, Not(bottom), SelectInt(rand, -1, RandomWithoutShuffle))
    ///     Temp_int_Variable++
    /// if (shuffle) ShuffleDeckBySide(side, …)
    /// </code>
    ///
    /// ⚠️ **`numberOfCards` 以前被整个忽略**（只建 1 张）。实测
    /// `card_event_fog_of_war`（IR i=95）传的就是 `int 2`，卡面写着
    /// 「Put **two** copies on top of owner's deck.」—— 对局 `773639` `#29 t7`
    /// 那张雾战因此少塞了 1 张。
    ///
    /// ⚠️ **仍未修（如实记录，别当成已实现）**：
    /// <list type="bullet">
    /// <item><c>bottom</c>（a[6]）/ <c>shuffle</c>（a[7]）是**两个 bool**，
    ///   而这里用的是「扫到某个 int 等于 2 就当放牌库顶」的旧启发式 ——
    ///   在 `numberOfCards == 2` 时碰巧对，`numberOfCards == 1`（28 个调用点里 21 个）
    ///   时会把本该放**顶**的卡放到**底**。没改是因为它会同时挪动 19 个调用点的落点，
    ///   需要单独一轮对拍归因。</item>
    /// <item><c>RandomWithoutShuffle</c>（a[9]）为假时，蓝图按
    ///   `RandomIntegerInRangeFromStream(0, 牌库数)` 把卡插到**随机位置**；
    ///   这里只做顶/底两档。</item>
    /// </list>
    ///
    /// ★ 出参 <c>spawnedCardIDs</c> **已经改成数组**（2026-10-02，见函数末尾的注释）：
    /// 它是 <c>TArray&lt;int32&gt;</c> 卡 ID，旧实现返回单张卡 ⇒
    /// 三个真实读者（`card_event_colossus` i=337 / **`card_unit_meteor` i=577** /
    /// `card_event_imperial_weapon_no_2` i=451）的 <c>Array_Length</c> 恒 0、整段循环被跳过。
    /// </summary>
    private object? DoSpawnInDeck(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = a.Select(AsString).FirstOrDefault(s => s is not null && s.StartsWith("card_", StringComparison.Ordinal));
        if (cardName is null)
        {
            return null;
        }

        // `numberOfCards` 在 a[3]（蓝图签名第 4 个参数）。取不到时按 1 —— 与旧行为一致。
        int count = IntArg(a, 3, 1);
        if (count < 1)
        {
            count = 1;
        }

        var deck = c.State.Deck(side).ToList();
        bool toBottom = true;
        foreach (object? v in a)
        {
            if (v is int i && i == (int)SpawnInDeckLocation.Top)
            {
                toBottom = false;
            }
        }

        CardInstance? last = null;
        var spawned = new List<int>();
        for (int n = 0; n < count; n++)
        {
            if (toBottom)
            {
                last = c.State.Create(cardName, side, side.DeckOf(),
                                      c.State.NextLocationNumber(side, side.DeckOf()));
            }
            else
            {
                // 放到牌库顶：把现有牌整体后移一位
                foreach (var existing in deck)
                {
                    existing.LocationNumber++;
                }

                last = c.State.Create(cardName, side, side.DeckOf(), 0);
            }

            // ★★ **必须消耗这一个随机数** —— 它是「随机效果与客户端不一致」这一类
            //    在**消费点**上的第二个独立成因（第一个是 RNG 算法本身）。
            //
            // 蓝图 `BP_CardFunctions::SpawnCardInDeckBySide`（`spawnerID > 0` 分支，
            // `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:34856-34867`）：
            // <code>
            //   CreateCard(...)                                   // L_0173 先建卡
            //   GetDeckByside(side) → deckCardIDs                 // L_0225
            //   len  = Array_Length(deckCardIDs)                  // L_0245（**含**刚建的这张）
            //   rand = RandomIntegerInRangeFromStream(stream, 0, len)   // L_0280 ★ 每次生成都消耗
            //   pos  = SelectInt(rand, -1, RandomWithoutShuffle)  // L_02B3
            //   AddCardToDeckBySide(side, cardID, Not(bottom), pos)// L_0303
            // </code>
            // 注意 `rand` 是**无条件**算出来的（`SelectInt` 只是决定用不用它），
            // 所以只要走了这条生成路径，每个生成的卡都恰好消耗 1 个随机数。
            //
            // 漏掉它会怎样（实测 773639 `#10 t3`，人类打 `card_event_colossus`）：
            // 「Copy three random orders in the enemy deck」连抽 3 次，
            // 每次抽完 `SpawnCardInDeckBySide` 都要消耗 1 个 —— 我们一个都没消耗
            // ⇒ 游标**落后 1**，第二次抽签起全部错位。实测内核抽到
            // `[iron_from_north, iron_from_north, baker_street]`，
            // 而客户端（动作流揭示的 3001/3002）是 `[iron_from_north, royal_research, …]`
            // —— 第一次对、第二次就不对了，正是"落后 1"的特征。
            int deckLen = c.State.Deck(side).Count;
            int rand = c.State.Random.RandRange(0, deckLen);
            c.State.TraceRandom($"SpawnCardInDeckBySide {side} deckLen={deckLen} rand={rand} " +
                                $"-> #{last.CardId} {cardName}");

            spawned.Add(last.CardId);
        }

        // ★★ 出参必须是**数组**（`TArray<int32>` 的卡 ID），不是单张卡。
        //
        // 蓝图签名（`ref/kards-sim/KardsSim/Generated/_index.g.cs:4336`）：
        //   `SpawnCardInDeckBySide(side, card_name, spawnerID, numberOfCards, salvageFaction,
        //    HideFromOpponent, bottom, shuffle, SkipDrawAnimation, RandomWithoutShuffle,
        //    out TArray<int32> spawnedCardIDs)`
        // —— 名字里的 `IDs` 就是"整数卡 ID 数组"，读者一律先 `Array_Length`、
        // 再 `Array_Get` 取元素，取到的是**卡 ID**（不是卡实例）。
        //
        // 旧实现返回**单张 CardInstance**，后果是整段循环被跳过：
        //   `Array_Length(卡实例)` = 0（`KismetVm.EvalMath` 的 `Array_Length` 只认
        //   `ICollection`）、`Array_Get` 也取不到 —— 循环体一次都不执行。
        //
        // 实测（对局 389594，任务 A 的那 1 点 HQ 差）：
        //   `card_unit_meteor` 的 `OnAfterAttack`（`card_unit_meteor` IR i=929→i=1017→i=577）
        //   是「移除自己 → `SpawnCardInDeckBySide` 生成一张同名卡 →
        //   **循环把新卡的攻/防设成 `attTotal*2` / `defTotal*2`**」。
        //   循环被跳过后，生成的副本还是 **1/1**，客户端那边是 **2/2**：
        //   t7 `#32` 用 1/1 的 `meteor#14` 打完生成 `#7001`（我们 1/1、客户端 2/2）
        //   → t15 `#71` 人类打出 `#7001`、`#72` 打右 HQ：客户端 21→**19**（2 点），
        //     我们 21→20（1 点）⇒ `#73` 起 HQ 校验和全程差 1。
        //   t15 再生成的 `#15001` 同理（我们 1/1，客户端应为 4/4）。
        return spawned;
    }

    /// <summary>
    /// 派发表里已经实现的原语名。
    ///
    /// 给「差距量化」工具用（<c>BotSim gaps</c>）：拿它去对
    /// 卡组实际需要的调用集合，才能算出真实缺口，而不是猜。
    /// </summary>
    public IReadOnlyCollection<string> ImplementedNames => _dispatch.Keys;

    /// <summary>
    /// 执行**卡自己的** <c>GetPlayFromHandDamage</c>，返回它写进输出参数
    /// <c>damage</c> 的值。
    ///
    /// 为什么不是「读某个字段」：全卡池扫过一遍，这个函数**每张卡都不一样**，
    /// 值就写在卡自己的字节码里，不存在统一的字段或数据源。
    /// 三种典型形状（<c>klink bot/tools/gen-kismet-ir.py</c> 的 <c>LOCAL_FUNCTIONS</c>）：
    ///
    /// <code>
    /// 23×  card_unit_17th_infantry_brigade   damage = 2                       ← 纯字面量
    ///  1×  card_event_the_commonwealth       damage = SelectInt(20, 0, HQ防御 >= 30)
    ///  1×  card_event_forward_base_anzac     damage = 手牌最左那张的 getTotalKreditCost()
    /// </code>
    ///
    /// 所以这里只能把那张卡的函数体**解释执行**一遍（<see cref="Blueprint.KismetVm.RunLocalProgram"/>）。
    /// 卡没有这个函数时返回 0 **并且计入未实现统计** —— 不猜一个值。
    /// </summary>
    /// <param name="target">
    /// 调用点的第一个实参（蓝图里是 <c>K2Node_Event_targetCard</c>），
    /// 作为函数入参 <c>targetCard</c> 喂进去。实测英联邦等卡根本不用它，
    /// 但 <c>card_unit_zero</c> 用它（<c>SelectInt(1, 0, IsValid(targetCard))</c>）。
    /// </param>
    private int DoGetPlayFromHandDamage(EffectContext c, CardInstance? target)
    {
        var self = c.Self;
        var library = Blueprint.KismetLibrary.Default;
        if (self is null || library is null)
        {
            return 0;
        }

        var program = library.FindLocalProgram(self.Definition.Name, "GetPlayFromHandDamage");
        if (program is null)
        {
            // 这张卡没有这个局部函数 —— 记成缺口，别用 0 假装算出来了
            NotifyUnimplemented($"<GetPlayFromHandDamage:{self.Definition.Name}>");
            return 0;
        }

        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["targetCard"] = target ?? c.Target,
        };

        return Blueprint.KismetVm.ToInt(Vm.RunLocalProgram(program, c, seed, "damage"));
    }

    // ==================== Develop（GetChooseSpawnCards 一族）====================
    //
    // 「Develop」= 从**卡池模板**里挑一张、把它**生成**成一张新卡。
    // 全链路（每一环都有反编译出处）：
    //
    //   1. 卡自己的 `OnPlayedFromHand` 调 `selectCardToDraw(cardID, false, false, out)`
    //      —— 例 `card_event_pams` IR i=822（`out/gcs/pams.bpasm` 的 `.export 5`）。
    //   2. `BP_CardFunctions.selectCardToDraw` 的 `L_0680` 调
    //      `GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)`
    //      —— 见 `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:33744-33748`。
    //      候选是 `GetAllActiveStaticCards()` 过滤出来的**卡池模板**，不是牌库实例。
    //   3. 候选 >1 时只发 `NotifySelectCardToDrawPending`（`L_0D61`），
    //      把候选的**卡名**发给玩家；玩家选完发 `XActionCardToDrawSelected`（回放里的 `CS`）。
    //   4. 答复到达后由 `OpponentActionsCardToDrawSelected` 落实
    //      （`klink bot/decompiled/BP_OnlineMatch.live.all-functions.json`，bytecode 26-53）：
    //      <code>
    //      tmpSourceCard = GetCardFromID(cardTriggeringDraw)
    //      tmpTargetCardID = cardFunctions.CreateCard(
    //          tmpSourceCard.side, Conv_StringToName(cardNameToSpawn),
    //          &lt;isEffect ? 8(弃牌堆) : side==1 ? 3(左手) : side==2 ? 4(右手)&gt;,
    //          0, -1, spawnCardInHand=true, isGold, "", false, true, cardTriggeringDraw, …)
    //      tmpSourceCard.OnHandTargetSelected(tmpTargetCardID, cardTriggeringDraw)
    //      DevelopAndForecastCheck(cardTriggeringDraw, tmpTargetCardID, tmpSourceCard)
    //      </code>
    //      —— **新卡先落在挑牌方的手牌里**，再由那张卡自己的 `OnHandTargetSelected`
    //      决定最终去哪（pams 是"塞回牌库随机位置 + 费用设 0"）。

    /// <summary>
    /// `GetAllActiveStaticCards(includeNotAttainable, includeReserved, out cards)`
    /// —— **卡池里的静态卡模板**（`UBaseCardObject*` 数组），**不是**对局里的实例。
    ///
    /// 真实实现在 `BP_CardFunctions.GetAllActiveStaticCards`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:19104-19338`）：
    /// 遍历 `GameStateRef.GetAllStaticCardsSortedByName()`，按三层过滤：
    /// <list type="number">
    /// <item>卡集（`cardSet`）：见 <see cref="CardPoolTable.IsSetInPool"/> ——
    ///   1/8/9/10/12/13/15/16/17/18/19/20 直接保留；21 看 `isCardSetConfigActive(21)`；
    ///   2..7 只在 `includeNotAttainable` 为真时保留；0/11/14/其余**无条件剔除**。</item>
    /// <item>`includeReserved` 为假时过 `NotifyCheckCardReserved`（= `IsCardReserved`）；</item>
    /// <item>`NotifyCheckCardBlacklisted`（服务端 `DSession.cards_blacklist`，离线恒空）。</item>
    /// </list>
    ///
    /// ## 历史（2026-10-02 修正）
    ///
    /// 这里原来**直接返回整个卡库**（2021 张），只做"按卡名排序"，并在注释里把
    /// 「卡集 1..10/12/13/15..20 保留」记成了近似 —— 那个记法本身是**错的**：
    /// 反编译里只有 2..7 走 `includeNotAttainable` 门控，15..20 是无条件保留的
    /// （对照 `BP_CardFunctions.g.cs:19238-19260`：`!=15/16/17/18/19/20` 四个分支的
    /// 跳转目标全是 `L_01F0`，只有 `!=2..7` 跳 `L_08CC`）。
    /// 后果：候选池里混进了 **Special / OnlySpawnable / Placeholder / Expansion1 /
    /// Wildcards** 五个卡集的 472 张卡，以及 563 张预备卡。
    ///
    /// 为什么这件事会直接改变抽卡结果：`CardApi.GetRandomCard` 的下标是
    /// `RandomIntegerInRangeFromStream(0, pool.Count-1)`（`CardApi.cs:2255`）——
    /// 池子大小一变，**同一个流位置算出的下标就变**，取到的卡就变。
    /// </summary>
    private List<CardInstance> StaticCardPool(
        EffectContext c, bool includeNotAttainable, bool includeReserved)
    {
        var db = c.State.Database;
        if (!ReferenceEquals(_staticPoolDb, db) || _staticPool is null)
        {
            var pool = new List<CardInstance>(db.Count);
            foreach (var def in db.All.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                pool.Add(TemplateInstance(def, c.Controller));
            }

            _staticPool = pool;
            _staticPoolDb = db;

            // ⚠️ 卡池一换，按卡名缓存的候选表就全部失效（候选表里的元素就是这些模板实例）。
            _staticPoolGeneration++;
            _staticPoolVariants.Clear();
            _gcsCache.Clear();
            _gcsPurityCache.Clear();
        }

        // 两个开关的组合只有 4 种，且**整局都是常量**（调用点的实参是字面量 +
        // `IsCardReserved(这张卡的名字)`）⇒ 变体表全局缓存，元素仍与不缓存时同一批实例。
        var key = (includeNotAttainable, includeReserved);
        if (!_staticPoolVariants.TryGetValue(key, out var filtered))
        {
            filtered = new List<CardInstance>(_staticPool.Count);
            foreach (var card in _staticPool)
            {
                if (!CardPoolTable.IsSetInPool(card.Definition.CardSet, includeNotAttainable))
                {
                    continue;
                }

                if (!includeReserved && CardPoolTable.IsReserved(card.Name))
                {
                    continue;
                }

                filtered.Add(card);
            }

            _staticPoolVariants[key] = filtered;
        }

        // 每次返回一份新的 List（调用方会 Array_Clear / Array_Add 原地改它）
        return new List<CardInstance>(filtered);
    }

    /// <summary>
    /// Implements the four card-local <c>CheckHasUnitToSpawn</c> exports:
    /// <c>red_banner</c> (+2 same-faction unit), <c>ural_factories</c>
    /// (+1 same-faction unit), <c>refit</c> (+3 same-faction tank), and
    /// <c>stand_together_brothers</c> (same-cost Soviet tank).
    ///
    /// The helper is called from each card's <c>CanPlayFromHand</c>, so the
    /// receiver context (<see cref="EffectContext.Self"/>) identifies which
    /// card-specific predicate applies.  The target is an explicit first
    /// argument in all four Blueprint call sites.
    /// </summary>
    private bool HasUnitToSpawn(EffectContext c, object?[] args)
    {
        var target = AsCardOrId(c, args.ElementAtOrDefault(0));
        if (target is null || c.Self is null)
        {
            return false;
        }

        string cardName = c.Self.Definition.Name;
        int expectedCost;
        int expectedFaction;
        bool requireTank;

        switch (cardName)
        {
            case "card_event_red_banner":
                expectedCost = target.KreditCost + 2;
                expectedFaction = target.Definition.FactionId;
                requireTank = false;
                break;
            case "card_event_ural_factories":
                expectedCost = target.KreditCost + 1;
                expectedFaction = target.Definition.FactionId;
                requireTank = false;
                break;
            case "card_event_refit":
                expectedCost = target.KreditCost + 3;
                expectedFaction = target.Definition.FactionId;
                requireTank = true;
                break;
            case "card_event_stand_together_brothers":
                expectedCost = target.KreditCost;
                expectedFaction = 4; // Soviet, as in RandomlyChooseTank.
                requireTank = true;
                break;
            default:
                NotifyUnimplemented($"<CheckHasUnitToSpawn:{cardName}>");
                return false;
        }

        bool includeReserved = CardPoolTable.IsReserved(c.Self.Name);
        return StaticCardPool(c, includeNotAttainable: false, includeReserved)
            .Any(card => card.Definition.FactionId == expectedFaction
                && card.KreditCost == expectedCost
                && (requireTank ? IsTank(card) : IsUnit(card)));
    }

    private static CardDatabase? _staticPoolDb;
    private static List<CardInstance>? _staticPool;

    /// <summary>
    /// `(includeNotAttainable, includeReserved)` → 过滤后的卡池（元素是 <see cref="_staticPool"/> 的模板实例）。
    /// </summary>
    private static readonly Dictionary<(bool, bool), List<CardInstance>> _staticPoolVariants = new();

    /// <summary>卡池代数 —— 每次重建 <see cref="_staticPool"/> 时 +1，用来判定缓存条目是否过期。</summary>
    private static int _staticPoolGeneration;

    // ==================== `GetChooseSpawnCards` 候选表缓存 ====================
    //
    // 为什么要缓存：这张卡的 `GetChooseSpawnCards` 在蓝图里是「**扫一遍整个卡池再过滤**」
    // 的形状 —— `GetAllActiveStaticCards()` 返回 2021 张模板卡，然后对每一张跑
    // `Array_Get → 谓词 → 比较 → Array_Add` 那一小段（约 8 步）。
    // 一次调用 ≈ 2021 × 8 ≈ 16k 步；实测 `BotSim play` 每局 34,631 步里绝大部分是它。
    //
    // 为什么**能**缓存：对下面 <see cref="PoolPureOps"/> 白名单里的那些卡，
    // 这个过滤条件在**整局、甚至跨局**都是静态的 —— 判据只有
    // 「卡池模板自己的定义（faction / 类型 / 费用 / tag / 私有 JSON）」+ 程序里的字面量。
    // 卡池模板本身已经是全局缓存的（<see cref="_staticPool"/>），所以候选表也是常量。
    //
    // ⚠️ **哪些卡不能缓存，为什么**（判据是自动的，见 <see cref="IsPoolPureProgram"/>）：
    //   · 读对局状态的：`GetAllCards` / `GetCardsInHandBySide` / `IsLocatedInDeck` /
    //     `GetOppositeSide`（`SelfSide(c)` = 效果控制方）——
    //     `air_land_sea` / `happy_time` / `sabotage` / `the_rock_of_gibraltar`；
    //   · 带随机的：`RandomIntFromRangeWithStream` —— `rain1_mist` / `storm1_gale` / `sunny1_blue_sky`；
    //   · 读**这张卡自己的**私有 JSON（`{self:true}` 实参）：`baker_street_irregulars`
    //     （`JSON_GetBool(self, "oneChosen", …)`，`oneChosen` 是对局中会变的标志）、
    //     `bpf`、`the_rock_of_gibraltar`；
    //   · 调**未实现**函数的：`IsBomber`（bomber_mafia / ingenuity）、
    //     `GetStaticCard`（bpf / ingenuity / 6 张 unit_*）、`IsGotcha`（15_aufklarungs）、
    //     `getAndDecryptAttack`（sea_embargo）、`GetMainNationForSide`（pilot_escape）、
    //     `getSkirmishBP` + `Get Start Of Turn Spawn Cards`（brawl_test1）。
    //     这些**不进缓存**是为了保住 `UnimplementedCalls` 计数 —— 缓存命中会跳过整段
    //     程序执行，未实现调用的统计会凭空消失，那是诊断口径，不该被缓存悄悄改掉。
    //
    // 命中率：36 张定义了 `GetChooseSpawnCards` 的卡里 15 张可缓存，
    // 而**开销大的那 25 张（扫全池）里 15 张全在缓存内** —— 剩下 10 张恰好就是上面那些
    // 有状态/随机/未实现的。所以省下的正是全部重活。

    /// <summary>
    /// 「卡池纯函数」白名单：**只读卡池模板自身的定义、不读任何对局状态**的原语。
    ///
    /// 只列**已经实现**（在派发表里）的那些 —— 未实现的函数会让程序走
    /// `UnimplementedCalls` 统计，缓存命中会把它抹掉。
    /// </summary>
    private static readonly HashSet<string> PoolPureOps = new(StringComparer.Ordinal)
    {
        // 卡池本身
        "GetAllActiveStaticCards",
        // 恒 false 的桩（`IsCardReserved` 见上面派发表），不读任何东西
        "IsCardReserved",
        // 只读「接收者那张卡自己的定义」的谓词 / 取值 —— 接收者一律来自卡池数组
        "IsUnit", "IsOrder", "IsTank", "IsAirUnit", "IsInfantry", "IsArtillery", "IsLocation",
        "IsLocatedOnBoard", "IsLocatedInHand", "IsVeteran", "IsDamaged", "IsBuffed",
        "IsGroundUnit", "IsForecastCard", "IsForecasted",
        "getTotalKreditCost", "getTotalOperationCost", "getTotalAttack", "getTotalDefense",
        // `getAndDecryptKredit` 与 `getTotalKreditCost` 同义（都只读卡自己的费用字段），
        // 是纯读 —— 不列进来会让"用解密费用做判据"的那些 `GetChooseSpawnCards`
        // 被判成不可缓存（`IsPoolPureProgram` 要求**每个**原语都在本表里）。
        "getAndDecryptKredit",
        "getTotalHeavyArmor", "getHasGameplayTag",
        "JSON_GetBool", "JSON_GetInt", "JSON_GetString", "JSON_GetIntArray",
        // 局部数组操作（`PossibleCards` 这种程序内数组，不碰对局）
        "Array_Add", "Array_Append", "Array_Clear", "Array_Contains", "Array_Get",
        "Array_IsEmpty", "Array_IsNotEmpty", "Array_IsValidIndex", "Array_LastIndex",
        "Array_Length", "Array_Remove", "Array_RemoveItem", "Array_Reverse",
        // 纯算术 / 比较（KismetVm.EvalMath 的分支）
        "Add_IntInt", "Subtract_IntInt", "Multiply_IntInt", "Divide_IntInt", "Percent_IntInt",
        "Abs_Int", "Min_IntInt", "Max_IntInt", "Greater_IntInt", "GreaterEqual_IntInt",
        "Less_IntInt", "LessEqual_IntInt", "EqualEqual_IntInt", "NotEqual_IntInt",
        "EqualEqual_StrStr", "NotEqual_StrStr", "EqualEqual_NameName", "NotEqual_NameName",
        "EqualEqual_ObjectObject", "NotEqual_ObjectObject", "EqualEqual_BoolBool", "NotEqual_BoolBool",
        "BooleanAND", "BooleanOR", "Not_PreBool", "SelectInt", "IsValid",
        "Conv_IntToString", "Conv_IntToText", "Conv_ByteToText", "Conv_TextToString",
        "Conv_StringToText", "Conv_IntToBool", "Conv_BoolToInt", "Conv_IntToByte", "Conv_ByteToInt",
        "EqualEqual_ByteByte", "NotEqual_ByteByte",
        "EnumCompareFaction", "EnumCompareSide", "EnumCompareCardType", "EnumCompareCardLocation",
        "Concat_StrStr", "Conv_NameToString", "GetEnumeratorUserFriendlyName",
    };

    /// <summary>卡名（已解析变体）→ 这个程序的 `GetChooseSpawnCards` 是否可缓存。只算一次。</summary>
    private static readonly Dictionary<string, bool> _gcsPurityCache = new(StringComparer.Ordinal);

    /// <summary>卡名 → 候选表（元素是 <see cref="_staticPool"/> 里的模板实例，全程只读）。</summary>
    private static readonly Dictionary<string, GcsCacheEntry> _gcsCache = new(StringComparer.Ordinal);

    private sealed record GcsCacheEntry(
        int Generation, List<CardInstance> Cards, bool MarkAsSeen, bool KeepOrder);

    /// <summary>缓存命中次数（诊断用；也是「跑的是新 dll」的哨兵）。</summary>
    public static long GcsCacheHits { get; private set; }

    /// <summary>判定为可缓存、但缓存里没有、真跑了一遍的次数。</summary>
    public static long GcsCacheMisses { get; private set; }

    /// <summary>判定为不可缓存、照旧每次真跑的次数。</summary>
    public static long GcsCacheBypassed { get; private set; }

    /// <summary>
    /// 这个 `GetChooseSpawnCards` 程序是不是「卡池纯函数」—— 只看它碰了哪些原语。
    ///
    /// 三条判据（任何一条不满足就不缓存，宁慢勿错）：
    /// 1. 程序里**任何地方**都没有 `{self:true}` —— 那是「这张卡自己」，
    ///    读它就会读到对局状态（`baker_street_irregulars` 的 `JSON_GetBool(self,…)`）。
    /// 2. 每一条 `call` / `math` 步骤都**带接收者**（`recv`）—— 没有接收者时
    ///    `SelfArg` 会退回 `ctx.Self` / `ctx.Target`，同样是读对局状态。
    /// 3. 出现的每一个函数 / 算术名都在 <see cref="PoolPureOps"/> 里。
    ///    （表达式里的嵌套调用要求带 `ctx`，否则接收者同样会退回 `ctx.Self`。）
    /// </summary>
    private static bool IsPoolPureProgram(Blueprint.KismetProgram program)
    {
        foreach (Blueprint.KismetStep step in program.Steps)
        {
            if (step.Op is "call" or "math")
            {
                if (step.Receiver is null || step.Function is null || !PoolPureOps.Contains(step.Function))
                {
                    return false;
                }
            }

            foreach (Blueprint.KismetExpr arg in step.Args)
            {
                if (!IsPoolPureExpr(arg))
                {
                    return false;
                }
            }

            if (step.Source is not null && !IsPoolPureExpr(step.Source)) return false;
            if (step.Condition is not null && !IsPoolPureExpr(step.Condition)) return false;
            if (step.Receiver is not null && !IsPoolPureExpr(step.Receiver)) return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="IsPoolPureProgram"/> 返回 false 时，给出**第一条**不满足的判据 ——
    /// 只在 <see cref="GcsDiag"/> 打开时调用（<c>KLINK_GCS_DIAG=1</c>），
    /// 用来回答「这张卡为什么没进缓存」。默认关闭，零开销。
    /// </summary>
    private static string PoolImpurityReason(Blueprint.KismetProgram program)
    {
        foreach (Blueprint.KismetStep step in program.Steps)
        {
            if (step.Op is "call" or "math")
            {
                if (step.Receiver is null) return $"i={step.Index} {step.Op} {step.Function} 无接收者";
                if (step.Function is null) return $"i={step.Index} {step.Op} 无函数名";
                if (!PoolPureOps.Contains(step.Function)) return $"i={step.Index} 不在白名单: {step.Function}";
            }

            foreach (Blueprint.KismetExpr arg in step.Args)
            {
                if (!IsPoolPureExpr(arg)) return $"i={step.Index} 实参不纯: {arg}";
            }

            if (step.Source is not null && !IsPoolPureExpr(step.Source)) return $"i={step.Index} src 不纯: {step.Source}";
            if (step.Condition is not null && !IsPoolPureExpr(step.Condition)) return $"i={step.Index} cond 不纯: {step.Condition}";
            if (step.Receiver is not null && !IsPoolPureExpr(step.Receiver)) return $"i={step.Index} recv 不纯: {step.Receiver}";
        }

        return "-";
    }

    private static bool IsPoolPureExpr(Blueprint.KismetExpr expr)
    {
        if (expr.Self)
        {
            return false;   // 「这张卡自己」= 对局状态
        }

        if (expr.Call is { } call)
        {
            // 表达式级调用：接收者来自 `ctx`；没有 `ctx` 就会退回 `ctx.Self`。
            if (expr.Context is null || !PoolPureOps.Contains(call))
            {
                return false;
            }
        }

        if (expr.Math is { } math && !PoolPureOps.Contains(math))
        {
            return false;
        }

        foreach (Blueprint.KismetExpr arg in expr.Args)
        {
            if (!IsPoolPureExpr(arg)) return false;
        }

        foreach (Blueprint.KismetExpr item in expr.Array)
        {
            if (!IsPoolPureExpr(item)) return false;
        }

        return expr.Context is null || IsPoolPureExpr(expr.Context);
    }

    /// <summary>
    /// 造一张**脱离对局的模板卡实例**（不进 `GameState` 的 `_byCardId` / `_cardsBySide`）。
    ///
    /// 为什么需要它：`GetChooseSpawnCards` 的候选表在蓝图里就是 `UBaseCardObject*` 数组，
    /// 而卡自己的过滤判据是 `卡.faction` / `IsOrder(卡)` / `getTotalKreditCost(卡)` ——
    /// 这三个原语在本内核里都要求实参是 <see cref="CardInstance"/>（见 `GetMember` 与 `SelfArg`）。
    /// 用 `CardDefinition` 的话这三个读法全落空，过滤条件会**静默恒假**、候选表永远是空的。
    ///
    /// `CardId` 取 0：卡池模板在真实客户端有自己的号，但本内核没有那份号表，
    /// 而这条链路上**没有任何一步读模板的 cardID**
    /// （玩家答复带的是卡组码→卡名，生成时走 `Create` 重新发号）。
    /// </summary>
    public static CardInstance TemplateInstance(CardDefinition def, Side side)
    {
        var card = new CardInstance
        {
            CardId = 0,
            Name = def.Name,
            Owner = side,
            Definition = def,
            Location = CardLocation.NotAvailable,
            LocationNumber = 0,
            Attack = def.Attack,
            Defense = def.Defense,
            MaxDefense = def.Defense,
            KreditCost = def.Kredits,
            OperationCost = def.OperationCost,
        };

        card.InitializeFromDefinition();
        return card;
    }

    private static CardDefinition? FindVeteranDefinition(CardDatabase database, CardInstance card)
    {
        string baseName = CardDatabase.ResolveBaseName(card.Definition.Name);
        string veteranName = $"{baseName}_vet";
        return database.All.FirstOrDefault(definition =>
            string.Equals(definition.Name, veteranName, StringComparison.Ordinal));
    }

    /// <summary>
    /// 执行**这张卡自己**的 <c>GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)</c>。
    ///
    /// 它是卡的局部函数（独立 export，不在 ubergraph 里），所以只能执行那张卡的字节码
    /// —— 全卡池 36 张卡定义了它，过滤条件各不相同。原文见
    /// <c>out/gcs/pams.bpasm</c> 第 256–457 行（pams 的条件是「英国 + 指令 + 总费 &lt; 5」）。
    /// 这也就是为什么 IR 生成器必须把 <c>GetChooseSpawnCards</c> 收进 `locals`
    /// （见 `klink bot/tools/gen-kismet-ir.py` 的 `LOCAL_FUNCTIONS`）。
    ///
    /// ⚠️ **带候选表缓存**（见 <see cref="PoolPureOps"/> 那一段注释）。
    /// 对「卡池纯函数」的卡，结果只取决于卡池模板自己，而卡池模板本身是全局常量
    /// （<see cref="_staticPool"/>）—— 所以候选表也是常量，直接按卡名存下来。
    /// 每次仍然返回**一份新的 List**（调用方会 `Array_Clear` / `Array_Add` 原地改它），
    /// 元素引用与不缓存时**完全相同**（不缓存时也是从同一个 <see cref="_staticPool"/> 里取）。
    /// </summary>
    public List<CardInstance> GetChooseSpawnCards(
        EffectContext c, CardInstance selecting, out bool markAsSeen, out bool keepOrder)
    {
        markAsSeen = false;
        keepOrder = false;

        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return new List<CardInstance>();
        }

        var program = library.FindLocalProgram(selecting.Definition.Name, "GetChooseSpawnCards");
        if (program is null)
        {
            // 这张卡没定义这个局部函数（或 IR 是旧的）—— 记成缺口，别拿空表假装算过了
            NotifyUnimplemented($"<GetChooseSpawnCards:{selecting.Definition.Name}>");
            return new List<CardInstance>();
        }

        // 缓存键用**解析过变体回退的卡名**：`xxx_bal` / `xxx_vet` 的蓝图逻辑挂在基础卡上，
        // 走的是同一份程序，自然也共享同一条缓存。
        string cacheKey = library.ResolveCardName(selecting.Definition.Name) ?? selecting.Definition.Name;

        if (!_gcsPurityCache.TryGetValue(cacheKey, out bool cacheable))
        {
            cacheable = IsPoolPureProgram(program);
            _gcsPurityCache[cacheKey] = cacheable;

            if (GcsDiag)
            {
                Console.Error.WriteLine(
                    $"[GCS-DIAG] {cacheKey} cacheable={cacheable} steps={program.Steps.Count} "
                    + $"why={PoolImpurityReason(program)}");
            }
        }

        if (GcsNoCache || !cacheable)
        {
            GcsCacheBypassed++;
            return RunChooseSpawnCards(program, c, out markAsSeen, out keepOrder);
        }

        if (_gcsCache.TryGetValue(cacheKey, out var entry) && entry.Generation == _staticPoolGeneration)
        {
            GcsCacheHits++;
            markAsSeen = entry.MarkAsSeen;
            keepOrder = entry.KeepOrder;

            if (GcsVerify)
            {
                VerifyAgainstFreshRun(program, c, cacheKey, entry);
            }

            return new List<CardInstance>(entry.Cards);
        }

        GcsCacheMisses++;
        var cards = RunChooseSpawnCards(program, c, out markAsSeen, out keepOrder);
        _gcsCache[cacheKey] = new GcsCacheEntry(_staticPoolGeneration, cards, markAsSeen, keepOrder);
        return cards;
    }

    private List<CardInstance> RunChooseSpawnCards(
        Blueprint.KismetProgram program, EffectContext c, out bool markAsSeen, out bool keepOrder)
    {
        var outs = Vm.RunLocalProgramMulti(program, c, null, "cards", "markAsSeen", "keepOrder");
        markAsSeen = Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("markAsSeen"));
        keepOrder = Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("keepOrder"));
        return outs.GetValueOrDefault("cards") as List<CardInstance> ?? new List<CardInstance>();
    }

    /// <summary>
    /// 自检开关（环境变量 <c>KLINK_GCS_VERIFY=1</c>）：每次缓存命中都**再真跑一遍**，
    /// 逐元素比对（顺序 + 引用 + 两个 bool）。对不上直接抛 —— 缓存必须对结果完全透明。
    /// 默认关闭，零开销。
    /// </summary>
    private static readonly bool GcsVerify =
        Environment.GetEnvironmentVariable("KLINK_GCS_VERIFY") is { Length: > 0 } v && v != "0";

    /// <summary>
    /// 诊断开关（环境变量 <c>KLINK_GCS_DIAG=1</c>）：每张卡第一次被问到「能不能缓存」时，
    /// 往 stderr 打一行「卡名 / 判定 / 步数 / 第一条不满足的判据」。默认关闭，零开销。
    /// </summary>
    private static readonly bool GcsDiag =
        Environment.GetEnvironmentVariable("KLINK_GCS_DIAG") is { Length: > 0 } d && d != "0";

    /// <summary>
    /// A/B 开关（环境变量 <c>KLINK_GCS_NOCACHE=1</c>）：把缓存整个旁路掉，
    /// 走的就是「没有缓存」那条路 —— 同一份 dll 上做前后对照，排除构建/机器噪声。
    /// 默认关闭。
    /// </summary>
    private static readonly bool GcsNoCache =
        Environment.GetEnvironmentVariable("KLINK_GCS_NOCACHE") is { Length: > 0 } n && n != "0";

    private void VerifyAgainstFreshRun(
        Blueprint.KismetProgram program, EffectContext c, string cacheKey, GcsCacheEntry entry)
    {
        var fresh = RunChooseSpawnCards(program, c, out bool freshSeen, out bool freshOrder);
        bool same = freshSeen == entry.MarkAsSeen && freshOrder == entry.KeepOrder
                    && fresh.Count == entry.Cards.Count;
        if (same)
        {
            for (int i = 0; i < fresh.Count; i++)
            {
                if (!ReferenceEquals(fresh[i], entry.Cards[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            throw new InvalidOperationException(
                $"GetChooseSpawnCards 缓存不一致（{cacheKey}）："
                + $"缓存 {entry.Cards.Count} 张 / markAsSeen={entry.MarkAsSeen} / keepOrder={entry.KeepOrder}，"
                + $"重算 {fresh.Count} 张 / markAsSeen={freshSeen} / keepOrder={freshOrder}。"
                + "缓存改变了语义 —— 必须把这张卡从 PoolPureOps 白名单路径里摘出去。");
        }
    }

    /// <summary>
    /// 把玩家选中的**卡池模板卡**落实成一张新卡 —— 对应 `OpponentActionsCardToDrawSelected`
    /// 里的 `CreateCard(...)` + `OnHandTargetSelected(...)`（出处见本节顶部注释第 4 条）。
    ///
    /// 新卡先按蓝图落在挑牌方的**手牌**（`side==1 → 3 HandLeft`、`side==2 → 4 HandRight`；
    /// `isEffect` 为真时是 8 = 弃牌堆，本函数只走非 isEffect 那条），
    /// 再由那张卡自己的 `OnHandTargetSelected` 决定最终去向。
    /// </summary>
    public CardInstance? DevelopChosenCard(CardInstance? selecting, string cardName)
    {
        if (selecting is null || string.IsNullOrEmpty(cardName) || State.Database.Find(cardName) is null)
        {
            return null;
        }

        Side side = selecting.Owner;
        var created = State.Create(cardName, side, side.HandOf(),
                                   State.NextLocationNumber(side, side.HandOf()), selecting.IsGold);

        // 事件契约（docs/event-contracts.json）：[Int handTargetCardID, Int instigatorID]
        FireTrigger("OnHandTargetSelected", selecting, side,
                    eventArgs: new object?[] { created.CardId, selecting.CardId },
                    eventSubject: created);

        // `OpponentActionsCardToDrawSelected` 在处理 Develop 选牌时，
        // 先让新卡执行 `OnHandTargetSelected`，再调用
        // `DevelopAndForecastCheck(cardTriggeringDraw, tmpTargetCardID, tmpSourceCard)`。
        // 后者对旁观卡广播这两个事件参数；不能把它并入创建钩子，
        // 否则接收者会在新卡决定最终落点之前观察到错误状态。
        FireTrigger("OnOtherCardDeveloped", created, side,
                    eventArgs: new object?[] { created, selecting.CardId },
                    eventSubject: created,
                    namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["cardDeveloped"] = created,
                        ["instigatorID"] = selecting.CardId,
                    });

        ResolveTriggerQueue();

        return created;
    }

    /// <summary>
    /// `MoveCardToTopOfOwnersDeck(cardID, instigatorID, positionFromTop, out)`
    /// —— 把那张卡放进**它自己的**牌库，位置是「从牌库顶往下第 `positionFromTop` 张」。
    ///
    /// 出处：`BP_CardFunctions.MoveCardToTopOfOwnersDeck`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:27309-27335`），
    /// 函数体只有一句 `MoveCardToTopOfDeck(self, cardID, instigatorID, positionFromTop, false)`。
    /// 调用点：`card_event_pams.OnHandTargetSelected` IR i=1126 —— pams 靠它把 develop
    /// 出来的那张牌塞回牌库（卡面 "Add it to your deck with a cost of 0."）。
    ///
    /// ⚠️ 本内核**不建模** `MoveCardToTopOfDeck` 里的离场事件链
    /// （`ExecuteOnBeforeLeaveBoardOrOwnerEvents` 等）；这里只做"换区 + 插到指定位置"。
    /// 对本族（新卡刚生成在手牌里）这条链本来就不会触发。
    /// </summary>
    private object? DoMoveCardToTopOfOwnersDeck(EffectContext c, object?[] a)
    {
        var card = c.State.ById(IntArg(a, 0, -1));
        if (card is null)
        {
            return null;
        }

        int position = IntArg(a, 2, 0);
        Side side = card.Owner;

        bool wasOnBoard = card.Location.IsBoard() && !card.IsHq;

        // 先记下"除了这张卡之外的牌库顺序"，再把它插进第 position 位。
        var rest = c.State.Deck(side).Where(x => !ReferenceEquals(x, card)).ToList();
        c.State.Move(card, side.DeckOf());
        rest.Insert(Math.Clamp(position, 0, rest.Count), card);
        for (int i = 0; i < rest.Count; i++)
        {
            rest[i].LocationNumber = i;
        }

        // 蓝图 `MoveCardToTopOfDeck` i=714-828：卡还在 `GetAllCardInBattleAsMap()` 里
        // （= 原本在场上）时调 `ResetCardInBattle`。见 ResetCardInBattle 的出处注释。
        if (wasOnBoard)
        {
            ResetCardInBattle(card);
        }

        return null;
    }

    /// <summary>
    /// `StealCardFromBoardToDeck(cardID, instigatorID, deckSide, out qqq)`.
    /// The Blueprint removes the original through the normal non-destruction
    /// leave-board path, creates a fresh copy in the requested side's deck,
    /// inserts it at the top, then shuffles that deck.
    /// </summary>
    private object? DoStealCardFromBoardToDeck(EffectContext c, object? r, object?[] a)
    {
        var original = AsCardOrId(c, a.ElementAtOrDefault(0));
        if (original is null || original.IsHq || !original.Location.IsBoard())
        {
            return 0;
        }

        int instigatorId = IntArg(a, 1, c.Self?.CardId ?? 0);
        var instigator = c.State.ById(instigatorId) ?? c.Self;
        CardLocation oldLocation = original.Location;
        Side deckSide = SideArg(r, a, 2, c.Controller);

        // ApplyRemoveCardFromBoard(destroyed=false, converting=false) only
        // runs the before-leave chain; after-leave belongs to destroy/convert.
        c.Engine.FireLeaveTrigger(original, CardLocation.Discard,
            includeAfterEvents: false);
        c.State.Move(original, CardLocation.Discard);

        var copy = c.State.Create(original.Name, deckSide, deckSide.DeckOf(), 0,
            isGold: original.IsGold);

        var deck = c.State.Deck(deckSide).Where(x => !ReferenceEquals(x, copy)).ToList();
        foreach (var existing in deck)
        {
            existing.LocationNumber++;
        }

        copy.LocationNumber = 0;
        // `IsActionProcess` is a Blueprint-only routing predicate; the
        // headless action path is always the gameplay path.
        c.Engine.FireSubAction("ZActionStealCardFromBoardToDeck", new[]
        {
            ActionValue2.Int("oldLocation", (int)oldLocation),
            ActionValue2.Str("deckSide", deckSide.ToWire()),
            ActionValue2.Int("instigatorID", instigator?.CardId ?? 0),
        });

        // The Blueprint always shuffles after inserting the copy.
        ShuffleDeckBySide(c, deckSide, instigator);
        return 0;
    }

    private static void ShuffleDeckBySide(EffectContext c, Side side, CardInstance? instigator)
    {
        var order = c.State.Deck(side).ToList();
        c.State.Random.Shuffle(order);
        c.State.TraceRandom($"ShuffleDeckBySide {side} n={order.Count}");
        for (int i = 0; i < order.Count; i++)
        {
            order[i].LocationNumber = i;
        }

        // Open-game deck construction has no effect context and does not pass here.
        c.Engine.Api.FireTrigger("OnDeckShuffled", subject: null, side,
            eventSubject: instigator,
            eventArgs: new object?[] { (int)side, instigator },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deckSide"] = (int)side,
                ["instigatorCard"] = instigator,
            });
    }

    /// <summary>
    /// `MoveMultipleCardsToTopOfOwnersDeck(cardIDs, instigatorID, positionFromTop, out)`。
    ///
    /// `BP_CardFunctions` 的实现是按数组顺序循环调用
    /// `MoveCardToTopOfDeck(cardID, instigatorID, positionFromTop)`，然后发一个
    /// 纯客户端的 `NotifyMoveToDeckMultipleUnits`。因此 position=0 时，输入数组中
    /// 后出现的卡最终在牌库顶；不能把整个数组当作一次稳定批量插入。
    ///
    /// 内核只保留换区和牌库顺序这两个规则效果；通知是表现层，不产生状态变化。
    /// </summary>
    private object? DoMoveMultipleCardsToTopOfOwnersDeck(EffectContext c, object?[] a)
    {
        if (a.ElementAtOrDefault(0) is not System.Collections.IList values)
        {
            return true;
        }

        int instigatorId = IntArg(a, 1, 0);
        int position = IntArg(a, 2, 0);
        bool allMoved = true;

        foreach (var value in values)
        {
            int cardId = value is CardInstance card ? card.CardId : AsInt(value);
            if (c.State.ById(cardId) is null)
            {
                allMoved = false;
                continue;
            }

            DoMoveCardToTopOfOwnersDeck(c, new object?[] { cardId, instigatorId, position, null });
        }

        return allMoved;
    }

    /// <summary>
    /// `AdjustCardPositionInDeck(deckSide, cardToAdjust, positionFromTop, out qqq)`.
    ///
    /// The Blueprint implementation is deliberately a remove/reinsert pair:
    /// `RemoveCardFromDeckBySide` followed by `AddCardToDeckBySide` and then
    /// `ExecuteOnAfterDeckChanged`.  Keeping the card in the same location while
    /// rewriting the ordered location numbers matches that state-level contract
    /// without emitting a spurious location-moved event.
    /// </summary>
    private object? DoAdjustCardPositionInDeck(EffectContext c, object? r, object?[] a)
    {
        Side side = SideArg(r, a, 0, c.Controller);
        CardInstance? card = AsCardOrId(c, a.ElementAtOrDefault(1));
        if (card is null || card.Owner != side || card.Location != side.DeckOf())
        {
            return null;
        }

        var deck = c.State.Deck(side);
        int currentIndex = deck.FindIndex(x => ReferenceEquals(x, card));
        if (currentIndex < 0)
        {
            return null;
        }

        deck.RemoveAt(currentIndex);
        int targetIndex = Math.Clamp(IntArg(a, 2, 0), 0, deck.Count);
        for (int i = 0; i < deck.Count; i++)
        {
            deck[i].LocationNumber = i < targetIndex ? i : i + 1;
        }

        card.LocationNumber = targetIndex;

        // ExecuteOnAfterDeckChanged broadcasts to every registered subscriber;
        // namedArgs is required because the event stub's suffix is not a stable
        // positional index across generated Blueprint signatures.
        FireTrigger("OnAfterDeckChanged", subject: null, side,
            eventArgs: new object?[] { (int)side },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deckSide"] = (int)side,
            });

        return null;
    }

    /// <summary>
    /// `MoveUnitFromBoardToOwnersHand(card, instigatorID)` —— 把场上的单位退回原主手牌。
    ///
    /// 出处：`out/bp-cardfn.json` 函数 `MoveUnitFromBoardToOwnersHand`：
    /// <code>
    /// i=0/41/82/120  if (!(card.IsLocatedOnBoard() &amp;&amp; card.IsUnit())) → 直接返回
    /// i=134          handLocation = GetHandLocationBySide(card.originalSide)
    /// i=264          MoveCardFromBoardToOwnersHand(card.cardID, instigatorID, card.location, handLocation)
    /// </code>
    /// 与 `MoveCardFromBoardToOwnersHand`（i=10..944）：
    /// <code>
    /// i=10   ExecuteOnBeforeLeaveBoardOrOwnerEvents(cardID, NewLocation, OldLocation, Method=3 /*OnMovedToHand*/)
    /// i=59   FetchCardsByLocation(NewLocation) → isLocationFull
    /// i=173  JumpIfNot 925 (isLocationFull)     ; 没满 ⇒ 用手牌
    /// i=187  tmpNewLocation = 8 /*Discard*/     ; 满了 ⇒ 改去弃牌堆
    /// i=213  CardLocationMoved(…)
    /// i=291  ExecuteOnAfterLeaveBoardOrOwnerEvents(…)
    /// i=626  ResetCardInBattle(tmpCardToMove)
    /// </code>
    /// 签名取自 `ref/kards-sim/KardsSim/Generated/_index.g.cs:4016/4008`。
    /// </summary>
    private object? DoMoveUnitFromBoardToOwnersHand(EffectContext c, object? r, object?[] a)
    {
        var card = AsCard(a.FirstOrDefault()) ?? AsCard(r) ?? c.Target;
        if (card is null || card.IsHq || !IsLocatedOnBoard(card) || !IsUnit(card))
        {
            return null;
        }

        Side side = card.Owner;

        // 手牌满 ⇒ 退回的那张直接进弃牌堆（蓝图 i=173/187）。
        bool handFull = c.State.Hand(side).Count >= GameState.HandCapacity;
        CardLocation target = handFull ? CardLocation.Discard : side.HandOf();

        // 离场触发点必须在 Move **之前**（效果里要读旧位置），与 Destroy 那条一致。
        c.Engine.FireLeaveTrigger(card, target);
        c.State.Move(card, target);
        card.EnteredPlayOnTurn = 0;

        if (handFull)
        {
            c.Engine.FireSubAction("ZActionDiscardCard", new[]
            {
                ActionValue2.Int("discarderID", card.CardId),
            });
        }

        // i=626：回手之后重置这张卡的累积状态（→ OnCardReset / OnOtherCardReset）。
        ResetCardInBattle(card);
        return null;
    }

    /// <summary>
    /// `MakeCardRetreat(cards, instigatorID)` — front-line units retreat to
    /// their own support line when it has room; support-line units, or units
    /// whose support line is full, return to their owner's hand.
    /// </summary>
    private object? DoMakeCardRetreat(EffectContext c, object?[] a)
    {
        var targets = new List<CardInstance>();
        if (a.ElementAtOrDefault(0) is System.Collections.IList list)
        {
            foreach (var value in list)
            {
                if (AsCardOrId(c, value) is { } card)
                {
                    targets.Add(card);
                }
            }
        }
        else if (AsCardOrId(c, a.ElementAtOrDefault(0)) is { } one)
        {
            targets.Add(one);
        }

        var support = targets.Where(x => x.Location != CardLocation.BoardFrontline)
            .OrderBy(x => x.LocationNumber).ToList();
        var frontline = targets.Where(x => x.Location == CardLocation.BoardFrontline)
            .OrderBy(x => x.LocationNumber).ToList();

        foreach (var card in support.Concat(frontline))
        {
            if (HasCustomAbility(card, "cantRetreat") || !c.Engine.Api.IsLocatedOnBoard(card))
            {
                continue;
            }

            // ApplyMakeCardRetreat dispatches these before choosing the destination.
            if (!card.IsSuppressed)
            {
                FireTrigger("OnBeforeRetreat", card, card.Owner);
            }
            FireTrigger("OnOtherCardRetreat", card, card.Owner,
                eventArgs: new object?[] { card }, eventSubject: card,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardRetreated"] = card,
                });

            if (card.Location == CardLocation.BoardFrontline
                && c.State.Cards(card.Owner, card.Owner.HqOf()).Count < GameState.HalfBoardCapacity)
            {
                c.State.Move(card, card.Owner.HqOf());
            }
            else
            {
                DoMoveUnitFromBoardToOwnersHand(c, card, new object?[] { card });
            }
        }

        return null;
    }

    /// <summary>
    /// `ConvertCard(cards, instigatorID, newCardName, newCardID, ... , out newCardIDs)`.
    /// 转换保留原 cardID、拥有者、区域和位置号；只替换卡身份并清理本卡的临时状态。
    /// </summary>
    private object? DoConvertCard(EffectContext c, object?[] a)
    {
        var targets = new List<CardInstance>();
        if (a.ElementAtOrDefault(0) is System.Collections.IList list)
        {
            foreach (var value in list)
            {
                if (AsCardOrId(c, value) is { } card && !targets.Contains(card))
                {
                    targets.Add(card);
                }
            }
        }
        else if (AsCardOrId(c, a.ElementAtOrDefault(0)) is { } one)
        {
            targets.Add(one);
        }

        int instigatorID = IntArg(a, 1, c.Self?.CardId ?? 0);
        string? targetName = a.ElementAtOrDefault(2) switch
        {
            string s when !string.Equals(s, "None", StringComparison.OrdinalIgnoreCase) => s,
            _ => null,
        };
        int targetId = IntArg(a, 3, 0);
        if (targetName is null && targetId > 0)
        {
            targetName = c.State.ById(targetId)?.Name;
        }

        var converted = new List<int>();
        if (targetName is null || c.State.Database.Find(targetName) is not { } def)
        {
            return converted;
        }

        foreach (var card in targets)
        {
            if (card.Location == CardLocation.NotAvailable || card.IsHq)
            {
                continue;
            }

            int oldCardId = card.CardId;
            card.Reidentify(def.Name, def);

            // Conversion replaces the card identity, so transient state from the old card
            // must not leak into the new definition.
            card.CustomJson.Clear();
            card.CustomAbility = null;
            card.BuffsBySource.Clear();
            card.Attack = def.Attack;
            card.Defense = def.Defense;
            card.MaxDefense = def.Defense;
            card.HeavyArmorZeroedBySuppress = false;
            card.SuppressedOnTurn = -1;
            card.IsRevealed = false;
            card.SuppressStrippedKeywords?.Clear();
            card.SuppressStrippedCustomAbility = null;
            card.PinnedTurns = 0;
            card.HasMovedThisTurn = false;
            card.HasAttackedThisTurn = false;
            card.AttacksThisTurn = 0;
            card.OperationsUsedThisTurn = 0;
            card.EnteredPlayOnTurn = c.State.Turn;
            card.RecalculateStats();

            c.Engine.Api.FireTrigger("OnOtherCardConverted", card, card.Owner,
                eventArgs: new object?[] { oldCardId, card.CardId, card.Name, instigatorID },
                eventSubject: card,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["oldCardIDs"] = oldCardId,
                    ["newCardIDs"] = card.CardId,
                    ["newCardName"] = card.Name,
                    ["instigatorID"] = instigatorID,
                });
            converted.Add(card.CardId);
        }

        return converted;
    }

    /// <summary>
    /// `RandomIntFromRangeWithStream(minimum, maximum, out randomResult)` ——
    /// **闭区间** `[minimum, maximum]` 的均匀整数。
    ///
    /// ⚠️ **这条以前写错了，是「随机效果与客户端不一致」的第二个独立成因**：
    /// 旧注释/旧实现按**半开区间** `[minimum, maximum)` 处理
    /// （`c.State.Random.Next(min, max)`），于是 `RandomIntFromRangeWithStream(0, 2)`
    /// 只会出 0/1，而客户端会出 0/1/2。
    ///
    /// 权威判据（两条独立互证）：
    /// 1. 这个函数在蓝图里是**一行转发**
    ///    （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:29203-29224`）：
    ///    `RandomIntegerInRangeFromStream(cardsRandomStream, minimum, maximum)` —— 直接透传。
    /// 2. `RandomIntegerInRangeFromStream` 的 IDA 反编译
    ///    （`Kards_RNG_report` 的 `Weather.md` §4.2，地址 `0x143ddce50`）：
    ///    <c>Min + (int)(GetFraction() * (Max - Min + 1))</c> —— 除数是 `Max-Min+1`，
    ///    也就是**两端都取得到**。
    /// </summary>
    private object? DoRandomIntFromRange(EffectContext c, object?[] a)
    {
        int lo = IntArg(a, 0);
        int hi = IntArg(a, 1);
        uint seedBefore = c.State.Random.Seed;
        long cursorBefore = c.State.Random.ConsumedCount;
        int v = c.State.Random.RandRange(lo, hi);
        c.State.TraceRandom($"RandomIntFromRangeWithStream({lo},{hi}) -> {v} " +
                            $"cursor={cursorBefore}->{c.State.Random.ConsumedCount} " +
                            $"seed={seedBefore}->{c.State.Random.Seed}");
        return v;
    }

    /// <summary>派发一次调用。<paramref name="handled"/> 为 false 表示内核还没实现这个名字。</summary>
    public object? InvokeByName(string name, object? receiver, object?[] args, EffectContext ctx, out bool handled)    {
        if (_dispatch.TryGetValue(name, out var handler))
        {
            handled = true;
            try
            {
                return handler(ctx, receiver, args);
            }
            catch (Exception ex)
            {
                // 单次调用失败不能毁掉整局；记下来继续
                NotifyUnimplemented($"{name}<fault:{ex.GetType().Name}>");
                return null;
            }
        }

        handled = false;
        return null;
    }

    // ==================== 效果实现 ====================

    private object? DoChangeAttack(EffectContext c, object? r, object?[] a, bool invert = false)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // ---- 蓝图的第一道守位：`CanCardBeBuffed` ----
        // 出处 `out/bp-cardfn.json` 的 `ChangeAttack`：
        //   si=28  IsValid(card)      → si=57  JumpIfNot → si=393（log "error in [Change Attack]" + return）
        //   si=71  CanCardBeBuffed(card)
        //   si=103 JumpIfNot → si=472（qqq=False + return）★ **客户端在这里拒绝**
        //   si=117 instigatorID > 0  → si=151 JumpIfNot → si=393（return）
        //
        // ⚠️⚠️ **这一条在本内核里【不落地】—— 理由已更新（2026-09-30）**：
        //
        // 旧理由（**已作废，别再引用**）：「蓝图自相矛盾」—— 红牛 `OnStartOfTurn` 先判
        //   `IsLocatedOnBoard(self)` 再 `ChangeAttack(self,…)`，卡自己要求在场、门又拒绝在场，
        //   于是把 4 条蓝图自测当成反证。**那个"矛盾"是我们自己实现的 bug 造出来的**：
        //   `CardApi.CanCardBeBuffed` 当时把 `si=41` 的分支极性读反了，对在场单位返回 false。
        //   真语义是「**不是**未揭示的隐蔽卡 ⇒ 直接放行（true）」（取证见
        //   `klink bot/docs/CanCardBeBuffed矛盾调查.md` 与 `CardApi.CanCardBeBuffed` 的注释）。
        //   ⇒ 红牛 / 爱国热忱 / 敢死队 / 3 掷弹兵那 4 条自测**从此不构成反证**。
        //
        // 新理由（本内核的事实）：**这道门在本内核里恒真 ⇒ 加了是空转。**
        //   门的形状是 `if (!IsUnrevealedCovertCard(card)) return true;`，而内核没有建模
        //   Covert 的「已揭示/未揭示」状态（P1 只到 `Keyword.Covert` + `getHasCovert` 判据面），
        //   所以恒真。等 Covert 状态落地之后，这里再加门才有意义 —— 那时它会真的挡人。
        //   ⚠️ 加门时**必须**用修好之后的 `CardApi.CanCardBeBuffed`：用旧实现会立刻打死
        //   334 张 `ChangeAttack` + 311 张 `ChangeDefense` 的攻防改动。
        int delta = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        // ★★ `changeType == 2`（`EChangeType::SetValue`）= **设成绝对值**，不是"加 delta"。
        //
        // 判据是蓝图本体（`ref/kards-sim/.../BP_CardFunctions.g.cs` 的 `ChangeAttack`，
        // 函数起始 `:6491`）：
        // <code>
        //   si  localChangeType 分支：0→L_06BA  1→L_04C8  2/3/5/default→L_0357  4→L_096F
        //   L_0357  getAndDecryptAttack(card) → decryptedAttack
        //           if (decryptedAttack == localInputAmount) { valueChanged = False; 结束 }
        //           maxAttack = Clamp(localInputAmount, 0, 99)
        //           setAndEncryptAttack(card, Clamp(localInputAmount, 0, 99), …)
        //   L_04C8（ct==1 permBuff）  newVal = Clamp(decryptedAttack + localInputAmount, 0, 99)
        // </code>
        // 即：**2 = 总量赋成 amount**、**1 = 加 amount**。
        // 枚举值逐字来自 `<kards-src>\Source\kards\Public\EChangeType.h`
        // （`tempBuffGive=0, permBuff=1, SetValue=2, Suppress=3, tempBuffRemove=4, …`）。
        //
        // 为什么必须修（对局 389594 任务 A 的另一半）：`card_unit_meteor` 的
        // `OnAfterAttack` 生成的副本要「攻/防 = 原值 ×2」，
        // 而它是用 `ChangeAttack(新卡, cardID, attTotal*2, changeType=2, …)` 表达的
        // （`card_unit_meteor` IR i=202 / i=432）。副本是刚 `CreateCard` 出来的 1/1，
        // 按"加"算是 1+2=**3**、按"设"算是 **2** —— 客户端是 2。
        // 全卡池 7 个 `ChangeAttack` + 11 个 `ChangeDefense` 走 changeType=2。
        //
        // ⚠️ `!invert`：`invert` 是本内核给 `LoseAttack` 用的取负开关，
        //    「取负」和「设成绝对值」放一起没有意义（蓝图里 `LoseAttack` 是另一个函数）。
        //    实测 IR 里 `GainAttack` / `LoseAttack` 作为 `fn` **一个调用点都没有**，
        //    所以这只是防御性写法。
        if (!invert && changeType == ChangeTypeSetValueReal)
        {
            ChangeAttack(target, Math.Clamp(delta, 0, 99) - target.Attack, c.Self);
            return null;
        }

        // ★★ `changeType == 4`（`EChangeType::tempBuffRemove`）= **撤销该来源施加的攻击 buff**。
        //
        // 判据是蓝图本体（`ref/kards-sim/.../BP_CardFunctions.g.cs` 的 `ChangeAttack`，起始 `:6491`）：
        // <code>
        //   :6591 localChangeType 分支：0→L_06BA  1→L_04C8  2/3/5/default→L_0357  4→L_096F
        //   :6789 L_096F  if (amountRemoved == 0) { valueChanged = False; return }  ← 没撤到东西就什么都不做
        //   :6805 L_09AB  localInputAmount = amountRemoved     ★ 实参 amount 被覆盖，不参与运算
        //   :6808 L_09EF  decryptedAttackBuff = getAndDecryptAttackBuff(card)
        //   :6811 L_0A18  newBuff = decryptedAttackBuff + amountRemoved
        //   :6813 L_0A46  setAndEncryptAttackBuff(card, newBuff, …)
        // </code>
        // `amountRemoved` 是 `ChangeBuffsFromCards` 的出参（:6561 调用 / :6848 出参槽）：
        //   ct=4 在它的入口分派里走 :6887 → `L_0831`（用 `EChangeType::tempBuffGive`(=0) 与
        //   `buffType` 拼出键名），再进 `L_0369`（:6936）的通用删除路径，最终 `L_16E6`（:7474）：
        //   `localAmountRemoved = 已存的量 × -1`（:7481/:7483），然后 `Map_Remove` 掉那个键（:7485）。
        // ⇒ **撤销量 = 当初存进去的量**，与本次实参 `amount` 无关。
        //   （对照 ct=0/1 走的 `L_0250`（:6918），那里才有 `if (amount == 0) { amountRemoved = 0; return; }` 的短路。）
        //
        // 卡池证据（`docs/card-ir.json` 全 IR 扫描，1735 条）：`ChangeAttack` 的 changeType 分布是
        // `{0:60, 1:314, 2:6, 4:48}`；其中 ct=4 且 `amount≠0` 的 6 处、5 张卡，**全部是「先给后撤」
        // 的成对形态**，撤销量恰好等于当初给的量：
        //   card_unit_su_100                    i=376 `+4, ct=0` / i=206  `4, ct=4`
        //   card_unit_ki_42_ii_ko               i=100 `+2, ct=0` / i=277  `2, ct=4`
        //   card_unit_type_97                   i=1306 `+1, ct=0` / i=495 `1, ct=4`（另有 i=2595 一处）
        //   card_unit_kyushu_j7w3               （给）/ i=155 `2, ct=4`
        //   card_unit_type_92_105mm_field_gun   （给）/ i=1206 `1, ct=4`
        // 剩下 42 处 ct=4 传的是 `amount=0` —— 旧实现下它们是**纯空转**
        // （`ChangeAttack(target, 0)` 在 `CardApi.ChangeAttack` 开头就被 `delta == 0` 挡掉），
        // 修完之后才会真的撤销。
        //
        // ⚠️ 这里**故意不碰** ct=0 / ct=1 的语义（它们仍然走下面那条 `ChangeAttack(target, delta)`）。
        //    项目自己的日志里记过这条教训：把两件事混在一个改动里，A/B 结论就没法归因了。
        //
        // ⚠️ 来源必须取 `c.Self`，**不能**取 `SourceCardIdArg(a, 1, c.Self)`：
        //    施加路径（下面的默认分支）用的就是 `c.Self`，撤销必须落在同一个槽上才对得起来。
        if (!invert && changeType == ChangeTypeTempBuffRemove)
        {
            if (c.Self is { } remover)
            {
                RemoveAttackBuff(target, remover.CardId);
            }

            return null;
        }

        ChangeAttack(target, invert ? -delta : delta, c.Self);
        return null;
    }

    private object? DoChangeDefense(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // 同 `DoChangeAttack`：`ChangeDefense` si=48/80 是同一个守位，
        // 同理**不落地** —— 门在本内核恒真，加了是空转（理由见 `DoChangeAttack` 那段）。
        //
        // ★ `changeType == 2`（SetValue）同样是**设成绝对值**，出处同 `DoChangeAttack`：
        //   `ChangeDefense` 的 L_03D8 分支 `setAndEncryptDefense(card, Clamp(amount,0,99))`
        //   + `maxDefense = Clamp(amount,0,99)`。这里走已有的 `SetDefenseValue`
        //   （它额外发 `OnAfterDefenseIsSet`，正是 SetValue 分支的专属事件）。
        //   实测调用点：`card_unit_no_9_commando` i=53（防设成 1）、
        //   `card_unit_meteor` i=432（防设成 defTotal×2）。
        int value = IntArg(a, 2);

        // ★ `changeType == 4`：蓝图 `ChangeDefense`（起始 `:7543`）**没有**撤销分支 ——
        //   :7646 `localChangeType == 4 → L_0E96`，而 :7906 `L_0E96` 是
        //   `DirectClientLogger("change type incorrect for \"Change Defense\"")`（:7907）
        //   + `qqq = False`（:7909）+ return（:7911），也就是**非法值，什么都不改**。
        //   对照 `ChangeAttack`（:6591）：那里的 ct=4 有专门的 `L_096F` 撤销分支。
        //   ⇒ 两个函数的 ct=4 **语义不一致**，所以这里对齐的是 no-op，
        //     **不是** `RemoveAttackBuff` —— 对称地挂一个撤销会和蓝图相反。
        //
        // 为什么把它显式写出来（而不是省掉）：`ChangeAttack` 的 ct=4 修好之后，
        //   很容易有人「顺手对称」地给这里也挂一个 `RemoveAttackBuff`。
        // 当前卡池 `ChangeDefense` 的 changeType 分布是 `{1:333, 2:10}`，
        //   **ct=4 有 0 个调用点** ⇒ 这条是**行为中性**的预防性对齐（A/B 一格都不会动）。
        if (IntArg(a, 3) == ChangeTypeTempBuffRemove)
        {
            return null;
        }

        if (IntArg(a, 3) == ChangeTypeSetValueReal)
        {
            SetDefenseValue(target, Math.Clamp(value, 0, 99), c.Self);
            return null;
        }

        ChangeDefense(target, value, c.Self);
        return null;
    }

    private object? DoSetDefense(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        int value = IntArg(a, 2);
        ChangeDefense(target, value - target.Defense, c.Self);
        return null;
    }

    private object? DoChangeKreditCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        // 签名（实测 136 个调用点）：ChangeKreditCost(卡, instigatorID, 数值, changeType, bool, out)
        //   a[1] = 来源卡 ID —— **必须带上**，光环的 `isBuffedByCard` / RemoveTheBuff 都按来源记账
        //   a[2] = 数值
        //   a[3] = changeType：0=临时相对修正，1=永久相对修正，2/3=直接设值，
        //            4=撤销该来源的临时修正。
        //
        // ⚠️ 这里**不能**像旧版那样做 `target.KreditCost += delta`：
        //    旧版既不看 a[1]（来源）也不看 a[3]（changeType），于是
        //    (1) 光环每 Apply 一次就累减一次、`RemoveTheBuff` 撤不掉（它传的是 0）；
        //    (2) `card_unit_big_red_one` 每次抽牌都会重算一遍「设成 4 费」的偏移，
        //        累加语义下每抽一张牌费用就再掉一截。
        //    现在改成：**buff 字典是唯一真源**，改完调 `RecalculateStats()` 用绝对值重算。
        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            // 蓝图按 buff 字段撤销；既有手写光环使用永久来源槽，
            // 所以临时槽优先，找不到时兼容回退到永久槽。
            RemoveCostBuff(target, sourceId, temporary: true,
                fallbackToPermanent: true);
            return null;
        }

        bool temporary = changeType == ChangeTypeTempBuffGive;
        if (changeType is ChangeTypeSetValueReal or ChangeTypeSuppress)
        {
            // 设值语义：不管卡面多少，最终就是 amount。存成「相对卡面费用的偏移」，
            // 这样和其它来源叠加时仍然是加法。
            amount -= target.Definition.Kredits;
        }

        int previousCost = target.KreditCost;
        var buff = GetOrCreateBuff(target, sourceId, temporary);
        if (temporary || changeType == ChangeTypePermBuff)
        {
            // Live fields are additive for both temporary and permanent buff modes.
            buff.KreditCost += amount;
        }
        else
        {
            buff.KreditCost = amount;
        }
        buff.KreditCostSetsAbsoluteValue =
            changeType is ChangeTypeSetValueReal or ChangeTypeSuppress;
        target.RecalculateStats();

        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
            ActionValue2.Int("instigatorID", sourceId),
        });

        if (target.KreditCost != previousCost)
        {
            c.Engine.Api.FireKreditCostChanged(target, target.KreditCost);
        }

        return null;
    }

    /// <summary>
    /// `ChangeKreditCost` 的 changeType 取值（实测只出现这三种）。
    ///
    /// ⚠️ 这几个常量是**逐字的 `EChangeType` 枚举值**，所以同名的 4 也被
    /// `ChangeAttack`（撤销该来源的攻 buff）/ `ChangeDefense`（非法值，no-op）复用 ——
    /// 名字里的 "Cost" 只是它当初的落点，别再按"只属于费用"来理解。
    /// </summary>
    private const int ChangeTypeTempBuffGive = 0;  // 临时相对费用加减
    private const int ChangeTypeSetValue = 1;      // 兼容手写光环的绝对值标记
    private const int ChangeTypePermBuff = 1;     // 永久相对费用加减
    private const int ChangeTypeSuppress = 3;     // 设值/抑制分支
    private const int ChangeTypeOffset = 0;        // 兼容旧的手写光环调用
    private const int ChangeTypeTempBuffRemove = 4; // 撤销该来源的临时改费
    private const int ChangeTypeOperationCostSetValue = 2;
    private const int ChangeTypeOperationCostSuppress = 3;
    private const int ChangeTypeOperationCostVeteranSet = 5;

    /// <summary>
    /// ⚠️ `EChangeType::SetValue` 的**真实枚举值**是 <b>2</b>，不是手写光环兼容常量 1。
    ///
    /// 出处：`<kards-src>\Source\kards\Public\EChangeType.h:6-17`
    /// <code>
    /// enum class EChangeType : uint8 {
    ///     tempBuffGive,   // 0
    ///     permBuff,       // 1
    ///     SetValue,       // 2
    ///     Suppress,       // 3
    ///     tempBuffRemove, // 4
    ///     veteranSet, customAdd, customRemove, combatModify, notUsed,
    /// };
    /// </code>
    ///
    /// 线上 1 实际是 `permBuff`（相对值、永久）；2 才是 `SetValue`。
    /// 全池统计为 changeType=0 ×58 / 1 ×41 / 2 ×7 / 4 ×30。
    /// 这里只**新增** 2 的处理，因为 `card_event_pams` 用它做
    /// 「Add it to your deck with a cost of 0.」（卡面原文，7 个调用点全是 `amount=0`）。
    /// </summary>
    private const int ChangeTypeSetValueReal = 2;

    /// <summary>
    /// 撤销某个来源在目标卡上的**费用** buff（其余 buff 保留）。
    ///
    /// 默认清理永久来源槽；`changeType=4` 会先清临时槽，找不到时回退到永久槽，
    /// 以兼容已经按来源记账的手写光环。
    /// </summary>
    private void RemoveCostBuff(CardInstance target, int sourceId, bool temporary = false,
                                bool fallbackToPermanent = false)
    {
        var key = (sourceId, temporary);
        if (temporary && !target.BuffsBySource.ContainsKey(key) && fallbackToPermanent)
        {
            key = (sourceId, false);
        }

        if (!target.BuffsBySource.TryGetValue(key, out var buff))
        {
            return;
        }

        buff.KreditCost = 0;
        buff.KreditCostSetsAbsoluteValue = false;
        if (buff.IsEmpty)
        {
            target.BuffsBySource.Remove(key);
        }

        target.RecalculateStats();
    }

    /// <summary>撤销某个来源在目标卡上的**全部永久** buff（`RemoveTheBuff` 的通用形态）。</summary>
    private void RemoveBuffFromSource(CardInstance target, int sourceId)
    {
        if (target.BuffsBySource.Remove((sourceId, false)))
        {
            target.RecalculateStats();
        }
    }

    /// <summary>
    /// 撤销某个来源在目标卡上的**攻击** buff（该来源的其余 buff 保留）——
    /// `ChangeAttack` 的 `changeType = 4`（`EChangeType::tempBuffRemove`）的落地处，
    /// 形状照 <see cref="RemoveCostBuff"/>（取槽 → 清零 → 空槽删掉 → `RecalculateStats`）。
    ///
    /// ⚠️ 与费用那个的关键差别：**攻击力不是派生量**。
    /// <see cref="CardInstance.RecalculateStats"/> 只重算费用 / 行动费 / 重甲
    /// （见它自己的注释「落点：KreditCost、OperationCost、重甲关键字」），**不动 Attack**；
    /// 而 `CardApi.ChangeAttack` 是直接写 `target.Attack += delta` 的 ——
    /// 所以撤销必须自己做**逆运算** `-=`（同一形状见 `CardApi.RemoveTemporaryBuffs`），
    /// 光清 buff 槽会让攻击力永久偏高。
    ///
    /// ⚠️ 槽位取 `(sourceId, false)`（永久槽）：内核的施加路径
    /// （`DoChangeAttack` 的默认分支）调的是 `ChangeAttack(target, delta, c.Self)`，
    /// `temporary` 用默认值 `false` ⇒ ct=0 / ct=1 都落在**永久槽**里。
    /// 撤销必须落在**同一个槽**上，「给→撤」才闭合。
    /// （蓝图那边更细：ct=0 存的是 `tempBuffGive` 键、ct=1 存的是 `permBuff` 键，
    /// ct=4 只删 `tempBuffGive` 那一个键。内核是「一个来源一个槽」的简化模型 ——
    /// 这属于既有口径，本轮**不动**，只保证给/撤这一对能对消。）
    ///
    /// 不发任何事件：这是 <see cref="RemoveCostBuff"/> 的形状（它只清槽 + `RecalculateStats`）。
    /// 蓝图 ct=4 之后确实还会走到 `NotifyGainAttack` / `ExecuteAfterChangeAttackEvents`
    /// （:6730 `L_07C6` → `IsActionProcess` → :6771 `L_08C0`），但那是**另一件事**：
    /// 补事件会往 `ActionLog` 里多插动作，把回放对拍的结论和本次数值修复搅在一起。
    /// </summary>
    private void RemoveAttackBuff(CardInstance target, int sourceId)
    {
        var key = (sourceId, false);
        if (!target.BuffsBySource.TryGetValue(key, out var buff))
        {
            // 蓝图 :6789 `L_096F` 的短路：`amountRemoved == 0` ⇒ 什么都不做（不崩、不改数值）。
            return;
        }

        // 逆运算：`CardApi.ChangeAttack` 是 `target.Attack += delta` + `buff.Attack += delta`，
        // 所以撤销是 `-= buff.Attack`；下限 0 与施加侧的 `Math.Max(0, …)` 对称。
        if (buff.Attack != 0)
        {
            target.Attack = Math.Max(0, target.Attack - buff.Attack);
        }

        buff.Attack = 0;
        if (buff.IsEmpty)
        {
            target.BuffsBySource.Remove(key);
        }

        target.RecalculateStats();
    }

    private object? DoSetKreditCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is { } target)
        {
            // 沿用「相对」实现：`SetKreditCost` 是旧派发表自己加的近似名，
            // 卡蓝图里没有这个词（实测 0 个调用点），改动它没有收益。
            ChangeKreditCost(target, IntArg(a, 2) - target.KreditCost);
        }

        return null;
    }

    private object? DoChangeOperationCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        // 签名（实测 140 个调用点）：ChangeOperationCost(卡, instigatorID, 数值, changeType, b, b, b)
        //   a[1] = 来源卡 ID，a[2] = 数值，a[3] = changeType（含义同 ChangeKreditCost）
        //
        // ⚠️ 和 ChangeKreditCost 一样必须按来源记账、绝对值重算。旧版只做
        //    `target.OperationCost += delta`，于是 `card_unit_214th_amur` 的
        //    「T-34 行动费 -1」每触发一次就再减一次，而 `RemoveTheBuff`
        //    传的 0 又什么都没撤销 —— 行动费一路掉到 0 再也回不来。
        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            var key = target.BuffsBySource.ContainsKey((sourceId, true))
                ? (sourceId, true)
                : (sourceId, false);
            if (target.BuffsBySource.TryGetValue(key, out var existing))
            {
                existing.OperationCost = 0;
                existing.OperationCostSetsAbsoluteValue = false;
                if (existing.IsEmpty)
                {
                    target.BuffsBySource.Remove(key);
                }

                target.RecalculateStats();
            }

            return null;
        }

        bool temporary = changeType == ChangeTypeTempBuffGive;
        var buff = GetOrCreateBuff(target, sourceId, temporary);
        bool setsAbsolute = changeType is ChangeTypeOperationCostSetValue
            or ChangeTypeOperationCostSuppress
            or ChangeTypeOperationCostVeteranSet;
        if (setsAbsolute)
        {
            amount -= target.Definition.OperationCost;
            buff.OperationCost = amount;
            buff.OperationCostSetsAbsoluteValue = true;
        }
        else if (temporary || changeType == ChangeTypePermBuff)
        {
            buff.OperationCost += amount;
            buff.OperationCostSetsAbsoluteValue = false;
        }
        else
        {
            buff.OperationCost = amount;
            buff.OperationCostSetsAbsoluteValue = false;
        }
        target.RecalculateStats();

        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", sourceId),
            ActionValue2.Int("amount", amount),
        });

        return null;
    }

    /// <summary>
    /// 改重甲（对应 `ChangeHeavyArmor`）。
    ///
    /// 签名（实测 6 个调用点）：`ChangeHeavyArmor(卡, instigatorID, 数值, changeType, bool, out)`
    /// 语义和费用那两个同构 —— changeType=4 是「撤销该来源的临时重甲」。
    /// `card_unit_214th_amur` 用 `+1 / changeType=0` 施加、`0 / changeType=4` 撤销。
    ///
    /// 注意重甲是**可叠加的数值**，不是关键字；关键字只在点数 &gt; 0 时同步挂上
    /// （见 `CardInstance.RecalculateStats`），因为别处（快照、老的 `AddHeavyArmor`）按关键字判。
    /// </summary>
    private object? DoChangeHeavyArmor(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            if (target.BuffsBySource.TryGetValue((sourceId, false), out var existing))
            {
                existing.HeavyArmor = 0;
                if (existing.IsEmpty)
                {
                    target.BuffsBySource.Remove((sourceId, false));
                }

                target.RecalculateStats();
            }

            return null;
        }

        var buff = GetOrCreateBuff(target, sourceId);
        buff.HeavyArmor = amount;
        target.RecalculateStats();

        _engine.FireSubAction("ZActionAddHeavyArmor", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("instigatorID", sourceId),
            ActionValue2.Int("amount", amount),
        });

        return null;
    }

    private object? DoDamageCard(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // DamageCard(card, amount, damagerCardID, isRedirected, fromFight, isFightDefenderDamage, out targetDestroyed)
        // 参数名/顺序出处：资产里 `FunctionExport.LoadedProperties` 的 `CPF_Parm` 声明序
        // （`ref/kards-sim/KardsTranspiler/BlueprintSignatures.cs` 的取法），
        // 调用点互证 `out/bp-cardfn.json` → `DamageCard` si=690。
        //
        // ⚠️ 第 3 参在蓝图里是 **cardID（int）**，不是卡对象（权威签名
        //    `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:11606-11618`：
        //    `L["damagerCardID"] = args[2]` → `GetCardFromID(damagerCardID)` 得
        //    `damageDealer`，见 `g.cs:11624-11626`）。
        //
        // ★ 2026-10-03 修：旧写法 `AsCard(a[2]) ?? c.Self` —— `AsCard(整数) = null`
        //   ⇒ **整数形状一律退回施法者**，伤害来源记成施法者自己。改成 `AsCardOrId`
        //   （同族先例：`DestroyCard` 的 `destroyer`、`DiscardCard` 的 `discarder`）。
        //
        // IR 实测（`docs/card-ir.json` 扫描，2026-10-03）：271 个调用点，`a[2]` =
        //   · 裸 `{"var":"cardID"}`（= `ctx.Self.CardId`）×262 —— 新旧解析**逐位相同**；
        //   · `{"var":"cardID","ctx":{…}}` = **别人卡的 cardID** ×7
        //     （`card_event_infiltrate`、`card_unit_halifax`、`card_event_yamato`、
        //      `card_event_jungle_warfare`、`card_event_special_attack`、
        //      `card_event_sunny2_heatwave3`、`card_event_sunny4_scorching_sun3`）；
        //   · `spawnedCardID` ×2（`card_event_audacity`、`card_location_soviet_scen4`）。
        //   ⇒ 那 9 处的「伤害来源」此前记成施法者（影响 `ZActionDamageCard.attackerCardID`
        //     与 `OnCardDealDamage` / `OnOtherCardDealDamage` 的 `damageDealer` 事件参数）。
        int amount = IntArg(a, 1);
        var source = AsCardOrId(c, a.ElementAtOrDefault(2)) ?? c.Self;

        // si=149 `JumpIfNot(isRedirected) -> si=690`：重定向伤害**跳过**修正链。
        // si=690 `ExecuteOnDealDamageAddDamage(damageDealer, card, amount, False, fromFight, False, …)`
        bool isRedirected = a.Length > 3 && Blueprint.KismetVm.Truthy(a[3]);
        bool fromFight = a.Length > 4 && Blueprint.KismetVm.Truthy(a[4]);

        DealDamage(target, amount, source, isRedirected: isRedirected, fromFight: fromFight);
        return null;
    }

    private object? DoHealCard(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is { } target)
        {
            HealCard(target, IntArg(a, 1));
        }

        return null;
    }

    /// <summary>
    /// `BP_CardFunctions.DamageMultipleCards`（7 条语句）——
    /// 「对一批卡同时造成等量伤害」的包装。
    ///
    /// 蓝图逐字：
    /// <code>
    /// si=0  Array_Length(cardsToDamage)          ; 空数组直接返回
    /// si=1  Greater_IntInt(len, 0)               ; si=2 JumpIfNot → 184 return
    /// si=3  ApplyDamageToMultipleCards(receiverIDs, damagerCardID, damage,
    ///                                  out damageReceivedArr, out destroyedArr,
    ///                                  out locationArr, out OldDefenseArr)
    /// si=4  cardsDestroyed = ApplyDamageToMultipleCards_outputDestroyedCards
    /// </code>
    ///
    /// 实参形状（6 个独立调用点互证：`anzac_spirit` / `firestorm_skirm` /
    /// `shelling` / `blockade` / `carpet_bombing` / `ApplyDamageToMultipleCards` 自身）：
    /// <code>
    /// [0] cardsToDamage   ← **卡对象数组**（不是 ID 数组！）
    /// [1] damage          （多数是字面量，少数是 this.damageToDeal）
    /// [2] attackerID / instigatorID
    /// [3] out cardsDestroyed
    /// </code>
    ///
    /// ⚠️ 关键：`[0]` 是**对象数组**，而 `receiverIDs` 这个名字是在
    /// `ApplyDamageToMultipleCards` **内部**（它才是收 ID 数组的那个）。
    /// 外层按名字去读 ID 数组会一张卡都打不到 —— 这正是它一直没被实现时
    /// 最容易被写错的地方。这里按调用点形状读对象数组。
    ///
    /// 伤害走 <see cref="CardApi.DealDamage"/> 这唯一漏斗，所以修正链
    /// （`OnCardDealDamage_ModifyDamageDealt` / `OnOtherCardDealDamageAddDamage`）
    /// 和 `ZActionDamageMultipleCards` 都自然带上，不需要在这里重做一遍。
    /// </summary>
    private object? DoDamageMultipleCards(EffectContext c, object? r, object?[] a)
    {
        var targets = EvalArray(r, a)
            .Select(AsCard)
            .Where(x => x is not null && x.IsAlive)
            .Select(x => x!)
            .ToList();

        if (targets.Count == 0)
        {
            return null;
        }

        int amount = IntArg(a, 1);
        var source = AsCard(a.ElementAtOrDefault(2)) ?? c.Self;

        var destroyed = new List<int>();
        // 先物化一份快照：DealDamage 会就地 Destroy（改 Location），
        // 在 foreach 里读正在被改的集合是错的。
        foreach (var target in targets)
        {
            DealDamage(target, amount, source);
        }

        // 收尾统一数一次「谁死了」。蓝图是在 ApplyDamageToMultipleCards 内部逐个记的，
        // 效果一样（`destroyed` 出参只被读、不影响结算）。
        foreach (var target in targets)
        {
            if (!target.IsAlive)
            {
                destroyed.Add(target.CardId);
            }
        }

        if (destroyed.Count > 0)
        {
            _engine.FireSubAction("ZActionDamageMultipleCards", new[]
            {
                ActionValue2.Int("attackerID", source?.CardId ?? 0),
                ActionValue2.Int("damage", amount),
            });
        }

        return destroyed;
    }

    /// <summary>
    /// `BP_CardFunctions.MakeCardsFight`（**互斗**）—— 让两个单位互相打一次。
    ///
    /// ## 1. 签名：**4 个实参**，不是 6 个
    ///
    /// ⚠️ 任务书里那句「`CardApi.cs:850` 的签名形状 `(a, b, dmg, False, True, False)` ——
    /// **6 个参数**」说的是 `MakeCardsFight` **内部**那两次
    /// `ExecuteOnDealDamageAddDamage` 调用（6 个入参 + 1 个出参），**不是**
    /// `MakeCardsFight` 自己的签名。证据：
    /// <list type="bullet">
    /// <item><c>ref/kards-sim/KardsSim/Generated/_index.g.cs:3994</c> ——
    /// <c>["MakeCardsFight"] = new[] { "unitThisSide", "unitOppositeSide", "instigatorID", "qqq" }</c>
    /// （参数名来自资产 `FunctionExport.LoadedProperties` 的 `CPF_Parm` 声明序）；</item>
    /// <item><c>BP_CardFunctions.g.cs:25913-25916</c> ——
    /// <c>args[0]=unitThisSide, args[1]=unitOppositeSide, args[2]=instigatorID, args[3]=out qqq</c>；</item>
    /// <item>IR 里 12/12 个调用点都是 4 个实参（`args[3]` 是 out 槽
    /// <c>CallFunc_MakeCardsFight_qqq</c>，且出参**从不被读**）。</item>
    /// </list>
    /// 六个参数的形状是蓝图内部（`g.cs:25977` / `26006`）：
    /// <c>ExecuteOnDealDamageAddDamage(_damageDealerCard, _damageRecieverCard, damage,
    /// _fromAttack, fromFight, _isDefenderDamage)</c>
    /// —— `dmg` 是"这一方向、修正前的伤害"，后三个 bool 是
    /// `_fromAttack=False` / `fromFight=True` / `_isDefenderDamage=False`。
    ///
    /// ## 2. 和 `Attack`（定向攻击）的区别
    ///
    /// | | `MatchEngine.Attack`（`ExecuteAttackCard`） | `MakeCardsFight` |
    /// |---|---|---|
    /// | 攻防对称性 | 攻击方 → 防御方，防御方**反击** | **双向**，两边各用自己的 `getTotalAttack` |
    /// | 伤害算的时机 | 打一下、算一下 | **两个方向都算完**，再依次落地（`si=12F/1E8` 在 `si=26E/299` 之前） |
    /// | `fromAttack` | `True`（战斗伤害） | **`False`** —— 互斗是**效果伤害**，不是战斗伤害 |
    /// | `isDefenderDamage` | 反击那笔是 `True` | **两个方向都是 `False`** |
    /// | 反击门 | 防御方**死了就不反击**（`MatchEngine.cs:1570` 的 `defender.IsAlive`） | **没有这道门** ⇒ 3/3 打 3/3 **双方都死** |
    /// | 行动资源 | 扣油费、`HasAttackedThisTurn`、`AttacksThisTurn++`、`OnBeforeAttack`、`OnAfterAttack` | **一样都不碰**（谁都不是"攻击方"） |
    /// | 烟幕 | 攻击后自己消失 | **不消失** |
    /// | 召唤失调 / 压制 / 守护 / 射程 | 全都要过 `CanAttack` 那一族门 | **一道门都不过** |
    ///
    /// ## 3. 门：只有 `IsValid` + `IsLocatedOnBoard`
    ///
    /// `cardsCheckFunctions` 里**没有**对应的门。那份规则库只有 `CanAttack` /
    /// `CanSelectAsTarget`（`ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs`），
    /// 而 `MakeCardsFight` **两个都不调**。它自己只有两道：
    /// <list type="number">
    /// <item>`si=1C/43` `IsValid(unitThisSide)` / `IsValid(unitOppositeSide)` —— 任一无效直接返回；</item>
    /// <item>`si=FC/1B5` `IsLocatedOnBoard(...)` —— **只决定"那一方向的伤害算不算"**，
    /// 不算就保持 `si=5` 的初值 0；两个 `ApplyDamageToCard`（`si=26E/299`）
    /// **在控制流上无条件执行**（两条 PopExecutionFlow 分支都汇到 `L_026E`）。</item>
    /// </list>
    /// ⇒ **召唤失调的单位照样能被强制互斗**，被压制的、有烟幕的、够不着的、有守护保护的
    /// 也全都照样。这和"能不能主动攻击"是两码事。
    ///
    /// ## 4. 互斗双方**可以是同一方**——蓝图对此**没有任何处理**
    ///
    /// 参数名虽然叫 `unitThisSide` / `unitOppositeSide`，但函数体里**一次阵营判定都没有**
    /// （`g.cs:25918-26034` 全文没有 `side` / `IsSameSide` / `GetPlayingSide` 之类的调用）。
    /// 传两个友方单位进去，它们就真的互相打。内核**照抄这个行为，不自己加门**。
    /// 实测 12 个调用点里 `card_unit_57th_rifles`（`i=624`）的 `enemyUnit` 也是
    /// 蓝图自己选出来的，不是 `MakeCardsFight` 校验的。
    ///
    /// ## 5. 已知偏差（如实记，不修）
    ///
    /// <list type="bullet">
    /// <item>`isFightDefenderDamage`（第 5 个实参：`si=26E` 传 `False`、`si=299` 传 `True`）
    /// 在蓝图里**只流向 `NotifyDamageCard`**（`g.cs:1104`），是纯客户端表现标记，
    /// 不进任何规则判定 —— 内核的 `ZActionDamageCard` 没有这个字段，故不建模。</item>
    /// <item>内核的 <see cref="CardApi.IsLocatedOnBoard"/> 是
    /// `Location.IsBoard() &amp;&amp; !IsHq`（**排除 HQ**），而反编译参考里原生
    /// `IsLocatedOnBoard` 是 `Loc is Loc.Board or Loc.Frontline`（**含 HQ**，
    /// `ref/kards-sim/KardsSim/Bridge/EngineHost.cs:995-999`）。
    /// 这里**复用内核那一个**（与 `IsLocatedOnBoard` 派发键同源，不另立第二套判据）；
    /// 12 个调用点传的都是"单位"（`unitThisSide` / `unitOppositeSide`），HQ 不在其中，
    /// 所以实际无差。若以后真出现"互斗 HQ"，要改的是内核那一个判据，不是这里。</item>
    /// <item>`ExecuteOnDealDamageAddDamageAfterCalc` 内核没有单独一层
    /// （它只改 `finalDamage`，见 `g.cs:15949`），所以这里与 `DealDamage` 一致地省略。
    /// ⚠️ 免疫那一半不会因此漏：蓝图在那层里做 `getHasImmune(toCard) ⇒ finalDamage = 0`
    /// （`g.cs:15979-15985`），内核等价地做在 `MatchEngine.ApplyDamage`
    /// （`MatchEngine.cs:1801-1804`），净效果相同。</item>
    /// <item>**"先算完两笔、再落地"只做到了伤害值那一半。**
    /// 蓝图 `ApplyDamageToCard` **自己不摧毁**（只 `_isDestroyed = getTotalDefense() &lt;= 0`
    /// 再 `NotifyDamageCard`，`g.cs:1076-1104`），收尸在外面统一做 ⇒
    /// 方向 1 打死人**不会**插在方向 2 的扣血之前。
    /// 内核的 `DealDamage` 尾段是**立即** `Destroy(target)`（`CardApi.cs:1046-1049`），
    /// 所以这里方向 1 的死亡触发会先于方向 2 的扣血。
    /// **不为此另开旁路**：那是 `DealDamage` 这个唯一漏斗的既有形状，
    /// `DamageCard` / `DamageMultipleCards` 全都一样；要改就该在漏斗里改。
    /// 实际影响面很窄 —— 两笔伤害的**数值**已经先算好了（`si=6A/AE`），
    /// 只有"方向 1 的死亡触发去改方向 2 的**防御**（治疗/加护甲/给免疫）"才看得出差别。</item>
    /// </list>
    /// </summary>
    private object? DoMakeCardsFight(EffectContext c, object?[] a)
    {
        // 出参 `qqq` 全卡池从不被读（12/12 调用点的 `CallFunc_MakeCardsFight_qqq` 只写不读），
        // 蓝图里它连赋值都没有（`g.cs:25917` 初值 Nothing，`26038` 原样回传）⇒ 返回 null。
        var thisSide = AsCardOrId(c, a.ElementAtOrDefault(0));
        var opposite = AsCardOrId(c, a.ElementAtOrDefault(1));

        // si=1C / si=43：两道 IsValid 门。任一无效 ⇒ 整段不发生（连攻击值都不读）。
        if (thisSide is null || opposite is null)
        {
            return null;
        }

        // si=6A / si=AE：两个方向的攻击值都在**任何伤害落地之前**取。
        // 用 `Attack`（= 蓝图 `getTotalAttack`，含 buff）而不是 `Definition.Attack`。
        int damageToOpposite = thisSide.Attack;
        int damageToThis = opposite.Attack;

        // si=12F / si=1E8：两个方向的修正链也都在**落地之前**跑完（这就是相位拆分的理由，
        // 见 `CardApi.ApplyCalculatedDamage` 的注释）。
        // 入参逐字对齐 `g.cs:25977` / `26006`：
        //   ExecuteOnDealDamageAddDamage(dealer, receiver, damage, _fromAttack=False,
        //                                fromFight=True, _isDefenderDamage=False)
        // ⚠️ `fromFight=True` 是**互斗伤害的身份标记**，不是战斗伤害 —— `isCombatDamage` 保持 False。
        int finalToOpposite = IsLocatedOnBoard(opposite)
            ? ExecuteOnDealDamageAddDamage(thisSide, opposite, damageToOpposite,
                                           fromAttack: false, fromFight: true, isDefenderDamage: false)
            : 0;

        int finalToThis = IsLocatedOnBoard(thisSide)
            ? ExecuteOnDealDamageAddDamage(opposite, thisSide, damageToThis,
                                           fromAttack: false, fromFight: true, isDefenderDamage: false)
            : 0;

        // si=26E / si=299：两笔伤害**依次**落地，数值都已算好。
        // 蓝图 `ApplyDamageToCard`（`g.cs:893-896`，标签 `L_0108`）：
        // `if (!(finalDamage > 0)) goto L_0181` ⇒ 0 伤害整段跳过，
        // 这里照抄那道门（也因此不会发出多余的 `ZActionDamageCard`）。
        if (finalToOpposite > 0)
        {
            ApplyCalculatedDamage(opposite, finalToOpposite, thisSide,
                                  isCombatDamage: false, counterDamage: false, isRedirected: false);
        }

        if (finalToThis > 0)
        {
            ApplyCalculatedDamage(thisSide, finalToThis, opposite,
                                  isCombatDamage: false, counterDamage: false, isRedirected: false);
        }

        // ⚠️ **不在这里判"谁死了"**：`Defense <= 0` 只是"待销毁"，
        // 真死在 `CheckDeaths()` 里（由动作层收尾调用）。蓝图同理 ——
        // `MakeCardsFight` 自己不做任何摧毁，两个方向都打完之后才由外层收尸。
        return null;
    }

    /// <summary>
    /// `BP_CardFunctions.AddAttackUntilEndOfTurn` —— 「+N 攻击，直到回合结束」。
    /// 语义与出处见 <see cref="CardApi.AddAttackUntilEndOfTurn"/>。
    ///
    /// ⚠️ 这里**不用** `TargetCard`：那个辅助函数是给「接收者=施法者、
    /// 目标在 `Parameters[0]`」这一类原语用的（见它自己的注释）。
    /// 而本函数的调用点形状是 `(卡, this.cardID, IntConst:N)` —— 三个参数**都是实参**，
    /// `[0]` 就是目标卡。用 `TargetCard` 会去读接收者，在这条链上是错的。
    /// </summary>
    private object? DoAddAttackUntilEndOfTurn(EffectContext c, object?[] a)
    {
        var target = AsCard(a.ElementAtOrDefault(0));
        var source = AsCard(a.ElementAtOrDefault(1)) ?? c.Self;
        AddAttackUntilEndOfTurn(target, source, IntArg(a, 2));
        return null;
    }

    private object? DoDestroyCard(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is not null)
        {
            DestroyCard(target, c.Self);
        }

        return null;
    }

    private object? DoTriggerDestruction(EffectContext c, object? r, object?[] a)
    {
        var target = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? c.Target ?? AsCard(r) ?? c.Self;
        var instigator = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Self;
        if (target is null)
        {
            return 0;
        }

        return c.Engine.Api.TriggerDestruction(target, instigator,
            TruthyArg(a, 2), TruthyArg(a, 3));
    }

    private object? DoTriggerDeployment(EffectContext c, object? r, object?[] a)
    {
        var target = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? c.Target ?? AsCard(r);
        if (target is null)
        {
            return 0;
        }

        c.Engine.Api.TriggerDeployment(target, IntArg(a, 1, c.Self?.CardId ?? 0));
        return 0;
    }

    private object? DoSpawnInHand(EffectContext c, object? r, object?[] a)
    {
        // SpawnCardInHandBySide(side, ?, instigatorID, bool, bool, bool, 卡名, textVar, int, out)
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = FindCardNameArg(c, a);
        return cardName is null ? null : SpawnCardInHand(side, cardName);
    }

    /// <summary>
    /// 从参数里找出「要生成的卡名」。
    ///
    /// 不能死认第 6 个位置：不同卡的参数排布不一样，而且有时卡名来自运行时变量
    /// （例如随机选卡的结果）而不是字符串常量。所以按「看起来像卡名且确实在卡库里」
    /// 扫一遍，找不到就返回 null —— 交给调用方记成已知未支持，而不是抛异常。
    /// </summary>
    private string? FindCardNameArg(EffectContext c, object?[] a)
    {
        foreach (object? v in a)
        {
            if (v is string s && s.StartsWith("card_", StringComparison.Ordinal) && c.State.Database.Find(s) is not null)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// `SpawnCardOnBattlefield(side, Frontline, card_name, spawnerID, campaignName,
    ///  NewGiveBlitz, locationNumber, salvageFaction, NewMakeVeteran, forceGoldCard,
    ///  out spawnedCardID)` —— 权威签名 `CardFunctionsStub.h:68`。
    ///
    /// ⚠️ 旧实现只读 `a[0]`（side）+ 扫卡名，**其余可选参数全丢** —— 其中
    /// <c>Frontline</c>（<c>a[1]</c>）是**落点**：丢掉它等于把 215 个传 `false`
    /// 的调用点全部生成到**前线**。IR 实测（`out/audit/p0-argshapes.py`，234 个调用点）：
    /// <code>
    /// a[0] side   a[1] Frontline(false×215 / true×4 / 变量×15)   a[2] card_name
    /// a[3] spawnerID   a[4] campaignName   a[5] NewGiveBlitz(false×169 / true×63)
    /// a[6] locationNumber(-1×193)   a[7] salvageFaction   a[8] NewMakeVeteran(false×234)
    /// a[9] forceGoldCard(false×233)   a[10] out
    /// </code>
    /// 本实现读：`Frontline`、`locationNumber`（-1 = 追加到队尾）、`NewGiveBlitz`、
    /// `NewMakeVeteran`、`forceGoldCard`。
    /// **不实现**：`campaignName`（战役）、`salvageFaction`（Salvage 关键字，P1 未建模）。
    /// `spawnerID` 只用于"谁生成的"记账，当前内核没有对应字段，不假装实现。
    /// </summary>
    private object? DoSpawnOnBattlefield(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = a.Select(AsString).FirstOrDefault(s => s is not null && s.StartsWith("card_", StringComparison.Ordinal));
        if (cardName is null)
        {
            return null;
        }

        var card = SpawnOnBattlefield(side, cardName,
            frontline: TruthyArg(a, 1),
            locationNumber: IntArg(a, 6, -1),
            newGiveBlitz: TruthyArg(a, 5),
            forceGoldCard: TruthyArg(a, 9));

        if (TruthyArg(a, 8))
        {
            MakeVeteran(card);
        }

        return card;
    }

    /// <summary>
    /// `SpawnMultipleCardsOnBattlefield(side, Frontline, cardNames, spawnerID,
    /// giveBlitz, out spawnedCardIDs, makeVeteran)`。
    ///
    /// 蓝图先要求卡名数组非空且 <c>spawnerID &gt; 0</c>，再按数组顺序逐张生成。
    /// 半场在包含 HQ 的 5 格达到上限时停止；前线沿用蓝图的特殊路径，不在此处
    /// 额外截断。生成卡继承生成者的金卡标记，出参是实际生成卡的 ID 数组。
    /// </summary>
    private object? DoSpawnMultipleOnBattlefield(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        var names = EvalList(r, a)
            .Cast<object?>()
            .Select(AsString)
            .Where(name => name is not null
                && name.StartsWith("card_", StringComparison.Ordinal)
                && c.State.Database.Find(name) is not null)
            .Select(name => name!)
            .ToList();
        int spawnerId = IntArg(a, 3);
        if (names.Count == 0 || spawnerId <= 0)
        {
            return new List<int>();
        }

        bool frontline = TruthyArg(a, 1);
        bool giveBlitz = TruthyArg(a, 4);
        bool makeVeteran = TruthyArg(a, 6);
        bool forceGoldCard = c.State.ById(spawnerId)?.IsGold ?? false;
        var spawnedIds = new List<int>(names.Count);

        foreach (string name in names)
        {
            if (!frontline
                && c.State.Cards(side, side.HqOf()).Count >= GameState.HalfBoardCapacity)
            {
                break;
            }

            var card = SpawnOnBattlefield(side, name,
                frontline: frontline,
                locationNumber: -1,
                newGiveBlitz: giveBlitz,
                forceGoldCard: forceGoldCard);
            if (makeVeteran)
            {
                MakeVeteran(card);
            }

            spawnedIds.Add(card.CardId);
        }

        return spawnedIds;
    }

    /// <summary>
    /// `SpawnNextToCard(card_name, spawnerId, side, out spawnedCardID,
    /// copySpawnerAttack, copySpawnerDefense, salvageFaction, makeVeteran)`.
    ///
    /// 蓝图实际把生成者的 side/location/locationNumber+1、金卡标记和 spawnerID
    /// 传给 `SpawnCardToBoard`；`copySpawnerAttack` 为真时再把生成卡的攻击力调到
    /// 与生成者一致。`copySpawnerDefense` 在当前函数体中没有被读取，故保持无效。
    /// </summary>
    private object? DoSpawnNextToCard(EffectContext c, object?[] a)
    {
        string? cardName = a.ElementAtOrDefault(0) as string;
        int spawnerId = IntArg(a, 1);
        var spawner = c.State.ById(spawnerId);
        if (spawner is null
            || string.IsNullOrWhiteSpace(cardName)
            || c.State.Database.Find(cardName) is null)
        {
            return 0;
        }

        bool frontline = spawner.Location == CardLocation.BoardFrontline;
        if (!frontline && spawner.Location != spawner.Owner.HqOf())
        {
            return 0;
        }

        CardLocation location = spawner.Location;
        int insertNumber = spawner.LocationNumber + 1;
        var existing = c.State.Cards(spawner.Owner, location)
            .Where(card => !card.IsHq)
            .ToList();
        var originalNumbers = existing.ToDictionary(card => card, card => card.LocationNumber);

        var spawned = SpawnOnBattlefield(spawner.Owner, cardName,
            frontline,
            insertNumber,
            newGiveBlitz: false,
            forceGoldCard: spawner.IsGold);

        // `GameState.Move` normalizes a newly appended card by insertion order.
        // The Blueprint's `SpawnCardToBoard` instead inserts at the requested slot
        // and shifts cards already occupying that slot, so restore that numbering
        // after the shared spawn path has emitted its normal events.
        foreach (var card in existing)
        {
            int originalNumber = originalNumbers[card];
            card.LocationNumber = originalNumber >= insertNumber
                ? originalNumber + 1
                : originalNumber;
        }

        spawned.LocationNumber = insertNumber;

        if (TruthyArg(a, 4))
        {
            int delta = spawner.Attack - spawned.Attack;
            if (delta != 0)
            {
                ChangeAttack(spawned, delta, spawned);
            }
        }

        if (TruthyArg(a, 7))
        {
            MakeVeteran(spawned);
        }

        return spawned.CardId;
    }

    /// <summary>
    /// `ResetUnitOperations(cardID, giverID, out qqq)`.
    ///
    /// 蓝图先要求目标是棋盘上的单位，然后把 movementLeft 设为 1，
    /// 把 attackLeft 设为 Fury ? 2 : 1。内核用本回合移动/攻击状态和
    /// 攻击次数表示这两个额度；`NotifyResetUnitOperations` 只是动作流程
    /// 下的客户端通知，本无头内核没有 notifier，故不产生表现层状态。
    /// </summary>
    private object? DoResetUnitOperations(EffectContext c, object?[] a)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0));
        if (card is null || !IsLocatedOnBoard(card) || !IsUnit(card))
        {
            return null;
        }

        card.HasMovedThisTurn = false;
        card.HasAttackedThisTurn = false;
        card.AttacksThisTurn = 0;
        return null;
    }

    // ==================== 2026-10-02：补缺口的辅助实现 ====================

    /// <summary>
    /// `DiscardCardFromHand(card_or_cardID, discarderID, skipTriggers, skipVisuals, out success)`
    /// —— 派发表里的 (B) 一行注册，落到已有的 <see cref="CardApi.DiscardCard"/>。
    ///
    /// 出处：<c>CardApi.cs:1653</c> 的文档注释
    /// 「对应 `BP_CardFunctions::DiscardCardFromHand` / `DiscardCard`」。
    ///
    /// ⚠️ 参数形状**不唯一**（IR 实测 39 个调用点 / 14 种形状，
    /// <c>out/audit/ir-callsites.py DiscardCardFromHand</c>）：
    ///   <list type="bullet">
    ///   <item><c>a[0]</c> 有时是**卡对象**（`{var:cardToDiscard}` / `{var:CallFunc_Array_Get_Item}`），
    ///         有时是**整数 cardID**（`{var:cardID, ctx:{var:...}}`，9+3 个调用点）——
    ///         所以必须走 <see cref="AsCardOrId"/>，只认一种形状会静默丢掉一半调用点
    ///         （同 <see cref="AsCardOrId"/> 注释里那 94 张卡的前科）。</item>
    ///   <item><c>a[1]</c> = `discarderID`（"谁弃的"）。</item>
    ///   <item><c>a[2]</c>/<c>a[3]</c> = `skipTriggers`/`skipVisuals`（全卡池都是 false）。
    ///         `skipTriggers` 本内核**不支持**（见「没修的」清单）—— 传 true 的调用点会多广播一次
    ///         `OnOtherCardDiscarded`，不会少做事。</item>
    ///   </list>
    /// 返回值写进 <c>a[4]</c> 的 out 槽：弃成功 true / 卡无效 false
    /// （蓝图里 `success` 只在卡无效或 `OnAttemptedDiscard` 取消时为 false）。
    /// </summary>
    private object? DoDiscardCardFromHand(EffectContext c, object? r, object?[] a)
    {
        var card = AsCard(a.ElementAtOrDefault(0)) ?? AsCardOrId(c, a.ElementAtOrDefault(0));
        if (card is null)
        {
            return false;
        }

        DiscardCard(card, AsCardOrId(c, a.ElementAtOrDefault(1)));
        return true;
    }

    /// <summary>
    /// `DiscardRandomCardFromHand(side, instigatorID, out discardedCardID)`。
    ///
    /// 蓝图按 `GetAllCards` 的顺序筛选目标阵营手牌；只有候选非空时才消费一次
    /// `GetRandomCard` 的 UE 随机流。复用 <see cref="DiscardCard"/> 保持弃牌动作和
    /// `OnOtherCardDiscarded` 广播一致；弃牌被规则限制时回报 0。
    /// </summary>
    private object? DoDiscardRandomCardFromHand(EffectContext c, object? r, object?[] a)
    {
        var hand = GetAllCards()
            .Where(card => card.Location == GetHandLocationBySide(SideArg(r, a, 0, c.Controller)))
            .ToList();
        if (hand.Count == 0)
        {
            return 0;
        }

        var selected = GetRandomCard(hand);
        if (selected is null)
        {
            return 0;
        }

        DiscardCard(selected, AsCardOrId(c, a.ElementAtOrDefault(1)));
        return selected.Location == CardLocation.Discard ? selected.CardId : 0;
    }

    /// <summary>
    /// `DiscardCardFromDeck(cardID, discarderID, skipTriggers, skipVisuals, out success)`。
    ///
    /// 出处：直译产物 <c>out/Generated-gap/_deps/BP_CardFunctions.g.cs</c> 的同名函数体：
    /// <code>
    /// L_0005  cardID > 0 ?
    /// L_0031  GetCardFromID(cardID) → tmpCardToDiscard ; IsValid ?
    /// L_008B  tmpCard.location == 2 || == 1 ?        ; 只在牌库里才弃
    /// L_01C8  OnAttemptedDiscard(…, out cancelDiscard) ; cancel ⇒ success=false 且不弃
    /// L_0228  RemoveCardFromDeckBySide(side, cardID) + ExecuteOnAfterDeckChanged(side)
    /// L_02AB  SetCardLocationAndLocNumber(cardID, 8 /*Discard*/, 0)
    /// L_02EE  NotifyDiscardCard(cardID, discarderID, oldLocation, …) ; success = true
    /// </code>
    ///
    /// 本内核的 <see cref="CardApi.DiscardCard"/> 做的正是「移到 Discard + 广播
    /// `OnOtherCardDiscarded`」，所以直接复用。
    /// **不实现** <c>OnAttemptedDiscard</c> 的取消门（本内核没有这个事件）——
    /// 少一次取消机会，**不会多弃牌**。
    /// </summary>
    private object? DoDiscardCardFromDeck(EffectContext c, object? r, object?[] a)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0));
        if (card is null || !IsLocatedInDeck(card))
        {
            return false;
        }

        DiscardCard(card, AsCardOrId(c, a.ElementAtOrDefault(1)));
        return true;
    }

    /// <summary>
    /// ★ `SpawnCardInFrontline(card_name, side, spawnerID, out campaignName, giveBlitz,
    ///   out spawnedCardID, salvageFaction, locationNumber, makeVeteran)`
    /// —— 108 调用点 / 29 张卡。
    ///
    /// 出处（**不是猜的**）：直译产物 <c>out/Generated-gap/_deps/BP_CardFunctions.g.cs:35201</c>
    /// 的同名函数体，全部逻辑只有一步：
    /// <code>
    /// SpawnCardToBoard(card_name, side, 7 /*BoardFrontline*/, locationNumber, 0,
    ///                  gold, giveBlitz, spawnerID, 0, makeVeteran, out spawnedCardID)
    /// </code>
    /// 其中 <c>gold</c> 的来历：`campaignName` 为空且 `spawnerID > 0` 时取
    /// <c>GetCardFromID(spawnerID).isGoldCard</c>。
    ///
    /// 参数下标逐个与 IR 的 108 个调用点吻合
    /// （<c>out/audit/ir-callsites.py SpawnCardInFrontline</c>；例 `card_event_airdrop` i=219
    ///  <c>[name, side, cardID, Temp_text_Variable_1, false, out, 0, -1, false]</c>）。
    ///
    /// 落点 `7` = <see cref="CardLocation.BoardFrontline"/>，**不是** `SpawnCardOnBattlefield`
    /// 那种按 `a[1]` 判半场/前线 —— 这正是它和那条"同族但已验证"的实现的关键差别。
    /// 本内核的 <see cref="CardApi.SpawnOnBattlefield"/> 已经处理了
    /// 「追加到队尾 / 指定槽位 / Blitz / 山地加成 / `ZActionSpawnCard`」，所以复用它。
    /// </summary>
    private object? DoSpawnInFrontline(EffectContext c, object? r, object?[] a)
    {
        string? cardName = FindCardNameArg(c, a);
        if (cardName is null)
        {
            return null;
        }

        // side 在 a[1]（a[0] 是卡名）。`SideArg` 的兜底是 c.Controller ——
        // 蓝图里这一位几乎总是显式 `side` 变量或 1/2 常量。
        var side = SideArg(r, a, 1, c.Controller);

        // `gold`：spawner 是金卡时生成的也是金卡（函数体 L_007B）。
        bool gold = AsCardOrId(c, a.ElementAtOrDefault(2))?.IsGold ?? false;

        var card = SpawnOnBattlefield(side, cardName,
            frontline: true,
            locationNumber: IntArg(a, 7, -1),
            newGiveBlitz: TruthyArg(a, 4),
            forceGoldCard: gold);

        // `makeVeteran`（a[8]）：函数体把它当 `SpawnCardToBoard` 的实参传下去；
        // 本内核 `SpawnOnBattlefield` 没有这个参数，等价地在生成之后调 `MakeVeteran`
        // （与 `DoSpawnOnBattlefield` 对 `SpawnCardOnBattlefield` 的处理同构）。
        if (TruthyArg(a, 8))
        {
            MakeVeteran(card);
        }

        return card;
    }

    /// <summary>
    /// `GetCardsInSupportLineBySide(side, unitsOnly, includeCovertCards, out cards)`
    /// —— 41 调用点 / 34 张卡。
    ///
    /// 出处：直译产物 <c>_deps/BP_CardFunctions.g.cs</c> 同名函数体的完整循环，
    /// 四个条件依次是：
    /// <code>
    /// ① card.side == side
    /// ② card.location == GetSupportLineBySide(side)
    /// ③ !IsUnrevealedCovertCard(card) || includeCovertCards
    /// ④ unitsOnly ⇒ IsUnit(card)
    /// </code>
    /// 本内核的映射：
    /// <list type="bullet">
    /// <item>② 半场就是 <see cref="SideExtensions.HqOf"/>（`BoardHqLeft/Right`，
    ///       见 <c>Engine/Enums.cs</c> 的注释：`GetSupportLineLocationBySide(side)` = 5/6）。</item>
    /// <item>③ <see cref="CardApi.IsUnrevealedCovertCard"/> 按卡面 Covert 与揭示位过滤。</item>
    /// <item>④ 按参数过滤。**HQ 不排除**：蓝图里 `GetAllCardInBattle` 含 HQ、
    ///       过滤只按 location；`unitsOnly=false` 的调用点会拿到 HQ（与蓝图同）。
    ///       实测 41 个调用点里 `unitsOnly` 全是 true 或变量。</item>
    /// </list>
    /// </summary>
    private object? DoGetCardsInSupportLine(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        bool unitsOnly = TruthyArg(a, 1);

        var cards = c.State.Cards(side, side.HqOf());
        if (unitsOnly)
        {
            cards = cards.FindAll(x => IsUnit(x));
        }

        return cards;
    }

    /// <summary>
    /// `IsLocationFull(location, out isFull)` —— 20 调用点 / 17 张卡。
    ///
    /// 出处：直译产物 <c>_deps/BP_CardFunctions.g.cs</c> 同名函数体的**全部**内容
    /// 就是一次 `FetchCardsByLocation(location, out QtyInLocation, out isLocationFull, …)`
    /// 然后把 `isLocationFull` 转出去。
    ///
    /// 容量（`FetchCardsByLocation` 的体 + <see cref="GameState"/> 的常量）：
    /// <list type="bullet">
    /// <item><c>7</c> 前线 → <see cref="GameState.FrontlineCapacity"/>
    ///       （默认 <see cref="GameState.DefaultFrontlineCapacity"/>=5，有 limiter 时 2）。</item>
    /// <item><c>5</c>/<c>6</c> 半场 → <see cref="GameState.HalfBoardCapacity"/>=5，
    ///       **计数含 HQ**（`MatchEngine.HalfBoardFull` 的注释已定案）。</item>
    /// <item><c>3</c>/<c>4</c> 手牌 → <see cref="GameState.HandCapacity"/>=9。</item>
    /// <item>牌库 <c>1</c>/<c>2</c>、弃牌堆 <c>8</c> → **永不"满"**（蓝图里没有上限）。</item>
    /// </list>
    /// </summary>
    private object? DoIsLocationFull(EffectContext c, object? r, object?[] a)
    {
        var loc = (CardLocation)IntArg(a, 0);
        int count = c.State.CardsUnordered().Count(x => x.Location == loc);
        return loc switch
        {
            CardLocation.BoardFrontline => count >= c.State.FrontlineCapacity,
            CardLocation.BoardHqLeft or CardLocation.BoardHqRight => count >= GameState.HalfBoardCapacity,
            CardLocation.HandLeft or CardLocation.HandRight => count >= GameState.HandCapacity,
            _ => false,
        };
    }

    /// <summary>
    /// `DestroyMultipleCards(cardsToDestroy, destroyerCardID, out)` —— 20 调用点 / 19 张卡。
    ///
    /// 出处：直译产物 <c>_deps/BP_CardFunctions.g.cs</c> 同名函数体：
    /// 数组长度 &gt; 0 时调 <c>ApplyDestroyMultipleCards(self, destroyerCardID, out cardsToDestroy)</c>。
    /// `ApplyDestroyMultipleCards` 本身也是缺失键，但它的语义就是「逐张 Destroy」
    /// （参考实现 `EngineHost.cs` 里同名的也是这个形状），所以这里直接逐张走
    /// <see cref="CardApi.DestroyCard"/> —— 少一层名字依赖，行为一致。
    ///
    /// ⚠️ 数组元素**两种形状都有**（卡对象 / 整数 cardID），用 <see cref="AsCardOrId"/>
    /// 统一（同 `DiscardCardFromHand` 的理由）。
    /// </summary>
    private object? DoDestroyMultipleCards(EffectContext c, object? r, object?[] a)
    {
        var list = EvalList(r, a);
        var destroyer = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Self;
        int n = 0;
        foreach (object? v in list)
        {
            var card = AsCard(v) ?? AsCardOrId(c, v);
            if (card is not null)
            {
                DestroyCard(card, destroyer);
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// `SalvageMultipleUnits(cardsToSalvage, instigatorID, out createdCardIDs)`。
    /// The input array is card IDs; the result is the newly created card ID array.
    /// </summary>
    private object? DoSalvageMultipleUnits(EffectContext c, object? r, object?[] a)
    {
        var ids = new List<int>();
        foreach (object? value in EvalList(r, a))
        {
            int id = value is int cardId ? cardId : AsCard(value)?.CardId ?? 0;
            if (id > 0)
            {
                ids.Add(id);
            }
        }
        int instigatorId = IntArg(a, 1, c.Self?.CardId ?? 0);
        return SalvageMultipleUnits(ids, instigatorId).ToArray();
    }

    /// <summary>
    /// `AddDefenseToMultipleCards(receiverIDs, amount, giverCardID, out qqq)`.
    ///
    /// The Blueprint's receiver array is <c>TArray&lt;int&gt;</c>, so resolve each
    /// ID independently and preserve the input order.  Its post-pass lowers or
    /// raises <c>maxDefense</c> by the same amount for cards that remain alive;
    /// that matters for later repair and <c>IsDamaged</c> checks, especially for
    /// the -2 debuff used by the two current callers.
    /// </summary>
    private object? DoAddDefenseToMultipleCards(EffectContext c, object? r, object?[] a)
    {
        var receivers = EvalList(r, a);
        int amount = IntArg(a, 1);
        CardInstance? source = AsCardOrId(c, a.ElementAtOrDefault(2)) ?? c.Self;

        foreach (object? value in receivers)
        {
            var target = AsCardOrId(c, value);
            if (target is null || !target.IsAlive)
            {
                continue;
            }

            int oldMaxDefense = target.MaxDefense;
            ChangeDefense(target, amount, source);

            if (target.IsAlive)
            {
                target.MaxDefense = Math.Max(0, oldMaxDefense + amount);
                if (target.MaxDefense < target.Defense)
                {
                    target.MaxDefense = target.Defense;
                }
            }
        }

        // The native out parameter is an Int. Current callers do not consume it;
        // return a deterministic success value rather than exposing a bool.
        return 0;
    }

    private object? DoForceCardChangeLocation(EffectContext c, object?[] a)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0));
        if (card is null || HasCustomAbility(card, "cantMove"))
        {
            return new object?[] { false, (int)CardLocation.NotAvailable, 0 };
        }

        var oldLocation = card.Location;
        int oldLocationNumber = card.LocationNumber;
        var newLocation = (CardLocation)IntArg(a, 2);
        int requestedNumber = IntArg(a, 3);

        if (oldLocation == newLocation && oldLocationNumber == requestedNumber)
        {
            return new object?[] { false, (int)oldLocation, oldLocationNumber };
        }

        // Blueprint callers use -1 as "append" for generated moves.
        c.State.Move(card, newLocation, requestedNumber < 0 ? null : requestedNumber);
        return new object?[] { true, (int)oldLocation, oldLocationNumber };
    }

    // ---- 自定义名后缀（`CustomName1*` / `CustomName2*`）--------------------
    //
    // 语义出处：参考实现 `ref/kards-sim/KardsSim/Bridge/EngineHost.cs:2160-2182`
    // （`SuffixAdd` / `SuffixRemove`）与 `:1088/:1307/:1498-1500/:1639`（各条原语）。
    // 存储：`CardInstance.CustomJson`（和客户端的卡私有 JSON 同一层），
    // 值是用逗号分隔的标记串。**幂等**（重复 Add 同一个标记不重复追加）——
    // 参考实现就是这么写的，而且客户端里 `CustomName1Add` 会被反复触发。
    //
    // 为什么值得补：`CardApi.cs:631` 早就记了一条「没做 `StopDestructionEffect` 那一半：
    // 它是 `CustomName1` 属性（si=3539 `CustomName1Add("StopDestructionEffect")`），
    // 而 `CustomName1*` 三件套内核里没有」。这一段就是来关掉那条 TODO 的。

    /// <summary>`CustomName{1,2}Add(标记)`：往这张卡的后缀串里加一个标记（幂等）。</summary>
    private void SuffixAdd(EffectContext c, object? r, object?[] a, string key)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a);
        string tag = StrArg(a, 0);
        if (card is null || tag.Length == 0)
        {
            return;
        }

        var parts = JsonGetString(card, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Contains(tag, StringComparer.Ordinal))
        {
            return;
        }

        parts.Add(tag);
        JsonSetString(card, key, string.Join(",", parts));
    }

    /// <summary>`CustomName{1,2}Remove(标记)`：从后缀串里删掉一个标记。</summary>
    private void SuffixRemove(EffectContext c, object? r, object?[] a, string key)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a);
        string tag = StrArg(a, 0);
        if (card is null || tag.Length == 0)
        {
            return;
        }

        var kept = JsonGetString(card, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.Equals(x, tag, StringComparison.Ordinal));
        JsonSetString(card, key, string.Join(",", kept));
    }

    /// <summary>
    /// `CustomName{1,2}HasAttribute(标记, out doesIt)` —— 后缀串里有没有这个标记。
    /// 形状：`recv` = 被查的卡（8 个 `CustomName1` 调用点里有 2 个 recv 是 null ⇒
    /// 走 <see cref="SelfArg"/> 的 `c.Self` 兜底），`a[0]` = 标记串，`a[1]` = out。
    /// </summary>
    private object? SuffixHas(EffectContext c, object? r, object?[] a, string key)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a);
        string tag = StrArg(a, 0);
        if (card is null || tag.Length == 0)
        {
            return false;
        }

        // 与引擎内部（`CardApi.ShouldTriggerDestructionEffect` 读 StopDestructionEffect）
        // 共用同一个读法 —— 写方与读方分开实现过一次就会漂，这里不再留第二份。
        return CustomNameHasAttribute(card, key, tag);
    }

    /// <summary>`GetCustomName2Attributes(out 属性数组)` —— 参考实现 `EngineHost.cs:1639`。</summary>
    private object? SuffixList(EffectContext c, object? r, object?[] a, string key)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0)) ?? SelfArg(c, r, a);
        if (card is null)
        {
            return new List<string>();
        }

        return JsonGetString(card, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    // ==================== 光环（aura）的实现 ====================
    //
    // 四张光环卡：
    //   card_unit_85_pioneer_company  本回合第一张指令 -1 费（通用下限 0）
    //   card_unit_big_red_one         手牌里的牌都是 4 费
    //   card_unit_214th_amur          己方 T-34 +1 重甲、行动费 -1
    //   card_event_committed_crew     本回合 Spitfire 0 费、部署时 +3+3
    //
    // 语义全部从 `ref/kards-sim/KardsSim/Generated/.../<卡名>.g.cs` 的直译产物读出
    // （IR 里没有私有函数体，见派发表里那一段注释）。
    // `self` 就是 IR 里的 `cardFunction`；`args` 已经由 VM 摘掉 `{self:true}` 占位。

    /// <summary>
    /// 光环的「我 buff 过哪些卡」——对应客户端把 `cardID` 写进卡自己的私有 JSON。
    ///
    /// 为什么必须自己维护这份账：`isBuffedByCard(卡, 来源ID)` 是
    /// `RemoveTheBuff` / `ApplyTheBuff` 判「这张卡我已经加过了吗」的**唯一**依据。
    /// 以前派发表把 `isBuffedByCard` 近似成「`BuffsBySource` 非空」，
    /// 那对光环是错的：手牌里 A 卡被光环 buff 过，不代表 B 卡也被 buff 过。
    /// </summary>
    private const string BuffedCardsKey = "buffedCards";

    /// <summary>诊断用：非 null 时记录光环每一次 Apply 的资格判定结果。</summary>
    public List<string>? AuraTrace { get; set; }

    /// <summary>
    /// 卡自己的 `buffActive` 标记 —— 客户端用 `JSON_SetBool(buffActive, true)`
    /// 记「这个光环当前是不是生效中」。
    ///
    /// ⚠️ **必须维护它**，不能省：`card_unit_85_pioneer_company` 的每一个
    /// 触发分支（`OnOtherCardPlayedFromHand` 还原、`OnEndOfTurn` 重挂、
    /// `OnOtherCardDrawnFromDeck` 补挂、`OnLeaveBoardOrOwner` 还原）
    /// 都先问 `JSON_GetBool(buffActive)`，取到 `found=false` 就直接 return。
    /// 只在别处补 buff、不设这个标记，那些分支**全部静默失效** ——
    /// 实测症状：光环进场时手牌指令确实 -1 了，但打出一张指令之后**不会还原**
    /// （还原分支判 `buffActive` 为假就跳过了）。
    ///
    /// 客户端里"found"来自 JSON 键是否存在，所以这里用「键在不在」而不是值：
    /// `JsonSetBool` 写入 "0" 之后再读仍然是 found=true。
    /// </summary>
    private void MarkBuffActive(CardInstance aura, bool active)
        => JsonSetBool(aura, "buffActive", active);

    /// <summary>把 <paramref name="target"/> 记进光环自己的「我 buff 过的卡」名单。</summary>
    private void RememberBuffedCard(CardInstance aura, CardInstance target)
    {
        var ids = JsonGetIntArray(aura, BuffedCardsKey);
        if (ids.Contains(target.CardId))
        {
            return;
        }

        ids.Add(target.CardId);
        JsonSetIntArray(aura, BuffedCardsKey, ids);
    }

    /// <summary>第 <paramref name="index"/> 个参数是不是这张卡自己 buff 过的卡。</summary>
    private bool IsBuffedBySelf(CardInstance aura, object? arg)
    {
        if (AsCard(arg) is not { } card)
        {
            return false;
        }

        return JsonGetIntArray(aura, BuffedCardsKey).Contains(card.CardId);
    }

    /// <summary>
    /// `getCardsBuffedByThisCard(out Cards)` —— 返回当前来源卡仍挂着其 buff 的目标 ID。
    ///
    /// 客户端字段是 <c>cardsBuffedByThisCard: TArray&lt;int&gt;</c>。内核没有单独复制
    /// 这个原生数组，而是把光环自己的名单写入 <c>buffedCards</c>，并把每个目标的
    /// 实际增益记在 <see cref="CardInstance.BuffsBySource"/>。两份来源取并集：
    /// JSON 名单保留蓝图的添加顺序，BuffsBySource 则覆盖 ChangeAttack/ChangeDefense
    /// 等非光环路径，避免只靠某一条记账链漏掉目标。
    /// </summary>
    private object? GetCardsBuffedByThisCard(EffectContext c, object? receiver, object?[] args)
    {
        var source = SelfArg(c, receiver, args);
        if (source is null)
        {
            return new List<int>();
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();

        void Add(int id)
        {
            if (id > 0 && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        foreach (int id in JsonGetIntArray(source, BuffedCardsKey))
        {
            Add(id);
        }

        foreach (var target in c.State.AllCards)
        {
            if (target.BuffsBySource.Keys.Any(key => key.SourceCardId == source.CardId))
            {
                Add(target.CardId);
            }
        }

        return ids;
    }

    /// <summary>
    /// 取「改费/改行动费/改重甲」的**来源卡 ID**（这些原语的第 2 个实参）。
    ///
    /// ⚠️ 实测调用约定**不统一**，不能死认某一个下标：
    ///   内部函数里是 `ChangeKreditCost(tempCard, cardID, -1, 0, …)`（cardID 在 a[1]）
    ///   而 `ExecuteUbergraph` 内联路径里可能是 `ChangeKreditCost(tempCard, -1, 0, …)`（数值在 a[1]）
    ///   —— 后者是 kardsim 的读法（它按 `a[3]` 取数值）。
    ///   判据：a[1] 必须是一个**真实存在的卡 ID**；否则退回施法者自己。
    ///   来源 ID 认错的后果是 buff 记到不存在的来源上，撤销时找不到 → 数值永久偏移，
    ///   所以这里宁可退回施法者（至少在 `RemoveTheBuff` 时会用同一个来源去撤）。
    /// </summary>
    private static int SourceCardIdArg(object?[] a, int index, CardInstance? self)
    {
        if (index < a.Length && a[index] is int id && id > 0)
        {
            return id;
        }

        return self?.CardId ?? 0;
    }

    /// <summary>
    /// `ApplyTheBuff` —— 光环把 buff 施加到目标卡上。
    ///
    /// 调用形状有两种，都要认：
    /// - **无实参**：`ApplyTheBuff()` —— self 就是光环，作用于「所有符合条件的卡」
    ///   （85_pioneer：手牌里所有指令；committed_crew：所有 Spitfire；214th：所有 T-34）。
    ///   这三张卡的 g.cs 里 `ApplyTheBuff` 自己就是**循环体**（`GetAllCards` + 逐个判条件），
    ///   所以这里按卡名分派到对应的判定上。
    /// - **一个实参**：`ApplyTheBuff(cardToBeBuffed)` —— self 是光环，参数是目标
    ///   （big_red_one 的 `ExecuteUbergraph` 里就是 `ApplyTheBuff(CallFunc_Array_Get_Item)`）。
    /// </summary>
    private object? DoApplyTheBuff(EffectContext c, object?[] a)
    {
        var aura = c.Self;
        if (aura is null)
        {
            return null;
        }

        if (a.Length > 0 && AsCard(a[0]) is { } explicitTarget)
        {
            ApplyAuraBuffTo(aura, explicitTarget);
            return null;
        }

        foreach (var target in c.State.AllCards)
        {
            ApplyAuraBuffTo(aura, target);
        }

        return null;
    }

    /// <summary>按光环卡名把 buff 加到一张目标卡上（含各自的资格判定）。</summary>
    private void ApplyAuraBuffTo(CardInstance aura, CardInstance target)
    {
        if (AuraTrace is not null)
        {
            AuraTrace.Add($"{aura.Definition.Name} → {target.Name}#{target.CardId}" +
                          $"（在手上={IsLocatedInHand(target)} 在场={IsLocatedOnBoard(target)}" +
                          $" 指令={IsOrder(target)} 同阵营={target.Owner == aura.Owner}" +
                          $" 费用={target.EffectiveKreditCost}" +
                          $" t34={HasGameplayTag(target, "subtype.t34")}" +
                          $" spitfire={HasGameplayTag(target, "subtype.spitfire")}" +
                          $" buff槽={target.BuffsBySource.Count}）");
        }

        switch (aura.Definition.Name)
        {
            case "card_unit_85_pioneer_company":
                // 判据（g.cs ApplyTheBuff）：IsOrder && IsLocatedInHand && 同阵营
                // 效果：ChangeKreditCost(卡, 自己, -1, 0)  —— 只减 1，不是减到 1
                AuraTrace?.Add($"      85 判定: IsOrder={IsOrder(target)} " +
                               $"IsLocatedInHand={IsLocatedInHand(target)} 同阵营={target.Owner == aura.Owner}");
                if (IsOrder(target) && IsLocatedInHand(target) && target.Owner == aura.Owner)
                {
                    AuraTrace?.Add($"      → 85 命中 {target.Name}#{target.CardId}");
                    ApplyAuraKreditCost(aura, target, -1, ChangeTypeOffset);
                }

                break;

            case "card_unit_big_red_one":
                // 判据：IsLocatedInHand && 同阵营；然后
                //   if (getTotalKreditCost(卡) == 4) 跳过（已经是 4 费了，别再动）
                //   else ChangeKreditCost(卡, 自己, 4 - 当前费用, 0)
                if (IsLocatedInHand(target) && target.Owner == aura.Owner)
                {
                    int current = target.EffectiveKreditCost;
                    if (current != BigRedOneCost)
                    {
                        ApplyAuraKreditCost(aura, target, BigRedOneCost - current, ChangeTypeOffset);
                    }
                }

                break;

            case "card_event_committed_crew":
                // 判据：IsLocatedInHand && 同阵营 && subtype.spitfire && 当前费用 > 0
                // 效果：ChangeKreditCost(卡, 自己, -当前费用, 1)  —— changeType=1，
                //       所以允许落到 0（卡面就写着 "Spitfires cost 0 to deploy"）
                if (IsLocatedInHand(target)
                    && target.Owner == aura.Owner
                    && HasGameplayTag(target, "subtype.spitfire"))
                {
                    int cost = target.EffectiveKreditCost;
                    AuraTrace?.Add($"      → committed_crew 命中 Spitfire，当前费用 {cost}");
                    if (cost > 0)
                    {
                        ApplyAuraKreditCost(aura, target, -cost, ChangeTypeSetValue);
                    }
                }

                break;

            case "card_unit_214th_amur":
                // 判据：IsLocatedOnBoard && subtype.t34 && 同阵营
                // 效果：ChangeHeavyArmor(+1, changeType=0) + ChangeOperationCost(-1, changeType=0)
                if (IsLocatedOnBoard(target) && target.Owner == aura.Owner
                    && HasGameplayTag(target, "subtype.t34"))
                {
                    ApplyAuraHeavyArmor(aura, target, 1);
                    ApplyAuraOperationCost(aura, target, -1);
                }

                break;
        }
    }

    /// <summary>big_red_one 把手里所有牌定成这个费用（卡面原话 "cost 4 kredits"）。</summary>
    private const int BigRedOneCost = 4;

    /// <summary>光环式改费：**同一来源幂等**。</summary>
    private void ApplyAuraKreditCost(CardInstance aura, CardInstance target, int amount, int changeType)
    {
        if (target.BuffsBySource.TryGetValue((aura.CardId, false), out var existing)
            && existing.KreditCost == amount
            && existing.KreditCostSetsAbsoluteValue == (changeType == ChangeTypeSetValue))
        {
            return;   // 已经加过同一个值，重复施加不叠加
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.KreditCost = amount;
        buff.KreditCostSetsAbsoluteValue = changeType == ChangeTypeSetValue;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);

        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
            ActionValue2.Int("instigatorID", aura.CardId),
        });
    }

    private void ApplyAuraOperationCost(CardInstance aura, CardInstance target, int amount)
    {
        if (target.BuffsBySource.TryGetValue((aura.CardId, false), out var existing)
            && existing.OperationCost == amount)
        {
            return;
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.OperationCost = amount;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);

        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", aura.CardId),
            ActionValue2.Int("amount", amount),
        });
    }

    private void ApplyAuraHeavyArmor(CardInstance aura, CardInstance target, int amount)
    {
        if (target.BuffsBySource.TryGetValue((aura.CardId, false), out var existing)
            && existing.HeavyArmor == amount)
        {
            return;
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.HeavyArmor = amount;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);
    }

    /// <summary>
    /// `RemoveTheBuff` —— 光环撤销自己施加过的全部 buff。
    ///
    /// 调用形状：
    /// - 无实参：self 是光环，撤销「名单上所有卡」（85_pioneer / committed_crew / 214th）
    /// - `RemoveTheBuff(卡)`：self 是光环，只撤这一张（big_red_one 的 ubergraph 路径）
    ///
    /// 判据用光环自己记的名单，不用 `IsLocatedInHand` 之类的**当前位置**：
    /// 卡可能已经打出/被弃掉了，位置上已经判不出来，但 buff 还挂在它身上。
    /// （客户端的 RemoveTheBuff 里也有 `isBuffedByCard` 这一步，但 g.cs 里 85_pioneer
    ///  的 RemoveTheBuff 判的是 `IsValid(tempCard)`，214th 的判的是名单 —— 位置判据不可靠。）
    /// </summary>
    private object? DoRemoveTheBuff(EffectContext c, object?[] a)
    {
        var aura = c.Self;
        if (aura is null)
        {
            AuraTrace?.Add("RemoveTheBuff: Self 为 null，跳过");
            return null;
        }

        AuraTrace?.Add($"RemoveTheBuff({aura.Definition.Name}#{aura.CardId}) 实参 {a.Length} 个，" +
                       $"名单={string.Join(",", JsonGetIntArray(aura, BuffedCardsKey))}");

        if (a.Length > 0 && AsCard(a[0]) is { } explicitTarget)
        {
            RemoveAuraBuffFrom(aura, explicitTarget);
            return null;
        }

        foreach (int cardId in JsonGetIntArray(aura, BuffedCardsKey))
        {
            if (c.State.ById(cardId) is { } target)
            {
                RemoveAuraBuffFrom(aura, target);
            }
        }

        JsonSetIntArray(aura, BuffedCardsKey, Array.Empty<int>());
        MarkBuffActive(aura, false);
        return null;
    }

    private void RemoveAuraBuffFrom(CardInstance aura, CardInstance target)
    {
        AuraTrace?.Add($"RemoveTheBuff: {aura.Definition.Name} → {target.Name}#{target.CardId}" +
                       $"（buff槽={target.BuffsBySource.Count} 含本来源={target.BuffsBySource.ContainsKey((aura.CardId, false)) || target.BuffsBySource.ContainsKey((aura.CardId, true))}）");

        bool removed = target.BuffsBySource.Remove((aura.CardId, false));
        removed |= target.BuffsBySource.Remove((aura.CardId, true));
        if (!removed)
        {
            return;
        }

        // ⚠️ 这里撤销的是「该来源在目标卡上的**全部** buff」，而不只是费用那一条。
        //    对这四张光环是等价的（它们只改费用/行动费/重甲，互不冲突）。
        //    但如果将来某张卡的 RemoveTheBuff 只想撤费用、保留攻防，
        //    就必须改成按字段撤销 —— 客户端的 ChangeKreditCost(…, changeType=4)
        //    只清 KreditCost，不清 Attack/Defense。
        target.RecalculateStats();
    }

    /// <summary>
    /// `anyOrderPlayedThisTurn` —— 本回合有没有打过**己方**的指令牌。
    ///
    /// g.cs 的循环体：遍历 `GetCardsPlayedThisTurn()`，命中
    /// `IsOrder(卡) && 卡.side == self.side` 就把结果置 true 并 break。
    /// 这是 `card_unit_85_pioneer_company`（"The first order you play each turn costs 1 less"）
    /// 判「第一张已经用掉了」的唯一依据。
    /// </summary>
    private bool AnyOrderPlayedThisTurn(EffectContext c)
    {
        Side own = SelfSide(c);
        foreach (var card in c.State.CardsPlayedThisTurn)
        {
            if (IsOrder(card) && card.Owner == own)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// `isBuffedByCard(卡, 来源ID)` —— 这张卡有没有被指定来源 buff 过。
    ///
    /// ⚠️ 只用「来源卡自己记的名单」判，不看 `BuffsBySource` 非空：
    /// 旧实现是「有任意 buff 就算」，对光环是错的 ——
    /// `card_unit_85_pioneer_company` 的 RemoveTheBuff 会遍历**所有卡**，
    /// 拿这个判据决定要不要给某张手牌还原费用，用「非空」会把没被它 buff 过的牌也还原。
    /// </summary>
    private bool IsBuffedByCard(EffectContext c, object? r, object?[] a)
    {
        // ⚠️ **接收者也要走 SelfArg**（审计 §5.1）：9 个调用点是隐式 self，
        //    旧写法 `AsCard(r)` 在 r 为 null 时恒 false ⇒ 光环的
        //    "这张卡我已经加过了吗"判据失效（会重复施加 buff）。
        //    注意 `a[0]` 是来源卡 ID（int），`SelfArg` 只认 `CardInstance`，
        //    所以扫参数不会把来源 ID 误当成接收者。
        if (SelfArg(c, r, a) is not { } target)
        {
            return false;
        }

        // 来源 ID 在 a[0]（实测 102 个调用点里绝大多数是 `isBuffedByCard(卡, cardID, out)`；
        // 也有 0 参形态，那时 a[0] 是 out 槽 = null）。
        // 认不出来时退回「有没有被任何来源 buff 过」——这是旧行为，比恒 false 安全。
        if (a.Length > 0 && a[0] is int sourceId && sourceId > 0)
        {
            return target.BuffsBySource.ContainsKey((sourceId, false));
        }

        return target.BuffsBySource.Count > 0;
    }

    /// <summary>
    /// `getHasGameplayTag(标签数组, out 有没有)` —— 判子类型。
    ///
    /// 实参里可能出现两种形状（都实测到了）：
    /// - `{array:[{name:"subtype.t34"}]}`（`card_unit_214th_amur` 等）→ 字符串列表
    /// - `{struct:null}`（`card_event_committed_crew` 的 IR：生成器把
    ///   `StructConst /Script/GameplayTags.GameplayTag` 整段丢了，只剩 null）
    ///
    /// 后一种是**上游 IR 的缺口**：真正的标签名在 `cards.full.json` 的
    /// `Properties["Missing property name0"][0].Value` 里。所以这里在拿到空列表时，
    /// 退一步用「卡名 → tag」的静态表（`GameplayTagTable`）反查：
    /// 那张表把所有 tag 一次列全，按卡自己带的 tag 判，等价于「这个 tag 在不在我身上」。
    /// </summary>
    private static bool HasGameplayTag(EffectContext c, object? r, object?[] a)
    {
        // ⚠️ 接收者走 `SelfArg`（审计 §5.1：`getHasGameplayTag` 有 1 个隐式 self 调用点，
        //    旧写法 `AsCard(r)` 在那里恒 false）。参数里都是 tag 字符串，
        //    `SelfArg` 扫参数不会误判。
        if (SelfArg(c, r, a) is not { } card)
        {
            return false;
        }

        var wanted = new List<string>();
        foreach (object? v in a)
        {
            CollectTagStrings(v, wanted);
        }

        if (wanted.Count == 0)
        {
            // IR 把 tag 丢了 —— 只要这张卡带了**任意** tag，就说明调用方想问的是
            // 「它是不是某个子类型」。这个兜底会让「有 tag 的卡」全部命中，
            // 对 214th/committed_crew 这两张卡是正确的（它们各自只判一个 tag），
            // 但它**不是通用正确解**，所以同时把缺口记进未实现统计里，别让它静默。
            c.Engine.State.UnimplementedCalls["getHasGameplayTag<ir-tag-lost>"] =
                c.Engine.State.UnimplementedCalls.GetValueOrDefault("getHasGameplayTag<ir-tag-lost>") + 1;
            return GameplayTagTable.Any(card.Name);
        }

        // 数组语义是「命中任意一个即为真」（客户端把单 tag 也包成一元数组）
        foreach (string tag in wanted)
        {
            if (c.Engine.Api.HasCustomGameplayTag(card, tag)
                || GameplayTagTable.Has(card.Name, tag))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGameplayTag(CardInstance card, string tag)
        => card.CustomJson.TryGetValue(CardApi.DynamicGameplayTagsKey, out string? raw)
            && raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase))
            || GameplayTagTable.Has(card.Name, tag);

    private static bool IsForecastCard(CardInstance card)
        => HasGameplayTag(card, "subtype.rain")
            || HasGameplayTag(card, "subtype.storm")
            || HasGameplayTag(card, "subtype.sunny");

    private static bool HasBond(CardInstance card)
        => !string.Equals(card.CustomAbility, "bond_removed", StringComparison.Ordinal)
            && (card.Keywords.Contains(Keyword.Bond) || HasGameplayTag(card, "ability.bond"));

    private static void CollectTagStrings(object? v, List<string> into)
    {
        switch (v)
        {
            case string s when s.Length > 0:
                into.Add(s);
                break;
            case System.Collections.IEnumerable e and not string:
                foreach (object? item in e)
                {
                    CollectTagStrings(item, into);
                }

                break;
        }
    }

    private object? DoChangeKredits(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        int delta = a.Select(AsInt).FirstOrDefault(v => v != 0);
        State.AddKredits(side, delta);
        return null;
    }

    private object? DoCustomAbilityAdd(EffectContext c, object? r, object?[] a)
    {
        // CustomAbilityAdd(能力名, 目标cardID, 施予者cardID, bool, bool, bool, out)
        //
        // ⚠️ `a[1]` 是**整数 cardID**（注释与调用点都是这个形状），
        //    而旧实现用 `AsCard(a[1])` ⇒ **恒为 null** ⇒ 永远退回 `c.Target`。
        //    对"目标是施法者自己"的调用点恰好等价，对指向别人的调用点就**静默打错卡**。
        //    （与 `Array_Add` 那个 bug 同一族，见 `AsCardOrId` 的注释。）
        string ability = StrArg(a, 0);
        var target = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Target;
        if (target is not null && ability.Length > 0)
        {
            CustomAbilityAdd(target, ability, c.Self);
        }

        return null;
    }

    private object? DoAddCustomGameplayTag(EffectContext c, object? r, object?[] a)
    {
        var tags = new List<string>();
        if (a.Length > 0)
        {
            CollectTagStrings(a[0], tags);
        }

        var target = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Target ?? AsCard(r) ?? c.Self;
        if (target is not null)
        {
            foreach (string tag in tags)
            {
                c.Engine.Api.AddCustomGameplayTag(target, tag);
            }
        }

        return null;
    }

    private object? DoRemoveCustomGameplayTag(EffectContext c, object? r, object?[] a)
    {
        var tags = new List<string>();
        if (a.Length > 0)
        {
            CollectTagStrings(a[0], tags);
        }

        var target = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Target ?? AsCard(r) ?? c.Self;
        if (target is not null)
        {
            foreach (string tag in tags)
            {
                c.Engine.Api.RemoveCustomGameplayTag(target, tag);
            }
        }

        return null;
    }

    private object? DoCustomAbilityRemove(EffectContext c, object? r, object?[] a)
    {
        var target = AsCardOrId(c, a.ElementAtOrDefault(1)) ?? c.Target;
        target?.CustomAbility = null;
        return null;
    }

    private object? DoSuppressUnit(EffectContext c, object? r, object?[] a)
    {
        // `SuppressUnit(卡, instigatorID, bool, bool, out)` —— 目标在 a[0]。
        // ⚠️ 同样可能是**整数 cardID**（与 `CustomAbilityAdd` 同一族），所以用 `AsCardOrId`。
        var target = AsCardOrId(c, a.FirstOrDefault()) ?? AsCard(r) ?? c.Target;
        if (target is not null)
        {
            SuppressUnit(target);
        }

        return null;
    }

    /// <summary>
    /// `SuppressMultipleUnits(cardsToSuppress, instigatorID, out qqq)`
    /// —— `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:35659`。
    ///
    /// 这是「抑制」的**真正实现体**（`SuppressUnit` 只是把单张卡包成数组转发进来，
    /// 见同文件 `:36484`），所以派发表里两个键都必须在。
    ///
    /// 逐张调 <see cref="CardApi.SuppressUnit"/>：蓝图那一遍循环（`:35728-35738`
    /// `Array_Length` → `Array_Get` → `GetCardFromID`）对每张卡做的唯一一件事就是
    /// 走同一段 `SuppressMultipleUnits` 主体，所以"逐张调同一个函数"与蓝图等价。
    ///
    /// ⚠️ 队列要**先物化快照**：`SuppressUnit` 会跑触发器，触发器的效果可能
    /// 反过来改这些卡（例如 `card_unit_cromwell_mk_iv`「When an enemy unit is pinned,
    /// Suppress it.」那类连锁），在 `foreach` 里读正在被改的集合是错的
    /// —— 与 `DoDamageMultipleCards`（本文件 `:2726`）同一条理由。
    /// </summary>
    private object? DoSuppressMultipleUnits(EffectContext c, object? r, object?[] a)
    {
        var targets = new List<CardInstance>(EvalArray(r, a));

        foreach (var target in targets)
        {
            SuppressUnit(target);
        }

        return null;
    }

    // ECombatKeyword values from the shipped enum.  None (0) is a sentinel and
    // is deliberately excluded from random candidates.
    private static readonly (int Value, string Keyword)[] CombatKeywordEntries =
    [
        (1, Keyword.Ambush),
        (2, Keyword.Blitz),
        (3, Keyword.Fury),
        (4, Keyword.Guard),
        (5, Keyword.HeavyArmor),
        (6, Keyword.Shock),
        (7, Keyword.Smokescreen),
    ];

    private static object? DoGetCombatKeywords(EffectContext c, object? receiver, object?[] args)
    {
        var card = SelfArg(c, receiver, args);
        if (card is null)
        {
            return new object?[] { new List<int>(), 0 };
        }

        var keywords = CombatKeywordEntries
            .Where(entry => card.Keywords.Contains(entry.Keyword)
                || (entry.Keyword == Keyword.HeavyArmor && card.HeavyArmor > 0))
            .Select(entry => entry.Value)
            .ToList();
        return new object?[] { keywords, keywords.Count };
    }

    /// <summary>
    /// `GiveRandomCombatKeyword(cardID, instigatorID, out keywordGiven, out success)`。
    ///
    /// The Blueprint builds the candidate set from enum values 1..7, removes the
    /// target's existing combat keywords, and removes Smokescreen for frontline
    /// cards, Fighters, or cards that already have Guard.  The success path in the
    /// shipped bytecode never writes either out variable, so the observable out
    /// values remain `0/false` even though the selected keyword is granted.
    /// </summary>
    private object? DoGiveRandomCombatKeyword(EffectContext c, object? receiver, object?[] args)
    {
        var target = AsCardOrId(c, args.ElementAtOrDefault(0));
        if (target is null || !CanCardBeBuffed(target))
        {
            return new object?[] { 0, false };
        }

        bool excludeSmokescreen = target.Location == CardLocation.BoardFrontline
            || string.Equals(target.Definition.Type, "fighter", StringComparison.OrdinalIgnoreCase)
            || target.Keywords.Contains(Keyword.Guard);
        var candidates = CombatKeywordEntries
            .Where(entry => !target.Keywords.Contains(entry.Keyword)
                && (entry.Keyword != Keyword.Smokescreen || !excludeSmokescreen))
            .Select(entry => entry.Value)
            .ToList();
        if (candidates.Count == 0)
        {
            return new object?[] { 0, false };
        }

        int index = c.State.Random.RandRange(0, candidates.Count - 1);
        int selected = candidates[index];
        c.State.TraceRandom($"GiveRandomCombatKeyword({target.CardId}) -> {selected}");
        int instigatorId = IntArg(args, 1, c.Self?.CardId ?? 0);
        switch (selected)
        {
            case 1: GiveKeyword(target, Keyword.Ambush); break;
            case 2: GiveKeyword(target, Keyword.Blitz); break;
            case 3: GiveKeyword(target, Keyword.Fury); break;
            case 4: GiveKeyword(target, Keyword.Guard); break;
            case 5:
                DoChangeHeavyArmor(c, receiver,
                    new object?[] { target, instigatorId, 1, 1, false, null });
                break;
            case 6: GiveKeyword(target, Keyword.Shock); break;
            case 7: GiveKeyword(target, Keyword.Smokescreen); break;
        }

        return new object?[] { 0, false };
    }

    /// <summary>
    /// 解析「**目标卡**」实参 —— **先看实参、再看 c.Target、最后才看接收者**，
    /// 而且**整数 cardID 也认**。
    ///
    /// ## ⚠️⚠️ 顺序是关键（这是个大 bug）
    ///
    /// `PinUnit` / `GiveBlitz` / `GiveSmokescreen` / `GetAdjacentCards` 这一族的
    /// 目标在 `args[0]`，而**接收者 `r` 恒为 `cardFunction`（= 施法的那张牌自己）**。
    ///
    /// 旧实现写成 `AsCard(r) ?? AsCard(a[0]) ?? c.Target` ——
    /// `AsCard(r)` **永远命中且非 null** ⇒ **效果一律落到施法的那张牌上，目标从没被命中过**。
    ///
    /// 实测（对局 781364 / `card_event_monty`，卡面
    /// 「Pin target unit **and adjacent units**」）：**一个单位都没钉住** ——
    /// 用户看到"被钉住的单位还能移动"。自测 `PinTargetsCorrectUnits` 复现。
    ///
    /// 全卡池扫描（实参是整数 cardID 的调用点）：
    /// `PinUnit` **69 处**、`GiveBlitz` **37 处**、`GiveShock` 12、`GiveGuard` 4 …
    /// ⇒ 影响面远大于一张卡。
    ///
    /// 对照：<see cref="SelfArg"/> 是「先接收者、后实参」—— 那一族的语义确实是"自己"，
    /// 顺序相反是对的。**"目标"和"自己"两类原语不能用同一套解析顺序。**
    /// </summary>
    private static CardInstance? TargetArg(EffectContext c, object? receiver, object?[] args)
    {
        if (args.Length > 0 && AsCardOrId(c, args[0]) is { } fromArgs)
        {
            return fromArgs;
        }

        return c.Target ?? AsCard(receiver) ?? c.Self;
    }

    private object? DoGiveKeyword(EffectContext c, object? r, object?[] a, string keyword)
    {
        var target = TargetArg(c, r, a);
        if (target is not null)
        {
            if (keyword == Keyword.Alpine)
            {
                // `GiveAlpine(card, out qqq, giverID)` keeps the giver in the
                // target's received-ability list.  The compact runtime stores
                // that list in card-private JSON so RemoveAlpine can remove one
                // source without stripping another source or an innate Alpine.
                int giverId = IntArg(a, 2);
                if (giverId > 0)
                {
                    const string key = "alpineGivers";
                    var givers = JsonGetIntArray(target, key);
                    if (!givers.Contains(giverId))
                    {
                        givers.Add(giverId);
                        JsonSetIntArray(target, key, givers);
                    }
                }
            }
            else if (keyword == Keyword.Bond)
            {
                c.Engine.Api.AddCustomGameplayTag(target, "ability.bond");
                if (string.Equals(target.CustomAbility, "bond_removed", StringComparison.Ordinal))
                {
                    target.CustomAbility = null;
                }
            }

            GiveKeyword(target, keyword);
        }

        return null;
    }

    private object? DoRemoveAlpine(EffectContext c, object? r, object?[] a)
    {
        var target = TargetArg(c, r, a);
        if (target is null)
        {
            return 0;
        }

        const string key = "alpineGivers";
        var givers = JsonGetIntArray(target, key);
        int giverId = IntArg(a, 3);

        if (giverId > 0)
        {
            givers.Remove(giverId);
        }
        else
        {
            givers.Clear();
        }

        JsonSetIntArray(target, key, givers);

        // Keep card-defined Alpine and any remaining dynamic source. Only the
        // final source removal should emit the normal keyword-change event.
        bool innate = target.Definition.Keywords.Contains(Keyword.Alpine);
        if (!innate && givers.Count == 0)
        {
            RemoveKeyword(target, Keyword.Alpine);
        }

        return 0;
    }

    private object? DoRemoveSalvage(EffectContext c, object? r, object?[] a)
    {
        DoRemoveKeyword(c, r, a, Keyword.Salvage);
        return 0;
    }

    private object? DoRemoveBond(EffectContext c, object? r, object?[] a)
    {
        var target = TargetArg(c, r, a);
        if (target is not null)
        {
            CustomAbilityAdd(target, "bond_removed", c.Self);
            c.Engine.Api.RemoveCustomGameplayTag(target, "ability.bond");
        }

        return 0;
    }

    private object? DoRemoveKeyword(EffectContext c, object? r, object?[] a, string keyword)
    {
        var target = TargetArg(c, r, a);
        if (target is not null)
        {
            RemoveKeyword(target, keyword);
        }

        return null;
    }

    /// <summary>
    /// `PinUnit` —— 与 <see cref="DoGiveKeyword"/> 同一套目标解析（`TargetArg`），
    /// 但落到 <see cref="CardApi.PinUnit"/> 上，因为钉住还要记 `pinnedTurns`
    /// （出处 `BP_CardFunctions::PinUnit` i=955，见那个方法的注释）。
    /// </summary>
    private object? DoPinUnit(EffectContext c, object? r, object?[] a)
    {
        var target = TargetArg(c, r, a);
        if (target is not null)
        {
            PinUnit(target);
        }

        return null;
    }

    private object? DoChangedPinnedTurns(EffectContext c, object?[] a)
    {
        if (AsCardOrId(c, a.ElementAtOrDefault(0)) is { } target)
        {
            ChangePinnedTurns(target, IntArg(a, 2), IntArg(a, 1));
        }

        return null;
    }

    // ==================== 参数提取小工具 ====================

    /// <summary>
    /// 取出「被作用的那张卡」。
    ///
    /// ⚠️ **顺序至关重要：先看参数，再看接收者。**
    ///
    /// 这类原语（`DamageCard` / `ChangeAttack` / `DestroyCard` …）在字节码里的形状是
    /// <c>Context{cardFunction}.DamageCard(目标, 数值, 来源, …)</c> —— 接收者是
    /// `cardFunction`（**施法者自己**），而**真正的目标是 `Parameters[0]`**。
    /// 实测（card_unit_10_5_cm_lefh）：
    /// <code>
    /// DamageCard(
    ///   [0] LocalVariable CallFunc_GetLocationCardBySide_card  ← 目标（敌方 HQ）
    ///   [1] IntConst 2                                          ← 伤害
    ///   [2] InstanceVariable cardID                             ← 来源，不是目标
    ///   …
    /// )
    /// </code>
    ///
    /// 旧实现写成 <c>AsCard(receiver) ?? args…</c>，于是**每个显式指定目标的原语
    /// 都会打到施法者自己身上** —— 表现为 10.5cm lefh 的「对敌方 HQ 造成 2 点伤害」
    /// 打掉了自己 2 点防御（3→1），而敌方 HQ 纹丝不动。
    ///
    /// 注意 `Parameters[0]` 也可能是 `InstanceVariable cardID`（自我引用），
    /// 那种情况下 <see cref="AsCard"/> 取不到卡，自然回退到接收者 —— 仍然正确。
    ///
    /// ## ★★ 2026-10-02：`Parameters[0]` 的**整数 cardID 形状**必须认（对局 `773639` #45 的根因）
    ///
    /// 上面那句"取不到卡就回退到接收者"**在目标是别人时不成立**。实测
    /// `card_event_fog_of_war`（IR i=10）：
    /// <code>
    /// RemoveCardFromBoard(cardID@K2Node_Event_targetCard, cardID@self, out qqq)
    /// </code>
    /// 两个实参**都是 int**（`cardID` 是 `BaseCardObject.h` 上的 int 成员），
    /// 而接收者是 `cardFunction` = **施法者自己**（见 `KismetVm.Frame` 的
    /// `_locals["cardFunction"] = ctx.Self`）。于是旧实现返回的是**雾战这张指令自己**，
    /// `RemoveCardFromBoard` 把"弃牌堆里的自己"再 Move 到弃牌堆 —— **静默空转**：
    /// 不报错、不记缺口、目标纹丝不动。
    ///
    /// 后果链（`out/_server-replays/replay-773639`）：
    /// <code>
    /// #25 t7 R ML {"0":"60"}   ; bot 把 card_unit_1st_airborne#60 推上前线 ⇒ 归属=Right
    /// #29 t7 L PC {"0":"19","2":"60"}  ; 人类打 19=card_event_fog_of_war，目标 #60
    ///                          ; 客户端：移除成功 ⇒ 前线空 ⇒ 归属释放
    ///                          ; 我们：空转 ⇒ #60 一直挂在前线（审计① 到 #64 t13 才离场）
    /// #45 t9 L ML {"0":"21"}   ; 人类推前线 ⇒ 被互斥门拒（归属仍是 Right）
    /// </code>
    /// 审计 ⑤b 首个人类失败点 = `#45 t9 ML：移动被拒：前线被对面占着（前线归属=Right，本单位=Left）`。
    ///
    /// 权威签名（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:4202`）：
    /// `["RemoveCardFromBoard"] = new[] { "cardID", "discarderID", "qqq" }` ——
    /// 第 0 个参数**就是 int 卡 ID**，同族还有 `DestroyCard` / `DamageCard` 等
    /// （全部 10 个 `TargetCard` 调用点的第 0 参都是"目标卡"）。
    ///
    /// 修法：把「参数」这一段拆成两步 —— 先扫**卡对象**（老行为，一个字没改），
    /// 再单独看 `args[0]` 的**整数卡 ID**。只看 `[0]`、不看 `[1..]`，是因为
    /// 那些位置在蓝图的签名里是 `instigatorID` / 数值，扫下去会把"伤害 3 点"
    /// 当成"cardID=3"（`HealCard(0, 3)` 这种非指向性调用会凭空挑中 3 号卡）。
    /// 指向**自己**的卡 ID 也跳过 —— 那一支本来就该回退到接收者（老行为）。
    /// </summary>
    private static CardInstance? TargetCard(EffectContext c, object? receiver, object?[] args)
    {
        foreach (var v in args)
        {
            if (AsCard(v) is { } explicitTarget)
            {
                return explicitTarget;
            }
        }

        // 参数位 [0] 是**整数卡 ID**（`RemoveCardFromBoard(cardID, …)` 这一族）。
        // ⚠️ 只认真正的 `int`，不走 `AsInt` —— `AsInt(true) == 1` 会挑中 1 号卡。
        if (args.Length > 0 && args[0] is int targetId && targetId != 0
            && c.State.ById(targetId) is { } byId
            && !ReferenceEquals(byId, c.Self))
        {
            return byId;
        }

        return AsCard(receiver) ?? c.Target ?? c.Self;
    }

    internal static CardInstance? AsCard(object? v) => v as CardInstance;

    /// <summary>
    /// 卡牌私有 JSON 里有没有这个键 —— `JSON_Get*` 的第 2 个输出槽（`found`）。
    /// 客户端判的是"键在不在"，不是"值是不是真"：写进去 `"0"` 之后 `found` 仍然是真。
    /// </summary>
    private static bool JsonHasKey(CardInstance card, string key)
        => key.Length > 0 && card.CustomJson.ContainsKey(key);

    /// <summary>
    /// 「这次调用作用在谁身上」—— 给**隐式 self** 的调用用。
    ///
    /// 判据顺序（每一条都有实测依据）：
    /// 1. <paramref name="receiver"/> —— 显式 `Context(card)` 的接收者（IR 的 `recv`）
    /// 2. <paramref name="args"/> 里第一个卡 —— 有些调用把目标写在参数里
    /// 3. <c>ctx.Self</c> —— **隐式 self 就是「正在跑这张卡的程序的那张卡」**
    /// 4. <c>ctx.Target</c> —— 最后才退回「触发这件事的那张卡」
    ///
    /// ⚠️ 为什么 3 必须排在 4 前面：Blueprint 里 `self.IsLocatedOnBoard()` 编译出来
    /// **没有 `Context` 包装**（实测 `card_unit_214th_amur` i=848、
    /// `card_unit_85_pioneer_company` i=477/701 都是 `{"Inst":"FinalFunction"}`），
    /// 所以 IR 里压根没有 `recv`。这里的 `self` 是**蓝图自己所属的对象**，
    /// 也就是 `ctx.Self`，而不是事件参数。
    /// 把 Target 排在前面会踩一个非常隐蔽的坑：`card_unit_85_pioneer_company` 的
    /// `OnOtherCardPlayedFromHand` 里判 `self.IsLocatedOnBoard()`（问的是**光环自己**
    /// 还在不在场），如果读成「刚打出的那张牌」，指令一进弃牌堆就判假、整条
    /// 还原分支被静默跳过 —— 表现成"打出一张指令后手牌费用不还原"。
    /// </summary>
    internal static CardInstance? SelfArg(EffectContext c, object? receiver, object?[] args)
    {
        if (AsCard(receiver) is { } explicitReceiver)
        {
            return explicitReceiver;
        }

        foreach (object? v in args)
        {
            if (AsCard(v) is { } fromArgs)
            {
                return fromArgs;
            }
        }

        return c.Self ?? c.Target;
    }

    private static object?[] GetEdgeCardInHand(
        EffectContext c, object? receiver, object?[] args, bool rightMost)
    {
        var self = SelfArg(c, receiver, args);
        if (self is null)
        {
            return new object?[] { false, null };
        }

        var hand = c.State.Hand(self.Owner);
        var card = rightMost ? hand.LastOrDefault() : hand.FirstOrDefault();
        return new object?[] { card is not null, card };
    }

    internal static List<CardInstance> AsList(object? v) => v as List<CardInstance> ?? new List<CardInstance>();

    /// <summary>
    /// 把「数组元素」实参解析成卡 —— **可能是卡对象，也可能是整数 cardID**。
    ///
    /// ⚠️⚠️ **两种形状实测都存在**，而旧实现（`AsCard(a[^1])`）只认卡对象：
    /// <list type="bullet">
    /// <item>`card_event_pams.GetChooseSpawnCards`：<c>Array_Add(PossibleCards, localvariable Item)</c>
    ///   → **卡对象**（所以那条路一直是对的，掩盖了这个 bug）。</item>
    /// <item>`card_event_forward_observers`：<c>Array_Add(unitsToDamage, {var:cardID, ctx:unit})</c>
    ///   → **整数 cardID**（`GetMember(unit,"cardID")` 返回 int）。</item>
    /// </list>
    ///
    /// 只认卡对象 ⇒ 整数 ID 一律被静默丢掉 ⇒ 目标数组**恒为空**。
    ///
    /// **实测后果**（雪雾 2026-10-01）：`card_event_forward_observers`
    /// （卡面 Deal 2 damage to all enemy units）**一点伤害都不打** ——
    /// 客户端那边被打死的单位，我们这边还活着 ⇒ **AI 去移动那些"已经死了"的单位**。
    ///
    /// 全卡池扫描：**94 张卡**用整数 ID 形状攒数组
    /// （`anzac_spirit` / `firestorm_skirm` / `carpet_bombing` / `shelling` /
    ///  `blockade` / `red_skies_skirm` / `the_end_is_near_skirm` …）——
    /// 也就是**所有"对敌方全体造成伤害"的卡**。
    /// </summary>
    /// <summary>
    /// 往蓝图数组里追加一个元素，**按目标数组的元素类型归一**：
    /// <list type="bullet">
    /// <item><c>List&lt;int&gt;</c>（<c>TArray&lt;int&gt;</c>，例
    ///       <c>card_event_semper_fi</c> 的 <c>cardsToRandom = GetDeckByside(side)</c>）
    ///       —— 存**整数**，不能解析成卡实例，否则后续
    ///       <c>Array_Get</c> / <c>RandomIntFromRangeWithStream</c> 的元素类型对不上。</item>
    /// <item><c>List&lt;CardInstance&gt;</c>（<c>TArray&lt;UObject*&gt;</c>，例
    ///       <c>PossibleCards</c> / <c>cardsToDamage</c>，由
    ///       <c>KismetVm.SeedArrayTarget</c> 播种）—— 走 <see cref="AsCardOrId"/>，
    ///       整数卡 ID 也要能塞进去（全卡池 94 张卡用整数 ID 形状攒数组，
    ///       见 <see cref="AsCardOrId"/> 的注释）。</item>
    /// </list>
    /// 混用会让 <c>List&lt;CardInstance&gt;.Add(装箱 int)</c> 直接抛
    /// <c>InvalidCastException</c>，所以两种目标必须分开处理。
    /// </summary>
    private static void ArrayAppendOne(EffectContext c, System.Collections.IList arr, object? v)
    {
        if (arr is List<int> ids)
        {
            ids.Add(v is CardInstance ci ? ci.CardId : AsInt(v));
        }
        else if (arr is List<CardInstance> cards && AsCardOrId(c, v) is { } item)
        {
            cards.Add(item);
        }
    }

    /// <summary>
    /// 蓝图数组元素的**值比较**（`Array_Contains` / `Array_Remove` / `Array_RemoveItem` 用）。
    ///
    /// 两种元素表示都要认，而且可以**混在同一个数组里**：
    /// <list type="bullet">
    /// <item>卡实例（<c>TArray&lt;UObject*&gt;</c>，例 `GetCardsOnBoardBySide` 的 `Cards`）</item>
    /// <item>整数卡 ID（<c>TArray&lt;int&gt;</c>，例 `GetDeckByside` 的 `deckCardIDs`；
    ///       见 <see cref="CardApi.GetDeckBySide"/> 的注释）</item>
    /// </list>
    /// 全卡池有 94 张卡用**整数 ID 形状**攒数组（见 <see cref="AsCardOrId"/> 的注释），
    /// 所以"拿整数 ID 去查一个装着实例的数组"也必须命中 —— 这一条在
    /// `AsCardOrId` 那一族里已经是既成事实。
    ///
    /// 旧写法是 `ReferenceEquals(x, y) || x.CardId == y.CardId`，
    /// 只对"两边都是卡实例"成立；ID 数组上 `AsInt(卡实例)=0` ⇒ 恒假。
    /// </summary>
    private static bool SameArrayValue(object? x, object? y)
        => ReferenceEquals(x, y) || IdOf(x) == IdOf(y);

    /// <summary>数组元素的「整数身份」：卡实例取它的卡 ID，别的走 <see cref="AsInt"/>。</summary>
    private static int IdOf(object? v) => v is CardInstance c ? c.CardId : AsInt(v);

    private static CardInstance? AsCardOrId(EffectContext c, object? v)
    {
        if (AsCard(v) is { } card)
        {
            return card;
        }

        int id = AsInt(v);
        return id != 0 ? c.State.ById(id) : null;
    }

    internal static int AsInt(object? v) => v switch
    {
        int i => i,
        bool b => b ? 1 : 0,
        string s when int.TryParse(s, out int r) => r,
        _ => 0,
    };

    internal static string? AsString(object? v) => v as string;

    private static string? GameplayTagArg(object? value)
    {
        if (value is string tag)
        {
            return tag;
        }

        if (value is System.Collections.IEnumerable values)
        {
            foreach (object? item in values)
            {
                if (item is string nestedTag)
                {
                    return nestedTag;
                }
            }
        }

        return null;
    }

    internal static int IntArg(object?[] a, int i, int fallback = 0)
        => i < a.Length ? AsInt(a[i]) : fallback;

    internal static string StrArg(object?[] a, int i)
        => i < a.Length ? (a[i] as string ?? "") : "";

    internal static string? StrArgOrNull(object?[] a, int i)
        => i < a.Length ? a[i] as string : null;

    internal static bool TruthyArg(object?[] a, int i)
        => i < a.Length && KismetVmTruthy(a[i]);

    private static bool KismetVmTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>
    /// 取阵营参数。
    /// 参数可能是 <c>side</c> 这个 int（ESideEnum 1/2），也可能是接收者本身代表的阵营；
    /// 都拿不到时退回效果控制方 —— 这比返回 0 安全（0 = NotAvailable 会让效果静默失效）。
    /// </summary>
    internal static Side SideArg(object? receiver, object?[] args, int index, Side? fallback = null)
    {
        if (index < args.Length)
        {
            var v = args[index];
            if (v is int i && i is 1 or 2)
            {
                return (Side)i;
            }

            if (v is Side s && s != Side.NotAvailable)
            {
                return s;
            }
        }

        if (receiver is CardInstance card)
        {
            return card.Owner;
        }

        return fallback ?? Side.NotAvailable;
    }

    /// <summary>
    /// 取「阵营实参」，**只认真正的阵营值**（<c>int</c> 1/2 或 <see cref="Side"/>），
    /// 读不到就返回 <c>null</c> —— 与 <see cref="SideArg"/> 的唯一区别是**不做兜底**。
    ///
    /// 为什么需要它：<c>IsSameSideUnit</c> 的判据是「**这张卡**属于**这个阵营**吗」。
    /// 一旦退回 <see cref="SideArg"/> 的兜底（<c>receiver.Owner</c>），
    /// 就退化成「卡属于它自己的阵营」⇒ **判据恒真**，比返回 false 更危险
    /// （会把"必须是友方单位"的门全部放行）。所以这类"查 A 是否等于 B"的原语
    /// 一律用这个不兜底的版本。
    /// </summary>
    internal static Side? SideArgOrNull(object?[] args, int index)
    {
        if (index >= args.Length)
        {
            return null;
        }

        return args[index] switch
        {
            int i when i is 1 or 2 => (Side)i,
            Side s when s != Side.NotAvailable => s,
            _ => null,
        };
    }

    /// <summary>
    /// 「我方」阵营 —— 给那些**没有入参、隐含以卡自己为上下文**的原语用
    /// （目前已知只有 <c>GetOppositeSide</c>）。
    /// 优先取卡自己的 owner，退回效果控制方。
    /// </summary>
    internal static Side SelfSide(EffectContext c)
    {
        if (c.Self is { } self && self.Owner != Side.NotAvailable)
        {
            return self.Owner;
        }

        return c.Controller;
    }

    // ==================================================================
    //  ★★ 目标合法性门（2026-10-02）
    //
    //  背景：真人玩家报告「有一些**有指定向指令或部署效果**的单位或指令
    //  （例：使一个敌方/友方**空军**撤退；或只能指定**老兵**单位），
    //  模拟器里好像没有限制条件，人机可以随意指定」。
    //
    //  客户端**有两道**目标门，缺一不可（枚举主循环 `_deps/BP_Logic.g.cs:1235-1355`）：
    //  <code>
    //  遍历 GetAllCardInBattle:
    //      _card.targetOverride = 候选卡
    //      _card.CanPlayFromHand(out canIt, …, out targetedCard)   ← ① 卡自己的判据
    //      if (!canIt || !IsValid(targetedCard)) continue
    //      CanSelectAsTarget(候选卡, _card, byPlayFromHand: True, …) ← ② 规则库的门
    //      if (can) → 这个候选合法
    //  </code>
    //
    //  ① 才是「只能指定空军 / 老兵 / 敌方」那一道（438 张卡的 `CanPlayFromHand` 里
    //     写着 `IsAirUnit` 13 / `IsVeteran` 2 / `IsGroundUnit` 17 / `IsSameSideUnit` 131 …）；
    //  ② 只管隐蔽 / 敌方指令 / 费用 / 被指方自身 / 触发点 2 否决位。
    //  ⇒ **两道都实现、都过，才算把客户端的门补齐**。
    // ==================================================================

    /// <summary>
    /// 一次目标判定的结果。`Reason` 沿用**蓝图自己的失败原因字符串**
    /// （`"air_unit"` / `"veteran_unit"` / `"enemy_air_or_infantry_unit"` /
    /// `"cant_be_targeted_by_enemy_orders"` / `"cost_extra_to_target"` …），
    /// 不是我们发明的词 —— 审计与日志里能直接和客户端文案对上。
    /// </summary>
    public readonly record struct TargetCheck(bool Can, string Reason, string ReasonParam1, string ReasonParam2)
    {
        /// <summary>放行。</summary>
        public static TargetCheck Ok { get; } = new(true, "", "", "");

        /// <summary>失败原因（带参数时拼上），用于日志/自测断言。</summary>
        public string Describe()
            => Can ? "ok" : (ReasonParam1.Length > 0 ? $"{Reason}({ReasonParam1})" : Reason);

        public override string ToString() => Describe();
    }

    /// <summary>
    /// ★★ `cardsCheckFunctions.CanSelectAsTarget` —— 规则库的**目标合法性门**，
    /// 逐句移植 `ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs:1052-1219`。
    ///
    /// ## 签名与语义（8 个形参，权威参数表见 `Generated/_index.g.cs:1850`）
    /// <code>
    /// CanSelectAsTarget(Targeted, Targeting, byPlayFromHand, __WorldContext,
    ///                   out can, out Reason, out ReasonParam1, out ReasonParam2)
    /// </code>
    /// · `Targeted` —— **候选目标那张卡**（被指定的）
    /// · `Targeting` —— **正在指定别人的那张卡**（施法方 / 攻击方）
    /// · `byPlayFromHand` —— 这次判定是不是「从手牌打出」触发的。
    ///   客户端选目标走 `BP_Logic.g.cs:1312`（传 **True**）；攻击走
    ///   `CanAttack` 的 `g.cs:902`（传 **False**）。
    /// · 四个出参只有 `can` / `Reason` 有用（后两个是给 UI 文案做参数替换的）
    /// · **无副作用**（纯判定）
    ///
    /// ## 判据（按 `g.cs` 语句顺序）
    /// <list type="number">
    /// <item>`IsValid(Targeted)` 假 → 早退（`can` 保持 false）</item>
    /// <item>`IsLocatedOnBoard(Targeted)` 假 → 早退。
    ///   ⚠️ 这里必须是**蓝图语义**（`Loc is Board or Frontline`，**含 HQ**），
    ///   不是内核 `CardApi.IsLocatedOnBoard`（那个排除 HQ，见 `MakeCardsFight` 的注释）。
    ///   用错会把「指定 HQ」全部拒掉（`card_event_the_commonwealth` 这类牌直接废掉）。</item>
    /// <item>未揭示隐蔽卡 + 从手牌打出 + 施法方没有 `canTargetCovert`
    ///   → 拒，`cant_target_unrevealed`。（按 `IsUnrevealedCovertCard` 的当前状态判定）</item>
    /// <item>目标是敌方指令的禁指对象（`cantBeTargetedByEnemyOrder`）→ 拒，
    ///   `cant_be_targeted_by_enemy_orders`。（卡池里 5 张，与卡面串名一致）</item>
    /// <item>费用：`kredit(Targeting.side) - (byPlayFromHand ? 卡费 : 行动费) &lt; 0` → 拒，
    ///   `play_from_hand_not_enough_kredits_to_target` / `not_enough_kredits_to_target`。
    ///   `SelectInt(A, B, cond)` = **cond ? A : B**（取证：`card_event_the_commonwealth` 的
    ///   `SelectInt(20, 0, HQ防御>=30)`、`card_unit_type_97_cam1` 的 `SelectInt(2, 1, 有战役升级)`）。</item>
    /// <item>再加「被敌方指定的额外税」`KreditsTax_AsEnemyTarget`（**同阵营不付税**）
    ///   → 不够则 `cost_extra_to_target`。</item>
    /// <item>`Targeted.isSuppressed || CanBeTargetted(Targeted, Targeting, byPlayFromHand)`
    ///   为假 → 拒（原因取 `CanBeTargetted` 的）。</item>
    /// <item>`CanOtherCardBeTargetted(...)`（触发点 2 的否决位）为假 → 拒。</item>
    /// </list>
    ///
    /// ## ⚠️⚠️ 它**不判**「目标类型」（空军 / 老兵 / 地面 / 敌我）
    ///
    /// 那一道在**每张卡自己的 `CanPlayFromHand`** 里 —— 见 <see cref="CanPlayFromHandOn"/>。
    /// 玩家报告的「只能指定空军 / 只能指定老兵」不在这里。把两道门混成一道，
    /// 就会写出一个"看起来实现了、其实一张空军牌都管不住"的门。
    /// </summary>
    /// <param name="targeting">正在指定别人的卡（施法/攻击方）。</param>
    /// <param name="targeted">候选目标卡。</param>
    /// <param name="byPlayFromHand">是否「从手牌打出」这条路径（选目标恒为 true）。</param>
    public TargetCheck CanSelectAsTarget(CardInstance? targeting, CardInstance? targeted, bool byPlayFromHand)
    {
        // ① IsValid(Targeted)
        if (targeting is null || targeted is null)
        {
            return new TargetCheck(false, "invalid", "", "");
        }

        // ② IsLocatedOnBoard(Targeted) —— 蓝图语义（含 HQ）
        if (!targeted.Location.IsBoard())
        {
            return new TargetCheck(false, "not_on_board", "", "");
        }

        // ③ 未揭示的隐蔽卡
        if (IsUnrevealedCovertCard(targeted) && byPlayFromHand
            && !CustomNameHasAttribute(targeting, "customName1", "canTargetCovert"))
        {
            return new TargetCheck(false, "cant_target_unrevealed", "", "");
        }

        // ④ 敌方指令不能指定它
        if (CustomNameHasAttribute(targeted, "customName1", "cantBeTargetedByEnemyOrder")
            && targeted.Owner != targeting.Owner
            && IsOrder(targeting))
        {
            return new TargetCheck(false, "cant_be_targeted_by_enemy_orders", "", "");
        }

        // ⑤ 费用够不够（`SelectInt(卡费, 行动费, byPlayFromHand)` = byPlayFromHand ? 卡费 : 行动费）
        int cost = byPlayFromHand ? targeting.KreditCost : targeting.OperationCost;
        int remaining = State.Kredits(targeting.Owner) - cost;
        if (remaining < 0)
        {
            return new TargetCheck(false,
                byPlayFromHand ? "play_from_hand_not_enough_kredits_to_target" : "not_enough_kredits_to_target",
                "", "");
        }

        // ⑥ 额外税（`SelectInt(0, 税, 同阵营)` = 同阵营 ? 0 : 税）
        int tax = targeting.Owner == targeted.Owner ? 0 : targeted.KreditsTaxAsEnemyTarget;
        if (remaining < tax)
        {
            return new TargetCheck(false, "cost_extra_to_target", tax.ToString(), "");
        }

        // ⑦ 被指方自身（被压制的卡直接放行 —— 蓝图是 `isSuppressed || canIt`）
        if (!targeted.IsSuppressed)
        {
            var canBe = CanBeTargetted(targeted, targeting, byPlayFromHand);
            if (!canBe.Can)
            {
                return canBe;
            }
        }

        // ⑧ 触发点 2 的否决位
        return CanOtherCardBeTargetted(targeting, targeted, byPlayFromHand);
    }

    /// <summary>
    /// `UBaseCardObject::CanBeTargetted(out canIt, out Reason, out p1, out p2, targettingCard, byPlayFromHand)`
    /// —— **被指方自身**的「我能不能被指定」判定。
    ///
    /// ⚠️ **诚实标注：这是推断，不是移植。** 它是 `BlueprintNativeEvent`，
    /// 真实函数体编译在游戏二进制里；随附源码里 `CanBeTargetted_Implementation`
    /// 是空体。参考实现（`ref/kards-sim/KardsSim/Bridge/EngineHost.cs:1125-1172`）
    /// 也明确写了同一句话，并给出结论：**默认放行，只拦"数据里有依据"的限制**，
    /// 而唯一有依据的那条就是 `cantBeTargetedByEnemyOrder`。
    ///
    /// 那条在 <see cref="CanSelectAsTarget"/> 的 ④ 里**已经判过**（蓝图自己也在
    /// 两处重复判了同一件事）。所以这里返回放行 —— 不是"没实现"，而是
    /// 「已实现的部分与 ④ 同源，剩下的部分连客户端都只存在于二进制里」。
    /// 将来若在数据里发现新的限制依据，落点就是这里。
    /// </summary>
    private static TargetCheck CanBeTargetted(CardInstance targeted, CardInstance targeting, bool byPlayFromHand)
    {
        _ = targeted;
        _ = targeting;
        _ = byPlayFromHand;
        return TargetCheck.Ok;
    }

    /// <summary>
    /// `cardsCheckFunctions.CanOtherCardBeTargetted`（库版，`g.cs:963-1048`）——
    /// 遍历 `FetchAllCardsWithEventTrigger(GameState, 2)`，对每张注册卡调**卡版**同名函数，
    /// **任何一张回 false 就整体否决**。
    ///
    /// 触发点 2 = `Trigger.CanOtherCardBeTargetted`
    ///（`ref/kards-sim/KardsSim/Core/Trigger.g.cs:12`）。
    ///
    /// ## 订阅表：数据驱动，不写死卡名清单
    ///
    /// 蓝图那一步是「问客户端：谁注册了触发点 2」。内核里与它**同源**的数据是
    /// `card-effects.json` 的 `functions`（反编译出的**每卡注册函数表**，装载进
    /// <see cref="CardDefinition.FunctionCalls"/>），判据就是
    /// `FunctionCalls.ContainsKey("CanOtherCardBeTargetted")` —— 不需要另造一张表，
    /// 也就不会与卡池数据漂移。全卡池实测**恰好 1 张**注册它：
    /// `card_unit_no_3_commando`（扫 `docs/card-effects.json` 的 2730 个函数名，
    /// 只有它带这一项）。
    ///
    /// ## 判据来源：**先跑卡自己的函数体**，拿不到才用转写体
    ///
    /// 蓝图那版是「对每张注册卡调**它自己的** `CanOtherCardBeTargetted`」，所以这里
    /// 第一选择就是**执行那张卡的函数体**（IR 的 `locals`，与
    /// <see cref="CanPlayFromHandOn"/> / `GetChooseSpawnCards` 同一条路：
    /// `RunOwnLocal` + `KismetLibrary.FindLocalProgram`）。
    /// 入参按蓝图卡版的形参名播种
    ///（`card_unit_no_3_commando.g.cs:37-39`：`targettingCard` / `targetCard` / `byPlayFromHand`），
    /// 出参取 `canIt` / `reason` / `reasonParam1` / `reasonParam2`。
    ///
    /// ⚠️ **但唯一实现者的函数体现在不在 IR 里**，所以会落到下面的转写体：
    /// <list type="number">
    /// <item>`card_unit_no_3_commando` **不在 `card-ir.json` 里**。IR 只收 1735 张，
    ///   它是「卡池有（2021 张）、IR 没有」的那批之一 —— 它在蓝图里
    ///   **只有 `CanOtherCardBeTargetted` 一个函数、没有任何事件入口**，而
    ///   `tools/gen-kismet-ir.py:538` 对「收集不到入口点」的卡直接 `continue`。</item>
    /// <item>修法是**加法**、不动本文件：把 `CanOtherCardBeTargetted` 加进
    ///   `tools/gen-kismet-ir.py` 的 `LOCAL_FUNCTIONS`（照 `CanPlayFromHand` 那条的
    ///   注释风格），然后
    ///   <code>
    ///   python "klink bot\tools\gen-kismet-ir.py" "decompiled\cards.full.json" "klink bot\docs\card-ir.json"
    ///   </code>
    ///   （`decompiled/cards.full.json` 在本机存在，86.8 MB；**两份 `card-ir.json`
    ///   副本要一起同步** —— 工作副本 csproj 读的是 `klink bot/klink bot/docs/` 那份，
    ///   上游镜像 csproj 读的是 `klink bot/docs/` 那份）。
    ///   上一轮的独立复核量过这一步是**外科手术式**的：1735 → 1736 张，新增的只有
    ///   这张卡、**0 张已有卡内容变化**、新旧 IR 的调用名集合完全相同
    ///   ⇒ 派发表缺口指纹不变、`DispatchGapGuard` 仍绿；新卡的
    ///   `locals["CanOtherCardBeTargetted"]` 是 18 步。
    ///   ⚠️ **本轮改动没有复现这一步**（数据只在复核里量过，见上）。
    ///   **一旦做了，上面的通用路径自动接管、下面的转写体自然休眠 —— 但不要删它**：
    ///   它是「IR 被重新生成却没带这个白名单」时的安全网。</item>
    /// </list>
    /// ⇒ 转写体逐句来自
    /// `ref/kards-sim/KardsSim/Generated/Britain/Breakthrough/units/card_unit_no_3_commando.g.cs:40-65`：
    /// <code>
    /// :41  Not_PreBool(byPlayFromHand)
    /// :43  getTotalAttack(targettingCard)          ← 攻方**总**攻（含 buff）
    /// :45  IsLocatedOnBoard(self)                  ← self = 订阅者自己
    /// :47  GreaterEqual_IntInt(总攻, 4)
    /// :49  BooleanAND(≥4, Not_PreBool)
    /// :51  BooleanAND(…, IsLocatedOnBoard(self))
    /// :53  三条全真 ⇒ :55 canIt = False / :57 reason = "unit_cant_attack"
    /// </code>
    /// 卡面互证（`docs/cards.live.json`）：「No. 3 COMMANDO」=
    /// <c>Units with 4 or more attack cannot attack.</c>
    ///
    /// ## ⚠️ `self` 是**订阅者**（那张 commando），**不是**被指的目标
    ///
    /// 库版 `g.cs:1003` 的实参形状是
    /// `H.Call("CanOtherCardBeTargetted", [item, out canIt, out reason, out p1, out p2,
    /// cardTargetting, targetCard, byPlayFromHand])` —— 数组第 0 个元素是**接收者**，
    /// 也就是 `FetchAllCardsWithEventTrigger(…, 2)` 取出来的那张卡。所以卡版里的 `self`
    /// 就是订阅者；而 `targetCard`（被指的那张）它**一次都没用到**。
    /// ⇒ 语义是「**我在场** + 这次不是从手牌打出（即攻击路径）+ 攻方总攻 ≥ 4 ⇒ 谁都不能被打」，
    /// 与卡面一致。把 `self` 当成 `targeted`（= 只有当 commando 自己被打时才生效）
    /// 会让「4 攻打**别的**单位」漏过去，那不是蓝图语义。
    ///
    /// ## 三条边界
    /// <list type="bullet">
    /// <item>`byPlayFromHand = true`（**出牌路径**）⇒ 第一条判据 `Not_PreBool` 恒假
    ///   ⇒ 整个 AND 恒假 ⇒ 落 else 分支 `canIt = True`。本方法**直接早退**，
    ///   既是性能也是硬保证：出牌路径（`CanTarget` / `LegalPlayTargets`）行为
    ///   **一个字节都不变**，8 条目标门自测守着这一点。</item>
    /// <item>订阅表里**每一张**都问过，任何一张回 false ⇒ 整体否决
    ///   （库版 `g.cs:1005` 的分支）—— 不是「只看第一张」。</item>
    /// <item>⚠️ 转写体**只对唯一实现者成立**。出现第二个实现者时，若它的函数体也不在
    ///   IR 里，必须在这里补它的判据（或直接把 IR 补上，让通用路径接管）。</item>
    /// </list>
    /// </summary>
    private TargetCheck CanOtherCardBeTargetted(CardInstance targeting, CardInstance targeted,
                                               bool byPlayFromHand)
    {
        // 出牌路径：蓝图 `Not_PreBool(byPlayFromHand)` 恒假 ⇒ 永不否决（见上「三条边界」）。
        if (byPlayFromHand)
        {
            return TargetCheck.Ok;
        }

        // 遍历范围沿用本内核触发派发的口径（`CardApi.FireTrigger` 的快照：双方棋盘 + 弃牌堆）。
        // 「在不在棋盘上」由**卡自己的判据**（`IsLocatedOnBoard(self)`）负责 ——
        // 蓝图也是这么分工的（`FetchAllCardsWithEventTrigger` 只看注册表，不看位置）。
        for (int i = 0; i < 2; i++)
        {
            Side side = i == 0 ? Side.Left : Side.Right;
            foreach (CardInstance sub in State.Board(side).Concat(State.Discard(side)))
            {
                if (sub.Location == CardLocation.NotAvailable
                    || !sub.Definition.FunctionCalls.ContainsKey(Trigger2Function))
                {
                    continue;   // 没注册触发点 2 ⇒ 不归它管
                }

                var r = AskCardCanOtherCardBeTargetted(sub, targeting, targeted, byPlayFromHand);
                if (!r.Can)
                {
                    return r;   // 任何一张回 false ⇒ 整体否决（库版 `g.cs:1005` 的分支）
                }
            }
        }

        return TargetCheck.Ok;
    }

    /// <summary>
    /// 问**订阅表里的一张卡**：`sub` 自己的 `CanOtherCardBeTargetted` 判这次指定合不合法。
    ///
    /// 两条路（顺序即优先级）：
    /// <list type="number">
    /// <item><b>通用路径</b>：跑 `sub` 自己的函数体（IR `locals`）。这条路对
    ///   「将来新增的实现者」自动成立，不需要改本文件。</item>
    /// <item><b>兜底路径</b>：IR 里没有这张卡的函数体时，用手写转写体
    ///   （目前只有唯一实现者 `card_unit_no_3_commando` 需要它）。
    ///   转写体不认识的名字**不否决**（等同空集），但会记一笔
    ///   `UnimplementedCalls` 留痕 —— 不静默。</item>
    /// </list>
    /// </summary>
    private TargetCheck AskCardCanOtherCardBeTargetted(CardInstance sub, CardInstance targeting,
                                                       CardInstance targeted, bool byPlayFromHand)
    {
        // ---- ① 通用路径：这张卡自己的函数体（IR locals）----
        //
        // 入参名逐字用蓝图卡版的形参名（`card_unit_no_3_commando.g.cs:37-39`），
        // 与 `CanPlayFromHandOn` 播种 `toCard` 是同一个约定（`KismetVm.RunLocalProgramMulti`
        // 把 seed 覆盖进帧的默认值）。
        var outs = RunOwnLocal(
            sub, Trigger2Function,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targettingCard"] = targeting,
                ["targetCard"] = targeted,
                ["byPlayFromHand"] = byPlayFromHand,
            },
            "canIt", "reason", "reasonParam1", "reasonParam2");

        if (outs is not null)
        {
            return new TargetCheck(
                Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("canIt")),
                outs.GetValueOrDefault("reason") as string ?? "",
                outs.GetValueOrDefault("reasonParam1") as string ?? "",
                outs.GetValueOrDefault("reasonParam2") as string ?? "");
        }

        // ---- ② 兜底：函数体不在 IR 里 ----
        if (sub.Definition.Name == CommandoCard)
        {
            return CommandoCanOtherCardBeTargetted(sub, targeting, targeted);
        }

        // 有订阅者、但既没有 IR 函数体、也没有转写体 ⇒ 放行（等同空集），
        // 但必须留痕：静默放行正是「门看起来实现了、其实一张都管不住」的形状。
        State.UnimplementedCalls[$"<{Trigger2Function}-body-missing>"] =
            State.UnimplementedCalls.GetValueOrDefault($"<{Trigger2Function}-body-missing>") + 1;
        return TargetCheck.Ok;
    }

    /// <summary>触发点 2 的注册函数名（`ERegisteredCardFunction.h` 第 3 项 = 序号 2）。</summary>
    private const string Trigger2Function = "CanOtherCardBeTargetted";

    /// <summary>
    /// **卡版** `CanOtherCardBeTargetted` 的**兜底转写体** —— 只覆盖全池唯一实现者
    /// `card_unit_no_3_commando`（`ref/kards-sim/.../card_unit_no_3_commando.g.cs:40-65`）。
    ///
    /// 它是 <see cref="AskCardCanOtherCardBeTargetted"/> 的**第二选择**：只有在
    /// 「这张卡注册了触发点 2、但它的函数体不在 IR 里」时才会被调用。
    /// **一旦那张卡进了 `card-ir.json`，通用路径接管，本方法就不再被触发。**
    ///
    /// 为什么判据里显式带卡名（而不是做成对所有卡生效的通用规则）：全池**只有这 1 张**
    /// 注册触发点 2，而它没有 IR、函数体执行不了，所以只能按名字转写；
    /// 卡面文字（`Units with 4 or more attack cannot attack.`）与蓝图逐句互证。
    /// **若出现第二个实现者，必须在这里补它的判据**（或直接把 IR 补上）。
    /// </summary>
    /// <param name="self">订阅者（= 蓝图卡版里的 `self`，`IsLocatedOnBoard(self)` 判的就是它）。</param>
    /// <param name="targeting">发起方（蓝图 `targettingCard`，攻方）。</param>
    /// <param name="targeted">候选目标（蓝图 `targetCard`）—— ⚠️ 转写体**不用**它，见注释。</param>
    private static TargetCheck CommandoCanOtherCardBeTargetted(CardInstance self, CardInstance targeting,
                                                              CardInstance targeted)
    {
        _ = targeted;   // 蓝图的卡版全文没有一处用到 `targetCard`（`g.cs:40-65`）

        if (self.Definition.Name != CommandoCard)
        {
            return TargetCheck.Ok;   // 尚未转写判据的实现者：放行（调用方会记一笔留痕）
        }

        // :47 `getTotalAttack(targettingCard) >= 4`
        //     —— 内核 `getTotalAttack` 的派发就是 `SelfArg(...)?.Attack ?? 0`
        //        （本文件 `:268`），所以 `Attack` 已经是含 buff 的当前总攻。
        // :45 `IsLocatedOnBoard(self)` —— 用**蓝图语义**的 `Location.IsBoard()`
        //     （含 HQ；与 `CanSelectAsTarget` 的 ② 同一口径，见那里的注释）。
        // :41 `Not_PreBool(byPlayFromHand)` —— 上面已经早退，走到这里必然为真。
        if (targeting.Attack >= 4 && self.Location.IsBoard())
        {
            return new TargetCheck(false, "unit_cant_attack", "", "");
        }

        return TargetCheck.Ok;
    }

    /// <summary>全池唯一实现触发点 2 的卡（见 <see cref="CommandoCanOtherCardBeTargetted"/> 的注释）。</summary>
    private const string CommandoCard = "card_unit_no_3_commando";

    /// <summary>
    /// ★★ **卡自己的**目标判据 —— 执行这张卡的 `CanPlayFromHand`（IR 的 `locals`），
    /// 把候选目标当作客户端的 `targetOverride` 传进去。
    ///
    /// ## 为什么必须执行函数体，而不是写一条 C# 规则
    ///
    /// 全卡池 **438 张卡**各自实现了它，判据五花八门（`card-ir.json` 的
    /// `locals.CanPlayFromHand` 全量统计，45 个不同被调函数）：
    /// <code>
    /// card_event_aa_barrage       "Target air unit must retreat"
    ///     → IsAirUnit(目标) ? (HasCustomAbility(目标,"cantRetreat") ? 拒 : 放) : 拒("air_unit")
    /// card_event_breakout         "…"
    ///     → IsVeteran(目标) ? 放 : 拒("veteran_unit")
    /// card_unit_m16_halftrack     "An enemy air or infantry unit must retreat"
    ///     → 敌方 && (IsAirUnit || IsInfantry) ? … : 拒("enemy_air_or_infantry_unit")
    /// </code>
    /// 「一条通用公式」不可能存在 —— 唯一忠实的做法是**跑那张卡自己的函数体**，
    /// 与 `GetPlayFromHandDamage` / `GetChooseSpawnCards` 走的是同一条路
    /// （`KismetVm.RunLocalProgramMulti` + `CardApi.RunOwnLocal`）。
    ///
    /// ## 为什么 IR 里原来没有它
    ///
    /// `klink bot/tools/gen-kismet-ir.py` 的 `LOCAL_FUNCTIONS` 是一个**白名单**，
    /// 以前只有 78 个名字，注释里明写「别的局部函数（`CanPlayFromHand` /
    /// `ShouldHighlightInHand` …）有各自的调用路径，不在这次修复范围内」。
    /// 本次把它加进白名单并重生成 `card-ir.json`
    /// （已验证：`steps` / `entrypoints` **逐字节不变**，只多了 438 个 `locals` 条目）。
    ///
    /// ## 返回值
    ///
    /// 这张卡**没有** `CanPlayFromHand` ⇒ 客户端也没有这道门 ⇒ **放行**，
    /// 而且**不计**未实现（"没有这道门"是事实，不是缺口）。
    ///
    /// ## ⚠️ 为什么"跑一遍函数体"是安全的（不会污染局面）
    ///
    /// 这个方法会在**候选枚举**里被逐张候选调用（`MatchEngine.LegalPlayTargets`），
    /// 而 `NnPolicy.Choose` 是**在实时引擎 `live` 上**枚举的
    ///（`verifyEveryReplay: false`，见 `BotTurnService.cs:528`）——
    /// 万一门有副作用，就会把实时局面改掉而没人发现。
    ///
    /// 所以把 438 张卡的 `CanPlayFromHand` 函数体里**所有被调函数**扫了一遍
    ///（`card-ir.json` 全量统计，**45 种**）：`GetTargetedCard`(431) / `IsUnit`(201) /
    /// `IsSameSideUnit`(132) / `GetOppositeSide`(125) / `HasCampaignUpgrade`(30) /
    /// `IsTank`(29) / `IsInfantry`(29) / `HasCustomAbility`(25) / `Array_Get`(20) /
    /// `getTotalAttack`(20) / `getAndDecryptKredit`(19) … ——
    /// **全部是只读谓词/取值**，没有任何 `Change*` / `Damage*` / `Destroy*` / `Spawn*` /
    /// `Pin*` / `MakeCardRetreat`。唯一一个写操作是 `Array_Clear`（1 处，
    /// 清的是**函数自己的局部数组**，不进引擎状态）。
    ///
    /// 另外 `RunOwnLocal` 自带独立的 `Frame` + `EffectContext`，共享的可变状态只有
    /// VM 的计数器与 `_triggerDepth`。⇒ 这道门是纯函数。
    /// **将来若某张卡的 `CanPlayFromHand` 里出现写操作，这条结论就失效** ——
    /// 那时要么把它做成"在副本上判"，要么把副作用单独摘出来。
    /// </summary>
    public TargetCheck CanPlayFromHandOn(CardInstance card, CardInstance? target)
    {
        var outs = RunOwnLocal(
            card, "CanPlayFromHand",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["toCard"] = target },
            "canIt", "reason", "reasonParam1", "reasonParam2", "targetedCard");

        if (outs is null)
        {
            return TargetCheck.Ok;
        }

        return new TargetCheck(
            Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("canIt")),
            outs.GetValueOrDefault("reason") as string ?? "",
            outs.GetValueOrDefault("reasonParam1") as string ?? "",
            outs.GetValueOrDefault("reasonParam2") as string ?? "");
    }

    /// <summary>
    /// ★★ **两道门一起过** —— 这就是客户端选一个目标时的完整判据
    /// （`BP_Logic.g.cs:1235-1355` 的循环体，顺序也一致）。
    ///
    /// 调用方：`NnPolicy` / `GreedyBot` / `BotTurnService` 的**候选枚举**，
    /// 以及 `ReplayRunner` 的**目标校验**。
    ///
    /// ⚠️ `target` 为 null 时返回**拒绝**（`no_target`）：调用方必须先判
    /// "这张牌到底需不需要目标"（`Definition.ExternalCalls` 含 `GetTargetedCard`），
    /// 不要拿 null 当"无目标合法"。
    /// </summary>
    public TargetCheck CanTarget(CardInstance card, CardInstance? target, bool byPlayFromHand = true)
    {
        if (target is null)
        {
            return new TargetCheck(false, "no_target", "", "");
        }

        // ① 卡自己的 CanPlayFromHand（客户端枚举里先调的就是它）
        var own = CanPlayFromHandOn(card, target);
        if (!own.Can)
        {
            return own;
        }

        // ② 规则库的 CanSelectAsTarget
        return CanSelectAsTarget(card, target, byPlayFromHand);
    }

    /// <summary>派发表适配器：把 IR 的 8 个实参映射到 <see cref="CanSelectAsTarget"/> 的 4 个出参。</summary>
    /// <remarks>
    /// 实参形状（`CanAttack` 的调用点 `_deps/cardsCheckFunctions.g.cs:902`）：
    /// <c>[Targeted, Targeting, byPlayFromHand, __WorldContext, out can, out Reason, out p1, out p2]</c>
    /// —— 接收者是 `Val.Ref("cardsCheckFunctions")`，不在实参里（见 `KismetVm.cs:528` 的约定）。
    /// IR 里这个函数的调用点目前是 **0**（全在规则库里，规则库不在 IR 里），
    /// 注册它是为了让"名字 → 实现"可查，不是为了让缺口数字好看。
    /// </remarks>
    private object? InvokeCanSelectAsTarget(EffectContext c, object?[] a)
    {
        var targeted = AsCardOrId(c, a.ElementAtOrDefault(0));
        var targeting = AsCardOrId(c, a.ElementAtOrDefault(1));
        bool byPlayFromHand = TruthyArg(a, 2);
        var r = CanSelectAsTarget(targeting, targeted, byPlayFromHand);
        return new object?[] { r.Can, r.Reason, r.ReasonParam1, r.ReasonParam2 };
    }

    /// <summary>
    /// `AddKreditsTax(card, costToAdd, instigatorID, out qqq)` —— 见派发表里那条的注释。
    /// 函数体全文：`IsValid(card)` → `Max(0, 当前 + costToAdd)` → 写回 →
    /// `IsActionProcess` 时通知客户端（本内核无 notifier，不实现）→ `qqq = False`。
    /// </summary>
    private object? DoAddKreditsTax(EffectContext c, object?[] a)
    {
        var card = AsCardOrId(c, a.ElementAtOrDefault(0));
        if (card is null)
        {
            // 蓝图那一支只写一条 `DirectClientLogger`（纯客户端日志），`qqq` 仍为 False
            return false;
        }

        card.KreditsTaxAsEnemyTarget = Math.Max(0, card.KreditsTaxAsEnemyTarget + AsInt(a.ElementAtOrDefault(1)));
        return false;
    }
}
