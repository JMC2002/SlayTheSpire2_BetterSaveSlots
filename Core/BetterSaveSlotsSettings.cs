using JmcModLib.Config;
using JmcModLib.Config.UI;

namespace BetterSaveSlots.Core;

public static class BetterSaveSlotsSettings
{
    public const int VanillaSlotCount = 3;
    public const int MaxSlotCount = 12;
    public const int SlotsPerPage = 3;

    private const string SaveSlotGroup = "save_slots";

    [UIIntSlider(VanillaSlotCount, MaxSlotCount)]
    [Config(
        "存档槽总数",
        group: SaveSlotGroup,
        Description = "配置存档槽总数。修改后需要重新进入存档选择流程，必要时重启游戏。",
        Key = "slot_count",
        RestartRequired = true,
        Order = 10)]
    public static int SlotCount = VanillaSlotCount;

    public static int EffectiveSlotCount => Math.Clamp(SlotCount, VanillaSlotCount, MaxSlotCount);
}
