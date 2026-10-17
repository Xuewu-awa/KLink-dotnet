namespace KLink.Bot.Engine;

/// <summary>
/// **UE `FRandomStream` 的逐位复刻** —— 客户端 `cardsRandomStream` 的等价物。
///
/// ## 为什么必须复刻客户端那条流（2026-10-02 方向变更）
///
/// 锁步下随机效果是**各客户端本地结算**的，双方必须得到同一个结果。
/// 客户端用的是 UE 引擎自带的 `FRandomStream`，**算法与常数完全公开**，
/// 所以内核不需要"猜"，也不需要事后校正 —— 只要用同一个种子、
/// 按同一个顺序消耗同样多次，就能**逐位复现**客户端抽到的那张卡。
///
/// 出处（全部是二进制/反编译级证据，不是推测）：
/// - `Kards_RNG_report` 的 `Weather.md` §4.2 给出 `kards-Win64-Shipping.exe`
///   里 `RandomIntegerInRangeFromStream`（IDA `0x143ddce50`）的反编译：
///   <code>
///   v13 = 196314165 * *((_DWORD *)v7 + 1) + 907633515;  // Seed = Seed*A + C (mod 2^32)
///   *((_DWORD *)v7 + 1) = v13;
///   result = (v13 >> 9) | 0x3F800000;                   // 高 23 位 → [1,2) 的 float
///   v14 = v11 + (int)(float)((float)(*(float *)&result - 1.0) * (float)v12);
///   </code>
///   其中 `v12 = max - min + 1` ⇒ **`RandRange(min,max)` 两端都是闭的**。
/// - 两个 LCG 常数（`196314165` / `907633515`）已用字节搜索在整个 exe 里定位到多处命中，
///   确认这个 UE5.6 fork **没有改**它们。
/// - 播种：`BP_CardFunctions::SetRandomStreamByMatchID(MatchID)` 里
///   `cardsRandomStream = MakeRandomStream(MatchID)`（`BP_CardFunctions.cpp:12339-12347`），
///   对局开始时用 `match_id` 播一次（`BP_OnlineMatch.cpp:1734-1744`）。
/// - 当前 pak 的 `BP_OnlineMatch:SetRandomStreamWithActionID` 仅含 Return/Nothing
///   和 EndOfScript；虽仍有行动接收/创建调用点，函数本身不重播种（RULE-105）。
///   此结论来自当前资产，不能据此推断服务端历史改动日期或所有恢复路径。
///
/// ## 验证
///
/// 报告 `Weather.md` §4.2.1 给了一组**可复算的测试向量**
/// （`match_id=1000000000`、重播种用 `CurrentActionId=10` ⇒ `seed=1000193900`，
/// 连抽三次原始 `RandomIntegerInRangeFromStream(0,2)`）：
/// <code>
///   第1次：Seed 1000193900 → 1626977479，返回 1
///   第2次：Seed 1626977479 → 4280773790，返回 2
///   第3次：Seed 4280773790 →  431904801，返回 0
/// </code>
/// 见 `tools/BotSim/SelfTest.cs` 的 `UeRandomStreamMatchesReportVector`。
/// 这三行同时钉住了 LCG 常数、高 23 位变换、以及"闭区间"三件事。
///
/// ## 与 <see cref="DeterministicRandom"/> 的关系
///
/// 那个是内核自己写的 splitmix64，**与客户端无关** —— 留着它只因为
/// `tools/NNTrain` 用它做独立的训练采样（不参与对局）。对局路径（`GameState.Random`）
/// 已经全部换成这一条流。
/// </summary>
public sealed class UeRandomStream
{
    /// <summary>LCG 乘数（`FRandomStream::MutateSeed` 的 A，已确认 fork 未改）。</summary>
    private const uint Multiplier = 196314165u;

    /// <summary>LCG 增量（同上的 C）。</summary>
    private const uint Increment = 907633515u;

    /// <summary>`FRandomStream::InitialSeed`（`Reset()` 会回到它）。</summary>
    public int InitialSeed { get; private set; }

    /// <summary>`FRandomStream::Seed` —— 当前状态（UE 里是 int32，这里存 uint 以避免溢出歧义）。</summary>
    public uint Seed { get; private set; }

    /// <summary>当前种子（供指纹/对拍用；<c>AtomicAction</c> 把它写进决策指纹）。</summary>
    public ulong State => Seed;

    /// <summary>
    /// **本局已经消耗过多少个随机数** —— 保真度探针。
    ///
    /// 为什么它是个强判据：随机流是一条**游标**，客户端和内核必须逐次对齐。
    /// 我们漏掉一个消费点（某原语没实现）⇒ 游标**落后**；多消费一次
    /// （一次调用拆成两次）⇒ 游标**超前**。两种情况下后面所有随机取数全错位。
    /// 所以「游标同步」等价于「我们的执行路径与客户端一致」。
    /// 审计里由 `ReplayAudit` 的 ⑦ 段报出来；`ReplayRunner` 发现身份不符时
    /// 会把它写进 <c>UnimplementedCalls</c>（`&lt;rng-cursor-desync:卡名:次数&gt;`）。
    /// </summary>
    public long ConsumedCount { get; private set; }

    /// <param name="seed">
    /// 播种值 = 对局的 `match_id`。UE 的 `MakeRandomStream(int32)` 把入参截成 int32，
    /// 这里显式做同样的截断（`match_id` 目前在 int32 范围内，但别依赖这一点）。
    /// </param>
    public UeRandomStream(ulong seed) => Initialize(unchecked((int)(uint)seed));

    /// <summary>`FRandomStream::Initialize(InSeed)`。</summary>
    public void Initialize(int inSeed)
    {
        InitialSeed = inSeed;
        Seed = unchecked((uint)inSeed);
    }

    /// <summary>`FRandomStream::Reset()`。</summary>
    public void Reset() => Seed = unchecked((uint)InitialSeed);

    /// <summary>`FRandomStream::MutateSeed()` —— 一次 LCG 步进，返回变换后的种子。</summary>
    private uint MutateSeed()
    {
        // 必须显式 unchecked：uint 乘法在 checked 上下文里会抛。
        Seed = unchecked(Seed * Multiplier + Increment);
        ConsumedCount++;
        return Seed;
    }

    /// <summary>
    /// `FRandomStream::GetFraction()` —— 返回 `[0,1)`。
    ///
    /// 做法是把**变换后种子的高 23 位**当作 `[1,2)` 的 float 尾数
    /// （`| 0x3F800000` 就是"指数 = 0、隐含前导 1"），再减 1 落到 `[0,1)`。
    /// 反汇编里的 `result = (v13 >> 9) | 0x3F800000` 就是这一步。
    /// </summary>
    public float GetFraction()
    {
        uint s = MutateSeed();
        int bits = unchecked((int)((s >> 9) | 0x3F800000u));
        return BitConverter.Int32BitsToSingle(bits) - 1.0f;
    }

    /// <summary>
    /// `UKismetMathLibrary::RandomIntegerInRangeFromStream(Stream, Min, Max)`。
    ///
    /// ⚠️ **两端都是闭的**（`Min + floor(GetFraction() * (Max-Min+1))`）。
    /// 以前内核把它当半开区间用（`DeterministicRandom.Next(min,max)`），
    /// 于是 `RandomIntFromRangeWithStream(0, 2)` 只会出 0/1，而客户端会出 0/1/2 ——
    /// 这是**独立于 RNG 算法的第二个错**，见 `CardApiDispatch.DoRandomIntFromRange`。
    /// </summary>
    public int RandRange(int min, int max)
    {
        // Current native wrapper RVA 0x3dddd90: signed 32-bit range <= 0
        // returns Min without mutating the stream (i=0x3ddde8a..0x3ddded9).
        int range = unchecked(max - min + 1);
        return range <= 0 ? min : unchecked(min + (int)(GetFraction() * range));
    }

    /// <summary>从 `maxExclusive` 个里挑一个下标（= `RandRange(0, maxExclusive-1)`）。</summary>
    public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : RandRange(0, maxExclusive - 1);

    /// <summary>半开区间版本（保留旧调用形状；新代码请直接用 <see cref="RandRange"/>）。</summary>
    public int Next(int minInclusive, int maxExclusive) => RandRange(minInclusive, maxExclusive - 1);

    /// <summary>
    /// `UKismetArrayLibrary::Array_ShuffleFromStream` —— **前向** Fisher-Yates：
    /// <code>
    /// for (i = 0; i &lt;= LastIndex; ++i) { Index = RandRange(i, LastIndex); swap(i, Index); }
    /// </code>
    /// ⚠️ 两个容易写错的点：
    /// 1. **前向**（从 0 往 n-1 走），不是常见的后向写法 —— 换一下洗出来的顺序完全不同；
    /// 2. 循环跑满 `n` 次（`i == LastIndex` 时 `RandRange` 返回 `i`、但**照样消耗一次**），
    ///    所以一共消耗 **n** 个随机数，不是 n-1 个。
    /// 这两点决定了后续所有随机取数的流位置，错了就全错。
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        int last = list.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            int j = RandRange(i, last);
            if (i != j)
            {
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];
}
