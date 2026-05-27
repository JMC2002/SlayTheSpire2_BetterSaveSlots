using BetterSaveSlots.Configuration;
using BetterSaveSlots.Events;
using BetterSaveSlots.Features.SaveSlots;
using BetterSaveSlots.Localization;
using Godot;
using JmcModLib.Prefabs;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Saves;

namespace BetterSaveSlots.Patches.ProfileScreen;

internal static partial class ProfileScreenPatches
{
    private static void TurnPage(NProfileScreen screen, int delta)
    {
        ProfileScreenState state = States.GetOrCreateValue(screen);
        int pageCount = GetPageCount();
        state.PageIndex = Math.Clamp(state.PageIndex + delta, 0, pageCount - 1);
        UpdateScreen(screen, preferCurrentProfile: false);
        QueueDeferredLayoutUpdate(screen);
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
            QueueDeferredLayoutUpdate(screen);
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
            QueueDeferredLayoutUpdate(screen);
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
}
