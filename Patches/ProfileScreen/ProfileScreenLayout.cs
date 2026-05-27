using BetterSaveSlots.Configuration;
using BetterSaveSlots.Features.SaveSlots;
using BetterSaveSlots.Localization;
using Godot;
using JmcModLib.Utils;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Saves;

namespace BetterSaveSlots.Patches.ProfileScreen;

internal static partial class ProfileScreenPatches
{
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
                PositionPageButton(state.PreviousPageButton, profileButtons[pageStart], deleteButtons[pageStart], isPrevious: true);
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
                PositionPageButton(state.NextPageButton, profileButtons[lastIndex], deleteButtons[lastIndex], isPrevious: false);
                SetPageButtonFocus(state.NextPageButton, profileButtons[lastIndex], isPrevious: false);
                profileButtons[lastIndex].FocusNeighborRight = state.NextPageButton.GetPath();
            }
        }
    }

    private static void PositionButtonInSlot(NDeleteProfileButton actionButton, NProfileButton slotButton, float xOffset)
    {
        actionButton.GlobalPosition = GetSlotActionGlobalPosition(slotButton, actionButton, xOffset);
    }

    private static void PositionCompanionButton(
        NDeleteProfileButton actionButton,
        NProfileButton slotButton,
        NDeleteProfileButton anchorDeleteButton,
        float xOffset)
    {
        Vector2 anchorPosition = GetDeleteButtonAnchorGlobalPosition(slotButton, anchorDeleteButton);
        actionButton.GlobalPosition = AlignButtonToAnchorCenter(anchorPosition, anchorDeleteButton, actionButton, xOffset);
    }

    private static void PositionPageButton(
        NDeleteProfileButton pageButton,
        NProfileButton slotButton,
        NDeleteProfileButton anchorDeleteButton,
        bool isPrevious)
    {
        Rect2 slotRect = GetSlotGlobalRect(slotButton);
        Vector2 deletePosition = GetDeleteButtonAnchorGlobalPosition(slotButton, anchorDeleteButton);
        float buttonWidth = GetButtonWidth(pageButton);
        float x = isPrevious
            ? slotRect.Position.X - buttonWidth - PageButtonGap
            : slotRect.Position.X + slotRect.Size.X + PageButtonGap;
        float y = AlignButtonToAnchorCenter(deletePosition, anchorDeleteButton, pageButton, centerOffsetX: 0f).Y;
        pageButton.GlobalPosition = new Vector2(x, y);
    }

    private static bool IsExtendedDeleteButton(NDeleteProfileButton button)
    {
        return button.Name.ToString().StartsWith("BetterSaveSlotsDeleteProfileButton", StringComparison.Ordinal);
    }

    private static Vector2 GetDeleteButtonAnchorGlobalPosition(NProfileButton slotButton, NDeleteProfileButton anchorDeleteButton)
    {
        Rect2 anchorRect = anchorDeleteButton.GetGlobalRect();
        if (anchorRect.Size.X > 10f && anchorRect.Size.Y > 10f)
        {
            return anchorRect.Position;
        }

        return GetSlotActionGlobalPosition(slotButton, anchorDeleteButton, xOffset: 0f);
    }

    private static Vector2 AlignButtonToAnchorCenter(
        Vector2 anchorPosition,
        Control anchorButton,
        Control actionButton,
        float centerOffsetX)
    {
        float x = anchorPosition.X
            + (GetButtonWidth(anchorButton) - GetButtonWidth(actionButton)) / 2f
            + centerOffsetX;
        float y = anchorPosition.Y
            + (GetButtonHeight(anchorButton) - GetButtonHeight(actionButton)) / 2f;
        return new Vector2(x, y);
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

    private static float GetButtonHeight(Control button)
    {
        return button.Size.Y > 10f ? button.Size.Y : FallbackButtonHeight;
    }

    private static Rect2 GetSlotGlobalRect(Control slotButton)
    {
        Rect2 slotRect = slotButton.GetGlobalRect();
        Vector2 slotSize = new(GetSlotWidth(slotButton), GetSlotHeight(slotButton));
        if (slotRect.Size.X <= 10f || slotRect.Size.Y <= 10f)
        {
            slotRect.Size = slotSize;
        }

        return slotRect;
    }

    private static Vector2 GetSlotActionGlobalPosition(Control slotButton, Control actionButton, float xOffset)
    {
        Rect2 slotRect = GetSlotGlobalRect(slotButton);
        float x = slotRect.Position.X + (slotRect.Size.X - GetButtonWidth(actionButton)) / 2f + xOffset;
        float y = slotRect.Position.Y + slotRect.Size.Y + SlotActionBottomGap;
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
}
