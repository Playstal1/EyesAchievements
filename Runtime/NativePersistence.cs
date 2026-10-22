using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria.Achievements;

namespace EyesAchievements.Runtime;

internal static class NativePersistence
{
    private static ILHook? _save, _load;
    private static Hook? _clear;
    private static string[] _keys = Array.Empty<string>();

    internal static void Install()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            _save = new ILHook(typeof(AchievementManager).GetMethod("Save", flags, new[] { typeof(string), typeof(bool) })!, FilterSave);
            _load = new ILHook(typeof(AchievementManager).GetMethod("Load", flags, new[] { typeof(string), typeof(bool) })!, FilterLoad);
            _clear = new Hook(typeof(AchievementManager).GetMethod(nameof(AchievementManager.ClearAll))!, Clear);
        }
        catch { Unload(); throw; }
    }
    internal static void UpdateKeys(IEnumerable<string> keys) => _keys = new List<string>(keys).ToArray();
    internal static void Unload()
    {
        _clear?.Dispose(); _clear = null;
        _load?.Dispose(); _load = null;
        _save?.Dispose(); _save = null;
        _keys = Array.Empty<string>();
    }
    private static void Remove(object data)
    {
        if (data is IDictionary dictionary) foreach (string key in _keys) dictionary.Remove(key);
    }
    private static void FilterSave(ILContext il)
    {
        var cursor = new ILCursor(il);
        int data = -1;
        if (!cursor.TryGotoNext(MoveType.After, instruction => instruction.MatchCall<AchievementManager>("LoadExistingAchievements"), instruction => instruction.MatchStloc(out data)))
            throw new InvalidOperationException("Unsupported tModLoader achievement save implementation.");
        if (!cursor.TryGotoNext(MoveType.AfterLabel, instruction => instruction.MatchNewobj<System.IO.MemoryStream>())) // Фильтр нужен и при переходе по метке после цикла заполнения снимка.
            throw new InvalidOperationException("Achievement save snapshot boundary was not found.");
        cursor.Emit(OpCodes.Ldloc, data);
        cursor.EmitDelegate<Action<object>>(Remove);
    }
    private static void FilterLoad(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, instruction => instruction.OpCode == OpCodes.Callvirt && instruction.Operand is Mono.Cecil.MethodReference method && method.Name == "Deserialize"))
            throw new InvalidOperationException("Unsupported tModLoader achievement load implementation.");
        cursor.Emit(OpCodes.Dup);
        cursor.EmitDelegate<Action<object>>(Remove);
    }
    private static void Clear(Action<AchievementManager> original, AchievementManager manager)
    {
        original(manager);
        AchievementRuntime.ResetVisible();
    }
}
