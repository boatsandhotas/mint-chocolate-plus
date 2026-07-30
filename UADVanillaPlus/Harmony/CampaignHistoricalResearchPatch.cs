using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;

namespace UADVanillaPlus.Harmony;

// Patch intent: provide a deterministic research model where campaign year,
// not monthly research spending, owns ordinary technology availability.
[HarmonyPatch(typeof(CampaignController))]
internal static class CampaignHistoricalResearchPatch
{
    private const float ResearchCompleteProgress = 100f;
    private const int MaxSyncPasses = 256;
    private static readonly string[] TrackedHistoricalProbeTechs =
    {
        "gun_mech_25",
        "hull_strength_3",
        "hull_cruiser_3",
        "hull_cruiser_24",
        "gun_sec_8",
    };
    private static readonly MethodInfo? TechStartNewResearchsMethod =
        AccessTools.Method(typeof(CampaignController), "TechStartNewResearchs");
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedTrackedProbeVerifiedYears = new(StringComparer.Ordinal);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CampaignController.NextTurn))]
    internal static void NextTurnPrefix(CampaignController __instance)
        => ApplyBudgetClampOnly("next turn prefix", __instance);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CampaignController.GetResearchSpeed))]
    internal static bool GetResearchSpeedPrefix(ref float __result)
    {
        if (ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical)
            return true;

        __result = 0f;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("AiTechBudget")]
    internal static bool AiTechBudgetPrefix(Player player)
    {
        if (ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical)
            return true;

        int ignored = 0;
        ZeroResearchSpending(player, ref ignored);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch("OnNewTurn")]
    internal static void OnNewTurnPostfix(CampaignController __instance)
        => ApplyCurrentSetting("new turn", __instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CampaignController.OnLoadingScreenHide))]
    internal static void OnLoadingScreenHidePostfix(CampaignController __instance)
        => ApplyCurrentSetting("campaign load", __instance);

    internal static void ApplyCurrentSetting(string context = "manual", CampaignController? campaign = null)
    {
        if (ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical)
            return;

        SynchronizeHistoricalTechs(context, campaign ?? CampaignController.Instance);
    }

    internal static void ApplyBudgetClampOnly(string context = "manual", CampaignController? campaign = null)
    {
        if (ModSettings.TechnologySpread != ModSettings.TechnologySpreadMode.Historical)
            return;

        ClampHistoricalResearchSpending(context, campaign ?? CampaignController.Instance);
    }

    private static void SynchronizeHistoricalTechs(string context, CampaignController? campaign)
    {
        if (campaign?.CampaignData?.PlayersMajor == null)
            return;

        int currentYear = CampaignYear(campaign);
        if (currentYear <= 0)
            return;

        List<Player> players = MajorPlayers(campaign);
        if (players.Count == 0)
            return;

        List<TechnologyData> dueData = DueHistoricalTechData(currentYear);
        Dictionary<string, int> completedByPlayer = new(StringComparer.Ordinal);
        List<Technology> completedForMainPlayer = new();
        int completedTotal = 0;
        int completedExistingTotal = 0;
        int addedMissingDueTotal = 0;
        int syncedPlayers = 0;
        int budgetChanges = 0;
        int pass;

        for (pass = 1; pass <= MaxSyncPasses; pass++)
        {
            int completedThisPass = 0;

            foreach (Player player in players)
            {
                ZeroResearchSpending(player, ref budgetChanges);
                HistoricalSyncResult result = CompleteDueTechnologies(
                    player,
                    dueData,
                    IsMainPlayer(player) ? completedForMainPlayer : null);
                if (result.TotalChanged <= 0)
                    continue;

                completedThisPass += result.TotalChanged;
                completedTotal += result.TotalChanged;
                completedExistingTotal += result.CompletedExisting;
                addedMissingDueTotal += result.AddedMissingDue;
                string label = PlayerLabel(player);
                completedByPlayer[label] = completedByPlayer.TryGetValue(label, out int previous)
                    ? previous + result.TotalChanged
                    : result.TotalChanged;
            }

            if (completedThisPass == 0)
                break;

            RefreshVanillaResearchState(campaign);
        }

        foreach (Player player in players)
        {
            ZeroResearchSpending(player, ref budgetChanges);
            syncedPlayers++;
        }

        if (pass > MaxSyncPasses)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning(
                $"UADVP historical research: stopped after {MaxSyncPasses} passes at year {currentYear}; additional due techs may remain.");
        }

        HistoricalResearchAudit audit = AuditHistoricalResearch(players, dueData);

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP historical research: {currentYear} synced {syncedPlayers} nations, completed {completedTotal} technologies (existing={completedExistingTotal}, addedMissingDue={addedMissingDueTotal}), budgets zeroed, changedBudgets={budgetChanges}, passes={Math.Min(pass, MaxSyncPasses)}, context={context}.");

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP historical research audit: year={currentYear} players={players.Count} dueData={audit.DueDataCount} completedExisting={completedExistingTotal} addedMissingDue={addedMissingDueTotal} missingDueAfter={audit.MissingDueAfter} budgetsZeroed={syncedPlayers} changedBudgets={budgetChanges} passes={Math.Min(pass, MaxSyncPasses)} context={context}.");

        WarnForMissingDueTechnologies(currentYear, audit);
        LogTrackedTechProbe(currentYear, players, dueData);

        ReportPlayerDiscoveries(context, completedForMainPlayer);

        if (completedByPlayer.Count == 0)
            return;

        string samples = string.Join(
            ", ",
            completedByPlayer
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Take(8)
                .Select(static pair => $"{pair.Key} +{pair.Value}"));
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP historical research samples: {samples}.");
    }

    private static void ClampHistoricalResearchSpending(string context, CampaignController? campaign)
    {
        if (campaign?.CampaignData?.PlayersMajor == null)
            return;

        int changedBudgets = 0;
        int syncedPlayers = 0;
        foreach (Player player in MajorPlayers(campaign))
        {
            ZeroResearchSpending(player, ref changedBudgets);
            syncedPlayers++;
        }

        if (changedBudgets <= 0)
            return;

        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP historical research: clamped research spending for {syncedPlayers} nations, changedBudgets={changedBudgets}, context={context}.");
    }

    private static HistoricalSyncResult CompleteDueTechnologies(Player player, IReadOnlyList<TechnologyData> dueData, List<Technology>? completedTechnologies)
    {
        if (player.technologies == null)
            return default;

        PlayerTechnologyIndex index = BuildPlayerTechnologyIndex(player);
        int completedExisting = 0;
        int addedMissingDue = 0;
        foreach (TechnologyData data in dueData)
        {
            Technology? tech = index.Find(data);
            if (tech != null)
            {
                if (IsTechnologyComplete(tech))
                    continue;

                tech.progress = ResearchCompleteProgress;
                completedTechnologies?.Add(tech);
                completedExisting++;
                continue;
            }

            tech = new Technology
            {
                data = data,
                progress = ResearchCompleteProgress,
                Index = 0,
            };
            player.technologies.Add(tech);
            index.Add(tech);
            completedTechnologies?.Add(tech);
            addedMissingDue++;
        }

        if (completedExisting > 0 || addedMissingDue > 0)
        {
            try { player.OnTechnologiesChanged(); }
            catch (Exception ex)
            {
                WarnOnce(
                    $"tech-changed:{PlayerKey(player)}:{ex.GetType().Name}",
                    $"UADVP historical research: OnTechnologiesChanged failed for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new HistoricalSyncResult(completedExisting, addedMissingDue);
    }

    private static List<TechnologyData> DueHistoricalTechData(int currentYear)
    {
        List<TechnologyData> due = new();
        try
        {
            var technologies = G.GameData?.technologies;
            if (technologies == null)
                return due;

            foreach (var entry in technologies)
            {
                TechnologyData? data = entry.Value;
                if (data == null || SafeIsEndTechnology(data))
                    continue;

                int techYear = HistoricalTechYear(data);
                if (techYear > currentYear)
                    continue;

                due.Add(data);
            }
        }
        catch (Exception ex)
        {
            WarnOnce(
                "due-data:" + ex.GetType().Name,
                $"UADVP historical research: failed to enumerate global technologies for historical sync. {ex.GetType().Name}: {ex.Message}");
        }

        return due
            .OrderBy(HistoricalTechYear)
            .ThenBy(TechDataKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HistoricalResearchAudit AuditHistoricalResearch(IReadOnlyList<Player> players, IReadOnlyList<TechnologyData> dueData)
    {
        List<PlayerMissingDueAudit> missingPlayers = new();
        int missingDueAfter = 0;

        foreach (Player player in players)
        {
            PlayerTechnologyIndex index = BuildPlayerTechnologyIndex(player);
            List<string> samples = new();
            int playerMissing = 0;

            foreach (TechnologyData data in dueData)
            {
                Technology? tech = index.Find(data);
                if (tech != null && IsTechnologyComplete(tech))
                    continue;

                playerMissing++;
                missingDueAfter++;
                if (samples.Count < 8)
                    samples.Add(TechDataKey(data));
            }

            if (playerMissing > 0)
                missingPlayers.Add(new PlayerMissingDueAudit(PlayerLabel(player), playerMissing, samples));
        }

        return new HistoricalResearchAudit(dueData.Count, missingDueAfter, missingPlayers);
    }

    private static void WarnForMissingDueTechnologies(int currentYear, HistoricalResearchAudit audit)
    {
        if (audit.MissingDueAfter <= 0)
            return;

        foreach (PlayerMissingDueAudit player in audit.MissingPlayers.Take(8))
        {
            string samples = string.Join(",", player.Samples.Select(LogToken));
            WarnOnce(
                $"audit-missing:{currentYear}:{player.PlayerLabel}:{samples}",
                $"UADVP historical research audit WARNING: year={currentYear} {player.PlayerLabel} missingDueAfter={player.MissingCount} samples={samples}.");
        }
    }

    private static void LogTrackedTechProbe(int currentYear, IReadOnlyList<Player> players, IReadOnlyList<TechnologyData> dueData)
    {
        Dictionary<string, TechnologyData> dueTracked = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> dueNames = dueData
            .Select(TechDataKey)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string tracked in TrackedHistoricalProbeTechs)
        {
            if (!dueNames.Contains(tracked))
                continue;

            TechnologyData? data = dueData.FirstOrDefault(candidate => string.Equals(TechDataKey(candidate), tracked, StringComparison.OrdinalIgnoreCase));
            if (data != null)
                dueTracked[tracked] = data;
        }

        if (dueTracked.Count == 0)
            return;

        List<string> missingByPlayer = new();
        foreach (Player player in players)
        {
            PlayerTechnologyIndex index = BuildPlayerTechnologyIndex(player);
            List<string> missing = new();
            foreach (var pair in dueTracked)
            {
                Technology? tech = index.Find(pair.Value);
                if (tech == null || !IsTechnologyComplete(tech))
                    missing.Add(pair.Key + "=missing");
            }

            if (missing.Count > 0)
                missingByPlayer.Add($"{PlayerLabel(player)} {string.Join(" ", missing)}");
        }

        if (missingByPlayer.Count == 0)
        {
            if (LoggedTrackedProbeVerifiedYears.Add(currentYear.ToString()))
            {
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP historical research probe: year={currentYear} tracked due techs verified for {players.Count} nations.");
            }

            return;
        }

        WarnOnce(
            $"tracked-probe:{currentYear}:{string.Join("|", missingByPlayer.Take(4))}",
            $"UADVP historical research probe WARNING: year={currentYear} {string.Join("; ", missingByPlayer.Take(8))}.");
    }

    private static PlayerTechnologyIndex BuildPlayerTechnologyIndex(Player player)
    {
        PlayerTechnologyIndex index = new();
        foreach (Technology tech in Technologies(player))
            index.Add(tech);

        return index;
    }

    private static bool IsTechnologyComplete(Technology tech)
    {
        try
        {
            return tech.progress >= ResearchCompleteProgress || tech.isResearched;
        }
        catch
        {
            return Safe(() => tech.progress, 0f) >= ResearchCompleteProgress;
        }
    }

    private static bool SafeIsEndTechnology(TechnologyData data)
    {
        try { return data.isEnd; }
        catch { return false; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string TechDataKey(TechnologyData? data)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(data?.name))
                return data.name;
        }
        catch
        {
        }

        return data?.GetHashCode().ToString() ?? string.Empty;
    }

    private static long TechDataPointer(TechnologyData? data)
    {
        try { return data?.Pointer.ToInt64() ?? 0L; }
        catch { return 0L; }
    }

    private static void ReportPlayerDiscoveries(string context, List<Technology> completedTechnologies)
    {
        if (completedTechnologies.Count == 0)
            return;

        Player? mainPlayer = PlayerController.Instance;
        Ui? ui = G.ui;
        if (mainPlayer == null || ui == null)
        {
            WarnOnce(
                $"report-discovery-unavailable:{context}",
                $"UADVP historical research: could not report {completedTechnologies.Count} player discoveries because campaign UI is unavailable, context={context}.");
            return;
        }

        int reported = 0;
        int failed = 0;
        foreach (Technology tech in completedTechnologies)
        {
            try
            {
                ui.ReportDiscovery(mainPlayer, tech);
                reported++;
            }
            catch (Exception ex)
            {
                failed++;
                WarnOnce(
                    $"report-discovery:{TechKey(tech)}:{ex.GetType().Name}",
                    $"UADVP historical research: ReportDiscovery failed for {TechnologyName(tech)}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        string failedText = failed > 0 ? $", failed={failed}" : string.Empty;
        Melon<UADVanillaPlusMod>.Logger.Msg(
            $"UADVP historical research: reported {reported}/{completedTechnologies.Count} player discoveries, context={context}{failedText}.");
    }

    private static int HistoricalTechYear(TechnologyData data)
    {
        try
        {
            return GameManager.GetTechYear(data, true, true);
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"tech-year:{data.Pointer}",
                $"UADVP historical research: failed to read historical year for {TechLabel(data)}. {ex.GetType().Name}: {ex.Message}");
            return int.MaxValue;
        }
    }

    private static void RefreshVanillaResearchState(CampaignController campaign)
    {
        try
        {
            TechStartNewResearchsMethod?.Invoke(campaign, Array.Empty<object>());
        }
        catch (Exception ex)
        {
            WarnOnce(
                "tech-start-new-researchs",
                $"UADVP historical research: TechStartNewResearchs invoke failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ZeroResearchSpending(Player player, ref int budgetChanges)
    {
        try
        {
            if (Math.Abs(player.techBudget) > 0.0001f)
                budgetChanges++;

            player.techBudget = 0f;
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"tech-budget:{PlayerKey(player)}",
                $"UADVP historical research: failed to zero tech budget for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            player.techPriorities?.Clear();
        }
        catch (Exception ex)
        {
            WarnOnce(
                $"tech-priorities:{PlayerKey(player)}",
                $"UADVP historical research: failed to clear tech priorities for {PlayerLabel(player)}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<Player> MajorPlayers(CampaignController campaign)
    {
        List<Player> players = new();

        foreach (Player player in campaign.CampaignData.PlayersMajor)
        {
            if (player?.technologies != null)
                players.Add(player);
        }

        return players;
    }

    private static List<Technology> Technologies(Player player)
    {
        List<Technology> technologies = new();

        foreach (Technology tech in player.technologies)
        {
            if (tech != null)
                technologies.Add(tech);
        }

        return technologies;
    }

    private static int CampaignYear(CampaignController campaign)
    {
        try
        {
            return campaign.CurrentDate.AsDate().Year;
        }
        catch
        {
            return -1;
        }
    }

    private static string PlayerKey(Player? player)
    {
        if (!string.IsNullOrWhiteSpace(player?.data?.name))
            return player.data.name;

        return player?.GetHashCode().ToString() ?? "unknown";
    }

    private static bool IsMainPlayer(Player? player)
    {
        try
        {
            return player?.isMain == true && player.isAi == false;
        }
        catch
        {
            return false;
        }
    }

    private static string PlayerLabel(Player? player)
    {
        try
        {
            string? name = player?.Name(false);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(player?.data?.nameUi))
            return player.data.nameUi;

        return player?.data?.name ?? "unknown nation";
    }

    private static string TechLabel(TechnologyData data)
        => !string.IsNullOrWhiteSpace(data.name) ? data.name : data.GetHashCode().ToString();

    private static string TechKey(Technology? tech)
    {
        try
        {
            TechnologyData? data = tech?.data;
            if (data != null && !string.IsNullOrWhiteSpace(data.name))
                return data.name;
        }
        catch
        {
        }

        return tech?.GetHashCode().ToString() ?? "unknown";
    }

    private static string TechnologyName(Technology? tech)
    {
        try
        {
            string? name = tech?.data?.GetName();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(tech?.data?.name))
                return tech.data.name;
        }
        catch
        {
        }

        return "unknown technology";
    }

    private static string LogToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        return value
            .Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace(";", ",")
            .Replace("[", "(")
            .Replace("]", ")")
            .Replace(" ", "_");
    }

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
            Melon<UADVanillaPlusMod>.Logger.Warning(message);
    }

    private sealed class PlayerTechnologyIndex
    {
        private readonly Dictionary<long, Technology> byPointer = new();
        private readonly Dictionary<string, Technology> byName = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(Technology? tech)
        {
            TechnologyData? data = null;
            try { data = tech?.data; }
            catch { }

            if (tech == null || data == null)
                return;

            long pointer = TechDataPointer(data);
            if (pointer != 0L && !byPointer.ContainsKey(pointer))
                byPointer[pointer] = tech;

            string name = TechDataKey(data);
            if (!string.IsNullOrWhiteSpace(name) && !byName.ContainsKey(name))
                byName[name] = tech;
        }

        internal Technology? Find(TechnologyData? data)
        {
            if (data == null)
                return null;

            long pointer = TechDataPointer(data);
            if (pointer != 0L && byPointer.TryGetValue(pointer, out Technology? byPointerTech))
                return byPointerTech;

            string name = TechDataKey(data);
            return !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name, out Technology? byNameTech)
                ? byNameTech
                : null;
        }
    }

    private readonly record struct HistoricalSyncResult(int CompletedExisting, int AddedMissingDue)
    {
        internal int TotalChanged => CompletedExisting + AddedMissingDue;
    }

    private readonly record struct HistoricalResearchAudit(
        int DueDataCount,
        int MissingDueAfter,
        IReadOnlyList<PlayerMissingDueAudit> MissingPlayers);

    private readonly record struct PlayerMissingDueAudit(
        string PlayerLabel,
        int MissingCount,
        IReadOnlyList<string> Samples);
}
