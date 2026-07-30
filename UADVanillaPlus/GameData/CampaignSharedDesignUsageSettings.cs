using Il2Cpp;
using MelonLoader;

namespace UADVanillaPlus.GameData;

// This is campaign save-state, not a VP PlayerPrefs option. The active
// controller value is written into CampaignController.Store on the next save.
internal static class CampaignSharedDesignUsageSettings
{
    internal enum SharedDesignPolicy
    {
        Off = 0,
        Selective = 1,
        Always = 2,
        Only = 3,
    }

    internal static bool HasActiveCampaign
        => CampaignController.Instance?.CampaignData != null;

    internal static CampaignController.SharedDesignUsage CurrentMode
        => CampaignController.Instance?.SharedDesignsUsage ?? CampaignController.SharedDesignUsage.Off;

    internal static SharedDesignPolicy CurrentPolicy
        => !HasActiveCampaign
            ? SharedDesignPolicy.Off
            : CurrentMode == CampaignController.SharedDesignUsage.Always && ModSettings.SharedDesignsOnlyMode
                ? SharedDesignPolicy.Only
                : PolicyFromVanillaMode(CurrentMode);

    internal static bool IsOnlyModeActive
        => HasActiveCampaign &&
           CurrentMode == CampaignController.SharedDesignUsage.Always &&
           ModSettings.SharedDesignsOnlyMode;

    internal static string CurrentModeText()
        => HasActiveCampaign ? ModeText(CurrentPolicy) : "No Campaign";

    internal static bool TrySetMode(CampaignController.SharedDesignUsage mode)
        => TrySetMode(PolicyFromVanillaMode(mode));

    internal static bool TrySetMode(SharedDesignPolicy policy)
    {
        CampaignController? campaign = CampaignController.Instance;
        if (campaign?.CampaignData == null)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning("UADVP option: Shared Designs cannot be changed without a loaded campaign.");
            return false;
        }

        SharedDesignPolicy oldPolicy = CurrentPolicy;
        CampaignController.SharedDesignUsage oldMode = campaign.SharedDesignsUsage;
        CampaignController.SharedDesignUsage vanillaMode = VanillaModeForPolicy(policy);
        bool onlyMode = policy == SharedDesignPolicy.Only;
        if (oldPolicy == policy && oldMode == vanillaMode && ModSettings.SharedDesignsOnlyMode == onlyMode)
            return false;

        campaign.SharedDesignsUsage = vanillaMode;
        ModSettings.SharedDesignsOnlyMode = onlyMode;
        Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP option: Shared Designs mode {ModeText(oldPolicy)} -> {ModeText(policy)}.");
        ModSettings.LogCurrentSettings("after Shared Designs change");
        return true;
    }

    internal static string ModeText(CampaignController.SharedDesignUsage mode)
        => ModeText(PolicyFromVanillaMode(mode));

    internal static string ModeText(SharedDesignPolicy policy)
        => policy switch
        {
            SharedDesignPolicy.Selective => "Selective",
            SharedDesignPolicy.Always => "Always",
            SharedDesignPolicy.Only => "Only",
            _ => "Off",
        };

    private static SharedDesignPolicy PolicyFromVanillaMode(CampaignController.SharedDesignUsage mode)
        => mode switch
        {
            CampaignController.SharedDesignUsage.Selective => SharedDesignPolicy.Selective,
            CampaignController.SharedDesignUsage.Always => SharedDesignPolicy.Always,
            _ => SharedDesignPolicy.Off,
        };

    private static CampaignController.SharedDesignUsage VanillaModeForPolicy(SharedDesignPolicy policy)
        => policy switch
        {
            SharedDesignPolicy.Selective => CampaignController.SharedDesignUsage.Selective,
            SharedDesignPolicy.Always => CampaignController.SharedDesignUsage.Always,
            SharedDesignPolicy.Only => CampaignController.SharedDesignUsage.Always,
            _ => CampaignController.SharedDesignUsage.Off,
        };
}
