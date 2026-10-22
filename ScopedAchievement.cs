using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.Achievements;
using Terraria.GameContent.Achievements;
using Terraria.Localization;
using Terraria.ModLoader;
using EyesAchievements.Core;
using EyesAchievements.Runtime;

namespace EyesAchievements;

/// <summary>
/// Определение с постоянными ID ModName/Name и Scope: их нельзя переиспользовать для другого достижения.
/// Условия регистрируются в SetStaticDefaults; нужны уникальные постоянные ключи до 192 символов без префикса __eyes_, не более 256 условий.
/// Все условия должны завершиться. Для областей кроме User мод-потребитель обязан иметь side=Both; Client/NoSync допускают только локальный User.
/// </summary>
[Autoload(true, Side = ModSide.Both)]
public abstract class ScopedAchievement : ModType, ILocalizedModType
{
    private static readonly HashSet<AchievementCondition> OwnedConditions = new(ReferenceEqualityComparer.Instance);
    private readonly List<ConditionTemplate> _conditions = new();
    private bool _configured;
    internal IReadOnlyList<ConditionTemplate> Conditions => _conditions;
    internal NativeAchievement? Presentation { get; private set; }
    internal bool IsNetworked => Mod.Side == ModSide.Both;
    public string Id { get; private set; } = "";
    public abstract AchievementScope Scope { get; }
    public virtual ObservationPolicy ObservationPolicy => ObservationPolicy.ServerOnly;
    public virtual string TextureName => (GetType().Namespace + "." + Name).Replace('.', '/');
    public virtual int Index => 0;
    public virtual bool Hidden => false;
    public virtual AchievementCategory Category => AchievementCategory.Challenger;
    public string LocalizationCategory => "Achievements";
    public virtual LocalizedText FriendlyName => this.GetLocalization("FriendlyName", PrettyPrintName);
    public virtual LocalizedText Description => this.GetLocalization("Description");
    public virtual ModAchievement.Position GetDefaultPosition() => ModAchievement.AfterLastVanillaAchievement;
    public virtual ModAchievement.Position? GetAdvisorPosition() => null;
    public virtual IEnumerable<ModAchievement.Position> GetModdedConstraints() => Array.Empty<ModAchievement.Position>();
    /// <summary>Клиентская проекция для нативного UI и порядка достижений; null на выделенном сервере. Её изменяемые условия не служат источником игрового прогресса.</summary>
    public ModAchievement? NativePresentation => Presentation;

    protected sealed override void Register()
    {
        Id = FullName;
        ValidateSide(Mod.Side, Scope);
        ProgressStore.ValidateKey(FullName);
        if (!Enum.IsDefined(Scope) || !Enum.IsDefined(ObservationPolicy)) throw new InvalidOperationException($"Invalid scope or observation policy for {FullName}.");
        ModTypeLookup<ScopedAchievement>.Register(this);
        AchievementRuntime.Register(this);
        if (!Main.dedServ) Mod.AddContent(Presentation = new NativeAchievement(this));
    }
    internal static void ValidateSide(ModSide side, AchievementScope scope)
    {
        if (side == ModSide.Server || scope != AchievementScope.User && side != ModSide.Both)
            throw new InvalidOperationException("Scoped achievements require a Both-side consumer mod. Client/NoSync consumers may register local User achievements only.");
    }

    public sealed override void SetupContent()
    {
        SetStaticDefaults();
        if ((int)Category < 0 || (int)Category > 3 || Index < 0) throw new InvalidOperationException($"{FullName} must have a valid achievement category and nonnegative icon index.");
        if (_conditions.Count == 0) throw new InvalidOperationException($"{FullName} must have at least one condition.");
        _configured = true;
        AchievementRuntime.Index(this);
        if (Scope != AchievementScope.User && ObservationPolicy == ObservationPolicy.ServerOnly && _conditions.Any(c => c.Event is AchievementEvent.ItemCrafted or AchievementEvent.ItemPickedUp or AchievementEvent.TileDestroyed))
            Mod.Logger.Warn($"{FullName}: client-observed helpers require explicit AllowClientObserved in multiplayer; otherwise confirm them with AchievementApi.Observe on the server.");
    }

    /// <summary>Фабрика создаёт новый экземпляр с Name=key для каждого состояния и UI; release освобождает его подписки и ресурсы. Правила владения совпадают с перегрузкой, принимающей контекст.</summary>
    public ConditionHandle<T> AddCondition<T>(string key, Func<T> factory, Action<T>? release = null) where T : AchievementCondition
        => AddCondition(key, _ => factory(), factory, release);

    /// <summary>
    /// Передаёт библиотеке новый экземпляр с Name=key при каждом вызове, включая временную проверку входящих данных; release освобождает подписки и ресурсы.
    /// factory получает только ID владельца области: UserId, PlayerId, WorldId либо пару PlayerId/WorldId; остальные ID пусты.
    /// presentationFactory вызывается отдельно, до выбора персонажа и мира. Не переиспользуйте условия и не меняйте их вне Mutate; Load/Clear должны восстанавливать сериализованное состояние без игровых побочных эффектов.
    /// </summary>
    public ConditionHandle<T> AddCondition<T>(string key, Func<AchievementContext, T> factory, Func<T> presentationFactory, Action<T>? release = null) where T : AchievementCondition
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(presentationFactory);
        AddTemplate(new(key, context => factory(context), condition => release?.Invoke((T)condition), PresentationFactory: () => presentationFactory()));
        return new(this, key);
    }

    public ConditionHandle<CustomFlagCondition> AddCondition(string key = "Condition")
    {
        AddTemplate(new(key, _ => new CustomFlagCondition(key), _ => { }, Target: 1));
        return new(this, key);
    }
    public ConditionHandle<CustomIntCondition> AddIntCondition(int maxValue) => AddIntCondition("Condition", maxValue);
    public ConditionHandle<CustomIntCondition> AddIntCondition(string key, int maxValue)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        AddTemplate(new(key, _ => new CustomIntCondition(key, maxValue), _ => { }, Target: maxValue));
        return new(this, key);
    }
    public ConditionHandle<CustomFloatCondition> AddFloatCondition(float maxValue) => AddFloatCondition("Condition", maxValue);
    public ConditionHandle<CustomFloatCondition> AddFloatCondition(string key, float maxValue)
    {
        if (!float.IsFinite(maxValue) || maxValue <= 0 || maxValue >= (float)decimal.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxValue));
        AddTemplate(new(key, _ => new CustomFloatCondition(key, maxValue), _ => { }, Target: maxValue));
        return new(this, key);
    }
    public ConditionHandle<CustomFlagCondition> AddNPCKilledCondition(int npcId) => AddNPCKilledCondition(new[] { npcId });
    public ConditionHandle<CustomFlagCondition> AddNPCKilledCondition(int[] npcIds) => AddNPCKilledCondition("Kill_" + ContentKey(npcIds, true), npcIds);
    public ConditionHandle<CustomFlagCondition> AddNPCKilledCondition(string key, int npcId) => AddNPCKilledCondition(key, new[] { npcId });
    public ConditionHandle<CustomFlagCondition> AddNPCKilledCondition(string key, int[] npcIds) => AddEvent(key, AchievementEvent.NpcKilled, npcIds);
    public ConditionHandle<CustomFlagCondition>[] AddManyNPCKilledCondition(int[] npcIds) => npcIds.Select(AddNPCKilledCondition).ToArray();
    public ConditionHandle<CustomFlagCondition> AddItemCraftCondition(int itemId) => AddItemCraftCondition(new[] { itemId });
    public ConditionHandle<CustomFlagCondition> AddItemCraftCondition(int[] itemIds) => AddItemCraftCondition("Craft_" + ContentKey(itemIds, false), itemIds);
    public ConditionHandle<CustomFlagCondition> AddItemCraftCondition(string key, int itemId) => AddItemCraftCondition(key, new[] { itemId });
    public ConditionHandle<CustomFlagCondition> AddItemCraftCondition(string key, int[] itemIds) => AddEvent(key, AchievementEvent.ItemCrafted, itemIds);
    public ConditionHandle<CustomFlagCondition>[] AddManyItemCraftCondition(int[] itemIds) => itemIds.Select(AddItemCraftCondition).ToArray();
    public ConditionHandle<CustomFlagCondition> AddItemPickupCondition(int itemId) => AddItemPickupCondition(new[] { itemId });
    public ConditionHandle<CustomFlagCondition> AddItemPickupCondition(int[] itemIds) => AddItemPickupCondition("Pickup_" + ContentKey(itemIds, false), itemIds);
    public ConditionHandle<CustomFlagCondition> AddItemPickupCondition(string key, int itemId) => AddItemPickupCondition(key, new[] { itemId });
    public ConditionHandle<CustomFlagCondition> AddItemPickupCondition(string key, int[] itemIds) => AddEvent(key, AchievementEvent.ItemPickedUp, itemIds);
    public ConditionHandle<CustomFlagCondition>[] AddManyItemPickupCondition(int[] itemIds) => itemIds.Select(AddItemPickupCondition).ToArray();
    public ConditionHandle<CustomFlagCondition> AddTileDestroyedCondition(int[] tileIds) => AddTileDestroyedCondition("Tile_" + TileKey(tileIds), tileIds);
    public ConditionHandle<CustomFlagCondition> AddTileDestroyedCondition(string key, int[] tileIds) => AddEvent(key, AchievementEvent.TileDestroyed, tileIds);

    /// <summary>Настраивает отдельный трекер каждого экземпляра, включая UI. Нативное меню допускает AchievementTracker&lt;int&gt; или AchievementTracker&lt;float&gt; с соответствующим TrackerType.</summary>
    public virtual void ConfigureTracker(Achievement achievement)
    {
        if (_conditions.Count > 1) achievement.UseConditionsCompletedTracker();
        else if (achievement.GetCondition(_conditions[0].Key).GetAchievementTracker() is { } tracker) achievement.UseTracker(tracker);
    }

    /// <summary>Вызывается после нового перехода к завершению на стороне мутации; загрузка и синхронизация его не вызывают. Повторная мутация этого же достижения внутри callback запрещена.</summary>
    public virtual void OnCompleted(AchievementCompletion completion) { }

    internal Achievement Create(AchievementContext context)
    {
        var achievement = new Achievement(FullName);
        Populate(achievement, context);
        return achievement;
    }

    internal void Populate(Achievement achievement, AchievementContext context, bool presentation = false)
    {
        achievement.SetCategory(Category);
        var created = new List<(ConditionTemplate Template, AchievementCondition Condition)>();
        try
        {
            foreach (var template in _conditions)
            {
                var condition = (presentation && template.PresentationFactory is not null ? template.PresentationFactory() : template.Factory(context)) ?? throw new InvalidOperationException($"{FullName}/{template.Key} returned no condition.");
                lock (OwnedConditions)
                    if (!OwnedConditions.Add(condition)) throw new InvalidOperationException($"{FullName}/{template.Key} reused a condition instance; factories must create a fresh instance for each context.");
                created.Add((template, condition));
                if (condition.Name != template.Key) throw new InvalidOperationException($"Condition factory for {FullName}/{template.Key} returned a different name.");
                achievement.AddCondition(condition);
            }
            ConfigureTracker(achievement);
            var tracker = achievement.GetTracker();
            if (tracker is not null && !(tracker.GetTrackerType() == TrackerType.Int && tracker is AchievementTracker<int> || tracker.GetTrackerType() == TrackerType.Float && tracker is AchievementTracker<float>))
                throw new InvalidOperationException($"{FullName} uses a tracker incompatible with Terraria's achievement menu; derive from AchievementTracker<int> or AchievementTracker<float>.");
        }
        catch
        {
            foreach (var pair in created) ReleaseOne(pair.Template, pair.Condition);
            ReleaseTrackerSafely(achievement.GetTracker());
            throw;
        }
    }

    internal void Release(Achievement achievement)
    {
        foreach (var template in _conditions)
            if (achievement.GetCondition(template.Key) is { } condition) ReleaseOne(template, condition);
        ReleaseTrackerSafely(achievement.GetTracker());
    }
    /// <summary>Освобождает трекер при удалении состояния или UI, в том числе после ошибки создания; по умолчанию вызывает IDisposable.Dispose.</summary>
    public virtual void ReleaseTracker(IAchievementTracker tracker) => (tracker as IDisposable)?.Dispose();
    private void ReleaseOne(ConditionTemplate template, AchievementCondition condition)
    {
        try { template.Release(condition); }
        catch (Exception error) { Mod.Logger.Error($"Condition cleanup failed: {FullName}/{template.Key}", error); }
        finally { lock (OwnedConditions) OwnedConditions.Remove(condition); }
    }
    private void ReleaseTrackerSafely(IAchievementTracker? tracker)
    {
        if (tracker is null) return;
        try { ReleaseTracker(tracker); }
        catch (Exception error) { Mod.Logger.Error($"Tracker cleanup failed: {FullName}", error); }
    }

    private ConditionHandle<CustomFlagCondition> AddEvent(string key, AchievementEvent kind, int[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Length == 0 || ids.Length > 4096 || ids.Any(id => id < (kind == AchievementEvent.NpcKilled ? short.MinValue : 0))) throw new ArgumentException("Content IDs must be a nonempty list of valid IDs, at most 4096 entries.", nameof(ids));
        AddTemplate(new(key, _ => new CustomFlagCondition(key), _ => { }, kind, ids.Distinct().ToArray(), 1));
        return new(this, key);
    }

    private void AddTemplate(ConditionTemplate template)
    {
        if (_configured) throw new InvalidOperationException("Conditions must be registered in SetStaticDefaults.");
        if (string.IsNullOrWhiteSpace(template.Key) || template.Key.StartsWith("__eyes_", StringComparison.Ordinal) || template.Key.Length > 192 || _conditions.Any(c => c.Key == template.Key)) throw new ArgumentException("Condition keys must be nonempty, unique, not start with __eyes_, and at most 192 characters.");
        if (_conditions.Count >= 256) throw new InvalidOperationException("An achievement supports at most 256 conditions.");
        _conditions.Add(template);
    }

    private static string ContentKey(int[] ids, bool npc)
    {
        if (ids is null || ids.Length == 0) throw new ArgumentException("At least one content ID is required.", nameof(ids));
        return npc ? NPCLoader.GetNPC(ids[0])?.FullName ?? ids[0].ToString() : ItemLoader.GetItem(ids[0])?.FullName ?? ids[0].ToString();
    }
    private static string TileKey(int[] ids)
    {
        if (ids is null || ids.Length == 0) throw new ArgumentException("At least one tile ID is required.", nameof(ids));
        return TileLoader.GetTile(ids[0])?.FullName ?? ids[0].ToString();
    }
}

internal sealed record ConditionTemplate(string Key, Func<AchievementContext, AchievementCondition> Factory, Action<AchievementCondition> Release, AchievementEvent? Event = null, int[]? ContentIds = null, double? Target = null, Func<AchievementCondition>? PresentationFactory = null);
