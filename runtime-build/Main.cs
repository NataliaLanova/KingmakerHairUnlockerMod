using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityModManagerNet;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace HairUnlocker
{
    public static class Main
    {
        private const string HumanRaceGuid = "0a5d473ead98b0646b94495af250fdc4";
        private const string MainMenuSceneName = "MainMenu";

        private static UnityModManager.ModEntry.ModLogger logger;
        private static bool enabled;
        private static bool originalCaptured;
        private static object humanFemaleOptions;
        private static Array originalFemaleHair;
        private static bool applied;
        private static bool meshPatchInstalled;
        private static bool hairNamesReady;
        private static readonly HashSet<string> maleHairEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<MeshSwap> meshSwaps = new List<MeshSwap>();
        private static readonly List<ModifierSwap> modifierSwaps = new List<ModifierSwap>();
        private static bool skeletonModifierPatchInstalled;
        private static bool hairRigPatchInstalled;
        private static readonly List<object> activeMaleHairParts = new List<object>();

        private sealed class ModifierSwap
        {
            public IList Collection;
            public object[] Original;
        }

        private sealed class MeshSwap
        {
            public object Renderer;
            public object OriginalMesh;
            public object ClonedMesh;
        }

        private sealed class BindPoseCandidate
        {
            public object Value;
            public int Priority;
        }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                logger = modEntry.Logger;
                modEntry.OnToggle = OnToggle;
                modEntry.OnUpdate = OnUpdate;
                SceneManager.sceneLoaded += OnSceneLoaded;
                InstallHairRigPatch();
                Log("Loaded. Female human male hairstyles are retargeted during mesh assembly, including bones missing from the female rig.");
                return true;
            }
            catch (Exception ex)
            {
                LogError(ex);
                return false;
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            try
            {
                if (enabled)
                    Apply();
                else
                    Restore();
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!enabled) return;
            try
            {
                // Another mod may rebuild Human FemaleOptions during the main menu.
                // Re-apply only when one or more male hairstyle links are missing.
                Apply();
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!enabled || scene.name != MainMenuSceneName)
                return;

            try
            {
                Apply();
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void Apply()
        {
            if (!enabled)
                return;

            object game = GetStaticProperty(FindType("Kingmaker.Game"), "Instance");
            if (game == null)
                return;

            object blueprintRoot = ReadMember(game, "BlueprintRoot");
            object progression = ReadMember(blueprintRoot, "Progression");
            object races = ReadMember(progression, "CharacterRaces");
            IEnumerable enumerable = races as IEnumerable;
            if (enumerable == null)
            {
                Log("Could not enumerate CharacterRaces. Will retry on the next main-menu load.");
                return;
            }

            object humanRace = null;
            foreach (object race in enumerable)
            {
                if (race == null) continue;
                object guid = ReadMember(race, "AssetGuid");
                if (guid != null && string.Equals(guid.ToString(), HumanRaceGuid, StringComparison.OrdinalIgnoreCase))
                {
                    humanRace = race;
                    break;
                }
            }

            if (humanRace == null)
            {
                Log("Human race blueprint was not found. Check that this is Pathfinder: Kingmaker.");
                return;
            }

            object maleOptions = ReadMember(humanRace, "MaleOptions");
            object femaleOptions = ReadMember(humanRace, "FemaleOptions");
            if (maleOptions == null || femaleOptions == null)
            {
                Log("Human male/female customization options were not available yet; will retry.");
                return;
            }

            Array maleHair = ReadMember(maleOptions, "Hair") as Array;
            Array femaleHair = ReadMember(femaleOptions, "Hair") as Array;
            if (maleHair == null || femaleHair == null)
            {
                Log("Could not read the hair option arrays; will retry.");
                return;
            }

            CacheMaleHairEntityNames(maleHair);
            Array combined = CombineHair(femaleHair, maleHair);
            if (combined.Length == femaleHair.Length)
            {
                applied = true;
                return;
            }

            // Snapshot the current list immediately before extending it. This also
            // preserves changes made by the original Hair Unlocker or other mods.
            humanFemaleOptions = femaleOptions;
            originalFemaleHair = (Array)femaleHair.Clone();
            originalCaptured = true;

            if (!WriteMember(femaleOptions, "Hair", combined))
            {
                Log("Could not update Human FemaleOptions.Hair; this game version may store the list differently.");
                return;
            }

            applied = true;
            Log("Applied: human female characters can now select male human hairstyles (" +
                originalFemaleHair.Length + " current, " + combined.Length + " total).");
        }

        private static Array CombineHair(Array femaleHair, Array maleHair)
        {
            Type elementType = femaleHair.GetType().GetElementType();
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddLinks(femaleHair, result, seen);
            AddLinks(maleHair, result, seen);

            Array combined = Array.CreateInstance(elementType, result.Count);
            for (int i = 0; i < result.Count; i++)
                combined.SetValue(result[i], i);
            return combined;
        }

        private static void AddLinks(Array source, List<object> result, HashSet<string> seen)
        {
            foreach (object item in source)
            {
                if (item == null) continue;
                object assetId = ReadMember(item, "AssetId");
                string key = assetId == null ? item.ToString() : assetId.ToString();
                if (seen.Add(key))
                    result.Add(item);
            }
        }

        private static void Restore()
        {
            if (originalCaptured && humanFemaleOptions != null && originalFemaleHair != null)
            {
                if (WriteMember(humanFemaleOptions, "Hair", originalFemaleHair))
                    Log("Restored the previous human female hairstyle list.");
            }
            humanFemaleOptions = null;
            originalFemaleHair = null;
            originalCaptured = false;
            applied = false;
        }

        private static Type FindType(string fullName)
        {
            Type type = Type.GetType(fullName + ", Assembly-CSharp", false);
            if (type != null) return type;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static object GetStaticProperty(Type type, string name)
        {
            if (type == null) return null;
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property == null) return null;
            return property.GetValue(null, null);
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null)
            {
                MethodInfo getter = property.GetGetMethod(true);
                if (getter != null) return getter.Invoke(target, null);
            }
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field.GetValue(target);
            string backingName = "<" + name + ">k__BackingField";
            field = type.GetField(backingName, flags);
            if (field != null) return field.GetValue(target);
            return null;
        }

        private static bool WriteMember(object target, string name, object value)
        {
            if (target == null) return false;
            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null)
            {
                MethodInfo setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(target, new object[] { value });
                    return true;
                }
            }

            FieldInfo field = type.GetField("<" + name + ">k__BackingField", flags)
                ?? type.GetField(name, flags)
                ?? type.GetField("_" + char.ToLowerInvariant(name[0]) + name.Substring(1), flags)
                ?? type.GetField("m_" + name, flags);
            if (field != null && !field.IsInitOnly)
            {
                field.SetValue(target, value);
                return true;
            }
            return false;
        }


        // Install without a compile-time reference to Harmony so this patch can build
        // independently of the exact Harmony assembly used by the player's UMM install.
        private static void InstallBuildMeshPatch()
        {
            if (meshPatchInstalled) return;
            try
            {
                Type characterType = FindType("Kingmaker.Visual.CharacterSystem.Character");
                if (characterType == null)
                {
                    Log("Character type not found; mesh-bind repair is unavailable on this game build.");
                    return;
                }

                MethodInfo target = null;
                foreach (MethodInfo candidate in characterType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (candidate.Name != "BuildMesh") continue;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.Name.Contains("Dictionary"))
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target == null)
                {
                    Log("Character.BuildMesh was not found; hairstyle access will still work, but mesh-bind repair is unavailable.");
                    return;
                }

                Type harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                Type harmonyType = FindType("HarmonyLib.Harmony");
                bool harmonyV2 = harmonyType != null;
                if (harmonyType == null)
                    harmonyType = FindType("HarmonyLib.HarmonyInstance");
                if (harmonyMethodType == null || harmonyType == null)
                {
                    Log("Harmony API was not found; hairstyle access will still work, but mesh-bind repair is unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(Main).GetMethod("BuildMeshPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(Main).GetMethod("BuildMeshPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(Main).GetMethod("BuildMeshFinalizer", BindingFlags.Static | BindingFlags.NonPublic);
                object prefixHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
                object postfixHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { postfix });
                object finalizerHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { finalizer });

                object harmony;
                if (harmonyV2)
                {
                    harmony = Activator.CreateInstance(harmonyType, new object[] { "openai.human-female-male-hairstyles.bindpose" });
                }
                else
                {
                    MethodInfo create = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (create == null)
                    {
                        Log("HarmonyInstance.Create was not found; mesh-bind repair is unavailable.");
                        return;
                    }
                    harmony = create.Invoke(null, new object[] { "openai.human-female-male-hairstyles.bindpose" });
                }

                MethodInfo patchMethod = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "Patch")
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault(m => {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length >= 4 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
                    });
                if (patchMethod == null)
                {
                    Log("Harmony Patch method was not found; mesh-bind repair is unavailable.");
                    return;
                }

                ParameterInfo[] patchParameters = patchMethod.GetParameters();
                object[] args = new object[patchParameters.Length];
                args[0] = target;
                args[1] = prefixHarmony;
                args[2] = postfixHarmony;
                if (args.Length > 3) args[3] = null;
                if (args.Length > 4) args[4] = finalizerHarmony;
                patchMethod.Invoke(harmony, args);
                meshPatchInstalled = true;
                Log("Installed Character.BuildMesh mesh-bind repair.");
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }


        // Pathfinder's character mesh builder stops mapping a part at the first bone
        // name missing from the current rig. Male long-hair assets can contain bones
        // absent from the female rig; all later indices then retain the default map.
        // Patch EnsureBones for those hair parts and map each bone to the best matching
        // female bone (or nearest available ancestor), rebuilding bind poses against the
        // female character's current skeleton.
        private static void InstallHairRigPatch()
        {
            if (hairRigPatchInstalled) return;
            try
            {
                Type characterType = FindType("Kingmaker.Visual.CharacterSystem.Character");
                if (characterType == null)
                {
                    Log("Character type not found; cross-gender hair rig repair is unavailable.");
                    return;
                }

                MethodInfo buildMesh = null;
                foreach (MethodInfo candidate in characterType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (candidate.Name != "BuildMesh") continue;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.Name.Contains("Dictionary"))
                    {
                        buildMesh = candidate;
                        break;
                    }
                }

                MethodInfo ensureBones = characterType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "EnsureBones" && m.GetParameters().Length == 5);

                if (buildMesh == null || ensureBones == null)
                {
                    Log("BuildMesh or EnsureBones was not found; hairstyle access will work, but bone retargeting is unavailable on this game build.");
                    return;
                }

                Type harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                Type harmonyType = FindType("HarmonyLib.Harmony");
                bool harmonyV2 = harmonyType != null;
                if (harmonyType == null)
                    harmonyType = FindType("HarmonyLib.HarmonyInstance");

                if (harmonyMethodType == null || harmonyType == null)
                {
                    Log("Harmony API was not found; cross-gender hair rig repair is unavailable.");
                    return;
                }

                object harmony;
                if (harmonyV2)
                {
                    harmony = Activator.CreateInstance(harmonyType, new object[] { "com.natalialanova.human-female-male-hairstyles.rig-retarget" });
                }
                else
                {
                    MethodInfo create = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (create == null)
                    {
                        Log("HarmonyInstance.Create was not found; cross-gender hair rig repair is unavailable.");
                        return;
                    }
                    harmony = create.Invoke(null, new object[] { "com.natalialanova.human-female-male-hairstyles.rig-retarget" });
                }

                PatchWithHarmony(harmonyType, harmonyMethodType, harmony, buildMesh,
                    typeof(Main).GetMethod("PrepareMaleHairPartsPrefix", BindingFlags.Static | BindingFlags.NonPublic),
                    typeof(Main).GetMethod("ClearActiveMaleHairPartsPostfix", BindingFlags.Static | BindingFlags.NonPublic),
                    typeof(Main).GetMethod("ClearActiveMaleHairPartsFinalizer", BindingFlags.Static | BindingFlags.NonPublic));

                PatchWithHarmony(harmonyType, harmonyMethodType, harmony, ensureBones,
                    typeof(Main).GetMethod("RepairMissingHairBonesPrefix", BindingFlags.Static | BindingFlags.NonPublic),
                    null, null);

                hairRigPatchInstalled = true;
                Log("Installed BuildMesh/EnsureBones hooks for cross-gender hair rig retargeting.");
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void PatchWithHarmony(Type harmonyType, Type harmonyMethodType, object harmony,
            MethodInfo target, MethodInfo prefix, MethodInfo postfix, MethodInfo finalizer)
        {
            MethodInfo patchMethod = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "Patch")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault(m => {
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length >= 4 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
                });
            if (patchMethod == null)
                throw new MissingMethodException(harmonyType.FullName, "Patch");

            object prefixHarmony = prefix == null ? null : Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
            object postfixHarmony = postfix == null ? null : Activator.CreateInstance(harmonyMethodType, new object[] { postfix });
            object finalizerHarmony = finalizer == null ? null : Activator.CreateInstance(harmonyMethodType, new object[] { finalizer });
            ParameterInfo[] patchParameters = patchMethod.GetParameters();
            object[] args = new object[patchParameters.Length];
            args[0] = target;
            if (args.Length > 1) args[1] = prefixHarmony;
            if (args.Length > 2) args[2] = postfixHarmony;
            if (args.Length > 3) args[3] = null;
            if (args.Length > 4) args[4] = finalizerHarmony;
            patchMethod.Invoke(harmony, args);
        }

        private static void PrepareMaleHairPartsPrefix(object __instance, object __0)
        {
            activeMaleHairParts.Clear();
            if (!enabled || !hairNamesReady || maleHairEntityNames.Count == 0 || !IsFemaleHumanCharacter(__instance))
                return;

            IDictionary parts = __0 as IDictionary;
            if (parts == null) return;

            foreach (DictionaryEntry entry in parts)
            {
                object entity = entry.Value;
                if (entity == null || entry.Key == null) continue;
                object nameValue = ReadMember(entity, "name");
                string entityName = nameValue == null ? "" : nameValue.ToString();
                if (maleHairEntityNames.Contains(entityName))
                    activeMaleHairParts.Add(entry.Key);
            }

            if (activeMaleHairParts.Count > 0)
                Log("Preparing female-rig remap for " + activeMaleHairParts.Count + " male-hair body part(s).");
        }

        private static void ClearActiveMaleHairPartsPostfix()
        {
            activeMaleHairParts.Clear();
        }

        private static Exception ClearActiveMaleHairPartsFinalizer(Exception __exception)
        {
            activeMaleHairParts.Clear();
            return __exception;
        }

        private static bool RepairMissingHairBonesPrefix(object __instance, object __0,
            List<Transform> __1, List<Matrix4x4> __2, int[] __3, Dictionary<string, Transform> __4)
        {
            if (!enabled || __instance == null || __0 == null ||
                !activeMaleHairParts.Any(part => object.ReferenceEquals(part, __0)) ||
                !IsFemaleHumanCharacter(__instance))
                return true;

            try
            {
                object rendererValue = ReadMember(__0, "SkinnedRenderer");
                SkinnedMeshRenderer renderer = rendererValue as SkinnedMeshRenderer;
                if (renderer == null || renderer.sharedMesh == null || __4 == null || __4.Count == 0)
                    return true;

                Transform[] sourceBones = renderer.bones;
                Matrix4x4[] sourceBindposes = renderer.sharedMesh.bindposes;
                int count = Math.Min(Math.Min(sourceBones.Length, sourceBindposes.Length), __3.Length);
                if (count == 0 || sourceBones.Length != sourceBindposes.Length || sourceBones.Length != __3.Length)
                    return true;

                object animator = ReadMember(__instance, "m_Animator");
                Transform meshRoot = ReadMember(animator, "transform") as Transform;
                if (meshRoot == null)
                    meshRoot = ReadMember(__instance, "transform") as Transform;
                if (meshRoot == null) return true;

                Transform fallback = FindFallbackHairBone(__4);
                if (fallback == null) return true;

                // Resolve every mapping before mutating the lists. Exact name matches
                // keep normal animation; missing male-only hair bones use their nearest
                // ancestor available on the female rig, preserving the strand's rest shape.
                Transform[] targets = new Transform[count];
                int remapped = 0;
                for (int i = 0; i < count; i++)
                {
                    Transform source = sourceBones[i];
                    Transform target = null;
                    if (source != null)
                        __4.TryGetValue(source.name, out target);

                    if (target == null)
                    {
                        target = FindNearestExistingAncestor(source, __4);
                        if (target == null)
                            target = fallback;
                        remapped++;
                    }
                    targets[i] = target;
                }

                for (int i = 0; i < count; i++)
                {
                    Transform target = targets[i];
                    Matrix4x4 retargetedBindpose = target.worldToLocalMatrix * meshRoot.localToWorldMatrix;
                    int mappedIndex = -1;

                    for (int j = 0; j < __1.Count; j++)
                    {
                        if (__1[j] == target && __2[j].Equals(retargetedBindpose))
                        {
                            mappedIndex = j;
                            break;
                        }
                    }

                    if (mappedIndex < 0)
                    {
                        mappedIndex = __1.Count;
                        __1.Add(target);
                        __2.Add(retargetedBindpose);
                    }
                    __3[i] = mappedIndex;
                }

                Log("Retargeted male-hair bones; remapped " + remapped + " of " + count + " source bone(s) to the female skeleton.");
                return false;
            }
            catch (Exception ex)
            {
                LogError(ex);
                return true;
            }
        }

        private static bool IsFemaleHumanCharacter(object character)
        {
            object gender = ReadMember(character, "m_RuntimeGender");
            object race = ReadMember(character, "m_RuntimeRace");
            return gender != null && race != null
                && string.Equals(gender.ToString(), "Female", StringComparison.OrdinalIgnoreCase)
                && string.Equals(race.ToString(), "Human", StringComparison.OrdinalIgnoreCase);
        }

        private static Transform FindNearestExistingAncestor(Transform source, Dictionary<string, Transform> cachedBones)
        {
            for (Transform current = source == null ? null : source.parent; current != null; current = current.parent)
            {
                Transform target;
                if (cachedBones.TryGetValue(current.name, out target) && target != null)
                    return target;
            }
            return null;
        }

        private static Transform FindFallbackHairBone(Dictionary<string, Transform> cachedBones)
        {
            if (cachedBones == null) return null;
            string[] hints = { "Head", "Neck", "Spine", "Bip001" };
            foreach (string hint in hints)
            {
                foreach (KeyValuePair<string, Transform> pair in cachedBones)
                {
                    if (pair.Value != null && string.Equals(pair.Key, hint, StringComparison.OrdinalIgnoreCase))
                        return pair.Value;
                }
            }
            foreach (string hint in hints)
            {
                foreach (KeyValuePair<string, Transform> pair in cachedBones)
                {
                    if (pair.Value != null && pair.Key.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return pair.Value;
                }
            }
            return cachedBones.Values.FirstOrDefault(value => value != null);
        }

        private static void CacheMaleHairEntityNames(Array maleHair)
        {
            if (hairNamesReady || maleHair == null) return;

            int found = 0;
            foreach (object link in maleHair)
            {
                if (link == null) continue;
                object entity = LoadEquipmentEntity(link);
                object nameValue = ReadMember(entity, "name");
                string entityName = nameValue == null ? null : nameValue.ToString();
                if (!string.IsNullOrEmpty(entityName))
                {
                    maleHairEntityNames.Add(entityName);
                    found++;
                }
            }

            if (found > 0)
            {
                hairNamesReady = true;
                Log("Cached " + maleHairEntityNames.Count + " male human hairstyle model names for mesh repair.");
            }
        }

        private static object LoadEquipmentEntity(object link)
        {
            if (link == null) return null;
            try
            {
                MethodInfo load = link.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
                if (load != null) return load.Invoke(link, new object[] { false });

                load = link.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Length == 0);
                return load == null ? null : load.Invoke(link, null);
            }
            catch
            {
                return null;
            }
        }

        private static void BuildMeshPrefix(object __instance, object __0)
        {
            RestoreMeshSwaps();
            if (!enabled || !hairNamesReady || maleHairEntityNames.Count == 0 || __instance == null || __0 == null)
                return;

            try
            {
                object gender = ReadMember(__instance, "m_RuntimeGender");
                object race = ReadMember(__instance, "m_RuntimeRace");
                if (gender == null || !string.Equals(gender.ToString(), "Female", StringComparison.OrdinalIgnoreCase))
                    return;
                if (race != null && !string.Equals(race.ToString(), "Human", StringComparison.OrdinalIgnoreCase))
                    return;

                IDictionary parts = __0 as IDictionary;
                if (parts == null || parts.Count == 0) return;

                // A female reference bind pose is taken from existing non-male-hair parts.
                // Prefer the head EE for head/neck/jaw bones, and the body EE for torso bones.
                var reference = new Dictionary<string, BindPoseCandidate>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in parts)
                {
                    object ee = entry.Value;
                    if (ee == null) continue;
                    string eeName = ReadMember(ee, "name") == null ? "" : ReadMember(ee, "name").ToString();
                    if (maleHairEntityNames.Contains(eeName)) continue;

                    object part = entry.Key;
                    object renderer = ReadMember(part, "SkinnedRenderer");
                    object mesh = ReadMember(renderer, "sharedMesh");
                    Array bones = ReadMember(renderer, "bones") as Array;
                    Array bindposes = ReadMember(mesh, "bindposes") as Array;
                    if (bones == null || bindposes == null) continue;

                    int count = Math.Min(bones.Length, bindposes.Length);
                    for (int i = 0; i < count; i++)
                    {
                        object bone = bones.GetValue(i);
                        object boneNameValue = ReadMember(bone, "name");
                        if (boneNameValue == null || bindposes.GetValue(i) == null) continue;
                        string boneName = boneNameValue.ToString();
                        int priority = GetBindPosePriority(eeName, boneName);

                        BindPoseCandidate existing;
                        if (!reference.TryGetValue(boneName, out existing) || priority > existing.Priority)
                        {
                            reference[boneName] = new BindPoseCandidate { Value = bindposes.GetValue(i), Priority = priority };
                        }
                    }
                }
                if (reference.Count == 0) return;

                foreach (DictionaryEntry entry in parts)
                {
                    object ee = entry.Value;
                    if (ee == null) continue;
                    string eeName = ReadMember(ee, "name") == null ? "" : ReadMember(ee, "name").ToString();
                    if (!maleHairEntityNames.Contains(eeName)) continue;

                    object renderer = ReadMember(entry.Key, "SkinnedRenderer");
                    object originalMesh = ReadMember(renderer, "sharedMesh");
                    Array bones = ReadMember(renderer, "bones") as Array;
                    Array bindposes = ReadMember(originalMesh, "bindposes") as Array;
                    if (renderer == null || originalMesh == null || bones == null || bindposes == null) continue;

                    Array adjusted = (Array)bindposes.Clone();
                    int matched = 0;
                    int changed = 0;
                    int count = Math.Min(bones.Length, adjusted.Length);
                    for (int i = 0; i < count; i++)
                    {
                        object boneNameValue = ReadMember(bones.GetValue(i), "name");
                        if (boneNameValue == null) continue;
                        BindPoseCandidate target;
                        if (!reference.TryGetValue(boneNameValue.ToString(), out target)) continue;
                        matched++;
                        object previous = adjusted.GetValue(i);
                        if (previous != null && !previous.Equals(target.Value))
                        {
                            adjusted.SetValue(target.Value, i);
                            changed++;
                        }
                    }
                    if (matched < 2 || changed == 0) continue;

                    object clone = CloneUnityObject(originalMesh);
                    if (clone == null || !WriteMember(clone, "bindposes", adjusted))
                    {
                        DestroyUnityObject(clone);
                        continue;
                    }

                    meshSwaps.Add(new MeshSwap { Renderer = renderer, OriginalMesh = originalMesh, ClonedMesh = clone });
                    if (!WriteMember(renderer, "sharedMesh", clone))
                    {
                        meshSwaps.RemoveAt(meshSwaps.Count - 1);
                        DestroyUnityObject(clone);
                        continue;
                    }
                    Log("Adjusted " + eeName + " using " + changed + " female bind pose(s).");
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static int GetBindPosePriority(string eeName, string boneName)
        {
            string ee = eeName ?? "";
            string bone = boneName ?? "";
            bool headBone = bone.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0
                || bone.IndexOf("neck", StringComparison.OrdinalIgnoreCase) >= 0
                || bone.IndexOf("jaw", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hairBone = bone.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hairBone)
                return ee.IndexOf("Hair", StringComparison.OrdinalIgnoreCase) >= 0 ? 90 : 10;
            if (headBone)
            {
                if (ee.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0) return 90;
                if (ee.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0) return 50;
                return 20;
            }
            if (ee.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0
                || ee.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0)
                return 80;
            if (ee.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0)
                return 30;
            return 10;
        }

        private static object CloneUnityObject(object source)
        {
            if (source == null) return null;
            Type unityObject = FindType("UnityEngine.Object");
            if (unityObject == null) return null;
            MethodInfo instantiate = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Instantiate" && !m.IsGenericMethod
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == unityObject);
            return instantiate == null ? null : instantiate.Invoke(null, new object[] { source });
        }

        private static void DestroyUnityObject(object value)
        {
            if (value == null) return;
            try
            {
                Type unityObject = FindType("UnityEngine.Object");
                if (unityObject == null) return;
                MethodInfo destroy = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Destroy" && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == unityObject);
                if (destroy != null) destroy.Invoke(null, new object[] { value });
            }
            catch { }
        }

        private static void BuildMeshPostfix()
        {
            RestoreMeshSwaps();
        }

        private static Exception BuildMeshFinalizer(Exception __exception)
        {
            RestoreMeshSwaps();
            return __exception;
        }

        private static void RestoreMeshSwaps()
        {
            for (int i = meshSwaps.Count - 1; i >= 0; i--)
            {
                MeshSwap swap = meshSwaps[i];
                try
                {
                    WriteMember(swap.Renderer, "sharedMesh", swap.OriginalMesh);
                }
                catch { }
                DestroyUnityObject(swap.ClonedMesh);
            }
            meshSwaps.Clear();
        }


        private static void InstallSkeletonModifierPatch()
        {
            if (skeletonModifierPatchInstalled) return;
            try
            {
                Type characterType = FindType("Kingmaker.Visual.CharacterSystem.Character");
                if (characterType == null)
                {
                    Log("Character type not found; hairstyle skeleton-modifier repair is unavailable.");
                    return;
                }

                MethodInfo target = characterType.GetMethod("CacheSkeletonBones", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    Log("Character.CacheSkeletonBones was not found; hairstyle skeleton-modifier repair is unavailable.");
                    return;
                }

                Type harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                Type harmonyType = FindType("HarmonyLib.Harmony");
                bool harmonyV2 = harmonyType != null;
                if (harmonyType == null)
                    harmonyType = FindType("HarmonyLib.HarmonyInstance");
                if (harmonyMethodType == null || harmonyType == null)
                {
                    Log("Harmony API was not found; hairstyle skeleton-modifier repair is unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(Main).GetMethod("CacheSkeletonBonesPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(Main).GetMethod("CacheSkeletonBonesPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(Main).GetMethod("CacheSkeletonBonesFinalizer", BindingFlags.Static | BindingFlags.NonPublic);
                object prefixHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
                object postfixHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { postfix });
                object finalizerHarmony = Activator.CreateInstance(harmonyMethodType, new object[] { finalizer });

                object harmony;
                if (harmonyV2)
                {
                    harmony = Activator.CreateInstance(harmonyType, new object[] { "openai.human-female-male-hairstyles.skeletonmodifiers" });
                }
                else
                {
                    MethodInfo create = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (create == null)
                    {
                        Log("HarmonyInstance.Create was not found; hairstyle skeleton-modifier repair is unavailable.");
                        return;
                    }
                    harmony = create.Invoke(null, new object[] { "openai.human-female-male-hairstyles.skeletonmodifiers" });
                }

                MethodInfo patchMethod = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "Patch")
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault(m => {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length >= 4 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
                    });
                if (patchMethod == null)
                {
                    Log("Harmony Patch method was not found; hairstyle skeleton-modifier repair is unavailable.");
                    return;
                }

                ParameterInfo[] patchParameters = patchMethod.GetParameters();
                object[] args = new object[patchParameters.Length];
                args[0] = target;
                args[1] = prefixHarmony;
                args[2] = postfixHarmony;
                if (args.Length > 3) args[3] = null;
                if (args.Length > 4) args[4] = finalizerHarmony;
                patchMethod.Invoke(harmony, args);
                skeletonModifierPatchInstalled = true;
                Log("Installed Character.CacheSkeletonBones repair hook.");
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void CacheSkeletonBonesPrefix(object __instance)
        {
            RestoreModifierSwaps();
            if (!enabled || !hairNamesReady || __instance == null) return;

            try
            {
                object gender = ReadMember(__instance, "m_RuntimeGender");
                if (gender == null || !string.Equals(gender.ToString(), "Female", StringComparison.OrdinalIgnoreCase))
                    return;

                IEnumerable entities = ReadMember(__instance, "m_EquipmentEntities") as IEnumerable;
                if (entities == null) return;

                foreach (object entity in entities)
                {
                    if (entity == null) continue;
                    object nameValue = ReadMember(entity, "name");
                    string entityName = nameValue == null ? "" : nameValue.ToString();
                    if (!maleHairEntityNames.Contains(entityName)) continue;

                    IList modifiers = ReadMember(entity, "SkeletonModifiers") as IList;
                    if (modifiers == null || modifiers.Count == 0) continue;

                    object[] original = new object[modifiers.Count];
                    modifiers.CopyTo(original, 0);
                    modifiers.Clear();
                    modifierSwaps.Add(new ModifierSwap { Collection = modifiers, Original = original });
                    Log("Temporarily suppressed " + original.Length + " skeleton modifier(s) on " + entityName + " for a female character.");
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
                RestoreModifierSwaps();
            }
        }

        private static void CacheSkeletonBonesPostfix()
        {
            RestoreModifierSwaps();
        }

        private static Exception CacheSkeletonBonesFinalizer(Exception __exception)
        {
            RestoreModifierSwaps();
            return __exception;
        }

        private static void RestoreModifierSwaps()
        {
            for (int i = modifierSwaps.Count - 1; i >= 0; i--)
            {
                ModifierSwap swap = modifierSwaps[i];
                try
                {
                    swap.Collection.Clear();
                    foreach (object modifier in swap.Original)
                        swap.Collection.Add(modifier);
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            }
            modifierSwaps.Clear();
        }

        private static void Log(string message)
        {
            if (logger != null) logger.Log("[Human Male Hairstyles] " + message);
        }

        private static void LogError(Exception ex)
        {
            if (logger != null) logger.Log("[Human Male Hairstyles] " + ex);
        }
    }
}