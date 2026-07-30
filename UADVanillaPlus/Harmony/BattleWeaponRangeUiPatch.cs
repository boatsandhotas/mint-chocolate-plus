using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace UADVanillaPlus.Harmony;

// Adds a compact target-range line to each battle weapon row's existing accuracy
// label. The row callback already owns the current Aim object and row GameObject,
// so this avoids extra scene scans or a new cramped UI child.
[HarmonyPatch]
internal static class BattleWeaponRangeUiPatch
{
    private static readonly Type? WeaponRowCallbackType = ResolveWeaponRowCallbackType();
    private static readonly MethodInfo? WeaponRowCallbackMethod = WeaponRowCallbackType == null
        ? null
        : ResolveCallbackMethod(WeaponRowCallbackType);
    private static readonly MethodInfo? AimGetter = ResolveGetter(WeaponRowCallbackType, "aim", "get_aim");
    private static readonly MethodInfo? TempObjGetter = ResolveGetter(WeaponRowCallbackType, "tempObj", "get_tempObj");
    private static readonly MethodInfo? Locals3Getter = ResolveGetter(
        WeaponRowCallbackType,
        "field_Public___c__DisplayClass465_3_0",
        "get_field_Public___c__DisplayClass465_3_0");
    private static readonly MethodInfo? Locals2Getter = Locals3Getter == null
        ? null
        : ResolveGetter(
            Locals3Getter.ReturnType,
            "field_Public___c__DisplayClass465_2_0",
            "get_field_Public___c__DisplayClass465_2_0");
    private static readonly MethodInfo? Locals1Getter = Locals2Getter == null
        ? null
        : ResolveGetter(
            Locals2Getter.ReturnType,
            "field_Public___c__DisplayClass465_0_0",
            "get_field_Public___c__DisplayClass465_0_0");
    private static readonly MethodInfo? ShipGetter = Locals1Getter == null ? null : ResolveGetter(Locals1Getter.ReturnType, "ship", "get_ship");
    private static readonly Regex RangeLinePattern = new(
        @"(?:\r?\n)?(?:<color=#[0-9a-fA-F]{6,8}>)?\d+(?:\.\d)? km(?:</color>)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const float AccuracyMissRetrySeconds = 1f;
    private static readonly Dictionary<int, CachedAccuracyLabel> AccuracyLabelByRow = new();
    private static readonly Dictionary<int, float> AccuracyMissRetryAfter = new();
    private static bool loggedActive;

    private static MethodBase? TargetMethod()
        => WeaponRowCallbackMethod;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool available = WeaponRowCallbackMethod != null &&
                         AimGetter != null &&
                         TempObjGetter != null;
        if (!available)
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP battle weapon range UI unavailable: RefreshShipInfo weapon callback not found.");

        return available;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance)
    {
        try
        {
            DecorateAimText(__instance);
        }
        catch
        {
            // This runs during a hot battle UI refresh path. Fail closed and let
            // vanilla keep the original weapon row text if anything is unusual.
        }
    }

    private static void DecorateAimText(object instance)
    {
        Ship.Aim? aim = Safe(() => InvokeGetter(AimGetter, instance) as Ship.Aim, null);
        GameObject? row = Safe(() => InvokeGetter(TempObjGetter, instance) as GameObject, null);
        if (row == null)
            return;

        if (!TryGetAccuracyText(row, out Text? text, out TMP_Text? tmpText))
            return;

        string current = text != null
            ? Safe(() => text.text, string.Empty)
            : Safe(() => tmpText?.text ?? string.Empty, string.Empty);
        string clean = StripRangeLine(current);

        Ship? ship = TryGetSelectedShip(instance);
        string next = clean;
        if (aim != null &&
            clean.Contains("%", StringComparison.Ordinal) &&
            TryGetRangeKilometers(aim, ship, out float kilometers))
        {
            string rangeLine = kilometers.ToString("0.0", CultureInfo.InvariantCulture) + " km";
            next = string.IsNullOrWhiteSpace(clean) ? rangeLine : clean.TrimEnd() + "\n" + rangeLine;
            LogActiveOnce();
        }

        if (string.Equals(current, next, StringComparison.Ordinal))
            return;

        if (text != null)
            text.text = next;
        else if (tmpText != null)
            tmpText.text = next;
    }

    private static bool TryGetAccuracyText(GameObject row, out Text? text, out TMP_Text? tmpText)
    {
        text = null;
        tmpText = null;

        int rowId = Safe(() => row.GetInstanceID(), 0);
        if (rowId == 0)
            return false;

        if (AccuracyLabelByRow.TryGetValue(rowId, out CachedAccuracyLabel cached))
        {
            if (cached.TryGet(out text, out tmpText))
                return true;

            AccuracyLabelByRow.Remove(rowId);
        }

        float now = Safe(() => Time.realtimeSinceStartup, 0f);
        if (AccuracyMissRetryAfter.TryGetValue(rowId, out float retryAfter))
        {
            if (now < retryAfter)
                return false;

            AccuracyMissRetryAfter.Remove(rowId);
        }

        if (FindNamedText(row, "Acc", out text, out tmpText))
        {
            AccuracyLabelByRow[rowId] = new CachedAccuracyLabel(text, tmpText);
            AccuracyMissRetryAfter.Remove(rowId);
            return true;
        }

        AccuracyMissRetryAfter[rowId] = now + AccuracyMissRetrySeconds;
        return false;
    }

    private static bool FindNamedText(GameObject root, string name, out Text? text, out TMP_Text? tmpText)
    {
        text = null;
        tmpText = null;

        Transform? found = FindDescendantByName(Safe(() => root.transform, null), name);
        if (found == null)
            return false;

        GameObject? foundObject = Safe(() => found.gameObject, null);
        if (foundObject == null)
            return false;

        text = Safe(() => foundObject.GetComponent<Text>() ?? foundObject.GetComponentInChildren<Text>(true), null);
        if (text != null)
            return true;

        tmpText = Safe(() => foundObject.GetComponent<TMP_Text>() ?? foundObject.GetComponentInChildren<TMP_Text>(true), null);
        return tmpText != null;
    }

    private static Transform? FindDescendantByName(Transform? root, string name)
    {
        if (root == null)
            return null;

        if (string.Equals(Safe(() => root.name, string.Empty), name, StringComparison.Ordinal))
            return root;

        int childCount = Safe(() => root.childCount, 0);
        for (int index = 0; index < childCount; index++)
        {
            Transform? child = Safe(() => root.GetChild(index), null);
            Transform? found = FindDescendantByName(child, name);
            if (found != null)
                return found;
        }

        return null;
    }

    private static string StripRangeLine(string? text)
    {
        string result = text ?? string.Empty;
        while (true)
        {
            string stripped = RangeLinePattern.Replace(result, string.Empty).TrimEnd();
            if (string.Equals(stripped, result, StringComparison.Ordinal))
                return stripped;

            result = stripped;
        }
    }

    private static bool TryGetRangeKilometers(Ship.Aim aim, Ship? ship, out float kilometers)
    {
        kilometers = 0f;

        Ship? target = Safe(() => aim.target, null);
        if (target == null)
            return false;

        float range = 0f;
        bool hasRange = Safe(
            () =>
            {
                if (!aim.lastRange.HasValue)
                    return false;

                range = aim.lastRange.Value;
                return true;
            },
            false);

        if (!hasRange && ship != null)
        {
            hasRange = Safe(
                () =>
                {
                    range = Vector3.Distance(ship.transform.position, target.transform.position);
                    return true;
                },
                false);
        }

        if (!hasRange)
            return false;

        kilometers = NormalizeRangeKilometers(range);
        return kilometers > 0.01f && kilometers < 250f;
    }

    private static float NormalizeRangeKilometers(float range)
        => range > 100f ? range / 1000f : range;

    private static Ship? TryGetSelectedShip(object instance)
    {
        object? locals3 = Safe(() => InvokeGetter(Locals3Getter, instance), null);
        object? locals2 = locals3 == null ? null : Safe(() => InvokeGetter(Locals2Getter, locals3), null);
        object? locals1 = locals2 == null ? null : Safe(() => InvokeGetter(Locals1Getter, locals2), null);
        return locals1 == null ? null : Safe(() => InvokeGetter(ShipGetter, locals1) as Ship, null);
    }

    private static void LogActiveOnce()
    {
        if (loggedActive)
            return;

        loggedActive = true;
        Melon<UADVanillaPlusMod>.Logger.Msg("UADVP battle weapon range UI active: patched weapon accuracy rows.");
    }

    private static Type? ResolveWeaponRowCallbackType()
        => typeof(Ui)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(type =>
                type.Name.Contains("DisplayClass465_4", StringComparison.Ordinal) &&
                ResolveGetter(type, "aim", "get_aim") != null &&
                ResolveGetter(type, "tempObj", "get_tempObj") != null &&
                ResolveCallbackMethod(type) != null);

    private static MethodInfo? ResolveCallbackMethod(Type type)
        => FindMethod(type, "_RefreshShipInfo_b__16", typeof(MiniScript)) ??
           FindMethod(type, "<RefreshShipInfo>b__16", typeof(MiniScript));

    private static MethodInfo? ResolveGetter(Type? type, params string[] names)
    {
        if (type == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in names)
        {
            MethodInfo? getter = type.GetProperty(name, flags)?.GetGetMethod(true);
            if (getter != null)
                return getter;

            string methodName = name.StartsWith("get_", StringComparison.Ordinal) ? name : "get_" + name;
            getter = FindMethod(type, methodName);
            if (getter != null)
                return getter;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name, params Type[] parameterTypes)
        => type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null);

    private static object? InvokeGetter(MethodInfo? getter, object instance)
        => getter == null ? null : getter.Invoke(instance, Array.Empty<object>());

    private sealed class CachedAccuracyLabel
    {
        private readonly Text? text;
        private readonly TMP_Text? tmpText;

        internal CachedAccuracyLabel(Text? text, TMP_Text? tmpText)
        {
            this.text = text;
            this.tmpText = tmpText;
        }

        internal bool TryGet(out Text? cachedText, out TMP_Text? cachedTmpText)
        {
            cachedText = text;
            cachedTmpText = tmpText;
            return cachedText != null || cachedTmpText != null;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}
