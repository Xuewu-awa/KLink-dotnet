namespace KLink.Bot.Engine;

/// <summary>Native BaseCardObject custom-name attributes (installed PE, RULE-097).</summary>
internal static class CardCustomNames
{
    // The native FString comparator folds ASCII through a byte table; non-ASCII
    // UTF-16 code units are compared directly, not with Unicode case folding.
    internal static bool Equal(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            if (Fold(left[i]) != Fold(right[i])) return false;
        }
        return true;
    }

    internal static string Text(CardInstance card, string key)
    {
        string value = card.CustomJson.GetValueOrDefault(key, "");
        return value.Length == 0 || Equal(value, "None") ? "None" : value;
    }

    internal static bool Has(CardInstance card, string key, string attribute)
        => attribute.Length > 0 && Text(card, key).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => Equal(part, attribute));

    internal static List<string> List(CardInstance card, string key)
    {
        string value = Text(card, key);
        return Equal(value, "None") ? new List<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    internal static void Add(CardInstance card, string key, string attribute)
    {
        string value = Text(card, key);
        Assign(card, key, Equal(value, "None") ? attribute : value + ";" + attribute);
    }

    internal static void Remove(CardInstance card, string key, string attribute)
    {
        string value = Text(card, key);
        Assign(card, key, Equal(value, attribute) ? ""
            : string.Join(";", value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !Equal(part, attribute))));
    }

    internal static void Assign(CardInstance card, string key, string value)
    {
        if (value.Length == 0 || Equal(value, "None")) card.CustomJson.Remove(key);
        else card.CustomJson[key] = value;
    }
}
