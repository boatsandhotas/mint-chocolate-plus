using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace UADVanillaPlus.GameData;

// Keeps a division's followers at the leader's manual speed. When the player slows a division
// leader (sets its engineCustomSpeed), the followers otherwise keep running at full speed and
// then loop / do 360s to fall back into line. While a player division's leader has a manual speed
// order, force every follower to that speed (clamped to its own max). When the leader has no
// manual order, clear the followers' forced order so they resume normal formation speed.
// Player-controlled divisions only; driven from the Ui.Update postfix, throttled.
internal static class BattleSpeedSync
{
    private static float lastTick;

    internal static void Tick()
    {
        if (!ModSettings.BattleSpeedSyncEnabled)
            return;
        try
        {
            if (!GameManager.IsBattle)
                return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - lastTick < 0.2f)
                return;
            lastTick = now;

            var divisions = DivisionsManager.Instance?.MainPlayerDivisions;
            if (divisions == null)
                return;

            foreach (Division d in divisions)
            {
                if (d == null)
                    continue;
                Ship? leader = SafeLeader(d);
                if (leader == null || SafeAi(leader))
                    continue;

                float target = SafeF(() => leader.engineCustomSpeed); // < 0 = no manual order
                var ships = d.ships;
                if (ships == null)
                    continue;

                foreach (Ship s in ships)
                {
                    if (s == null || SafeAi(s) || s.Pointer == leader.Pointer)
                        continue;

                    float cur = SafeF(() => s.engineCustomSpeed);
                    if (target >= 0f)
                    {
                        float max = SafeMax(s);
                        float want = max > 0f ? Mathf.Min(target, max) : target;
                        if (Mathf.Abs(cur - want) > 0.05f)
                        {
                            try { s.SetEngineCustomSpeed(want); } catch { }
                        }
                    }
                    else if (cur >= 0f)
                    {
                        // Leader released its manual speed — release the followers too.
                        try { s.engineCustomSpeed = -1f; } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP speed-sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Ship? SafeLeader(Division d) { try { return d.leader; } catch { return null; } }
    private static bool SafeAi(Ship s) { try { return s.isAiControlled; } catch { return false; } }
    private static float SafeMax(Ship s) { try { return s.SpeedMax(); } catch { return 0f; } }
    private static float SafeF(Func<float> f) { try { return f(); } catch { return -1f; } }
}
