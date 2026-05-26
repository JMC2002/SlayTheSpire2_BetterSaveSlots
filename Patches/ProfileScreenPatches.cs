using BetterSaveSlots.Core;
using Godot;
using HarmonyLib;
using JmcModLib.Prefabs;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.CompilerServices;

namespace BetterSaveSlots.Patches;

[HarmonyPatch]
internal static class ProfileScreenPatches
{
    private static readonly ConditionalWeakTable<NProfileScreen, ProfileScreenState> States = new();
    private static readonly List<WeakReference<NProfileScreen>> KnownScreens = [];
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

        ProfileScreenState state = States.GetValue(screen, _ => new ProfileScreenState());
        KnownScreens.Add(new WeakReference<NProfileScreen>(screen));
        if (!eventsSubscribed)
        {
            BetterSaveSlotsEvents.ProfilesChanged += RefreshKnownScreens;
            eventsSubscribed = true;
        }

        screen.TreeExiting += () =>
        {
            States.Remove(screen);
        };

        EnsureSlotControls(screen);
        CreatePageButtons(screen, state);
        UpdateScreen(screen, preferCurrentProfile: true);
    }

    private static void EnsureSlotControls(NProfileScreen screen)
    {
        List<NProfileButton> profileButtons = ProfileButtonsRef(screen);
        List<NDeleteProfileButton> deleteButtons = DeleteButtonsRef(screen);
        int desiredCount = BetterSaveSlotsSettings.EffectiveSlotCount;

        EnsureProfileButtons(profileButtons, desiredCount);
        EnsureDeleteButtons(deleteButtons, desiredCount);

        ProfileScreenState state = States.GetOrCreateValue(screen);
        while (state.ActionButtons.Count < desiredCount)
        {
            int profileId = state.ActionButtons.Count + 1;
            Godot.Button actionButton = CreateActionButton(profileButtons[profileId - 1], profileId, screen);
            state.ActionButtons.Add(actionButton);
        }
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

    private static Godot.Button CreateActionButton(NProfileButton profileButton, int profileId, NProfileScreen screen)
    {
        Godot.Button button = new()
        {
            Name = $"BetterSaveSlotsCopyPasteButton{profileId}",
            Text = BetterSaveSlotsLoc.Text("UI.copy"),
            CustomMinimumSize = new Vector2(150f, 42f),
            Size = new Vector2(150f, 42f),
            Position = GetActionButtonPosition(profileButton),
            ZIndex = 100,
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Stop
        };

        button.Pressed += () => _ = TaskHelper.RunSafely(OnActionButtonPressedAsync(screen, profileId));
        profileButton.AddChild(button);
        return button;
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

    private static Vector2 GetActionButtonPosition(NProfileButton profileButton)
    {
        float x = profileButton.Size.X > 220f ? profileButton.Size.X - 178f : 286f;
        float y = profileButton.Size.Y > 120f ? 28f : 24f;
        return new Vector2(x, y);
    }

    private static void CreatePageButtons(NProfileScreen screen, ProfileScreenState state)
    {
        if (state.PreviousPageButton != null || state.NextPageButton != null)
        {
            return;
        }

        List<NProfileButton> profileButtons = ProfileButtonsRef(screen);
        if (profileButtons.Count < BetterSaveSlotsSettings.SlotsPerPage)
        {
            return;
        }

        Godot.Button previous = CreatePageButton("BetterSaveSlotsPreviousPageButton", "<");
        Godot.Button next = CreatePageButton("BetterSaveSlotsNextPageButton", ">");

        previous.Pressed += () =>
        {
            state.PageIndex = Math.Max(0, state.PageIndex - 1);
            UpdateScreen(screen, preferCurrentProfile: false);
        };
        next.Pressed += () =>
        {
            int pageCount = GetPageCount();
            state.PageIndex = Math.Min(pageCount - 1, state.PageIndex + 1);
            UpdateScreen(screen, preferCurrentProfile: false);
        };

        NProfileButton leftTemplate = profileButtons[0];
        NProfileButton rightTemplate = profileButtons[2];
        previous.Position = leftTemplate.Position + new Vector2(-88f, Math.Max(210f, leftTemplate.Size.Y * 0.42f));
        next.Position = rightTemplate.Position + new Vector2(rightTemplate.Size.X + 28f, Math.Max(210f, rightTemplate.Size.Y * 0.42f));

        screen.AddChild(previous);
        screen.AddChild(next);
        state.PreviousPageButton = previous;
        state.NextPageButton = next;
    }

    private static Godot.Button CreatePageButton(string name, string text)
    {
        return new Godot.Button
        {
            Name = name,
            Text = text,
            CustomMinimumSize = new Vector2(54f, 76f),
            Size = new Vector2(54f, 76f),
            ZIndex = 100,
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
    }

    private static async Task OnActionButtonPressedAsync(NProfileScreen screen, int profileId)
    {
        ProfileScreenState state = States.GetOrCreateValue(screen);
        int? copiedProfileId = state.CopiedProfileId;

        if (!copiedProfileId.HasValue)
        {
            if (!SaveSlotService.ProfileHasSave(profileId))
            {
                await ShowMessageAsync("POPUP.COPY_EMPTY.title", "POPUP.COPY_EMPTY.body", "POPUP.ok", ("Id", profileId));
                return;
            }

            state.CopiedProfileId = profileId;
            ModLogger.Info($"已选择复制源存档槽：{profileId}。");
            UpdateScreen(screen, preferCurrentProfile: false);
            return;
        }

        int sourceProfileId = copiedProfileId.Value;
        if (sourceProfileId == profileId)
        {
            return;
        }

        await SaveSlotService.SyncKnownProfileFilesFromCloudAsync(
            SaveSlotService.GetSaveStore(),
            profileId,
            SaveSlotMode.Modded,
            deleteLocalWhenCloudMissing: false);
        bool targetHasSave = SaveSlotService.ProfileHasSave(profileId);
        if (targetHasSave)
        {
            bool confirmed = await ConfirmOverwriteAsync(sourceProfileId, profileId);
            if (!confirmed)
            {
                ModLogger.Info($"用户取消覆盖存档槽：{sourceProfileId} -> {profileId}。");
                return;
            }
        }

        try
        {
            await SaveSlotService.CopyProfileAsync(
                sourceProfileId,
                profileId,
                SaveSlotMode.Modded,
                SaveSlotMode.Modded,
                overwriteTarget: targetHasSave);

            state.CopiedProfileId = null;
            screen.Refresh();
            BetterSaveSlotsEvents.RaiseProfilesChanged();
            await ShowMessageAsync(
                "POPUP.COPY_DONE.title",
                "POPUP.COPY_DONE.body",
                "POPUP.ok",
                ("Source", sourceProfileId),
                ("Target", profileId));
        }
        catch (Exception ex)
        {
            ModLogger.Error($"复制存档槽失败：{sourceProfileId} -> {profileId}。", ex);
            await ShowMessageAsync("POPUP.COPY_FAILED.title", ex.Message, "POPUP.ok");
        }
    }

    private static async Task<bool> ConfirmOverwriteAsync(int sourceProfileId, int targetProfileId)
    {
        if (!JmcConfirmationPopup.IsAvailable)
        {
            ModLogger.Warn($"复制存档需要确认覆盖 {targetProfileId} 号槽，但原生确认框当前不可用。");
            return false;
        }

        return await JmcConfirmationPopup.ShowConfirmationAsync(
            BetterSaveSlotsLoc.Text("POPUP.COPY_OVERWRITE.title"),
            BetterSaveSlotsLoc.Format(
                "POPUP.COPY_OVERWRITE.body",
                ("Source", sourceProfileId),
                ("Target", targetProfileId)),
            BetterSaveSlotsLoc.Text("POPUP.COPY_OVERWRITE.confirm"),
            BetterSaveSlotsLoc.Text("POPUP.cancel"));
    }

    private static async Task ShowMessageAsync(
        string titleKey,
        string bodyKeyOrText,
        string okKey,
        params (string Name, object Value)[] variables)
    {
        if (!JmcConfirmationPopup.IsAvailable)
        {
            ModLogger.Warn($"无法显示提示框：{titleKey} / {bodyKeyOrText}");
            return;
        }

        string body = bodyKeyOrText.StartsWith("POPUP.", StringComparison.Ordinal)
            ? BetterSaveSlotsLoc.Format(bodyKeyOrText, variables)
            : bodyKeyOrText;

        await JmcConfirmationPopup.ShowMessageAsync(
            BetterSaveSlotsLoc.Text(titleKey),
            body,
            BetterSaveSlotsLoc.Text(okKey));
    }

    private static void UpdateScreen(NProfileScreen screen, bool preferCurrentProfile)
    {
        if (!States.TryGetValue(screen, out ProfileScreenState? state))
        {
            return;
        }

        List<NProfileButton> profileButtons = ProfileButtonsRef(screen);
        List<NDeleteProfileButton> deleteButtons = DeleteButtonsRef(screen);
        int slotCount = BetterSaveSlotsSettings.EffectiveSlotCount;
        int pageCount = GetPageCount();

        if (preferCurrentProfile && SaveManager.Instance.CurrentProfileId >= 1 && SaveManager.Instance.CurrentProfileId <= slotCount)
        {
            state.PageIndex = (SaveManager.Instance.CurrentProfileId - 1) / BetterSaveSlotsSettings.SlotsPerPage;
        }

        state.PageIndex = Math.Clamp(state.PageIndex, 0, pageCount - 1);
        int pageStart = state.PageIndex * BetterSaveSlotsSettings.SlotsPerPage;
        int pageEnd = pageStart + BetterSaveSlotsSettings.SlotsPerPage;

        if (state.CopiedProfileId is { } copiedId && !SaveSlotService.ProfileHasSave(copiedId))
        {
            state.CopiedProfileId = null;
        }

        for (int i = 0; i < profileButtons.Count; i++)
        {
            bool inConfiguredRange = i < slotCount;
            bool onCurrentPage = inConfiguredRange && i >= pageStart && i < pageEnd;
            int profileId = i + 1;

            profileButtons[i].Visible = onCurrentPage;
            if (i < deleteButtons.Count)
            {
                deleteButtons[i].Visible = onCurrentPage
                    && NProfileScreen.forceShowProfileAsDeleted != profileId
                    && SaveSlotService.ProfileHasSave(profileId);
            }

            if (i < state.ActionButtons.Count)
            {
                UpdateActionButton(state.ActionButtons[i], state, profileId, onCurrentPage);
            }
        }

        UpdatePageButtons(state, pageCount);
        UpdateFocusNeighbors(profileButtons, deleteButtons, state, pageStart, Math.Min(pageEnd, slotCount));
    }

    private static void UpdateActionButton(
        Godot.Button button,
        ProfileScreenState state,
        int profileId,
        bool visible)
    {
        button.Visible = visible;
        if (!visible)
        {
            return;
        }

        if (!state.CopiedProfileId.HasValue)
        {
            button.Text = BetterSaveSlotsLoc.Text("UI.copy");
            button.Disabled = !SaveSlotService.ProfileHasSave(profileId);
            return;
        }

        if (state.CopiedProfileId.Value == profileId)
        {
            button.Text = BetterSaveSlotsLoc.Text("UI.copied");
            button.Disabled = true;
            return;
        }

        button.Text = BetterSaveSlotsLoc.Text("UI.paste");
        button.Disabled = false;
    }

    private static void UpdatePageButtons(ProfileScreenState state, int pageCount)
    {
        bool visible = pageCount > 1;
        if (state.PreviousPageButton != null)
        {
            state.PreviousPageButton.Visible = visible;
            state.PreviousPageButton.Disabled = state.PageIndex <= 0;
        }

        if (state.NextPageButton != null)
        {
            state.NextPageButton.Visible = visible;
            state.NextPageButton.Disabled = state.PageIndex >= pageCount - 1;
        }
    }

    private static void UpdateFocusNeighbors(
        List<NProfileButton> profileButtons,
        List<NDeleteProfileButton> deleteButtons,
        ProfileScreenState state,
        int pageStart,
        int pageEnd)
    {
        int visibleCount = pageEnd - pageStart;
        if (visibleCount <= 0)
        {
            return;
        }

        for (int index = pageStart; index < pageEnd; index++)
        {
            int previousIndex = index == pageStart ? pageEnd - 1 : index - 1;
            int nextIndex = index == pageEnd - 1 ? pageStart : index + 1;
            Godot.Button? action = index < state.ActionButtons.Count ? state.ActionButtons[index] : null;

            profileButtons[index].FocusNeighborTop = profileButtons[index].GetPath();
            profileButtons[index].FocusNeighborBottom = action?.GetPath() ?? deleteButtons[index].GetPath();
            profileButtons[index].FocusNeighborLeft = profileButtons[previousIndex].GetPath();
            profileButtons[index].FocusNeighborRight = profileButtons[nextIndex].GetPath();

            if (action != null)
            {
                action.FocusNeighborTop = profileButtons[index].GetPath();
                action.FocusNeighborBottom = deleteButtons[index].GetPath();
                action.FocusNeighborLeft = state.ActionButtons[previousIndex].GetPath();
                action.FocusNeighborRight = state.ActionButtons[nextIndex].GetPath();
            }

            deleteButtons[index].FocusNeighborTop = action?.GetPath() ?? profileButtons[index].GetPath();
            deleteButtons[index].FocusNeighborBottom = deleteButtons[index].GetPath();
            deleteButtons[index].FocusNeighborLeft = deleteButtons[previousIndex].GetPath();
            deleteButtons[index].FocusNeighborRight = deleteButtons[nextIndex].GetPath();
        }
    }

    private static int GetPageCount()
    {
        return Math.Max(
            1,
            (BetterSaveSlotsSettings.EffectiveSlotCount + BetterSaveSlotsSettings.SlotsPerPage - 1)
            / BetterSaveSlotsSettings.SlotsPerPage);
    }

    private sealed class ProfileScreenState
    {
        public int PageIndex { get; set; }

        public int? CopiedProfileId { get; set; }

        public List<Godot.Button> ActionButtons { get; } = [];

        public Godot.Button? PreviousPageButton { get; set; }

        public Godot.Button? NextPageButton { get; set; }
    }
}
