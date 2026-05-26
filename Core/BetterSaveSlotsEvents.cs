namespace BetterSaveSlots.Core;

internal static class BetterSaveSlotsEvents
{
    public static event Action? ProfilesChanged;

    public static void RaiseProfilesChanged()
    {
        ProfilesChanged?.Invoke();
    }
}
