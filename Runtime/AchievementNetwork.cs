using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using EyesAchievements.Core;

namespace EyesAchievements.Runtime;

internal static class AchievementNetwork
{
    private const byte Version = 1;
    private enum Message : byte { ProfileBegin, ProfileEntry, ProfileEnd, SnapshotBegin, Actor, Entry, SnapshotEnd, Delta, Observation, UserDelta }
    private static readonly Dictionary<int, Upload> Uploads = new();
    private static List<Record>? _snapshot;
    private static readonly List<(int Slot, Guid User, Guid Player)> Actors = new();
    private static Guid _snapshotWorld;
    private static int _snapshotBytes;
    private static readonly Queue<(AchievementEvent Kind, int Id)> Observations = new();
    private static readonly HashSet<(AchievementEvent Kind, int Id)> PendingObservations = new();
    private static readonly Dictionary<string, Record> UserChanges = new(StringComparer.Ordinal);

    internal static void Clear() { Uploads.Clear(); _snapshot = null; Actors.Clear(); _snapshotBytes = 0; Observations.Clear(); PendingObservations.Clear(); UserChanges.Clear(); }
    internal static void Disconnect(int slot, Guid session)
    {
        if (Uploads.TryGetValue(slot, out var upload) && upload.Session == session) Uploads.Remove(slot);
    }
    internal static void Abort(int sender)
    {
        if (Main.netMode == NetmodeID.Server) Uploads.Remove(sender);
        else { _snapshot = null; Actors.Clear(); _snapshotBytes = 0; }
    }
    private static ModPacket Packet(Message type)
    {
        var packet = ModContent.GetInstance<EyesAchievementsMod>().GetPacket();
        packet.Write(Version); packet.Write((byte)type); return packet;
    }
    private static void GuidWrite(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static Guid GuidRead(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new Guid(bytes);
    }
    private static void TextWrite(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string TextRead(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > limit) throw new InvalidDataException("Achievement packet text exceeds its limit.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    internal static void SendProfile(AchievementPlayer player)
    {
        var packet = Packet(Message.ProfileBegin);
        GuidWrite(packet, player.Session); GuidWrite(packet, player.UserId); GuidWrite(packet, player.PlayerId); packet.Send();
        foreach (var record in ProfileRecords(player, true))
        {
            packet = Packet(Message.ProfileEntry); GuidWrite(packet, player.Session); Write(packet, record); packet.Send();
        }
        packet = Packet(Message.ProfileEnd); GuidWrite(packet, player.Session); packet.Send();
    }
    private static IEnumerable<Record> ProfileRecords(AchievementPlayer player, bool local)
    {
        var context = new AchievementContext(player.UserId, player.PlayerId, AchievementWorld.WorldId);
        foreach (var tuple in new[] { (AchievementScope.User, local ? AchievementWorld.UserStore! : player.Users), (AchievementScope.Player, player.Characters) })
        {
            Dictionary<string, string>? entries;
            lock (AchievementRuntime.Gate) entries = tuple.Item2.Entries.TryGetValue(context.Address(tuple.Item1), out var values) ? new(values, StringComparer.Ordinal) : null;
            if (entries is null) continue;
            foreach (var entry in entries)
                if (AchievementRuntime.Definitions.TryGetValue(entry.Key, out var definition) && definition.IsNetworked && definition.Scope == tuple.Item1)
                    yield return new(tuple.Item1, context, entry.Key, entry.Value, false);
        }
    }
    private static IEnumerable<Record> WorldRecords()
    {
        Dictionary<string, Dictionary<string, string>> snapshot;
        lock (AchievementRuntime.Gate) snapshot = AchievementWorld.WorldStore.Entries.ToDictionary(x => x.Key, x => new Dictionary<string, string>(x.Value, StringComparer.Ordinal));
        foreach (var address in snapshot)
            foreach (var entry in address.Value)
            {
                if (!AchievementRuntime.Definitions.TryGetValue(entry.Key, out var definition) || definition.Scope is not (AchievementScope.World or AchievementScope.PlayerWorld)) continue;
                var parts = address.Key.Split(':');
                if (!Guid.TryParse(parts[0], out var world) || world != AchievementWorld.WorldId) continue;
                Guid player = Guid.Empty;
                if (definition.Scope == AchievementScope.PlayerWorld && (parts.Length != 2 || !Guid.TryParse(parts[1], out player))) continue;
                yield return new(definition.Scope, new(Guid.Empty, player, world), entry.Key, entry.Value, false);
            }
    }
    private static void SendSnapshot(AchievementPlayer recipient)
    {
        int target = recipient.Player.whoAmI;
        var packet = Packet(Message.SnapshotBegin); GuidWrite(packet, AchievementWorld.WorldId); packet.Send(target);
        foreach (var player in new[] { recipient })
        {
            if (!player.Ready) continue;
            packet = Packet(Message.Actor); packet.Write((byte)player.Player.whoAmI); GuidWrite(packet, player.UserId); GuidWrite(packet, player.PlayerId); packet.Send(target);
            foreach (var record in ProfileRecords(player, false)) { packet = Packet(Message.Entry); Write(packet, record); packet.Send(target); }
        }
        foreach (var record in WorldRecords())
            if (record.Scope == AchievementScope.World || record.Context.PlayerId == recipient.PlayerId) { packet = Packet(Message.Entry); Write(packet, record); packet.Send(target); }
        Packet(Message.SnapshotEnd).Send(target);
    }
    private static void Write(BinaryWriter writer, Record record)
    {
        writer.Write((byte)record.Scope);
        GuidWrite(writer, record.Context.UserId); GuidWrite(writer, record.Context.PlayerId); GuidWrite(writer, record.Context.WorldId);
        TextWrite(writer, record.Key); TextWrite(writer, record.State); writer.Write(record.Notify);
    }
    private static Record Read(BinaryReader reader)
    {
        var scope = (AchievementScope)reader.ReadByte();
        var context = new AchievementContext(GuidRead(reader), GuidRead(reader), GuidRead(reader));
        string key = TextRead(reader, 192), state = TextRead(reader, ProgressStore.MaxEntryBytes);
        bool notify = reader.ReadBoolean();
        if (!Enum.IsDefined(scope) || !context.Supports(scope) || !AchievementRuntime.Definitions.TryGetValue(key, out var definition) || !definition.IsNetworked || definition.Scope != scope) throw new InvalidDataException("Unknown or inconsistent achievement record.");
        return new(scope, context, key, state, notify);
    }
    internal static void Changed(ScopedAchievement definition, AchievementContext context, string state, bool completed)
    {
        if (Main.netMode == NetmodeID.SinglePlayer || !definition.IsNetworked) return;
        context = WireContext(definition.Scope, context);
        var record = new Record(definition.Scope, context, definition.Id, state, completed);
        if (Main.netMode == NetmodeID.Server)
        {
            var packet = Packet(Message.Delta); Write(packet, record);
            int target = definition.Scope == AchievementScope.World ? -1 : AchievementPlayer.Find(context, definition.Scope)?.Player.whoAmI ?? -2;
            if (target >= -1) packet.Send(target);
        }
        else if (definition.Scope == AchievementScope.User)
        {
            var player = Main.LocalPlayer.GetModPlayer<AchievementPlayer>();
            if (!player.Ready || player.Sequence == uint.MaxValue) return;
            UserChanges[definition.Id] = record with { Notify = false };
        }
    }
    private static AchievementContext WireContext(AchievementScope scope, AchievementContext context)
    {
        var player = AchievementPlayer.Find(context, scope);
        if (Main.netMode == NetmodeID.MultiplayerClient && scope == AchievementScope.User) player = Main.LocalPlayer.GetModPlayer<AchievementPlayer>();
        return new(player?.UserId ?? context.UserId, player?.PlayerId ?? context.PlayerId, AchievementWorld.WorldId);
    }
    internal static void SendObservation(AchievementEvent kind, int id)
    {
        if (PendingObservations.Count < 4096 && PendingObservations.Add((kind, id))) Observations.Enqueue((kind, id));
    }
    internal static void FlushObservations()
    {
        if (Main.netMode != NetmodeID.MultiplayerClient || !AchievementWorld.Ready) return;
        var player = Main.LocalPlayer.GetModPlayer<AchievementPlayer>();
        if (!player.Ready || player.Sequence == uint.MaxValue) return;
        for (int count = 0; count < 32 && Observations.TryDequeue(out var observation); count++)
        {
            PendingObservations.Remove(observation);
            if (player.Sequence == uint.MaxValue) return;
            var packet = Packet(Message.Observation); GuidWrite(packet, player.Session); packet.Write(++player.Sequence); packet.Write((byte)observation.Kind); packet.Write(observation.Id); packet.Send();
        }
        if (UserChanges.Count > 0) foreach (string key in UserChanges.Keys.Take(32).ToArray())
        {
            if (player.Sequence == uint.MaxValue) return;
            var packet = Packet(Message.UserDelta); GuidWrite(packet, player.Session); packet.Write(++player.Sequence); Write(packet, UserChanges[key]); packet.Send();
            UserChanges.Remove(key);
        }
    }
    internal static void Receive(BinaryReader reader, int sender)
    {
        if (reader.ReadByte() != Version) throw new InvalidDataException("Unsupported achievement protocol version.");
        var message = (Message)reader.ReadByte();
        if (!Enum.IsDefined(message)) throw new InvalidDataException("Unknown achievement packet kind.");
        if (Main.netMode == NetmodeID.Server) ReceiveServer(reader, sender, message);
        else if (Main.netMode == NetmodeID.MultiplayerClient) ReceiveClient(reader, message);
    }
    private static void ReceiveServer(BinaryReader reader, int sender, Message message)
    {
        if (sender < 0 || sender >= Main.maxPlayers || !AchievementWorld.Ready) return;
        var player = Main.player[sender].GetModPlayer<AchievementPlayer>();
        if (message == Message.ProfileBegin)
        {
            if (player.Ready || Uploads.ContainsKey(sender)) return;
            Guid session = GuidRead(reader), user = GuidRead(reader), character = GuidRead(reader);
            if (session == Guid.Empty || user == Guid.Empty || character == Guid.Empty || AchievementPlayer.ByPlayer.ContainsKey(character) || AchievementPlayer.ByUser.ContainsKey(user)) throw new InvalidDataException("Achievement identity is missing or already connected.");
            player.Session = session;
            Uploads.Add(sender, new(session, user, character)); return;
        }
        if (message is Message.ProfileEntry or Message.ProfileEnd)
        {
            Guid session = GuidRead(reader);
            if (!Uploads.TryGetValue(sender, out var upload) || upload.Session != session) return;
            if (message == Message.ProfileEntry)
            {
                var record = Read(reader);
                if (record.Scope is not (AchievementScope.User or AchievementScope.Player) || record.Context.UserId != upload.User || record.Context.PlayerId != upload.Player || record.Notify) throw new InvalidDataException("A profile upload can only contain its own user and character state.");
                upload.Add(record with { Context = new(upload.User, upload.Player, AchievementWorld.WorldId) }); return;
            }
            var characters = new ProgressStore(); var users = new ProgressStore();
            if (AchievementPlayer.ByPlayer.ContainsKey(upload.Player) || AchievementPlayer.ByUser.ContainsKey(upload.User)) throw new InvalidDataException("Achievement identity is already connected.");
            foreach (var record in upload.Records)
            {
                AchievementRuntime.ValidateIncoming(AchievementRuntime.Definitions[record.Key], record.Context, record.State);
                (record.Scope == AchievementScope.User ? users : characters).Set(record.Context.Address(record.Scope), record.Key, record.State);
            }
            AchievementRuntime.ForgetPlayer(upload.User, upload.Player);
            player.PlayerId = upload.Player; player.UserId = upload.User; player.Session = upload.Session;
            player.Characters = characters; player.Users = users; player.LastSequence = 0; player.Ready = true;
            player.RegisterIdentity(); Uploads.Remove(sender); SendSnapshot(player);
            AchievementRuntime.AnnounceContext(AchievementApi.ForPlayer(player.Player));
            return;
        }
        if (message is not (Message.Observation or Message.UserDelta) || !player.Ready || !player.Player.active) return;
        Guid token = GuidRead(reader); uint sequence = reader.ReadUInt32();
        if (token != player.Session || sequence == 0 || sequence <= player.LastSequence) return;
        if (player.ObservationTick != Main.GameUpdateCount) { player.ObservationTick = Main.GameUpdateCount; player.ObservationCount = 0; }
        if (++player.ObservationCount > 256) return;
        player.LastSequence = sequence;
        if (message == Message.Observation)
        {
            var kind = (AchievementEvent)reader.ReadByte(); int id = reader.ReadInt32();
            if (!Enum.IsDefined(kind) || id < 0 || !AchievementRuntime.HasClientObservation(kind, id)) return;
            AchievementRuntime.Observe(kind, id, AchievementApi.ForPlayer(player.Player), true);
        }
        else
        {
            var record = Read(reader);
            if (record.Scope != AchievementScope.User || record.Context.UserId != player.UserId || record.Context.PlayerId != player.PlayerId || record.Context.WorldId != AchievementWorld.WorldId || record.Notify) throw new InvalidDataException("A user update cannot write another scope or identity.");
            AchievementRuntime.Apply(AchievementRuntime.Definitions[record.Key], record.Context, record.State, false);
        }
    }
    private static void ReceiveClient(BinaryReader reader, Message message)
    {
        switch (message)
        {
            case Message.SnapshotBegin:
                _snapshotWorld = GuidRead(reader);
                if (_snapshotWorld == Guid.Empty) throw new InvalidDataException("Empty world identity.");
                _snapshot = new(); Actors.Clear(); _snapshotBytes = 0; AchievementWorld.Ready = false;
                break;
            case Message.Actor:
                if (_snapshot is null) return;
                int slot = reader.ReadByte(); Guid user = GuidRead(reader), player = GuidRead(reader);
                if (slot >= Main.maxPlayers || user == Guid.Empty || player == Guid.Empty || Actors.Any(a => a.Slot == slot || a.Player == player || a.User == user)) throw new InvalidDataException("Invalid snapshot actor.");
                Actors.Add((slot, user, player)); break;
            case Message.Entry:
                if (_snapshot is null) return;
                var record = Read(reader);
                _snapshotBytes += Encoding.UTF8.GetByteCount(record.State);
                if (_snapshot.Count >= ProgressStore.MaxEntries * 3 || _snapshotBytes > ProgressStore.MaxStoreBytes * 3 || record.Context.WorldId != _snapshotWorld) throw new InvalidDataException("Achievement snapshot exceeds its bounds or references another world.");
                _snapshot.Add(record); break;
            case Message.SnapshotEnd:
                if (_snapshot is not null) CommitSnapshot(); break;
            case Message.Delta:
                if (!AchievementWorld.Ready) return;
                var delta = Read(reader);
                if (delta.Context.WorldId != AchievementWorld.WorldId) return;
                AchievementRuntime.Apply(AchievementRuntime.Definitions[delta.Key], delta.Context, delta.State, delta.Notify);
                break;
        }
    }
    private static void CommitSnapshot()
    {
        if (Actors.Count != 1 || Actors[0].Slot != Main.myPlayer) throw new InvalidDataException("A private snapshot must identify the local character.");
        var actor = Actors[0];
        var local = Main.LocalPlayer.GetModPlayer<AchievementPlayer>();
        if (local.UserId != actor.User || local.PlayerId != actor.Player) throw new InvalidDataException("Server snapshot changed the local character identity.");
        var world = new ProgressStore();
        var users = new ProgressStore(); var characters = new ProgressStore();
        lock (AchievementRuntime.Gate) { users.CopyFrom(AchievementWorld.UserStore!); characters.CopyFrom(local.Characters); }
        var keys = new HashSet<(AchievementScope, string)>();
        foreach (var record in _snapshot!)
        {
            if (record.Notify || !keys.Add((record.Scope, record.Key)) || record.Scope != AchievementScope.World && record.Context.PlayerId != actor.Player || record.Scope is AchievementScope.User or AchievementScope.Player && record.Context.UserId != actor.User)
                throw new InvalidDataException("Snapshot contains duplicate or unrelated private state.");
            AchievementRuntime.ValidateIncoming(AchievementRuntime.Definitions[record.Key], record.Context, record.State);
            var store = record.Scope is AchievementScope.World or AchievementScope.PlayerWorld ? world : record.Scope == AchievementScope.User ? users : characters;
            store.Set(record.Context.Address(record.Scope), record.Key, record.State);
        }
        AchievementRuntime.ClearSession(); AchievementPlayer.ClearIdentities();
        lock (AchievementRuntime.Gate)
        {
            AchievementWorld.WorldId = _snapshotWorld; AchievementWorld.WorldStore = world;
            AchievementWorld.UserStore = users; AchievementWorld.UserDirty = true;
            local.Characters = characters; local.Users = users; local.Ready = true;
        }
        local.RegisterIdentity();
        foreach (var record in _snapshot!) if (record.Scope == AchievementScope.PlayerWorld) AchievementPlayer.KnownPlayers.Add(record.Context.PlayerId);
        AchievementWorld.Ready = true; _snapshot = null; Actors.Clear();
        AchievementRuntime.RefreshAll();
        AchievementRuntime.AnnounceContext(AchievementApi.LocalContext);
        AchievementWorld.ObserveOwnedItems();
    }
    private sealed record Record(AchievementScope Scope, AchievementContext Context, string Key, string State, bool Notify);
    private sealed class Upload(Guid session, Guid user, Guid player)
    {
        internal Guid Session { get; } = session;
        internal Guid User { get; } = user;
        internal Guid Player { get; } = player;
        internal List<Record> Records { get; } = new();
        private int _bytes;
        private readonly HashSet<(AchievementScope, string)> _keys = new();
        internal void Add(Record record)
        {
            _bytes += Encoding.UTF8.GetByteCount(record.State);
            if (Records.Count >= 4096 || _bytes > ProgressStore.MaxStoreBytes * 2 || !_keys.Add((record.Scope, record.Key))) throw new InvalidDataException("Invalid or excessive profile upload.");
            Records.Add(record);
        }
    }
}
