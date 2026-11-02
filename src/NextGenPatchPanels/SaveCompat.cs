using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2Cpp;
using UnityEngine;

namespace NextGenPatchPanels
{
    // Keeps saves vanilla on disk, the same way as Next-Gen Hardware. SaveGame builds its data from the live
    // devices in one synchronous call, so for the length of that call each trunk panel is switched back to the
    // vanilla panel it was built from (patchPanelType). The trunk panels are written to
    // "<save>.nextgenpatchpanels.json" next to the save, and LoadGame's result is re-mapped before anything
    // spawns. Without the mod the save loads with vanilla panels in those slots.
    //
    // Links need nothing extra: a link is the trunk cable, which the game saves like any other cable.
    internal static class SaveCompat
    {
        const string SidecarSuffix = ".nextgenpatchpanels.json";

        sealed class Sidecar
        {
            public int Version { get; set; } = 1;
            public List<Entry> Panels { get; set; } = new();
        }

        sealed class Entry
        {
            public string Id { get; set; }
            public string Key { get; set; }
            public int Twin { get; set; }
            public float[] Pos { get; set; }
        }

        static readonly List<(PatchPanel panel, TrunkType type)> Swapped = new();
        static bool _inSave;
        static string _saveName;

        // ---- saving ----

        public static void SaveGamePrefix(string __0)
        {
            try
            {
                _inSave = true;
                _saveName = string.IsNullOrEmpty(__0) ? null : __0;
                SwapToVanilla();
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"SaveGamePrefix: {e}"); }
        }

        // WriteMeta runs inside SaveGame and is the only place a manual save's file name appears.
        public static void WriteMetaPrefix(string __0)
        {
            if (!string.IsNullOrEmpty(__0)) _saveName = __0;
        }

        public static void SaveGamePostfix()
        {
            try
            {
                var sidecar = new Sidecar
                {
                    Panels = Swapped.Select(x => new Entry
                    {
                        Id = x.panel.patchPanelId,
                        Key = x.type.Key,
                        Twin = x.type.TwinIndex,
                        Pos = new[] { x.panel.transform.position.x, x.panel.transform.position.y, x.panel.transform.position.z },
                    }).ToList(),
                };
                Restore();
                WriteSidecar(_saveName, sidecar);

                // Only tidy up once we know SaveDir() really is where the game writes saves.
                var savePath = Path.Combine(SaveDir(), BaseName(_saveName) ?? "") + ".save";
                if (File.Exists(savePath))
                    DeleteOrphanSidecars();
                else
                    NextGenPatchPanelsMod.Log.Warning($"Expected the save at {savePath} but it isn't there; sidecars may be in the wrong folder.");
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"SaveGamePostfix: {e}"); }
            finally { _inSave = false; }
        }

        static void SwapToVanilla()
        {
            Swapped.Clear();
            if (!Templates.Ready) return;
            var panels = Resources.FindObjectsOfTypeAll<PatchPanel>();
            for (int i = 0; i < panels.Length; i++)
            {
                var p = panels[i];
                if (p == null || string.IsNullOrEmpty(p.gameObject.scene.name)) continue; // templates and prefabs
                var t = Templates.Of(p);
                if (t == null) continue;
                Swapped.Add((p, t));
                p.patchPanelType = t.TwinIndex;
            }
        }

        static void Restore()
        {
            foreach (var (p, t) in Swapped)
                if (p != null) p.patchPanelType = t.Index;
            Swapped.Clear();
        }

        // Safety net: if SaveGame threw, its postfix never ran and panels are still swapped.
        public static void RestoreIfStuck()
        {
            if (_inSave || Swapped.Count == 0) return;
            NextGenPatchPanelsMod.Log.Warning($"Save did not finish normally; restoring {Swapped.Count} trunk panels.");
            Restore();
        }

        // ---- loading ----

        public static void LoadGamePostfix(string __0, SaveData __result)
        {
            try
            {
                if (__result == null) return;
                Trunks.Reset("save loading");
                NextGenPatchPanelsMod.Setup("save loading");
                if (!Templates.Ready) return;

                var sidecar = ReadSidecar(__0);
                var list = __result.networkData?.patchPanels;
                if (sidecar == null || list == null || sidecar.Panels == null || sidecar.Panels.Count == 0) return;

                var byId = sidecar.Panels.Where(e => !string.IsNullOrEmpty(e.Id)).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
                int restored = 0, stale = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var sd = list[i];
                    if (!byId.TryGetValue(sd.patchPanelID ?? "", out var e)) e = ByPosition(sidecar.Panels, sd.position);
                    if (e == null) continue;
                    var t = Templates.ByKey(e.Key);
                    // A different vanilla type means the panel was replaced (e.g. while the mod was removed).
                    if (t == null || sd.patchPanelType != t.TwinIndex) { stale++; continue; }
                    sd.patchPanelType = t.Index;
                    restored++;
                }
                NextGenPatchPanelsMod.Log.Msg($"Loaded '{__0}': restored {restored} trunk panels" + (stale > 0 ? $", skipped {stale} that no longer match" : "") + ".");
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"LoadGamePostfix: {e}"); }
        }

        static Entry ByPosition(List<Entry> entries, Vector3 pos)
        {
            foreach (var e in entries)
                if (e.Pos?.Length == 3 && (new Vector3(e.Pos[0], e.Pos[1], e.Pos[2]) - pos).sqrMagnitude < 0.0001f) return e;
            return null;
        }

        // ---- sidecar files ----

        static string SaveDir()
        {
            var dir = SaveSystem.saveDirPath;
            return string.IsNullOrEmpty(dir) ? Path.Combine(Application.persistentDataPath, "saves") : dir;
        }

        // Save names may arrive as bare names or paths; normalise to the bare name.
        static string BaseName(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;
            var name = Path.GetFileName(nameOrPath);
            return name.EndsWith(".save") || name.EndsWith(".meta") ? Path.GetFileNameWithoutExtension(name) : name;
        }

        static string SidecarPath(string saveName) => Path.Combine(SaveDir(), BaseName(saveName) + SidecarSuffix);

        static void WriteSidecar(string saveName, Sidecar sidecar)
        {
            if (string.IsNullOrEmpty(saveName))
            {
                if (sidecar.Panels.Count > 0)
                    NextGenPatchPanelsMod.Log.Error($"Could not tell which save file was written; {sidecar.Panels.Count} trunk panels not recorded.");
                return;
            }
            var path = SidecarPath(saveName);
            if (sidecar.Panels.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
            NextGenPatchPanelsMod.Log.Msg($"Saved '{BaseName(saveName)}' as vanilla; {sidecar.Panels.Count} trunk panels recorded in {path}.");
        }

        static Sidecar ReadSidecar(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return null;
            var path = SidecarPath(saveName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path));
        }

        static void DeleteOrphanSidecars()
        {
            var dir = SaveDir();
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.GetFiles(dir, "*" + SidecarSuffix))
            {
                var saveName = Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - SidecarSuffix.Length);
                if (!File.Exists(Path.Combine(dir, saveName + ".save")))
                    File.Delete(path);
            }
        }

        // Autosave rotation renames save files; keep the sidecar with its save.
        public static void MoveSaveFilesPostfix(string __0, string __1)
        {
            try
            {
                var from = SidecarPath(__0);
                if (string.IsNullOrEmpty(BaseName(__1)) || !File.Exists(from)) return;
                var to = SidecarPath(__1);
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"MoveSaveFilesPostfix('{__0}', '{__1}'): {e.Message}"); }
        }

        public static void DeleteSaveFilePostfix(string __0)
        {
            try
            {
                var path = SidecarPath(__0);
                if (BaseName(__0) != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"DeleteSaveFilePostfix('{__0}'): {e.Message}"); }
        }
    }
}
