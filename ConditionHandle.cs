using System;
using Terraria.Achievements;

namespace EyesAchievements;

/// <summary>Ключ условия, общий для всех контекстов; изменяет их через транзакцию AchievementApi.Mutate, не предоставляя постоянной ссылки на экземпляр условия.</summary>
public sealed class ConditionHandle<T> where T : AchievementCondition
{
    public ScopedAchievement Definition { get; }
    public string Key { get; }
    internal ConditionHandle(ScopedAchievement definition, string key) { Definition = definition; Key = key; }
    public bool Mutate(AchievementContext context, Action<T> mutation) => AchievementApi.Mutate(Definition.Id, Key, context, mutation);
    public bool Complete(AchievementContext context) => Mutate(context, condition => condition.Complete());
}
