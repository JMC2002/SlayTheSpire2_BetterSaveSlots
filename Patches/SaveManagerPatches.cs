using BetterSaveSlots.Configuration;
using BetterSaveSlots.Features.SaveSlots;
using BetterSaveSlots.State;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace BetterSaveSlots.Patches.SaveManagement;

[HarmonyPatch(typeof(SaveManager))]
internal static class SaveManagerPatches
{
    private static readonly AccessTools.FieldRef<SaveManager, int?> CurrentProfileIdRef =
        AccessTools.FieldRefAccess<SaveManager, int?>("_currentProfileId");

    private static readonly AccessTools.FieldRef<SaveManager, ProfileSaveManager> ProfileSaveManagerRef =
        AccessTools.FieldRefAccess<SaveManager, ProfileSaveManager>("_profileSaveManager");

    private static readonly AccessTools.FieldRef<SaveManager, RunHistorySaveManager> RunHistorySaveManagerRef =
        AccessTools.FieldRefAccess<SaveManager, RunHistorySaveManager>("_runHistorySaveManager");

    private static readonly AccessTools.FieldRef<SaveManager, Action<int>?> ProfileIdChangedRef =
        AccessTools.FieldRefAccess<SaveManager, Action<int>?>("ProfileIdChanged");

    [HarmonyPatch(nameof(SaveManager.InitProfileId))]
    [HarmonyPostfix]
    private static void InitProfileIdPostfix(SaveManager __instance, int? profileId)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        try
        {
            int slotCount = BetterSaveSlotsSettings.EffectiveSlotCount;
            ProfileSaveManager profileSaveManager = ProfileSaveManagerRef(__instance);
            ProfileSave? profileSave = profileSaveManager.Profile;
            int currentProfileId = CurrentProfileIdRef(__instance) ?? BetterSaveSlotsSettings.VanillaSlotCount;
            int vanillaLastProfileId = profileSave == null
                ? Math.Clamp(currentProfileId, 1, BetterSaveSlotsSettings.VanillaSlotCount)
                : Math.Clamp(profileSave.LastProfileId, 1, BetterSaveSlotsSettings.VanillaSlotCount);

            if (profileSave?.LastProfileId > BetterSaveSlotsSettings.VanillaSlotCount)
            {
                int migratedProfileId = Math.Clamp(profileSave.LastProfileId, 1, slotCount);
                BetterSaveSlotsState.CurrentProfileId = migratedProfileId;
                profileSave.LastProfileId = vanillaLastProfileId;
                __instance.SaveProfile();
                ModLogger.Warn($"检测到原版 profile.save 记录了扩展槽 {migratedProfileId}，已迁移到 MOD 状态并回写为 {vanillaLastProfileId}。");
            }

            int desiredProfileId = ResolveDesiredProfileId(profileId, currentProfileId, slotCount);

            if (desiredProfileId != currentProfileId)
            {
                CurrentProfileIdRef(__instance) = desiredProfileId;
                RunHistorySaveManagerRef(__instance).CreateRunHistoryDirectory();
                ModLogger.Info($"初始化时恢复 BetterSaveSlots 当前槽位：{desiredProfileId}。");
            }

            BetterSaveSlotsState.CurrentProfileId = desiredProfileId;
            if (desiredProfileId <= BetterSaveSlotsSettings.VanillaSlotCount
                && profileSave != null
                && profileSave.LastProfileId != desiredProfileId)
            {
                profileSave.LastProfileId = desiredProfileId;
                __instance.SaveProfile();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error("初始化 BetterSaveSlots 当前槽位状态失败。", ex);
        }
    }

    [HarmonyPatch(nameof(SaveManager.SwitchProfileId))]
    [HarmonyPrefix]
    private static bool SwitchProfileIdPrefix(SaveManager __instance, int profileId)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            return true;
        }

        int slotCount = BetterSaveSlotsSettings.EffectiveSlotCount;
        if (profileId < 1 || profileId > slotCount)
        {
            ModLogger.Warn($"忽略非法存档槽切换请求：{profileId}，当前配置上限={slotCount}。");
            return false;
        }

        BetterSaveSlotsState.CurrentProfileId = profileId;
        if (profileId <= BetterSaveSlotsSettings.VanillaSlotCount)
        {
            return true;
        }

        ModLogger.Info($"切换到 BetterSaveSlots 扩展存档槽：{profileId}。");
        CurrentProfileIdRef(__instance) = profileId;
        RunHistorySaveManagerRef(__instance).CreateRunHistoryDirectory();
        ProfileIdChangedRef(__instance)?.Invoke(profileId);
        return false;
    }

    [HarmonyPatch(nameof(SaveManager.SyncCloudToLocal))]
    [HarmonyPostfix]
    private static void SyncCloudToLocalPostfix(SaveManager __instance, ref Task __result)
    {
        __result = SyncCloudToLocalAfterOriginalAsync(__instance, __result);
    }

    [HarmonyPatch("CleanupTemporaryFiles")]
    [HarmonyPostfix]
    private static void CleanupTemporaryFilesPostfix(SaveManager __instance)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        try
        {
            ISaveStore store = SaveSlotService.GetSaveStore(__instance);
            for (int profileId = BetterSaveSlotsSettings.VanillaSlotCount + 1;
                 profileId <= BetterSaveSlotsSettings.EffectiveSlotCount;
                 profileId++)
            {
                SaveSlotService.CleanupTemporaryFiles(store, profileId, SaveSlotMode.Modded);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn("清理扩展槽临时文件失败。", ex);
        }
    }

    [HarmonyPatch("CleanupStaleCurrentRunSaves")]
    [HarmonyPostfix]
    private static void CleanupStaleCurrentRunSavesPostfix(SaveManager __instance)
    {
        CleanupExtendedStaleCurrentRunSaves(__instance);
    }

    private static int ResolveDesiredProfileId(int? explicitProfileId, int currentProfileId, int slotCount)
    {
        if (explicitProfileId.HasValue)
        {
            return Math.Clamp(explicitProfileId.Value, 1, slotCount);
        }

        int? savedProfileId = BetterSaveSlotsState.CurrentProfileId;
        if (savedProfileId.HasValue)
        {
            return Math.Clamp(savedProfileId.Value, 1, slotCount);
        }

        return Math.Clamp(currentProfileId, 1, slotCount);
    }

    private static async Task SyncCloudToLocalAfterOriginalAsync(SaveManager saveManager, Task originalTask)
    {
        await originalTask;

        if (!UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        ISaveStore store = SaveSlotService.GetSaveStore(saveManager);
        for (int profileId = BetterSaveSlotsSettings.VanillaSlotCount + 1;
             profileId <= BetterSaveSlotsSettings.EffectiveSlotCount;
             profileId++)
        {
            await SaveSlotService.SyncKnownProfileFilesFromCloudAsync(store, profileId, SaveSlotMode.Modded);
            SaveSlotService.CleanupStaleCurrentRunSaves(store, profileId, SaveSlotMode.Modded);
        }

        ModLogger.Info($"BetterSaveSlots 已完成 4-{BetterSaveSlotsSettings.EffectiveSlotCount} 号槽云同步。");
    }

    private static void CleanupExtendedStaleCurrentRunSaves(SaveManager saveManager)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        try
        {
            ISaveStore store = SaveSlotService.GetSaveStore(saveManager);
            for (int profileId = BetterSaveSlotsSettings.VanillaSlotCount + 1;
                 profileId <= BetterSaveSlotsSettings.EffectiveSlotCount;
                 profileId++)
            {
                SaveSlotService.CleanupStaleCurrentRunSaves(store, profileId, SaveSlotMode.Modded);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn("清理扩展槽陈旧 current_run 失败。", ex);
        }
    }
}
