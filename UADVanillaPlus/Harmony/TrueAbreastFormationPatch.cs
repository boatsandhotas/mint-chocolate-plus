// TrueAbreastFormationPatch removed: overriding Division.CalcFollowing did NOT reshape the formation.
// Confirmed in-game (0.5.176): the postfix ran and the managed-read station went clean abreast, but
// ships didn't move there — the native follow-steer calls CalcFollowing natively, bypassing the
// HarmonyX-patched managed trampoline. In IL2CPP a patch only affects MANAGED callers; the engine's
// own internal calls are not intercepted. The only lever that actually moves ships is Division.MoveTo,
// so "abreast" is now an offset preset of the station-keeping Parallel order (see ParallelOrderPatch).
// Empty file kept to avoid a stale reference; safe to delete.
