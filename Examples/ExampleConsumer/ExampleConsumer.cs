using EyesAchievements;
using Terraria;
using Terraria.Achievements;
using Terraria.GameContent.Achievements;
using Terraria.ID;
using Terraria.ModLoader;

namespace ExampleConsumer;

public sealed class ExampleConsumer : Mod { }

public sealed class WorldHunt : ScopedAchievement
{
    public override AchievementScope Scope => AchievementScope.World;
    public override string TextureName => "Terraria/Images/UI/Achievements";
    public override AchievementCategory Category => AchievementCategory.Slayer;
    public override void SetStaticDefaults() => AddNPCKilledCondition("KingSlime", NPCID.KingSlime);
}

public sealed class CharacterTraining : ScopedAchievement
{
    public ConditionHandle<CustomIntCondition> Training { get; private set; } = null!;
    public override AchievementScope Scope => AchievementScope.Player;
    public override string TextureName => "Terraria/Images/UI/Achievements";
    public override void SetStaticDefaults() => Training = AddIntCondition("Training", 10);
}

public sealed class TrainingKills : GlobalNPC
{
    public override void OnKill(NPC npc)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || npc.friendly) return;
        var achievement = ModContent.GetInstance<CharacterTraining>();
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            var player = Main.player[i];
            if (!player.active || !npc.playerInteraction[i]) continue;
            var context = AchievementApi.ForPlayer(player);
            if (AchievementApi.TryIsCompleted(achievement.Id, context, out bool completed) && !completed)
                achievement.Training.Mutate(context, counter => counter.Value++);
        }
    }
}

public sealed class TrainingPlayer : ModPlayer
{
    public bool TrainingComplete => AchievementApi.TryIsCompleted("ExampleConsumer/CharacterTraining", AchievementApi.ForPlayer(Player), out bool completed) && completed;
}
