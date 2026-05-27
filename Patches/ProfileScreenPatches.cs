using BetterSaveSlots.Core;
using Godot;
using HarmonyLib;
using JmcModLib.Prefabs;
using JmcModLib.Utils;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.CompilerServices;

namespace BetterSaveSlots.Patches;

[HarmonyPatch]
internal static class ProfileScreenPatches
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
    private const float SlotActionBottomGap = 30f;

    private static readonly ConditionalWeakTable<NProfileScreen, ProfileScreenState> States = new();
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
        if (state.PreviousPageButton == null)
        {
            state.PreviousPageButton = CreateActionButton(
                screen,
                deleteButtons[0],
                "BetterSaveSlotsPreviousPageButton",
                ProfileActionKind.PreviousPage,
                profileId: 0,
                PreviousIconPath,
                "UI.previous_page");
        }

        if (state.NextPageButton == null)
        {
            state.NextPageButton = CreateActionButton(
                screen,
                deleteButtons[Math.Min(2, deleteButtons.Count - 1)],
                "BetterSaveSlotsNextPageButton",
                ProfileActionKind.NextPage,
                profileId: 0,
                NextIconPath,
                "UI.next_page");
        }
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

    private static void TurnPage(NProfileScreen screen, int delta)
    {
        ProfileScreenState state = States.GetOrCreateValue(screen);
        int pageCount = GetPageCount();
        state.PageIndex = Math.Clamp(state.PageIndex + delta, 0, pageCount - 1);
        UpdateScreen(screen, preferCurrentProfile: false);
    }

    private static void ClearCopiedProfile(NProfileScreen screen, bool refresh, string reason)
    {
        if (!States.TryGetValue(screen, out ProfileScreenState? state) || !state.CopiedProfileId.HasValue)
        {
            return;
        }

        int copiedProfileId = state.CopiedProfileId.Value;
        state.CopiedProfileId = null;
        ModLogger.Info($"已取消复制源存档槽：{copiedProfileId}，原因={reason}。");

        if (refresh && GodotObject.IsInstanceValid(screen))
        {
            UpdateScreen(screen, preferCurrentProfile: false);
        }
    }

    private static async Task OnCopyPasteButtonPressedAsync(NProfileScreen screen, int profileId)
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
            ClearCopiedProfile(screen, refresh: true, "再次点击复制源");
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
            bool confirmed = await ConfirmOverwriteAsync(sourceProfileId, profileId, "POPUP.COPY_OVERWRITE");
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

    private static async Task OnImportButtonPressedAsync(NProfileScreen screen, int targetProfileId)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            await ShowMessageAsync("POPUP.IMPORT_NOT_MODDED.title", "POPUP.IMPORT_NOT_MODDED.body", "POPUP.ok");
            return;
        }

        try
        {
            IReadOnlyList<int> sourceProfileIds = await SaveSlotService.GetImportableNormalProfileIdsAsync(
                BetterSaveSlotsSettings.VanillaSlotCount);

            if (sourceProfileIds.Count == 0)
            {
                await ShowMessageAsync("POPUP.IMPORT_EMPTY.title", "POPUP.IMPORT_EMPTY.body", "POPUP.ok");
                return;
            }

            int? sourceProfileId = await ShowImportSourcePickerAsync(targetProfileId, sourceProfileIds);
            if (!sourceProfileId.HasValue)
            {
                ModLogger.Info($"用户取消导入普通存档到 MOD {targetProfileId} 号槽。");
                return;
            }

            await SaveSlotService.SyncKnownProfileFilesFromCloudAsync(
                SaveSlotService.GetSaveStore(),
                targetProfileId,
                SaveSlotMode.Modded,
                deleteLocalWhenCloudMissing: false);
            bool targetHasSave = SaveSlotService.ProfileHasSave(targetProfileId);

            if (targetHasSave)
            {
                bool confirmed = await ConfirmOverwriteAsync(
                    sourceProfileId.Value,
                    targetProfileId,
                    "POPUP.IMPORT_OVERWRITE");
                if (!confirmed)
                {
                    ModLogger.Info($"用户取消导入覆盖：普通 {sourceProfileId} -> MOD {targetProfileId}。");
                    return;
                }
            }

            await SaveSlotService.ImportNormalProfileToModdedAsync(
                sourceProfileId.Value,
                targetProfileId,
                overwriteTarget: targetHasSave);

            screen.Refresh();
            BetterSaveSlotsEvents.RaiseProfilesChanged();
            await ShowMessageAsync(
                "POPUP.IMPORT_DONE.title",
                "POPUP.IMPORT_DONE.body",
                "POPUP.ok",
                ("Source", sourceProfileId.Value),
                ("Target", targetProfileId));
        }
        catch (Exception ex)
        {
            ModLogger.Error($"导入普通存档失败：目标 MOD {targetProfileId} 号槽。", ex);
            await ShowMessageAsync("POPUP.IMPORT_FAILED.title", ex.Message, "POPUP.ok");
        }
    }

    private static Task<int?> ShowImportSourcePickerAsync(int targetProfileId, IReadOnlyList<int> sourceProfileIds)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            ModLogger.Warn("无法显示普通存档来源选择框：当前没有可用的模态容器或已有弹窗。");
            return Task.FromResult<int?>(null);
        }

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup == null)
        {
            ModLogger.Warn("无法创建普通存档来源选择框。");
            return Task.FromResult<int?>(null);
        }

        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Connect(Node.SignalName.TreeExiting, Callable.From(() => completion.TrySetResult(null)));

        try
        {
            modalContainer.Add(popup);
            if (!ReferenceEquals(modalContainer.OpenModal, popup))
            {
                popup.QueueFree();
                completion.TrySetResult(null);
                return completion.Task;
            }

            ConfigureImportSourcePicker(popup.GetNode<NVerticalPopup>("VerticalPopup"), targetProfileId, sourceProfileIds, completion);
            return completion.Task;
        }
        catch (Exception ex)
        {
            ModLogger.Error("显示普通存档来源选择框失败。", ex);
            if (ReferenceEquals(modalContainer.OpenModal, popup))
            {
                modalContainer.Clear();
            }
            else
            {
                popup.QueueFree();
            }

            completion.TrySetResult(null);
            return completion.Task;
        }
    }

    private static void ConfigureImportSourcePicker(
        NVerticalPopup popup,
        int targetProfileId,
        IReadOnlyList<int> sourceProfileIds,
        TaskCompletionSource<int?> completion)
    {
        popup.SetText(
            BetterSaveSlotsLoc.Text("POPUP.IMPORT_PICKER.title"),
            BetterSaveSlotsLoc.Format("POPUP.IMPORT_PICKER.body", ("Target", targetProfileId)));

        Node? buttonParent = popup.YesButton.GetParent();
        List<(int SourceProfileId, NPopupYesNoButton Button)> extraSourceButtons = [];
        if (buttonParent != null)
        {
            for (int index = 1; index < sourceProfileIds.Count; index++)
            {
                int sourceProfileId = sourceProfileIds[index];
                NPopupYesNoButton sourceButton = popup.YesButton.Duplicate() as NPopupYesNoButton
                    ?? throw new InvalidOperationException("复制普通存档来源按钮失败。");
                sourceButton.Name = $"BetterSaveSlotsImportSource{sourceProfileId}";
                buttonParent.AddChild(sourceButton);
                extraSourceButtons.Add((sourceProfileId, sourceButton));
            }
        }

        int firstSourceProfileId = sourceProfileIds[0];
        var firstSourceText = BetterSaveSlotsLoc.Loc("POPUP.IMPORT_SOURCE_BUTTON");
        firstSourceText.AddObj("Source", firstSourceProfileId);
        popup.InitYesButton(
            firstSourceText,
            _ => completion.TrySetResult(firstSourceProfileId));

        popup.InitNoButton(
            BetterSaveSlotsLoc.Loc("POPUP.cancel"),
            _ => completion.TrySetResult(null));
        popup.NoButton.SetText(BetterSaveSlotsLoc.Text("POPUP.cancel"));

        float rowHeight = Math.Max(
            72f,
            Math.Abs(popup.NoButton.Position.Y - popup.YesButton.Position.Y));
        if (rowHeight < 72f)
        {
            rowHeight = Math.Max(72f, popup.YesButton.Size.Y + 18f);
        }

        Vector2 startPosition = popup.YesButton.Position;
        popup.YesButton.Position = startPosition;

        for (int index = 0; index < extraSourceButtons.Count; index++)
        {
            (int sourceProfileId, NPopupYesNoButton sourceButton) = extraSourceButtons[index];
            sourceButton.Position = startPosition + new Vector2(0f, rowHeight * (index + 1));
            sourceButton.SetText(BetterSaveSlotsLoc.Format("POPUP.IMPORT_SOURCE_BUTTON", ("Source", sourceProfileId)));
            sourceButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => CompleteImportSourcePicker(completion, sourceProfileId)));
        }

        popup.NoButton.Position = startPosition + new Vector2(0f, rowHeight * sourceProfileIds.Count);
    }

    private static void CompleteImportSourcePicker(TaskCompletionSource<int?> completion, int? sourceProfileId)
    {
        if (completion.TrySetResult(sourceProfileId))
        {
            NModalContainer.Instance?.Clear();
        }
    }

    private static async Task<bool> ConfirmOverwriteAsync(int sourceProfileId, int targetProfileId, string keyPrefix)
    {
        if (!JmcConfirmationPopup.IsAvailable)
        {
            ModLogger.Warn($"存档操作需要确认覆盖 {targetProfileId} 号槽，但原生确认框当前不可用。");
            return false;
        }

        return await JmcConfirmationPopup.ShowConfirmationAsync(
            BetterSaveSlotsLoc.Text($"{keyPrefix}.title"),
            BetterSaveSlotsLoc.Format(
                $"{keyPrefix}.body",
                ("Source", sourceProfileId),
                ("Target", targetProfileId)),
            BetterSaveSlotsLoc.Text($"{keyPrefix}.confirm"),
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
        int pageEnd = Math.Min(pageStart + BetterSaveSlotsSettings.SlotsPerPage, slotCount);

        if (state.CopiedProfileId is { } copiedId && !SaveSlotService.ProfileHasSave(copiedId))
        {
            state.CopiedProfileId = null;
        }

        for (int i = 0; i < profileButtons.Count; i++)
        {
            bool inConfiguredRange = i < slotCount;
            bool onCurrentPage = inConfiguredRange && i >= pageStart && i < pageEnd;
            int profileId = i + 1;
            bool modProfileHasSave = inConfiguredRange && SaveSlotService.ProfileHasSave(profileId);

            profileButtons[i].Visible = onCurrentPage;
            if (i < deleteButtons.Count)
            {
                deleteButtons[i].Visible = onCurrentPage
                    && NProfileScreen.forceShowProfileAsDeleted != profileId
                    && modProfileHasSave;
                if (IsExtendedDeleteButton(deleteButtons[i]))
                {
                    PositionButtonInSlot(deleteButtons[i], profileButtons[i], xOffset: 0f);
                }
            }

            if (i < state.CopyPasteButtons.Count)
            {
                UpdateCopyPasteButton(state.CopyPasteButtons[i], state, profileId, onCurrentPage, modProfileHasSave);
                PositionCompanionButton(state.CopyPasteButtons[i], profileButtons[i], deleteButtons[i], -SlotActionSpacing);
            }

            if (i < state.ImportButtons.Count)
            {
                UpdateImportButton(state.ImportButtons[i], onCurrentPage);
                PositionCompanionButton(state.ImportButtons[i], profileButtons[i], deleteButtons[i], SlotActionSpacing);
            }
        }

        UpdateFocusNeighbors(profileButtons, deleteButtons, state, pageStart, pageEnd);
        UpdatePageButtons(profileButtons, deleteButtons, state, pageCount, pageStart, pageEnd);
    }

    private static void UpdateCopyPasteButton(
        NDeleteProfileButton button,
        ProfileScreenState state,
        int profileId,
        bool visible,
        bool modProfileHasSave)
    {
        if (!visible)
        {
            button.Visible = false;
            return;
        }

        if (!state.CopiedProfileId.HasValue)
        {
            SetActionButtonIcon(button, CopyIconPath);
            SetActionButtonHoverText(button, "UI.copy");
            SetActionButtonState(button, modProfileHasSave, modProfileHasSave);
            return;
        }

        if (state.CopiedProfileId.Value == profileId)
        {
            SetActionButtonIcon(button, CopyIconPath);
            SetActionButtonHoverText(button, "UI.cancel_copy");
            SetActionButtonState(button, visible: true, enabled: true);
            return;
        }

        SetActionButtonIcon(button, PasteIconPath);
        SetActionButtonHoverText(button, "UI.paste");
        SetActionButtonState(button, visible: true, enabled: true);
    }

    private static void UpdateImportButton(NDeleteProfileButton button, bool visible)
    {
        bool enabled = visible && UserDataPathProvider.IsRunningModded;
        SetActionButtonIcon(button, ImportIconPath);
        SetActionButtonHoverText(button, "UI.import");
        SetActionButtonState(button, enabled, enabled);
    }

    private static void UpdatePageButtons(
        List<NProfileButton> profileButtons,
        List<NDeleteProfileButton> deleteButtons,
        ProfileScreenState state,
        int pageCount,
        int pageStart,
        int pageEnd)
    {
        bool hasPrevious = pageCount > 1 && state.PageIndex > 0;
        bool hasNext = pageCount > 1 && state.PageIndex < pageCount - 1;

        if (state.PreviousPageButton != null)
        {
            SetActionButtonState(state.PreviousPageButton, hasPrevious, hasPrevious);
            if (hasPrevious)
            {
                AttachPageButton(state.PreviousPageButton, profileButtons[pageStart], deleteButtons[pageStart], isPrevious: true);
                SetPageButtonFocus(state.PreviousPageButton, profileButtons[pageStart], isPrevious: true);
                profileButtons[pageStart].FocusNeighborLeft = state.PreviousPageButton.GetPath();
            }
        }

        if (state.NextPageButton != null)
        {
            SetActionButtonState(state.NextPageButton, hasNext, hasNext);
            if (hasNext)
            {
                int lastIndex = Math.Max(pageStart, pageEnd - 1);
                AttachPageButton(state.NextPageButton, profileButtons[lastIndex], deleteButtons[lastIndex], isPrevious: false);
                SetPageButtonFocus(state.NextPageButton, profileButtons[lastIndex], isPrevious: false);
                profileButtons[lastIndex].FocusNeighborRight = state.NextPageButton.GetPath();
            }
        }
    }

    private static void PositionButtonInSlot(NDeleteProfileButton actionButton, NProfileButton slotButton, float xOffset)
    {
        AttachToSlot(actionButton, slotButton);
        actionButton.Position = GetDeleteButtonSlotOffset(slotButton, actionButton) + new Vector2(xOffset, 0f);
    }

    private static void PositionCompanionButton(
        NDeleteProfileButton actionButton,
        NProfileButton slotButton,
        NDeleteProfileButton anchorDeleteButton,
        float xOffset)
    {
        AttachToSlot(actionButton, slotButton);
        actionButton.Position = GetDeleteButtonAnchorPosition(slotButton, anchorDeleteButton, actionButton)
            + new Vector2(xOffset, 0f);
    }

    private static void AttachPageButton(
        NDeleteProfileButton pageButton,
        NProfileButton slotButton,
        NDeleteProfileButton anchorDeleteButton,
        bool isPrevious)
    {
        if (pageButton.GetParent() != slotButton)
        {
            pageButton.GetParent()?.RemoveChild(pageButton);
            slotButton.AddChild(pageButton);
        }

        Vector2 localDeletePosition = GetDeleteButtonAnchorPosition(slotButton, anchorDeleteButton, pageButton);
        float buttonWidth = GetButtonWidth(pageButton);
        float slotWidth = GetSlotWidth(slotButton);
        float x = isPrevious ? -buttonWidth - PageButtonGap : slotWidth + PageButtonGap;
        pageButton.Position = new Vector2(x, localDeletePosition.Y);
    }

    private static void AttachToSlot(Control control, NProfileButton slotButton)
    {
        if (control.GetParent() == slotButton)
        {
            return;
        }

        control.GetParent()?.RemoveChild(control);
        slotButton.AddChild(control);
    }

    private static bool IsExtendedDeleteButton(NDeleteProfileButton button)
    {
        return button.Name.ToString().StartsWith("BetterSaveSlotsDeleteProfileButton", StringComparison.Ordinal);
    }

    private static Vector2 GetDeleteButtonAnchorPosition(
        NProfileButton slotButton,
        NDeleteProfileButton anchorDeleteButton,
        Control fallbackButton)
    {
        if (anchorDeleteButton.GetParent() == slotButton)
        {
            return anchorDeleteButton.Position;
        }

        Rect2 anchorRect = anchorDeleteButton.GetGlobalRect();
        if (anchorRect.Size.X > 10f && anchorRect.Size.Y > 10f)
        {
            return anchorRect.Position - slotButton.GetGlobalRect().Position;
        }

        return GetDeleteButtonSlotOffset(slotButton, fallbackButton);
    }

    private static void SetPageButtonFocus(NDeleteProfileButton pageButton, Control slotButton, bool isPrevious)
    {
        NodePath slotPath = slotButton.GetPath();
        pageButton.FocusNeighborTop = pageButton.GetPath();
        pageButton.FocusNeighborBottom = pageButton.GetPath();
        pageButton.FocusNeighborLeft = isPrevious ? pageButton.GetPath() : slotPath;
        pageButton.FocusNeighborRight = isPrevious ? slotPath : pageButton.GetPath();
    }

    private static void SetActionButtonIcon(NDeleteProfileButton button, string iconPath)
    {
        Texture2D? texture = LoadIcon(iconPath);
        if (texture == null)
        {
            return;
        }

        TextureRect? icon = button.GetNodeOrNull<TextureRect>("Icon");
        if (icon != null)
        {
            icon.Texture = texture;
        }
    }

    private static void SetActionButtonHoverText(NDeleteProfileButton button, string key)
    {
        MegaLabel? label = button.GetNodeOrNull<MegaLabel>("%MegaLabel");
        label?.SetTextAutoSize(BetterSaveSlotsLoc.Text(key));
    }

    private static void SetActionButtonState(NDeleteProfileButton button, bool visible, bool enabled)
    {
        button.Visible = visible;
        button.SetEnabled(enabled);
        button.Modulate = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.48f);
    }

    private static Texture2D? LoadIcon(string iconPath)
    {
        if (IconCache.TryGetValue(iconPath, out Texture2D? cached))
        {
            return cached;
        }

        Texture2D? texture = ResourceLoader.Load<Texture2D>(iconPath, null, ResourceLoader.CacheMode.Reuse);
        if (texture == null)
        {
            ModLogger.Warn($"未能加载 BetterSaveSlots 按钮图标：{iconPath}");
        }

        IconCache[iconPath] = texture;
        return texture;
    }

    private static float GetSlotWidth(Control slotButton)
    {
        return slotButton.Size.X > 10f ? slotButton.Size.X : FallbackSlotWidth;
    }

    private static float GetSlotHeight(Control slotButton)
    {
        return slotButton.Size.Y > 10f ? slotButton.Size.Y : FallbackSlotHeight;
    }

    private static float GetButtonWidth(Control button)
    {
        return button.Size.X > 10f ? button.Size.X : FallbackButtonWidth;
    }

    private static Vector2 GetDeleteButtonSlotOffset(Control slotButton, Control actionButton)
    {
        float x = (GetSlotWidth(slotButton) - GetButtonWidth(actionButton)) / 2f;
        float y = GetSlotHeight(slotButton) + SlotActionBottomGap;
        return new Vector2(x, y);
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
            Control firstRowButton = VisibleOrFallback(
                state.CopyPasteButtons[index],
                state.ImportButtons[index],
                deleteButtons[index]);

            profileButtons[index].FocusNeighborTop = profileButtons[index].GetPath();
            profileButtons[index].FocusNeighborBottom = firstRowButton.GetPath();
            profileButtons[index].FocusNeighborLeft = profileButtons[previousIndex].GetPath();
            profileButtons[index].FocusNeighborRight = profileButtons[nextIndex].GetPath();

            SetRowFocus(state.CopyPasteButtons[index], profileButtons[index], deleteButtons[index], state.ImportButtons[index]);
            SetRowFocus(deleteButtons[index], profileButtons[index], state.CopyPasteButtons[index], state.ImportButtons[index]);
            SetRowFocus(state.ImportButtons[index], profileButtons[index], deleteButtons[index], state.CopyPasteButtons[index]);
        }
    }

    private static Control VisibleOrFallback(params Control[] controls)
    {
        foreach (Control control in controls)
        {
            if (control.Visible)
            {
                return control;
            }
        }

        return controls[0];
    }

    private static void SetRowFocus(Control control, Control top, Control left, Control right)
    {
        control.FocusNeighborTop = top.GetPath();
        control.FocusNeighborBottom = control.GetPath();
        control.FocusNeighborLeft = left.GetPath();
        control.FocusNeighborRight = right.GetPath();
    }

    private static int GetPageCount()
    {
        return Math.Max(
            1,
            (BetterSaveSlotsSettings.EffectiveSlotCount + BetterSaveSlotsSettings.SlotsPerPage - 1)
            / BetterSaveSlotsSettings.SlotsPerPage);
    }

    private enum ProfileActionKind
    {
        CopyPaste,
        Import,
        PreviousPage,
        NextPage
    }

    private sealed record ActionButtonInfo(
        WeakReference<NProfileScreen> Screen,
        int ProfileId,
        ProfileActionKind Kind);

    private sealed class ProfileScreenState
    {
        public int PageIndex { get; set; }

        public int? CopiedProfileId { get; set; }

        public List<NDeleteProfileButton> CopyPasteButtons { get; } = [];

        public List<NDeleteProfileButton> ImportButtons { get; } = [];

        public NDeleteProfileButton? PreviousPageButton { get; set; }

        public NDeleteProfileButton? NextPageButton { get; set; }
    }
}
