using BetterSaveSlots.Configuration;
using BetterSaveSlots.Features.SaveSlots;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using System.Reflection;

namespace BetterSaveSlots.Patches.SaveManagement;

internal static class SaveManagerCloudSyncCompatibilityPatches
{
    private const string LegacyFirstTimeCloudSyncMethodName = "TryFirstTimeCloudSync";
    private const string BetaOverwriteCloudWithLocalMethodName = "OverwriteCloudWithLocal";

    public static void Apply(Harmony harmony)
    {
        bool legacyPatched = PatchIfExists(
            harmony,
            LegacyFirstTimeCloudSyncMethodName,
            nameof(TryFirstTimeCloudSyncPostfix),
            "正式版首次云同步");
        bool betaPatched = PatchIfExists(
            harmony,
            BetaOverwriteCloudWithLocalMethodName,
            nameof(OverwriteCloudWithLocalPostfix),
            "107 Beta 云覆盖上传");

        if (!legacyPatched && !betaPatched)
        {
            ModLogger.Warn("未找到可挂载的 SaveManager 云上传入口，扩展槽首次云上传补丁未应用。");
        }
    }

    private static bool PatchIfExists(Harmony harmony, string originalMethodName, string postfixMethodName, string description)
    {
        MethodInfo? original = AccessTools.Method(typeof(SaveManager), originalMethodName, Type.EmptyTypes);
        if (original == null)
        {
            return false;
        }

        MethodInfo postfix = AccessTools.Method(typeof(SaveManagerCloudSyncCompatibilityPatches), postfixMethodName)
            ?? throw new MissingMethodException(nameof(SaveManagerCloudSyncCompatibilityPatches), postfixMethodName);
        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        ModLogger.Info($"已挂载 SaveManager {description}补丁：{originalMethodName}。");
        return true;
    }

    private static void TryFirstTimeCloudSyncPostfix(SaveManager __instance, ref Task<bool> __result)
    {
        __result = UploadExtendedSlotsAfterLegacyFirstTimeCloudSyncAsync(__instance, __result);
    }

    private static void OverwriteCloudWithLocalPostfix(SaveManager __instance, ref Task __result)
    {
        __result = UploadExtendedSlotsAfterOverwriteCloudWithLocalAsync(__instance, __result);
    }

    private static async Task<bool> UploadExtendedSlotsAfterLegacyFirstTimeCloudSyncAsync(
        SaveManager saveManager,
        Task<bool> originalTask)
    {
        bool uploaded = await originalTask;
        if (uploaded)
        {
            await UploadExtendedSlotsToCloudAsync(saveManager, "首次云上传");
        }

        return uploaded;
    }

    private static async Task UploadExtendedSlotsAfterOverwriteCloudWithLocalAsync(
        SaveManager saveManager,
        Task originalTask)
    {
        await originalTask;
        await UploadExtendedSlotsToCloudAsync(saveManager, "云覆盖上传");
    }

    private static async Task UploadExtendedSlotsToCloudAsync(SaveManager saveManager, string reason)
    {
        if (!UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        ISaveStore store = SaveSlotService.GetSaveStore(saveManager);
        for (int profileId = BetterSaveSlotsSettings.VanillaSlotCount + 1;
             profileId <= BetterSaveSlotsSettings.EffectiveSlotCount;
             profileId++)
        {
            await SaveSlotService.OverwriteKnownProfileFilesToCloudAsync(store, profileId, SaveSlotMode.Modded);
        }

        ModLogger.Info($"BetterSaveSlots 已完成 4-{BetterSaveSlotsSettings.EffectiveSlotCount} 号槽{reason}。");
    }
}
