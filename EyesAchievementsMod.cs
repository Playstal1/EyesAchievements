using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Terraria.Achievements;
using Terraria;
using Terraria.ModLoader;
using EyesAchievements.Runtime;

namespace EyesAchievements;

public sealed class EyesAchievementsMod : Mod
{
    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        try { AchievementNetwork.Receive(reader, whoAmI); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or Newtonsoft.Json.JsonException or System.Text.Json.JsonException or InvalidOperationException)
        {
            AchievementNetwork.Abort(whoAmI);
            Logger.Warn($"Rejected achievement packet from {whoAmI}: {error.Message}");
        }
    }
    /// <summary>
    /// Строковый API игрового потока: IsCompleted возвращает null при недоступном контексте или неизвестном ID; Get сохраняет различие в Status.
    /// Контекст принимается как AchievementContext, Player или Guid[3] в порядке user, player, world. Register вызывается при загрузке мода-владельца.
    /// </summary>
    public override object Call(params object[] args)
    {
        if (args.Length == 0 || args[0] is not string operation) throw new ArgumentException("EyesAchievements.Call requires an operation name.");
        return operation switch
        {
            "ApiVersion" when args.Length == 1 => AchievementApi.ApiVersion,
            "Register" when args.Length == 4 && args[1] is Mod owner && args[2] is string name && args[3] is IDictionary<string, object> options => Register(owner, name, options),
            "LocalContext" when args.Length == 1 => AchievementApi.LocalContext,
            "Context" when args.Length == 2 && args[1] is Player player => AchievementApi.ForPlayer(player),
            "Get" when args.Length == 3 && args[1] is string key => Snapshot(AchievementApi.Get(key, Context(args[2]))),
            "IsCompleted" when args.Length == 3 && args[1] is string key => AchievementApi.TryIsCompleted(key, Context(args[2]), out bool completed) ? completed : null!,
            "Complete" when args.Length == 4 && args[1] is string key && args[2] is string condition => AchievementApi.Complete(key, condition, Context(args[3])),
            "SetInt" when args.Length == 5 && args[1] is string key && args[2] is string condition && args[4] is int value => AchievementApi.SetInt(key, condition, Context(args[3]), value),
            "AddInt" when args.Length == 5 && args[1] is string key && args[2] is string condition && args[4] is int value => AchievementApi.AddInt(key, condition, Context(args[3]), value),
            "SetFloat" when args.Length == 5 && args[1] is string key && args[2] is string condition && args[4] is float value => AchievementApi.SetFloat(key, condition, Context(args[3]), value),
            "Mutate" when args.Length == 5 && args[1] is string key && args[2] is string condition && args[4] is Action<AchievementCondition> mutation => AchievementApi.Mutate(key, condition, Context(args[3]), mutation),
            "Observe" when args.Length == 4 && args[1] is string kind && args[2] is int id && args[3] is Player player => Observe(kind, id, player),
            "Reset" when args.Length == 3 && args[1] is string key => AchievementApi.Reset(key, Context(args[2])),
            "List" when args.Length == 1 => AchievementApi.List(),
            _ => throw new ArgumentException($"Unknown operation or invalid arguments for EyesAchievements.Call('{operation}').")
        };
    }
    private static string Register(Mod owner, string name, IDictionary<string, object> options)
    {
        if (!owner.AddContent(new RegisteredAchievement(name, options))) throw new InvalidOperationException("Achievement registration was disabled.");
        return owner.Name + "/" + name;
    }
    private static IReadOnlyDictionary<string, object> Snapshot(AchievementProgress progress) => new Dictionary<string, object>
    {
        ["Status"] = progress.Status.ToString(), ["IsCompleted"] = progress.IsCompleted,
        ["Trust"] = progress.Trust.ToString(), ["Conditions"] = progress.Conditions,
        ["Values"] = progress.Values.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["IsCompleted"] = pair.Value.IsCompleted, ["Value"] = pair.Value.Value, ["Target"] = pair.Value.Target
        }, StringComparer.Ordinal)
    };
    private static bool Observe(string kind, int id, Player player)
    {
        AchievementApi.Observe(Enum.Parse<AchievementEvent>(kind, true), id, player);
        return true;
    }
    private static AchievementContext Context(object value) => value switch
    {
        AchievementContext context => context,
        Player player => AchievementApi.ForPlayer(player),
        Guid[] { Length: 3 } ids => new(ids[0], ids[1], ids[2]),
        _ => throw new ArgumentException("Context must be an AchievementContext, Player, or Guid[3] containing user, character, and world IDs.")
    };
}
