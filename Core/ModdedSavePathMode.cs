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
        ModLogger.Warn($"存档初始化阶段检测到 MOD 存档路径标记仍未启用，可能受到其他 MOD 影响，已由 BetterSaveSlots 补设。来源={source}。");
    }
}
