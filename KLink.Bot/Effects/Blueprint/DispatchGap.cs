using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Effects.Blueprint;

/// <summary>
/// ★ **派发表静态缺口**的计算 —— 「IR 调了、派发表里没有、而且静默失败」这一类 bug 的量化。
///
/// 计算放在库里（而不是只放在 `tools/BotSim`）的原因：**两个地方要用同一份数字** ——
/// <list type="number">
/// <item>`tools/BotSim` 的**防回归自测**（`DispatchGap.Check`，冻结基线 + 指纹）；</item>
/// <item>`tools/ServerBridgeTest --audit-replay` 的**审计 ⑥b**（每次审计都报一次，
///       让人在看回放对拍时顺手看到这个数）。</item>
/// </list>
/// 两处各写一遍的话，某天它们会给出不同的数，然后没人知道该信哪个。
///
/// ## 判据（三层，缺一不可）
///
/// <list type="number">
/// <item><b>不在派发表里</b>（`CardApi.ImplementedNames`）。</item>
/// <item><b>locals 也兜不住</b> —— `KismetVm.ExecuteCall` 在派发表认不出来时会回退执行
///       **调用方那张卡自己的**函数体（IR 的 `locals`，见 `KismetVm.cs:583` 那段）。
///       所以必须**按调用点**判：`KismetLibrary.FindLocalProgram(调用卡, 名字)` 命中就不算缺口
///       （`FindLocalProgram` 自带 `ResolveBaseName` 变体后缀回退）。
///       实测有 **63 种 / 148 个调用点**属于这一类 —— 光看"不在派发表里"会把它算成缺口。</item>
/// <item>事件入口的 `Steps` **和** `locals` 的函数体都要扫 —— locals 体是**真会被执行**的，
///       里面再调一个没实现的名字同样是缺口（实测多出 20 种）。</item>
/// </list>
///
/// ⚠️ 它**不判断缺口该不该修**（那是 A/B/C 分类的事，
/// 见 `out/audit/missing-keys-classify2.py`）。它只负责"集合不再悄悄变大"。
/// </summary>
public static class DispatchGap
{
    /// <summary>缺口集合：名字 → 真缺口调用点数（按名字排序，确定性）。</summary>
    public static SortedDictionary<string, int> Compute(CardDatabase db)
    {
        if (KismetLibrary.Default is not { } lib)
        {
            throw new InvalidOperationException("蓝图 IR 没加载（KismetLibrary.Default 是 null）");
        }

        var engine = new MatchEngine(db, Array.Empty<string>(), Array.Empty<string>(), seed: 1);
        var implemented = engine.Api.ImplementedNames;

        var gaps = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (cardName, card) in lib.AllCards)
        {
            foreach (var step in card.Steps)
            {
                Count(cardName, step);
            }

            foreach (var body in card.Locals.Values)
            {
                foreach (var step in body)
                {
                    Count(cardName, step);
                }
            }

            void Count(string card, KismetStep step)
            {
                if (!string.Equals(step.Op, "call", StringComparison.Ordinal)
                    || step.Function is not { } fn)
                {
                    return;
                }

                if (implemented.Contains(fn))
                {
                    return;
                }

                if (lib.FindLocalProgram(card, fn) is not null)
                {
                    return;
                }

                // Card-local custom events are represented as ordinary
                // entrypoints and invoked from a small wrapper (for example
                // `CustomEventOnCardDealDamage`). KismetVm executes these
                // through its no-output entrypoint fallback.
                if (step.OutParams.Count == 0 && lib.FindProgram(card, fn) is not null)
                {
                    return;
                }

                gaps[fn] = gaps.GetValueOrDefault(fn) + 1;
            }
        }

        return gaps;
    }

    /// <summary>
    /// 缺口集合的指纹：排序后的 `名字:调用点数` 行的 SHA-256 前 16 位。
    ///
    /// 为什么用指纹而不是只比总数：只比总数会漏掉「修一个 + 坏一个」——
    /// 两个方向的改动互相抵消，总数不变，但缺口集合已经变了。
    /// 指纹对**任何**集合变化都敏感。
    /// </summary>
    public static string Fingerprint(SortedDictionary<string, int> gaps)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in gaps)
        {
            sb.Append(k).Append(':').Append(v).Append('\n');
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }
}
