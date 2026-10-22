using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Terraria;
using Terraria.Achievements;
using Terraria.Localization;
using Terraria.ModLoader;

namespace EyesAchievements.Runtime;

[Autoload(false)]
internal sealed class NativeAchievement(ScopedAchievement definition) : ModAchievement
{
    internal ScopedAchievement Definition { get; } = definition;
    public override string Name => Definition.Name;
    public override string TextureName => Definition.TextureName;
    public override int Index => Definition.Index;
    public override bool Hidden => Definition.Hidden;
    public override LocalizedText FriendlyName => Definition.FriendlyName;
    public override LocalizedText Description => Definition.Description;
    public override Position GetDefaultPosition() => Definition.GetDefaultPosition();
    public override Position? GetAdvisorPosition() => Definition.GetAdvisorPosition();
    public override IEnumerable<Position> GetModdedConstraints() => Definition.GetModdedConstraints();
    public override void SetStaticDefaults()
    {
        Definition.Populate(Achievement, default, true);
        Refresh(false, true);
    }
    public override void AutoStaticDefaults() { }
    internal void Refresh(bool notify, bool setup = false)
    {
        if (Main.dedServ || Achievement.GetCondition(Definition.Conditions[0].Key) is null) return;
        var progress = AchievementRuntime.Get(Definition.Id, AchievementApi.LocalContext, setup);
        Achievement.ClearProgress();
        var values = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (progress.Status == ProgressStatus.Available)
            foreach (var pair in progress.Conditions) values[pair.Key] = JObject.Parse(pair.Value);
        if (notify && progress.IsCompleted)
        {
            string key = Definition.Conditions[^1].Key;
            if (values.TryGetValue(key, out var last)) last["Completed"] = false;
            Achievement.Load(values);
            Achievement.GetCondition(key).Complete();
        }
        else Achievement.Load(values);
    }
    public override void Unload() => Definition.Release(Achievement);
}
