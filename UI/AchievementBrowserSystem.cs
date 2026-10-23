using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using ReLogic.Content;
using Terraria;
using Terraria.Achievements;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace EyesAchievements.UI;

[Autoload(Side = ModSide.Client)]
public sealed class AchievementBrowserSystem : ModSystem
{
    private Hook? _drawHook;
    private Hook? _resetHook;
    private readonly List<AchievementGroup> _groups = new();
    private int _selected;
    private int _offset;
    private bool _expanded;
    private float _reveal;
    private static readonly FieldInfo Cards = typeof(AchievementAdvisor).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public override void Load()
    {
        _drawHook = new Hook(typeof(AchievementAdvisor).GetMethod(nameof(AchievementAdvisor.DrawOneAchievement))!, DrawAdvisor);
        _resetHook = new Hook(typeof(Terraria.GameContent.UI.States.UIAchievementsMenu).GetMethod("ResetAchievements", BindingFlags.Instance | BindingFlags.NonPublic)!, GroupAchievementsMenu.Reset);
    }

    public override void Unload()
    {
        _drawHook?.Dispose();
        _resetHook?.Dispose();
        _drawHook = _resetHook = null;
        _groups.Clear();
    }

    public override void UpdateUI(GameTime gameTime)
    {
        if (!Main.playerInventory || Main.gameMenu) _expanded = false;
        _reveal = _expanded ? Math.Min(1f, _reveal + (float)gameTime.ElapsedGameTime.TotalSeconds * 8f) : 0f;
    }

    private void EnsureGroups()
    {
        if (_groups.Count != 0) return;
        var achievements = Main.Achievements.CreateAchievementsList();
        _groups.Add(new AchievementGroup(null, achievements.Where(a => a.ModAchievement == null).ToArray()));
        foreach (var group in achievements.Where(a => a.ModAchievement != null).GroupBy(a => a.ModAchievement.Mod).OrderBy(g => g.Key.DisplayNameClean, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key.Name, StringComparer.Ordinal))
            _groups.Add(new AchievementGroup(group.Key, group.ToArray()));
    }

    private void DrawAdvisor(Action<AchievementAdvisor, SpriteBatch, Vector2, bool> original, AchievementAdvisor advisor, SpriteBatch batch, Vector2 position, bool large)
    {
        if (Main.gameMenu || !Main.playerInventory)
        {
            original(advisor, batch, position, large);
            return;
        }
        EnsureGroups();
        float scale = large ? 0.75f : 0.35f;
        int size = (int)(64 * scale);
        Vector2 start = position + new Vector2(8 * scale);
        AchievementGroup selected = _groups[_selected];
        Achievement? current = null;
        if (_selected == 0)
        {
            foreach (var card in (List<AchievementAdvisorCard>)Cards.GetValue(advisor)!)
                if (card.achievement.ModAchievement == null && !card.achievement.IsCompleted && !card.achievement.Hidden && card.IsAchievableInWorld()) { current = card.achievement; break; }
        }
        else current = selected.Achievements.FirstOrDefault(a => !a.IsCompleted && !a.Hidden && Available(a));
        var mainBounds = new Rectangle((int)start.X, (int)start.Y, size, size);
        DrawIcon(batch, mainBounds, selected, current);
        if (Hover(mainBounds))
        {
            Main.instance.MouseText(current?.FriendlyName.Value ?? selected.Title + " — " + Language.GetTextValue(selected.Achievements.All(a => a.IsCompleted) ? "Mods.EyesAchievements.UI.Completed" : "Mods.EyesAchievements.UI.View"));
            if (Click())
            {
                _expanded = false;
                Main.ingameOptionsWindow = false;
                var menu = new GroupAchievementsMenu(selected);
                IngameFancyUI.OpenUIState(menu);
                if (current != null) menu.GotoAchievement(current);
            }
        }
        int gap = 8;
        int arrowWidth = Math.Max(18, size / 2);
        float screenWidth = Main.screenWidth;
        int available = Math.Max(1, (int)((screenWidth - start.X - size - arrowWidth - 24) / (size + gap)));
        int visible = Math.Min(_groups.Count, Math.Min(available, 8));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _groups.Count - visible));
        var arrow = new Rectangle(mainBounds.Right + gap + (_expanded ? (int)(visible * (size + gap) * _reveal) : 0), mainBounds.Y, arrowWidth, size);
        bool arrowHover = Hover(arrow);
        DrawArrow(batch, arrow, _expanded, arrowHover ? Color.White : Color.White * 0.5f);
        if (arrowHover)
        {
            Main.instance.MouseText(Language.GetTextValue("Mods.EyesAchievements.UI.SelectMod"));
            if (Click()) { _expanded = !_expanded; SoundEngine.PlaySound(SoundID.MenuTick); }
        }
        if (!_expanded) return;
        var strip = new Rectangle(mainBounds.Right + gap, mainBounds.Y, visible * (size + gap), size);
        if (_reveal >= 1f && Hover(strip) && PlayerInput.ScrollWheelDeltaForUI != 0)
        {
            _offset = Math.Clamp(_offset - Math.Sign(PlayerInput.ScrollWheelDeltaForUI), 0, Math.Max(0, _groups.Count - visible));
            PlayerInput.LockVanillaMouseScroll("EyesAchievements/Groups");
        }
        for (int i = 0; i < visible; i++)
        {
            int revealSize = Math.Max(1, (int)(size * _reveal));
            var bounds = new Rectangle(strip.X + (int)(i * (size + gap) * _reveal), strip.Y + (size - revealSize) / 2, revealSize, revealSize);
            var group = _groups[_offset + i];
            DrawIcon(batch, bounds, group, null);
            if (_reveal < 1f || !Hover(bounds)) continue;
            Main.instance.MouseText(group.Title);
            if (Click()) { _selected = _offset + i; _expanded = false; SoundEngine.PlaySound(SoundID.MenuTick); break; }
        }
    }

    private static bool Available(Achievement achievement) => achievement.ModAchievement is not Runtime.NativeAchievement native || AchievementApi.TryIsCompleted(native.Definition.Id, AchievementApi.LocalContext, out _);
    private static bool Hover(Rectangle bounds)
    {
        if (PlayerInput.IgnoreMouseInterface || !bounds.Contains(Main.MouseScreen.ToPoint())) return false;
        Main.LocalPlayer.mouseInterface = true;
        return true;
    }
    private static bool Click()
    {
        if (!Main.mouseLeft || !Main.mouseLeftRelease) return false;
        Main.mouseLeftRelease = false;
        return true;
    }
    private static void DrawIcon(SpriteBatch batch, Rectangle bounds, AchievementGroup group, Achievement? achievement)
    {
        Texture2D texture;
        Rectangle? frame = null;
        if (achievement != null)
        {
            int index = Main.Achievements.GetIconIndex(achievement.Name);
            texture = achievement.ModAchievement?.Texture.Value ?? Main.Assets.Request<Texture2D>("Images/UI/Achievements").Value;
            frame = achievement.ModAchievement != null ? new Rectangle(66, index * 66, 64, 64) : new Rectangle(index % 8 * 66 + 528, index / 8 * 66, 64, 64);
        }
        else
        {
            texture = group.Icon.Value;
            frame = group.IconFrame;
        }
        batch.Draw(texture, bounds, frame, Color.White);
        float scale = bounds.Width / 64f;
        batch.Draw(Main.Assets.Request<Texture2D>("Images/UI/Achievement_Borders").Value, new Vector2(bounds.X - 4 * scale, bounds.Y - 4 * scale), null, Color.White, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }
    private static void DrawArrow(SpriteBatch batch, Rectangle bounds, bool left, Color color)
    {
        Vector2 center = bounds.Center.ToVector2();
        float half = Math.Min(12, bounds.Height * 0.3f);
        Vector2 tip = center + new Vector2(left ? -half / 2 : half / 2, 0);
        Vector2 top = center + new Vector2(left ? half / 2 : -half / 2, -half);
        Vector2 bottom = new(top.X, center.Y + half);
        for (int i = 0; i < 2; i++)
        {
            Vector2 delta = (i == 0 ? top : bottom) - tip;
            batch.Draw(TextureAssets.MagicPixel.Value, tip, new Rectangle(0, 0, 1, 1), color, delta.ToRotation(), Vector2.Zero, new Vector2(delta.Length(), 2), SpriteEffects.None, 0);
        }
    }
}

internal sealed class AchievementGroup
{
    internal Mod? Owner { get; }
    internal Achievement[] Achievements { get; }
    internal string Title => Owner?.DisplayNameClean ?? "Terraria";
    internal Asset<Texture2D> Icon { get; }
    internal Rectangle? IconFrame { get; }
    internal AchievementGroup(Mod? owner, Achievement[] achievements)
    {
        Owner = owner;
        Achievements = achievements.OrderBy(a => a.Id).ToArray();
        if (owner != null && ModContent.RequestIfExists<Texture2D>(owner.Name + "/icon", out var icon, AssetRequestMode.ImmediateLoad))
        {
            Icon = icon;
            return;
        }
        Achievement fallback = owner != null ? Achievements.FirstOrDefault(a => !a.Hidden) ?? Main.Achievements.GetAchievement("TIMBER") : Main.Achievements.GetAchievement("TIMBER");
        int index = Main.Achievements.GetIconIndex(fallback.Name);
        Icon = fallback.ModAchievement?.Texture ?? Main.Assets.Request<Texture2D>("Images/UI/Achievements", AssetRequestMode.ImmediateLoad);
        IconFrame = fallback.ModAchievement != null ? new Rectangle(0, index * 66, 64, 64) : new Rectangle(index % 8 * 66, index / 8 * 66, 64, 64);
    }
}




