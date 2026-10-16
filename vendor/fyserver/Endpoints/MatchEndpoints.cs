using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        // KARDS replay entry point. Keep this literal route separate from /matches/v2/{id}:
        // otherwise "replay" is bound as an integer id and fails before a useful response is produced.
        app.MapPut("/matches/v2/replay", async (JsonElement payload, HttpContext context,
            AuthService auth, MatchHistoryService history, CodecService codec, ServerOptions options) =>
        {
            if (await auth.GetUserFromAuthAsync(context) == null)
                return Results.Unauthorized();
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("match_id", out var idNode) || !idNode.TryGetInt32(out var matchId) || matchId <= 0)
                return Results.Text("{\"error\":\"Invalid request\",\"message\":\"match_id must be a positive integer\",\"status_code\":400}", "application/json", statusCode: 400);
            if (!payload.TryGetProperty("pov_side", out var sideNode) || sideNode.ValueKind != JsonValueKind.String ||
                sideNode.GetString() is not ("left" or "right"))
                return Results.Text("{\"error\":\"Invalid request\",\"message\":\"pov_side must be left or right\",\"status_code\":400}", "application/json", statusCode: 400);

            var side = sideNode.GetString()!;
            var record = await history.GetAsync(matchId, context.RequestAborted);
            if (record?.Summary.Status != "completed" || record.StartingInfo == null)
                return Results.NotFound($"Replay with ID {matchId} not found");

            var actions = new List<MatchAction>();
            var cursor = 0;
            while (true)
            {
                var page = await history.GetActionsAsync(matchId, cursor, 1000, context.RequestAborted);
                if (page == null) return Results.NotFound($"Replay with ID {matchId} not found");
                actions.AddRange(page.Actions);
                if (!page.HasMore) break;
                if (page.NextActionId <= cursor)
                    return Results.Problem("Stored replay actions have an invalid cursor", statusCode: 500);
                cursor = page.NextActionId;
            }

            var starting = record.StartingInfo.MatchAndStartingData;
            var selectedPlayerId = side == "left" ? starting.StartingData.PlayerIdLeft : starting.StartingData.PlayerIdRight;
            var replayUrl = $"{options.GetAddressHttpR()}/replays/{matchId}";
            var replayActionsUrl = $"{replayUrl}/actions";
            var replay = new MatchReconnect
            {
                Actions = actions.OrderBy(x => x.ActionId).Select(x => codec.Encode(x)).ToArray(),
                LocalSubactions = record.StartingInfo.LocalSubactions,
                Match = starting.Match with
                {
                    ActionPlayerId = selectedPlayerId,
                    ActionSide = side,
                    Actions = [],
                    ActionsUrl = replayActionsUrl,
                    CurrentActionId = 0,
                    CurrentTurn = 1,
                    MatchId = matchId,
                    MatchUrl = replayUrl,
                    // Replay clients reject a live-match status; this payload represents an ended match.
                    Status = GameConstants.Finished,
                    WinnerId = record.Summary.WinnerSide == "left" ? record.Summary.LeftPlayerId :
                        record.Summary.WinnerSide == "right" ? record.Summary.RightPlayerId : 0,
                    WinnerSide = record.Summary.WinnerSide ?? ""
                },
                SameTurn = false,
                StartingData = starting.StartingData,
                TimeSinceStartOfTurn = -1,
                WaitingForSitNGoMatch = false
            };
            return Results.Json(replay, FyJsonContext.Default.MatchReconnect);
        });

        app.MapGet("/matches/v2", async (HttpContext context, AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            Console.WriteLine("正在获取匹配信息，用户ID：" + user.Id);

            var match = matches.GetActiveMatchForUser(user.Id);
            if (match == null)
                return Results.Text("null");

            var startingInfo = await matches.MakeMatchStartingInfo(user.Id, match);
            await history.SaveSnapshotAsync(match, startingInfo, context.RequestAborted);
            return Results.Ok(startingInfo);
        });

        app.MapGet("/matches/v2/reconnect", async (HttpContext context, AuthService auth, MatchManagerService matches, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            // ── 重连判定日志（2026-10-05，为"注入回放后重连不上"而加）───────────
            // 为什么必须打：这个端点**没匹配上时也返回 200**（`Results.Ok("null")`），
            // 所以光看状态码分不清"没匹配上"和"匹配上了但客户端不用"。
            // 实测（2026-10-05）：客户端用 `/players/335868/...` 而认证头解析出 `460707`
            // ⇒ 两个 id 不是一回事，必须把**判定用的那个 id**打出来。
            Console.WriteLine($"[reconnect] user.Id={user.Id} userName={user.UserName} 候选对局={matches.MatchedPairs.Count}");
            foreach (var i in matches.MatchedPairs)
            {
                MatchInfo mi = i.Value;
                Console.WriteLine($"[reconnect]   候选 matchId={mi.MatchId} left={mi.Left?.PlayerId} " +
                                  $"right={mi.Right?.PlayerId} winner='{mi.WinnerSide}' " +
                                  $"startingInfo={mi.MatchStartingInfo is not null} 动作={mi.MatchActions.Count}");
            }

            foreach (var i in matches.MatchedPairs)
            {
                MatchInfo mi = i.Value;
                // ★ `mi.Ex == "injected"`：**注入的调试对局**（`POST /replays/inject`）不按 id 匹配。
                //   为什么必须放宽：本地模式**没有持久化用户库**（`Find user: steam:… → 未找到
                //   → Created new user`），**每次进游戏都是新号**（实测同一账号
                //   807083 → 460707 → 160931）⇒ 注入时记的 id 与重连时的 id 必然不同 ⇒
                //   按 id 匹配永远连不上。注入对局本来就是"给我自己调试用的"，放宽是安全的。
                if ((mi.WinnerSide != "left" && mi.WinnerSide != "right") &&
                    (mi.Left?.PlayerId == user.Id || mi.Right?.PlayerId == user.Id ||
                     mi.Ex == "injected"))
                {
                    if (mi.MatchStartingInfo == null)
                        continue;

                    MatchReconnect mr = new()
                    {
                        Actions = [],
                        LocalSubactions = true,
                        Match = mi.MatchStartingInfo.MatchAndStartingData.Match with
                        {
                            CurrentActionId = mi.currentActionId,
                            CurrentTurn = mi.Turns,
                            PlayerStatusLeft = mi.PlayerStatusLeft,
                            PlayerStatusRight = mi.PlayerStatusRight,
                            Status = "running"
                        },
                        MulliganLeft = mi.MulliganLeft,
                        MulliganRight = mi.MulliganRight,
                        SameTurn = false,
                        StartingData = mi.MatchStartingInfo.MatchAndStartingData.StartingData,
                        TimeSinceStartOfTurn = -1,
                        WaitingForSitNGoMatch = false
                    };
                    var actions = mi.GetActionsByMinActionId(0);
                    if (actions.Count > 0)
                    {
                        mr.Actions = actions.Select(x => codec.Encode(x)).ToArray();
                    }

                    // ── 载荷落盘（2026-10-05，为"注入回放后重连不上"取证）──────────
                    // 为什么必须落盘：注入的对局**匹配成功、返回 200、载荷也发出去了**，
                    // 但客户端就是不进对局（实测三次）。这时唯一的办法是拿一次
                    // **真实断线重连**的载荷做对照 —— 逐字段 diff 才知道差在哪。
                    // 写进 `bot-log/reconnect-payload-<matchId>.json`（不写 header/token）。
                    try
                    {
                        Directory.CreateDirectory("bot-log");
                        File.WriteAllText($"bot-log/reconnect-payload-{mi.MatchId}.json",
                            JsonSerializer.Serialize(mr, FyJsonContext.Default.MatchReconnect));
                        Console.WriteLine($"[reconnect] 已落盘载荷 matchId={mi.MatchId} " +
                                          $"动作={mr.Actions?.Length ?? 0} status={mr.Match?.Status} " +
                                          $"left={mr.Match?.PlayerStatusLeft} right={mr.Match?.PlayerStatusRight} " +
                                          $"mulliganLeft={(mr.MulliganLeft is null ? "null" : "有")} " +
                                          $"mulliganRight={(mr.MulliganRight is null ? "null" : "有")}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[reconnect] 载荷落盘失败: {ex.Message}");
                    }

                    return Results.Ok(mr);
                }
            }
            return Results.Ok("null");
        });

        app.MapGet("/matches/v2/{id}", async (int id, HttpContext context, AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null) return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match)) return Results.NotFound($"Match with ID {id} not found");
            return IsParticipant(match, user.Id) ? Results.Text("running") : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        app.MapPut("/matches/v2/{id}/", async (int id, JsonElement payload, HttpContext context,
            AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, ServerOptions options, WebSocketHubService webSockets) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!matches.TryParseMatchActionPayload(payload, out var matchAction))
            {
                var rawPayload = payload.ValueKind == JsonValueKind.Undefined ? "<undefined>" : payload.GetRawText();
                Console.WriteLine($"无法解析 /matches/v2/{id} 请求体: {rawPayload}");
                return Results.Text("OK");
            }

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                user.Banned = true;
                await users.SaveUserAsync(user);
                lock (match.SyncRoot) match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                await history.MarkCompletedAsync(match, context.RequestAborted);
                await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

            MatchAction[] recorded;
            lock (match.SyncRoot)
            {
                var priorCount = match.MatchActions.Count;
                matches.TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}", payload);
                if (!string.IsNullOrEmpty(matchAction.ActionType) || !string.IsNullOrEmpty(matchAction.Action))
                {
                    matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns };
                    match.MatchActions.Add(matchAction);
                    match.currentActionId++;
                }
                recorded = match.MatchActions.Skip(priorCount).ToArray();
            }
            if (recorded.Length > 0) await history.AppendActionsAsync(match, recorded, context.RequestAborted);
            if (!string.IsNullOrEmpty(match.WinnerSide)) await history.MarkCompletedAsync(match, context.RequestAborted);
            return Results.Text("OK");
        });

        app.MapPut("/matches/v2/{id}/actions", async (int id, MatchPut matchPut, HttpContext context,
            AuthService auth, MatchManagerService matches, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = new Dictionary<string, object>();
            var actions = match.GetActionsByMinActionId(matchPut.MinActionId);
            Console.WriteLine($"正在获取动作，最小动作id：{matchPut.MinActionId}，动作数量：{actions.Count}");
            Console.ForegroundColor = ConsoleColor.Blue;
            foreach (var a in actions)
            {
                Console.WriteLine(a?.ToString());
            }
            Console.ForegroundColor = ConsoleColor.White;
            if (actions.Count > 0)
            {
                result["actions"] = actions.Select(x => codec.Encode(x)).ToArray();
            }

            result["match"] = new MatchPollDto(
                match.PlayerStatusLeft,
                // 单人对战
                string.Equals(match.Ex, "pw", StringComparison.Ordinal) ? "mulligan_done" : match.PlayerStatusRight,
                "running"
            );

            result["opponent_polling"] = true;

            if (!string.IsNullOrEmpty(match.WinnerSide))
            {
                result["match"] = new MatchPollDto(
                    GameConstants.EndMatch,
                    GameConstants.EndMatch,
                    GameConstants.Finished
                );
            }

            return Results.Ok(result);
        });

        app.MapGet("/config", () => Results.Ok(new ServerConfigDto(
            XserverClosed: "",
            XserverClosedHeader: "Server maintenance",
            ForgotPasswordUrl: "https://pornhub.com"
        )));

        app.MapPost("/matches/v2/{id}/actions", async (int id, MatchActionEn matchActionen, HttpContext context,
            AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, ServerOptions options, WebSocketHubService webSockets, ServerBotService bot) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var matchAction = matches.DecryptMatchAction(matchActionen);
            Console.WriteLine(matchActionen.A);

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                user.Banned = true;
                await users.SaveUserAsync(user);
                lock (match.SyncRoot) match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                await history.MarkCompletedAsync(match, context.RequestAborted);
                await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

            MatchAction[] recordedActions;
            lock (match.SyncRoot)
            {
                if (string.Equals(matchAction.ActionType, "XActionStartOfTurn", StringComparison.Ordinal))
                {
                    Console.WriteLine("OK有个入开始了回合");
                    match.Turns += 1;
                }
                var priorCount = match.MatchActions.Count;
                matches.TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}/actions");
                if (!string.IsNullOrEmpty(matchAction.ActionType) || !string.IsNullOrEmpty(matchAction.Action))
                {
                    if (matchAction.sub_actions != null)
                        matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns, PlayerId = user.Id };
                    else
                        matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns, PlayerId = user.Id, sub_actions = [] };
                    match.MatchActions.Add(matchAction);
                    match.currentActionId++;
                }

                if (string.Equals(matchAction.ActionType, "XActionEndOfTurn", StringComparison.Ordinal)
                    && ServerBotService.ShouldDriveBot(match) && user.Id == match.Left?.PlayerId
                    && string.IsNullOrEmpty(match.WinnerSide))
                {
                    match.Turns += 1;

                    // ── bot 的「回合开始」──────────────────────────────────────
                    // ⚠️ HQ 那个键**不能写死**。它其实是「那一方 HQ 卡自己的 cardID」，
                    //    每局不同（实测 replay-15→键 15、206428→键 28、634651→键 51、
                    //    989040→键 40、542091→键 91）。旧实现硬编码 `"75"`，只在某一局碰巧对。
                    //    这里从**人类玩家自己发来的动作**里学 —— 它必然是对的。
                    string hqKey = LearnHqKeyFrom(match, matchAction);

                    // Replay must see this marker before deciding, including the generated-card turn ID.
                    int startIndex = match.MatchActions.Count;
                    match.MatchActions.Add(new MatchAction(match.currentActionId, "XActionStartOfTurn", User.BotPlayerId,
                        new() { { "side", "right" } }, [], match.Turns, SendActionId: matchAction.SendActionId));
                    match.currentActionId++;

                    // ── bot 真正下棋（**先决策**，见下）───────────────────────
                    // 由内核「重放动作流重建局面 → 决策 → 产出动作」。
                    // 无法决策时（内核没加载 / **漂开闸门拒绝下棋**）返回空列表，
                    // 退化成原来那套空回合。
                    IReadOnlyList<MatchAction> botActions;
                    try
                    {
                        botActions = bot.DecideBotTurn(match, matchAction);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"bot 决策异常，本回合只发空回合：{ex}");
                        botActions = Array.Empty<MatchAction>();
                    }

                    // ★★ 顺序：**先决策、再按「决策前 / 决策后」两个时刻填 HQ**。
                    //
                    // 这个字段是「**对手**（人类）HQ 血量」的**采样值**，不是回合内的常量。
                    // 实测（214436）人类自己的 `XActionStartOfTurn` 写 22、同回合
                    // `XActionEndOfTurn` 写 20 —— 因为它随回合内发生的事变。
                    //
                    // ⚠️ 旧实现是「先取 `bot.LastOpponentHq`（= **上一次**决策后的值）
                    //    再决策」，于是 StartOfTurn 与 EndOfTurn 用的是**同一个过期值**，
                    //    过期了整整一个「人类回合 + bot 回合」：
                    //      · 542091 #65 `XActionStartOfTurn` 写 19，当时其实是 **21**
                    //      · 542091 #49 `XActionEndOfTurn`   写 20，当时其实是 **19**
                    //        （bot 在那一回合把人类 HQ 打掉了 1）
                    //    两者都在 `out/audit` 的「④ HQ 对不上」里，也是 542091 那 9 条中的 2 条。
                    //
                    // ⚠️ 但**动作在流里的位置不能变**：`XActionStartOfTurn` 必须排在
                    //    bot 的操作**前面**（客户端按序执行）。所以这里是「先算、后按序 append」。
                    var (hqBefore, hqAfter) = bot.HqFor(match.MatchId);

                    match.MatchActions[startIndex] = match.MatchActions[startIndex] with { ActionData = new()
                    {
                        { "side", "right" },
                        { hqKey, hqBefore.ToString() },
                    } };

                    foreach (var a in botActions)
                    {
                        match.MatchActions.Add(a with { ActionId = match.currentActionId });
                        match.currentActionId++;
                    }

                    // ── bot 的「回合结束」──────────────────────────────────────
                    // ⚠️ 即使上面一条操作都没产出，也必须发这条 —— 客户端靠它把控制权交回来。
                    if (!botActions.Any(a => a.ActionType == "XActionEndOfTurn"))
                    {
                        match.MatchActions.Add(new MatchAction(match.currentActionId, "XActionEndOfTurn", User.BotPlayerId, new()
                        {
                            { "side", "right" },
                            { "reason", "endTurnButton" },
                            { hqKey, hqAfter.ToString() },
                        }, [], match.Turns, SendActionId: matchAction.SendActionId));
                        match.currentActionId++;
                    }
                }
                recordedActions = match.MatchActions.Skip(priorCount).ToArray();
            }

            if (recordedActions.Length > 0)
                await history.AppendActionsAsync(match, recordedActions, context.RequestAborted);
            if (!string.IsNullOrEmpty(match.WinnerSide)) await history.MarkCompletedAsync(match, context.RequestAborted);

            return Results.Text("OK");
        });

        // 调度阶段
        app.MapPost("/matches/v2/{id}/mulligan", async (int id, MulliganCards mulliganCards, HttpContext context,
            AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            List<MatchCard> deck, hand;
            if (user.Id == match.Left?.PlayerId)
            {
                deck = match.LeftDeck;
                hand = match.LeftHand;
                match.PlayerStatusLeft = GameConstants.MulliganDone;
            }
            else if (user.Id == match.Right?.PlayerId)
            {
                deck = match.RightDeck;
                hand = match.RightHand;
                match.PlayerStatusRight = GameConstants.MulliganDone;
            }
            else return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = new MulliganResult(
                Deck: deck,
                ReplacementCards: new List<MatchCard>()
            );

            foreach (var id2 in mulliganCards.DiscardedCardIds)
            {
                // 1. 定位手牌
                int handIndex = hand.FindIndex(c => c.CardId == id2);
                if (handIndex == -1) continue;
                var cardInHand = hand[handIndex];
                // 2. 随机选取牌库中的一张牌
                int deckIndex = Random.Shared.Next(result.Deck.Count);
                var cardInDeck = result.Deck[deckIndex];
                // 3. 使用 'with' 交换位置信息 (Location 和 LocationNumber)
                // 产生一张 “带着旧手牌位置信息” 的新手牌
                var newHandCard = cardInDeck with
                {
                    Location = cardInHand.Location,
                    LocationNumber = cardInHand.LocationNumber
                };
                // 产生一张 “带着旧牌库位置信息” 的旧手牌
                var newDeckCard = cardInHand with
                {
                    Location = cardInDeck.Location,
                    LocationNumber = cardInDeck.LocationNumber
                };
                result.ReplacementCards.Add(newHandCard); // 这张牌将进入玩家手牌
                result.Deck[deckIndex] = newDeckCard; // 旧牌被洗回牌库对应位置
                hand[handIndex] = newHandCard; // 同步内存中的手牌状态，避免后续状态不一致
            }

            // 保存该侧的 mulligan 结果，供对手通过 /mulligan/{location} 拉取
            var snapshot = new MulliganResult(
                Deck: result.Deck.ToList(),
                ReplacementCards: result.ReplacementCards.ToList()
            );
            if (user.Id == match.Left?.PlayerId)
                match.MulliganLeft = snapshot;
            else
                match.MulliganRight = snapshot;

            return Results.Ok(result);
        });

        app.MapGet("/matches/v2/{id}/mulligan/{location}", async (int id, string location, HttpContext context, AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null) return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.Text("null");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (location is not ("left" or "right")) return Results.BadRequest();

            var mulligan = location == "left" ? match.MulliganLeft : match.MulliganRight;
            if (string.Equals(match.Ex, "pw", StringComparison.Ordinal))
            {
                // 单人
                mulligan = new MulliganResult(
                    Deck: match.RightDeck,
                    ReplacementCards: new List<MatchCard>()
                );
            }

            return mulligan == null ? Results.Text("null") : Results.Ok(mulligan);
        });

        // 比赛结束
        app.MapGet("/matches/v2/{id}/post", async (int id, HttpContext context, AuthService auth, MatchManagerService matches, MatchHistoryService history) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (MatchManagerService.IsSoloMatch(match))
            {
                match.PlayerStatusLeft = GameConstants.EndMatch;
                match.PlayerStatusRight = GameConstants.EndMatch;
            }
            else if (user.Id == match.Left?.PlayerId)
            {
                match.PlayerStatusLeft = GameConstants.EndMatch;
            }
            else
            {
                match.PlayerStatusRight = GameConstants.EndMatch;
            }

            if (MatchManagerService.CanRemoveMatch(match))
            {
                await history.MarkCompletedAsync(match, context.RequestAborted);
                matches.ClearMatchRuntimeState(match);
                matches.MatchedPairs.TryRemove(id, out _);
            }

            var player = match.GetPlayerById(user.Id);
            var isWinner = match.WinnerSide == (user.Id == match.Left?.PlayerId ? "left" : "right");

            if (player != null && user.Decks.TryGetValue(player.DeckId, out var deck))
            {
                var response = new PostMatchResponse(
                    Faction: deck.MainFaction,
                    Winner: isWinner
                );
                Console.WriteLine(JsonSerializer.Serialize(response, FyJsonContext.Default.PostMatchResponse));
                return Results.Ok(response);
            }

            return Results.NotFound();
        });

        return app;
    }

    private static bool IsParticipant(MatchInfo match, int userId) => match.Left?.PlayerId == userId || match.Right?.PlayerId == userId;

    /// <summary>
    /// 从对局动作流里**学出**「HQ 防御」那个键。
    ///
    /// 为什么不能写死：那个键是**那一方 HQ 卡自己的 cardID**，每局不同 ——
    /// 实测 replay-15→键 15、206428→键 28、634651→键 51、989040→键 40。
    /// 旧实现硬编码 `"75"`，只在某一局碰巧对。
    ///
    /// 判据：每个 `XActionStartOfTurn` / `XActionEndOfTurn` 都带且只带这一个整数键
    /// （实测 266/266）。人类玩家自己发来的动作必然用的是**客户端认的那个键**，
    /// 所以从它学最可靠 —— 比从 HQ 卡 cardID 推更稳（服务端注入的那条老动作
    /// 用的就是 `"75"`，而该局 HQ 卡其实是 41，说明两者不一定相等）。
    /// </summary>
    private static string LearnHqKeyFrom(MatchInfo match, MatchAction current)
    {
        foreach (var a in EnumerateFrom(match, current))
        {
            if (!string.Equals(a.ActionType, "XActionStartOfTurn", StringComparison.Ordinal)
                && !string.Equals(a.ActionType, "XActionEndOfTurn", StringComparison.Ordinal))
            {
                continue;
            }

            if (a.ActionData is null)
            {
                continue;
            }

            foreach (string k in a.ActionData.Keys)
            {
                if (int.TryParse(k, out _))
                {
                    return k;
                }
            }
        }

        return "75";   // 老行为的兜底，保留以便出问题时退化到旧格式
    }

    /// <summary>当前这一条优先，然后往前找（最近的动作最可信）。</summary>
    private static IEnumerable<MatchAction> EnumerateFrom(MatchInfo match, MatchAction current)
    {
        yield return current;
        for (int i = match.MatchActions.Count - 1; i >= 0; i--)
        {
            yield return match.MatchActions[i];
        }
    }

    // ⚠️ 这里原来有个 `BotHqDefenseOf(match) => "20"` —— **已删除**。
    //    它把 bot 回合边界动作的 HQ 血量写死成 20，实测一局 91 条动作里
    //    有 22 条对不上（人类 HQ 涨到 38 了还报 20）。
    //    现在改用 `ServerBotService.HqFor(matchId)`：**决策前**的值填
    //    `XActionStartOfTurn`、**决策后**的值填 `XActionEndOfTurn`
    //    （理由与实测证据见那段代码的注释）。留着那个返回 "20" 的辅助函数，
    //    早晚会被误用 —— 所以直接删掉。
}
