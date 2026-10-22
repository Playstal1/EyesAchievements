using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Achievements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using EyesAchievements.Core;

namespace EyesAchievements.Runtime;

public sealed class AchievementWorld : ModSystem
{
    internal static ProgressStore WorldStore = new();
    internal static ProgressStore? UserStore;
    internal static Guid LocalUserId, WorldId;
    internal static bool Ready, UserDirty;
    private static string _profilePath = "";

    public override void Load()
    {
        if (!Main.dedServ)
        {
            _profilePath = Path.Combine(Main.SavePath, "EyesAchievements", "user.json");
            LoadUser();
            NativePersistence.Install();
            AchievementsHelper.OnItemCraft += Crafted;
            AchievementsHelper.OnItemPickup += PickedUp;
            AchievementsHelper.OnTileDestroyed += Mined;
            AchievementsHelper.OnNPCKilled += Killed;
        }
    }
    public override void ClearWorld()
    {
        WorldStore = new(); WorldId = Guid.Empty; Ready = false;
        AchievementPlayer.ClearIdentities();
        AchievementNetwork.Clear();
        AchievementRuntime.ClearSession();
    }
    public override void OnWorldLoad()
    {
        WorldId = Main.ActiveWorldFileData.UniqueId;
        Ready = Main.netMode != NetmodeID.MultiplayerClient && WorldId != Guid.Empty;
    }
    public override void OnWorldUnload()
    {
        SaveUser();
        ClearWorld();
        AchievementRuntime.RefreshAll();
    }
    public override void SaveWorldData(TagCompound tag)
    {
        byte[] progress = AchievementStorage.Encode(WorldStore);
        tag["version"] = AchievementStorage.TagVersion;
        tag["progress"] = progress;
    }
    public override void LoadWorldData(TagCompound tag)
    {
        AchievementStorage.ValidateVersion(tag);
        if (tag.ContainsKey("progress")) WorldStore = AchievementStorage.Decode(tag);
    }
    public override void PostUpdateEverything()
    {
        AchievementNetwork.FlushObservations();
        if (!Main.dedServ && UserDirty && Main.GameUpdateCount % 60 == 0) SaveUser();
    }
    public override void Unload()
    {
        if (!Main.dedServ)
        {
            SaveUser();
            AchievementsHelper.OnItemCraft -= Crafted;
            AchievementsHelper.OnItemPickup -= PickedUp;
            AchievementsHelper.OnTileDestroyed -= Mined;
            AchievementsHelper.OnNPCKilled -= Killed;
            NativePersistence.Unload();
        }
        ClearWorld(); UserStore = null; LocalUserId = Guid.Empty;
        AchievementRuntime.Unload();
    }
    private static void LoadUser()
    {
        if (!File.Exists(_profilePath))
        {
            LocalUserId = Guid.NewGuid(); UserStore = new(); UserDirty = true; SaveUser(); return;
        }
        var data = JsonSerializer.Deserialize<UserFile>(File.ReadAllText(_profilePath)) ?? throw new InvalidDataException("Invalid Eyes'Achievements user profile.");
        if (data.Version != 1 || !Guid.TryParse(data.UserId, out LocalUserId) || LocalUserId == Guid.Empty) throw new InvalidDataException("Unsupported or invalid Eyes'Achievements user profile.");
        UserStore = ProgressStore.Parse(data.Progress);
        UserDirty = false;
    }
    internal static void SaveUser()
    {
        if (Main.dedServ || UserStore is null || !UserDirty || string.IsNullOrEmpty(_profilePath)) return;
        lock (AchievementRuntime.Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            string next = _profilePath + ".tmp";
            File.WriteAllText(next, JsonSerializer.Serialize(new UserFile { Version = 1, UserId = LocalUserId.ToString("N"), Progress = UserStore.Serialize() }));
            if (File.Exists(_profilePath)) File.Replace(next, _profilePath, _profilePath + ".bak");
            else File.Move(next, _profilePath);
            UserDirty = false;
        }
    }
    private static void Crafted(short item, int count) => ObserveLocal(AchievementEvent.ItemCrafted, item);
    private static void PickedUp(Player player, short item, int count) { if (player.whoAmI == Main.myPlayer) ObserveLocal(AchievementEvent.ItemPickedUp, item); }
    private static void Mined(Player player, ushort tile) { if (player.whoAmI == Main.myPlayer) ObserveLocal(AchievementEvent.TileDestroyed, tile); }
    private static void Killed(Player player, short npc) { if (player.whoAmI == Main.myPlayer) ObserveLocal(AchievementEvent.NpcKilled, npc); }
    private static void ObserveLocal(AchievementEvent kind, int id)
    {
        if (Main.gameMenu || id < (kind == AchievementEvent.NpcKilled ? short.MinValue : 0) || !Ready || !Main.LocalPlayer.GetModPlayer<AchievementPlayer>().Ready) return;
        AchievementRuntime.Observe(kind, id, AchievementApi.LocalContext, false);
        if (Main.netMode == NetmodeID.MultiplayerClient && AchievementRuntime.HasClientObservation(kind, id)) AchievementNetwork.SendObservation(kind, id);
    }
    internal static void ObserveOwnedItems()
    {
        if (Main.dedServ || !Ready) return;
        var player = Main.LocalPlayer;
        var ids = new HashSet<int>();
        void Add(Item[] items, int length = -1)
        {
            for (int i = 0; i < (length < 0 ? items.Length : length); i++)
                if (!items[i].IsAir) ids.Add(items[i].type);
        }
        Add(player.inventory, 58); Add(player.armor); Add(player.dye); Add(player.miscEquips); Add(player.miscDyes);
        Add(player.bank.item); Add(player.bank2.item); Add(player.bank3.item); Add(player.bank4.item);
        foreach (var loadout in player.Loadouts) { Add(loadout.Armor); Add(loadout.Dye); }
        foreach (int id in ids) ObserveLocal(AchievementEvent.ItemPickedUp, id);
    }
    private sealed class UserFile
    {
        public int Version { get; set; }
        public string UserId { get; set; } = "";
        public string Progress { get; set; } = "";
    }
}
