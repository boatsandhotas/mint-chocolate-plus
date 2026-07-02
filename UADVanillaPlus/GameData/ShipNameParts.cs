using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Il2Cpp;

namespace UADVanillaPlus.GameData;

// Shared base-name/suffix logic for ship naming, ported faithfully from
// DesignRefitNamePatch so the naming-theme system keys classes EXACTLY the way the
// game's refit naming does. A class name can carry: a refit-year suffix
// ("(Jul. 1904)" / "(1904a)"), a legacy clone counter ("- 2"), and a leading
// ship-type code ("BB ", "CA-"). BaseName strips all three; RefitSuffix returns the
// trailing "(...)" portion to re-append when renaming so refit variants keep their date.
//
// NOTE: this mirrors DesignRefitNamePatch's private helpers (kept in sync deliberately).
// The refit subsystem in the offline save tool (RenameGenericShips) groups refit
// FAMILIES via Ship.refitDesignListID; that family-wide rename is handled separately
// (ShipNaming) — this type is just the single-name parse both sides rely on.
internal static class ShipNameParts
{
    private static readonly string[] Months =
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    };

    private static readonly Regex RefitYearNameRegex = new(
        @"^\s*(?<base>.*?)\s*\((?:(?<month>[A-Za-z]{3})\.?\s+)?(?<year>\d{4})(?<letter>[A-Za-z]*)\)\s*(?:-\s*(?<number>\d+))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string BaseName(Ship? ship)
        => ship == null ? string.Empty : BaseName(SafeName(ship), SafeType(ship));

    internal static string BaseName(string? name, ShipType? shipType)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        if (TryReadRefitYearName(name, shipType, out string baseName, out _, out _, out _))
            return baseName;

        string cleaned = StripLegacyCloneSuffix(name.Trim());
        int paren = cleaned.IndexOf('(');
        if (paren > 0)
            cleaned = cleaned.Substring(0, paren).TrimEnd();

        return StripLeadingShipTypePrefix(cleaned, shipType);
    }

    // The trailing "(...)" portion to preserve when renaming (e.g. " (Jul. 1904)").
    internal static string RefitSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        int i = name.IndexOf(" (", StringComparison.Ordinal);
        return i >= 0 ? name.Substring(i) : string.Empty;
    }

    private static bool TryReadRefitYearName(string? name, ShipType? shipType, out string baseName, out int year, out int month, out int ordinal)
    {
        baseName = string.Empty;
        year = 0;
        month = 0;
        ordinal = 1;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        Match match = RefitYearNameRegex.Match(name);
        if (!match.Success || !int.TryParse(match.Groups["year"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
            return false;

        baseName = StripLeadingShipTypePrefix(match.Groups["base"].Value.Trim(), shipType);
        if (string.IsNullOrWhiteSpace(baseName))
            return false;

        month = MonthFromAbbreviation(match.Groups["month"].Value);

        Group number = match.Groups["number"];
        if (number.Success && int.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numberOrdinal))
        {
            ordinal = Math.Max(1, numberOrdinal);
            return true;
        }

        ordinal = LetterOrdinal(match.Groups["letter"].Value);
        return true;
    }

    private static string StripLeadingShipTypePrefix(string baseName, ShipType? shipType)
    {
        string cleaned = baseName.Trim();
        foreach (string typeCode in ShipTypeCodes(shipType))
        {
            if (!IsCompactShipTypeCode(typeCode))
                continue;

            string token = typeCode.Trim();
            if (cleaned.Length <= token.Length || !cleaned.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;

            char boundary = cleaned[token.Length];
            if (!char.IsWhiteSpace(boundary) && boundary != '-' && boundary != ':')
                continue;

            string withoutType = cleaned.Substring(token.Length + 1).TrimStart();
            while (withoutType.Length > 0 && (withoutType[0] == '-' || withoutType[0] == ':'))
                withoutType = withoutType.Substring(1).TrimStart();

            if (!string.IsNullOrWhiteSpace(withoutType))
                return withoutType;
        }

        return cleaned;
    }

    private static IEnumerable<string> ShipTypeCodes(ShipType? shipType)
    {
        if (shipType == null)
            yield break;
        string n = SafeStr(() => shipType.name);
        if (n.Length > 0)
            yield return n;
        string u = SafeStr(() => shipType.nameUi);
        if (u.Length > 0)
            yield return u;
    }

    private static bool IsCompactShipTypeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string token = value.Trim();
        if (token.Length < 1 || token.Length > 4)
            return false;
        foreach (char ch in token)
            if (!char.IsLetter(ch))
                return false;
        return true;
    }

    private static string StripLegacyCloneSuffix(string name)
    {
        int end = name.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(name[end]))
            end--;
        int digitEnd = end;
        while (end >= 0 && char.IsDigit(name[end]))
            end--;
        if (end == digitEnd)
            return name;
        while (end >= 0 && char.IsWhiteSpace(name[end]))
            end--;
        if (end <= 0 || name[end] != '-' || !char.IsWhiteSpace(name[end - 1]))
            return name;
        return name.Substring(0, end).TrimEnd();
    }

    private static int MonthFromAbbreviation(string? abbreviation)
    {
        if (string.IsNullOrWhiteSpace(abbreviation))
            return 0;
        string token = abbreviation.Trim();
        for (int i = 0; i < Months.Length; i++)
            if (string.Equals(token, Months[i], StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return 0;
    }

    private static int LetterOrdinal(string letters)
    {
        if (string.IsNullOrWhiteSpace(letters))
            return 1;
        int value = 0;
        foreach (char letter in letters.Trim().ToLowerInvariant())
        {
            if (letter < 'a' || letter > 'z')
                return 1;
            value = (value * 26) + (letter - 'a' + 1);
        }
        return Math.Max(1, value);
    }

    private static string SafeName(Ship ship) { try { return ship.name ?? string.Empty; } catch { return string.Empty; } }
    private static ShipType? SafeType(Ship ship) { try { return ship.shipType; } catch { return null; } }
    private static string SafeStr(Func<string?> f) { try { return f() ?? string.Empty; } catch { return string.Empty; } }
}
