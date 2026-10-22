using System.IO;
using System.Text;
using Terraria.ModLoader.IO;
using EyesAchievements.Core;

namespace EyesAchievements.Runtime;

internal static class AchievementStorage
{
    internal const int TagVersion = 2; // UTF-8 в byte[] обходит лимит Int16 длины NBT-строки; версия JSON остаётся прежней.
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(ProgressStore store) => Utf8.GetBytes(AchievementRuntime.Save(store));

    internal static void ValidateVersion(TagCompound tag)
    {
        int version = tag.ContainsKey("version") ? tag.GetInt("version") : 1;
        if (version is not (1 or TagVersion)) throw new InvalidDataException("Unsupported Eyes'Achievements save-tag version.");
    }

    internal static ProgressStore Decode(TagCompound tag)
    {
        ValidateVersion(tag);
        int version = tag.ContainsKey("version") ? tag.GetInt("version") : 1;
        string json = tag["progress"] switch
        {
            byte[] bytes => Utf8.GetString(bytes),
            string legacy when version == 1 => legacy,
            _ => throw new InvalidDataException("Achievement progress must be a UTF-8 byte array.")
        };
        return ProgressStore.Parse(json);
    }
}
