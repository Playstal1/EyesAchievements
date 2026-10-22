using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace EyesAchievements.Runtime;

public sealed class AchievementNpc : GlobalNPC
{
    public override void OnKill(NPC npc)
    {
        if (Main.netMode != NetmodeID.Server || !AchievementWorld.Ready) return;
        for (int i = 0; i < Main.maxPlayers; i++)
            if (npc.playerInteraction[i] && Main.player[i].active && Main.player[i].GetModPlayer<AchievementPlayer>().Ready)
                AchievementRuntime.Observe(AchievementEvent.NpcKilled, npc.netID, AchievementApi.ForPlayer(Main.player[i]), false);
    }
}
