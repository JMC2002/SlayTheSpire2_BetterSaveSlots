using JmcModLib.Config;
using JmcModLib.Config.UI;
using JmcModLib.Prefabs;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Saves;

namespace BetterSaveSlots.Core;

public static class BetterSaveSlotsSettings
{
    public const int VanillaSlotCount = 3;
    public const int MaxSlotCount = 12;
    public const int SlotsPerPage = 3;

    private const string SaveSlotGroup = "save_slots";

    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    [UIIntSlider(VanillaSlotCount, MaxSlotCount)]
    [Config(
        "存档槽总数",
        group: SaveSlotGroup,
        Description = "配置存档槽总数。修改后需要重新进入存档选择流程，必要时重启游戏。",
        Key = "slot_count",
        RestartRequired = true,
        Order = 10)]
    public static int SlotCount = VanillaSlotCount;

    public static int EffectiveSlotCount => Math.Clamp(SlotCount, VanillaSlotCount, MaxSlotCount);

    [UIButton(
        "导入普通模式存档到 MOD",
        "导入",
        SaveSlotGroup,
        Key = "import_vanilla_to_mod",
        HelpText = "按槽位把普通模式 profileN 导入到 modded/profileN。已有 MOD 存档时会逐个确认覆盖。",
        Color = UIButtonColor.Gold,
        Order = 20)]
    public static void ImportVanillaSavesToMod()
    {
        _ = TaskHelper.RunSafely(ImportVanillaSavesToModAsync());
    }

    private static async Task ImportVanillaSavesToModAsync()
    {
        if (!ImportLock.Wait(TimeSpan.Zero))
        {
            ModLogger.Warn("普通存档导入已在执行中，忽略重复点击。");
            return;
        }

        try
        {
            if (!UserDataPathProvider.IsRunningModded)
            {
                await ShowMessageAsync("POPUP.IMPORT_NOT_MODDED.title", "POPUP.IMPORT_NOT_MODDED.body", "POPUP.ok");
                return;
            }

            SaveSlotImportResult result = await SaveSlotService.ImportNormalProfilesToModdedAsync(
                EffectiveSlotCount,
                ConfirmImportOverwriteAsync);

            BetterSaveSlotsEvents.RaiseProfilesChanged();

            string body = BetterSaveSlotsLoc.Format(
                "POPUP.IMPORT_DONE.body",
                ("Copied", result.Copied),
                ("SkippedEmpty", result.SkippedEmpty),
                ("SkippedByUser", result.SkippedByUser),
                ("Failed", result.Failed));

            await ShowMessageAsync("POPUP.IMPORT_DONE.title", body, "POPUP.ok");
        }
        catch (Exception ex)
        {
            ModLogger.Error("导入普通模式存档失败。", ex);
            await ShowMessageAsync("POPUP.IMPORT_FAILED.title", ex.Message, "POPUP.ok");
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private static async Task<bool> ConfirmImportOverwriteAsync(int profileId)
    {
        if (!JmcConfirmationPopup.IsAvailable)
        {
            ModLogger.Warn($"导入普通存档需要确认覆盖 {profileId} 号槽，但原生确认框当前不可用。");
            return false;
        }

        return await JmcConfirmationPopup.ShowConfirmationAsync(
            BetterSaveSlotsLoc.Text("POPUP.IMPORT_OVERWRITE.title"),
            BetterSaveSlotsLoc.Format("POPUP.IMPORT_OVERWRITE.body", ("Id", profileId)),
            BetterSaveSlotsLoc.Text("POPUP.IMPORT_OVERWRITE.confirm"),
            BetterSaveSlotsLoc.Text("POPUP.cancel"));
    }

    private static async Task ShowMessageAsync(string titleKey, string bodyKeyOrText, string okKey)
    {
        if (!JmcConfirmationPopup.IsAvailable)
        {
            ModLogger.Warn($"无法显示提示框：{titleKey} / {bodyKeyOrText}");
            return;
        }

        string body = bodyKeyOrText.StartsWith("POPUP.", StringComparison.Ordinal)
            ? BetterSaveSlotsLoc.Text(bodyKeyOrText)
            : bodyKeyOrText;

        await JmcConfirmationPopup.ShowMessageAsync(
            BetterSaveSlotsLoc.Text(titleKey),
            body,
            BetterSaveSlotsLoc.Text(okKey));
    }
}
