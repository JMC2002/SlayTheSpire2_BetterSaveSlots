using HarmonyLib;
using BetterSaveSlots.Configuration;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using System.Globalization;
using System.Text.Json;

namespace BetterSaveSlots.Features.SaveSlots;

public static partial class SaveSlotService
{
    private static readonly string[] ProfileRootNames =
    [
        UserDataPathProvider.SavesDir,
        "replays"
    ];

    private static readonly string[] CurrentRunFileNames =
    [
        "current_run.save",
        "current_run_mp.save"
    ];

    private static readonly AccessTools.FieldRef<SaveManager, ISaveStore> SaveStoreRef =
        AccessTools.FieldRefAccess<SaveManager, ISaveStore>("_saveStore");

    public static ISaveStore GetSaveStore(SaveManager? saveManager = null)
    {
        return SaveStoreRef(saveManager ?? SaveManager.Instance);
    }

    public static bool ProfileHasSave(int profileId, SaveSlotMode mode = SaveSlotMode.Modded)
    {
        ISaveStore store = GetSaveStore();
        return store.FileExists(GetRelativePath(profileId, mode, "saves/progress.save"));
    }

    public static async Task CopyProfileAsync(
        int sourceProfileId,
        int targetProfileId,
        SaveSlotMode sourceMode,
        SaveSlotMode targetMode,
        bool overwriteTarget)
    {
        ISaveStore store = GetSaveStore();

        await SyncKnownProfileFilesFromCloudAsync(
            store,
            sourceProfileId,
            sourceMode,
            deleteLocalWhenCloudMissing: false);
        await SyncKnownProfileFilesFromCloudAsync(
            store,
            targetProfileId,
            targetMode,
            deleteLocalWhenCloudMissing: false);

        if (!ProfileHasSave(sourceProfileId, sourceMode))
        {
            throw new InvalidOperationException($"源存档槽 {sourceProfileId} 没有 progress.save。");
        }

        if (!overwriteTarget && ProfileHasSave(targetProfileId, targetMode))
        {
            throw new InvalidOperationException($"目标存档槽 {targetProfileId} 已有存档，不能直接覆盖。");
        }

        // 复制是 profile 目录级操作；即便目标没有 progress.save，也清理可能残留的 prefs/current_run。
        DeleteProfileDirectory(store, targetProfileId, targetMode);

        ISaveStore copyStore = GetLocalStore(store);
        int copiedFiles = 0;
        foreach (string rootName in ProfileRootNames)
        {
            string sourceRoot = GetRelativePath(sourceProfileId, sourceMode, rootName);
            string targetRoot = GetRelativePath(targetProfileId, targetMode, rootName);
            copiedFiles += await CopyDirectoryRecursiveAsync(copyStore, sourceRoot, targetRoot);
        }

        await OverwriteKnownProfileFilesToCloudAsync(store, targetProfileId, targetMode);

        ModLogger.Info(
            $"已复制存档：{Describe(sourceMode, sourceProfileId)} -> {Describe(targetMode, targetProfileId)}，文件数={copiedFiles}。");
    }

    public static async Task<IReadOnlyList<int>> GetImportableNormalProfileIdsAsync(int sourceSlotCount)
    {
        int maxProfileId = Math.Clamp(
            sourceSlotCount,
            BetterSaveSlotsSettings.VanillaSlotCount,
            BetterSaveSlotsSettings.MaxSlotCount);
        ISaveStore store = GetSaveStore();
        List<int> profileIds = [];

        for (int profileId = 1; profileId <= maxProfileId; profileId++)
        {
            try
            {
                await SyncKnownProfileFilesFromCloudAsync(
                    store,
                    profileId,
                    SaveSlotMode.Normal,
                    includeDirectories: false,
                    deleteLocalWhenCloudMissing: false);

                if (ProfileHasSave(profileId, SaveSlotMode.Normal))
                {
                    profileIds.Add(profileId);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"检查普通模式 {profileId} 号槽是否可导入失败。", ex);
            }
        }

        return profileIds;
    }

    public static Task ImportNormalProfileToModdedAsync(
        int sourceProfileId,
        int targetProfileId,
        bool overwriteTarget)
    {
        return CopyProfileAsync(
            sourceProfileId,
            targetProfileId,
            SaveSlotMode.Normal,
            SaveSlotMode.Modded,
            overwriteTarget);
    }

    public static string GetProfileDir(int profileId, SaveSlotMode mode)
    {
        string prefix = mode == SaveSlotMode.Modded ? "modded/" : string.Empty;
        return $"{prefix}profile{profileId}";
    }

    public static string GetRelativePath(int profileId, SaveSlotMode mode, string relativePath)
    {
        return NormalizeRelativePath($"{GetProfileDir(profileId, mode)}/{relativePath}");
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string Describe(SaveSlotMode mode, int profileId)
    {
        string modeName = mode == SaveSlotMode.Modded ? "MOD" : "普通";
        return string.Create(CultureInfo.InvariantCulture, $"{modeName} profile{profileId}");
    }
}
