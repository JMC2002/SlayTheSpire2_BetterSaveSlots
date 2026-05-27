using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace BetterSaveSlots.Features.SaveSlots;

public static partial class SaveSlotService
{
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
        await Task.WhenAll(cloudSaveStore.OverwriteCloudWithLocalDirectory(
            GetRelativePath(profileId, mode, "replays"),
            byteLimit: null,
            fileLimit: null));
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

    private static bool ShouldSkipCloudSyncFile(string fileName)
    {
        return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup.backup", StringComparison.OrdinalIgnoreCase);
    }
}
