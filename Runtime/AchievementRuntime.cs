using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Terraria;
using Terraria.Achievements;
using Terraria.GameContent.Achievements;
using Terraria.ID;
using EyesAchievements.Core;

namespace EyesAchievements.Runtime;

internal static class AchievementRuntime
{
    internal static readonly object Gate = new();
    internal static readonly Dictionary<string, ScopedAchievement> Definitions = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string, Guid, Guid), LiveProgress> Live = new();
    private static readonly Dictionary<string, PendingPresentation> Presentations = new(StringComparer.Ordinal);
    private static readonly Dictionary<(AchievementEvent, int), List<(ScopedAchievement Definition, string Condition)>> Events = new();
    internal static event Action<AchievementCompletion>? Completed;
    internal static event Action<AchievementChange>? Changed;
    internal static event Action<AchievementContext>? ContextReady;
    internal static void MainThread()
    {
        if (!Terraria.Program.IsMainThread) throw new InvalidOperationException("Achievement APIs must be called on the game thread.");
    }
    internal static void Register(ScopedAchievement definition)
    {
        if (Definitions.Count >= 4096 || !Definitions.TryAdd(definition.Id, definition)) throw new InvalidOperationException($"Duplicate or excessive achievement registration: {definition.Id}.");
        NativePersistence.UpdateKeys(Definitions.Keys);
    }
    internal static void Index(ScopedAchievement definition)
    {
        foreach (var condition in definition.Conditions)
            if (condition.Event is { } kind)
                foreach (int id in condition.ContentIds!)
                {
                    if (!Events.TryGetValue((kind, id), out var matches)) Events.Add((kind, id), matches = new());
                    matches.Add((definition, condition.Key));
                }
    }
    internal static void ClearSession()
    {
        lock (Gate)
        {
            var instances = Live.Values.ToArray();
            Live.Clear();
            Presentations.Clear();
            foreach (var progress in instances) progress.Definition.Release(progress.Achievement);
        }
    }
    internal static void Unload()
    {
        ClearSession();
        Definitions.Clear(); Events.Clear(); Completed = null; Changed = null; ContextReady = null;
    }
    internal static void ForgetPlayer(Guid userId, Guid playerId)
    {
        lock (Gate)
        {
            var keys = Live.Where(pair => pair.Value.Definition.Scope == AchievementScope.User ? pair.Key.Item2 == userId : pair.Value.Definition.Scope is AchievementScope.Player or AchievementScope.PlayerWorld && pair.Key.Item2 == playerId).Select(pair => pair.Key).ToArray();
            foreach (var key in keys)
            {
                var progress = Live[key]; Live.Remove(key); progress.Definition.Release(progress.Achievement);
            }
        }
    }

    internal static AchievementProgress Get(string key, AchievementContext context, bool setup = false)
    {
        lock (Gate) return GetCore(key, context, setup);
    }
    private static AchievementProgress GetCore(string key, AchievementContext context, bool setup)
    {
        if (!setup) MainThread();
        if (!Definitions.TryGetValue(key, out var definition)) return AchievementProgress.Missing(ProgressStatus.UnknownAchievement);
        if (!Resolve(definition, context, out var store, out var trust)) return AchievementProgress.Missing(ProgressStatus.ContextUnavailable);
        var live = GetLive(definition, context, store!);
        if (live.ClientReported) trust = ProgressTrust.ClientReported;
        var states = Capture(live);
        var conditions = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, ConditionProgress>(StringComparer.Ordinal);
        foreach (var condition in definition.Conditions)
        {
            var actual = live.Achievement.GetCondition(condition.Key);
            conditions[condition.Key] = states[condition.Key].ToString(Formatting.None);
            double? current = actual switch { CustomIntCondition number => number.Value, CustomFloatCondition number => number.Value, CustomFlagCondition => actual.IsCompleted ? 1 : 0, _ => null };
            values[condition.Key] = new(actual.IsCompleted, current, condition.Target);
        }
        return new(ProgressStatus.Available, live.Achievement.IsCompleted, trust, new ReadOnlyDictionary<string, string>(conditions), new ReadOnlyDictionary<string, ConditionProgress>(values));
    }

    internal static ProgressStatus Status(string key, AchievementContext context, out bool completed)
    {
        lock (Gate) return StatusCore(key, context, out completed);
    }
    private static ProgressStatus StatusCore(string key, AchievementContext context, out bool completed)
    {
        MainThread(); completed = false;
        if (!Definitions.TryGetValue(key, out var definition)) return ProgressStatus.UnknownAchievement;
        if (!Resolve(definition, context, out var store, out _)) return ProgressStatus.ContextUnavailable;
        completed = GetLive(definition, context, store!).Achievement.IsCompleted;
        return ProgressStatus.Available;
    }

    internal static bool Mutate(string key, string condition, AchievementContext context, Action<AchievementCondition> action, bool clientReported = false)
    {
        lock (Gate) return MutateCore(key, condition, context, action, clientReported);
    }
    private static bool MutateCore(string key, string condition, AchievementContext context, Action<AchievementCondition> action, bool clientReported)
    {
        MainThread();
        if (!Definitions.TryGetValue(key, out var definition)) throw new KeyNotFoundException($"Unknown achievement {key}.");
        if (!Resolve(definition, context, out var store, out var trust)) throw new InvalidOperationException("The requested achievement context has not been loaded or synchronized.");
        if (!CanWrite(definition, context)) throw new InvalidOperationException("This achievement is controlled by the server; mutate it on the server or use an explicitly enabled observation helper.");
        var live = GetLive(definition, context, store!);
        var target = live.Achievement.GetCondition(condition) ?? throw new KeyNotFoundException($"Unknown condition {key}/{condition}.");
        return Change(live, context, store!, trust, () => action(target), clientReported);
    }

    internal static bool Reset(string key, AchievementContext context)
    {
        lock (Gate) return ResetCore(key, context);
    }
    private static bool ResetCore(string key, AchievementContext context)
    {
        MainThread();
        if (!Definitions.TryGetValue(key, out var definition)) throw new KeyNotFoundException($"Unknown achievement {key}.");
        if (!Resolve(definition, context, out var store, out var trust)) throw new InvalidOperationException("Achievement context is unavailable.");
        if (!CanWrite(definition, context)) throw new InvalidOperationException("Only the authoritative side can reset this achievement.");
        return Change(GetLive(definition, context, store!), context, store!, trust, () => GetLive(definition, context, store!).Achievement.ClearProgress(), reset: true);
    }

    private static bool Change(LiveProgress live, AchievementContext context, ProgressStore store, ProgressTrust trust, Action mutation, bool clientReported = false, bool reset = false)
    {
        if (live.Mutating) throw new InvalidOperationException("Recursive mutation of the same achievement is not allowed.");
        var before = Capture(live);
        string previous = JsonConvert.SerializeObject(before, Formatting.None);
        bool completed = live.Achievement.IsCompleted;
        bool priorTrust = live.ClientReported;
        string next;
        live.Mutating = true;
        try
        {
            mutation();
            ValidateNumbers(live);
            live.ClientReported = !reset && (live.ClientReported || clientReported);
            next = JsonConvert.SerializeObject(Capture(live), Formatting.None);
            ProgressStore.ValidateState(next);
            Restore(live.Achievement, Parse(next));
        }
        catch
        {
            Restore(live.Achievement, before);
            live.ClientReported = priorTrust;
            throw;
        }
        finally { live.Mutating = false; }
        if (next == previous) return false;
        try { lock (Gate) store.Set(context.Address(live.Definition.Scope), live.Definition.Id, next); }
        catch { Restore(live.Achievement, before); live.ClientReported = priorTrust; throw; }
        live.Persisted = next;
        bool newlyCompleted = !completed && live.Achievement.IsCompleted;
        live.Mutating = true;
        try
        {
        AchievementNetwork.Changed(live.Definition, context, next, newlyCompleted);
        Refresh(live.Definition, context, newlyCompleted);
        if (live.Definition.Scope == AchievementScope.User && context.UserId == AchievementWorld.LocalUserId) AchievementWorld.UserDirty = true;
        if (Main.gameMenu && AchievementWorld.UserDirty) AchievementWorld.SaveUser();
        RaiseChanged(live.Definition, context, live.Achievement.IsCompleted, false);
        if (newlyCompleted)
        {
            var completion = new AchievementCompletion(live.Definition.Id, live.Definition.Scope, context, live.ClientReported ? ProgressTrust.ClientReported : trust);
            try { live.Definition.OnCompleted(completion); }
            catch (Exception error) { live.Definition.Mod.Logger.Error($"Achievement completion callback failed: {live.Definition.Id}", error); }
            if (Completed is { } listeners)
                foreach (Action<AchievementCompletion> listener in listeners.GetInvocationList())
                    try { listener(completion); }
                    catch (Exception error) { live.Definition.Mod.Logger.Error("Achievement completion subscriber failed.", error); }
        }
        }
        finally { live.Mutating = false; }
        return true;
    }

    internal static void Observe(AchievementEvent kind, int contentId, AchievementContext context, bool clientReport)
    {
        lock (Gate) ObserveCore(kind, contentId, context, clientReport);
    }
    private static void ObserveCore(AchievementEvent kind, int contentId, AchievementContext context, bool clientReport)
    {
        MainThread();
        if (!Events.TryGetValue((kind, contentId), out var matches)) return;
        foreach (var match in matches)
        {
            if (clientReport && (kind == AchievementEvent.NpcKilled || match.Definition.ObservationPolicy != ObservationPolicy.AllowClientObserved)) continue;
            if (Main.netMode == NetmodeID.MultiplayerClient && match.Definition.Scope != AchievementScope.User) continue;
            if (Main.netMode == NetmodeID.Server && match.Definition.Scope == AchievementScope.User) continue;
            if (!Resolve(match.Definition, context, out var store, out _)) continue;
            if (GetLive(match.Definition, context, store!).Achievement.GetCondition(match.Condition).IsCompleted) continue;
            Mutate(match.Definition.Id, match.Condition, context, condition => condition.Complete(), clientReport);
        }
    }

    internal static bool HasClientObservation(AchievementEvent kind, int id) => kind != AchievementEvent.NpcKilled && Events.TryGetValue((kind, id), out var matches) && matches.Any(match => match.Definition.Scope != AchievementScope.User && match.Definition.ObservationPolicy == ObservationPolicy.AllowClientObserved);

    internal static bool Resolve(ScopedAchievement definition, AchievementContext context, out ProgressStore? store, out ProgressTrust trust)
    {
        store = null; trust = Main.netMode == NetmodeID.SinglePlayer ? ProgressTrust.Local : ProgressTrust.ServerSession;
        if (!context.Supports(definition.Scope)) return false;
        if (definition.Scope == AchievementScope.User && !Main.dedServ && context.UserId == AchievementWorld.LocalUserId)
        {
            store = AchievementWorld.UserStore; trust = ProgressTrust.Local; return store is not null;
        }
        if (definition.Scope == AchievementScope.User && Main.netMode == NetmodeID.MultiplayerClient) return false;
        if (!definition.IsNetworked && Main.dedServ) return false;
        if (!AchievementWorld.Ready) return false;
        if (definition.Scope is AchievementScope.World or AchievementScope.PlayerWorld)
        {
            if (context.WorldId != AchievementWorld.WorldId) return false;
            if (definition.Scope == AchievementScope.PlayerWorld && Main.netMode == NetmodeID.MultiplayerClient && !AchievementPlayer.KnownPlayers.Contains(context.PlayerId)) return false;
            store = AchievementWorld.WorldStore; return true;
        }
        var player = AchievementPlayer.Find(context, definition.Scope);
        if (player is null || !player.Ready) return false;
        store = definition.Scope == AchievementScope.User ? player.Users : player.Characters;
        trust = Main.netMode == NetmodeID.SinglePlayer ? ProgressTrust.Local : ProgressTrust.ClientReported;
        return true;
    }

    private static bool CanWrite(ScopedAchievement definition, AchievementContext context) => Main.netMode != NetmodeID.MultiplayerClient || AchievementWorld.Ready && definition.Scope == AchievementScope.User && context.UserId == AchievementWorld.LocalUserId;

    private static LiveProgress GetLive(ScopedAchievement definition, AchievementContext context, ProgressStore store)
    {
        var cacheKey = CacheKey(definition, context);
        if (Live.TryGetValue(cacheKey, out var cached)) return cached;
        string? state;
        lock (Gate) state = store.Get(context.Address(definition.Scope), definition.Id);
        var parsed = Parse(state);
        var live = new LiveProgress(definition, definition.Create(OwnerContext(definition.Scope, context)), state, Reported(parsed));
        try { Restore(live.Achievement, parsed); ValidateNumbers(live); }
        catch { definition.Release(live.Achievement); throw; }
        Live.Add(cacheKey, live);
        return live;
    }
    private static (string, Guid, Guid) CacheKey(ScopedAchievement definition, AchievementContext context) => definition.Scope switch
    {
        AchievementScope.User => (definition.Id, context.UserId, Guid.Empty),
        AchievementScope.Player => (definition.Id, context.PlayerId, Guid.Empty),
        AchievementScope.World => (definition.Id, context.WorldId, Guid.Empty),
        _ => (definition.Id, context.PlayerId, context.WorldId)
    };
    private static AchievementContext OwnerContext(AchievementScope scope, AchievementContext context) => scope switch
    {
        AchievementScope.User => AchievementContext.ForUser(context.UserId),
        AchievementScope.World => AchievementContext.ForWorld(context.WorldId),
        AchievementScope.Player => AchievementContext.ForPlayer(context.PlayerId),
        _ => AchievementContext.ForPlayerWorld(context.PlayerId, context.WorldId)
    };

    internal static Dictionary<string, JObject> Parse(string? state)
    {
        var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (state is null) return result;
        var root = JObject.Parse(state, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        foreach (var property in root.Properties())
            result.Add(property.Name, property.Value is JObject value ? value : throw new System.IO.InvalidDataException("Condition state must be an object: " + property.Name));
        if (result.TryGetValue("__eyes_meta", out var meta) && meta["ClientReported"]?.Type != JTokenType.Boolean) throw new System.IO.InvalidDataException("Invalid achievement trust metadata.");
        return result;
    }
    private static Dictionary<string, JObject> Capture(LiveProgress live)
    {
        var result = Parse(live.Persisted);
        if (live.ClientReported) result["__eyes_meta"] = new JObject { ["ClientReported"] = true };
        else result.Remove("__eyes_meta");
        foreach (var template in live.Definition.Conditions) result[template.Key] = JObject.FromObject(live.Achievement.GetCondition(template.Key));
        return result;
    }
    internal static void Restore(Achievement achievement, Dictionary<string, JObject> state)
    {
        foreach (var pair in state)
        {
            var condition = achievement.GetCondition(pair.Key);
            if (condition is null) continue;
            if (pair.Value["Completed"]?.Type != JTokenType.Boolean) throw new System.IO.InvalidDataException("Condition state needs a boolean Completed property: " + pair.Key);
            if (condition is CustomIntCondition && (pair.Value["Value"]?.Type != JTokenType.Integer || pair.Value.Value<long>("Value") is < int.MinValue or > int.MaxValue)) throw new System.IO.InvalidDataException("Invalid integer condition value: " + pair.Key);
            if (condition is CustomFloatCondition && pair.Value["Value"]?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new System.IO.InvalidDataException("Invalid floating-point condition value: " + pair.Key);
        }
        achievement.ClearProgress();
        achievement.Load(state);
    }
    private static void ValidateNumbers(LiveProgress live)
    {
        foreach (var template in live.Definition.Conditions)
        {
            var condition = live.Achievement.GetCondition(template.Key);
            double? number = condition switch { CustomIntCondition value => value.Value, CustomFloatCondition value => value.Value, _ => null };
            if (number is { } current && (!double.IsFinite(current) || current < 0 || template.Target is { } max && current > max)) throw new ArgumentOutOfRangeException(template.Key, "Condition progress must be finite and within its declared range.");
        }
    }

    internal static void ValidateIncoming(ScopedAchievement definition, AchievementContext context, string state)
    {
        try
        {
            ProgressStore.ValidateState(state);
            var parsed = Parse(state);
            var live = new LiveProgress(definition, definition.Create(OwnerContext(definition.Scope, context)), state, Reported(parsed));
            try { Restore(live.Achievement, parsed); ValidateNumbers(live); }
            finally { definition.Release(live.Achievement); }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new System.IO.InvalidDataException($"Invalid incoming progress for {definition.Id}.", error);
        }
    }
    internal static void Apply(ScopedAchievement definition, AchievementContext context, string state, bool notify)
    {
        lock (Gate) ApplyCore(definition, context, state, notify);
    }
    private static void ApplyCore(ScopedAchievement definition, AchievementContext context, string state, bool notify)
    {
        ValidateIncoming(definition, context, state);
        if (!Resolve(definition, context, out var store, out _)) throw new InvalidOperationException("Cannot apply state to an unavailable context.");
        var previous = GetLive(definition, context, store!);
        if (previous.Persisted == state) return;
        bool wasCompleted = previous.Achievement.IsCompleted;
        lock (Gate) store!.Set(context.Address(definition.Scope), definition.Id, state);
        if (Live.Remove(CacheKey(definition, context), out var stale)) definition.Release(stale.Achievement);
        if (definition.Scope == AchievementScope.User && context.UserId == AchievementWorld.LocalUserId) AchievementWorld.UserDirty = true;
        Refresh(definition, context, notify && !wasCompleted);
        RaiseChanged(definition, context, GetLive(definition, context, store!).Achievement.IsCompleted, true);
    }
    private static void Refresh(ScopedAchievement definition, AchievementContext context, bool notify)
    {
        if (Main.dedServ) return;
        lock (Gate)
        {
            bool ready = PresentationContext(out var local, out var session);
            if (!ready) local = AchievementApi.LocalContext;
            if (!local.Supports(definition.Scope) || context.Address(definition.Scope) != local.Address(definition.Scope)) return;
            if (Presentations.Remove(definition.Id, out var pending) && pending.Context == local && pending.Session == session)
                notify |= pending.Notify;
            if (Main.gameMenu && definition.Scope != AchievementScope.User)
            {
                if (ready)
                {
                    StatusCore(definition.Id, local, out bool completed);
                    Presentations[definition.Id] = new(local, session, notify && completed); // При подключении уведомление ждёт выхода из меню; токен сессии исключает перенос на следующее подключение.
                }
                return;
            }
            definition.Presentation?.Refresh(notify);
        }
    }
    private static bool PresentationContext(out AchievementContext context, out Guid session)
    {
        context = default; session = Guid.Empty;
        if (Main.dedServ || !AchievementWorld.Ready || Main.myPlayer < 0 || Main.myPlayer >= Main.maxPlayers
            || !Main.LocalPlayer.TryGetModPlayer<AchievementPlayer>(out var player) || !player.Ready || player.Session == Guid.Empty) return false;
        context = AchievementApi.ForPlayer(Main.LocalPlayer);
        session = player.Session;
        return true;
    }
    internal static void FlushPresentations()
    {
        if (Main.dedServ || Main.gameMenu) return;
        lock (Gate)
        {
            if (Presentations.Count == 0) return;
            var pending = Presentations.ToArray();
            Presentations.Clear();
            if (!PresentationContext(out var local, out var session)) return;
            foreach (var entry in pending)
                if (entry.Value.Context == local && entry.Value.Session == session && Definitions.TryGetValue(entry.Key, out var definition))
                    definition.Presentation?.Refresh(entry.Value.Notify);
        }
    }
    internal static void RefreshAll()
    {
        if (!Terraria.Program.IsMainThread) { Main.QueueMainThreadAction(RefreshAll); return; }
        if (Main.dedServ) return;
        if (Main.gameMenu && PresentationContext(out var local, out _))
        {
            foreach (var definition in Definitions.Values) Refresh(definition, local, false);
        }
        else foreach (var definition in Definitions.Values) definition.Presentation?.Refresh(false);
    }
    internal static void ResetVisible()
    {
        foreach (var definition in Definitions.Values)
            if (CanWrite(definition, AchievementApi.LocalContext) && Resolve(definition, AchievementApi.LocalContext, out _, out _)) Reset(definition.Id, AchievementApi.LocalContext);
        RefreshAll();
    }
    internal static string Save(ProgressStore store) { lock (Gate) return store.Serialize(); }
    internal static void AnnounceContext(AchievementContext context)
    {
        if (ContextReady is not { } listeners) return;
        foreach (Action<AchievementContext> listener in listeners.GetInvocationList())
            try { listener(context); }
            catch (Exception error) { Terraria.ModLoader.ModContent.GetInstance<EyesAchievementsMod>().Logger.Error("Achievement context subscriber failed.", error); }
    }
    private static void RaiseChanged(ScopedAchievement definition, AchievementContext context, bool completed, bool synchronized)
    {
        if (Changed is not { } listeners) return;
        var change = new AchievementChange(definition.Id, definition.Scope, context, completed, synchronized);
        foreach (Action<AchievementChange> listener in listeners.GetInvocationList())
            try { listener(change); }
            catch (Exception error) { definition.Mod.Logger.Error("Achievement progress subscriber failed.", error); }
    }
    private static bool Reported(Dictionary<string, JObject> state) => state.TryGetValue("__eyes_meta", out var meta) && meta.Value<bool>("ClientReported");

    private sealed record PendingPresentation(AchievementContext Context, Guid Session, bool Notify);

    private sealed class LiveProgress(ScopedAchievement definition, Achievement achievement, string? persisted, bool reported)
    {
        internal ScopedAchievement Definition { get; } = definition;
        internal Achievement Achievement { get; } = achievement;
        internal string? Persisted = persisted;
        internal bool Mutating;
        internal bool ClientReported = reported;
    }
}
