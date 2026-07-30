using System.Globalization;
using System.Reflection;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.Harmony;

namespace UADVanillaPlus.GameData;

// Centralized safe wrapper for VP-owned AI design buildability probes. It does
// not make invalid designs buildable; exception paths fail closed and emit the
// design identity that vanilla's stack trace does not include.
internal static class AiDesignBuildability
{
    private const string LogPrefix = "UADVP AI buildability";
    private static readonly HashSet<string> LoggedExceptionKeys = new(StringComparer.Ordinal);

    internal static bool CanBuildDesign(Player? player, Ship? design, int amount, string caller, out string reason)
    {
        reason = "unknown";
        Player? owner = player ?? Safe(() => design?.player, null);
        if (owner == null)
        {
            reason = "noPlayer";
            return false;
        }

        if (design == null)
        {
            reason = "noDesign";
            return false;
        }

        PlayerController? controller = PlayerController.Instance;
        if (controller == null)
        {
            reason = "noPlayerController";
            return false;
        }

        if (amount <= 0)
            amount = 1;

        try
        {
            string localReason = "unknown";
            bool result = CampaignAiShipbuildingDiagnosticsPatch.WithoutValidationAggregation(() =>
                controller.CanBuildShipsFromDesign(design, amount, out localReason));
            reason = NormalizeReason(localReason);
            return result;
        }
        catch (Exception ex)
        {
            reason = "exception:" + ex.GetType().Name;
            LogException(owner, design, amount, caller, reason, ex);
            return false;
        }
    }

    internal static bool IsAiPlayer(Player? player)
        => player != null && Safe(() => player.isAi && !player.isMain, false);

    internal static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "unknown"
            : reason.Trim().Replace(' ', '_');

    private static void LogException(Player player, Ship design, int amount, string caller, string reason, Exception ex)
    {
        string key = string.Join(
            "|",
            LogToken(caller),
            PlayerPointer(player),
            ShipPointer(design),
            SafeString(() => design.id.ToString()),
            HullKey(design),
            ex.GetType().FullName ?? ex.GetType().Name,
            ExceptionMessage(ex));
        if (!LoggedExceptionKeys.Add(key))
            return;

        Melon<UADVanillaPlusMod>.Logger.Warning(
            $"{LogPrefix}: exception caller={LogToken(caller)} nation={LogToken(AiDesignCompetitiveness.PlayerLabel(player))} " +
            $"design={Quoted(AiDesignCompetitiveness.ShipLabel(design))} id={LogToken(AiDesignCompetitiveness.ShipId(design))} " +
            $"type={LogToken(AiDesignCompetitiveness.NormalizeShipType(design.shipType))} hull={LogToken(HullKey(design))} " +
            $"tons={Fmt(Safe(() => design.Tonnage(), 0f))} amount={amount} dateCreated={GameDateLabel(Safe(() => design.dateCreated, default))} " +
            $"dateCreatedRefit={GameDateLabel(Safe(() => design.dateCreatedRefit, default))} isDesign={BoolText(Safe(() => design.isDesign, false))} " +
            $"isRefit={BoolText(Safe(() => design.isRefitDesign, false))} shared={BoolText(IsSharedDesign(design))} " +
            $"erased={BoolText(Safe(() => design.isErased, false))} parts={PartCount(design)} ex={LogToken(ex.GetType().Name)} " +
            $"message={LogToken(ExceptionMessage(ex))} reason={LogToken(reason)} result=false.");
    }

    private static string HullKey(Ship ship)
    {
        string key = SafeString(() => ship.hull?.data?.name);
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        key = SafeString(() => ship.hull?.name);
        return string.IsNullOrWhiteSpace(key) ? "unknown" : key;
    }

    private static int PartCount(Ship ship)
    {
        try
        {
            if (ship.parts == null)
                return 0;

            int count = 0;
            foreach (Part part in ship.parts)
            {
                if (part != null)
                    count++;
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsSharedDesign(Ship ship)
        => Safe(() =>
               ReadBoolMember(ship, "IsSharedDesign") ||
               ReadBoolMember(ship, "isSharedDesign") ||
               ReadBoolMember(ship, "isShared"),
           false);

    private static bool ReadBoolMember(object target, string memberName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();

        PropertyInfo? property = type.GetProperty(memberName, flags);
        if (property?.PropertyType == typeof(bool))
            return (bool)(property.GetValue(target) ?? false);

        FieldInfo? field = type.GetField(memberName, flags);
        if (field?.FieldType == typeof(bool))
            return (bool)(field.GetValue(target) ?? false);

        return false;
    }

    private static string GameDateLabel(GameDate date)
    {
        int turn = Safe(() => date.turn, -1);
        int year = Safe(() => date.AsDate().Year, -1);
        return $"{turn}/{year}";
    }

    private static string ExceptionMessage(Exception ex)
    {
        Exception? current = ex is TargetInvocationException { InnerException: not null }
            ? ex.InnerException
            : ex;

        List<string> parts = new();
        int depth = 0;
        while (current != null && depth < 3)
        {
            string message = string.IsNullOrWhiteSpace(current.Message) ? current.GetType().Name : current.Message;
            parts.Add($"{current.GetType().Name}:{Compact(message)}");
            current = current.InnerException;
            depth++;
        }

        return parts.Count == 0 ? ex.GetType().Name : string.Join(">", parts);
    }

    private static string Compact(string value)
        => value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

    private static string Quoted(string value)
        => "\"" + Compact(value).Replace("\"", "'") + "\"";

    private static string LogToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : Compact(value).Replace(' ', '_');

    private static string SafeString(Func<string?> read)
    {
        try
        {
            string? value = read();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long PlayerPointer(Player player)
        => Safe(() => player.Pointer.ToInt64(), 0L);

    private static long ShipPointer(Ship ship)
        => Safe(() => ship.Pointer.ToInt64(), 0L);

    private static string Fmt(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BoolText(bool value)
        => value.ToString().ToLowerInvariant();

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
