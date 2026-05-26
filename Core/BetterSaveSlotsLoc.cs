using MegaCrit.Sts2.Core.Localization;

namespace BetterSaveSlots.Core;

internal static class BetterSaveSlotsLoc
{
    public const string Table = "settings_ui";
    private const string Prefix = "EXTENSION.BETTERSAVESLOTS.";

    public static LocString Loc(string key)
    {
        return new LocString(Table, Prefix + key);
    }

    public static string Text(string key)
    {
        return Loc(key).GetFormattedText();
    }

    public static string Format(string key, params (string Name, object Value)[] variables)
    {
        LocString locString = Loc(key);
        foreach ((string name, object value) in variables)
        {
            locString.AddObj(name, value);
        }

        return locString.GetFormattedText();
    }
}
