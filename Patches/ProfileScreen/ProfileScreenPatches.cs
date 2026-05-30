using BetterSaveSlots.Configuration;
using BetterSaveSlots.Events;
using Godot;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using System.Runtime.CompilerServices;

namespace BetterSaveSlots.Patches.ProfileScreen;

[HarmonyPatch]
internal static partial class ProfileScreenPatches
{
    private const string CopyIconPath = "res://BetterSaveSlots/ui/profile/copy_icon.png";
    private const string PasteIconPath = "res://BetterSaveSlots/ui/profile/paste_icon.png";
    private const string ImportIconPath = "res://BetterSaveSlots/ui/profile/import_icon.png";
    private const string PreviousIconPath = "res://BetterSaveSlots/ui/profile/prev_icon.png";
    private const string NextIconPath = "res://BetterSaveSlots/ui/profile/next_icon.png";

    private const float SlotActionSpacing = 86f;
    private const float PageButtonGap = 24f;
    private const float FallbackSlotWidth = 450f;
    private const float FallbackSlotHeight = 568f;
    private const float FallbackButtonWidth = 72f;
    private const float FallbackButtonHeight = 72f;
    private const float SlotActionBottomGap = 30f;
    private const int DeferredLayoutFrames = 2;

    private static readonly ConditionalWeakTable<NProfileScreen, ProfileScreenState> States = [];
    private static readonly List<WeakReference<NProfileScreen>> KnownScreens = [];
    private static readonly Dictionary<ulong, ActionButtonInfo> ActionButtons = [];
    private static readonly Dictionary<string, Texture2D?> IconCache = [];

    private static bool eventsSubscribed;

    private static readonly AccessTools.FieldRef<NProfileScreen, List<NProfileButton>> ProfileButtonsRef =
        AccessTools.FieldRefAccess<NProfileScreen, List<NProfileButton>>("_profileButtons");

    private static readonly AccessTools.FieldRef<NProfileScreen, List<NDeleteProfileButton>> DeleteButtonsRef =
        AccessTools.FieldRefAccess<NProfileScreen, List<NDeleteProfileButton>>("_deleteButtons");

    [HarmonyPatch(typeof(NProfileScreen), nameof(NProfileScreen._Ready))]
    [HarmonyPostfix]
    private static void NProfileScreenReadyPostfix(NProfileScreen __instance)
    {
        Install(__instance);
    }

    [HarmonyPatch(typeof(NProfileScreen), nameof(NProfileScreen.Refresh))]
    [HarmonyPostfix]
    private static void NProfileScreenRefreshPostfix(NProfileScreen __instance)
    {
        Install(__instance);
        UpdateScreen(__instance, preferCurrentProfile: true);
        QueueDeferredLayoutUpdate(__instance);
    }

    [HarmonyPatch(typeof(NSubmenu), nameof(NSubmenu.OnSubmenuClosed))]
    [HarmonyPostfix]
    private static void NSubmenuClosedPostfix(NSubmenu __instance)
    {
        if (__instance is NProfileScreen screen)
        {
            ClearCopiedProfile(screen, refresh: false, "离开存档界面");
        }
    }

    [HarmonyPatch(typeof(NProfileIcon), nameof(NProfileIcon.SetProfileId))]
    [HarmonyPrefix]
    private static bool NProfileIconSetProfileIdPrefix(NProfileIcon __instance, int profileId)
    {
        if (profileId <= BetterSaveSlotsSettings.VanillaSlotCount)
        {
            return true;
        }

        try
        {
            int fallbackId = ((profileId - 1) % BetterSaveSlotsSettings.VanillaSlotCount) + 1;
            TextureRect icon = AccessTools.FieldRefAccess<NProfileIcon, TextureRect>("_icon")(__instance);
            icon.Texture = ResourceLoader.Load<Texture2D>(
                ImageHelper.GetImagePath($"ui/profile/profile_icon_{fallbackId}.png"),
                null,
                ResourceLoader.CacheMode.Reuse);
            return false;
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"设置扩展槽图标失败，Profile={profileId}。", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(NDeleteProfileButton), "OnRelease")]
    [HarmonyPrefix]
    private static bool NDeleteProfileButtonOnReleasePrefix(NDeleteProfileButton __instance)
    {
        if (!ActionButtons.TryGetValue(__instance.GetInstanceId(), out ActionButtonInfo? actionInfo))
        {
            if (IsExtendedDeleteButton(__instance))
            {
                return true;
            }

            if (__instance.Name.ToString().StartsWith("BetterSaveSlots", StringComparison.Ordinal))
            {
                ModLogger.Warn($"BetterSaveSlots 自定义按钮失去动作注册，已阻止落回原生删除逻辑：{__instance.Name}。");
                return false;
            }

            return true;
        }

        if (!actionInfo.Screen.TryGetTarget(out NProfileScreen? screen) || !GodotObject.IsInstanceValid(screen))
        {
            return false;
        }

        switch (actionInfo.Kind)
        {
            case ProfileActionKind.CopyPaste:
                _ = TaskHelper.RunSafely(OnCopyPasteButtonPressedAsync(screen, actionInfo.ProfileId));
                break;
            case ProfileActionKind.Import:
                _ = TaskHelper.RunSafely(OnImportButtonPressedAsync(screen, actionInfo.ProfileId));
                break;
            case ProfileActionKind.PreviousPage:
                TurnPage(screen, -1);
                break;
            case ProfileActionKind.NextPage:
                TurnPage(screen, 1);
                break;
        }

        return false;
    }

    public static void RefreshKnownScreens()
    {
        for (int i = KnownScreens.Count - 1; i >= 0; i--)
        {
            if (!KnownScreens[i].TryGetTarget(out NProfileScreen? screen) || !GodotObject.IsInstanceValid(screen))
            {
                KnownScreens.RemoveAt(i);
                continue;
            }

            screen.Refresh();
        }
    }

    private static void Install(NProfileScreen screen)
    {
        if (States.TryGetValue(screen, out _))
        {
            EnsureSlotControls(screen);
            return;
        }

        _ = States.GetValue(screen, _ => new ProfileScreenState());
        KnownScreens.Add(new WeakReference<NProfileScreen>(screen));
        if (!eventsSubscribed)
        {
            BetterSaveSlotsEvents.ProfilesChanged += RefreshKnownScreens;
            eventsSubscribed = true;
        }

        screen.TreeExiting += () =>
        {
            ClearCopiedProfile(screen, refresh: false, "销毁存档界面");
            States.Remove(screen);
            RemoveActionButtonsForScreen(screen);
        };

        EnsureSlotControls(screen);
        UpdateScreen(screen, preferCurrentProfile: true);
        QueueDeferredLayoutUpdate(screen);
    }

    private static void QueueDeferredLayoutUpdate(NProfileScreen screen)
    {
        if (!States.TryGetValue(screen, out ProfileScreenState? state) || state.DeferredLayoutUpdateQueued)
        {
            return;
        }

        state.DeferredLayoutUpdateQueued = true;
        _ = TaskHelper.RunSafely(UpdateScreenAfterLayoutAsync(screen));
    }

    private static async Task UpdateScreenAfterLayoutAsync(NProfileScreen screen)
    {
        try
        {
            for (int i = 0; i < DeferredLayoutFrames; i++)
            {
                if (!GodotObject.IsInstanceValid(screen))
                {
                    return;
                }

                SceneTree? tree = screen.GetTree();
                if (tree == null)
                {
                    return;
                }

                await screen.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            if (!GodotObject.IsInstanceValid(screen))
            {
                return;
            }

            EnsureSlotControls(screen);
            UpdateScreen(screen, preferCurrentProfile: false);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(screen) && States.TryGetValue(screen, out ProfileScreenState? state))
            {
                state.DeferredLayoutUpdateQueued = false;
            }
        }
    }

    private static void EnsureSlotControls(NProfileScreen screen)
    {
        List<NProfileButton> profileButtons = ProfileButtonsRef(screen);
        List<NDeleteProfileButton> deleteButtons = DeleteButtonsRef(screen);
        int desiredCount = BetterSaveSlotsSettings.EffectiveSlotCount;

        EnsureProfileButtons(profileButtons, desiredCount);
        EnsureDeleteButtons(deleteButtons, desiredCount);

        ProfileScreenState state = States.GetOrCreateValue(screen);
        while (state.CopyPasteButtons.Count < desiredCount)
        {
            int profileId = state.CopyPasteButtons.Count + 1;
            NDeleteProfileButton button = CreateActionButton(
                screen,
                deleteButtons[profileId - 1],
                $"BetterSaveSlotsCopyPasteButton{profileId}",
                ProfileActionKind.CopyPaste,
                profileId,
                CopyIconPath,
                "UI.copy");
            state.CopyPasteButtons.Add(button);
        }

        while (state.ImportButtons.Count < desiredCount)
        {
            int profileId = state.ImportButtons.Count + 1;
            NDeleteProfileButton button = CreateActionButton(
                screen,
                deleteButtons[profileId - 1],
                $"BetterSaveSlotsImportButton{profileId}",
                ProfileActionKind.Import,
                profileId,
                ImportIconPath,
                "UI.import");
            state.ImportButtons.Add(button);
        }

        EnsurePageButtons(screen, state, deleteButtons);
    }

    private static void EnsureProfileButtons(List<NProfileButton> profileButtons, int desiredCount)
    {
        if (profileButtons.Count == 0)
        {
            return;
        }

        while (profileButtons.Count < desiredCount)
        {
            int templateIndex = profileButtons.Count % BetterSaveSlotsSettings.SlotsPerPage;
            int profileId = profileButtons.Count + 1;
            NProfileButton template = profileButtons[templateIndex];
            NProfileButton clone = template.Duplicate() as NProfileButton
                ?? throw new InvalidOperationException("复制 NProfileButton 场景节点失败。");
            clone.Name = $"BetterSaveSlotsProfileButton{profileId}";
            clone.Visible = false;
            RemoveDuplicatedBetterSaveSlotsControls(clone);
            template.GetParent().AddChild(clone);
            clone.Position = template.Position;
            profileButtons.Add(clone);
        }
    }

    private static void EnsureDeleteButtons(List<NDeleteProfileButton> deleteButtons, int desiredCount)
    {
        if (deleteButtons.Count == 0)
        {
            return;
        }

        while (deleteButtons.Count < desiredCount)
        {
            int templateIndex = deleteButtons.Count % BetterSaveSlotsSettings.SlotsPerPage;
            int profileId = deleteButtons.Count + 1;
            NDeleteProfileButton template = deleteButtons[templateIndex];
            NDeleteProfileButton clone = template.Duplicate() as NDeleteProfileButton
                ?? throw new InvalidOperationException("复制 NDeleteProfileButton 场景节点失败。");
            clone.Name = $"BetterSaveSlotsDeleteProfileButton{profileId}";
            clone.Visible = false;
            template.GetParent().AddChild(clone);
            clone.Position = template.Position;
            deleteButtons.Add(clone);
        }
    }

    private static void EnsurePageButtons(
        NProfileScreen screen,
        ProfileScreenState state,
        List<NDeleteProfileButton> deleteButtons)
    {
        state.PreviousPageButton ??= CreateActionButton(
                screen,
                deleteButtons[0],
                "BetterSaveSlotsPreviousPageButton",
                ProfileActionKind.PreviousPage,
                profileId: 0,
                PreviousIconPath,
                "UI.previous_page");

        state.NextPageButton ??= CreateActionButton(
                screen,
                deleteButtons[Math.Min(2, deleteButtons.Count - 1)],
                "BetterSaveSlotsNextPageButton",
                ProfileActionKind.NextPage,
                profileId: 0,
                NextIconPath,
                "UI.next_page");
    }

    private static NDeleteProfileButton CreateActionButton(
        NProfileScreen screen,
        NDeleteProfileButton template,
        string name,
        ProfileActionKind kind,
        int profileId,
        string iconPath,
        string hoverTextKey)
    {
        NDeleteProfileButton button = template.Duplicate() as NDeleteProfileButton
            ?? throw new InvalidOperationException("复制存档槽操作按钮失败。");
        button.Name = name;
        button.Visible = false;
        button.ZIndex = template.ZIndex;
        template.GetParent().AddChild(button);
        button.Position = template.Position;
        SetActionButtonIcon(button, iconPath);
        SetActionButtonHoverText(button, hoverTextKey);
        RegisterActionButton(button, screen, profileId, kind);
        return button;
    }

    private static void RegisterActionButton(
        NDeleteProfileButton button,
        NProfileScreen screen,
        int profileId,
        ProfileActionKind kind)
    {
        ulong instanceId = button.GetInstanceId();
        ActionButtons[instanceId] = new ActionButtonInfo(new WeakReference<NProfileScreen>(screen), profileId, kind);
    }

    private static void RemoveActionButtonsForScreen(NProfileScreen screen)
    {
        foreach ((ulong instanceId, ActionButtonInfo actionInfo) in ActionButtons.ToArray())
        {
            if (!actionInfo.Screen.TryGetTarget(out NProfileScreen? target)
                || !GodotObject.IsInstanceValid(target)
                || ReferenceEquals(target, screen))
            {
                ActionButtons.Remove(instanceId);
            }
        }
    }

    private static void RemoveDuplicatedBetterSaveSlotsControls(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.Name.ToString().StartsWith("BetterSaveSlots", StringComparison.Ordinal))
            {
                node.RemoveChild(child);
                child.QueueFree();
            }
        }
    }

}
