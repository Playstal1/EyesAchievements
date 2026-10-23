using System;
using System.Collections.Generic;
using System.Reflection;
using EyesAchievements.Runtime;
using Terraria;
using Terraria.Achievements;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;

namespace EyesAchievements.UI;

internal sealed class GroupAchievementsMenu(AchievementGroup group) : UIAchievementsMenu
{
    private readonly AchievementGroup _group = group;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo AchievementElements = typeof(UIAchievementsMenu).GetField("_achievementElements", Flags)!;
    private static readonly FieldInfo List = typeof(UIAchievementsMenu).GetField("_achievementsList", Flags)!;
    private static readonly MethodInfo CloseConfirm = typeof(UIAchievementsMenu).GetMethod("CloseAchievementConfirm", Flags)!;

    public override void OnActivate()
    {
        base.OnActivate();
        var elements = (List<UIAchievementListItem>)AchievementElements.GetValue(this)!;
        elements.RemoveAll(item => item.GetAchievement().ModAchievement?.Mod != _group.Owner); // Поиск и категории заново строят список из этой коллекции.
        var list = (UIList)List.GetValue(this)!;
        list.Clear();
        foreach (var item in elements) list.Add(item);
        list.UpdateOrder();
        Recalculate();
    }

    internal static void Reset(Action<UIAchievementsMenu, UIMouseEvent, UIElement> original, UIAchievementsMenu menu, UIMouseEvent evt, UIElement element)
    {
        if (menu is not GroupAchievementsMenu filtered) { original(menu, evt, element); return; }
        CloseConfirm.Invoke(menu, new object[] { evt, element });
        bool denied = false;
        foreach (Achievement achievement in filtered._group.Achievements)
        {
            if (achievement.ModAchievement is NativeAchievement native)
            {
                if ((Main.netMode == NetmodeID.MultiplayerClient && native.Definition.Scope != AchievementScope.User) || !AchievementApi.TryIsCompleted(native.Definition.Id, AchievementApi.LocalContext, out _)) { denied = true; continue; }
                AchievementApi.Reset(native.Definition.Id, AchievementApi.LocalContext); // Сбрасываем хранилище выбранной области, а не только клиентское представление.
            }
            else achievement.ClearProgress();
        }
        Main.Achievements.Save();
        Main.menuMode = 0;
        IngameFancyUI.Close();
        if (denied) Main.NewText(Language.GetTextValue("Mods.EyesAchievements.UI.ServerReset"));
        SoundEngine.PlaySound(SoundID.MenuClose);
    }
}

