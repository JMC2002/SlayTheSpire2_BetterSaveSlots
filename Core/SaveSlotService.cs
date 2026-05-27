using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using System.Globalization;
using System.Text.Json;

namespace BetterSaveSlots.Core;

public static class SaveSlotService
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

        int copiedFiles = 0;
        foreach (string rootName in ProfileRootNames)
        {
            string sourceRoot = GetRelativePath(sourceProfileId, sourceMode, rootName);
            string targetRoot = GetRelativePath(targetProfileId, targetMode, rootName);
            copiedFiles += await CopyDirectoryRecursiveAsync(store, sourceRoot, targetRoot);
        }

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

    public static async Task SyncKnownProfileFilesFromCloudAsync(
        ISaveStore store,
        int profileId,
        SaveSlotMode mode,
        bool includeDirectories = true,
        bool deleteLocalWhenCloudMissing = true)
    {
        if (store is not CloudSaveStore cloudSaveStore)
        {
            return;
        }

        foreach (string path in EnumerateKnownSaveFiles(profileId, mode))
        {
            if (deleteLocalWhenCloudMissing || CloudFileExists(cloudSaveStore, path))
            {
                await cloudSaveStore.SyncCloudToLocal(path);
            }
        }

        if (!includeDirectories)
        {
            return;
        }

        await SyncCloudDirectorySafelyAsync(
            cloudSaveStore,
            GetRelativePath(profileId, mode, "saves/history"),
            deleteLocalWhenCloudMissing);
        await SyncCloudDirectorySafelyAsync(
            cloudSaveStore,
            GetRelativePath(profileId, mode, "replays"),
            deleteLocalWhenCloudMissing);
    }

    public static async Task OverwriteKnownProfileFilesToCloudAsync(ISaveStore store, int profileId, SaveSlotMode mode)
    {
        if (store is not CloudSaveStore cloudSaveStore)
        {
            return;
        }

        foreach (string path in EnumerateKnownSaveFiles(profileId, mode))
        {
            await cloudSaveStore.OverwriteCloudWithLocal(path);
        }

        await Task.WhenAll(cloudSaveStore.OverwriteCloudWithLocalDirectory(
            GetRelativePath(profileId, mode, "saves/history"),
            RunHistorySaveManager.maxCloudBytes,
            RunHistorySaveManager.maxCloudFileCount));
    }

    public static void CleanupTemporaryFiles(ISaveStore store, int profileId, SaveSlotMode mode)
    {
        string savesDir = GetRelativePath(profileId, mode, UserDataPathProvider.SavesDir);
        store.DeleteTemporaryFiles(savesDir);
        store.DeleteTemporaryFiles(GetRelativePath(profileId, mode, "saves/history"));
    }

    public static void CleanupStaleCurrentRunSaves(ISaveStore store, int profileId, SaveSlotMode mode)
    {
        foreach (string fileName in CurrentRunFileNames)
        {
            CleanupStaleCurrentRunSave(store, profileId, mode, fileName);
        }
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

    private static IEnumerable<string> EnumerateKnownSaveFiles(int profileId, SaveSlotMode mode)
    {
        yield return GetRelativePath(profileId, mode, "saves/progress.save");
        yield return GetRelativePath(profileId, mode, "saves/current_run.save");
        yield return GetRelativePath(profileId, mode, "saves/current_run_mp.save");
        yield return GetRelativePath(profileId, mode, "saves/prefs.save");
    }

    private static async Task SyncCloudDirectorySafelyAsync(
        CloudSaveStore cloudSaveStore,
        string directoryPath,
        bool deleteLocalWhenCloudMissing)
    {
        HashSet<string> visitedPaths = [];

        foreach (string fileName in ListCloudFiles(cloudSaveStore, directoryPath))
        {
            if (ShouldSkipCloudSyncFile(fileName))
            {
                continue;
            }

            string path = NormalizeRelativePath($"{directoryPath}/{fileName}");
            visitedPaths.Add(path);
            await cloudSaveStore.SyncCloudToLocal(path);
        }

        if (!deleteLocalWhenCloudMissing || !cloudSaveStore.LocalStore.DirectoryExists(directoryPath))
        {
            return;
        }

        foreach (string fileName in cloudSaveStore.LocalStore.GetFilesInDirectory(directoryPath))
        {
            if (ShouldSkipCloudSyncFile(fileName))
            {
                continue;
            }

            string path = NormalizeRelativePath($"{directoryPath}/{fileName}");
            if (!visitedPaths.Contains(path))
            {
                await cloudSaveStore.SyncCloudToLocal(path);
            }
        }
    }

    private static string[] ListCloudFiles(CloudSaveStore cloudSaveStore, string directoryPath)
    {
        try
        {
            return cloudSaveStore.CloudStore.DirectoryExists(directoryPath)
                ? cloudSaveStore.CloudStore.GetFilesInDirectory(directoryPath)
                : [];
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"列出云存档目录失败，已跳过：{directoryPath}", ex);
            return [];
        }
    }

    private static bool CloudFileExists(CloudSaveStore cloudSaveStore, string path)
    {
        try
        {
            return cloudSaveStore.CloudStore.FileExists(path);
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"检查云端存档文件失败，已跳过：{path}", ex);
            return false;
        }
    }

    private static async Task<int> CopyDirectoryRecursiveAsync(ISaveStore store, string sourceDirectory, string targetDirectory)
    {
        if (!store.DirectoryExists(sourceDirectory))
        {
            return 0;
        }

        store.CreateDirectory(targetDirectory);

        int copiedFiles = 0;
        foreach (string fileName in store.GetFilesInDirectory(sourceDirectory))
        {
            if (ShouldSkipCopyFile(fileName))
            {
                continue;
            }

            string sourcePath = NormalizeRelativePath($"{sourceDirectory}/{fileName}");
            string targetPath = NormalizeRelativePath($"{targetDirectory}/{fileName}");
            byte[] bytes = ReadLocalBytes(store, sourcePath);
            await store.WriteFileAsync(targetPath, bytes);
            copiedFiles++;
        }

        foreach (string directoryName in store.GetDirectoriesInDirectory(sourceDirectory))
        {
            string sourceChild = NormalizeRelativePath($"{sourceDirectory}/{directoryName}");
            string targetChild = NormalizeRelativePath($"{targetDirectory}/{directoryName}");
            copiedFiles += await CopyDirectoryRecursiveAsync(store, sourceChild, targetChild);
        }

        return copiedFiles;
    }

    private static byte[] ReadLocalBytes(ISaveStore store, string relativePath)
    {
        string fullPath = store.GetFullPath(relativePath);
        using Godot.FileAccess? file = Godot.FileAccess.Open(fullPath, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            throw new FileNotFoundException(
                $"无法读取源存档文件：{relativePath}，GodotError={Godot.FileAccess.GetOpenError()}",
                relativePath);
        }

        return file.GetBuffer((long)file.GetLength());
    }

    private static void DeleteProfileDirectory(ISaveStore store, int profileId, SaveSlotMode mode)
    {
        string profileDir = GetProfileDir(profileId, mode);
        foreach (string rootName in ProfileRootNames)
        {
            DeleteDirectoryRecursive(store, NormalizeRelativePath($"{profileDir}/{rootName}"));
        }

        DeleteDirectoryIfLocalExists(store, profileDir);
    }

    private static void DeleteDirectoryRecursive(ISaveStore store, string directoryPath)
    {
        if (store.DirectoryExists(directoryPath))
        {
            foreach (string childDirectory in store.GetDirectoriesInDirectory(directoryPath))
            {
                DeleteDirectoryRecursive(store, NormalizeRelativePath($"{directoryPath}/{childDirectory}"));
            }

            foreach (string fileName in store.GetFilesInDirectory(directoryPath))
            {
                store.DeleteFile(NormalizeRelativePath($"{directoryPath}/{fileName}"));
            }
        }

        DeleteCloudOnlyFiles(store, directoryPath);
        DeleteDirectoryIfLocalExists(store, directoryPath);
    }

    private static void DeleteCloudOnlyFiles(ISaveStore store, string directoryPath)
    {
        if (store is not CloudSaveStore cloudSaveStore)
        {
            return;
        }

        foreach (string fileName in ListCloudFiles(cloudSaveStore, directoryPath))
        {
            string path = NormalizeRelativePath($"{directoryPath}/{fileName}");
            try
            {
                cloudSaveStore.CloudStore.DeleteFile(path);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"删除云端存档文件失败：{path}", ex);
            }
        }
    }

    private static void DeleteDirectoryIfLocalExists(ISaveStore store, string directoryPath)
    {
        try
        {
            if (store.DirectoryExists(directoryPath))
            {
                store.DeleteDirectory(directoryPath);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"删除本地存档目录失败：{directoryPath}", ex);
        }
    }

    private static void CleanupStaleCurrentRunSave(
        ISaveStore store,
        int profileId,
        SaveSlotMode mode,
        string runSaveFileName)
    {
        string runSavePath = GetRelativePath(profileId, mode, $"saves/{runSaveFileName}");
        string backupPath = runSavePath + ".backup";
        string? pathToCheck = null;

        if (store.FileExists(runSavePath))
        {
            pathToCheck = runSavePath;
        }
        else if (store.FileExists(backupPath))
        {
            pathToCheck = backupPath;
        }

        if (pathToCheck == null)
        {
            return;
        }

        try
        {
            string? json = store.ReadFile(pathToCheck);
            if (json == null)
            {
                return;
            }

            long? startTime = ExtractStartTimeFromRunSave(json);
            if (!startTime.HasValue)
            {
                return;
            }

            string historyPath = GetRelativePath(profileId, mode, $"saves/history/{startTime.Value}.run");
            if (store.FileExists(historyPath))
            {
                ModLogger.Warn(
                    $"清理扩展槽陈旧当前局存档：Profile={profileId}, File={runSaveFileName}, StartTime={startTime.Value}。");
                store.DeleteFile(runSavePath);
                store.DeleteFile(backupPath);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"检查扩展槽 current_run 是否陈旧失败：Profile={profileId}, File={runSaveFileName}。", ex);
        }
    }

    private static long? ExtractStartTimeFromRunSave(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("start_time", out JsonElement value))
            {
                return value.GetInt64();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool ShouldSkipCopyFile(string fileName)
    {
        return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup.backup", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipCloudSyncFile(string fileName)
    {
        return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup.backup", StringComparison.OrdinalIgnoreCase);
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
