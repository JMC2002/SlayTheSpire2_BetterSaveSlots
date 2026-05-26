using Godot;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterSaveSlots.Core;

internal static class BetterSaveSlotsState
{
    private const string StateRelativePath = "BetterSaveSlots/state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int? CurrentProfileId
    {
        get
        {
            try
            {
                StateData state = Load();
                return state.CurrentProfileId;
            }
            catch (Exception ex)
            {
                ModLogger.Warn("读取 BetterSaveSlots 状态失败，将使用游戏原本记录。", ex);
                return null;
            }
        }
        set
        {
            try
            {
                StateData state = Load();
                state.CurrentProfileId = value;
                Save(state);
            }
            catch (Exception ex)
            {
                ModLogger.Warn("写入 BetterSaveSlots 状态失败。", ex);
            }
        }
    }

    private static StateData Load()
    {
        string path = GetStatePath();
        if (!Godot.FileAccess.FileExists(path))
        {
            return new StateData();
        }

        using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            ModLogger.Warn($"打开 BetterSaveSlots 状态文件失败：{path}，Error={Godot.FileAccess.GetOpenError()}");
            return new StateData();
        }

        string json = file.GetAsText();
        return JsonSerializer.Deserialize<StateData>(json, JsonOptions) ?? new StateData();
    }

    private static void Save(StateData state)
    {
        string path = GetStatePath();
        string directory = path.GetBaseDir();
        if (!DirAccess.DirExistsAbsolute(directory))
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
        }

        string json = JsonSerializer.Serialize(state, JsonOptions);
        using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file == null)
        {
            ModLogger.Warn($"打开 BetterSaveSlots 状态文件写入失败：{path}，Error={Godot.FileAccess.GetOpenError()}");
            return;
        }

        file.StoreString(json);
    }

    private static string GetStatePath()
    {
        return UserDataPathProvider.GetAccountScopedBasePath(StateRelativePath);
    }

    private sealed class StateData
    {
        [JsonPropertyName("current_profile_id")]
        public int? CurrentProfileId { get; set; }
    }
}
