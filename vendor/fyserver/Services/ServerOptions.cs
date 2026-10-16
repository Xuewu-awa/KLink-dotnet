using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 服务器运行配置（端口 / 监听与对外地址 / 反作弊 / 管理员密钥 / 默认玩家名）。
/// HTTP 与 WebSocket 共用 portHttp 端口。
/// 兼容旧 setting.json（System.Text.Json 读取，字段名保持原样）。
///
/// 注意：属性名即 JSON 字段名（没有命名策略），新增字段要同步写进 setting.json。
/// </summary>
public class ServerOptions
{
    public int portHttp { get; set; } = 5231;
    /// <summary>监听地址；默认 0.0.0.0（原实现硬编码，现可只绑本机）。</summary>
    public string listenIp { get; set; } = "0.0.0.0";
    /// <summary>对客户端通告的端口；为空则用 portHttp（反代/端口映射场景用）。</summary>
    public int? publicPortHttp { get; set; }
    public bool bancheat { get; set; } = false;
    /// <summary>对客户端通告的主机地址；用于生成客户端可达的 HTTP/WebSocket 地址。</summary>
    public string ip { get; set; } = "127.0.0.1";
    /// <summary>客户端收到的对外 HTTP 协议；HTTPS 通告时 WebSocket 地址自动使用 WSS。</summary>
    public string publicScheme { get; set; } = "http";
    /// <summary>管理 API 密钥；为空时管理接口仅允许 loopback 访问。</summary>
    public string adminApiKey { get; set; } = "";

    /// <summary>
    /// 新玩家的默认显示名（客户端里的 player_name）。
    /// KLink 启动器把它写进 setting.json，由 /session 在建号（或名字未设置）时套用。
    /// 为空则回退 <see cref="DefaultPreferredPlayerName"/>。
    /// </summary>
    public string preferredPlayerName { get; set; } = "";

    /// <summary>默认玩家名的兜底值（preferredPlayerName 为空时使用）。</summary>
    public const string DefaultPreferredPlayerName = "氧化铜";

    /// <summary>
    /// bot 的神经网络模型文件路径（`.bin`）。
    ///
    /// 为空时按 `BotData/nn-model.bin` 找；**再找不到就退回贪心策略**，
    /// 不影响接线本身（接线是否成立与 AI 强弱无关）。
    /// </summary>
    public string botModelPath { get; set; } = "";

    /// <summary>给 <see cref="ServerBotService"/> 用的别名（内核不认识 `botModelPath` 的小写风格）。</summary>
    public string BotModelPath => botModelPath;

    /// <summary>
    /// 后台界面展示的管理员名（KLink 启动器写入；本机免登录时 /session 用它作为 username）。
    /// 为空则回退 <see cref="DefaultAdminDisplayName"/>。
    /// </summary>
    public string adminDisplayName { get; set; } = "";

    /// <summary>后台管理员显示名的兜底值。</summary>
    public const string DefaultAdminDisplayName = "管理员";

    /// <summary>取实际使用的后台管理员显示名（配置为空则回退常量）。</summary>
    public string EffectiveAdminDisplayName =>
        string.IsNullOrWhiteSpace(adminDisplayName) ? DefaultAdminDisplayName : adminDisplayName.Trim();

    /// <summary>后台默认头像（KLink 应用图标）。账号未上传头像时前后端都用它。</summary>
    public const string DefaultAdminAvatarUrl = "/admin-ui/assets/klink-icon-256.png";

    /// <summary>
    /// 新号初始金币 / 钻石（KLink 启动器写入 setting.json）。
    ///
    /// 为什么要给：上游 fyserver 新建用户时 Gold/Diamonds 都是 0，而商店里卡包要 100 金币，
    /// 唯一"免费"的档位是真钱商品（diamonds_s / diamonds_m），私服上根本走不通支付 ——
    /// 结果就是**商店能打开但什么都买不了**（点购买只会得到 Insufficient gold）。
    /// 给一点启动资金，商店、开包这条链路才可用；不想要就把值设为 0。
    /// </summary>
    public int startingGold { get; set; } = DefaultStartingGold;
    public int startingDiamonds { get; set; } = DefaultStartingDiamonds;

    /// <summary>新号默认启动资金（够买几个卡包，便于测试商店与开包）。</summary>
    public const int DefaultStartingGold = 5000;
    public const int DefaultStartingDiamonds = 500;

    /// <summary>取实际使用的新号初始货币（负数按 0 处理）。</summary>
    public int EffectiveStartingGold => startingGold < 0 ? 0 : startingGold;
    public int EffectiveStartingDiamonds => startingDiamonds < 0 ? 0 : startingDiamonds;

    /// <summary><see cref="User.Name"/> 的"未设置"标记（见 Models/User.cs 的构造函数）。</summary>
    public const string UnsetPlayerName = "<anon>";

    /// <summary>取实际使用的默认玩家名（配置为空则回退常量）。</summary>
    public string EffectivePreferredPlayerName =>
        string.IsNullOrWhiteSpace(preferredPlayerName) ? DefaultPreferredPlayerName : preferredPlayerName.Trim();

    // 监听地址由 listenIp 决定；ip 只用于生成下发给客户端的可达地址与 websocketurl
    // （HTTP 与 WebSocket 共用同一端口，客户端直连 HTTP 端口根路径升级）。
    public string GetAddressHttp() => $"http://{listenIp}:{portHttp}";
    public string GetAddressHttpR() => $"{GetPublicHttpScheme()}://{ip}{GetPublicPortSuffix()}";
    public string GetAddressWsR() => $"{(string.Equals(publicScheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws")}://{ip}{GetPublicPortSuffix()}/";

    private string GetPublicHttpScheme() => string.Equals(publicScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
    private string GetPublicPortSuffix() => publicPortHttp is > 0 ? $":{publicPortHttp.Value}" : "";

    /// <summary>读取 ./setting.json；不存在或解析失败则保持默认值。</summary>
    public void ReadFromFile()
    {
        const string path = "./setting.json";
        if (!File.Exists(path)) return;

        ReadFromJson(File.ReadAllText(path));
        SnapshotLauncherOwnedValues();
    }

    // ==================== 启动器所有权（KLink PATCH） ====================
    //
    // 背景：Program.cs 启动时会再用数据库里持久化的 server:settings 覆盖一次本轮配置。
    // 那份 JSON 是历史快照，里面存着**上一次运行时的网络配置** ——
    // 于是"切到局域网模式"会被库里的旧值顶回去（实测：启动器写了 listenIp=0.0.0.0，
    // fyserver 却仍绑 127.0.0.1，别的设备自然连不上）。
    //
    // 而 setting.json 是启动器**每次启动都重写**的，这些字段的所有者是启动器。
    // 因此这里在读文件后把值快照下来，数据库覆盖之后再还原回去。

    /// <summary>由启动器决定、不允许被数据库覆盖的字段（读 setting.json 后快照）。</summary>
    private sealed record LauncherValues(
        int PortHttp, string ListenIp, int? PublicPortHttp, string Ip, string PublicScheme,
        bool Bancheat, string AdminApiKey, string PreferredPlayerName, string AdminDisplayName,
        int StartingGold, int StartingDiamonds);

    private LauncherValues? _launcherValues;

    private void SnapshotLauncherOwnedValues()
        => _launcherValues = new LauncherValues(
            portHttp, listenIp, publicPortHttp, ip, publicScheme, bancheat, adminApiKey,
            preferredPlayerName, adminDisplayName, startingGold, startingDiamonds);

    /// <summary>数据库覆盖之后调用：把启动器拥有的字段还原回去。</summary>
    public void RestoreLauncherOwnedValues()
    {
        if (_launcherValues is not { } v) return;
        portHttp = v.PortHttp;
        listenIp = v.ListenIp;
        publicPortHttp = v.PublicPortHttp;
        ip = v.Ip;
        publicScheme = v.PublicScheme;
        bancheat = v.Bancheat;
        adminApiKey = v.AdminApiKey;
        preferredPlayerName = v.PreferredPlayerName;
        adminDisplayName = v.AdminDisplayName;
        startingGold = v.StartingGold;
        startingDiamonds = v.StartingDiamonds;
    }

    public void ReadFromJson(string json)
    {
        var loaded = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ServerOptions);
        if (loaded == null)
        {
            return;
        }

        portHttp = loaded.portHttp;
        listenIp = loaded.listenIp;
        publicPortHttp = loaded.publicPortHttp;
        ip = loaded.ip;
        publicScheme = string.Equals(loaded.publicScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        bancheat = loaded.bancheat;
        adminApiKey = loaded.adminApiKey ?? "";
    }
}
