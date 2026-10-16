using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>后台会话鉴权与按类别授权；权限始终在服务端校验，前端隐藏按钮仅用于体验。</summary>
public sealed class AdminApiAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    public AdminApiAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AdminAccountService accounts, AdminAuditLogService audit,
        ServerOptions options)
    {
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }
        if (path.Equals("/admin/api/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/api/session", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/api/setup", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/api/database/configure", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var actor = accounts.GetSessionAccount(context.Request.Cookies[AdminAccountService.CookieName]);
        // PATCH(KLink): 本机（回环）访问后台免登录。
        //
        // 定位说明：KLink 是个人测试用启动器，后台只在服务器本机打开（启动器的「进入管理员界面」
        // 按钮固定跳 127.0.0.1），每次输密码纯属负担。因此：
        //   · 回环请求        → 直接视为 Owner，无需登录；
        //   · 带 X-Admin-Key 的远程请求（若配置了密钥）→ 仍按 Owner 放行，便于外部工具调用；
        //   · 其它远程请求    → 仍强制会话 Cookie 鉴权（局域网模式把后台地址发给别人时不会裸奔）。
        if (actor == null && ClientAddress.IsLoopback(context))
        {
            // 这个 Username 只用于审计日志（界面显示名由 /session 按 setting.json:adminDisplayName 给出），
            // 所以保留一个能区分「免登录本机操作」与「真实登录账号」的标识。
            actor = new AdminAccount { Id = "local-loopback", Username = "本机管理员（免登录）", IsOwner = true };
        }
        else if (actor == null &&
                 MatchesAdminKey(context.Request.Headers["X-Admin-Key"].FirstOrDefault(), options))
        {
            actor = new AdminAccount { Id = "local-launcher", Username = "KLink 启动器", IsOwner = true };
        }

        if (actor == null)
        {
            await Reject(context, 401, "请先登录后台账号");
            return;
        }
        context.Items["adminActor"] = actor;

        var local = path["/admin/api".Length..];
        var permission = RequiredPermission(local);
        var mutating = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        var isSelfAccountRoute = local.Equals("/me", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/me/", StringComparison.OrdinalIgnoreCase);
        if (local.Equals("/logout", StringComparison.OrdinalIgnoreCase)) mutating = false;
        var isPlayerRoleChange = mutating && local.StartsWith("/users/", StringComparison.OrdinalIgnoreCase) &&
                                 local.EndsWith("/roles", StringComparison.OrdinalIgnoreCase);
        if (isPlayerRoleChange && !accounts.HasPermission(actor, "permissions"))
        {
            await Reject(context, 403, "修改玩家角色需要权限管理权限");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }
        if (permission != null && (mutating || permission == "permissions") && !accounts.HasPermission(actor, permission))
        {
            await Reject(context, 403, "没有执行此操作所需的权限");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }
        if (mutating && permission == null && !actor.IsOwner && !isSelfAccountRoute)
        {
            await Reject(context, 403, "此操作仅 Owner 可执行");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }

        try { await _next(context); }
        catch
        {
            if (mutating) audit.Record(actor.Username, context.Request.Method, path, 500, context.Connection.RemoteIpAddress?.ToString());
            throw;
        }
        if (mutating || local.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            audit.Record(actor.Username, context.Request.Method, path, context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString());
    }

    private static string? RequiredPermission(string local)
    {
        if (local.StartsWith("/users", StringComparison.OrdinalIgnoreCase)) return "players";
        if (local.StartsWith("/content", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/store", StringComparison.OrdinalIgnoreCase)) return "content";
        if (local.StartsWith("/redeem", StringComparison.OrdinalIgnoreCase)) return "content";
        if (local.StartsWith("/matches", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/queues", StringComparison.OrdinalIgnoreCase)) return "matches";
        if (local.StartsWith("/server-config", StringComparison.OrdinalIgnoreCase)) return "serverConfig";
        // KLink：对局配置（开局卡牌金卡开关）与服务器设置同权限
        if (local.StartsWith("/match-config", StringComparison.OrdinalIgnoreCase)) return "serverConfig";
        if (local.StartsWith("/system-settings", StringComparison.OrdinalIgnoreCase)) return "systemSettings";
        if (local.StartsWith("/cards", StringComparison.OrdinalIgnoreCase)) return "systemSettings";
        if (local.StartsWith("/patch-paks", StringComparison.OrdinalIgnoreCase)) return "patchPaks";
        if (local.StartsWith("/database", StringComparison.OrdinalIgnoreCase)) return "permissions";
        if (local.StartsWith("/accounts", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/audit-logs", StringComparison.OrdinalIgnoreCase)) return "permissions";
        return null;
    }

    /// <summary>
    /// 校验 X-Admin-Key（PATCH/KLink）。上游删除了 AdminAuth，这里内联实现：
    /// 两侧都非空、长度一致、固定时间比较；未配置 adminApiKey 时恒为 false（不接受该通道）。
    /// </summary>
    private static bool MatchesAdminKey(string? supplied, ServerOptions options)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(options.adminApiKey))
            return false;
        var expected = System.Text.Encoding.UTF8.GetBytes(options.adminApiKey);
        var actual = System.Text.Encoding.UTF8.GetBytes(supplied);
        return expected.Length == actual.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString());
    }
}
