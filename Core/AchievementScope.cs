using System;

namespace EyesAchievements;

/// <summary>
/// User хранится в EyesAchievements/user.json локального профиля tModLoader (не Steam); Player — в .tplr персонажа.
/// World и PlayerWorld хранятся в .twld мира; PlayerWorld разделяет прогресс по паре ID персонажа и мира.
/// В мультиплеере сервер управляет World, Player и PlayerWorld, принимая начальные данные Player от клиента.
/// </summary>
public enum AchievementScope : byte { User, World, Player, PlayerWorld }
/// <summary>AllowClientObserved разрешает доверять клиентским событиям крафта, подбора и разрушения блоков; это не серверная проверка события.</summary>
public enum ObservationPolicy : byte { ServerOnly, AllowClientObserved }
public enum ProgressStatus : byte { UnknownAchievement, ContextUnavailable, Available }
/// <summary>Происхождение данных, не защита от правки сохранений. ClientReported для принятого клиентского наблюдения сохраняется до сброса достижения.</summary>
public enum ProgressTrust : byte { Local, ServerSession, ClientReported }
public enum AchievementEvent : byte { NpcKilled, ItemCrafted, ItemPickedUp, TileDestroyed }

/// <summary>ID сохранений, а не учётные данные; копия файла персонажа сохраняет PlayerId. Наличие ID само по себе не означает доступности прогресса.</summary>
public readonly record struct AchievementContext(Guid UserId, Guid PlayerId, Guid WorldId)
{
    public static AchievementContext ForUser(Guid userId) => new(userId, Guid.Empty, Guid.Empty);
    public static AchievementContext ForWorld(Guid worldId) => new(Guid.Empty, Guid.Empty, worldId);
    public static AchievementContext ForPlayer(Guid playerId) => new(Guid.Empty, playerId, Guid.Empty);
    public static AchievementContext ForPlayerWorld(Guid playerId, Guid worldId) => new(Guid.Empty, playerId, worldId);

    public bool Supports(AchievementScope scope) => scope switch
    {
        AchievementScope.User => UserId != Guid.Empty,
        AchievementScope.World => WorldId != Guid.Empty,
        AchievementScope.Player => PlayerId != Guid.Empty,
        AchievementScope.PlayerWorld => PlayerId != Guid.Empty && WorldId != Guid.Empty,
        _ => false
    };

    internal string Address(AchievementScope scope) => scope switch
    {
        AchievementScope.User => UserId.ToString("N"),
        AchievementScope.World => WorldId.ToString("N"),
        AchievementScope.Player => PlayerId.ToString("N"),
        AchievementScope.PlayerWorld => WorldId.ToString("N") + ":" + PlayerId.ToString("N"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
}
