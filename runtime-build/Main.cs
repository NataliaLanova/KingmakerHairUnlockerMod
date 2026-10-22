using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityModManagerNet;
using UnityEngine.SceneManagement;

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

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                logger = modEntry.Logger;
                modEntry.OnToggle = OnToggle;
                modEntry.OnUpdate = OnUpdate;
                SceneManager.sceneLoaded += OnSceneLoaded;
                Log("Loaded. This patch gives human female characters access to human male hairstyles.");
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