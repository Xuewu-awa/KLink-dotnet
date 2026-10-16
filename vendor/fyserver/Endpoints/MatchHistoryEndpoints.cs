using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class MatchHistoryEndpoints
{
    public static IEndpointRouteBuilder MapMatchHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // 公开只读接口：供游戏端观战与未来官网展示近期对局使用，不返回 Authorization/JWT 等凭据。
        app.MapGet("/replays", async (int? limit, int? playerId, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var rows = (await history.ListRecentAsync(limit ?? 50, playerId: playerId, cancellationToken: cancellationToken)).ToList();
            return Results.Json(new MatchHistoryListResponse(rows), FyJsonContext.Default.MatchHistoryListResponse);
        });

        app.MapGet("/replays/{id:int}", async (int id, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var record = await history.GetAsync(id, cancellationToken);
            if (record?.Summary.Status != "completed") return Results.NotFound();
            return Results.Json(new MatchHistoryDetailResponse(record.Summary, record.StartingInfo), FyJsonContext.Default.MatchHistoryDetailResponse);
        });

        app.MapGet("/replays/{id:int}/actions", async (int id, int? afterActionId, int? limit, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var record = await history.GetAsync(id, cancellationToken);
            if (record?.Summary.Status != "completed") return Results.NotFound();
            var page = await history.GetActionsAsync(id, afterActionId ?? 0, limit ?? 500, cancellationToken);
            return page == null ? Results.NotFound() : Results.Json(page, FyJsonContext.Default.MatchHistoryActionPage);
        });

        // ── 回放导入（2026-10-04）──────────────────────────────────────────────
        //
        // ## 为什么需要它
        //
        // `GET /replays` 读的是数据库 / 本地 FASTER KV，而**服务端一重启就空了**
        // （`ServerBotService.cs:291` 记着这个实测）。于是"想再看一眼某一局的某个动作"
        // 就变成不可能 —— 除非能**把回放推回来**。
        //
        // ## 关键用法：`?upto=<动作号>` —— 截断到那个动作
        //
        // 客户端播放一局"已结束"的对局时，**终局棋盘就是它那一刻的棋盘**。
        // 所以想核对"第 39 个动作时场上到底有谁"，只要把动作流**截到 #39** 推上去，
        // 客户端打开后的场面就**正好是那一刻**（不需要任何回放拖动/查看器）。
        //
        // ## 形状
        //
        // 请求体 = `MatchHistoryDocument`（`{summary, starting_info, actions}`），
        // 也就是本服务自己的 `GET /replays/{id}` + `GET /replays/{id}/actions` 两份载荷的**并集**。
        //   · `?upto=N`：只保留 `action_id <= N` 的动作（截断后重算 turns/action_count）；
        //   · `?id=M`  ：覆盖对局号 —— **截同一局的多个动作时必须给**，否则后推的会覆盖先推的。
        //
        // 落库走 `MatchHistoryService` 的三个既有方法（因此**顺带**会写进
        // `data/replays/` 的归档，见 `ArchiveReplay`）⇒ 推一次就同时有了 API 与文件。
        app.MapPost("/replays/import", async (JsonElement payload, int? upto, int? id,
            MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            MatchHistoryDocument? document;
            try
            {
                document = JsonSerializer.Deserialize(payload.GetRawText(), FyJsonContext.Default.MatchHistoryDocument);
            }
            catch (JsonException ex)
            {
                return Results.Problem($"回放 JSON 解析失败：{ex.Message}", statusCode: 400);
            }

            if (document?.StartingInfo is null)
            {
                return Results.Problem("回放缺少 starting_info（客户端要靠它还原双方牌库）", statusCode: 400);
            }

            var actions = document.Actions;
            if (upto is { } cut)
            {
                actions = actions.Where(x => x.ActionId <= cut).OrderBy(x => x.ActionId).ToList();
            }

            if (actions.Count == 0)
            {
                return Results.Problem("截断后没有动作（检查 ?upto= 是否小于第一个动作号）", statusCode: 400);
            }

            int matchId = id ?? document.Summary.MatchId;
            var match = new MatchInfo
            {
                MatchId = matchId,
                Ex = "injected",                        // ★ 标记：重连接口对"注入的调试对局"不按 id 匹配
                StartedAtUtc = document.Summary.StartedAt,
                CompletedAtUtc = document.Summary.CompletedAt ?? DateTime.UtcNow,
                MatchStartingInfo = document.StartingInfo,
                MatchActions = actions,
                Turns = document.Summary.Turns,
                WinnerSide = document.Summary.WinnerSide,
                currentActionId = actions[^1].ActionId + 1,
            };

            await history.SaveSnapshotAsync(match, document.StartingInfo, cancellationToken);
            await history.AppendActionsAsync(match, actions, cancellationToken);
            await history.MarkCompletedAsync(match, cancellationToken);

            return Results.Json(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["matchId"] = matchId,
                ["actions"] = actions.Count,
                ["upto"] = upto,
                ["lastActionId"] = actions[^1].ActionId,
                ["turns"] = match.Turns,
            });
        });

        // ── 回放注入为**活对局**（2026-10-04）—— "SL 式反复调试" ────────────────
        //
        // 与上面 `/replays/import` 的区别：
        // <code>
        //   import → 落成**已结束**的历史（客户端当回放看：只能看，不能打）
        //   inject → 注册进内存 `MatchedPairs` 的**活对局**（`WinnerSide` 留空）
        // </code>
        // 于是 `GET /matches/v2/reconnect`（`MatchEndpoints.cs:92`）会认它，把
        // `starting_data` + **截止到第 N 个动作的动作流**推给客户端，`status = "running"`
        // ⇒ **客户端接着打**。
        //
        // ⇒ 这就是"把一局回放推上去、从某个动作继续"的能力：想反复验证同一个局面，
        //   就反复 inject 到同一个动作、再重连 —— 等于模拟器的 **SL**。
        //
        // ⚠️ 两个前提（不满足就重连不到）：
        //   ① `Left/Right` 的 `PlayerId` 必须是**发起重连的那个账号**
        //      （`MatchEndpoints.cs:101-102` 按 `user.Id` 匹配）⇒ 默认取回放里的双方 id，
        //      可用 `?leftId=&rightId=` 覆盖；
        //   ② 必须带 `starting_info`（客户端靠它还原牌库；`:104-105` 会跳过没有的）。
        app.MapPost("/replays/inject", async (JsonElement payload, int? upto, int? id,
            int? leftId, int? rightId,
            MatchHistoryService history, MatchManagerService matches, CancellationToken cancellationToken) =>
        {
            MatchHistoryDocument? document;
            try
            {
                document = JsonSerializer.Deserialize(payload.GetRawText(), FyJsonContext.Default.MatchHistoryDocument);
            }
            catch (JsonException ex)
            {
                return Results.Problem($"回放 JSON 解析失败：{ex.Message}", statusCode: 400);
            }

            if (document?.StartingInfo is null)
            {
                return Results.Problem("回放缺少 starting_info（重连时客户端要用它还原牌库）", statusCode: 400);
            }

            var actions = document.Actions;
            if (upto is { } cut)
            {
                actions = actions.Where(x => x.ActionId <= cut).OrderBy(x => x.ActionId).ToList();
            }

            if (actions.Count == 0)
            {
                return Results.Problem("截断后没有动作（检查 ?upto=）", statusCode: 400);
            }

            int matchId = id ?? document.Summary.MatchId;
            var match = new MatchInfo
            {
                MatchId = matchId,
                Ex = "injected",                        // ★ 标记：重连接口对"注入的调试对局"不按 id 匹配
                StartedAtUtc = document.Summary.StartedAt,
                MatchStartingInfo = document.StartingInfo,
                MatchActions = actions,
                Turns = actions[^1].turn_number,
                WinnerSide = "",                        // ★ 未决 ⇒ 重连接口才会认它
                // ★★ 2026-10-05：**这两组必须设**，否则客户端不肯进这一局。
                //   `PlayerStatus*` 默认是 `"not_done"`（`MatchInfo` 构造函数），而真实的中局
                //   在换牌处理里会置成 `GameConstants.MulliganDone`（`MatchEndpoints.cs:406/412`）
                //   ⇒ 不设的话客户端以为**换牌还没做完**，拿到重连载荷也不进对局（实测）。
                //   `MulliganLeft/Right` 同理：真实重连时它们有值，客户端会读。
                //   这里按"换牌已完成"造一份：牌库用 starting_data 里的，替换牌为空。
                PlayerStatusLeft = GameConstants.MulliganDone,
                PlayerStatusRight = GameConstants.MulliganDone,
                MulliganLeft = new MulliganResult(
                    document.StartingInfo.MatchAndStartingData.StartingData.DeckLeft, []),
                MulliganRight = new MulliganResult(
                    document.StartingInfo.MatchAndStartingData.StartingData.DeckRight, []),
                currentActionId = actions[^1].ActionId + 1,
                Left = new LobbyPlayer(leftId ?? document.Summary.LeftPlayerId, 0, default),
                Right = new LobbyPlayer(rightId ?? document.Summary.RightPlayerId, 0, default),
            };

            matches.MatchedPairs[matchId] = match;       // ★ 注册成"活对局"
            await history.SaveSnapshotAsync(match, document.StartingInfo, cancellationToken);

            return Results.Json(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["matchId"] = matchId,
                ["actions"] = actions.Count,
                ["lastActionId"] = actions[^1].ActionId,
                ["turns"] = match.Turns,
                ["leftId"] = match.Left.PlayerId,
                ["rightId"] = match.Right.PlayerId,
                ["hint"] = "现在让客户端触发断线重连（GET /matches/v2/reconnect），它会从上面那个动作继续",
            });
        });

        // ── 活对局**就地回退**（2026-10-05）—— 真正可用的 "SL" ────────────────
        //
        // ## 为什么不用 `/replays/inject` 那条路
        //
        // 实测：注入一局**旧回放**时，`starting_info` 里的
        // `player_id_left` / `deck_id_left` 都是**录制那局时的账号**（例：807083 / 754230），
        // 而当前账号每次进游戏都不同（本地用户库 `Find user: steam:… → 未找到 → Created new user`）
        // ⇒ 客户端拿到的是一局"不属于自己、牌库也加载不出来"的对局
        // ⇒ **重连端点匹配成功、载荷也发出去了（200），客户端就是不进**。
        //
        // ## 这条接口做什么
        //
        // 拿**当前内存里那局真实对局**（身份、牌库、starting_info 全对），
        // 把它的动作流**截到第 `upto` 个动作**，并把胜负重置成"未决"
        // ⇒ 客户端下次 `GET /matches/v2/reconnect` 就会从那个动作**接着打**。
        //
        // 用法（想反复验证同一个局面 = 模拟器 SL）：
        //   ① 正常打，打到你关心的位置；
        //   ② `POST /replays/rewind?matchId=<当前局>&upto=<动作号>`（我这边会记下每步的动作号）；
        //   ③ 客户端断线重连 ⇒ 从那个动作继续；
        //   ④ 想再退回去，重复 ②。
        app.MapPost("/replays/rewind", (int matchId, int upto, MatchManagerService matches) =>
        {
            if (matches.GetMatch(matchId) is not { } match)
            {
                return Results.NotFound($"对局 {matchId} 不在内存里（已结束或被移除）");
            }

            var kept = match.MatchActions.Where(x => x.ActionId <= upto)
                                         .OrderBy(x => x.ActionId).ToList();
            if (kept.Count == 0)
            {
                return Results.Problem($"截断到 #{upto} 后没有动作（这局最小动作号是 " +
                                       $"{match.MatchActions.FirstOrDefault()?.ActionId}）", statusCode: 400);
            }

            match.MatchActions = kept;
            match.currentActionId = kept[^1].ActionId + 1;
            match.Turns = kept[^1].turn_number;
            match.WinnerSide = "";              // ★ 重新变"未决" ⇒ 重连接口才会认它
            match.IsCompleted = false;
            match.IsAborted = false;
            match.CompletedAtUtc = null;

            Console.WriteLine($"[rewind] matchId={matchId} 回退到 #{upto}：动作 {kept.Count} 条、" +
                              $"回合={match.Turns}、胜负已重置为未决");

            return Results.Json(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["matchId"] = matchId,
                ["actions"] = kept.Count,
                ["lastActionId"] = kept[^1].ActionId,
                ["turns"] = match.Turns,
                ["hint"] = "现在让客户端断线重连（GET /matches/v2/reconnect），它会从上面那个动作继续",
            });
        });

        app.MapGet("/spectate/matches", (MatchManagerService matches) =>
        {
            var response = new MatchHistoryListResponse(matches.GetActiveRealMatches().Where(x => !x.IsCompleted).Select(MatchHistoryService.CreateSummary).ToList());
            return Results.Json(response, FyJsonContext.Default.MatchHistoryListResponse);
        });

        app.MapGet("/spectate/matches/{id:int}", (int id, MatchManagerService matches) =>
        {
            var match = matches.GetMatch(id);
            if (match?.MatchStartingInfo == null || match.IsCompleted) return Results.NotFound();
            return Results.Json(new MatchHistoryDetailResponse(MatchHistoryService.CreateSummary(match), match.MatchStartingInfo), FyJsonContext.Default.MatchHistoryDetailResponse);
        });

        // ── bot 诊断 ────────────────────────────────────────────────────────
        // 「bot 只会跳过回合」有至少四种成因，且内核的失败都是静默的（只写日志）。
        // 这条把每次决策的关键数字摊开，一条 HTTP 请求就能分辨是哪一种：
        //   kernel-unavailable  内核没加载（数据/模型缺失）
        //   no-starting-info    拿不到双方牌库（MatchStartingInfo 还没生成）
        //   exception           决策抛异常
        //   no-state            重放没产出引擎
        //   no-action           走到了，但没找到可做的动作  ← 此时看 手牌 / 行动方
        //   **divergence**      判定与客户端漂开 ⇒ **主动拒绝下棋**（见下面的 divergence* 字段）
        //   ok                  正常产出
        app.MapGet("/spectate/bot", (ServerBotService bot, MatchManagerService matches) =>
        {
            var m = bot.Last;
            var d = bot.LastDivergence;

            // ⚠️ 为什么用 `Dictionary<string, object>` 而不是匿名类型：
            //    fyserver 的 JSON 走**源生成**（`FyJsonContext`，且 Program.cs 把
            //    TypeInfoResolverChain 清空只留它），匿名类型没有元数据，
            //    直接 `Results.Json(new { … })` 会抛
            //    `NotSupportedException: JsonTypeInfo metadata for type '<>f__AnonymousType…'`。
            //    字典是 ASP.NET Core 内置支持的类型，源生成器不用管。
            //    ⚠️ 所以这里**只放基本类型**（bool/int/string/null），别塞自定义 record。
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kernelLoaded"] = bot.IsReady,
                ["loadError"] = bot.LoadError,
                ["policy"] = bot.PolicyDescription,
                ["policyReady"] = bot.PolicyReady,
                ["deckCodes"] = bot.DeckCodeCount,
                ["stages"] = bot.Stages.OrderBy(kv => kv.Key)
                                       .ToDictionary(kv => kv.Key, kv => (object?)kv.Value),

                // ---- 漂开闸门 ----
                // 为什么单独一组：`stages` 里出现 `divergence` 只说明「发生过」，
                // 看不出是哪个信号、差多少。这几个数字是排查的入口。
                ["divergenceDetected"] = d?.Detected ?? false,
                ["divergenceReason"] = d?.Reason ?? "",
                ["hqOur"] = d?.HqOur ?? -1,
                ["hqClient"] = d?.HqClient ?? -1,
                ["unappliedHumanActions"] = d?.UnappliedHumanActions ?? 0,
                ["hqMismatches"] = d?.HqMismatches ?? 0,
                ["firstUnapplied"] = d?.FirstUnapplied,
                ["firstHqMismatch"] = d?.FirstHqMismatch,
                // 回合边界动作实际用的两个时刻的对手 HQ（见 ServerBotService.HqFor）
                ["hqOpponentBefore"] = d?.HqOpponentBefore ?? -1,
                ["hqOpponentAfter"] = d?.HqOpponentAfter ?? -1,
                ["divergenceMatchId"] = d?.MatchId,
                ["divergenceProducedActions"] = d?.ProducedActions,
                ["divergenceAtUtc"] = d?.AtUtc.ToString("o"),
                // 多局同时跑时按对局看（单例服务，别只看"最近一次"）
                ["divergenceByMatch"] = bot.DivergenceByMatch.Values
                    .OrderBy(x => x.MatchId)
                    .Select(x => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["matchId"] = x.MatchId,
                        ["detected"] = x.Detected,
                        ["reason"] = x.Reason,
                        ["unappliedHumanActions"] = x.UnappliedHumanActions,
                        ["hqMismatches"] = x.HqMismatches,
                        ["hqOur"] = x.HqOur,
                        ["hqClient"] = x.HqClient,
                        ["firstUnapplied"] = x.FirstUnapplied,
                        ["firstHqMismatch"] = x.FirstHqMismatch,
                        ["hqOpponentBefore"] = x.HqOpponentBefore,
                        ["hqOpponentAfter"] = x.HqOpponentAfter,
                        ["producedActions"] = x.ProducedActions,
                        ["atUtc"] = x.AtUtc.ToString("o"),
                    })
                    .ToList(),
            };

            payload["last"] = m is null
                ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["matchId"] = m.MatchId,
                    ["stage"] = m.Stage,
                    ["detail"] = m.Detail,
                    ["cardsInSnapshot"] = m.CardsInSnapshot,
                    ["actionsInSnapshot"] = m.ActionsInSnapshot,
                    ["botHandCount"] = m.BotHandCount,
                    ["producedActions"] = m.ProducedActions,
                    ["atUtc"] = m.AtUtc.ToString("o"),
                };

            payload["activeMatches"] = matches.MatchedPairs.Values
                .Where(x => !x.IsCompleted)
                .Select(x => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["matchId"] = x.MatchId,
                    ["ex"] = x.Ex,
                    ["leftPlayerId"] = x.Left?.PlayerId,
                    ["rightPlayerId"] = x.Right?.PlayerId,
                    ["startingInfoReady"] = x.MatchStartingInfo is not null,
                    ["leftHand"] = x.LeftHand.Count,
                    ["rightHand"] = x.RightHand.Count,
                    ["leftDeck"] = x.LeftDeck.Count,
                    ["rightDeck"] = x.RightDeck.Count,
                    ["actionCount"] = x.MatchActions.Count,
                    ["turns"] = x.Turns,
                    ["winnerSide"] = x.WinnerSide,
                })
                .ToList();

            return Results.Json(payload);
        });

        app.MapGet("/spectate/matches/{id:int}/actions", (int id, int? afterActionId, int? limit, MatchManagerService matches) =>
        {
            var match = matches.GetMatch(id);
            if (match == null || match.IsCompleted) return Results.NotFound();
            var take = Math.Clamp(limit ?? 500, 1, 1000);
            var actions = match.SnapshotActions().Where(x => x.ActionId > (afterActionId ?? 0)).OrderBy(x => x.ActionId).Take(take + 1).ToList();
            var hasMore = actions.Count > take;
            if (hasMore) actions.RemoveAt(actions.Count - 1);
            var next = actions.Count == 0 ? afterActionId ?? 0 : actions[^1].ActionId;
            var page = new MatchHistoryActionPage(id, next, hasMore, actions);
            return Results.Json(page, FyJsonContext.Default.MatchHistoryActionPage);
        });

        return app;
    }
}
