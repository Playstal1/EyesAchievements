# Eyes'Achievements

A tModLoader achievement library with independent **User**, **World**, **Player**, and **PlayerWorld** progress. Internal mod name: `EyesAchievements`. Licensed under MIT.

The library targets tModLoader's 1.4.4 stable API (`2026.08`) and .NET 8. It requires no third-party mod. It does not create Steam achievements or synchronize a Steam account.

The library itself is `side = Both` and must be installed on both client and server in multiplayer. Consumers using World, Player, or PlayerWorld must also use `side = Both`. Client/NoSync consumers may register local User achievements; those definitions and their private data are not uploaded to a server. Server-only consumers are rejected with a load-time diagnostic.

## Choose a scope

| Scope | Identity and storage | Multiplayer authority |
| --- | --- | --- |
| `User` | Local tModLoader save-data profile; `EyesAchievements/user.json` | Local client. Its reported state is available to the server after synchronization. |
| `World` | World's persistent GUID; world `.twld` data | Server, shared by all players. |
| `Player` | Persistent character GUID; character `.tplr` data | Character provides the initial state; server controls changes during the session. |
| `PlayerWorld` | Character GUID and world GUID; world `.twld` data | Server. The character has independent progress in each world. |

A copied character file retains the same character identity. These GUIDs identify save data; they are not authentication and do not protect against edited saves. Concurrent connections using the same profile or character identity are rejected by the library's synchronization handshake.

On a multiplayer client, queries cover the local user's profile, its character, its character in the current world, and shared world progress. Other clients' private condition data is not broadcast. On the server, queries cover connected users and characters, shared world progress, and saved character-in-world progress for the current world. An unloaded world or an offline user's/character's unavailable data returns `ContextUnavailable`.

## Add an achievement

Add `modReferences = EyesAchievements` to your `build.txt` and reference the library assembly in your IDE project. A `ScopedAchievement` subclass is automatically loaded on both client and dedicated server.

```csharp
using EyesAchievements;
using Terraria.ID;

public sealed class SharedVictory : ScopedAchievement
{
    public override AchievementScope Scope => AchievementScope.World;
    public override string TextureName => "MyMod/Assets/SharedVictory";

    public override void SetStaticDefaults()
    {
        AddNPCKilledCondition("KingSlime", NPCID.KingSlime);
    }
}
```

Provide `Mods.MyMod.Achievements.SharedVictory.FriendlyName` and `.Description` localization entries. Textures use the native `ModAchievement` layout: a 64×64 unlocked icon at x=0, a locked icon at x=66, and rows spaced 66 pixels apart. `Index` selects a row. `Hidden`, `Category`, `GetDefaultPosition`, `GetAdvisorPosition`, and `GetModdedConstraints` retain native presentation semantics. `NativePresentation` exposes the client-only proxy for native ordering references; it is null on dedicated servers. Never use its mutable condition state as gameplay authority.

All conditions must complete to complete an achievement. Conditions must have unique, stable keys. Register conditions in `SetStaticDefaults`; runtime registration is rejected. Achievement IDs are `ModName/Name`. Do not change an existing ID or scope to represent a different achievement. Names beginning `__eyes_` are reserved for internal condition metadata.

## Read and update progress

```csharp
var context = AchievementApi.ForPlayer(player);
if (AchievementApi.TryIsCompleted("MyMod/SharedVictory", context, out bool completed)
    && completed)
{
    EnableAbility(player);
}
```

`TryIsCompleted` returns false when the achievement is unknown or the context is unavailable. `GetStatus(id, context, out completed)` distinguishes those cases. The warmed-up completion check uses dictionary lookups and does not serialize conditions or allocate a progress snapshot. Use `Get` for UI snapshots with `Status`, `IsCompleted`, `Trust`, typed `Values` (completed/current/target), and serialized custom `Conditions`. `Get` allocates a detached snapshot and should not run every frame merely to check a boolean.

Contexts can also be created explicitly with `AchievementContext.ForUser(userId)`, `ForWorld(worldId)`, `ForPlayer(playerId)`, and `ForPlayerWorld(playerId, worldId)`. `LocalContext` has only a user identity in the main menu. World/character queries remain unavailable there. All runtime public APIs run on the game thread; use `Main.QueueMainThreadAction` from a background task.

Use `AddCondition`, `AddIntCondition`, `AddFloatCondition`, `AddNPCKilledCondition`, `AddItemCraftCondition`, `AddItemPickupCondition`, and `AddTileDestroyedCondition`. The item/NPC helpers support one ID, an array interpreted as any matching ID, and `AddMany...` variants that create a condition per ID. Built-in helpers are flags, like the native helpers; use a numeric condition for kill or item counts.

```csharp
public ConditionHandle<CustomIntCondition> Training { get; private set; } = null!;

public override void SetStaticDefaults()
{
    Training = AddIntCondition("Training", 100);
}

Training.Mutate(context, counter => counter.Value++);
AchievementApi.AddInt("MyMod/TrainingAchievement", "Training", context, 1);
```

`Complete`, `SetInt`, `AddInt`, `SetFloat`, `Mutate<T>`, and `Reset` validate context and authority. Numeric helpers clamp as their native conditions do; NaN and infinity are rejected. A mutation is captured and validated before committing; failure restores previous condition data. Do not retain a mutable condition reference outside the mutation callback, invoke asynchronous work inside it, or mutate the same achievement recursively. Update other gameplay state after a successful call or in the completion callback.

`OnCompleted(AchievementCompletion)` and `AchievementApi.Completed` run once for a new incomplete-to-complete transition on the side that owns the mutation. They do not run on loading or network synchronization. The event includes the achievement ID, scope, context, and trust. `ProgressChanged` also reports subsequent synchronized changes, with `FromSynchronization=true`; it does not grant gameplay rewards. `ContextReady` tells a consumer when initial local/connection state is available. Unsubscribe static-event handlers during your mod's `Unload`.

For another connected player's ability check, run `AchievementApi.TryIsCompleted(id, AchievementApi.ForPlayer(otherPlayer), out completed)` on the server. A client does not infer another player's status from missing private data.

## Multiplayer observations and trust

`ObservationPolicy.ServerOnly` is the default. NPC kill helpers run from server-observed NPC deaths and credit interacting players; World conditions naturally contribute to shared progress. Crafting, pickup, and player-mined tile helpers originate on the client in Terraria. In single-player they work directly. For non-User scopes in multiplayer, explicitly choose one of these approaches:

* Keep `ServerOnly` and have your server-side gameplay code confirm an event using `AchievementApi.Observe(kind, contentId, player)` or mutate the relevant condition.
* Override `ObservationPolicy` with `AllowClientObserved` to accept the registered craft/pickup/tile observations. This is an explicit trust decision, not anti-cheat verification.

The client-observation protocol accepts only registered helper event kinds/content IDs. It checks the sending connection, session token, increasing sequence, payload sizes, identity, scope, and rate. It never accepts a client request to complete an arbitrary World/PlayerWorld custom condition. NPC-kill observation packets are not accepted from clients.

`ProgressTrust.Local` means local single-player/profile state. `ServerSession` means server-owned world state. `ClientReported` marks uploaded user/character state and world progress affected by accepted client observations. Client-observed provenance is saved with the achievement and survives reconnect/reload and later policy changes; resetting that achievement clears its observation provenance. Character saves remain a client-supplied baseline even though the server controls their session updates.

Transport sends an initial snapshot to the joining player and subsequent changed achievement entries. It does not scan all achievements every tick. User/character/character-in-world updates are addressed to their owner; world updates are shared. Initial snapshot imports preserve data from disabled consumer mods. Malformed upload/snapshot transactions are aborted rather than committing an accepted prefix.

## Custom conditions and trackers

Use a factory that returns a fresh native `AchievementCondition` for every context. A custom condition must serialize its state with Newtonsoft.Json and restore silently through `Load(JObject)`, as native conditions do. Mutation callbacks are the explicit dirty-state boundary for partial progress; merely retaining an instance and changing it later is unsupported.

```csharp
AddCondition("Custom", () => new MyCondition("Custom"), condition => condition.Dispose());
```

When runtime construction needs the context, provide a separate context-independent presentation factory. Presentation instances are created while mods load, before any character/world is selected. Runtime factories receive only the owning scope's identity: UserId for User, WorldId for World, PlayerId for Player, and the character/world pair for PlayerWorld. A shared world instance is never bound to whichever player happened to query it first.

```csharp
AddCondition("Custom",
    context => new MyCondition("Custom", context.PlayerId),
    () => new MyCondition("Custom"),
    condition => condition.Dispose());
```

Factories should construct state, not grant rewards or subscribe an unscoped instance to global gameplay events. Hook gameplay once in your mod and call the scoped mutation API. A reused condition instance is rejected. Release callbacks run when runtime instances or projections are disposed, including partial construction failures. `ReleaseTracker` handles custom tracker ownership (the default disposes `IDisposable` trackers).

Override `ConfigureTracker(Achievement)` to use a native custom tracker. Terraria's menu requires `AchievementTracker<int>` or `AchievementTracker<float>` matching its `TrackerType`; a bare `IAchievementTracker` implementation is insufficient. Trackers should derive their loaded value from condition state. ConfigureTracker runs separately for runtime and presentation instances; never share a mutable tracker between contexts. Single numeric conditions and compound-condition trackers are configured automatically by default.

## Optional dependency with Mod.Call

With `weakReferences = EyesAchievements`, registration can be performed in your mod's `Load` without linking against this library's types:

```csharp
if (ModLoader.TryGetMod("EyesAchievements", out var library))
{
    library.Call("Register", this, "FirstStep", new Dictionary<string, object>
    {
        ["Scope"] = "PlayerWorld",
        ["Texture"] = "MyMod/Assets/FirstStep",
        ["Conditions"] = new[]
        {
            new Dictionary<string, object> { ["Key"] = "Done", ["Kind"] = "Flag" }
        }
    });
}
```

Registration options also accept `ObservationPolicy` (string), `Index` (int), `Hidden` (bool), `Category` (int), native `Position`/`AdvisorPosition`/`Constraints`, `ConfigureTracker` (`Action<Achievement>`), and `OnCompleted` (`Action<IReadOnlyDictionary<string,object>>`). Condition kinds are `Flag`, `Int` (`Max:int`), `Float` (`Max:float`), `NpcKilled`, `ItemCrafted`, `ItemPickedUp`, `TileDestroyed` (`Ids:int[]`), and `Custom` (`Factory:Func<AchievementCondition>`, optional `Release:Action<AchievementCondition>`). Localization uses the same keys as a typed definition.

| Call | Arguments after operation | Result |
| --- | --- | --- |
| `ApiVersion` | none | `int`, currently 1 |
| `Register` | owner Mod, name, options dictionary | full achievement ID |
| `Context` | Terraria.Player | opaque context object |
| `LocalContext` | none | opaque context object |
| `IsCompleted` | ID, context | `bool`, or null when unavailable/unknown |
| `Get` | ID, context | read-only dictionary with Status, IsCompleted, Trust, Conditions, Values |
| `Complete` | ID, condition key, context | bool: changed |
| `SetInt` / `AddInt` | ID, condition key, context, int | bool: changed |
| `SetFloat` | ID, condition key, context, float | bool: changed |
| `Mutate` | ID, condition key, context, `Action<AchievementCondition>` | bool: changed |
| `Observe` | event kind string, content ID, Terraria.Player | true when dispatched |
| `Reset` | ID, context | bool: changed |
| `List` | none | read-only ID list |

A context argument may also be a `Terraria.Player` or `Guid[3]` containing user, character, and world GUIDs. Wrong operation names, argument types, scope authority, or condition types produce descriptive exceptions.

`Get.Values` is an `IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>`. Each condition contains `IsCompleted` (bool), `Value` (nullable double), and `Target` (nullable double), so an optional consumer needs no library types to inspect standard progress. `Get.Conditions` is an `IReadOnlyDictionary<string,string>` for advanced custom state.

## Persistence and native UI

Progress uses versioned data and stable names. Unknown achievements and conditions survive when their consumer is disabled. World/character-in-world data follows the world's `.twld`; character data follows `.tplr`; user data uses an atomic replace with a `.bak` backup. Unsupported future save versions fail explicitly instead of being overwritten. Per-store limits are 16,384 achievement entries and 8 MiB of condition state; one achievement supports 256 conditions and a 24 KiB UTF-8 state. Registration supports up to 4,096 definitions. These limits bound storage and network payloads and are validated before writes.

World and character save tags use version 2 and store the JSON envelope as UTF-8 bytes, avoiding the NBT string-length limit. The reader also accepts the earlier development version 1 string tags (including world tags without an explicit version). The JSON envelope itself remains version 1; changing its NBT container does not change achievement IDs or condition state.

Native proxies provide the standard menu, filters, hidden text, icons, tracking, ordering, advisor, and completion popup. Their transient data is excluded from native `achievements.dat` save/load by version-checked IL hooks that filter local IO snapshots, without changing the shared native registry. The library fails to load if those tModLoader implementation points change unexpectedly.

The native reset button resets scopes writable locally in the active context. On a multiplayer client it resets User progress and restores authoritative World/Player/PlayerWorld presentation. Server-owned progress can be reset through the server-side API. Switching worlds/characters reloads the corresponding projection silently; loading already completed progress does not generate completion callbacks or popups.

## Examples and development

`Examples/ExampleConsumer` contains independent world and character achievements and a ready-to-build project. After building the library, run `dotnet build Examples/ExampleConsumer/ExampleConsumer.csproj -p:BuildMod=false`. To play it, copy that folder into your ModSources beside EyesAchievements and build it through tModLoader. The sample project supports both locations. Example/test sources are excluded from the library assembly and `.tmod` package.

The project imports the normal parent `tModLoader.targets`. For a standalone checkout, place it under ModSources or supply the normal parent targets file for your installation. `dotnet build -p:BuildMod=false` compiles without writing a package to the active player profile.
