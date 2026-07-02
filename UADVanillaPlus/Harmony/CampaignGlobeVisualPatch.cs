using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UADVanillaPlus.GameData;
using UnityEngine;

namespace UADVanillaPlus.Harmony;

// EXPERIMENTAL "Globe" campaign-map mode: a true 3D POLITICAL globe built from the game's own data —
// a blue-ocean sphere with each country's flat fill mesh RE-PROJECTED onto the sphere by latitude/
// longitude (no external assets, no reuse of the flat map texture). The sim stays a flat XZ plane; this
// is a visual skin. Markers (positions OUT) and click translation (IN) are later milestones.
//
// Coordinate model: every flat-map world position -> (lat,lon) via the game's UnityCoordsToLatitudeLongitude
// -> radians (×k, k = converter-units→radians) -> unit sphere -> ×radius. The ocean sphere is built in
// radians directly, so land and ocean align regardless of whether the converter returns degrees or radians.
//
// Camera: a CheckCameraBorders prefix skips the native flat clamp, and a Cam.Update POSTFIX drives the
// orbit AFTER native Update (M1 stayed flat because a prefix write was stomped by native Update).
//
// Gated behind ModSettings.MapGeometry == Globe. Mirrors CampaignMapWrapVisualPatch lifecycle/helpers.
[HarmonyPatch(typeof(CampaignMap))]
internal static class CampaignGlobeVisualPatch
{
    private const int OceanLatSteps = 48;
    private const int OceanLonSteps = 96;
    private const float LandLiftFactor = 1.001f;   // land above the OCEAN = 0.1% (was 0.3%). Border sits ON the fill via renderQueue. WATCH: 0.15% z-fought before -> if colors flicker/vanish, raise this.
    // Real-world latitude band the map's land spans (Greenland/Siberia in the north to Cape Horn in the
    // south). Used instead of the game converter's latitude, which is unreliable (it reported the whole
    // map as 88.9..-9.1, cramming everything into the northern hemisphere).
    private const float LandLatNorthDeg = 78f;
    private const float LandLatSouthDeg = -56f;
    private static readonly Color OceanColor = new(0.10f, 0.22f, 0.42f, 1f);
    private static readonly Color FallbackLand = new(0.55f, 0.52f, 0.42f, 1f);
    private static readonly Color BorderColor = new(0.05f, 0.05f, 0.07f, 1f);
    private static readonly bool DrawGeneratedBorders = true; // on-sphere territory borders (the flat ones are now killed)

    internal static GameObject? GlobeRoot;
    private static readonly List<Renderer> hiddenRenderers = new(); // every flat-map renderer we disabled
    private static readonly List<GameObject> hiddenCameraObjects = new(); // outline/RT camera GameObjects we disabled
    private static bool savedDrawAllBorders = true;   // CampaignBordersManager.DrawAllBorders prior value
    private static bool drawAllBordersWasSet;          // did we override DrawAllBorders?
    private static Material? bordersMat;               // CampaignBordersManager.BordersMaterial (alpha zeroed)
    private static Color savedBordersColor;            // its prior color
    private static readonly Dictionary<IntPtr, float> markerScale = new(); // last native scale per marker
    private static readonly Dictionary<IntPtr, Vector3[]> circleFlat = new(); // cached original flat circle points
    private static float provincesCanvasAlpha = 1f;                        // border-line CanvasGroup prior alpha
    private static Transform? labelAreaRoot, labelProvincesRoot;           // territory name label roots
    private static Transform? mineSweepRoot, denialZoneRoot, eventsRoot;   // gameplay circle roots (kept + reprojected)
    private static Vector3 mouseDownScreen;                                // for drag-vs-click discrimination
    private static bool dragOccurred;                                      // a drag happened this press
    private static bool dragOverScroll;                                    // this press started over a scroll popup
    private static readonly List<Vector3> borderVerts = new(); // accumulated territory-border line vertices
    private static readonly List<int> borderIdx = new();        // line index pairs into borderVerts
    private static int landRebuildAtFrame = -1; // deferred land re-clone to catch late fill coloring (reload)
    private static int markerProbeFrame = -1;   // delayed marker-structure probe (markers populate after build)
    private static int landRebuildsLeft;        // remaining retry rebuilds (coloring can arrive several seconds late)
    private static readonly Dictionary<int, Vector3> labelFlat = new(); // original flat pos per name label
    private static readonly List<(MeshRenderer src, MeshRenderer clone)> fillSources = new(); // for refreshing fill colors on ownership change
    private static int fillColorFrame;           // next fill-color refresh frame
    private static GameObject? zoneRingObj;      // our drawn invasion/denial/minesweep ring line mesh
    private static int lastZoneCount = -1;       // active zone count at last ring build
    private static int zoneRingFrame;            // next periodic ring rebuild frame
    private static bool zoneColorLogged;         // one-shot zone-ring color diagnostic
    private static readonly Color ZoneRingColor = new(1f, 0.30f, 0.25f, 1f); // fallback ring tint if a zone has no color
    private static Transform? provinceBattlesRoot; // land-battle/invasion container (under WorldEx)
    private static GameObject? battleArrowObj;    // our drawn battle-arrow line mesh
    private static int lastBattleCount = -1;
    private static int battleFrame;
    private static int battleDiagFrame; // throttle for the battle-flags/arrows state diagnostic
    private static int routeLogFrame;   // throttle for the route-reprojection diagnostic
    private static int flatRouteFrame;  // throttle for the catch-all flat-route reprojection pass
    private static readonly Color BattleArrowColor = new(0.88f, 0.38f, 0.32f, 1f); // game invasion-arrow salmon-red
    private static readonly Dictionary<int, Vector3[]> battleLineFlat = new(); // cached flat verts per battle line (persistent: avoids re-caching sphere pos as flat)
    private static readonly Dictionary<int, Vector3> battleMarkerFlat = new(); // cached flat pos per battle flag/end
    private static CampaignProvinceBattlePopupUI? battlePopup; // the native land-battle tooltip we drive on globe hover
    private static string battleHoverShown = "";  // id (flag-name suffix) currently shown, "" = none
    private static string lastHoverLogged = "\x01"; // last hover-id we logged (log on change, to diagnose "same battle")
    private static int markerScaleLog;          // limits the native-scale diagnostic
    private static int lastEnsureFrame = -100;   // throttle the self-heal rebuild
    private static bool circleProbed;            // one-shot circle-root diagnostic
    private const float MaxMarkerScale = 1.5f;   // cap so markers don't balloon when zoomed out
    private static bool flatHidden;
    private static float radius = 1000f;

    // Calibration: the native UnityCoordsToLatitudeLongitude is called ONLY for the 4 map corners (per-
    // vertex calls flooded the log — that native method LogErrors every call — and crashed the game).
    // Every vertex's lat/lon is then bilinearly interpolated from these corners. cll** = corner lat/lon
    // (.x=lat,.y=lon) at (minX,minZ)/(maxX,minZ)/(minX,maxZ)/(maxX,maxZ); calK = units->radians.
    private static Vector3 cll00, cll10, cll01, cll11;
    private static float calMinX, calMaxX, calMinZ, calMaxZ, calK = 1f, calCenterY;

    // Orbit camera state.
    private static bool cameraSeized;
    private static int driveLogCount;
    private static bool camOrthoOrig;
    private static float camFovOrig, camNearOrig, camFarOrig;
    private static float yaw, pitch = 12f, dist;
    private static int lastInputFrame = -1;       // input accumulates once/frame (driven from 2 hooks)
    private static Vector3 lastCamPos, lastCenter; // stashed for marker back-face cull (M3)

    [HarmonyPatch(nameof(CampaignMap.PostInit))]
    [HarmonyPostfix]
    private static void PostfixPostInit(CampaignMap __instance)
    {
        if (ModSettings.CampaignGlobeEnabled)
            BuildGlobe(__instance);
    }

    [HarmonyPatch(nameof(CampaignMap.OnDestroy))]
    [HarmonyPostfix]
    private static void PostfixOnDestroy() => DestroyGlobe();

    internal static void ApplyCurrentSetting()
    {
        CampaignMap? map = CampaignMap.Instance;
        if (map == null || map.MapRenderer == null)
            return;
        if (ModSettings.CampaignGlobeEnabled)
            BuildGlobe(map);
        else
        {
            DestroyGlobe();
            RestoreFlat();
        }
    }

    // Self-heal: if globe mode is on and we're on the world map but the globe is MISSING (e.g., it was
    // destroyed across a 3D-battle transition without a fresh PostInit), rebuild it. Otherwise the flat-map
    // renderers stay disabled with no globe over them and the world looks textureless.
    internal static void EnsureGlobe()
    {
        try
        {
            if (!ModSettings.CampaignGlobeEnabled || !GameManager.IsWorldMap)
                return;
            if (GlobeRoot != null) // Unity-null: a destroyed globe reads as null here
                return;
            if (Time.frameCount - lastEnsureFrame < 30)
                return; // throttle retries so a failing build can't spam
            lastEnsureFrame = Time.frameCount;
            CampaignMap map = CampaignMap.Instance;
            if (map == null || map.MapRenderer == null)
                return;
            if (flatHidden)
                RestoreFlat(); // clear any stale hidden-renderer state before rebuilding
            BuildGlobe(map);
        }
        catch { }
    }

    private static void BuildGlobe(CampaignMap map)
    {
        try
        {
            if (GlobeRoot != null)
                return;
            Renderer mapRenderer = map.MapRenderer;
            if (mapRenderer == null)
                return;

            Bounds bounds = mapRenderer.bounds;
            Vector3 center = bounds.center;
            radius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.2f;
            if (radius <= 1f) radius = 1000f;
            // Calibrate from the TRUE land extent (union of country-fill bounds). MapRenderer.bounds is
            // smaller than the map and gave a north-only lat range (88.9..-9.1) -> southern squish.
            Bounds calBounds = ComputeLandBounds(map, out Bounds lb) ? lb : bounds;
            Calibrate(map, calBounds);

            GlobeRoot = new GameObject("UADVP_Globe");
            GlobeRoot.layer = mapRenderer.gameObject.layer;
            GlobeRoot.transform.position = center;

            BuildOcean();
            int landCountries = BuildLand(map);

            HideFlat(map);
            ProbeMarkers(); // marker structure at build (templates only)
            markerProbeFrame = Time.frameCount + 200; // re-probe once markers (invasion circles/arrows) populate
            ReprojectExistingRoutes(map);
            dist = radius * 2.6f;
            cameraSeized = false; // re-seize + frame on next Update
            driveLogCount = 0;
            landRebuildAtFrame = Time.frameCount + 120; // re-clone land to catch colors that arrive late
            landRebuildsLeft = 1;                        // one re-clone for late coloring (more = heavy re-tessellation)
            // NOTE: do NOT clear labelFlat here — it caches each label's ORIGINAL flat position. Labels persist
            // across map in/outs (we leave them on the sphere), so clearing made re-entry cache the sphere pos
            // as "flat" and reproject it again -> labels clumped/compounded. Persistent cache = recorded once.
            zoneRingObj = null; lastZoneCount = -1; zoneColorLogged = false; // ring mesh is a GlobeRoot child (destroyed with it); force rebuild
            battleArrowObj = null; lastBattleCount = -1; provinceBattlesRoot = null; // re-find + rebuild battle arrows
            battleHoverShown = ""; lastHoverLogged = "\x01"; // clear globe battle-hover state + re-arm the diagnostic
            fillColorFrame = Time.frameCount + 120;
            circleProbed = false;
            bordersDumped = false;

            Melon<UADVanillaPlusMod>.Logger.Msg(
                $"UADVP_GLOBE political globe built: radius={radius:0} center={center} k={calK:0.####} latC=[{cll00.x:0.#},{cll11.x:0.#}] lonC=[{cll00.z:0.#},{cll11.z:0.#}] land-countries={landCountries}.");
            try
            {
                var s = map.Settings;
                if (s != null)
                    Melon<UADVanillaPlusMod>.Logger.Msg(
                        $"UADVP_GLOBE map settings: lonL={s.MapLongitudeLeft:0.##} lonR={s.MapLongitudeRight:0.##} latBottom={s.MapLongitudeBottom:0.##} W={s.MapWidth:0} H={s.MapHeight:0} offset={s.MapOffset} landZ=[{calMinZ:0},{calMaxZ:0}].");
            }
            catch (Exception sx) { Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE settings read failed: {sx.GetType().Name}"); }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE build failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Sample lat/lon at the 4 map corners (the ONLY native converter calls) and derive units->radians.
    private static void Calibrate(CampaignMap map, Bounds b)
    {
        calMinX = b.min.x; calMaxX = b.max.x; calMinZ = b.min.z; calMaxZ = b.max.z; calCenterY = b.center.y;
        cll00 = SafeLL(map, new Vector3(calMinX, b.center.y, calMinZ));
        cll10 = SafeLL(map, new Vector3(calMaxX, b.center.y, calMinZ));
        cll01 = SafeLL(map, new Vector3(calMinX, b.center.y, calMaxZ));
        cll11 = SafeLL(map, new Vector3(calMaxX, b.center.y, calMaxZ));
        float maxAbsLat = Mathf.Max(Mathf.Max(Mathf.Abs(cll00.x), Mathf.Abs(cll10.x)), Mathf.Max(Mathf.Abs(cll01.x), Mathf.Abs(cll11.x)));
        calK = maxAbsLat > 3.2f ? Mathf.Deg2Rad : 1f;
    }

    // Mercator Y for a latitude (radians), clamped shy of the poles to avoid Inf.
    private static float MercY(float latRad)
    {
        latRad = Mathf.Clamp(latRad, -1.48353f, 1.48353f); // +-85 degrees
        return Mathf.Log(Mathf.Tan(Mathf.PI * 0.25f + latRad * 0.5f));
    }

    private static Vector3 SafeLL(CampaignMap map, Vector3 w)
    {
        try { return map.UnityCoordsToLatitudeLongitude(w); } catch { return Vector3.zero; }
    }

    // True world AABB of all country fills (the rendered land) — the real geographic extent.
    private static bool ComputeLandBounds(CampaignMap map, out Bounds bounds)
    {
        bounds = new Bounds();
        bool any = false;
        try
        {
            CampaignBordersManager? bm = map.bordersManager;
            if (bm == null || bm.Countries == null)
                return false;
            foreach (CampaignBordersManager.Country country in bm.Countries)
            {
                if (country == null || country.MeshObjects == null)
                    continue;
                for (int i = 0; i < country.MeshObjects.Count; i++)
                {
                    MeshRenderer? r = country.MeshObjects[i];
                    if (r == null) continue;
                    Bounds rb;
                    try { rb = r.bounds; } catch { continue; }
                    if (!any) { bounds = rb; any = true; } else bounds.Encapsulate(rb);
                }
            }
        }
        catch { }
        return any;
    }

    private static float Bilerp(float v00, float v10, float v01, float v11, float fx, float fz)
        => Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fz);

    // X negated: the converter's longitude runs east-NEGATIVE (corners 180 on the west edge -> -180 on the
    // east edge), which mirrored the globe east-west; flipping X un-mirrors it.
    private static Vector3 LatLonToUnit(float latRad, float lonRad)
        => new(-Mathf.Cos(latRad) * Mathf.Sin(lonRad), Mathf.Sin(latRad), Mathf.Cos(latRad) * Mathf.Cos(lonRad));

    // Flat-map world position -> sphere position in GlobeRoot-local space, via the calibrated corners
    // (NO per-vertex native call — bilinear interpolation of the 4 corner lat/lons).
    private static Vector3 WorldToSphereLocal(Vector3 world, float r)
    {
        float fx = calMaxX > calMinX ? Mathf.Clamp01((world.x - calMinX) / (calMaxX - calMinX)) : 0.5f;
        float fz = calMaxZ > calMinZ ? Mathf.Clamp01((world.z - calMinZ) / (calMaxZ - calMinZ)) : 0.5f;
        // Latitude: the converter's latitude is unreliable, so map the land's Z extent onto a real-world
        // Mercator band (fz=0 = north edge / min Z, fz=1 = south edge). Interpolating in Mercator-Y then
        // inverting keeps Mercator proportions. Longitude still comes from the converter (its lon is the
        // correct full -180..180 range; .z holds lon).
        float gN = MercY(LandLatNorthDeg * Mathf.Deg2Rad);
        float gS = MercY(LandLatSouthDeg * Mathf.Deg2Rad);
        float latRad = 2f * Mathf.Atan(Mathf.Exp(Mathf.Lerp(gN, gS, fz))) - Mathf.PI * 0.5f;
        float lonRad = Bilerp(cll00.z, cll10.z, cll01.z, cll11.z, fx, fz) * calK;
        return LatLonToUnit(latRad, lonRad) * r;
    }

    private static void BuildOcean()
    {
        int rows = OceanLatSteps + 1, cols = OceanLonSteps + 1;
        var verts = new Vector3[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            float latRad = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, r / (float)OceanLatSteps);
            for (int c = 0; c < cols; c++)
            {
                float lonRad = Mathf.Lerp(-Mathf.PI, Mathf.PI, c / (float)OceanLonSteps);
                verts[r * cols + c] = LatLonToUnit(latRad, lonRad) * radius;
            }
        }
        var tris = new int[OceanLatSteps * OceanLonSteps * 6];
        int t = 0;
        for (int r = 0; r < OceanLatSteps; r++)
            for (int c = 0; c < OceanLonSteps; c++)
            {
                int a = r * cols + c, bb = a + 1, d = a + cols, e = d + 1;
                // Wound to face OUTWARD (LatLonToUnit's X-negation flips handedness; a naive winding ends up
                // inside-out, which the textured overlay reveals as the far inner surface showing through).
                tris[t++] = a; tris[t++] = bb; tris[t++] = d;
                tris[t++] = bb; tris[t++] = e; tris[t++] = d;
            }

        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Use the game's NATIVE map TEXTURE on the globe (richer than flat blue). Map each sphere vertex back
        // to its flat-map position (same projection as the land fills, UNclamped) and into the map texture's UV
        // space, so the terrain lines up under the political land fills. Falls back to flat blue if unavailable.
        Texture? mapTex = null; Material? mapMat = null; Bounds mb = default; bool haveTex = false;
        try
        {
            CampaignMap cm = CampaignMap.Instance;
            Renderer? mr = cm != null ? cm.MapRenderer : null;
            if (mr != null) { mapMat = mr.sharedMaterial; mapTex = mapMat != null ? mapMat.mainTexture : null; mb = mr.bounds; haveTex = mapTex != null && mb.size.x > 1e-3f && mb.size.z > 1e-3f; }
        }
        catch { }
        if (haveTex)
        {
            float gN = MercY(LandLatNorthDeg * Mathf.Deg2Rad), gS = MercY(LandLatSouthDeg * Mathf.Deg2Rad);
            var uv = new Vector2[verts.Length];
            var vcols = new Color[verts.Length]; // per-vertex alpha: fade terrain out where UVs leave the map (seam/poles) so ocean shows instead of streaks
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 nn = verts[i].normalized;
                float lat = Mathf.Asin(Mathf.Clamp(nn.y, -1f, 1f));
                float lon = Mathf.Atan2(-nn.x, nn.z);
                float fz = Mathf.Abs(gS - gN) < 1e-6f ? 0.5f : (MercY(lat) - gN) / (gS - gN);
                float lonU = calK != 0f ? lon / calK : lon;
                float P = cll00.z * (1f - fz) + cll01.z * fz;
                float Q = (cll10.z - cll00.z) * (1f - fz) + (cll11.z - cll01.z) * fz;
                float fx = Mathf.Abs(Q) < 1e-5f ? 0.5f : (lonU - P) / Q;
                float wx = Mathf.Lerp(calMinX, calMaxX, fx);
                float wz = Mathf.Lerp(calMinZ, calMaxZ, fz);
                float rawU = 1f - (wx - mb.min.x) / mb.size.x; // U flipped (terrain was mirrored E-W)
                float rawV = 1f - (wz - mb.min.z) / mb.size.z; // V flipped (was upside-down)
                float edge = Mathf.Min(Mathf.Min(rawU, 1f - rawU), Mathf.Min(rawV, 1f - rawV)); // dist to nearest UV edge (<0 = off the map)
                vcols[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(edge / 0.02f)); // fade to 0 across a 2% margin past the map edge
                uv[i] = new Vector2(Mathf.Clamp01(rawU), Mathf.Clamp01(rawV));
            }
            mesh.uv = uv;
            mesh.colors = vcols; // only the overlay (Sprites/Default) reads these; the blue ocean (Unlit/Color) ignores them
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE ocean texture='{mapTex!.name}' {mapTex.width}x{mapTex.height} mapBounds=({mb.min.x:0},{mb.min.z:0})..({mb.max.x:0},{mb.max.z:0})");
        }

        GameObject ocean = new("UADVP_GlobeOcean");
        ocean.layer = GlobeRoot!.layer;
        ocean.transform.SetParent(GlobeRoot.transform, false);
        ocean.AddComponent<MeshFilter>().mesh = mesh;
        ocean.AddComponent<MeshRenderer>().sharedMaterial = CreateUnlitColorMaterial(OceanColor, "UADVP_GlobeOcean_Mat");

        // Layer the game's terrain texture OVER the political colors as a SEMI-TRANSPARENT overlay (keep the
        // colors as the base, add terrain detail on top). An overlay only needs a transparent shader
        // (Sprites/Default), so this avoids the opaque-shader problem. Reuses the ocean mesh (carries the
        // aligned UVs), scaled to sit just above the land fills.
        if (haveTex && mapTex != null)
        {
            Material? tm = CreateTerrainOverlayMaterial(mapTex, 0.45f);
            if (tm != null)
            {
                var overlay = new GameObject("UADVP_GlobeTerrain");
                overlay.layer = GlobeRoot.layer;
                overlay.transform.SetParent(GlobeRoot.transform, false);
                overlay.transform.localScale = Vector3.one * (LandLiftFactor + 0.003f); // just above the fills
                overlay.AddComponent<MeshFilter>().mesh = mesh;
                overlay.AddComponent<MeshRenderer>().sharedMaterial = tm;
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE terrain overlay shader='{tm.shader.name}'");
            }
        }
    }

    // Semi-transparent textured material for the terrain overlay (over the political colors). Transparent
    // shaders are readily available (unlike opaque textured ones), so this is the reliable path.
    private static Material? CreateTerrainOverlayMaterial(Texture tex, float alpha)
    {
        try
        {
            Shader? s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("UI/Default");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Unlit/Transparent");
            if (s == null) return null;
            var m = new Material(s) { name = "UADVP_GlobeTerrain_Mat" };
            Color c = new(1f, 1f, 1f, alpha);
            try { m.mainTexture = tex; } catch { }
            try { if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex); } catch { }
            try { m.color = c; } catch { }
            try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
            try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
            try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { } // double-sided, in case the mesh side flips
            try { m.renderQueue = 3100; } catch { } // draw after the opaque fills/ocean
            return m;
        }
        catch { return null; }
    }

    private static int BuildLand(CampaignMap map)
    {
        int countries = 0;
        borderVerts.Clear();
        borderIdx.Clear();
        fillSources.Clear(); // repopulated below (also covers the deferred re-clone)
        try
        {
            CampaignBordersManager? bm = map.bordersManager;
            if (bm == null || bm.Countries == null)
                return 0;
            foreach (CampaignBordersManager.Country country in bm.Countries)
            {
                if (country == null || country.MeshObjects == null)
                    continue;
                // Does this country have any COLORED (non-gray) fill? If so, its dark-gray meshes are the base
                // layer and should be skipped. If NOT, the country is a genuinely dark nation (e.g. Germany,
                // whose fill is also ~[0.08]) and its gray meshes ARE the fills — keep them so it stays visible.
                bool hasColored = false;
                for (int i = 0; i < country.MeshObjects.Count; i++)
                {
                    MeshRenderer? r = country.MeshObjects[i];
                    if (r != null && !IsDarkGray(r)) { hasColored = true; break; }
                }
                bool any = false;
                for (int i = 0; i < country.MeshObjects.Count; i++)
                {
                    MeshRenderer? r = country.MeshObjects[i];
                    if (r != null && ReprojectCountryMesh(r, country.Name ?? "country", i, hasColored))
                        any = true;
                }
                if (any) countries++;
            }
        }
        catch (Exception ex)
        {
            Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE land build: {ex.GetType().Name}: {ex.Message}");
        }
        if (DrawGeneratedBorders) BuildBorderMesh();
        return countries;
    }

    // Territory borders: the perimeter of each re-projected fill region = edges that belong to exactly one
    // triangle. Accumulate them across all regions; BuildBorderMesh draws them as one line mesh.
    private static void AccumulateBorders(Vector3[] nv, int[] nt)
    {
        try
        {
            int n = nv.Length;
            // Weld by quantized position so shared edges are detected even on unwelded source meshes.
            var canon = new Dictionary<(int, int, int), int>();
            var pos = new List<Vector3>();
            var cidx = new int[n];
            for (int i = 0; i < n; i++)
            {
                var key = ((int)Mathf.Round(nv[i].x * 100f), (int)Mathf.Round(nv[i].y * 100f), (int)Mathf.Round(nv[i].z * 100f));
                if (!canon.TryGetValue(key, out int ci)) { ci = pos.Count; canon[key] = ci; pos.Add(nv[i]); }
                cidx[i] = ci;
            }
            var count = new Dictionary<long, int>();
            for (int t = 0; t + 2 < nt.Length; t += 3)
            {
                AddEdge(count, cidx[nt[t]], cidx[nt[t + 1]]);
                AddEdge(count, cidx[nt[t + 1]], cidx[nt[t + 2]]);
                AddEdge(count, cidx[nt[t + 2]], cidx[nt[t]]);
            }
            float lift = 1f; // border sits at the EXACT fill radius (its verts ARE fill perimeter verts) — no floating "cliff"; drawn after the fill via renderQueue so it still shows (shared verts = identical depth, no z-fight)
            float segLen = radius * LandLiftFactor * 0.04f;          // subdivide long edges so they curve
            foreach (KeyValuePair<long, int> kv in count)
            {
                if (kv.Value != 1) continue; // boundary edge = country/region outline
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFF);
                // Skip the flat map's WRAP SEAM (the date-line meridian, the straight line in Russia): both
                // endpoints sit at lon ~±180.
                if (Mathf.Abs(Mathf.Atan2(-pos[a].x, pos[a].z)) > 3.10f && Mathf.Abs(Mathf.Atan2(-pos[b].x, pos[b].z)) > 3.10f) continue;
                Vector3 A = pos[a] * lift, B = pos[b] * lift;
                int steps = Mathf.Clamp((int)((A - B).magnitude / segLen), 1, 10);
                Vector3 prev = A;
                for (int s = 1; s <= steps; s++)
                {
                    Vector3 cur = s == steps ? B : Vector3.Slerp(A, B, s / (float)steps);
                    int bi = borderVerts.Count;
                    borderVerts.Add(prev);
                    borderVerts.Add(cur);
                    borderIdx.Add(bi);
                    borderIdx.Add(bi + 1);
                    prev = cur;
                }
            }
        }
        catch { }
    }

    // Subdivide triangles whose edges span a large arc, projecting midpoints back onto the sphere, so a
    // big flat fill region follows the curvature instead of chording below the ocean shell.
    private static void Tessellate(Vector3[] nv, int[] nt, float landR, out Vector3[] outVerts, out int[] outTris)
    {
        var verts = new List<Vector3>(nv);
        var tris = new List<int>(nt.Length);
        var midCache = new Dictionary<long, int>();
        float maxEdge = landR * 0.09f; // target edge so the fill hugs the sphere

        int Mid(int i, int j)
        {
            long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            if (midCache.TryGetValue(key, out int m)) return m;
            Vector3 mid = ((verts[i] + verts[j]) * 0.5f).normalized * landR;
            m = verts.Count; verts.Add(mid); midCache[key] = m;
            return m;
        }
        // ADAPTIVE: subdivide ONLY triangles whose edges exceed maxEdge; recurse until edges hug the sphere OR
        // the depth cap. Cap raised 2->5 so HUGE territories (Siberia) keep subdividing until they hug instead
        // of chording through the globe. Safe: adaptive stops at maxEdge (bounded ~area/maxEdge^2), unlike the
        // old UNIFORM subdivision that exploded (4^depth on every triangle) and OOM-crashed.
        void Sub(int a, int b, int c, int d)
        {
            Vector3 A = verts[a], B = verts[b], C = verts[c];
            bool big = (A - B).magnitude > maxEdge || (B - C).magnitude > maxEdge || (C - A).magnitude > maxEdge;
            if (d >= 5 || !big) { tris.Add(a); tris.Add(b); tris.Add(c); return; }
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            Sub(a, ab, ca, d + 1); Sub(ab, b, bc, d + 1); Sub(ca, bc, c, d + 1); Sub(ab, bc, ca, d + 1);
        }
        for (int t = 0; t + 2 < nt.Length; t += 3)
            Sub(nt[t], nt[t + 1], nt[t + 2], 0);
        outVerts = verts.ToArray();
        outTris = tris.ToArray();
    }

    // Add a downward SKIRT (vertical wall, same mesh/color) along the fill's perimeter so grazing-angle views
    // can't see through the gaps BETWEEN provinces (separate meshes whose edges don't perfectly meet).
    private static void AppendSkirt(ref Vector3[] verts, ref int[] tris, float landR)
    {
        var count = new Dictionary<long, int>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            AddEdge(count, tris[t], tris[t + 1]);
            AddEdge(count, tris[t + 1], tris[t + 2]);
            AddEdge(count, tris[t + 2], tris[t]);
        }
        var vlist = new List<Vector3>(verts);
        var tlist = new List<int>(tris);
        float skirt = 1f / LandLiftFactor; // drop the wall only to the ocean surface (no tall cliff at the limb)
        foreach (var kv in count)
        {
            if (kv.Value != 1) continue; // boundary edge only
            int i = (int)(kv.Key >> 32), j = (int)(kv.Key & 0xffffffff);
            int al = vlist.Count; vlist.Add(verts[i] * skirt);
            int bl = vlist.Count; vlist.Add(verts[j] * skirt);
            // Double-sided wall so it blocks from either grazing side.
            tlist.Add(i); tlist.Add(j); tlist.Add(bl); tlist.Add(i); tlist.Add(bl); tlist.Add(al);
            tlist.Add(i); tlist.Add(bl); tlist.Add(j); tlist.Add(i); tlist.Add(al); tlist.Add(bl);
        }
        verts = vlist.ToArray();
        tris = tlist.ToArray();
    }

    private static void AddEdge(Dictionary<long, int> count, int a, int b)
    {
        if (a == b) return;
        int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
        long key = ((long)lo << 32) | (uint)hi;
        count.TryGetValue(key, out int c);
        count[key] = c + 1;
    }

    private static void BuildBorderMesh()
    {
        try
        {
            if (GlobeRoot == null || borderVerts.Count < 2)
                return;
            Transform existing = GlobeRoot.transform.Find("UADVP_GlobeBorders");
            if (existing != null)
                UnityEngine.Object.Destroy(existing.gameObject);

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = borderVerts.ToArray();
            mesh.SetIndices(borderIdx.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 4f)); // never frustum-cull the border mesh

            GameObject go = new("UADVP_GlobeBorders");
            go.layer = GlobeRoot.layer;
            go.transform.SetParent(GlobeRoot.transform, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            Material bmat = CreateUnlitColorMaterial(BorderColor, "UADVP_GlobeBorders_Mat");
            try { bmat.renderQueue = 2500; } catch { } // draw AFTER the fills so the coplanar border wins (no float-height, no z-fight)
            go.AddComponent<MeshRenderer>().sharedMaterial = bmat;
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE borders: {borderIdx.Count / 2} edges.");
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE borders: {ex.GetType().Name}: {ex.Message}"); }
    }

    // A mesh is the per-province dark base/outline layer if its color is near-black GRAY (~[0.08,0.08,0.08]).
    private static bool IsDarkGray(MeshRenderer r)
    {
        try { Color c = r.material.color; float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)); float mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b)); return mx < 0.14f && (mx - mn) < 0.03f; }
        catch { return false; }
    }

    private static bool ReprojectCountryMesh(MeshRenderer src, string countryName, int idx, bool countryHasColored)
    {
        try
        {
            MeshFilter? mf = src.GetComponent<MeshFilter>();
            Mesh? srcMesh = mf?.sharedMesh;
            if (srcMesh == null)
                return false;

            // Skip the dark-gray base layer ONLY when the country also has a colored fill; a dark-only nation
            // (e.g. Germany) keeps its gray meshes so it doesn't vanish into the ocean.
            if (countryHasColored && IsDarkGray(src))
                return false;

            var sv = srcMesh.vertices;
            int n = sv.Length;
            if (n == 0)
                return false;
            Transform st = src.transform;
            float landR = radius * LandLiftFactor;

            var nv = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 world = st.TransformPoint(sv[i]);
                nv[i] = WorldToSphereLocal(world, landR);
            }

            var sTri = srcMesh.triangles;
            var nt = new int[sTri.Length];
            for (int i = 0; i < sTri.Length; i++)
                nt[i] = sTri[i];

            // Borders from the ORIGINAL perimeter (not the tessellated mesh, whose T-junction interior
            // edges would otherwise draw as borders).
            if (DrawGeneratedBorders) AccumulateBorders(nv, nt);

            // Tessellate large triangles so the flat fill HUGS the sphere — otherwise big territories chord
            // below the ocean shell and the ocean shows through the middle ("sea in the middle").
            Tessellate(nv, nt, landR, out Vector3[] fillV, out int[] fillT);
            // Face triangles OUTWARD (the lon X-flip inverted the winding -> fills were inside-out, near side
            // backface-culled). Also made the material double-sided in ForceOpaque as a backstop.
            for (int w = 0; w + 2 < fillT.Length; w += 3) { int tmp = fillT[w + 1]; fillT[w + 1] = fillT[w + 2]; fillT[w + 2] = tmp; }
            // NOTE: skirt REMOVED — the downward colored wall it added showed below the border line at any
            // viewing angle, which is the "fill color on a different level than the border" cliff the user saw.
            // Fill is now a flat shell with the border coplanar on its edge. (AppendSkirt kept for reference.)

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = fillV;
            mesh.triangles = fillT;
            // Smooth RADIAL normals (outward from globe center) instead of per-face normals — per-face normals
            // make the tessellation visible as faceted shading on a lit fill material.
            var nrm = new Vector3[fillV.Length];
            for (int i = 0; i < fillV.Length; i++) nrm[i] = fillV[i].normalized;
            mesh.normals = nrm;
            mesh.RecalculateBounds();
            // Globe-spanning bounds so the fill is NEVER frustum-culled — per-country fills were vanishing
            // (ocean showing through) when zoomed in because the engine thought their bounds were off-screen.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 4f));

            GameObject go = new($"UADVP_GlobeLand_{countryName.Replace(' ', '_')}_{idx}");
            go.layer = GlobeRoot!.layer;
            go.transform.SetParent(GlobeRoot.transform, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            // Clone the source fill's INSTANCED material (its rich per-nation palette) and FORCE it opaque
            // (the overlay material is translucent and blue-tinted the land over the ocean).
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            Material? landMat = null;
            try { Material srcMat = src.material; if (srcMat != null) { landMat = new Material(srcMat) { name = go.name + "_Mat" }; ForceOpaque(landMat); } } catch { }
            mr.sharedMaterial = landMat ?? CreateUnlitColorMaterial(GetMaterialColor(src), go.name + "_Mat");
            fillSources.Add((src, mr)); // remember the source so we can refresh the color when territory changes hands
            return true;
        }
        catch { return false; }
    }

    // Refresh each reprojected fill's color from its live source, so the globe recolors when a territory
    // changes hands (the geometry is unchanged — only the material color is re-read; cheap).
    private static void UpdateFillColors()
    {
        for (int i = 0; i < fillSources.Count; i++)
        {
            try
            {
                (MeshRenderer src, MeshRenderer clone) = fillSources[i];
                if (src == null || clone == null) continue;
                Material cm = clone.sharedMaterial;
                if (cm == null) continue;
                Color c = GetMaterialColor(src); // MPB-aware: a territory's new owner color may be in the property block
                if (cm.color != c) cm.color = c;
                try { if (cm.HasProperty("_BaseColor")) cm.SetColor("_BaseColor", c); } catch { }
                try { if (cm.HasProperty("_Color")) cm.SetColor("_Color", c); } catch { }
            }
            catch { }
        }
    }

    // Logs a marker root's children (name/active/pos/euler/vertex count) so circles/arrows can be reprojected.
    private static void ProbeMarkerRoot(string tag, Transform? root)
    {
        if (root == null) { Melon<UADVanillaPlusMod>.Logger.Msg($"marker-root {tag}=null"); return; }
        int n = root.childCount;
        Melon<UADVanillaPlusMod>.Logger.Msg($"marker-root {tag}='{root.gameObject.name}' children={n}");
        for (int i = 0; i < n && i < 4; i++)
        {
            try
            {
                Transform ch = root.GetChild(i);
                int vc = -1; try { MeshFilter mf2 = ch.GetComponentInChildren<MeshFilter>(); if (mf2 != null && mf2.sharedMesh != null) vc = mf2.sharedMesh.vertexCount; } catch { }
                Melon<UADVanillaPlusMod>.Logger.Msg($"  {tag}[{i}] '{ch.gameObject.name}' active={ch.gameObject.activeInHierarchy} pos={ch.position} euler={ch.eulerAngles} childVerts={vc}");
            }
            catch { }
        }
    }

    // Logs the structure of the markers still to reproject (invasion/zone CIRCLES + route end DOT + land
    // battle ARROWS) so they can be placed on the sphere. Called on build and in the F9 dump.
    internal static void ProbeMarkers()
    {
        try { ProbeMarkerRoot("zones", denialZoneRoot); ProbeMarkerRoot("events", eventsRoot); ProbeMarkerRoot("minesweep", mineSweepRoot); } catch { }
        try
        {
            var all = CampaignMap.Instance != null ? CampaignMap.Instance.transform.root.GetComponentsInChildren<Transform>(true) : null;
            int nn = 0;
            if (all != null)
                for (int i = 0; i < all.Length && nn < 24; i++)
                {
                    string nm; try { nm = all[i].gameObject.name; } catch { continue; }
                    string lo = nm.ToLowerInvariant();
                    if (!(lo.Contains("arrow") || lo.Contains("destination") || lo.Contains("invasion")
                          || lo.Contains("attack") || lo.Contains("assault") || lo.Contains("landing")
                          || lo.Contains("offensive") || lo.Contains("army") || lo.Contains("troop")
                          || lo.Contains("direction") || lo.Contains("battle"))) continue;
                    bool act = false; try { act = all[i].gameObject.activeInHierarchy; } catch { }
                    int vc = -1; try { MeshFilter mf2 = all[i].GetComponent<MeshFilter>(); if (mf2 != null && mf2.sharedMesh != null) vc = mf2.sharedMesh.vertexCount; } catch { }
                    nn++;
                    Melon<UADVanillaPlusMod>.Logger.Msg($"marker-cand '{nm}' active={act} verts={vc} parent={(all[i].parent != null ? all[i].parent.gameObject.name : "-")}");
                }
            // Dump the ProvinceBattles subtree (land-battle/invasion container under WorldEx) so the arrow
            // structure (mesh vs line vs UI, position) is captured for reprojection.
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                {
                    string nm; try { nm = all[i].gameObject.name; } catch { continue; }
                    if (nm != "ProvinceBattles") continue;
                    DumpSubtree(all[i], 0, 4);
                    break;
                }
        }
        catch { }
    }

    private static void DumpSubtree(Transform t, int depth, int maxDepth)
    {
        try
        {
            int vc = -1; try { MeshFilter mf = t.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) vc = mf.sharedMesh.vertexCount; } catch { }
            bool lr = false; try { lr = t.GetComponent<LineRenderer>() != null; } catch { }
            bool mr = false; try { mr = t.GetComponent<MeshRenderer>() != null; } catch { }
            bool spr = false; try { spr = t.GetComponent<SpriteRenderer>() != null; } catch { }
            string mat = ""; try { Renderer r = t.GetComponent<Renderer>(); if (r != null && r.sharedMaterial != null) mat = r.sharedMaterial.name; } catch { }
            Melon<UADVanillaPlusMod>.Logger.Msg($"  pb-sub d{depth} '{t.gameObject.name}' act={t.gameObject.activeInHierarchy} verts={vc} lr={lr} mr={mr} spr={spr} mat='{mat}' pos={t.position}");
            if (depth < maxDepth)
                for (int i = 0; i < t.childCount && i < 12; i++) DumpSubtree(t.GetChild(i), depth + 1, maxDepth);
        }
        catch { }
    }

    // Full state dump for self-diagnosis: borders (mine + native), fill colors, label clustering, markers,
    // camera. Call on build and on the F8 hotkey, so issues can be confirmed from the log without the user
    // re-describing them. (Pure GPU artifacts like z-fighting still need a screenshot — those aren't loggable.)
    internal static void DumpGlobeState()
    {
        try
        {
            var L = Melon<UADVanillaPlusMod>.Logger;
            L.Msg("=== UADVP_GLOBE STATE ===");
            Vector3 gc = GlobeRoot != null ? GlobeRoot.transform.position : Vector3.zero;
            L.Msg($"globe root={(GlobeRoot != null)} radius={radius:0} center={gc} landLift={LandLiftFactor} genBorders={DrawGeneratedBorders}");

            int landCount = 0; string cols = "";
            Transform? borderMesh = null;
            if (GlobeRoot != null)
            {
                Transform t = GlobeRoot.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform ch = t.GetChild(i);
                    string nm = ch.name;
                    if (nm == "UADVP_GlobeBorders") borderMesh = ch;
                    if (!nm.StartsWith("UADVP_GlobeLand_")) continue;
                    landCount++;
                    if (landCount <= 8)
                    {
                        try { MeshRenderer rr = ch.GetComponent<MeshRenderer>(); if (rr != null && rr.sharedMaterial != null) { Color c = rr.sharedMaterial.color; cols += $"[{c.r:0.0},{c.g:0.0},{c.b:0.0},a{c.a:0.0}] "; } } catch { }
                    }
                }
            }
            L.Msg($"fills count={landCount} sampleColors={cols}");
            L.Msg($"my-border-mesh={(borderMesh != null ? "PRESENT" : "none")}");

            try { MapUI? ui = CampaignMap.Instance != null ? CampaignMap.Instance.UIMap : null;
                if (ui != null && ui.ProvincesCanvasGroup != null)
                    L.Msg($"ProvincesCanvasGroup active={ui.ProvincesCanvasGroup.gameObject.activeInHierarchy} alpha={ui.ProvincesCanvasGroup.alpha:0.0}");
                if (ui != null)
                    L.Msg($"markers ports={SafeCount(ui.portElements)} ships={SafeCount(ui.movingShipsElements)} battles={SafeCount(ui.mapBattles)} events={SafeCount(ui.mapSpecialEvents)}");
            } catch { }

            LogLabelSpread("area", labelAreaRoot);
            LogLabelSpread("prov", labelProvincesRoot);

            try { Cam cam = Cam.Instance; if (cam != null && cam.cameraComp != null)
                L.Msg($"cam ortho={cam.cameraComp.orthographic} fov={cam.cameraComp.fieldOfView:0} near={cam.cameraComp.nearClipPlane:0.0} far={cam.cameraComp.farClipPlane:0} dist={dist:0}"); } catch { }
            L.Msg($"hiddenRenderers={hiddenRenderers.Count}");

            // ALL cameras — the corner "flat 2D map" is almost certainly a 2nd camera with a small viewport
            // rect (or one rendering to a RenderTexture). rect != (0,0,1,1) => a sub-screen widget.
            try
            {
                var cams = Resources.FindObjectsOfTypeAll<Camera>();
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera cc = cams[i]; if (cc == null) continue;
                    L.Msg($"camera '{cc.name}' active={cc.gameObject.activeInHierarchy} en={cc.enabled} depth={cc.depth:0} rect=({cc.rect.x:0.00},{cc.rect.y:0.00},{cc.rect.width:0.00},{cc.rect.height:0.00}) rt={(cc.targetTexture != null)}");
                }
            }
            catch (Exception cex) { L.Msg($"camera-dump fail: {cex.Message}"); }

            // Flat-map containers / minimap widgets still active.
            try
            {
                var allT = CampaignMap.Instance != null ? CampaignMap.Instance.transform.root.GetComponentsInChildren<Transform>(true) : null;
                if (allT != null)
                    for (int i = 0; i < allT.Length; i++)
                    {
                        string nm; try { nm = allT[i].gameObject.name; } catch { continue; }
                        string lo = nm.ToLowerInvariant();
                        if (!(lo.Contains("2dmap") || lo.Contains("mini") || lo.Contains("monitor") || lo.Contains("overview") || lo == "worldex")) continue;
                        bool act = false; try { act = allT[i].gameObject.activeInHierarchy; } catch { }
                        bool ren = false; try { Renderer rr = allT[i].GetComponent<Renderer>(); ren = rr != null && rr.enabled; } catch { }
                        L.Msg($"flatmap-obj '{nm}' active={act} rendEnabled={ren} parent={(allT[i].parent != null ? allT[i].parent.gameObject.name : "-")}");
                    }
            }
            catch { }

            // Leaked renderers: active+enabled, not ours, not under the globe = flat-map stuff still drawing.
            // The border layer shows here IF it is a Renderer; if borders persist but nothing border-ish appears,
            // they are the immediate-mode BordersMaterial draw (targeted via DrawAllBorders/alpha above).
            try
            {
                var rends = Resources.FindObjectsOfTypeAll<Renderer>();
                int shown = 0;
                for (int i = 0; i < rends.Length && shown < 40; i++)
                {
                    Renderer r = rends[i];
                    if (r == null) continue;
                    bool en, act; try { en = r.enabled; act = r.gameObject.activeInHierarchy; } catch { continue; }
                    if (!en || !act) continue;
                    string nm; try { nm = r.gameObject.name; } catch { continue; }
                    if (nm.StartsWith("UADVP_")) continue;
                    try { if (GlobeRoot != null && r.transform.IsChildOf(GlobeRoot.transform)) continue; } catch { }
                    string matn = "?"; try { matn = r.sharedMaterial != null ? r.sharedMaterial.name : "null"; } catch { }
                    string par = "-"; try { if (r.transform.parent != null) par = r.transform.parent.gameObject.name; } catch { }
                    float y = 0f; try { y = r.bounds.center.y; } catch { }
                    shown++;
                    L.Msg($"leaked-rend '{nm}' type={r.GetType().Name} mat={matn} y={y:0.0} parent={par}");
                }
                L.Msg($"leaked-rend shown={shown}");
            }
            catch (Exception lex) { L.Msg($"leaked-rend fail: {lex.Message}"); }

            ProbeMarkers();

            L.Msg("=== END STATE ===");
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE state dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static int SafeCount(Il2CppSystem.Collections.Generic.List<CampaignMapElement>? l)
    { try { return l != null ? l.Count : -1; } catch { return -2; } }

    // Logs label count + position spread; a tiny spread means they are CLUSTERED (the center-pile bug).
    private static void LogLabelSpread(string tag, Transform? root)
    {
        if (root == null) { Melon<UADVanillaPlusMod>.Logger.Msg($"labels-{tag}=null"); return; }
        int n = root.childCount, active = 0;
        Vector3 mn = new(1e9f, 1e9f, 1e9f), mx = new(-1e9f, -1e9f, -1e9f);
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = root.GetChild(i);
                if (ch.gameObject.activeSelf) active++;
                Vector3 p = ch.position;
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            catch { }
        }
        Melon<UADVanillaPlusMod>.Logger.Msg($"labels-{tag} count={n} active={active} spread=({mx.x - mn.x:0},{mx.y - mn.y:0},{mx.z - mn.z:0})");
    }

    private static bool bordersDumped;

    // One-time diagnostic: log every scene object whose name looks like a border/outline, with its state,
    // so the actual "2d border" source can be identified from the log instead of guessing.
    private static void DumpBorderCandidates(CampaignMap map)
    {
        if (bordersDumped) return;
        bordersDumped = true;
        try
        {
            var all = map.transform.root.GetComponentsInChildren<Transform>(true);
            int n = 0;
            for (int i = 0; i < all.Length && n < 50; i++)
            {
                Transform tr = all[i];
                string nm; try { nm = tr.gameObject.name; } catch { continue; }
                string low = nm.ToLowerInvariant();
                if (!(low.Contains("border") || low.Contains("frontier") || low.Contains("province")
                      || low.Contains("outline") || low.Contains("boundary") || low.Contains("line"))) continue;
                bool active = false; try { active = tr.gameObject.activeInHierarchy; } catch { }
                bool rEn = false; bool hasR = false; try { Renderer rr = tr.GetComponent<Renderer>(); hasR = rr != null; rEn = rr != null && rr.enabled; } catch { }
                bool hasG = false; try { hasG = tr.GetComponent<UnityEngine.UI.Graphic>() != null; } catch { }
                n++;
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE border-candidate: '{nm}' active={active} rend={hasR}/en={rEn} graphic={hasG} parent={(tr.parent != null ? tr.parent.gameObject.name : "-")}");
            }
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE border-candidates dumped ({n}).");
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE border-dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void HideFlat(CampaignMap map)
    {
        try
        {
            flatHidden = true;
            // Disable every renderer in the WORLD ROOT hierarchy (the map component itself holds only the
            // terrain; the fills, base/water plane and border LINES live elsewhere under the same scene
            // root) so none of the 2D map shows behind the globe. UI markers use CanvasRenderer (not
            // Renderer) so they survive; our own globe objects (UADVP_*) and GlobeRoot are skipped.
            // Cache the roots we KEEP (and reproject onto the globe): routes, name labels, gameplay circles.
            MapUI? ui = null; try { ui = map.UIMap; } catch { }
            Transform? routeRoot = null;
            try { if (ui != null) routeRoot = ui.RouteLineRoot; } catch { }
            try { if (ui != null) mineSweepRoot = ui.MineSweepingRadiusRoot; } catch { }
            try { if (ui != null) denialZoneRoot = ui.ZonesRoot; } catch { }
            try { if (ui != null) eventsRoot = ui.EventsRoot; } catch { }
            try { labelAreaRoot = map.LabelsAreaRoot; } catch { }
            try { labelProvincesRoot = map.LabelsProvincesRoot; } catch { }

            try
            {
                Transform root = map.transform.root;
                var arr = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < arr.Length; i++)
                {
                    Renderer r = arr[i];
                    if (r == null || !r.enabled) continue;
                    string nm = "?"; try { nm = r.gameObject.name; } catch { }
                    if (nm.StartsWith("UADVP_")) continue;            // never disable our globe
                    Transform rt = r.transform;
                    if (IsUnder(rt, routeRoot)) continue;             // routes (reprojected)
                    if (IsUnder(rt, labelAreaRoot) || IsUnder(rt, labelProvincesRoot)) continue; // names (reprojected)
                    if (IsUnder(rt, denialZoneRoot) || IsUnder(rt, mineSweepRoot) || IsUnder(rt, eventsRoot)) continue; // invasion/zone circles (reprojected in RuntimeUpdate)
                    hiddenRenderers.Add(r);
                    r.enabled = false;
                }
            }
            catch (Exception rex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE renderer-hide: {rex.GetType().Name}: {rex.Message}"); }

            // Country fills may live outside the scene root scan — disable them explicitly too.
            try
            {
                CampaignBordersManager? bm = map.bordersManager;
                if (bm?.Countries != null)
                    foreach (CampaignBordersManager.Country country in bm.Countries)
                    {
                        if (country?.MeshObjects == null) continue;
                        for (int i = 0; i < country.MeshObjects.Count; i++)
                        {
                            MeshRenderer? r = country.MeshObjects[i];
                            if (r != null && r.enabled) { hiddenRenderers.Add(r); r.enabled = false; }
                        }
                    }
            }
            catch { }
            // The flat-map TERRAIN: map.MapRenderer is the plane that "sticks out" around the globe when you
            // zoom out. The GetComponentsInChildren sweep above MISSES it if it lives on a SEPARATE scene root
            // from map.transform.root (which is exactly the bug). Disable it explicitly (+ re-asserted every
            // frame in RuntimeUpdate, since the game re-enables it) and also sweep map.Root's hierarchy.
            try { Renderer mr = map.MapRenderer; if (mr != null && mr.enabled) { hiddenRenderers.Add(mr); mr.enabled = false; Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE disabled MapRenderer '{mr.gameObject.name}' (root={mr.transform.root.gameObject.name}, mapRoot={map.transform.root.gameObject.name})"); } } catch { }
            try
            {
                Transform mroot = map.Root;
                if (mroot != null)
                {
                    var arr2 = mroot.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < arr2.Length; i++)
                    {
                        Renderer r = arr2[i];
                        if (r == null || !r.enabled) continue;
                        string nm = "?"; try { nm = r.gameObject.name; } catch { }
                        if (nm.StartsWith("UADVP_")) continue;
                        Transform rt = r.transform;
                        if (IsUnder(rt, routeRoot) || IsUnder(rt, labelAreaRoot) || IsUnder(rt, labelProvincesRoot)) continue;
                        hiddenRenderers.Add(r); r.enabled = false;
                    }
                }
            }
            catch { }
            // Territory BORDERS draw via CampaignBordersManager.BordersMaterial. There is no name-matchable
            // border Renderer and no MonoBehaviour draw callback, so find EVERY renderer that uses BordersMaterial
            // (by reference, ANY material slot, ANY root) or a border/coast/outline material, and disable it.
            // Also flatten DrawAllBorders + alpha as backstops, and log the shader (for the GPU-immediate case).
            try
            {
                CampaignBordersManager? bm2 = map.bordersManager;
                if (bm2 != null)
                {
                    try { savedDrawAllBorders = bm2.DrawAllBorders; drawAllBordersWasSet = true; bm2.DrawAllBorders = false; } catch { }
                    try { bordersMat = bm2.BordersMaterial; if (bordersMat != null) { savedBordersColor = bordersMat.color; Color z = savedBordersColor; z.a = 0f; bordersMat.color = z; } } catch { }
                    string shd = "?"; try { shd = bordersMat != null ? bordersMat.shader.name : "null"; } catch { }
                    int hit = 0;
                    try
                    {
                        var rends = Resources.FindObjectsOfTypeAll<Renderer>();
                        for (int i = 0; i < rends.Length; i++)
                        {
                            Renderer r = rends[i]; if (r == null) continue;
                            bool match = false; string mn = "";
                            try
                            {
                                var ms = r.sharedMaterials;
                                if (ms != null)
                                    for (int m = 0; m < ms.Length; m++)
                                    {
                                        Material mm = ms[m]; if (mm == null) continue;
                                        if (bordersMat != null && mm == bordersMat) { match = true; mn = mm.name; break; }
                                        string ln = mm.name.ToLowerInvariant();
                                        if (ln.Contains("border") || ln.Contains("coast") || ln.Contains("outline") || ln.Contains("frontier")) { match = true; mn = mm.name; break; }
                                    }
                            }
                            catch { }
                            if (!match) continue;
                            try { if (r.gameObject.name.StartsWith("UADVP_")) continue; } catch { }
                            try { if (r.enabled) { hiddenRenderers.Add(r); r.enabled = false; } } catch { }
                            hit++;
                            try { Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE hid BORDER renderer '{r.gameObject.name}' mat={mn} parent={(r.transform.parent != null ? r.transform.parent.gameObject.name : "-")}"); } catch { }
                        }
                    }
                    catch { }
                    Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE borders: DrawAllBorders false (was {savedDrawAllBorders}); material={(bordersMat != null ? bordersMat.name : "null")} shader={shd} by-material-hits={hit}");
                }
            }
            catch { }
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE hid {hiddenRenderers.Count} flat-map renderer(s).");

            // The native flat province/country BORDER LINES are a CanvasGroup (UI, not a Renderer), so the
            // sweep misses them — disable it so they don't float over the globe.
            try
            {
                if (ui != null && ui.ProvincesCanvasGroup != null)
                {
                    CanvasGroup cg = ui.ProvincesCanvasGroup;
                    provincesCanvasAlpha = cg.alpha;
                    cg.alpha = 0f;
                    cg.gameObject.SetActive(false); // definitive: alpha alone did not hide the border UI
                }
            }
            catch { }

            // The flat black COASTLINE/BORDER line-drawing that "sticks out" of the globe is drawn by an
            // "Outline Camera" (it renders country/province outlines to a RenderTexture and MANUAL-renders, so
            // its .enabled reads false). It is not reprojected, so it stays flat and juts past the sphere.
            // Disable every non-main camera that is an outline cam / renders to an RT / has a small viewport,
            // by GameObject (so manual-render cams stop too). Stored for restore + re-asserted in RuntimeUpdate.
            try
            {
                Camera? main = Cam.Instance != null ? Cam.Instance.cameraComp : null;
                var cams = Resources.FindObjectsOfTypeAll<Camera>();
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera cc = cams[i];
                    if (cc == null || cc == main) continue;
                    GameObject go;
                    try { if (!cc.gameObject.activeInHierarchy) continue; go = cc.gameObject; } catch { continue; }
                    string lo = "?"; try { lo = cc.name.ToLowerInvariant(); } catch { }
                    bool outline = lo.Contains("outline");
                    bool rt = false; try { rt = cc.targetTexture != null; } catch { }
                    bool small = false; try { small = cc.rect.width < 0.7f || cc.rect.height < 0.7f; } catch { }
                    if (!outline && !rt && !small) continue;
                    string comps = ""; try { foreach (Component cm in go.GetComponents<Component>()) comps += cm.GetType().Name + " "; } catch { }
                    string par = "-"; try { if (go.transform.parent != null) par = go.transform.parent.gameObject.name; } catch { }
                    Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE disabled camera '{cc.name}' rt={rt} parent={par} comps=[{comps}]");
                    hiddenCameraObjects.Add(go);
                    go.SetActive(false);
                }
            }
            catch { }

            DumpBorderCandidates(map); // one-time diagnostic: log every border/line/province object + state

            // Labels are kept ACTIVE now (reprojected onto the globe in RuntimeUpdate) — do NOT hide them.
            Transform? grid = null;
            try { grid = map.transform.root.Find("WorldEx/MapVisualGrid"); } catch { }
            if (grid != null) grid.gameObject.SetActive(false);
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE hide-flat: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void RestoreFlat()
    {
        try
        {
            if (!flatHidden) return;
            foreach (Renderer r in hiddenRenderers)
                try { if (r != null) r.enabled = true; } catch { }
            hiddenRenderers.Clear();
            foreach (GameObject go in hiddenCameraObjects)
                try { if (go != null) go.SetActive(true); } catch { }
            hiddenCameraObjects.Clear();
            try
            {
                CampaignMap? bmMap = CampaignMap.Instance;
                CampaignBordersManager? bm = bmMap != null ? bmMap.bordersManager : null;
                if (bm != null && drawAllBordersWasSet) { bm.DrawAllBorders = savedDrawAllBorders; }
            }
            catch { }
            drawAllBordersWasSet = false;
            try { if (bordersMat != null) { bordersMat.color = savedBordersColor; bordersMat = null; } } catch { }

            CampaignMap? map = CampaignMap.Instance;
            if (map != null)
            {
                TrySetActive(map.LabelsAreaRoot, true);
                TrySetActive(map.LabelsProvincesRoot, true);
                try { MapUI? ui = map.UIMap; if (ui != null && ui.ProvincesCanvasGroup != null) { ui.ProvincesCanvasGroup.gameObject.SetActive(true); ui.ProvincesCanvasGroup.alpha = provincesCanvasAlpha; } } catch { }
                Transform? grid = null;
                try { grid = map.transform.root.Find("WorldEx/MapVisualGrid"); } catch { }
                if (grid != null) grid.gameObject.SetActive(true);
            }
            flatHidden = false;
        }
        catch { }
    }

    private static void DestroyGlobe()
    {
        try
        {
            if (GlobeRoot != null)
            {
                UnityEngine.Object.Destroy(GlobeRoot);
                GlobeRoot = null;
            }
            RestoreCamera();
        }
        catch { }
    }

    // True only when the pointer is over a scrollable UI panel (popup with a ScrollRect) — used so the wheel/
    // drag goes to the popup, not the globe. Narrower than IsPointerOverGameObject (which is always true
    // because the map has a full-screen raycast target).
    private static bool PointerOverScrollUI()
    {
        try
        {
            UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return false;
            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = Input.mousePosition };
            var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ped, results);
            for (int i = 0; i < results.Count; i++)
            {
                GameObject go = results[i].gameObject;
                if (go == null) continue;
                // ScrollRect (scroll lists), Slider/Scrollbar (e.g. the fleet-percentage drag bar)
                if (go.GetComponentInParent<UnityEngine.UI.ScrollRect>() != null
                    || go.GetComponentInParent<UnityEngine.UI.Slider>() != null
                    || go.GetComponentInParent<UnityEngine.UI.Scrollbar>() != null) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // Driven from the Cam.Update POSTFIX (after native Update, so our transform write wins).
    internal static void DriveOrbitCamera(Cam cam)
    {
        try
        {
            if (GlobeRoot == null || cam?.cameraComp == null)
                return;
            if (!cameraSeized)
            {
                camOrthoOrig = cam.cameraComp.orthographic;
                camFovOrig = cam.cameraComp.fieldOfView;
                camNearOrig = cam.cameraComp.nearClipPlane;
                camFarOrig = cam.cameraComp.farClipPlane;
                cameraSeized = true;
            }
            // Re-assert EVERY frame — native flips the camera back to orthographic each frame (diagnostic
            // showed ortho=True returning by frame 3), which kills zoom and the 3D view.
            cam.cameraComp.orthographic = false;
            cam.cameraComp.fieldOfView = 45f;
            cam.cameraComp.nearClipPlane = radius * 0.04f;
            cam.cameraComp.farClipPlane = radius * 8f; // very tight range -> good depth precision (no land/ocean z-fight)

            // Diagnostic: log the camera position at the START of the first few frames (before our write)
            // to confirm whether native is reverting it between frames.
            if (driveLogCount < 3)
            {
                driveLogCount++;
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE drive frame {driveLogCount}: beforeWrite={cam.transform.position} ortho={cam.cameraComp.orthographic} far={cam.cameraComp.farClipPlane:0}");
            }

            // Input accumulates ONCE per frame (DriveOrbitCamera is called from two hooks, which would
            // otherwise double every drag/keypress).
            if (Time.frameCount != lastInputFrame)
            {
                lastInputFrame = Time.frameCount;
                // Drag-vs-click discrimination (so a drag-release doesn't fire a native move order). On press,
                // note if we're over a scrollable popup so its scroll/drag doesn't also move the globe (#5).
                if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) { mouseDownScreen = Input.mousePosition; dragOccurred = false; dragOverScroll = PointerOverScrollUI(); }
                if ((Input.GetMouseButton(0) || Input.GetMouseButton(1)) && (Input.mousePosition - mouseDownScreen).magnitude > 6f) dragOccurred = true;
                // LEFT-drag rotates ("moves") the globe (native-style: left = navigate). Right-click is for
                // fleet move orders (translated to the globe destination), so it must NOT rotate.
                if (Input.GetMouseButton(0) && !dragOverScroll)
                {
                    // Scale drag rotation with zoom: a degree of rotation moves the surface more on-screen when
                    // zoomed in, so reduce sensitivity there (keeps the on-screen drag feel ~constant).
                    float sens = 3f * Mathf.Clamp(dist / (radius * 2.6f), 0.35f, 1.3f);
                    yaw += Input.GetAxis("Mouse X") * sens;
                    pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * sens, -85f, 85f);
                }
                // Keyboard pan (WASD / arrows) to move around the globe without dragging.
                float pan = 60f * Time.deltaTime;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) yaw -= pan;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) yaw += pan;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) pitch = Mathf.Clamp(pitch + pan, -85f, 85f);
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) pitch = Mathf.Clamp(pitch - pan, -85f, 85f);
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.0001f && !PointerOverScrollUI()) // let a hovered scroll popup take the wheel
                    dist = Mathf.Clamp(dist - scroll * radius * 2f, radius * 1.15f, radius * 6f);
            }
            if (dist <= 0f) dist = radius * 2.6f;

            // Drive the ACTUAL rendering camera's transform (cameraComp), not the Cam rig root — if the
            // camera is a child of a rig, writing the rig only moves the pivot while the child keeps its
            // top-down offset (which is why the view stayed flat).
            Transform camT = cam.cameraComp.transform;
            Vector3 c = GlobeRoot.transform.position;
            Quaternion q = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 camPos = c + q * (Vector3.back * dist);
            camT.position = camPos;
            camT.rotation = Quaternion.LookRotation(c - camPos, Vector3.up);
            lastCamPos = camPos; lastCenter = c;

            // Sync the native focus/minimap to the flat-map point centered in view (inverse of LatLonToUnit)
            // so the game registers that the view moved — the "can't move / stuck" fix.
            try
            {
                Vector3 nf = (camPos - c).normalized;
                float latR = Mathf.Asin(Mathf.Clamp(nf.y, -1f, 1f));
                float lonR = Mathf.Atan2(-nf.x, nf.z); // matches the negated X in LatLonToUnit
                Vector3 flat = CampaignMap.Instance.LatitudeLongitudeToUnityCoords(latR / calK, lonR / calK);
                cam.lookingAt = flat;
                cam.distanceDesired = dist;
            }
            catch { }
            if (driveLogCount == 1)
            {
                bool sameGo = camT.GetInstanceID() == cam.transform.GetInstanceID();
                string parent = "?"; try { parent = camT.parent != null ? camT.parent.name : "(none)"; } catch { }
                Melon<UADVanillaPlusMod>.Logger.Msg(
                    $"UADVP_GLOBE camera drive engaged: center={c} camPos={camPos} dist={dist:0} ortho={cam.cameraComp.orthographic} camComp==rig={sameGo} camParent={parent} readback={camT.position}");
            }
        }
        catch { }
    }

    private static void RestoreCamera()
    {
        try
        {
            Cam cam = Cam.Instance;
            if (cameraSeized && cam?.cameraComp != null)
            {
                cam.cameraComp.orthographic = camOrthoOrig;
                cam.cameraComp.fieldOfView = camFovOrig;
                cam.cameraComp.nearClipPlane = camNearOrig;
                cam.cameraComp.farClipPlane = camFarOrig;
            }
        }
        catch { }
        cameraSeized = false;
    }

    // Re-clone the land meshes a few seconds after building, to catch country fills that get COLORED late
    // (on reload the fills exist but aren't colored yet when the globe first builds -> blank land).
    internal static void MaybeRebuildLand()
    {
        if (GlobeRoot == null || landRebuildAtFrame < 0 || Time.frameCount < landRebuildAtFrame)
            return;
        if (landRebuildsLeft > 0) { landRebuildsLeft--; landRebuildAtFrame = Time.frameCount + 180; }
        else landRebuildAtFrame = -1;
        try
        {
            CampaignMap map = CampaignMap.Instance;
            if (map == null)
                return;
            Transform root = GlobeRoot.transform;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform ch = root.GetChild(i);
                string nm = "?"; try { nm = ch.name; } catch { }
                if (nm.StartsWith("UADVP_GlobeLand_"))
                    UnityEngine.Object.Destroy(ch.gameObject);
            }
            // Re-enable the source country fills so the re-clone reads their (now-colored) materials; the
            // fills are disabled by HideFlat, and cloning a disabled renderer's material can come back blank.
            SetCountryFillsEnabled(map, true);
            int countries = BuildLand(map);
            SetCountryFillsEnabled(map, false);
            Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE land re-cloned to catch late coloring: {countries} countries.");
        }
        catch (Exception ex) { Melon<UADVanillaPlusMod>.Logger.Warning($"UADVP_GLOBE land rebuild: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Temporarily flip the source country fill renderers (HideFlat disabled them) so a re-clone reads live
    // materials. They stay tracked in hiddenRenderers, so RestoreFlat still re-enables them on exit.
    private static void SetCountryFillsEnabled(CampaignMap map, bool enabled)
    {
        try
        {
            CampaignBordersManager? bm = map.bordersManager;
            if (bm?.Countries == null) return;
            foreach (CampaignBordersManager.Country country in bm.Countries)
            {
                if (country?.MeshObjects == null) continue;
                for (int i = 0; i < country.MeshObjects.Count; i++)
                    if (country.MeshObjects[i] != null) country.MeshObjects[i].enabled = enabled;
            }
        }
        catch { }
    }

    // Re-position the STATIC markers (ports, battles, events) every frame after the camera moves, so they
    // track the globe without lag (native only repositions them on refresh, so orbiting left them behind).
    // Ships already update every frame natively. Goes through the UpdatePositionScale prefix (which does
    // the sphere projection); we just supply each marker's last known scale.
    internal static void RuntimeUpdate()
    {
        try
        {
            if (!GlobeMarkerActive)
                return;
            CampaignMap map = CampaignMap.Instance;
            // Re-assert the flat terrain stays hidden — the game re-enables MapRenderer, which is the plane
            // that sticks out around the globe.
            try { if (map != null && map.MapRenderer != null && map.MapRenderer.enabled) map.MapRenderer.enabled = false; } catch { }
            // Re-assert the outline cameras stay off (the flat coastline/border line-drawing). The game may
            // re-activate them each frame.
            for (int ci = 0; ci < hiddenCameraObjects.Count; ci++)
                try { GameObject go = hiddenCameraObjects[ci]; if (go != null && go.activeSelf) go.SetActive(false); } catch { }
            // Re-assert borders stay off (the game re-enables DrawAllBorders).
            try { if (map != null && map.bordersManager != null && map.bordersManager.DrawAllBorders) map.bordersManager.DrawAllBorders = false; } catch { }
            if (markerProbeFrame > 0 && Time.frameCount >= markerProbeFrame) { markerProbeFrame = -1; ProbeMarkers(); } // capture live markers
            MapUI? mapUi = map != null ? map.UIMap : null;
            if (mapUi == null)
                return;
            RepositionMarkers(mapUi.portElements);
            RepositionMarkers(mapUi.mapBattles);
            RepositionMarkers(mapUi.mapSpecialEvents);
            RepositionMarkers(mapUi.movingShipsElements); // keep ships on the globe even when a popup pauses native updates
            RepositionLabels();
            ReprojectCircles();
            BuildZoneRings(); // draw our own rings for invasion/denial-zone/minesweeping circles (their shader won't transfer)
            BuildBattleArrows(); // our own surface-conforming ribbons (native LineRenderer can't lie flat on the sphere)
            ReprojectBattleFlags(); // native attacker flag at each arrow's origin (the tail)
            HandleBattleHover(); // drive the native land-battle tooltip when hovering a battle on the globe
            if (Time.frameCount >= flatRouteFrame) { flatRouteFrame = Time.frameCount + 15; ReprojectFlatRoutes(); } // catch routes SetRoutePath missed (multi-select change-port)
            if (Time.frameCount >= fillColorFrame) { fillColorFrame = Time.frameCount + 120; UpdateFillColors(); } // recolor on ownership change
        }
        catch { }
    }

    // Territory/area NAME labels: world-space TextMesh under the label roots. Move each onto the globe and
    // billboard it to the camera; hide the far hemisphere. (Kept active + whitelisted from HideFlat.)
    private static void RepositionLabels()
    {
        RepositionLabelRoot(labelAreaRoot);
        RepositionLabelRoot(labelProvincesRoot);
    }

    private static void RepositionLabelRoot(Transform? root)
    {
        if (root == null || GlobeRoot == null)
            return;
        Vector3 c = GlobeRoot.transform.position;
        float camDist = (lastCamPos - c).magnitude;
        float horizonCos = camDist > radius ? radius / camDist : 0.999f; // zoom-aware horizon
        Vector3 toCam = (lastCamPos - c).normalized;
        int n = root.childCount;
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = root.GetChild(i);
                int id = ch.GetInstanceID();
                // Cache the ORIGINAL flat position; reprojecting from the live (already-moved) position each
                // frame would drift every label toward the globe center (the clustering bug).
                if (!labelFlat.TryGetValue(id, out Vector3 flat))
                {
                    if (ch.position.sqrMagnitude < 1f) continue; // native hasn't positioned it yet
                    flat = ch.position; labelFlat[id] = flat;
                }
                Vector3 sphereLocal = WorldToSphereLocal(flat, radius * (LandLiftFactor + 0.03f)); // float clearly above so text doesn't sink into the globe
                if (Vector3.Dot(sphereLocal.normalized, toCam) < horizonCos + 0.02f) { ch.gameObject.SetActive(false); continue; } // hide past the (zoom-aware) horizon
                if (!ch.gameObject.activeSelf) ch.gameObject.SetActive(true);
                Vector3 w = c + sphereLocal;
                ch.position = w;
                // Face the camera with WORLD up so the text stays upright instead of rolling with the globe.
                ch.rotation = Quaternion.LookRotation(w - lastCamPos, Vector3.up);
            }
            catch { }
        }
    }

    // Invasion / denial-zone / minesweep circles: the game draws them as flat zone meshes with a special
    // shader that won't render on our reprojected geometry. Instead DRAW OUR OWN ring per zone — sample its
    // flat center + radius (from the renderer bounds) and reproject a circle of points onto the sphere. The
    // radius stays true to the game's, so the naval-invasion ring still tells you what's inside it.
    private static int CountActiveChildren(Transform? root)
    {
        if (root == null) return 0;
        int n = 0, c = root.childCount;
        for (int i = 0; i < c; i++) { try { if (root.GetChild(i).gameObject.activeSelf) n++; } catch { } }
        return n;
    }

    private static void BuildZoneRings()
    {
        if (GlobeRoot == null) return;
        int count = CountActiveChildren(denialZoneRoot) + CountActiveChildren(mineSweepRoot);
        if (zoneRingObj != null && count == lastZoneCount && Time.frameCount < zoneRingFrame) return; // rebuild on change or periodically
        lastZoneCount = count;
        zoneRingFrame = Time.frameCount + 120;

        // Group ring segments by the zone's OWN color so each ring inherits its zone's tint (an invasion zone
        // colored like its diamond -> the ring matches; mine/denial zones keep their color).
        var byColor = new Dictionary<Color, List<Vector3>>();
        AddZoneRings(denialZoneRoot, byColor);
        AddZoneRings(mineSweepRoot, byColor);

        if (zoneRingObj != null) UnityEngine.Object.Destroy(zoneRingObj);
        zoneRingObj = null;
        if (byColor.Count == 0) return;
        zoneRingObj = new GameObject("UADVP_GlobeZoneRings");
        zoneRingObj.layer = GlobeRoot.layer;
        zoneRingObj.transform.SetParent(GlobeRoot.transform, false);
        if (!zoneColorLogged) { zoneColorLogged = true; foreach (KeyValuePair<Color, List<Vector3>> kv in byColor) Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE zone-ring color=({kv.Key.r:0.00},{kv.Key.g:0.00},{kv.Key.b:0.00}) segs={kv.Value.Count / 2}"); }
        foreach (KeyValuePair<Color, List<Vector3>> kv in byColor)
        {
            List<Vector3> vlist = kv.Value;
            if (vlist.Count < 2) continue;
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vlist.ToArray();
            var ind = new int[vlist.Count];
            for (int i = 0; i < ind.Length; i++) ind[i] = i; // consecutive pairs = line segments
            mesh.SetIndices(ind, MeshTopology.Lines, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 4f));
            var go = new GameObject("ring");
            go.layer = GlobeRoot.layer;
            go.transform.SetParent(zoneRingObj.transform, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = CreateUnlitColorMaterial(kv.Key, "UADVP_GlobeZoneRing_Mat");
        }
    }

    // Adds each active zone's ring (sampled center+radius, reprojected) into the per-color segment lists.
    private static void AddZoneRings(Transform? root, Dictionary<Color, List<Vector3>> byColor)
    {
        if (root == null || GlobeRoot == null) return;
        Vector3 c = GlobeRoot.transform.position;
        int n = root.childCount;
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = root.GetChild(i);
                if (!ch.gameObject.activeSelf) continue;
                Renderer r = ch.GetComponentInChildren<Renderer>();
                if (r == null) continue;
                Bounds b = r.bounds;             // flat world AABB of the zone circle
                Vector3 center = b.center;
                float rad = Mathf.Max(b.extents.x, b.extents.z);
                if (rad < 0.05f) continue;

                Color col = ZoneRingColor;       // tint = the zone's own material color (quantized so float noise groups)
                try { Material m = r.sharedMaterial; if (m != null) { Color mc = m.color; if (mc.r + mc.g + mc.b > 0.05f) col = mc; } } catch { }
                col = new Color(Mathf.Round(col.r * 16f) / 16f, Mathf.Round(col.g * 16f) / 16f, Mathf.Round(col.b * 16f) / 16f, 1f);
                if (!byColor.TryGetValue(col, out List<Vector3>? list)) { list = new List<Vector3>(); byColor[col] = list; }

                const int N = 48;
                Vector3 prev = default;
                for (int s = 0; s <= N; s++)
                {
                    float a = s / (float)N * Mathf.PI * 2f;
                    Vector3 rim = new(center.x + Mathf.Cos(a) * rad, center.y, center.z + Mathf.Sin(a) * rad);
                    Vector3 sp = c + WorldToSphereLocal(rim, radius * (LandLiftFactor + 0.006f));
                    if (s > 0) { list.Add(prev); list.Add(sp); }
                    prev = sp;
                }
            }
            catch { }
        }
    }

    private static void EnsureProvinceBattlesRoot()
    {
        if (provinceBattlesRoot != null) return;
        try
        {
            var all = CampaignMap.Instance != null ? CampaignMap.Instance.transform.root.GetComponentsInChildren<Transform>(true) : null;
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    try { if (all[i].gameObject.name == "ProvinceBattles") { provinceBattlesRoot = all[i]; return; } } catch { }
        }
        catch { }
    }

    // Reproject the GAME'S OWN battle-arrow objects onto the globe (native look + keeps their hover/interaction):
    //  - province_battle_line_* (LineRenderer): reproject its points as a great-circle arc (cache flat verts so
    //    re-running stays idempotent).
    //  - province_battle_flag_* (disc) / province_battle_end_* (arrowhead sprite child): move onto the sphere.
    // Renderers are re-enabled in case HideFlat's build-time sweep disabled them.
    private static void ReprojectBattleElements()
    {
        if (GlobeRoot == null) return;
        EnsureProvinceBattlesRoot();
        if (provinceBattlesRoot == null) return;
        Vector3 c = GlobeRoot.transform.position;
        int n = provinceBattlesRoot.childCount;
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = provinceBattlesRoot.GetChild(i);
                if (!ch.gameObject.activeSelf) continue;
                foreach (Renderer rr in ch.GetComponentsInChildren<Renderer>(true)) { try { if (!rr.enabled) rr.enabled = true; } catch { } }

                LineRenderer lr = ch.GetComponent<LineRenderer>();
                if (lr != null)
                {
                    int id = ch.GetInstanceID();
                    battleLineFlat.TryGetValue(id, out Vector3[]? flat);
                    if (flat == null)
                    {
                        int pc = lr.positionCount;
                        if (pc < 2) continue;
                        bool ws0 = true; try { ws0 = lr.useWorldSpace; } catch { }
                        var f = new Vector3[pc];
                        bool any = false;
                        for (int p = 0; p < pc; p++) { Vector3 fp = lr.GetPosition(p); fp = ws0 ? fp : ch.TransformPoint(fp); f[p] = fp; if (fp.sqrMagnitude > 1f) any = true; }
                        if (!any) continue; // not positioned yet this frame
                        flat = f; battleLineFlat[id] = flat;
                        try { lr.useWorldSpace = true; } catch { }
                    }
                    var pts = new List<Vector3>();
                    for (int p = 0; p + 1 < flat.Length; p++)
                    {
                        Vector3 A = WorldToSphereLocal(flat[p], radius * (LandLiftFactor + 0.006f));
                        Vector3 B = WorldToSphereLocal(flat[p + 1], radius * (LandLiftFactor + 0.006f));
                        int steps = Mathf.Clamp((int)(Vector3.Angle(A, B) / 3f), 1, 16);
                        for (int s = 0; s < steps; s++) pts.Add(c + Vector3.Slerp(A, B, s / (float)steps));
                    }
                    pts.Add(c + WorldToSphereLocal(flat[flat.Length - 1], radius * (LandLiftFactor + 0.006f)));
                    lr.positionCount = pts.Count;
                    for (int p = 0; p < pts.Count; p++) lr.SetPosition(p, pts[p]);
                }
                else
                {
                    int id = ch.GetInstanceID();
                    if (!battleMarkerFlat.TryGetValue(id, out Vector3 flat))
                    {
                        if (ch.position.sqrMagnitude < 1f) continue;
                        flat = ch.position; battleMarkerFlat[id] = flat;
                    }
                    Vector3 sl = WorldToSphereLocal(flat, radius * (LandLiftFactor + 0.007f));
                    ch.position = c + sl;
                    ch.rotation = Quaternion.FromToRotation(Vector3.up, sl.normalized); // lie flat on the sphere
                }
            }
            catch { }
        }
    }

    // Reposition the native attacker FLAG disc (province_battle_flag_*) onto the globe at the arrow origin —
    // it's the visible "tail"/source marker (and the native hover object). Renderer re-enabled (HideFlat may
    // have disabled it). Flat pos cached so re-running is idempotent.
    private static void ReprojectBattleFlags()
    {
        if (GlobeRoot == null) return;
        EnsureProvinceBattlesRoot();
        if (provinceBattlesRoot == null) return;
        Vector3 c = GlobeRoot.transform.position;
        int n = provinceBattlesRoot.childCount;
        int fa = 0, fm = 0; float firstMag = -1f; bool firstRend = false;
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = provinceBattlesRoot.GetChild(i);
                if (!ch.gameObject.activeSelf) continue;
                if (!ch.gameObject.name.Contains("_flag")) continue;
                fa++;
                foreach (Renderer rr in ch.GetComponentsInChildren<Renderer>(true)) { try { if (!rr.enabled) rr.enabled = true; } catch { } }
                int id = ch.GetInstanceID();
                if (!battleMarkerFlat.TryGetValue(id, out Vector3 flat))
                {
                    if (ch.position.sqrMagnitude < 1f) continue;
                    flat = ch.position; battleMarkerFlat[id] = flat;
                }
                Vector3 sl = WorldToSphereLocal(flat, radius * (LandLiftFactor + 0.008f));
                ch.position = c + sl;
                ch.rotation = Quaternion.FromToRotation(Vector3.up, sl.normalized);
                fm++;
                if (firstMag < 0f) { firstMag = (ch.position - c).magnitude; try { Renderer r0 = ch.GetComponentInChildren<Renderer>(); firstRend = r0 != null && r0.enabled; } catch { } }
            }
            catch { }
        }
        if (Time.frameCount >= battleDiagFrame) { battleDiagFrame = Time.frameCount + 180; Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE battle-flags active={fa} moved={fm} firstPosMag={firstMag:0.0} (globeR~{radius * LandLiftFactor:0.0}) firstRendEn={firstRend}"); }
    }

    // Drive the GAME'S OWN land-battle tooltip (CampaignProvinceBattlePopupUI.Show/Hide) when the cursor is
    // over a battle's reprojected flag on the globe. The flag GameObject is named province_battle_flag_<id>,
    // and <id> is exactly the key Show() expects. The popup self-positions (follows the cursor) in its Update.
    private static void HandleBattleHover()
    {
        try
        {
            if (GlobeRoot == null) return;
            EnsureProvinceBattlesRoot();
            if (provinceBattlesRoot == null) return;
            if (battlePopup == null)
            {
                try { Ui ui = UnityEngine.Object.FindObjectOfType<Ui>(); if (ui != null) battlePopup = ui.ProvinceBattlePopupElement; } catch { }
                if (battlePopup == null) return;
            }
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 mouse = Input.mousePosition;
            Vector3 gc = GlobeRoot.transform.position;
            Vector3 camDir = (cam.transform.position - gc).normalized;
            string hoverId = "";
            float best = 38f * 38f; // px^2 pick radius
            const string pfx = "province_battle_flag_";
            int n = provinceBattlesRoot.childCount;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    Transform ch = provinceBattlesRoot.GetChild(i);
                    if (!ch.gameObject.activeSelf) continue;
                    string nm = ch.gameObject.name;
                    if (!nm.StartsWith(pfx)) continue;
                    Vector3 wp = ch.position;
                    if (Vector3.Dot((wp - gc).normalized, camDir) < 0f) continue; // on the far side of the globe
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0f) continue;
                    float d = (sp.x - mouse.x) * (sp.x - mouse.x) + (sp.y - mouse.y) * (sp.y - mouse.y);
                    if (d < best) { best = d; hoverId = nm.Substring(pfx.Length); }
                }
                catch { }
            }
            // also pick when the cursor is over the arrow SHAFT (province_battle_line_*), not just the flag
            const string lpfx = "province_battle_line_";
            for (int i = 0; i < n; i++)
            {
                try
                {
                    Transform ch = provinceBattlesRoot.GetChild(i);
                    if (!ch.gameObject.activeSelf) continue;
                    string nm = ch.gameObject.name;
                    if (!nm.StartsWith(lpfx)) continue;
                    LineRenderer lr = ch.GetComponent<LineRenderer>();
                    if (lr == null) continue;
                    int pc = lr.positionCount; if (pc < 2) continue;
                    bool ws = true; try { ws = lr.useWorldSpace; } catch { }
                    Vector2 prevS = default; bool prevOk = false;
                    for (int p = 0; p < pc; p++)
                    {
                        Vector3 fp = lr.GetPosition(p); if (!ws) fp = ch.TransformPoint(fp);
                        Vector3 wp = gc + WorldToSphereLocal(fp, radius * (LandLiftFactor + 0.007f));
                        bool near = Vector3.Dot((wp - gc).normalized, camDir) >= 0f;
                        Vector3 sp = cam.WorldToScreenPoint(wp);
                        Vector2 s = new(sp.x, sp.y);
                        bool ok = sp.z > 0f && near;
                        if (p > 0 && prevOk && ok)
                        {
                            float d = DistToSegment(new(mouse.x, mouse.y), prevS, s);
                            if (d * d < best) { best = d * d; hoverId = nm.Substring(lpfx.Length); }
                        }
                        prevS = s; prevOk = ok;
                    }
                }
                catch { }
            }
            if (hoverId.Length > 0)
            {
                // hoverId = the flag-name suffix (FANCY display key, e.g. "Serbia_Montenegro" — matches the Battles
                // dict key). But Show()/GetBattleFromUi want the ALL-LOWERCASE province-id form ("serbia_montenegro").
                // Resolve the battle by the fancy key, then rebuild the id from the province .Id (lowercase);
                // fall back to just lowercasing the suffix if the battle isn't found.
                ProvinceBattle pb = null!;
                try { foreach (var kv in ProvinceBattleManager.Battles) { if (kv.Key == hoverId) { pb = kv.Value; break; } } } catch { }
                // Show() wants the fancy Battles key LOWERCASED with spaces->underscores. The province .Id form
                // was wrong for some (e.g. "Western Poland" has .Id "poland" -> "poland_eastern_poland" != key).
                string showId = hoverId.ToLowerInvariant().Replace(' ', '_');
                try
                {
                    battlePopup.gameObject.SetActive(true);         // Show() may not self-activate
                    battlePopup.Show(showId);
                    try { RectTransform lr = battlePopup.LayoutRoot; if (lr != null) lr.position = mouse; } catch { } // pin to cursor
                }
                catch { }
                battleHoverShown = hoverId;
                if (hoverId != lastHoverLogged)
                {
                    lastHoverLogged = hoverId;
                    Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE battle-hover suffix='{hoverId}' pbFound={(pb != null)} showId='{showId}' shownFor='{battlePopup.shownFor}'");
                }
            }
            else if (battleHoverShown.Length > 0)
            {
                try { battlePopup.Hide(); } catch { }
                battleHoverShown = "";
            }
        }
        catch { }
    }

    // 2D distance from point p to segment [a,b] (screen space, for arrow-shaft hover picking).
    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f) return (p - a).magnitude;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }

    // Draw OUR OWN land-battle/invasion arrows on the globe as flat TRIANGLE RIBBONS lying on the sphere
    // (1px lines looked janky). Each battle's 'province_battle_line_*' is a flat LineRenderer; read its
    // endpoints, reproject as a great-circle arc, lay a widened ribbon along it, cap with a solid arrowhead.
    private static void BuildBattleArrows()
    {
        if (GlobeRoot == null) return;
        EnsureProvinceBattlesRoot();
        if (provinceBattlesRoot == null) return;
        int count = CountActiveChildren(provinceBattlesRoot);
        if (battleArrowObj != null && count == lastBattleCount && Time.frameCount < battleFrame) return;
        lastBattleCount = count;
        battleFrame = Time.frameCount + 90;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        Vector3 c = GlobeRoot.transform.position;
        float hw = radius * 0.0045f;  // ribbon half-width
        int n = provinceBattlesRoot.childCount;
        for (int i = 0; i < n; i++)
        {
            try
            {
                Transform ch = provinceBattlesRoot.GetChild(i);
                if (!ch.gameObject.activeSelf) continue;
                if (!ch.gameObject.name.Contains("_line")) continue; // the arrow shaft
                LineRenderer lr = ch.GetComponent<LineRenderer>();
                if (lr == null) continue;
                int pc = lr.positionCount;
                if (pc < 2) continue;
                bool ws = true; try { ws = lr.useWorldSpace; } catch { }

                // reproject endpoints -> sphere arc
                var pts = new List<Vector3>();
                for (int p = 0; p < pc; p++)
                {
                    Vector3 fp = lr.GetPosition(p);
                    if (!ws) fp = ch.TransformPoint(fp);
                    Vector3 sp = c + WorldToSphereLocal(fp, radius * (LandLiftFactor + 0.007f));
                    if (p > 0) { Vector3 A = pts[pts.Count - 1] - c, B = sp - c; int st = Mathf.Clamp((int)(Vector3.Angle(A, B) / 4f), 1, 12); for (int s = 1; s < st; s++) pts.Add(c + Vector3.Slerp(A, B, s / (float)st)); }
                    pts.Add(sp);
                }
                if (pts.Count < 2) continue;
                // shorten the shaft so the arrowhead sits at the tip without overlap
                float headLen = radius * 0.022f;
                Vector3 tip = pts[pts.Count - 1];
                Vector3 beforeTip = pts[pts.Count - 2];
                // ribbon along the arc (stop slightly before the tip)
                for (int p = 0; p + 1 < pts.Count; p++) AddRibbonQuad(verts, tris, c, pts[p], pts[p + 1], hw);
                // solid arrowhead triangle at the tip
                AddArrowhead(verts, tris, c, beforeTip, tip, headLen, hw * 3.2f);
                // fletching tail at the origin so the arrow reads as an arrow (sized clearly, ~arrowhead scale)
                if (pts.Count >= 2) AddTailFletch(verts, tris, c, pts[0], pts[1], radius * 0.03f, hw * 4f, hw * 1.4f);
            }
            catch { }
        }

        if (battleArrowObj != null) UnityEngine.Object.Destroy(battleArrowObj);
        battleArrowObj = null;
        if (verts.Count < 3) return;
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 4f));
        battleArrowObj = new GameObject("UADVP_GlobeBattleArrows");
        battleArrowObj.layer = GlobeRoot.layer;
        battleArrowObj.transform.SetParent(GlobeRoot.transform, false);
        battleArrowObj.AddComponent<MeshFilter>().mesh = mesh;
        Material amat = CreateUnlitColorMaterial(BattleArrowColor, "UADVP_GlobeBattleArrow_Mat");
        try { amat.renderQueue = 2600; } catch { } // over the fills/terrain
        battleArrowObj.AddComponent<MeshRenderer>().sharedMaterial = amat;
    }

    // A flat quad (2 tris) lying on the sphere tangent plane between A and B, widened ±hw perpendicular to the arc.
    private static void AddRibbonQuad(List<Vector3> verts, List<int> tris, Vector3 c, Vector3 A, Vector3 B, float hw)
    {
        try
        {
            Vector3 rA = (A - c).normalized, rB = (B - c).normalized;
            Vector3 dir = B - A;
            if (dir.sqrMagnitude < 1e-9f) return;
            Vector3 pA = Vector3.Cross(rA, dir).normalized * hw;
            Vector3 pB = Vector3.Cross(rB, dir).normalized * hw;
            int i0 = verts.Count;
            verts.Add(A - pA); verts.Add(A + pA); verts.Add(B + pB); verts.Add(B - pB);
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
        }
        catch { }
    }

    // Two short back-swept ribbons at the arrow's origin (fletching) so it reads as an arrow, tail->head.
    private static void AddTailFletch(List<Vector3> verts, List<int> tris, Vector3 c, Vector3 origin, Vector3 next, float len, float spread, float hw)
    {
        try
        {
            Vector3 radial = (origin - c).normalized;
            Vector3 f = next - origin;
            f -= Vector3.Dot(f, radial) * radial;
            if (f.sqrMagnitude < 1e-6f) return;
            f.Normalize();
            Vector3 side = Vector3.Cross(radial, f);
            float r = (origin - c).magnitude;
            Vector3 b1 = c + (origin - f * len + side * spread - c).normalized * r;
            Vector3 b2 = c + (origin - f * len - side * spread - c).normalized * r;
            AddRibbonQuad(verts, tris, c, origin, b1, hw);
            AddRibbonQuad(verts, tris, c, origin, b2, hw);
        }
        catch { }
    }

    private static void AddArrowhead(List<Vector3> verts, List<int> tris, Vector3 c, Vector3 before, Vector3 tip, float len, float halfW)
    {
        try
        {
            Vector3 radial = (tip - c).normalized;
            Vector3 fwd = tip - before;
            fwd -= Vector3.Dot(fwd, radial) * radial; // tangent
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            Vector3 side = Vector3.Cross(radial, fwd);
            float r = (tip - c).magnitude;
            Vector3 baseC = tip - fwd * len;
            Vector3 w1 = c + (baseC + side * halfW - c).normalized * r;
            Vector3 w2 = c + (baseC - side * halfW - c).normalized * r;
            int it = verts.Count;
            verts.Add(tip); verts.Add(w1); verts.Add(w2);
            tris.Add(it); tris.Add(it + 1); tris.Add(it + 2);
        }
        catch { }
    }

    private static void ReprojectCircles()
    {
        if (!circleProbed)
        {
            circleProbed = true;
            try
            {
                Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE circle roots: mineSweep={CircleInfo(mineSweepRoot)} denial={CircleInfo(denialZoneRoot)} events={CircleInfo(eventsRoot)}");
            }
            catch { }
        }
        ReprojectCircleRoot(mineSweepRoot);
        ReprojectCircleRoot(denialZoneRoot);
        ReprojectCircleRoot(eventsRoot);
    }

    private static string CircleInfo(Transform? t)
    {
        if (t == null) return "null";
        int lr = 0; try { lr = t.GetComponentsInChildren<LineRenderer>(true).Length; } catch { }
        return $"{t.name}(children={t.childCount},lines={lr})";
    }

    private static void ReprojectCircleRoot(Transform? root)
    {
        if (root == null || GlobeRoot == null)
            return;
        try
        {
            var lines = root.GetComponentsInChildren<LineRenderer>(true);
            Vector3 c = GlobeRoot.transform.position;
            float r = radius * (LandLiftFactor + 0.006f);
            for (int li = 0; li < lines.Length; li++)
            {
                LineRenderer line = lines[li];
                if (line == null) continue;
                int n = line.positionCount;
                if (n < 2) continue;
                IntPtr key = line.Pointer;
                if (!circleFlat.TryGetValue(key, out Vector3[]? flat) || flat == null || flat.Length != n)
                {
                    flat = new Vector3[n];
                    for (int i = 0; i < n; i++) flat[i] = line.GetPosition(i);
                    circleFlat[key] = flat;
                }
                try { line.useWorldSpace = true; } catch { }
                for (int i = 0; i < n; i++) line.SetPosition(i, c + WorldToSphereLocal(flat[i], r));
            }
        }
        catch { }
    }

    // OnClickDetected handling in globe mode: a drag is a camera rotate (suppress the order); a clean click
    // is TRANSLATED to the flat-map point under the cursor on the globe so the native order/select lands
    // where you clicked. Returns true to let native proceed (with `position` rewritten), false to suppress.
    internal static bool HandleGlobeClick(ref Vector3 position)
    {
        if (!GlobeMarkerActive) return true;   // not globe -> native as-is
        if (dragOccurred) return false;        // drag-release = camera rotate, not an order/select
        if (TryGlobeClickToFlat(out Vector3 flat)) { position = flat; return true; }
        return false;                          // clicked off the globe -> ignore
    }

    private static bool TryGlobeClickToFlat(out Vector3 flat)
    {
        flat = Vector3.zero;
        try
        {
            Cam cam = Cam.Instance;
            if (cam == null || cam.cameraComp == null || GlobeRoot == null) return false;
            Ray ray = cam.cameraComp.ScreenPointToRay(Input.mousePosition);
            Vector3 c = GlobeRoot.transform.position;
            float R = radius * LandLiftFactor;
            Vector3 oc = ray.origin - c;
            float b = Vector3.Dot(oc, ray.direction);
            float cc = Vector3.Dot(oc, oc) - R * R;
            float disc = b * b - cc;
            if (disc < 0f) return false;        // ray missed the globe
            float t = -b - Mathf.Sqrt(disc);
            if (t < 0f) t = -b + Mathf.Sqrt(disc);
            if (t < 0f) return false;
            flat = WorldFromSphereLocal(ray.origin + ray.direction * t - c);
            return true;
        }
        catch { return false; }
    }

    // Inverse of WorldToSphereLocal: a sphere position (GlobeRoot-local) -> the flat-map world point.
    private static Vector3 WorldFromSphereLocal(Vector3 local)
    {
        Vector3 n = local.normalized;
        float latRad = Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f));
        float lonRad = Mathf.Atan2(-n.x, n.z);   // matches the negated X in LatLonToUnit
        float gN = MercY(LandLatNorthDeg * Mathf.Deg2Rad), gS = MercY(LandLatSouthDeg * Mathf.Deg2Rad);
        float fz = Mathf.Abs(gS - gN) < 1e-6f ? 0.5f : Mathf.Clamp01((MercY(latRad) - gN) / (gS - gN));
        float lonU = calK != 0f ? lonRad / calK : lonRad;
        float P = cll00.z * (1f - fz) + cll01.z * fz;
        float Q = (cll10.z - cll00.z) * (1f - fz) + (cll11.z - cll01.z) * fz;
        float fx = Mathf.Abs(Q) < 1e-5f ? 0.5f : Mathf.Clamp01((lonU - P) / Q);
        return new Vector3(Mathf.Lerp(calMinX, calMaxX, fx), calCenterY, Mathf.Lerp(calMinZ, calMaxZ, fz));
    }

    private static void RepositionMarkers(Il2CppSystem.Collections.Generic.List<CampaignMapElement>? list)
    {
        if (list == null)
            return;
        int count; try { count = list.Count; } catch { return; }
        for (int i = 0; i < count; i++)
        {
            CampaignMapElement e; try { e = list[i]; } catch { continue; }
            if (e == null)
                continue;
            float s; try { s = markerScale.TryGetValue(e.Pointer, out float sc) ? sc : 1f; } catch { s = 1f; }
            try { e.UpdatePositionScale(Vector3.zero, s); } catch { }
        }
    }

    internal static bool GlobeMarkerActive
        => ModSettings.CampaignGlobeEnabled && GlobeRoot != null && GameManager.IsWorldMap && CampaignMap.Instance != null;

    // Remember the native (intended) scale for a marker, so per-frame repositioning keeps its real size.
    internal static void RememberMarkerScale(IntPtr ptr, float scale)
    {
        try { if (scale > 0.001f) markerScale[ptr] = scale; } catch { }
    }

    // Rewrite a flat marker's UI position to its position ON the globe. scale=0 hides the far hemisphere.
    internal static void ProjectMarkerToGlobe(Vector3 worldPos, ref Vector3 uiPos, ref float scale)
    {
        try
        {
            CampaignMap map = CampaignMap.Instance;
            if (map == null || GlobeRoot == null) return;
            MapUI mapUi = map.UIMap;
            if (mapUi == null) return;
            Vector3 sphereLocal = WorldToSphereLocal(worldPos, radius * LandLiftFactor);
            // Hide anything past the visible HORIZON (markers are UI with no depth test, so far-side ones bleed
            // through). The horizon cosine is zoom-dependent: cos(horizon) = radius / cameraDistance — a fixed
            // threshold left near-but-over-the-bulge markers showing when zoomed in. Off-screen + scale 0.
            Vector3 toCam = lastCamPos - GlobeRoot.transform.position;
            float camDist = toCam.magnitude;
            float horizonCos = camDist > radius ? radius / camDist : 0.999f;
            float cos = Vector3.Dot(sphereLocal.normalized, toCam.normalized);
            if (cos < horizonCos + 0.02f) { scale = 0f; uiPos = new Vector3(-99999f, -99999f, 0f); return; }
            Vector3 sphereWorld = GlobeRoot.transform.position + sphereLocal;
            uiPos = mapUi.WorldToUISpace(mapUi.UICanvas, sphereWorld);
            if (markerScaleLog < 12) { markerScaleLog++; Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE marker native scale={scale:0.###} dist={dist:0}"); }
            scale = Mathf.Min(scale, MaxMarkerScale); // keep the game's marker size, just cap the zoom-out balloon
        }
        catch { }
    }

    // Reproject a task-force route line onto the globe as a GREAT-CIRCLE arc: each native waypoint is
    // projected to the sphere, and consecutive waypoints are slerped (constant radius) so the segment
    // hugs the surface. World-space LineRenderer, so it stays on the globe as the camera orbits.
    internal static void ProjectRouteToGlobe(LineRenderer line)
    {
        try
        {
            if (GlobeRoot == null || line == null)
                return;
            int n = line.positionCount;
            if (Time.frameCount >= routeLogFrame) { routeLogFrame = Time.frameCount + 20; Vector3 f0 = n > 0 ? line.GetPosition(0) : Vector3.zero; bool ws0 = true; try { ws0 = line.useWorldSpace; } catch { } Melon<UADVanillaPlusMod>.Logger.Msg($"UADVP_GLOBE route-reproj n={n} ws={ws0} first={f0}"); }
            if (n < 2)
                return;
            Vector3 c = GlobeRoot.transform.position;
            float routeR = radius * (LandLiftFactor + 0.004f); // just above the land shell

            // Read waypoints in WORLD space (a route line may be local-space), then force world space so
            // our sphere positions apply correctly.
            bool wasWorld = true; try { wasWorld = line.useWorldSpace; } catch { }
            Transform lt = line.transform;
            var flat = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = line.GetPosition(i);
                flat[i] = wasWorld ? p : lt.TransformPoint(p);
            }
            try { line.useWorldSpace = true; } catch { }

            var pts = new List<Vector3>();
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 a = WorldToSphereLocal(flat[i], routeR);
                Vector3 b = WorldToSphereLocal(flat[i + 1], routeR);
                int steps = Mathf.Clamp((int)(Vector3.Angle(a, b) / 3f), 1, 24); // denser for longer arcs
                for (int s = 0; s < steps; s++)
                    pts.Add(c + Vector3.Slerp(a, b, s / (float)steps));
            }
            pts.Add(c + WorldToSphereLocal(flat[n - 1], routeR));

            line.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++)
                line.SetPosition(i, pts[i]);
        }
        catch { }
    }

    // Catch-all: reproject any route line still sitting FLAT (its first point isn't on the sphere shell), so
    // routes set by paths that don't go through SetRoutePath (e.g. Fleet multi-select Change Port) still curve
    // onto the globe. Idempotent: lines already on the shell (distance ~radius from center) are skipped.
    internal static void ReprojectFlatRoutes()
    {
        if (GlobeRoot == null) return;
        try
        {
            CampaignMap map = CampaignMap.Instance;
            MapUI? ui = map != null ? map.UIMap : null;
            Transform? root = ui != null ? ui.RouteLineRoot : null;
            if (root == null) return;
            Vector3 c = GlobeRoot.transform.position;
            float lo = radius * 0.85f, hi = radius * 1.2f;
            var lines = root.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < lines.Length; i++)
            {
                try
                {
                    LineRenderer lr = lines[i];
                    if (lr == null || lr.positionCount < 2) continue;
                    bool ws = true; try { ws = lr.useWorldSpace; } catch { }
                    Vector3 p0 = lr.GetPosition(0); if (!ws) p0 = lr.transform.TransformPoint(p0);
                    float d = (p0 - c).magnitude;
                    if (d > lo && d < hi) continue; // already reprojected onto the sphere shell
                    ProjectRouteToGlobe(lr);        // still flat -> reproject
                }
                catch { }
            }
        }
        catch { }
    }

    // Reproject any routes already active when Globe mode is entered.
    // Orbit the globe so a flat-map world point is centered in view (used when a mission-list entry is clicked).
    // Inverse of DriveOrbitCamera's q*back = camDir: camDir must equal the point's sphere direction.
    internal static void FocusGlobeOn(Vector3 flatWorld)
    {
        try
        {
            if (GlobeRoot == null) return;
            Vector3 n = WorldToSphereLocal(flatWorld, radius).normalized;
            pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg, -85f, 85f);
            yaw = Mathf.Atan2(-n.x, -n.z) * Mathf.Rad2Deg;
        }
        catch { }
    }

    internal static void ReprojectExistingRoutes(CampaignMap map)
    {
        try
        {
            MapUI? ui = map != null ? map.UIMap : null;
            Transform? root = ui != null ? ui.RouteLineRoot : null;
            if (root == null)
                return;
            var lines = root.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i] != null)
                    ProjectRouteToGlobe(lines[i]);
        }
        catch { }
    }

    internal static bool IsUnder(Transform? t, Transform? ancestor)
    {
        if (ancestor == null) return false;
        try
        {
            Transform? cur = t;
            while (cur != null) { if (cur == ancestor) return true; cur = cur.parent; }
        }
        catch { }
        return false;
    }

    private static void TrySetActive(Transform? t, bool active)
    {
        try { if (t != null) t.gameObject.SetActive(active); } catch { }
    }

    // Read the renderer's effective political color: per-renderer MaterialPropertyBlock first (some fills
    // are tinted via MPB, not the material), then the instanced material's _Color/_BaseColor/.color.
    private static Color GetMaterialColor(MeshRenderer src)
    {
        try
        {
            var mpb = new MaterialPropertyBlock();
            src.GetPropertyBlock(mpb);
            if (!mpb.isEmpty)
            {
                try { Color c = mpb.GetColor("_Color"); if (c.r + c.g + c.b + c.a > 0.01f) return Opaque(c); } catch { }
                try { Color c = mpb.GetColor("_BaseColor"); if (c.r + c.g + c.b + c.a > 0.01f) return Opaque(c); } catch { }
            }
        }
        catch { }

        Material? m = null;
        try { m = src.material; } catch { }
        if (m != null)
        {
            try { if (m.HasProperty("_Color")) return Opaque(m.GetColor("_Color")); } catch { }
            try { if (m.HasProperty("_BaseColor")) return Opaque(m.GetColor("_BaseColor")); } catch { }
            try { return Opaque(m.color); } catch { }
        }
        return FallbackLand;
    }

    private static Color Opaque(Color c) { c.a = 1f; return c; }

    // Force a (translucent overlay) material to render fully opaque on the globe — covers Standard + URP.
    private static void ForceOpaque(Material m)
    {
        try { Color c = m.color; c.a = 1f; m.color = c; } catch { }
        try { if (m.HasProperty("_Color")) { Color c = m.GetColor("_Color"); c.a = 1f; m.SetColor("_Color", c); } } catch { }
        try { if (m.HasProperty("_BaseColor")) { Color c = m.GetColor("_BaseColor"); c.a = 1f; m.SetColor("_BaseColor", c); } } catch { }
        try { if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f); } catch { }
        try { if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f); } catch { }
        try { if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One); } catch { }
        try { if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero); } catch { }
        try { if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1); } catch { }
        try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { } // double-sided: fills were inside-out (near side culled)
        try { m.DisableKeyword("_ALPHABLEND_ON"); } catch { }
        try { m.DisableKeyword("_ALPHAPREMULTIPLY_ON"); } catch { }
        try { m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
        try { m.renderQueue = 2000; } catch { }
    }

    private static Material CreateUnlitColorMaterial(Color color, string name)
    {
        Shader shader = FindFirstShader("Unlit/Color", "Legacy Shaders/Unlit/Color", "Sprites/Default", "Standard");
        Material m = new(shader) { name = name };
        try { m.color = color; } catch { }
        try { if (m.HasProperty("_Color")) m.SetColor("_Color", color); } catch { }
        try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color); } catch { }
        try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { } // double-sided
        return m;
    }

    private static Shader FindFirstShader(params string[] names)
    {
        foreach (string n in names)
        {
            Shader s = Shader.Find(n);
            if (s != null) return s;
        }
        return Shader.Find("Standard");
    }
}

// Skip the native flat-bounds clamp while Globe mode is active (coexists with the Disc prefix; each
// early-returns true outside its own mode).
[HarmonyPatch(typeof(Cam), "CheckCameraBorders")]
internal static class CampaignGlobeCameraBoundsPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Cam __instance)
    {
        if (ModSettings.CampaignGlobeEnabled && GameManager.IsWorldMap
            && CampaignMap.Instance != null && CampaignGlobeVisualPatch.GlobeRoot != null)
        {
            CampaignGlobeVisualPatch.DriveOrbitCamera(__instance);
            return false; // skip native flat clamp
        }
        return true;
    }
}

// Drive the orbit AFTER native Update so our camera transform is not overwritten.
[HarmonyPatch(typeof(Cam), "Update")]
internal static class CampaignGlobeCameraDrivePatch
{
    [HarmonyPostfix]
    private static void Postfix(Cam __instance)
    {
        if (!ModSettings.CampaignGlobeEnabled || !GameManager.IsWorldMap || CampaignMap.Instance == null)
            return;
        if (Input.GetKeyDown(KeyCode.F9)) CampaignGlobeVisualPatch.DumpGlobeState(); // on-demand state dump for diagnosis
        CampaignGlobeVisualPatch.EnsureGlobe(); // rebuild if it went missing (e.g., after a 3D battle)
        if (CampaignGlobeVisualPatch.GlobeRoot != null)
        {
            CampaignGlobeVisualPatch.DriveOrbitCamera(__instance);
            CampaignGlobeVisualPatch.RuntimeUpdate();
            CampaignGlobeVisualPatch.MaybeRebuildLand();
        }
    }
}

// Relocate map markers onto the globe: in Globe mode, rewrite the UI position the game computes for each
// marker to the projection of its WorldPos onto the sphere (far hemisphere hidden via scale 0). Prefix so
// it runs before the native apply; coexists with the Disc-mode wrap postfix on the same methods.
[HarmonyPatch(typeof(CampaignMapElement), nameof(CampaignMapElement.UpdatePositionScale))]
internal static class CampaignGlobeElementPositionPatch
{
    [HarmonyPrefix]
    private static void Prefix(CampaignMapElement __instance, ref Vector3 newPosition, ref float scale)
    {
        if (!CampaignGlobeVisualPatch.GlobeMarkerActive)
            return;
        Vector3 wp;
        try { wp = __instance.WorldPos; } catch { return; }
        CampaignGlobeVisualPatch.RememberMarkerScale(__instance.Pointer, scale);
        CampaignGlobeVisualPatch.ProjectMarkerToGlobe(wp, ref newPosition, ref scale);
    }
}

[HarmonyPatch(typeof(ShipUI), nameof(ShipUI.UpdatePositionScale))]
internal static class CampaignGlobeShipPositionPatch
{
    [HarmonyPrefix]
    private static void Prefix(ShipUI __instance, ref Vector3 newPosition, ref float scale)
    {
        if (!CampaignGlobeVisualPatch.GlobeMarkerActive)
            return;
        CampaignMapElement? elem = __instance.TryCast<CampaignMapElement>();
        if (elem == null)
            return;
        Vector3 wp;
        try { wp = elem.WorldPos; } catch { return; }
        CampaignGlobeVisualPatch.RememberMarkerScale(elem.Pointer, scale);
        CampaignGlobeVisualPatch.ProjectMarkerToGlobe(wp, ref newPosition, ref scale);
    }
}

// When the game (re)sets a task-force route's path, reproject the line onto the globe as a great-circle arc.
[HarmonyPatch(typeof(MapUI), nameof(MapUI.SetRoutePath))]
internal static class CampaignGlobeRoutePatch
{
    [HarmonyPostfix]
    private static void Postfix(LineRenderer line)
    {
        if (!CampaignGlobeVisualPatch.GlobeMarkerActive)
            return;
        CampaignGlobeVisualPatch.ProjectRouteToGlobe(line);
    }
}

// Clicking a mission-list entry (battle/invasion) calls the native flat-camera focus, which on the globe only
// rescales icons (and disturbs routes). Redirect it: orbit the globe to center on the point, skip the native.
[HarmonyPatch(typeof(Cam), nameof(Cam.LookAtPoint))]
internal static class CampaignGlobeLookAtPointPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Il2CppSystem.Nullable<Vector3> point)
    {
        if (!CampaignGlobeVisualPatch.GlobeMarkerActive) return true;
        try { if (point.HasValue) CampaignGlobeVisualPatch.FocusGlobeOn(point.Value); } catch { }
        return false;
    }
}

[HarmonyPatch(typeof(Cam), nameof(Cam.LookAtPointEx))]
internal static class CampaignGlobeLookAtPointExPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Vector3 position)
    {
        if (!CampaignGlobeVisualPatch.GlobeMarkerActive) return true;
        try { CampaignGlobeVisualPatch.FocusGlobeOn(position); } catch { }
        return false;
    }
}

// Suppress the native click->move-order while rotating the globe (a drag-release or right-click would send
// selected ships to a flat coordinate). Priority.First so this bool prefix wins over the wrap-mode prefix
// on the same method; marker clicks still select via the UI EventSystem.
[HarmonyPatch(typeof(CampaignMap), nameof(CampaignMap.OnClickDetected))]
internal static class CampaignGlobeClickSuppressPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref Vector3 position)
        => CampaignGlobeVisualPatch.HandleGlobeClick(ref position);
}
