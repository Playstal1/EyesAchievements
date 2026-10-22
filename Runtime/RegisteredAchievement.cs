using System;
using System.Collections.Generic;
using Terraria.Achievements;
using Terraria.ModLoader;

namespace EyesAchievements.Runtime;

[Autoload(false)]
internal sealed class RegisteredAchievement : ScopedAchievement
{
    private readonly string _name;
    private readonly Dictionary<string, object> _options;
    internal RegisteredAchievement(string name, IDictionary<string, object> options)
    {
        _name = name;
        _options = new(options, StringComparer.Ordinal);
    }
    public override string Name => _name;
    public override AchievementScope Scope => Enum.Parse<AchievementScope>(Required<string>(_options, "Scope"), true);
    public override ObservationPolicy ObservationPolicy => _options.TryGetValue("ObservationPolicy", out var policy) ? Enum.Parse<ObservationPolicy>((string)policy, true) : ObservationPolicy.ServerOnly;
    public override string TextureName => Required<string>(_options, "Texture");
    public override int Index => _options.TryGetValue("Index", out var value) ? (int)value : 0;
    public override bool Hidden => _options.TryGetValue("Hidden", out var value) && (bool)value;
    public override AchievementCategory Category => _options.TryGetValue("Category", out var value) ? (AchievementCategory)(int)value : AchievementCategory.Challenger;
    public override ModAchievement.Position GetDefaultPosition() => _options.TryGetValue("Position", out var position) ? (ModAchievement.Position)position : base.GetDefaultPosition();
    public override ModAchievement.Position? GetAdvisorPosition() => _options.TryGetValue("AdvisorPosition", out var position) ? (ModAchievement.Position)position : null;
    public override IEnumerable<ModAchievement.Position> GetModdedConstraints() => _options.TryGetValue("Constraints", out var positions) ? (IEnumerable<ModAchievement.Position>)positions : base.GetModdedConstraints();
    public override void SetStaticDefaults()
    {
        foreach (var condition in Required<IEnumerable<IDictionary<string, object>>>(_options, "Conditions"))
        {
            string key = Required<string>(condition, "Key");
            switch (Required<string>(condition, "Kind"))
            {
                case "Flag": AddCondition(key); break;
                case "Int": AddIntCondition(key, Required<int>(condition, "Max")); break;
                case "Float": AddFloatCondition(key, Required<float>(condition, "Max")); break;
                case "NpcKilled": AddNPCKilledCondition(key, Required<int[]>(condition, "Ids")); break;
                case "ItemCrafted": AddItemCraftCondition(key, Required<int[]>(condition, "Ids")); break;
                case "ItemPickedUp": AddItemPickupCondition(key, Required<int[]>(condition, "Ids")); break;
                case "TileDestroyed": AddTileDestroyedCondition(key, Required<int[]>(condition, "Ids")); break;
                case "Custom": AddCondition(key, Required<Func<AchievementCondition>>(condition, "Factory"), condition.TryGetValue("Release", out var release) ? (Action<AchievementCondition>)release : null); break;
                default: throw new ArgumentException($"Unknown condition Kind for {FullName}/{key}.");
            }
        }
    }
    public override void ConfigureTracker(Achievement achievement)
    {
        if (_options.TryGetValue("ConfigureTracker", out var configure)) ((Action<Achievement>)configure)(achievement);
        else base.ConfigureTracker(achievement);
    }
    public override void OnCompleted(AchievementCompletion completion)
    {
        if (_options.TryGetValue("OnCompleted", out var callback)) ((Action<IReadOnlyDictionary<string, object>>)callback)(new Dictionary<string, object>
        {
            ["AchievementId"] = completion.AchievementId, ["Scope"] = completion.Scope.ToString(), ["UserId"] = completion.Context.UserId,
            ["PlayerId"] = completion.Context.PlayerId, ["WorldId"] = completion.Context.WorldId, ["Trust"] = completion.Trust.ToString()
        });
    }
    private static T Required<T>(IDictionary<string, object> options, string key) => options.TryGetValue(key, out var value) && value is T typed ? typed : throw new ArgumentException($"'{key}' must be a {typeof(T).Name}.");
}
