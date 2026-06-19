using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Saves;

namespace BetterSaveSlots.Core;

internal static class ModdedSavePathMode
{
    public static void Ensure(string source)
    {
        if (UserDataPathProvider.IsRunningModded)
        {
            return;
        }

        UserDataPathProvider.IsRunningModded = true;
        ModLogger.Warn($"检测到游戏尚未启用 MOD 存档路径，已由 BetterSaveSlots 补设。来源={source}。");
    }
}
