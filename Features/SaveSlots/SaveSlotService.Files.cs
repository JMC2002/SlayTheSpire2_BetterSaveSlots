using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;

namespace BetterSaveSlots.Features.SaveSlots;

public static partial class SaveSlotService
{
    private static readonly string[] VolatileSaveDirectoryPrefixes =
    [
        "handsup_"
    ];

    private static readonly string[] VolatileSaveFilePrefixes =
    [
        "handsup_"
    ];

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
            store.SetLastModifiedTime(targetPath, store.GetLastModifiedTime(sourcePath));
            copiedFiles++;
        }

        foreach (string directoryName in store.GetDirectoriesInDirectory(sourceDirectory))
        {
            if (ShouldSkipCopyDirectory(directoryName))
            {
                continue;
            }

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

    private static void DeleteDirectoryIfLocalExists(ISaveStore store, string directoryPath)
    {
        try
        {
            ISaveStore localStore = GetLocalStore(store);
            if (localStore.DirectoryExists(directoryPath))
            {
                localStore.DeleteDirectory(directoryPath);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"删除本地存档目录失败：{directoryPath}", ex);
        }
    }

    private static ISaveStore GetLocalStore(ISaveStore store)
    {
        return store is CloudSaveStore cloudSaveStore ? cloudSaveStore.LocalStore : store;
    }

    private static bool ShouldSkipCopyFile(string fileName)
    {
        return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup.backup", StringComparison.OrdinalIgnoreCase)
            || HasAnyPrefix(fileName, VolatileSaveFilePrefixes);
    }

    private static bool ShouldSkipCopyDirectory(string directoryName)
    {
        return HasAnyPrefix(directoryName, VolatileSaveDirectoryPrefixes);
    }

    private static bool HasAnyPrefix(string value, IReadOnlyList<string> prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
