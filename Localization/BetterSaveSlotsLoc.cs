using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Localization;

namespace BetterSaveSlots.Localization;

internal static class BetterSaveSlotsLoc
{
    public const string Table = L10n.DefaultTable;
    private const string Prefix = "EXTENSION.BETTERSAVESLOTS.";

    public static LocString Loc(string key)
    {
        return L10n.Create(Table, BuildKey(key));
    }

    public static string Text(string key)
    {
        string fullKey = BuildKey(key);
        return L10n.Resolve(fullKey, fullKey, Table);
    }

    public static string Format(string key, params (string Name, object Value)[] variables)
    {
        string fullKey = BuildKey(key);

        return L10n.Resolve(
            fullKey,
            fullKey,
            Table,
            configure: locString =>
            {
                foreach ((string name, object value) in variables)
                {
                    locString.AddObj(name, value);
                }
            });
    }

    private static string BuildKey(string key)
    {
        return key.StartsWith("EXTENSION.", StringComparison.Ordinal)
            ? key
            : Prefix + key;
    }
}
