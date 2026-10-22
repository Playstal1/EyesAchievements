using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using EyesAchievements.Core;

namespace EyesAchievements.Runtime;

public sealed class AchievementPlayer : ModPlayer
{
    internal static readonly HashSet<Guid> KnownPlayers = new();
    internal static readonly Dictionary<Guid, AchievementPlayer> ByPlayer = new();
    internal static readonly Dictionary<Guid, AchievementPlayer> ByUser = new();
    internal ProgressStore Characters = new();
    internal ProgressStore Users = new();
    internal Guid PlayerId, UserId, Session;
    internal bool Ready;
    internal uint Sequence, LastSequence;
    internal ulong ObservationTick;
    internal int ObservationCount;

    public override void Initialize()
    {
        Characters = new(); Users = new();
        PlayerId = Guid.NewGuid(); UserId = Session = Guid.Empty;
        Ready = false; Sequence = LastSequence = 0;
    }
    public override void SaveData(TagCompound tag)
    {
        byte[] progress = AchievementStorage.Encode(Characters);
        tag["version"] = AchievementStorage.TagVersion;
        tag["characterId"] = PlayerId.ToString("N");
        tag["progress"] = progress;
    }
    public override void LoadData(TagCompound tag)
    {
        AchievementStorage.ValidateVersion(tag);
        Guid characterId = PlayerId;
        if (tag.ContainsKey("characterId"))
        {
            if (!Guid.TryParse(tag.GetString("characterId"), out characterId) || characterId == Guid.Empty) throw new System.IO.InvalidDataException("Invalid achievement character ID.");
        }
        var characters = tag.ContainsKey("progress") ? AchievementStorage.Decode(tag) : Characters;
        PlayerId = characterId;
        Characters = characters;
    }
    public override void OnEnterWorld()
    {
        if (Main.dedServ || Player.whoAmI != Main.myPlayer) return;
        UserId = AchievementWorld.LocalUserId;
        Session = Guid.NewGuid(); Sequence = LastSequence = 0;
        if (Main.netMode == NetmodeID.SinglePlayer)
        {
            Users = AchievementWorld.UserStore!;
            Ready = true;
            RegisterIdentity();
            AchievementRuntime.RefreshAll();
            AchievementRuntime.AnnounceContext(AchievementApi.ForPlayer(Player));
            AchievementWorld.ObserveOwnedItems();
        }
        else AchievementNetwork.SendProfile(this);
    }
    public override void PlayerDisconnect()
    {
        Guid session = Session, user = UserId, character = PlayerId;
        int slot = Player.whoAmI;
        void Cleanup()
        {
            if (Session != session) return; // Отложенное отключение старой сессии не должно очищать повторное подключение.
            bool ownsCharacter = ByPlayer.TryGetValue(character, out var characterOwner) && ReferenceEquals(characterOwner, this);
            bool ownsUser = ByUser.TryGetValue(user, out var userOwner) && ReferenceEquals(userOwner, this);
            AchievementRuntime.ForgetPlayer(ownsUser ? user : Guid.Empty, ownsCharacter ? character : Guid.Empty);
            if (ownsCharacter) ByPlayer.Remove(character);
            if (ownsUser) ByUser.Remove(user);
            Ready = false;
            AchievementNetwork.Disconnect(slot, session);
        }
        if (Terraria.Program.IsMainThread) Cleanup();
        else Main.QueueMainThreadAction(Cleanup);
    }
    internal void RegisterIdentity()
    {
        ByPlayer[PlayerId] = this;
        ByUser[UserId] = this;
        KnownPlayers.Add(PlayerId);
    }
    internal static AchievementPlayer? Find(AchievementContext context, AchievementScope scope)
        => (scope == AchievementScope.User ? ByUser.GetValueOrDefault(context.UserId) : ByPlayer.GetValueOrDefault(context.PlayerId));
    internal static void ClearIdentities()
    {
        KnownPlayers.Clear(); ByPlayer.Clear(); ByUser.Clear();
    }
}
