using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using System.Text.Json;

namespace BetterSaveSlots.Features.SaveSlots;

public static partial class SaveSlotService
{
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
}
