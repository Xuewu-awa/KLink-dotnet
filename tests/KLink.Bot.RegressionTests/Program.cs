using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Replay;
using KLink.Bot.Server;

string dataDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "Data");
var db = CardDatabase.Load(dataDir);
KismetLibrary.Initialize(Path.Combine(dataDir, "card-ir.json"));
var vecs = StateEncoder.LoadCardVectors(dataDir);
const string Gordon = "card_unit_gordon_highlanders";
const string Order = "card_event_forward_observers";
const int BotPlayerId = -9178;

var cases = new List<(string Name, Action Run)>
{
    ("Bot hand choice takes priority over an exhausted replay reader", BotHandChoice),
    ("Bot empty hand choice emits explicit target ID 0", EmptyBotHandChoice),
    ("Invalid bot hand choice does not fall back to a replay reply", InvalidBotHandChoice),
    ("Replay hand choice uses the recorded target", () => ReplayHandChoice(1)),
    ("Missing replay hand reply leaves the selection pending", () => ReplayHandChoice(null)),
    ("Explicit replay target ID 0 stays distinct from a missing reply", () => ReplayHandChoice(0)),
    ("Invalid replay target is rejected", () => ReplayHandChoice(-1)),
    ("Greedy server turn emits HT and replays the selected order", () => ServerHandChoice(neural: false)),
    ("Neural server turn emits HT and replays the selected order", () => ServerHandChoice(neural: true)),
    ("GetAdjacentCards follows physical location and includeCovert", AdjacentCardsContract),
    ("MakeVeteran applies the template and dispatches the explicit target", VeteranTemplateContract),
    ("MakeVeteran rejects ineligible and repeated promotions", VeteranEligibility),
    ("MakeVeteran preserves temporary and unrelated source state", VeteranBuffState),
    ("MakeVeteran grants Fury quota only under the Blueprint conditions", VeteranFuryQuota),
    ("MakeVeteran refreshes Guard only without a received Guard ability", VeteranGuardState),
    ("MakeVeteran sends veteran, ability and subject events in order", VeteranEventOrder),
    ("Veteran spawning promotes before copying the spawner's attack", VeteranSpawnOrder),
    ("Veteran temporary rollback preserves same-source permanent buffs", VeteranTemporaryRollback),
    ("Veteran registered turn-end expiry preserves the promoted base", VeteranTurnEndExpiry),
    ("Veteran suppression and return to hand reset the complete lifecycle", VeteranSuppressionAndBounce),
    ("Frontline source buffs survive turn end until their removal event", FrontlineBuffLifecycle),
    ("Range and exileNation instance values drive combat, snapshots and reset", RangeAndExileNationContract),
    ("Blueprint member reads expose hand-target wait and enemy-target tax", BlueprintMemberMapping),
};
if (args.Length > 1)
{
    cases.Add(("Pinned replay 310284 retains frontline buffs and matches 93 HQ checks", () => VeteranReplayContract(310284)));
    cases.Add(("Pinned diagnostic replay 955337 promotes Ace of Spades and matches 16 HQ checks", () => VeteranReplayContract(955337)));
}

int failed = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{cases.Count - failed}/{cases.Count} bot regression checks passed.");
return failed == 0 ? 0 : 1;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

(MatchEngine Engine, CardInstance Selecting, CardInstance Order) HandScenario()
{
    var engine = new MatchEngine(db, [], [], seed: 51);
    engine.Start();
    engine.State.ActiveSide = Side.Left;
    engine.State.SetKredits(Side.Left, 40);
    var order = engine.State.Create(Order, Side.Left, CardLocation.HandLeft, 0);
    var selecting = engine.State.Create(Gordon, Side.Left, CardLocation.HandLeft, 1);
    return (engine, selecting, order);
}

void BotHandChoice()
{
    var (engine, selecting, order) = HandScenario();
    var cheap = engine.State.Create("card_event_pams", Side.Left, CardLocation.HandLeft, 2);
    var unit = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.HandLeft, 3);
    int replayReads = 0, botChoices = 0;
    engine.PickHandTargetId = (_, _) => { replayReads++; return null; };
    engine.ChooseHandTarget = (_, candidates) =>
    {
        botChoices++;
        Check(!candidates.Contains(unit), "Gordon must only offer orders");
        return candidates.OrderByDescending(c => c.EffectiveKreditCost).First();
    };

    Check(engine.PlayCard(selecting), "Gordon must be playable");
    Check(botChoices == 1 && replayReads == 0,
        $"Expected one bot choice without reading replay; bot={botChoices}, replay={replayReads}");
    Check(engine.LastHandTarget == new MatchEngine.HandTargetRecord(selecting.CardId, order.CardId),
        "The chosen order must be recorded for HT");
    Check(order.Location == CardLocation.DeckLeft && engine.State.Deck(Side.Left)[0] == order,
        "The selected order must move to the top of the deck");
    Check(cheap.Location == CardLocation.HandLeft && unit.Location == CardLocation.HandLeft,
        "Unselected cards must stay in hand");
    engine.Api.DrawCards(Side.Left, 1);
    Check(order.Location == CardLocation.HandLeft && order.EffectiveKreditCost == 0,
        "Drawing the selected order must apply Gordon's cost reduction");
}

void EmptyBotHandChoice()
{
    var (engine, selecting, order) = HandScenario();
    engine.State.Move(order, CardLocation.DeckLeft);
    engine.PickHandTargetId = (_, _) => throw new InvalidOperationException("Bot selection read stale replay state");
    engine.ChooseHandTarget = (_, _) => throw new InvalidOperationException("Empty candidates must not ask for a card");

    Check(engine.PlayCard(selecting), "Gordon must be playable without a valid hand target");
    Check(engine.LastHandTarget == new MatchEngine.HandTargetRecord(selecting.CardId, 0),
        "No valid candidates must produce the explicit empty HT reply");
}

void InvalidBotHandChoice()
{
    var (engine, selecting, order) = HandScenario();
    engine.PickHandTargetId = (_, _) => throw new InvalidOperationException("Invalid bot choice fell back to replay");
    engine.ChooseHandTarget = (_, _) => selecting;

    Check(engine.PlayCard(selecting), "Gordon must be playable");
    Check(engine.LastHandTarget is null && order.Location == CardLocation.HandLeft,
        "A card outside the legal candidates must not resolve the hand selection");
}

void ReplayHandChoice(int? reply)
{
    var (engine, selecting, order) = HandScenario();
    int reads = 0;
    engine.PickHandTargetId = (_, _) =>
    {
        reads++;
        return reply == 1 ? order.CardId : reply;
    };

    Check(engine.PlayCard(selecting), "Gordon must be playable");
    Check(reads == 1, "Replay must read exactly one reply");
    if (reply == 1)
    {
        Check(order.Location == CardLocation.DeckLeft && engine.LastHandTarget?.ChosenCardId == order.CardId,
            "Recorded target must resolve the original choice");
    }
    else if (reply == 0)
    {
        Check(order.Location == CardLocation.HandLeft && engine.LastHandTarget?.ChosenCardId == 0,
            "Explicit empty reply must be recorded without selecting a card");
    }
    else
    {
        Check(order.Location == CardLocation.HandLeft && engine.LastHandTarget is null,
            "Missing or invalid reply must not choose a fallback card");
    }
}

void ServerHandChoice(bool neural)
{
    string unit = db.All.First(c => c.IsUnit && c.Kredits is > 0 and <= 1 && c.Defense > 0
        && c.ExternalCalls.Count == 0 && db.DeckCodeFor(c.Name) is not null).Name;
    var snapshot = new ServerMatchSnapshot(123, 2, 10, BotPlayerId,
        [
            new(1, false, "board_hqleft", 0, "card_location_london"),
            new(41, false, "board_hqright", 0, "card_location_london"),
            new(2, false, "deck_left", 4, unit),
            new(45, false, "deck_right", 5, unit),
            new(42, false, "hand_right", 0, Gordon),
            new(43, false, "hand_right", 1, Order),
        ],
        [
            new(1, "XActionStartOfTurn", 10, new Dictionary<string, string> { ["side"] = "left", ["40"] = "20" }, 1),
            new(2, "XActionEndOfTurn", 10, new Dictionary<string, string> { ["side"] = "left", ["40"] = "20" }, 1),
            new(3, "XActionStartOfTurn", BotPlayerId, new Dictionary<string, string> { ["side"] = "right" }, 2),
            new(4, "XActionCheat", BotPlayerId, new Dictionary<string, string>
                { ["0"] = "SetKredits", ["1"] = "right", ["2"] = db.Find(Gordon)!.Kredits.ToString() }, 2),
        ]) { NextActionId = 5 };
    var model = neural ? new NnModel
    {
        Dim = StateEncoder.Dim, Hidden = 1, CardDim = StateEncoder.CardDim,
        Zones = StateEncoder.Zones, PerSide = StateEncoder.PerSide,
        Mean = new float[StateEncoder.Dim], Std = Enumerable.Repeat(1f, StateEncoder.Dim).ToArray(),
        W1 = new float[StateEncoder.Dim], B1 = [0], W2 = [0],
    } : null;
    var service = new BotTurnService(db, Side.Right, BotPlayerId, model, neural ? vecs : null);
    service.LoadDeckCodeTable(db.DeckCodeIds);
    var result = service.DecideTurn(snapshot);

    Check(result.Divergence?.Detected == false, "Synthetic history must reconstruct cleanly");
    int playedIndex = result.Actions.ToList().FindIndex(a => a.ActionType == "PC" && a.ActionData["0"] == "42");
    Check(playedIndex >= 0, "Bot must play Gordon: " + string.Join("; ", result.Log));
    var reply = result.Actions.ElementAtOrDefault(playedIndex + 1);
    Check(reply?.ActionType == "HT" && reply.ActionData["0"] == "42" && reply.ActionData["1"] == "43",
        "Gordon's PC must be followed immediately by HT selecting the order");
    Check(result.Actions.Select(a => a.ActionId).SequenceEqual(Enumerable.Range(5, result.Actions.Count)),
        "Adding HT must keep action IDs contiguous");
    Check(result.State?.ById(43)?.Location == CardLocation.DeckRight,
        "Live bot state must contain the chosen order in its deck");
    if (neural)
    {
        Check(result.Log.Any(line => line.Contains("NN 决策")) && !result.Log.Any(line => line.Contains("改用贪心")),
            "Neural trials must run without falling back to greedy");
    }

    // Replay the emitted PC/HT on a fresh engine, with no bot hook installed.
    var replay = ServerReplayBridge.ToReplayData(snapshot with
    {
        Actions = snapshot.Actions.Concat(result.Actions.TakeWhile(a => a.ActionType != "XActionEndOfTurn")).ToArray(),
    });
    var runner = new ReplayRunner(db)
    {
        InitialLocations = replay.Cards.ToDictionary(c => c.CardId, c => c.Location),
        InitialLocationNumbers = replay.Cards.ToDictionary(c => c.CardId, c => c.LocationNumber),
    };
    var report = runner.Run(replay, verbose: false);
    Check(report.AppliedCount == report.TotalActions, "Generated PC/HT must both apply during replay");
    Check(report.Engine?.State.ById(43)?.Location == CardLocation.DeckRight,
        "Historical replay must honor HT without installing a bot selector");
    Check(report.Engine?.State.Random.State == result.State?.Random.State,
        "Bot choices and replay replies must consume the same random stream");
}

void AdjacentCardsContract()
{
    var engine = new MatchEngine(db, [], [], seed: 83);
    engine.Start();
    var state = engine.State;
    var source = state.CreateWithId("card_event_monty", Side.Left, 60,
        CardLocation.HandLeft, 0);
    var target = state.CreateWithId("card_unit_5th_regiment", Side.Left, 61,
        CardLocation.BoardHqLeft, 2);
    // The Blueprint query is physical-location based, so this opposite-owner
    // card is still a valid neighbor in the same support line.
    var lower = state.CreateWithId("card_unit_5th_regiment", Side.Right, 62,
        CardLocation.BoardHqLeft, 1);
    var hidden = state.CreateWithId("card_unit_5th_regiment", Side.Right, 63,
        CardLocation.BoardHqLeft, 3);
    hidden.Keywords.Add(Keyword.Covert);
    var nonAdjacent = state.CreateWithId("card_unit_5th_regiment", Side.Left, 64,
        CardLocation.BoardHqLeft, 4);

    var context = new KLink.Bot.Effects.EffectContext
    {
        Engine = engine,
        State = state,
        Self = source,
        Target = target,
        Controller = Side.Left,
    };
    List<CardInstance> Query(bool includeCovert)
    {
        object? result = engine.Api.InvokeByName("GetAdjacentCards", source,
            new object?[] { target, includeCovert }, context, out bool handled);
        Check(handled, "GetAdjacentCards must be dispatched");
        return result as List<CardInstance> ?? throw new InvalidOperationException("Adjacent result is not a card list");
    }

    var visible = Query(includeCovert: false);
    Check(visible.SequenceEqual(new[] { lower }),
        $"Physical adjacent query mismatch: {string.Join(",", visible.Select(c => $"{c.CardId}@{c.Location}#{c.LocationNumber}/{c.Owner}/covert={KLink.Bot.Effects.CardApi.IsUnrevealedCovertCard(c)}"))}");
    var all = Query(includeCovert: true);
    Check(all.SequenceEqual(new[] { lower, hidden }),
        "includeCovert=true must restore the hidden adjacent card in physical order");
    Check(!visible.Contains(nonAdjacent) && !all.Contains(nonAdjacent),
        "Cards beyond one physical slot must not be adjacent");

    var hqEngine = new MatchEngine(db, [], [], seed: 89);
    hqEngine.Start();
    var hqNeighborTarget = hqEngine.State.CreateWithId("card_unit_5th_regiment", Side.Left,
        70, CardLocation.BoardHqLeft, 1);
    var frontlineNeighbor = hqEngine.State.CreateWithId("card_unit_5th_regiment", Side.Right,
        71, CardLocation.BoardHqLeft, 2);
    var hqContext = new KLink.Bot.Effects.EffectContext
    {
        Engine = hqEngine,
        State = hqEngine.State,
        Self = hqNeighborTarget,
        Target = hqNeighborTarget,
        Controller = Side.Left,
    };
    var withHq = hqEngine.Api.InvokeByName("GetAdjacentCards", hqNeighborTarget,
        new object?[] { hqNeighborTarget, false }, hqContext, out _) as List<CardInstance>;
    Check(withHq is not null && withHq.SequenceEqual(
            new[] { hqEngine.State.Hq(Side.Left), frontlineNeighbor }),
        "Native physical-location scan includes the HQ occupying an adjacent slot");
}

MatchEngine VeteranScenario()
{
    var engine = new MatchEngine(db, [], [], seed: 97);
    engine.Start();
    return engine;
}

void VeteranTemplateContract()
{
    var engine = VeteranScenario();
    var source = engine.State.Create("card_event_battle_valor", Side.Left, CardLocation.HandLeft, 0);
    var target = engine.State.Create("card_unit_102_rifles", Side.Left, CardLocation.BoardHqLeft, 1);
    var definition = target.Definition;
    var context = new KLink.Bot.Effects.EffectContext
    {
        Engine = engine, State = engine.State, Self = source, Controller = Side.Left,
    };
    engine.Api.InvokeByName("MakeVeteran", source, new object?[] { target, null }, context, out bool handled);
    Check(handled && target.Attack == 6 && target.Defense == 6 && target.MaxDefense == 6,
        $"102nd Rifles must become 6/6, got {target.Attack}/{target.Defense} max={target.MaxDefense}");
    Check(target.Keywords.Contains(Keyword.Veteran) && engine.Api.JsonGetBool(target, "veteran")
        && target.CustomJson.HasType("veteran", CardJsonType.Boolean), "Promotion must set both veteran representations");
    Check(!target.Keywords.Contains(Keyword.Destruction), "The veteran template must replace the base Destruction flag");
    Check(definition.EffectType == 13 && target.EffectType == 0,
        "Promotion must copy the veteran effectType as well as boolean abilities");
    Check(target.Definition == definition && target.Name == definition.Name
        && !source.Keywords.Contains(Keyword.Veteran), "Promotion must update the explicit target's runtime attributes");
    Check(engine.State.Hand(Side.Left).Any(c => c.Name == "card_event_the_hammer"),
        "102nd Rifles must execute its own OnBecomingVeteran effect");

    var damaged = engine.State.Create("card_unit_641st_rifles", Side.Left, CardLocation.BoardHqLeft, 2);
    damaged.Defense = 4;
    engine.Api.MakeVeteran(damaged);
    Check(damaged.Attack == 8 && damaged.Defense == 1 && damaged.MaxDefense == 1 && damaged.AliveOnBoard,
        "Damaged 641st Rifles must adopt the veteran's 8/1 and maximum defense");
    Check(damaged.HasGuard && damaged.Keywords.Contains(Keyword.Ambush)
        && damaged.Keywords.Contains(Keyword.Destruction) && !damaged.HasBlitz,
        "641st Rifles must gain Ambush/Destruction and lose intrinsic Blitz");
}

void VeteranEligibility()
{
    foreach (var (name, location, defense) in new[]
    {
        ("card_unit_102_rifles", CardLocation.HandLeft, 1),
        ("card_unit_102_rifles", CardLocation.DeckLeft, 1),
        ("card_unit_102_rifles", CardLocation.BoardHqLeft, 0),
        ("card_unit_37mm_m1_aa_gun", CardLocation.BoardHqLeft, 2),
    })
    {
        var engine = VeteranScenario();
        var target = engine.State.Create(name, Side.Left, location, 1);
        target.Defense = defense;
        string before = engine.State.SnapshotJson();
        int actions = engine.State.ActionLog.Count;
        engine.Api.MakeVeteran(target);
        Check(engine.State.SnapshotJson() == before && engine.State.ActionLog.Count == actions,
            $"Ineligible promotion must leave state and actions untouched: {name}@{location}/{defense}");
    }

    var repeated = VeteranScenario();
    var promoted = repeated.State.Create("card_unit_102_rifles", Side.Left, CardLocation.BoardHqLeft, 1);
    repeated.Api.MakeVeteran(promoted);
    promoted.Attack += 2;
    promoted.Defense -= 1;
    string after = repeated.State.SnapshotJson();
    int actionCount = repeated.State.ActionLog.Count;
    repeated.Api.MakeVeteran(promoted);
    Check(repeated.State.SnapshotJson() == after && repeated.State.ActionLog.Count == actionCount,
        "Repeated promotion must neither reset attributes nor repeat triggers");
}

void VeteranBuffState()
{
    var engine = VeteranScenario();
    var target = engine.State.Create("card_unit_matilda_mk_iv", Side.Left, CardLocation.BoardHqLeft, 1);
    var template = db.All.Single(c => c.Name == target.Name + "_vet");
    var source = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.HandLeft, 0);
    engine.Api.ChangeAttack(target, 3, target);
    engine.Api.ChangeDefense(target, 4, target);
    var permanent = target.BuffsBySource[(target.CardId, false)];
    permanent.KreditCost = -1;
    permanent.OperationCost = 2;
    permanent.HeavyArmor = 2;
    permanent.Guard = 1;
    var context = new KLink.Bot.Effects.EffectContext
    {
        Engine = engine, State = engine.State, Self = source, Controller = Side.Left,
    };
    foreach (var (function, amount) in new[]
    {
        ("ChangeAttack", 2), ("ChangeOperationCost", 1), ("ChangeHeavyArmor", 1),
    })
    {
        engine.Api.InvokeByName(function, source,
            new object?[] { target, source.CardId, amount, 0, false, false, false }, context, out bool handled);
        Check(handled, $"Temporary {function} must be dispatched");
    }
    var temporary = target.BuffsBySource[(source.CardId, true)];
    target.ReceivedAbilitiesFromCards["blitz"] = [source.CardId];
    target.CustomJson.Set("unrelated", "keep", CardJsonType.String);
    target.Keywords.Add(Keyword.Alpine);
    target.Defense -= 2;
    target.AttacksThisTurn = 1;
    target.SetRemainingAttacks(0);
    target.HasAttackedThisTurn = true;
    target.RecalculateStats();

    engine.Api.MakeVeteran(target);
    Check(target.Attack == template.Attack + 2 && target.Defense == template.Defense
        && target.MaxDefense == template.Defense, "veteranSet must replace permanent stats and retain temporary attack");
    Check(ReferenceEquals(permanent, target.BuffsBySource[(target.CardId, false)])
        && permanent.Attack == 0 && permanent.Defense == 0 && permanent.OperationCost == 0 && permanent.HeavyArmor == 0
        && permanent.KreditCost == -1 && permanent.Guard == 1,
        "veteranSet must clear only the four permanent numeric fields, including a self-source entry");
    Check(ReferenceEquals(temporary, target.BuffsBySource[(source.CardId, true)])
        && temporary.Attack == 2 && temporary.OperationCost == 1 && temporary.HeavyArmor == 1,
        "Temporary source entries must survive promotion");
    Check(target.OperationCost == template.OperationCost + 1 && target.HeavyArmor == template.HeavyArmor + 1
        && target.KreditCost == target.Definition.Kredits - 1, "Derived stats must use the promoted base and remaining buffs");
    Check(target.HasBlitz && target.ReceivedAbilitiesFromCards["blitz"].SequenceEqual(new[] { source.CardId })
        && target.Keywords.Contains(Keyword.Alpine) && target.CustomJson["unrelated"] == "keep",
        "Received abilities, unrelated keywords and JSON must survive promotion");
    Check(target.AttacksThisTurn == 1 && target.RemainingAttacks == 0 && target.HasAttackedThisTurn,
        "Promotion must preserve consumed attack quota without newly gained Fury");
    target.RecalculateStats();
    Check(target.OperationCost == template.OperationCost + 1 && target.HeavyArmor == template.HeavyArmor + 1,
        "Recalculation must retain the veteran base");
    engine.Api.ResetCardInBattle(target);
    Check(target.OperationCost == target.Definition.OperationCost && target.HeavyArmor == target.Definition.HeavyArmor,
        "Reset must restore the ordinary operation cost and heavy armor");
}

void VeteranFuryQuota()
{
    const string name = "card_unit_the_silent_seventh";
    Check(!db.Require(name).Keywords.Contains(Keyword.Fury) && db.Require(name + "_vet").Keywords.Contains(Keyword.Fury),
        "Fury fixture must gain intrinsic Fury on promotion");
    foreach (var (attacks, left, alreadyHasFury, expected) in new[]
    {
        (0, 1, false, 2), (0, 0, false, 0), (1, 0, false, 1),
        (1, -2, false, -1), (2, 0, false, 0), (2, 3, false, 3),
        (0, 2, true, 2), (1, 0, true, 0),
    })
    {
        var engine = VeteranScenario();
        var target = engine.State.Create(name, Side.Left, CardLocation.BoardHqLeft, 1);
        if (alreadyHasFury) target.ReceivedAbilitiesFromCards["fury"] = [123];
        target.AttacksThisTurn = attacks;
        target.SetRemainingAttacks(left);
        engine.Api.MakeVeteran(target);
        Check(target.HasFury && target.AttacksThisTurn == attacks && target.RemainingAttacks == expected,
            $"Fury quota mismatch: attacks={attacks}, left={left}, existing={alreadyHasFury}, actual={target.RemainingAttacks}");
    }

    var losing = VeteranScenario();
    var unit = losing.State.Create("card_unit_641st_rifles", Side.Left, CardLocation.BoardHqLeft, 1);
    unit.Keywords.Add(Keyword.Fury);
    unit.SetRemainingAttacks(2);
    losing.Api.MakeVeteran(unit);
    Check(!unit.HasFury && unit.RemainingAttacks == 2, "Removing raw Fury must preserve native attackLeft");
}

void VeteranGuardState()
{
    foreach (bool receivedGuard in new[] { false, true })
    {
        var engine = VeteranScenario();
        var target = engine.State.Create("card_unit_16th_infantry_brigade", Side.Left, CardLocation.BoardHqLeft, 1);
        var neighbor = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.BoardHqLeft, 2);
        target.ReceivedAbilitiesFromCards["smokescreen"] = [123];
        if (receivedGuard) target.ReceivedAbilitiesFromCards["guard"] = [124];
        engine.Api.MakeVeteran(target);
        Check(target.HasGuard, "Veteran 16th Infantry Brigade must have effective Guard");
        Check(target.Keywords.Contains(Keyword.Guard) == !receivedGuard && neighbor.IsBeingGuarded == !receivedGuard,
            "Only a raw Guard transition without received Guard should refresh adjacent caches");
        Check(target.HasSmokescreen == receivedGuard,
            "Gaining raw Guard must remove received Smokescreen; received Guard must skip this branch");
    }

    var removal = VeteranScenario();
    var guard = removal.State.Create("card_unit_102_rifles", Side.Left, CardLocation.BoardHqLeft, 1);
    var guarded = removal.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.BoardHqLeft, 2);
    guard.Keywords.Add(Keyword.Guard);
    removal.Api.UpdateGuarded(guard.Location);
    Check(guarded.IsBeingGuarded, "Guard removal fixture must begin guarded");
    removal.Api.MakeVeteran(guard);
    Check(!guard.HasGuard && !guarded.IsBeingGuarded, "Losing raw Guard must refresh adjacent caches");
}

void VeteranEventOrder()
{
    var engine = VeteranScenario();
    var target = engine.State.Create("card_unit_102_rifles", Side.Left, CardLocation.BoardHqLeft, 1);
    engine.State.Create("card_unit_6th_brigade_nz", Side.Left, CardLocation.BoardHqLeft, 2);
    string watcher = db.All.First(c => c.IsRegisteredFor("OnOtherCardAbilitiesChanged")
        && KismetLibrary.Default?.FindProgram(c.Name, "OnOtherCardAbilitiesChanged") is not null).Name;
    engine.State.Create(watcher, Side.Left, CardLocation.BoardHqLeft, 3);
    var trace = new List<string>();
    engine.Api.TriggerTrace = trace;
    engine.Api.MakeVeteran(target);
    int veteran = trace.FindIndex(t => t.StartsWith("OnOtherCardBecomingVeteran → ")
        && t.Contains($"eventCard={target.Name}#{target.CardId}"));
    int abilities = trace.FindIndex(t => t.StartsWith("OnOtherCardAbilitiesChanged → ")
        && t.Contains($"eventCard={target.Name}#{target.CardId}"));
    int own = trace.FindIndex(t => t.StartsWith($"OnBecomingVeteran → {target.Name}#{target.CardId}"));
    Check(veteran >= 0 && abilities > veteran && own > abilities,
        "Expected veteran observers, ability observers, then subject event: " + string.Join("; ", trace));
    Check(!trace.Any(t => t.StartsWith("OnAfterGainDefense → ") || t.StartsWith("OnAfterDefenseIsSet → ")),
        "veteranSet must not dispatch permanent-gain or SetValue defense triggers");
}

void VeteranSpawnOrder()
{
    var engine = VeteranScenario();
    var spawner = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.BoardFrontline, 0, isGold: true);
    spawner.Attack = 11;
    var context = new KLink.Bot.Effects.EffectContext
    {
        Engine = engine, State = engine.State, Self = spawner, Controller = Side.Left,
    };
    object? result = engine.Api.InvokeByName("SpawnNextToCard", spawner,
        new object?[] { "card_unit_269th_rifles", spawner.CardId, (int)Side.Left, null, true, false, 0, true },
        context, out bool handled);
    var spawned = result is int id ? engine.State.ById(id) : null;
    Check(handled && spawned is not null && spawned.IsGold && spawned.Keywords.Contains(Keyword.Veteran)
        && spawned.Attack == 11 && spawned.Defense == db.Require("card_unit_269th_rifles_vet").Defense,
        "SpawnNextToCard must apply the veteran template before copying the spawner's attack");
}

void VeteranChange(MatchEngine engine, CardInstance source, CardInstance target,
    string function, int amount, int changeType)
{
    var context = new KLink.Bot.Effects.EffectContext
    {
        Engine = engine, State = engine.State, Self = source, Controller = source.Owner,
    };
    engine.Api.InvokeByName(function, source,
        new object?[] { target, source.CardId, amount, changeType, false, false, false },
        context, out bool handled);
    Check(handled, $"{function} must be dispatched");
}

(MatchEngine Engine, CardInstance Target, CardInstance Source, CardDefinition Template) VeteranLifecycle()
{
    var engine = VeteranScenario();
    var target = engine.State.Create("card_unit_matilda_mk_iv", Side.Left, CardLocation.BoardHqLeft, 1);
    var source = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.HandLeft, 0);
    foreach (var (function, amount) in new[]
    {
        ("ChangeOperationCost", 1), ("ChangeHeavyArmor", 1),
        ("ChangeKreditCost", -1),
    })
        VeteranChange(engine, source, target, function, amount, 0);
    engine.Api.AddAttackUntilEndOfTurn(target, source, 2);
    engine.Api.ChangeAttack(target, 5, source);
    engine.Api.MakeVeteran(target);
    var template = db.Require(target.Name + "_vet");
    Check(target.Attack == template.Attack + 2 && target.Defense == template.Defense
        && target.OperationCost == template.OperationCost + 1
        && target.HeavyArmor == template.HeavyArmor + 1,
        "Promotion must retain temporary numeric modifiers and remove the old permanent attack");
    // A fresh permanent modifier from the same source must survive later temp removal.
    engine.Api.ChangeAttack(target, 3, source);
    return (engine, target, source, template);
}

void VeteranTemporaryRollback()
{
    var (engine, target, source, template) = VeteranLifecycle();
    VeteranChange(engine, source, target, "ChangeAttack", 99, 4);
    Check(target.Attack == template.Attack + 3
        && target.BuffsBySource[(source.CardId, false)].Attack == 3
        && target.BuffsBySource[(source.CardId, true)].OperationCost == 1,
        "Attack rollback must use its recorded amount and retain the other temporary fields");
    VeteranChange(engine, source, target, "ChangeHeavyArmor", 99, 4);
    Check(target.HeavyArmor == template.HeavyArmor && target.OperationCost == template.OperationCost + 1,
        "Armor rollback must retain the veteran base and pending operation-cost modifier");
    VeteranChange(engine, source, target, "ChangeOperationCost", 99, 4);
    Check(target.OperationCost == template.OperationCost && target.KreditCost == target.Definition.Kredits - 1,
        "Operation-cost rollback must leave the unrelated temporary deployment-cost modifier");
    engine.Api.RemoveTemporaryBuffs();
    Check(target.Attack == template.Attack + 3 && target.HeavyArmor == template.HeavyArmor
        && target.OperationCost == template.OperationCost && target.KreditCost == target.Definition.Kredits
        && !target.BuffsBySource.Keys.Any(k => k.Temporary),
        "End-of-turn cleanup must not subtract an explicitly removed modifier twice");
    Check(source.CardsBuffedByThisCard.Contains(target.CardId),
        "The source must still track its surviving permanent attack modifier");
}

void VeteranTurnEndExpiry()
{
    var (engine, target, source, template) = VeteranLifecycle();
    target.Defense--;
    int defense = target.Defense;
    int logStart = engine.State.ActionLog.Count;
    engine.EndTurn(Side.Left);
    Check(engine.State.ActionLog.Skip(logStart).Any(a => a.SubActions.Any(s => s.Name == "ZActionGainAttack"
            && s.Values.Any(v => v.Name == "instigatorID" && v.Value == source.CardId)
            && s.Values.Any(v => v.Name == "gained" && v.Value == -2)
            && s.Values.Any(v => v.Name == "newAttackValue" && v.Value == target.Attack))),
        "Registered turn-end removal must notify the attack change with the recorded amount");
    Check(target.Attack == template.Attack + 3 && target.Defense == defense
        && target.MaxDefense == template.Defense
        && target.OperationCost == template.OperationCost + 1 && target.HeavyArmor == template.HeavyArmor + 1
        && target.Keywords.Contains(Keyword.Veteran) && engine.Api.JsonGetBool(target, "veteran")
        && target.BuffsBySource[(source.CardId, true)].Attack == 0
        && engine.State.GetBuffsToRemoveEndOfTurn().Count == 0,
        "Turn end must expire only the registered attack, retaining promotion, damage and other source fields");
    engine.Api.RemoveTemporaryBuffs();
    Check(target.Attack == template.Attack + 3
        && target.BuffsBySource[(source.CardId, false)].Attack == 3,
        "Repeated cleanup must retain the same-source permanent modifier");
}

void VeteranSuppressionAndBounce()
{
    foreach (bool suppress in new[] { false, true })
    {
        var (engine, target, source, _) = VeteranLifecycle();
        target.Defense = 1;
        if (suppress)
        {
            engine.Api.SuppressUnit(target);
            Check(target.IsSuppressed && !target.Keywords.Contains(Keyword.Veteran)
                && !target.CustomJson.ContainsKey("veteran") && target.Snapshot().VeteranOperationCost is null
                && target.Snapshot().VeteranHeavyArmor is null && target.EffectType == 0
                && target.Attack == target.Definition.Attack && target.Defense == 1
                && target.MaxDefense == target.Definition.Defense
                && target.OperationCost == target.Definition.OperationCost && target.HeavyArmor == 0,
                "Suppression must clear veteran bases and numeric modifiers without healing damage");
            engine.Api.RemoveTemporaryBuffs();
            Check(target.Attack == target.Definition.Attack && target.Defense == 1
                && target.HeavyArmor == 0, "Expiry after suppression must not subtract stripped modifiers again");
        }
        var context = new KLink.Bot.Effects.EffectContext
        {
            Engine = engine, State = engine.State, Self = source, Controller = Side.Left,
        };
        engine.Api.InvokeByName("MoveUnitFromBoardToOwnersHand", source,
            new object?[] { target, source.CardId }, context, out bool handled);
        Check(handled && target.Location == CardLocation.HandLeft && !target.IsSuppressed
            && !target.Keywords.Contains(Keyword.Veteran) && !target.CustomJson.ContainsKey("veteran")
            && target.Snapshot().VeteranOperationCost is null && target.Snapshot().VeteranHeavyArmor is null
            && target.Attack == target.Definition.Attack && target.Defense == target.Definition.Defense
            && target.MaxDefense == target.Definition.Defense && target.EffectType == target.Definition.EffectType
            && target.OperationCost == target.Definition.OperationCost
            && target.HeavyArmor == target.Definition.HeavyArmor && target.BuffsBySource.Count == 0
            && !source.CardsBuffedByThisCard.Contains(target.CardId),
            $"Return to hand must restore ordinary attributes and detach source links (suppressed={suppress})");
        engine.Api.RemoveTemporaryBuffs();
        Check(target.Attack == target.Definition.Attack && target.Defense == target.Definition.Defense,
            "Cleanup after return to hand must not resurrect stale modifiers");
    }
}

void FrontlineBuffLifecycle()
{
    var engine = VeteranScenario();
    var brewster = engine.State.Create("card_unit_brewster_f2a", Side.Left, CardLocation.BoardHqLeft, 1);
    engine.State.Move(brewster, CardLocation.BoardFrontline, 0);
    Check(brewster.Attack == 2 && brewster.BuffsBySource[(brewster.CardId, true)].Attack == 1,
        "Entering the frontline must apply Brewster's self-source temporary attack slot");
    int logStart = engine.State.ActionLog.Count;
    engine.EndTurn(Side.Left);
    Check(brewster.Attack == 2 && brewster.BuffsBySource[(brewster.CardId, true)].Attack == 1,
        "A source buff must not expire merely because its changeType is tempBuffGive");
    Check(!engine.State.ActionLog.Skip(logStart).Any(a => a.SubActions.Any(s => s.Name == "ZActionGainAttack"
            && s.Values.Any(v => v.Name == "instigatorID" && v.Value == brewster.CardId))),
        "Turn end must not announce removal for an unregistered source slot");
    engine.State.Move(brewster, CardLocation.BoardHqLeft, 1);
    Check(brewster.Attack == 1 && !brewster.BuffsBySource.ContainsKey((brewster.CardId, true)),
        "Leaving the frontline must explicitly remove the recorded attack modifier");
}

void RangeAndExileNationContract()
{
    var engine = VeteranScenario();
    var attacker = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.BoardHqLeft, 1);
    var defender = engine.State.Create("card_unit_5th_regiment", Side.Right, CardLocation.BoardHqRight, 1);
    Check(attacker.Definition.Range < 2 && attacker.Range == attacker.Definition.Range
        && attacker.ExileNation == 0, "Base range/exileNation must initialize from the card template");
    Check(!MatchEngine.CanReachAcrossFrontline(attacker, defender),
        "A base-range infantry cannot attack across the frontline");

    attacker.Range = 3;
    attacker.ExileNation = 2;
    Check(MatchEngine.CanReachAcrossFrontline(attacker, defender),
        "The mutable instance range must drive the cross-frontline check");
    var snapshot = attacker.Snapshot();
    Check(snapshot.Range == 3 && snapshot.ExileNation == 2,
        "Snapshots must expose mutable range and exileNation");

    engine.Api.SuppressUnit(attacker);
    Check(attacker.IsSuppressed && attacker.Range == attacker.Definition.Range
        && attacker.ExileNation == 0 && attacker.Snapshot().Range == attacker.Definition.Range
        && attacker.Snapshot().ExileNation == 0,
        "Suppression must restore the template range and clear exileNation");

    attacker.Range = 4;
    attacker.ExileNation = 1;
    engine.Api.ResetCardInBattle(attacker);
    Check(attacker.Range == attacker.Definition.Range && attacker.ExileNation == 0,
        "Reset must restore the template range and clear exileNation");
}

void BlueprintMemberMapping()
{
    var read = typeof(KismetVm).GetMethod("GetMember",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    Check(read is not null, "KismetVm.GetMember must exist for the member contract");
    object? Read(CardInstance card, string member)
        => read!.Invoke(null, new object?[] { card, member });

    var engine = VeteranScenario();
    var waitingName = db.All.First(c => c.WaitForHandTarget).Name;
    var waiting = engine.State.Create(waitingName, Side.Left, CardLocation.HandLeft, 0);
    var normal = engine.State.Create("card_unit_5th_regiment", Side.Left, CardLocation.HandLeft, 1);
    Check(Equals(Read(waiting, "waitWithOnOtherCardPlayedUntilHandTargetSelected"), true),
        "Card IR must see the CDO wait-for-hand-target flag");
    Check(Equals(Read(normal, "waitWithOnOtherCardPlayedUntilHandTargetSelected"), false),
        "A card without the CDO flag must read false");
    normal.KreditsTaxAsEnemyTarget = 3;
    Check(Equals(Read(normal, "KreditsTax_AsEnemyTarget"), 3)
        && normal.Snapshot().KreditsTaxAsEnemyTarget == 3,
        "Card IR and snapshots must see the native enemy-target tax field");
}

void VeteranReplayContract(int matchId)
{
    string root = Path.GetFullPath(args[1]);
    var replay = ReplayData.Load(Path.Combine(root, $"replay-{matchId}.json"),
        Path.Combine(root, $"replay-{matchId}.actions.json"));
    var observed = new List<(int ActionId, CardSnapshot Card, int RemainingAttacks)>();
    var seen = new HashSet<int>();
    int frontlineAttack = -1;
    var runner = new ReplayRunner(db) { IdentityCorrection = true, KreditSlotOnDuplicateStart = true };
    var report = runner.Run(replay, verbose: false, (action, state) =>
    {
        foreach (var card in state.AllCards.Where(c => c.Keywords.Contains(Keyword.Veteran)))
            if (seen.Add(card.CardId)) observed.Add((action.ActionId, card.Snapshot(), card.RemainingAttacks));
        if (matchId == 310284 && action.ActionId == 48) frontlineAttack = state.ById(11001)?.Attack ?? -1;
    });
    int expectedChecks = matchId == 310284 ? 93 : 16;
    Check(report.HqChecked == expectedChecks && report.HqMatched == expectedChecks,
        $"Replay {matchId} HQ checks differ: {report.HqMatched}/{report.HqChecked}, first={report.FirstMismatch}");
    var expected = matchId == 310284
        ? new[] { (Action: 35, Id: 65, Name: "card_unit_7_schutzen", Attack: 5, Defense: 5, Used: 1, Left: 0),
                  (Action: 55, Id: 32, Name: "card_unit_ace_of_spades", Attack: 8, Defense: 8, Used: 0, Left: 1) }
        : new[] { (Action: 10, Id: 72, Name: "card_unit_ace_of_spades", Attack: 8, Defense: 8, Used: 0, Left: 1) };
    Check(observed.Count == expected.Length, $"Replay {matchId} promotion count differs: {observed.Count}");
    foreach (var e in expected)
    {
        var p = observed.Single(x => x.Card.CardId == e.Id);
        Check(p.ActionId == e.Action && p.Card.Name == e.Name && p.Card.Attack == e.Attack
            && p.Card.Defense == e.Defense && p.Card.MaxDefense == e.Defense
            && p.Card.OperationCost == 1 && p.Card.AttacksThisTurn == e.Used
            && p.RemainingAttacks == e.Left && p.Card.CustomJsonTypes["veteran"] == CardJsonType.Boolean
            && (e.Name != "card_unit_ace_of_spades" || p.Card.Keywords.Contains(Keyword.Guard)),
            $"Replay {matchId} promotion #{e.Action}, card {e.Id}, runtime state differs");
    }
    if (matchId == 310284) Check(frontlineAttack == 2, "Brewster's frontline bonus must remain active for attack #48");
    // These are wire HQ assertions plus kernel-state regressions, not independent
    // client board-property comparisons. See ReplayAudit/README.md for boundaries.
}
