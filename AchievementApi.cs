using System;
using System.Collections.Generic;
using Terraria;
using Terraria.Achievements;
using Terraria.GameContent.Achievements;
using EyesAchievements.Runtime;

namespace EyesAchievements;

/// <summary>Доступ к прогрессу из игрового потока; фоновую работу передавайте через Main.QueueMainThreadAction. Снимайте подписки на события при Unload своего мода.</summary>
public static class AchievementApi
{
    public const int ApiVersion = 1;
    /// <summary>В главном меню содержит только ID локального профиля; данные персонажа и мира там недоступны.</summary>
    public static AchievementContext LocalContext => !Main.dedServ && !Main.gameMenu && AchievementWorld.Ready ? ForPlayer(Main.LocalPlayer) : AchievementContext.ForUser(AchievementWorld.LocalUserId);
    /// <summary>Новый переход к завершению на стороне, применившей мутацию; загрузка и синхронизация это событие не вызывают.</summary>
    public static event Action<AchievementCompletion> Completed { add => AchievementRuntime.Completed += value; remove => AchievementRuntime.Completed -= value; }
    /// <summary>Изменение прогресса, включая последующие сетевые обновления с FromSynchronization=true; не является сигналом выдачи награды.</summary>
    public static event Action<AchievementChange> ProgressChanged { add => AchievementRuntime.Changed += value; remove => AchievementRuntime.Changed -= value; }
    /// <summary>Начальные данные доступны после входа в одиночный мир или завершения сетевого обмена профилем; для сервера вызывается на каждое подключение.</summary>
    public static event Action<AchievementContext> ContextReady { add => AchievementRuntime.ContextReady += value; remove => AchievementRuntime.ContextReady -= value; }
    public static AchievementContext ForPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var data = player.GetModPlayer<AchievementPlayer>();
        return new(data.UserId, data.PlayerId, AchievementWorld.Ready ? AchievementWorld.WorldId : Guid.Empty);
    }
    /// <summary>Создаёт отдельный снимок с сериализованными условиями. Для частой проверки завершения используйте TryIsCompleted.</summary>
    public static AchievementProgress Get(string achievementId, AchievementContext context) => AchievementRuntime.Get(achievementId, context);
    public static AchievementProgress Get<T>(AchievementContext context) where T : ScopedAchievement => Get(Terraria.ModLoader.ModContent.GetInstance<T>().Id, context);
    /// <summary>Возвращает доступность результата, а не завершённость: false означает неизвестный ID или недоступный контекст, completed при этом false.</summary>
    public static bool TryIsCompleted(string achievementId, AchievementContext context, out bool completed)
    {
        return AchievementRuntime.Status(achievementId, context, out completed) == ProgressStatus.Available;
    }
    public static ProgressStatus GetStatus(string achievementId, AchievementContext context, out bool completed) => AchievementRuntime.Status(achievementId, context, out completed);
    /// <summary>
    /// Меняет условие с проверкой контекста и полномочий; в мультиплеере клиент может менять только собственный User. Возвращает наличие изменения сохранённого состояния.
    /// Ошибка callback или проверки до записи откатывает данные условий. Не сохраняйте ссылку на условие, не запускайте async и не меняйте это же достижение рекурсивно.
    /// Внешние побочные эффекты callback не откатываются; выполняйте их после успешного вызова.
    /// </summary>
    public static bool Mutate<T>(string achievementId, string conditionKey, AchievementContext context, Action<T> mutation) where T : AchievementCondition
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return AchievementRuntime.Mutate(achievementId, conditionKey, context, condition => mutation(condition is T typed ? typed : throw new ArgumentException($"{achievementId}/{conditionKey} is not {typeof(T).Name}.")));
    }
    public static bool Complete(string achievementId, string conditionKey, AchievementContext context) => Mutate<AchievementCondition>(achievementId, conditionKey, context, condition => condition.Complete());
    public static bool SetInt(string achievementId, string conditionKey, AchievementContext context, int value) => Mutate<CustomIntCondition>(achievementId, conditionKey, context, condition => condition.Value = value);
    public static bool AddInt(string achievementId, string conditionKey, AchievementContext context, int amount) => Mutate<CustomIntCondition>(achievementId, conditionKey, context, condition => condition.Value = (int)Math.Clamp((long)condition.Value + amount, 0, int.MaxValue));
    public static bool SetFloat(string achievementId, string conditionKey, AchievementContext context, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return Mutate<CustomFloatCondition>(achievementId, conditionKey, context, condition => condition.Value = value);
    }
    /// <summary>Сбрасывает условия и признак клиентского наблюдения с теми же ограничениями полномочий, что Mutate.</summary>
    public static bool Reset(string achievementId, AchievementContext context) => AchievementRuntime.Reset(achievementId, context);
    /// <summary>Применяет зарегистрированное событие на текущей стороне; вызов на клиенте не отправляет серверу наблюдение для World, Player или PlayerWorld.</summary>
    public static void Observe(AchievementEvent kind, int contentId, Player player)
    {
        if (!Enum.IsDefined(kind) || contentId < (kind == AchievementEvent.NpcKilled ? short.MinValue : 0)) throw new ArgumentOutOfRangeException(nameof(kind));
        AchievementRuntime.Observe(kind, contentId, ForPlayer(player), false);
    }
    public static IReadOnlyList<string> List() => new List<string>(AchievementRuntime.Definitions.Keys).AsReadOnly();
}
