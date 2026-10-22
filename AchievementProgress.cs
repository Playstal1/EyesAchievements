using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EyesAchievements;

/// <summary>Снимок API: IsCompleted имеет смысл только при Status=Available. Conditions содержит JSON условий, Values — известные библиотеке числовые значения.</summary>
public sealed record AchievementProgress(ProgressStatus Status, bool IsCompleted, ProgressTrust Trust, IReadOnlyDictionary<string, string> Conditions, IReadOnlyDictionary<string, ConditionProgress> Values)
{
    internal static AchievementProgress Missing(ProgressStatus status) => new(status, false, ProgressTrust.Local, new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()), new ReadOnlyDictionary<string, ConditionProgress>(new Dictionary<string, ConditionProgress>()));
}

public sealed record ConditionProgress(bool IsCompleted, double? Value, double? Target);
public sealed record AchievementCompletion(string AchievementId, AchievementScope Scope, AchievementContext Context, ProgressTrust Trust);
public sealed record AchievementChange(string AchievementId, AchievementScope Scope, AchievementContext Context, bool IsCompleted, bool FromSynchronization);
