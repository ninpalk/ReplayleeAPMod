using System.Collections;
using System.Reflection;
using System.Linq;
using System.IO;
using Microsoft.Win32;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using MelonLoader;
using Il2Cpp;
using Il2CppPlaytonic.Game;
using UnityEngine;
using Il2CppParadoxNotion;
using UnityEngine.SceneManagement;


namespace YookaArchipelago
{
    public static class BuildInfo
    {
        public const string Name = "Yooka-Replaylee Archipelago"; // Name of the Mod.  (MUST BE SET)
        public const string Description = "Yooka-Replaylee Archipelago connector mod"; // Description for the Mod.  (Set as null if none)
        public const string Author = "tommadness and ninpalk"; // Author of the Mod.  (MUST BE SET)
        public const string Company = null!; // Company that made the Mod.  (Set as null if none)
        public const string Version = "0.0.1"; // Version of the Mod.  (MUST BE SET)
        public const string DownloadLink = null!; // Download Link for the Mod.  (Set as null if none)
    }

    public class YRAPMod : MelonMod
    {
        private APClient _client = null!;
        private GameObject player = null!;
        private PlayerDeathManager deathManager = null!;
        private bool _deathlinkHooked = false;
        private volatile bool _pendingDeathlink = false;
        private bool _supportedSlotLoaded = false;
        private int _activeSupportedSlot = -1;
        private float _slotProbeAt = 0f;
        private bool _saveLoadHookInstalled = false;
        private string _pagieDoorCheckMap = null;
        private bool _pagieDoorCheckScheduledForMap = false;
        private bool _capitalBGateLastAllowed = false;
        private bool _capitalBGateStateKnown = false;
        private int _capitalBGateLastKeyCount = -1;
        private string _capitalBGateLogMap = null;
        private float _nextFinalBossStatPollAt = 0f;
        private int[] _finalBossGameStatBaseline = null;
        private bool _capitalBEntryDeathTriggered = false;
        private readonly List<MethodInfo> _patchedLoadMoveNextMethods = new List<MethodInfo>();
        private static readonly Dictionary<object, int> _loadIteratorSlots =
            new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _apWorldQuillTotals =
            new Dictionary<string, int>
            {
                { "TT Quill", 0 }, { "GlGl Quill", 0 }, { "MM Quill", 0 },
                { "CC Quill", 0 }, { "GaGa Quill", 0 }
            };
        public static ArchipelagoGUI gui = null!;

        public override void OnEarlyInitializeMelon()
        {
            Application.runInBackground = true;
        }

        public override void OnLateInitializeMelon()
        {
            TrySeedEmptySaveSlotsFromFile3();

            gui = new ArchipelagoGUI();
            gui.SetMoveToggledCallback(ActivatePlayerMove);
            InstallSaveSlotLoadHook();
            Hooks.InstallPagiePieceHooks(HarmonyInstance);
            Hooks.InstallCasinoTokenHooks(HarmonyInstance);
            // DeathLink is created only after a successful Archipelago connection.
            // Hook it from OnLateUpdate once the service exists instead of
            // dereferencing it during mod initialization.
        }

        private void TrySeedEmptySaveSlotsFromFile3()
        {
            const string friendlyMessage =
                "If File 1 or File 2 has less than 3 minutes, it will copy File 3 and replace those files, so feel free to put a post-one-book-saved in the slot2 file in your 2448020 -> remote folder to skip the tutorial if you wish!";

            MelonLogger.Msg(friendlyMessage);

            try
            {
                string steamPath = null;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\\Valve\\Steam"))
                {
                    steamPath = key?.GetValue("SteamPath") as string;
                }

                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\\Wow6432Node\\Valve\\Steam"))
                    {
                        steamPath = key?.GetValue("SteamPath") as string;
                    }
                }

                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                {
                    MelonLogger.Warning("YookaArchipelago: Could not locate the Steam install directory; File 3 template copy was skipped.");
                    return;
                }

                string userdataRoot = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdataRoot))
                {
                    MelonLogger.Warning($"YookaArchipelago: Steam userdata directory was not found at '{userdataRoot}'; File 3 template copy was skipped.");
                    return;
                }

                string[] replayleeRoots = Directory.GetDirectories(userdataRoot)
                    .Select(userDir => Path.Combine(userDir, "2448020", "remote"))
                    .Where(Directory.Exists)
                    .ToArray();

                if (replayleeRoots.Length == 0)
                {
                    MelonLogger.Warning("YookaArchipelago: Could not find Steam userdata/<account>/2448020/remote; File 3 template copy was skipped.");
                    return;
                }

                bool copiedAnything = false;
                foreach (string remoteDir in replayleeRoots)
                {
                    string source = Path.Combine(remoteDir, "slot2.dat");
                    if (!File.Exists(source) || new FileInfo(source).Length == 0)
                        continue;

                    copiedAnything |= CopyTemplateIfVeryNew(source, Path.Combine(remoteDir, "slot0.dat"), "File 1");
                    copiedAnything |= CopyTemplateIfVeryNew(source, Path.Combine(remoteDir, "slot1.dat"), "File 2");
                }

                if (!copiedAnything)
                {
                    MelonLogger.Msg("YookaArchipelago: File 1/File 2 were not eligible for replacement, or no non-empty slot2.dat template was found.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"YookaArchipelago: File 3 template copy failed safely without changing saves: {ex}");
            }
        }

        private static bool CopyTemplateIfVeryNew(string source, string destination, string destinationLabel)
        {
            try
            {
                // A missing/zero-byte slot is always safe to seed. For an existing
                // slot, use the save file's own lifetime as a conservative startup
                // proxy for a 0:02-or-less newly-created save. We intentionally do
                // not overwrite older saves.
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                {
                    DateTime createdUtc = File.GetCreationTimeUtc(destination);
                    DateTime writtenUtc = File.GetLastWriteTimeUtc(destination);
                    double lifetimeSeconds = Math.Max(0.0, (writtenUtc - createdUtc).TotalSeconds);

                    if (lifetimeSeconds > 179.999)
                    {
                        MelonLogger.Msg($"YookaArchipelago: {destinationLabel} is not a <=2:59 newly-created save (file lifetime {TimeSpan.FromSeconds(lifetimeSeconds):mm\\:ss}); leaving it untouched.");
                        return false;
                    }

                    MelonLogger.Msg($"YookaArchipelago: {destinationLabel} appears to be a <=2:59 newly-created save (file lifetime {TimeSpan.FromSeconds(lifetimeSeconds):mm\\:ss}); replacing it from File 3.");
                }
                else
                {
                    MelonLogger.Msg($"YookaArchipelago: {destinationLabel} is empty/missing; seeding it from File 3.");
                }

                File.Copy(source, destination, true);
                MelonLogger.Msg($"YookaArchipelago: Copied File 3 template '{Path.GetFileName(source)}' -> {destinationLabel} '{Path.GetFileName(destination)}'.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"YookaArchipelago: Could not copy File 3 to {destinationLabel}: {ex.Message}");
                return false;
            }
        }

        private void ReceiveDeathlink(DeathLink deathLink)
        {
            // Archipelago invokes this from its networking thread. Do not touch
            // Unity/IL2CPP objects here; queue the death for OnLateUpdate.
            if (!APClient.DeathlinkEnabled)
                return;

            _pendingDeathlink = true;
            MelonLogger.Msg($"YookaArchipelago: Received DeathLink from {deathLink.Source}.");
        }

        // Used by the GUI for manual toggles.
        public void ActivatePlayerMove(PlayerMoves.Moves move)
        {
            TryActivatePlayerMove(move);
        }

        // Called only from OnLateUpdate, i.e. the Unity/Melon main thread.
        // Returns false when the current scene has not finished creating the
        // player or PlayerMoves object, which causes APData to retry the queued
        // move on the next frame.
        private bool TryActivatePlayerMove(PlayerMoves.Moves move)
        {
            findPlayer();

            if (player == null)
                return false;

            var moves = player.GetComponent<PlayerMoves>();
            if (moves == null)
            {
                player = null;
                return false;
            }

            var playerMove = moves.GetMove(move);
            if (playerMove == null)
                return false;

            if (!APData.playerMoves.TryGetValue(move, out var enabled))
                return false;

            playerMove.mIsEnabledInGame = enabled;
            return true;
        }

        private void findPlayer()
        {
            if (player == null)
                player = GameObject.Find("PlayerKamBatV5");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // PlayerKamBatV5 is recreated between scenes; never retain the old
            // scene's player reference for queued AP moves.
            player = null;
            // A scene transition can occur while a save is loading. Re-probe
            // the active slot after the new gameplay scene has settled.
            _supportedSlotLoaded = false;
            _slotProbeAt = Time.realtimeSinceStartup + 1.0f;

            // GameStatManager and the hub door objects can be recreated as scenes
            // change. Re-check the Archipelago Progressive Pagie Door count after
            // every scene initialization and re-apply every door state currently
            // unlocked. The short retries cover scenes where GameStatManager is
            // not fully ready on the first frame.
            // OnSceneWasInitialized can fire more than once while the same map is
            // assembling. Schedule the delayed Pagie Door pass only once for that
            // map name. A different map resets the guard, so returning later will
            // schedule one fresh check for that load.
            if (_pagieDoorCheckMap != sceneName)
            {
                _pagieDoorCheckMap = sceneName;
                _pagieDoorCheckScheduledForMap = false;
            }

            if (!_pagieDoorCheckScheduledForMap)
            {
                _pagieDoorCheckScheduledForMap = true;
                MelonCoroutines.Start(ReapplyProgressivePagieDoorsAfterSceneChange(sceneName));
            }

            if (sceneName == "Level_00_Hub_A_Environment")
            {
                MelonCoroutines.Start(UnslipEarlySlopes());
            }

            if (sceneName == "Level_00_Hub_A_CaveJ")
            {
                MelonCoroutines.Start(SkipTutorialCave());
            }

            if (sceneName == "Level_Common")
            {
                MelonCoroutines.Start(FindDeathManager());
            }

            // Level 07 goal gate. The exact Final Boss environment is watched
            // every 30 seconds while it remains loaded. Non-Capital-B goals have
            // no reason to be in this fight, and Defeat Capital B requires keys.
            if (sceneName == "Level_07_FinalBoss_Environment")
            {
                _capitalBEntryDeathTriggered = false;
                MelonCoroutines.Start(EnforceLevel07GoalRequirement());
            }
        }


        private IEnumerator ReapplyProgressivePagieDoorsAfterSceneChange(string scheduledMap)
        {
            // Replaylee creates the hub/stat/door objects well after Unity reports
            // the scene initialized. Wait a full 10 seconds, then inspect the
            // loaded PagieDoor components directly.
            yield return new WaitForSeconds(10.0f);
            // If another map loaded during the delay, this coroutine belongs to
            // the old map and must not touch the new scene's doors.
            if (_pagieDoorCheckMap != scheduledMap)
                yield break;

            Melon<YRAPMod>.Logger.Msg($"Running Progressive Pagie Door check once for map '{scheduledMap}' (10 seconds after load).");
            Hooks.PagieDoorHook.RecheckAllLoadedPagieDoors();
        }


        private IEnumerator UnslipEarlySlopes()
        {
            yield return new WaitForSeconds(1f);
            GameObject.Find("hub_lair_floor_slippy_01_a").GetComponent<ObjectSurface>().IsSurfaceSlippy = false;
        }

        private IEnumerator SkipTutorialCave()
        {
            yield return new WaitForSeconds(5f);
            LoggerInstance.Msg("Setting up Tutorial Cave skip");
            var caveToHTDoor =
                GameObject.Find(
                    "DOORS/CaveExitToHivoryEntranceDoor");
            LoggerInstance.Msg(caveToHTDoor.name);
            var shipwreckToCaveDoor = GameObject.Find("DOORS/ShipwreckCreekToCaveEntranceDoor");
            LoggerInstance.Msg(shipwreckToCaveDoor.name);
            caveToHTDoor.GetComponent<Transform>().position = shipwreckToCaveDoor.GetComponent<Transform>().position;
            caveToHTDoor.GetComponent<Transform>().rotation = shipwreckToCaveDoor.GetComponent<Transform>().rotation;
            shipwreckToCaveDoor.SetActive(false);


        }


        private IEnumerator EnforceLevel07GoalRequirement()
        {
            // Give Level 07 player/death objects time to spawn, then enforce the
            // selected AP goal immediately and once every 30 seconds thereafter.
            yield return new WaitForSeconds(0.35f);

            while (IsFinalBossEnvironmentLoaded())
            {
                string message = null;

                if (APClient.IsTriplePagieHuntGoal() || APClient.IsGoldenPagieHuntGoal())
                {
                    message = "No need to be there, go get checks elsewhere! :)";
                }
                else if (APClient.IsDefeatCapitalBGoal())
                {
                    int required = APClient.GetCapitalKeyGoal();
                    int count = APClient.GetCapitalKeyCount();
                    if (required > 0 && count < required)
                        message = "You need your Capital Keys count to rise to see Capital B's demise!\n(If in a Pagie Door Cutscene, Save and Quit, Then the Pagie Door you can go through it!)";
                }

                if (message == null)
                {
                    if (APClient.IsDefeatCapitalBGoal())
                        MelonLogger.Msg($"Capital B fight entry allowed: Capital Keys={APClient.GetCapitalKeyCount()}/{APClient.GetCapitalKeyGoal()}.");
                    yield break;
                }

                if (gui != null)
                    gui.ShowPopupMessage(message, 6f);
                MelonLogger.Msg($"Level 07 goal gate: {message}");

                if (deathManager == null)
                {
                    GameObject deathManagerObject = GameObject.Find("PlayerDeathManager");
                    if (deathManagerObject != null)
                        deathManager = deathManagerObject.GetComponent<PlayerDeathManager>();
                }

                if (deathManager != null)
                {
                    deathManager.StartPostDeathSequence(false);
                }
                else
                {
                    MelonLogger.Warning("Level 07 goal gate: PlayerDeathManager was not ready; will retry on the next 30-second cycle.");
                }

                yield return new WaitForSeconds(30f);
            }
        }

        private bool IsFinalBossEnvironmentLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == "Level_07_FinalBoss_Environment")
                    return true;
            }
            return false;
        }

        private IEnumerator FindDeathManager()
        {
            yield return new WaitForSeconds(0.1f);
            LoggerInstance.Msg("Finding deathManager");
            deathManager = GameObject.Find("PlayerDeathManager").GetComponent<PlayerDeathManager>();
            LoggerInstance.Msg($"Found: {deathManager.name}");
        }

        private IEnumerator DoDeathLink()
        {
            yield return new WaitForSeconds(0.1f);

            if (!APClient.DeathlinkEnabled)
            {
                APData.DeathlinkReceived = false;
                yield break;
            }

            if (deathManager == null)
            {
                var deathManagerObject = GameObject.Find("PlayerDeathManager");
                if (deathManagerObject != null)
                    deathManager = deathManagerObject.GetComponent<PlayerDeathManager>();
            }

            if (deathManager == null)
            {
                MelonLogger.Warning("YookaArchipelago: Received DeathLink, but PlayerDeathManager is not ready; retrying.");
                _pendingDeathlink = true;
                APData.DeathlinkReceived = false;
                yield break;
            }

            deathManager.StartPostDeathSequence(false);
        }

        private static object? InvokeZeroArgStatic(Type type, string methodName)
        {
            MethodInfo? method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            return method?.Invoke(null, null);
        }

        private static PropertyInfo? FindReadableWritableIntProperty(object obj, string propertyName)
        {
            PropertyInfo? p = obj.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null && p.CanRead && p.CanWrite ? p : null;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private void InstallSaveSlotLoadHook()
        {
            if (_saveLoadHookInstalled)
                return;

            try
            {
                Assembly asm = typeof(SavegameManagerExtensions).Assembly;
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types.Where(t => t != null).Cast<Type>().ToArray();
                }

                int candidates = 0;

                foreach (Type t in types)
                {
                    string fullName = t.FullName ?? t.Name;
                    string lower = fullName.ToLowerInvariant();

                    // Generated IL2CPP iterator wrappers normally retain either
                    // the original method name or save/load terminology.
                    if (!(lower.Contains("internalloadslot") ||
                          (lower.Contains("save") && lower.Contains("load") && lower.Contains("slot"))))
                        continue;

                    MethodInfo? moveNext = t.GetMethod(
                        "MoveNext",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);

                    if (moveNext == null || moveNext.ReturnType != typeof(bool))
                        continue;

                    // Require a plausible captured slot field/property. We do
                    // not patch arbitrary save coroutines.
                    bool hasSlotMember = false;
                    foreach (FieldInfo f in t.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        string fn = f.Name.ToLowerInvariant();
                        if (f.FieldType == typeof(int) &&
                            (fn.Contains("slot") || fn.Contains("index")))
                        {
                            hasSlotMember = true;
                            break;
                        }
                    }

                    if (!hasSlotMember)
                    {
                        foreach (PropertyInfo prop in t.GetProperties(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            string pn = prop.Name.ToLowerInvariant();
                            if (prop.PropertyType == typeof(int) && prop.CanRead &&
                                (pn.Contains("slot") || pn.Contains("index")))
                            {
                                hasSlotMember = true;
                                break;
                            }
                        }
                    }

                    if (!hasSlotMember)
                        continue;

                    candidates++;
                    MelonLogger.Msg(
                        $"YookaArchipelago: InternalLoadSlot iterator candidate: {fullName}; MoveNext={moveNext}.");

                    HarmonyLib.HarmonyMethod pre = new HarmonyLib.HarmonyMethod(
                        typeof(YRAPMod).GetMethod(
                            nameof(LoadIteratorMoveNextPrefix),
                            BindingFlags.NonPublic | BindingFlags.Static));
                    HarmonyLib.HarmonyMethod post = new HarmonyLib.HarmonyMethod(
                        typeof(YRAPMod).GetMethod(
                            nameof(LoadIteratorMoveNextPostfix),
                            BindingFlags.NonPublic | BindingFlags.Static));

                    HarmonyInstance.Patch(moveNext, prefix: pre, postfix: post);
                    _patchedLoadMoveNextMethods.Add(moveNext);
                }

                if (candidates == 0)
                {
                    MelonLogger.Error(
                        "YookaArchipelago: No generated InternalLoadSlot MoveNext iterator with a captured slot member was found.");
                    return;
                }

                _saveLoadHookInstalled = true;
                MelonLogger.Msg(
                    $"YookaArchipelago: Patched {candidates} generated save-slot MoveNext iterator candidate(s).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"YookaArchipelago: Failed while enumerating/patching generated save-load iterators: {ex}");
            }
        }

        private static int ReadCapturedSlot(object iterator)
        {
            Type t = iterator.GetType();

            foreach (FieldInfo f in t.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string n = f.Name.ToLowerInvariant();
                if (f.FieldType != typeof(int) ||
                    !(n.Contains("slot") || n.Contains("index")))
                    continue;

                try
                {
                    int value = Convert.ToInt32(f.GetValue(iterator));
                    if (value >= 0 && value <= 2)
                        return value;
                }
                catch { }
            }

            foreach (PropertyInfo prop in t.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string n = prop.Name.ToLowerInvariant();
                if (prop.PropertyType != typeof(int) || !prop.CanRead ||
                    !(n.Contains("slot") || n.Contains("index")))
                    continue;

                try
                {
                    int value = Convert.ToInt32(prop.GetValue(iterator));
                    if (value >= 0 && value <= 2)
                        return value;
                }
                catch { }
            }

            return -1;
        }

        private static void LoadIteratorMoveNextPrefix(object __instance)
        {
            try
            {
                int slot = ReadCapturedSlot(__instance);
                if (slot < 0 || slot > 2)
                    return;

                YRAPMod? mod = Melon<YRAPMod>.Instance;
                if (mod == null)
                    return;

                if (!_loadIteratorSlots.ContainsKey(__instance))
                {
                    _loadIteratorSlots[__instance] = slot;
                    mod._activeSupportedSlot = slot;
                    mod._supportedSlotLoaded = false;
                    mod._slotProbeAt = Time.realtimeSinceStartup + 0.25f;

                    MelonLogger.Msg(
                        $"YookaArchipelago: InternalLoadSlot MoveNext captured File {slot + 1} (slot{slot}); " +
                        "world Quills remain queued until load completion + gameplay save readiness.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"YookaArchipelago: InternalLoadSlot MoveNext prefix failed: {ex}");
            }
        }

        private static void LoadIteratorMoveNextPostfix(object __instance, bool __result)
        {
            try
            {
                // IEnumerator.MoveNext() returning false means the load
                // coroutine has completed. Only after that do we permit the
                // normal gameplay/save-record readiness probe to release AP
                // world Quills.
                if (__result)
                    return;

                if (!_loadIteratorSlots.TryGetValue(__instance, out int slot))
                    return;

                _loadIteratorSlots.Remove(__instance);

                YRAPMod? mod = Melon<YRAPMod>.Instance;
                if (mod == null)
                    return;

                mod._activeSupportedSlot = slot;
                mod._supportedSlotLoaded = false;
                mod._slotProbeAt = Time.realtimeSinceStartup + 0.10f;

                MelonLogger.Msg(
                    $"YookaArchipelago: InternalLoadSlot MoveNext completed File {slot + 1} (slot{slot}); " +
                    "waiting for PlayerKamBatV5 and loaded save records.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"YookaArchipelago: InternalLoadSlot MoveNext postfix failed: {ex}");
            }
        }

        private bool IsLoadCoroutineAndSaveReady()
        {
            if (_activeSupportedSlot < 0 || _activeSupportedSlot > 2)
                return false;

            // Do not release during title/file-select. The player object plus
            // both live save records are the final readiness barrier.
            if (GameObject.Find("PlayerKamBatV5") == null)
                return false;

            try
            {
                object? worldSave = InvokeZeroArgStatic(
                    typeof(SavegameManagerExtensions), "GetWorldSaveDataRecord");
                object? playerSave = InvokeZeroArgStatic(
                    typeof(SavegameManagerExtensions), "GetPlayerSaveDataRecord");

                if (worldSave == null || playerSave == null)
                    return false;

                MelonLogger.Msg(
                    $"YookaArchipelago: File {_activeSupportedSlot + 1} (slot{_activeSupportedSlot}) " +
                    "has gameplay + loaded save records; releasing queued AP world Quills.");

                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetReceivedWorldQuillTotal(string itemName)
        {
            // Count the AP-side received world-Quill items from the client's
            // synchronized received-item history. This makes the save value a
            // reconciliation target rather than "+1 per callback".
            try
            {
                return APClient.GetReceivedItemCount(itemName);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"YookaArchipelago: Could not read AP received total for '{itemName}': {ex}");
                return -1;
            }
        }

        private float _nextWorldQuillReconcileAt = 0f;

        private string? GetCurrentWorldQuillItemName()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName.StartsWith("Level_01", StringComparison.Ordinal)) return "TT Quill";
            if (sceneName.StartsWith("Level_02", StringComparison.Ordinal)) return "GlGl Quill";
            if (sceneName.StartsWith("Level_03", StringComparison.Ordinal)) return "MM Quill";
            if (sceneName.StartsWith("Level_04", StringComparison.Ordinal)) return "CC Quill";
            if (sceneName.StartsWith("Level_05", StringComparison.Ordinal)) return "GaGa Quill";
            return null;
        }

        private bool ReconcileCurrentWorldQuillCount(bool logWhenAlreadyMatched = false)
        {
            if (!_supportedSlotLoaded || _activeSupportedSlot < 0 || _activeSupportedSlot > 2)
                return false;

            string? itemName = GetCurrentWorldQuillItemName();
            if (itemName == null)
                return false;

            int apTotal = GetReceivedWorldQuillTotal(itemName);
            if (apTotal < 0)
                return false;

            // Replaylee has 150 Quills per main world. AP may technically send
            // more copies through commands/item links, but there are only 150
            // vanilla pickup identities available to back the visible counter.
            int target = Math.Max(0, Math.Min(150, apTotal));

            try
            {
                int before = SavegameManagerExtensions.GetCollectedCoinCount();
                if (before == target)
                {
                    if (logWhenAlreadyMatched)
                        MelonLogger.Msg($"YookaArchipelago: {itemName} already synchronized at {before}/150.");
                    return true;
                }

                if (before > target)
                {
                    // Received-item totals are monotonic for a seed, so this should
                    // only occur when an existing vanilla save already had more
                    // Quills than AP. Do not destroy collection flags blindly.
                    MelonLogger.Warning(
                        $"YookaArchipelago: {itemName} vanilla count is {before}/150 but AP has {target}/150. " +
                        "Cannot safely lower collected pickup flags; leaving it unchanged.");
                    return false;
                }

                int needed = target - before;
                int added = 0;

                // AP mode blocks normal gameplay CollectCoin calls. For AP item
                // reconciliation only, temporarily allow Replaylee's real vanilla
                // CollectCoin(index) path. We intentionally use currently-uncollected
                // pickup identities so reconnecting is naturally idempotent.
                Hooks.ApplyingArchipelagoQuill = true;
                try
                {
                    for (int pickupIndex = 0; pickupIndex < 150 && added < needed; pickupIndex++)
                    {
                        CollectionStatus status =
                            SavegameManagerExtensions.GetCoinCollectionStatus(pickupIndex);
                        if (status == CollectionStatus.Collected)
                            continue;

                        int countBeforePickup = SavegameManagerExtensions.GetCollectedCoinCount();
                        SavegameManagerExtensions.CollectCoin(pickupIndex);
                        int countAfterPickup = SavegameManagerExtensions.GetCollectedCoinCount();

                        if (countAfterPickup > countBeforePickup)
                            added += countAfterPickup - countBeforePickup;
                    }
                }
                finally
                {
                    Hooks.ApplyingArchipelagoQuill = false;
                }

                SavegameManagerExtensions.UpdateQuillStats();
                int after = SavegameManagerExtensions.GetCollectedCoinCount();

                // Ask Replaylee to persist the same state its vanilla collection
                // path just changed. Reflection keeps compatibility with the
                // generated SaveSlot signature already used by this project.
                MethodInfo? saveSlot = typeof(SavegameManagerExtensions).GetMethod(
                    "SaveSlot",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (saveSlot != null)
                {
                    ParameterInfo[] ps = saveSlot.GetParameters();
                    if (ps.Length == 0)
                        saveSlot.Invoke(null, null);
                    else if (ps.Length == 1)
                        saveSlot.Invoke(null, new object?[] { null });
                }

                MelonLogger.Msg(
                    $"YookaArchipelago: AP Quill sync {itemName}: AP={target}/150, " +
                    $"vanilla {before}/150 -> {after}/150 using Replaylee CollectCoin().");

                return after == target;
            }
            catch (Exception ex)
            {
                Hooks.ApplyingArchipelagoQuill = false;
                MelonLogger.Error(
                    $"YookaArchipelago: Failed to synchronize '{itemName}' through vanilla CollectCoin: " +
                    $"{(ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex)}");
                return false;
            }
        }

        private bool ApplyWorldQuill(string itemName, string itemIdentity)
        {
            if (!_supportedSlotLoaded || _activeSupportedSlot < 0 || _activeSupportedSlot > 2)
                return false;

            // ReceivedItems synchronization can replay the full AP inventory.
            // The authoritative state is the total number of each world-Quill
            // item in Session.Items.AllItemsReceived, not the number of callbacks.
            // Mark this packet handled, then reconciliation below/current-world
            // polling makes the vanilla counter equal that authoritative total.
            ReconcileCurrentWorldQuillCount();
            APClient.MarkReceivedWorldQuillApplied(itemIdentity);
            return true;
        }

        private static MethodInfo? FindGameStatMethod(
            string methodName, Type firstParameterType, Type secondParameterType)
        {
            return typeof(GameStatManager).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance,
                null,
                new Type[] { firstParameterType, secondParameterType },
                null);
        }

        private static MethodInfo? FindGameStatMethod(
            string methodName, Type firstParameterType)
        {
            return typeof(GameStatManager).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance,
                null,
                new Type[] { firstParameterType },
                null);
        }

        private static void SaveWorldQuillStats()
        {
            try
            {
                MethodInfo? save = typeof(GameStatManager).GetMethod(
                    "Save",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                save?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"YookaArchipelago: Failed to save world Quill stats: {ex}");
            }
        }





        private void ApplyCapitalBKeyGate()
        {
            if (!APClient.IsDefeatCapitalBGoal())
                return;

            try
            {
                int required = APClient.GetCapitalKeyGoal();
                if (required < 1)
                    return;

                int count = APClient.GetCapitalKeyCount();
                bool allowed = count >= required;
                var stats = SavegameManagerExtensions.GetGameStatsSaveDataRecord();
                if (stats == null || stats.Values == null || stats.Values.Length <= 291)
                    return;

                int desired = allowed ? 1 : 0;
                int old = stats.Values[291];
                if (old != desired)
                    stats.Values[291] = desired;

                // Report only when the active map changes or the player's Capital Key
                // count changes. ApplyCapitalBKeyGate still runs normally so Values[291]
                // remains enforced, but the client log no longer repeats every poll.
                string gateMap = SceneManager.GetActiveScene().name ?? string.Empty;
                bool mapChanged = !string.Equals(_capitalBGateLogMap, gateMap, System.StringComparison.Ordinal);
                bool keyCountChanged = _capitalBGateLastKeyCount != count;

                if (mapChanged || keyCountChanged)
                {
                    _capitalBGateLogMap = gateMap;
                    _capitalBGateStateKnown = true;
                    _capitalBGateLastAllowed = allowed;
                    _capitalBGateLastKeyCount = count;
                    MelonLogger.Msg(
                        $"Capital B key gate: Capital Keys={count}/{required}; Values[291] {old} -> {desired}; " +
                        (allowed ? "FINAL BOSS ENTRANCE UNLOCKED" : "vanilla 120-Pagie unlock BLOCKED"));
                }
            }
            catch
            {
                // Save data is not available in every frontend/loading scene.
            }
        }

        private bool _capitalBCompletionSentThisSession = false;

        private void CheckCapitalBCompletion()
        {
            if (!APClient.IsDefeatCapitalBGoal() || _capitalBCompletionSentThisSession)
                return;

            // Reconnect/reload safe: Values[167] is Replaylee's vanilla Capital B
            // defeated flag. It flips 0 -> 1 immediately before the vanilla +3
            // Pagie reward at the end of Level_07_FinalBoss.
            try
            {
                var stats = SavegameManagerExtensions.GetGameStatsSaveDataRecord();
                if (stats == null || stats.Values == null || stats.Values.Length <= 167)
                    return;

                if (stats.Values[167] < 1)
                    return;

                if (APClient.IsLocationChecked("HT - Capital Beaten"))
                {
                    _capitalBCompletionSentThisSession = true;
                    return;
                }

                MelonLogger.Msg(
                    "Capital B completion detected: GameStats.Values[167] >= 1; sending HT - Capital Beaten.");
                APClient.SendLocation("HT - Capital Beaten");
                _capitalBCompletionSentThisSession = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Capital B completion check failed: {ex.Message}");
            }
        }

        public override void OnLateUpdate()
        {
            // ItemReceived is raised by Archipelago's networking code, not by
            // Unity's main thread. Apply any newly received movement upgrades
            // here so Unity/IL2CPP APIs are accessed on the game thread. This
            // runs every frame, so a received move is normally applied on the
            // very next frame instead of waiting for an arbitrary coroutine or
            // Unity object to become available.
            APData.ApplyPendingMoves(TryActivatePlayerMove);

            // AP may synchronize all received items before the player chooses
            // a file. Keep them queued until Replaylee confirms Files 1-3 / slots0-2
            // is an active supported gameplay save.
            if (!_saveLoadHookInstalled)
                InstallSaveSlotLoadHook();

            if (!_supportedSlotLoaded &&
                _activeSupportedSlot >= 0 &&
                Time.realtimeSinceStartup >= _slotProbeAt)
            {
                _slotProbeAt = Time.realtimeSinceStartup + 0.5f;
                _supportedSlotLoaded = IsLoadCoroutineAndSaveReady();
            }

            if (_supportedSlotLoaded)
            {
                APData.ApplyPendingWorldQuills(ApplyWorldQuill);

                // Reconcile repeatedly after a save/scene becomes ready. This is
                // the reconnect/reopen safety net: if AP says TT Quill = 30, TT's
                // vanilla counter is brought to 30/150 when TT is loaded, without
                // adding another +30 merely because ReceivedItems replayed.
                if (Time.realtimeSinceStartup >= _nextWorldQuillReconcileAt)
                {
                    _nextWorldQuillReconcileAt = Time.realtimeSinceStartup + 0.5f;
                    ReconcileCurrentWorldQuillCount();
                }
            }

            // Only attach the receive handler while DeathLink is enabled. This
            // also makes changing the GUI toggle take effect without reconnecting.
            if (APClient.DeathlinkEnabled && !_deathlinkHooked && APClient.DeathlinkService != null)
            {
                APClient.DeathlinkService.OnDeathLinkReceived += ReceiveDeathlink;
                _deathlinkHooked = true;
                MelonLogger.Msg("YookaArchipelago: DeathLink receive handler hooked.");
            }
            else if (!APClient.DeathlinkEnabled && _deathlinkHooked && APClient.DeathlinkService != null)
            {
                APClient.DeathlinkService.OnDeathLinkReceived -= ReceiveDeathlink;
                _deathlinkHooked = false;
                _pendingDeathlink = false;
                APData.DeathlinkReceived = false;
                MelonLogger.Msg("YookaArchipelago: DeathLink receive handler unhooked.");
            }

            if (APClient.DeathlinkEnabled && _pendingDeathlink)
            {
                _pendingDeathlink = false;
                APData.DeathlinkReceived = true;
                MelonCoroutines.Start(DoDeathLink());
            }

            ApplyCapitalBKeyGate();
            CheckCapitalBCompletion();

            // Keep the goal check in the game's normal update loop. This is
            // intentionally independent of ItemReceived so that medals that
            // were already synchronized before connection can still complete
            // the goal.
            APClient.UpdateGoalItemCountAndCheckGoal();

            if (Input.GetKeyDown(KeyCode.F2))
            {
                gui.ToggleAPUI();
            }

#if DEBUG
            if (Input.GetKeyDown(KeyCode.F3))
            {
                gui.ToggleDebugMenu();
            }
#endif
        }
    }
}