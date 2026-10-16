using System.Collections.Concurrent;
using System.Text;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 启动器房管接口（KLink.App 的「服务器 / 房间 / 控制台」页依赖）。
///
/// 背景：fyserver 原本只有 Web 后台（/admin/api/*，需 X-Admin-Key 或会话 Cookie），
/// 而启动器需要一组"本机轻量、只读状态 + 踢人"的接口。这里单独开 /launcher/room/* 命名空间，
/// 与 /admin/api/* 完全分开，互不影响（与 KLink.Server 时代的路径保持一致，启动器 UI 无需改动）。
///
/// ⚠️ 安全边界：本组接口**不鉴权**，只应部署在可信网络（本机 / 局域网自建服）。
/// 若将来要暴露到公网，必须补令牌校验（可复用 ServerOptions.adminApiKey）。
/// </summary>
public static class LauncherEndpoints
{
    /// <summary>房间昵称：按连接来源地址记住玩家自取的昵称（内存态，重启即清）。</summary>
    private static readonly ConcurrentDictionary<string, string> NicknamesByAddress = new();

    public static IEndpointRouteBuilder MapLauncherEndpoints(this IEndpointRouteBuilder app)
    {
        // 服务端状态：启动器「服务器」页用它显示 运行 / 端口 / 在线 / 对局
        app.MapGet("/launcher/room/status", (ServerOptions options, WebSocketHubService webSockets,
            MatchManagerService matches) =>
        {
            int waiting = matches.WaitingPlayers1.Count + matches.WaitingPlayers2.Count
                + matches.WaitingPlayersClassic.Count + matches.WaitingPlayersUnranked.Count
                + matches.WaitingPlayersDraft.Count + matches.WaitingPlayersBrawl.Count
                + matches.BattleCodePlayers.Values.Sum(v => v.Count);

            return Results.Ok(new LauncherRoomStatusDto(
                Running: true,
                HttpPort: options.portHttp,
                // fyserver 是 HTTP + WebSocket 单端口模型，两个字段同值
                WsPort: options.portHttp,
                OnlinePlayers: webSockets.OnlineCount,
                Matches: matches.GetActiveRealMatches().Count,
                WaitingPlayers: waiting,
                AdminUiUrl: "/admin-ui/",
                Ip: options.ip
            ));
        });

        // 在线玩家列表：启动器「房间」页
        app.MapGet("/launcher/room/players", async (WebSocketHubService webSockets, UserStoreService users,
            MatchManagerService matches) =>
        {
            var list = new List<LauncherPlayerDto>();
            foreach (int id in webSockets.OnlineUserIds())
            {
                User? user = null;
                try { user = await users.GetByIdAsync(id); } catch { /* 单条读取失败不影响整体 */ }

                MatchInfo? match = matches.GetActiveMatchForUser(id);
                list.Add(new LauncherPlayerDto(
                    PlayerId: id,
                    PlayerName: user?.Name ?? "",
                    Username: user?.UserName ?? "",
                    PlayerTag: user?.Tag ?? 0,
                    Online: true,
                    Banned: user?.Banned ?? false,
                    MatchId: match?.MatchId ?? 0
                ));
            }
            return Results.Ok(list);
        });

        // 服务端日志：启动器「控制台」页（由 LauncherLogBuffer 捕获控制台输出）
        app.MapGet("/launcher/room/logs", () => Results.Ok(LauncherLogBuffer.Recent()));

        // 玩家自取昵称（无账号体系时的显示名）
        app.MapPost("/launcher/room/nickname", (LauncherNicknameRequest? body, HttpContext context) =>
        {
            string nick = (body?.Nickname ?? "").Trim();
            if (nick.Length > 24)
                nick = nick[..24];
            string key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (nick.Length == 0)
                NicknamesByAddress.TryRemove(key, out _);
            else
                NicknamesByAddress[key] = nick;
            return Results.Ok(new LauncherNicknameResultDto(Ok: true, Nickname: nick));
        });

        // 踢人：只断开连接，不封禁（封禁请走 /admin/api/users/{id}/ban）
        app.MapPost("/launcher/room/kick", async (LauncherKickRequest? body, WebSocketHubService webSockets,
            UserStoreService users) =>
        {
            int id = body?.PlayerId ?? 0;
            if (id <= 0)
                return Results.BadRequest(new LauncherKickResultDto(false, 0, "", false));

            string reason = string.IsNullOrWhiteSpace(body?.Reason) ? "被房主移出房间" : body!.Reason!;
            bool kicked = await webSockets.DisconnectAsync(id, reason);

            User? user = null;
            try { user = await users.GetByIdAsync(id); } catch { /* 忽略 */ }

            return Results.Ok(new LauncherKickResultDto(
                Ok: true,
                PlayerId: id,
                PlayerName: user?.Name ?? "",
                Kicked: kicked
            ));
        });

        return app;
    }
}

/// <summary>
/// 控制台输出环形缓冲：给启动器「控制台」页提供日志。
/// fyserver 原本只有 Console.WriteLine（不落文件、无结构化日志），
/// 这里通过 Console.SetOut 分流捕获，**不改动任何现有输出语句**。
/// </summary>
public static class LauncherLogBuffer
{
    private const int MaxEntries = 300;
    private static readonly object Gate = new();
    private static readonly Queue<string> Entries = new();

    public static void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (Gate)
        {
            Entries.Enqueue(line);
            while (Entries.Count > MaxEntries)
                Entries.Dequeue();
        }
    }

    public static string[] Recent()
    {
        lock (Gate)
        {
            return Entries.ToArray();
        }
    }

    /// <summary>把控制台输出接一份到缓冲区（保留原有输出行为）。</summary>
    public static void AttachConsoleTee()
    {
        try
        {
            Console.SetOut(new TeeWriter(Console.Out));
        }
        catch
        {
            // 无控制台句柄等场景下静默跳过，不影响服务运行
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _pending = new();

        public TeeWriter(TextWriter inner) => _inner = inner;

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            if (value == '\n')
                FlushLine();
            else if (value != '\r')
                _pending.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            _inner.Write(value);
            foreach (char c in value)
            {
                if (c == '\n')
                    FlushLine();
                else if (c != '\r')
                    _pending.Append(c);
            }
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            _pending.Append(value);
            FlushLine();
        }

        private void FlushLine()
        {
            if (_pending.Length > 0)
            {
                Add(_pending.ToString());
                _pending.Clear();
            }
        }
    }
}
