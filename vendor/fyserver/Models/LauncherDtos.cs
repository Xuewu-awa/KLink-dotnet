namespace fyserver.Models;

/// <summary>
/// 启动器房管接口的数据传输对象（配合 Endpoints/LauncherEndpoints.cs）。
/// 属性名由 FyJsonContext 的 SnakeCaseLower 策略统一转成 snake_case，
/// 与 fyserver 其它接口保持一致（如 online_players / http_port）。
/// </summary>
public record LauncherRoomStatusDto(
    bool Running,
    int HttpPort,
    int WsPort,
    int OnlinePlayers,
    int Matches,
    int WaitingPlayers,
    string AdminUiUrl,
    string Ip
);

/// <summary>在线玩家条目（启动器「房间」页）。</summary>
public record LauncherPlayerDto(
    int PlayerId,
    string PlayerName,
    string Username,
    int PlayerTag,
    bool Online,
    bool Banned,
    int MatchId
);

/// <summary>设置昵称请求。</summary>
public record LauncherNicknameRequest(string? Nickname);

/// <summary>设置昵称响应。</summary>
public record LauncherNicknameResultDto(bool Ok, string Nickname);

/// <summary>踢人请求（PlayerId 必填，Reason 可选）。</summary>
public record LauncherKickRequest(int PlayerId, string? Reason);

/// <summary>踢人响应。</summary>
public record LauncherKickResultDto(bool Ok, int PlayerId, string PlayerName, bool Kicked);
