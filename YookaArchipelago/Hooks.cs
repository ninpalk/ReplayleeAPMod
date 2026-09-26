using System.Linq;
using Il2CppPlaytonic.Game;
using System.Reflection;
using Il2CppPlaytonic.Core;
using Il2CppRewiredConsts;
using System.Collections;
using System.Collections.Generic;
using System.Net.Mime;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace YookaArchipelago;
using MelonLoader;
using HarmonyLib;
using Il2Cpp;
using UnityEngine.SceneManagement;
public class Hooks
{
    public delegate void ArchipelagoLocationHandler(string location);
    public static event ArchipelagoLocationHandler LocationCollected;

    // Set after a successful AP login. While active, physical Quill pickups
    // remain AP checks but are not allowed to increase Replaylee's vanilla
    // Quill counter. Only AP-received world-Quill items may do that.
    public static bool ArchipelagoQuillModeActive = false;
    public static bool ApplyingArchipelagoQuill = false;
    
    // Trowzer shop progression: item N cannot be selected until item N-1 is purchased.
    // This affects only the Trowzer store and leaves other menus untouched.
    [HarmonyPatch(typeof(TrowzerStoreItemController))]
    private static class TrowzerSequentialShopHook
    {
        [HarmonyPatch(nameof(TrowzerStoreItemController.OnSelect))]
        [HarmonyPrefix]
        private static bool OnSelectPrefix(TrowzerStoreItemController __instance)
        {
            try
            {
                if (__instance == null || __instance.mItemIndex <= 0)
                    return true;

                var screen = __instance.GetComponentInParent<TrowzerStoreScreenController>();
                if (screen == null || screen.mItemControllers == null)
                    return true;

                TrowzerStoreItemController previous = null;
                foreach (var controller in screen.mItemControllers)
                {
                    if (controller != null && controller.mItemIndex == __instance.mItemIndex - 1)
                    {
                        previous = controller;
                        break;
                    }
                }

                if (previous == null || previous.mPurchased)
                    return true;

                Melon<YRAPMod>.Logger.Msg(
                    $"Trowzer sequential shop: blocking item {__instance.mItemIndex}; " +
                    $"item {previous.mItemIndex} has not been purchased yet.");

                if (EventSystem.current != null && previous.gameObject != null)
                    EventSystem.current.SetSelectedGameObject(previous.gameObject);

                screen.mItemsIndex = previous.mItemIndex;
                return false;
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"Trowzer sequential shop check failed: {ex.Message}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(PagiePickup))]
    private static class PagiePickupHook
    {
        [HarmonyPatch(nameof(PagiePickup.GetCollectionStatus))]
        [HarmonyPostfix]
        private static void CoinPickup_GetCollectionStatus(PagiePickup __instance, ref CollectionStatus __result)
        {
            string sceneName = __instance.gameObject.scene.name;
            string locationName = string.Format("{0} - {1}", Data.GetWorld(sceneName), __instance.name);
            __result = APClient.IsLocationChecked(locationName) ? CollectionStatus.Collected : CollectionStatus.NotSpawned;
        }
        
        [HarmonyPatch(nameof(PagiePickup.Collect))]
        [HarmonyPrefix]
        private static bool PagieCollect(PagiePickup __instance)
        {
            string sceneName = __instance.gameObject.scene.name;
            Melon<YRAPMod>.Logger.Msg($"Pagie collected in scene:{sceneName}, object name: {__instance.name}");
            PagieChallengeData challenge = __instance.PagieChallengeData;
            string locationName = $"{Data.GetWorld(sceneName)} - {challenge.PagieName.GetLocalizedString()}";
            Melon<YRAPMod>.Logger.Msg($"{locationName}");
            if (LocationCollected != null)
                LocationCollected(locationName);
            else
                Melon<YRAPMod>.Logger.Warning($"Pagie AP check queued before AP location handler was ready: {locationName}");

            // Let Replaylee perform the normal Pagie collection. AP remains
            // authoritative for whether this location has already been checked.
            return true;
        }
        
    }
    // Trowzer shop purchases become AP checks only when Quillsanity is enabled.
    // The Trowzer UI can live in a persistent/UI scene, so do NOT use the item
    // controller's GameObject scene to identify the world.  Use the active gameplay
    // scene instead.  Also defer the check briefly because mPurchased may be set
    // after Purchase() returns.
    [HarmonyPatch(typeof(TrowzerStoreItemController))]
    private static class TrowzerStoreItemControllerHook
    {
        [HarmonyPatch(nameof(TrowzerStoreItemController.Purchase))]
        [HarmonyPostfix]
        private static void Purchase_Postfix(TrowzerStoreItemController __instance)
        {
            if (!APClient.QuillsanityEnabled || __instance == null)
                return;

            int itemIndex = __instance.mItemIndex;
            if (itemIndex < 0 || itemIndex > 5)
                return;

            MelonCoroutines.Start(ReportTrowzerPurchaseWhenCommitted(__instance, itemIndex));
        }

        private static IEnumerator ReportTrowzerPurchaseWhenCommitted(
            TrowzerStoreItemController controller, int itemIndex)
        {
            // Give the vanilla purchase flow time to commit mPurchased.  This also
            // avoids reporting a failed/aborted purchase as an AP location.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (controller == null)
                    yield break;

                if (controller.mPurchased)
                    break;

                yield return new WaitForSeconds(0.05f);
            }

            if (controller == null || !controller.mPurchased)
            {
                Melon<YRAPMod>.Logger.Msg(
                    $"Trowzer AP check: item {itemIndex} was not committed as purchased; no check sent.");
                yield break;
            }

            // Resolve the purchased stock group itself. WorldIndex is an internal
            // runtime/menu identifier (and is not 0..4), so never map it directly
            // to an AP world. Instead find the TrowzerWorldItems group whose
            // WorldIndex matches the currently displayed stock, then use that
            // group's SaveIndex as the stable world identity.
            var screen = controller.GetComponentInParent<TrowzerStoreScreenController>();
            if (screen == null || screen.mWorldItems == null)
            {
                Melon<YRAPMod>.Logger.Warning(
                    $"Trowzer AP check: could not find Trowzer world-item data for purchased item {itemIndex}.");
                yield break;
            }

            int runtimeWorldIndex = screen.WorldIndex;
            TrowzerStoreData.TrowzerWorldItems purchasedGroup = null;
            foreach (var group in screen.mWorldItems)
            {
                if (group != null && group.WorldIndex == runtimeWorldIndex)
                {
                    purchasedGroup = group;
                    break;
                }
            }

            if (purchasedGroup == null)
            {
                Melon<YRAPMod>.Logger.Warning(
                    $"Trowzer AP check: no TrowzerWorldItems group matched runtime WorldIndex={runtimeWorldIndex}; " +
                    $"purchased item index={itemIndex}. No scene fallback was used.");
                foreach (var group in screen.mWorldItems)
                {
                    if (group != null)
                        Melon<YRAPMod>.Logger.Msg(
                            $"Trowzer AP stock group: WorldIndex={group.WorldIndex}, SaveIndex={group.SaveIndex}, " +
                            $"Items={(group.Items == null ? -1 : group.Items.Length)}");
                }
                yield break;
            }

            int saveIndex = purchasedGroup.SaveIndex;
            string world = saveIndex switch
            {
                0 => "TT",
                1 => "GlGl",
                2 => "MM",
                3 => "CC",
                4 => "GaGa",
                _ => null
            };

            if (world == null)
            {
                Melon<YRAPMod>.Logger.Warning(
                    $"Trowzer AP check: matched stock WorldIndex={runtimeWorldIndex}, but SaveIndex={saveIndex} is unsupported; " +
                    $"purchased item index={itemIndex}. No scene fallback was used.");
                foreach (var group in screen.mWorldItems)
                {
                    if (group != null)
                        Melon<YRAPMod>.Logger.Msg(
                            $"Trowzer AP stock group: WorldIndex={group.WorldIndex}, SaveIndex={group.SaveIndex}, " +
                            $"Items={(group.Items == null ? -1 : group.Items.Length)}");
                }
                yield break;
            }

            string locationName = $"{world} - Shop {itemIndex + 1}";
            long locationId = APClient.GetLocationIdFromName(locationName);
            Melon<YRAPMod>.Logger.Msg(
                $"Trowzer AP check: purchased item index={itemIndex}, stock WorldIndex={runtimeWorldIndex}, " +
                $"SaveIndex={saveIndex}, item world={world}, location={locationName}, id={locationId}");

            if (locationId < 0)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"Trowzer AP check: '{locationName}' is not present in the AP data package/seed.");
                yield break;
            }

            if (!APClient.IsLocationChecked(locationName))
                LocationCollected?.Invoke(locationName);
            else
                Melon<YRAPMod>.Logger.Msg($"Trowzer AP check: '{locationName}' was already checked.");
        }
    }

    [HarmonyPatch(typeof(PagieDoor))]
    public static class PagieDoorHook
    {
        [HarmonyPatch(nameof(PagieDoor.Start))]
        [HarmonyPostfix]
        private static void OnPagieDoorStart(PagieDoor __instance)
        {
            // Only register the component here. Opening/removal is handled by the
            // delayed map pass (or when a new AP Progressive Pagie Door arrives).
            TrackPagieDoor(__instance);
            ApplyInteractionGate(__instance);
        }

        [HarmonyPatch(nameof(PagieDoor.OnSceneReactivated))]
        [HarmonyPostfix]
        private static void OnPagieDoorReactivated(PagieDoor __instance)
        {
            // Reactivation can fire repeatedly. Never re-run the destructive/open
            // operation here; just make sure the component is known to us.
            TrackPagieDoor(__instance);
            ApplyInteractionGate(__instance);
        }

        [HarmonyPatch(nameof(PagieDoor.IsOpen))]
        [HarmonyPostfix]
        private static void ProgressiveIsOpen(PagieDoor __instance, ref bool __result)
        {
            // For the four AP-controlled Pagie Doors, vanilla Pagie count must never
            // grant access. Their effective open state is controlled exclusively by
            // the Progressive Pagie Door item count. This also means interacting
            // with a door while holding enough vanilla Pagies still reports LOCKED
            // until the corresponding AP progression item has been received.
            int tier = GetDoorTier(__instance);
            if (tier > 0)
                __result = APClient.GetProgressivePagieDoorCount() >= tier;
        }

        // Replaylee's generated PagieDoor wrapper does not expose an
        // IsInteractable() method. Gate the actual controlled transition pieces
        // instead: SceneTransition doors use DoorTrigger, while Teleport doors
        // expose an underlying Door component.
        private static void ApplyInteractionGate(PagieDoor door)
        {
            if (door == null)
                return;

            int tier = GetDoorTier(door);
            if (tier <= 0)
                return;

            int count = APClient.GetProgressivePagieDoorCount();
            bool allowed = count >= tier;

            try
            {
                if (door.DoorTrigger != null)
                    door.DoorTrigger.enabled = allowed;
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"Progressive Pagie Door: could not gate DoorTrigger for {door.name}: {ex.Message}");
            }

            try
            {
                if (door.Door != null)
                    door.Door.enabled = allowed;
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"Progressive Pagie Door: could not gate underlying Door for {door.name}: {ex.Message}");
            }

            // SceneTransition-type Pagie Doors (notably the 55-Pagie Casino door)
            // do not rely on the normal Door/Collider path for interaction. Their
            // transition is driven by this NodeCanvas FSMOwner, so gate the FSM
            // itself whenever AP progression says the door must remain locked.
            try
            {
                if (door.SceneTransitionFSM != null)
                {
                    bool before = door.SceneTransitionFSM.enabled;
                    door.SceneTransitionFSM.enabled = allowed;
                    Melon<YRAPMod>.Logger.Msg(
                        $"Progressive Pagie Door SceneTransitionFSM gate: {door.name}, " +
                        $"enabled {before} -> {door.SceneTransitionFSM.enabled}, " +
                        $"Tier={tier}, AP Count={count}");
                }
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"Progressive Pagie Door: could not gate SceneTransitionFSM for {door.name}: {ex.Message}");
            }

            string doorName = door.gameObject != null ? door.gameObject.name : door.name;
            Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door interaction gate: {doorName}, Tier={tier}, AP Count={count} -> {(allowed ? "ENABLED" : "BLOCKED")}");
        }

        // Keep references to PagieDoor components as the game initializes them.
        // This avoids Unity scene/object enumeration APIs that are missing from
        // Replaylee's generated IL2CPP wrappers.
        private static readonly List<PagieDoor> _trackedPagieDoors = new List<PagieDoor>();

        // Session-persistent guard. Once SetPagieDoorOpen() succeeds for an index,
        // that tier is never sent through the door-opening/removal code again, even
        // if the player leaves and later returns to the map.
        private static readonly HashSet<int> _successfullyProcessedDoorIndices = new HashSet<int>();

        private static void TrackPagieDoor(PagieDoor door)
        {
            if (door == null)
                return;

            for (int i = 0; i < _trackedPagieDoors.Count; i++)
            {
                if (_trackedPagieDoors[i] == door)
                    return;
            }

            _trackedPagieDoors.Add(door);
        }

        public static PagieDoor GetTrackedPagieDoorByIndex(int index)
        {
            for (int i = _trackedPagieDoors.Count - 1; i >= 0; i--)
            {
                PagieDoor door = _trackedPagieDoors[i];
                try
                {
                    if (door == null || door.gameObject == null)
                    {
                        _trackedPagieDoors.RemoveAt(i);
                        continue;
                    }

                    if (door.Index == index)
                        return door;
                }
                catch
                {
                    _trackedPagieDoors.RemoveAt(i);
                }
            }

            return null;
        }

        public static void RecheckAllLoadedPagieDoors()
        {
            int found = 0;
            int opened = 0;

            // PagieDoor.Start / OnSceneReactivated register every door we actually
            // encounter. Re-check those known components 10 seconds after a map
            // load rather than calling unsupported Unity object/scene scanners.
            for (int i = _trackedPagieDoors.Count - 1; i >= 0; i--)
            {
                PagieDoor door = _trackedPagieDoors[i];
                try
                {
                    if (door == null || door.gameObject == null)
                    {
                        _trackedPagieDoors.RemoveAt(i);
                        continue;
                    }

                    found++;
                    if (TryApplyProgressiveDoor(door))
                        opened++;
                }
                catch (Exception ex)
                {
                    // A retained IL2CPP wrapper can become invalid after its scene
                    // unloads. Drop it and continue checking the current scene.
                    _trackedPagieDoors.RemoveAt(i);
                    Melon<YRAPMod>.Logger.Warning($"Discarded stale PagieDoor reference during delayed check: {ex.Message}");
                }
            }

            Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door 10-second scene check: tracked {found}, applied {opened}, AP count={APClient.GetProgressivePagieDoorCount()}/4.");
        }

        private static bool TryApplyProgressiveDoor(PagieDoor door)
        {
            try
            {
                int tier = GetDoorTier(door);
                int count = APClient.GetProgressivePagieDoorCount();
                int index = door.Index;
                ApplyInteractionGate(door);
                string doorName = door.gameObject != null ? door.gameObject.name : door.name;

                if (tier <= 0)
                {
                    Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door: {doorName}: Index={index}, Tier=unknown, AP Count={count} -> SKIP");
                    return false;
                }

                if (_successfullyProcessedDoorIndices.Contains(index))
                {
                    Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door: {doorName}: Index={index}, Tier={tier} -> ALREADY PROCESSED, SKIP");
                    return false;
                }

                if (count < tier)
                {
                    Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door: {doorName}: Index={index}, Tier={tier}, AP Count={count} -> LOCKED");
                    return false;
                }

                door.SetPagieDoorOpen();
                _successfullyProcessedDoorIndices.Add(index);
                Melon<YRAPMod>.Logger.Msg($"Progressive Pagie Door: {doorName}: Index={index}, Tier={tier}, AP Count={count} -> OPEN (marked processed for this game session)");
                return true;
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Error($"Failed applying Progressive Pagie Door to {door?.name}: {ex}");
                return false;
            }
        }

        private static void DumpPagieDoorDiagnostics(PagieDoor door, int index)
        {
            try
            {
                string path = GetObjectPath(door.transform);
                Melon<YRAPMod>.Logger.Msg($"========== PAGIE DOOR DIAGNOSTIC #{index} ==========");
                Melon<YRAPMod>.Logger.Msg($"PATH: {path}");
                Melon<YRAPMod>.Logger.Msg($"GAMEOBJECT: {door.gameObject.name}");
                Melon<YRAPMod>.Logger.Msg($"COMPONENT TYPE: {door.GetType().FullName}");
                Melon<YRAPMod>.Logger.Msg($"ACTIVE SELF: {door.gameObject.activeSelf}, ACTIVE HIERARCHY: {door.gameObject.activeInHierarchy}");
                Melon<YRAPMod>.Logger.Msg($"DETECTED TIER: {GetDoorTier(door)}");

                Type t = door.GetType();
                foreach (PropertyInfo prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.GetIndexParameters().Length != 0)
                        continue;
                    try
                    {
                        object value = prop.GetValue(door);
                        Melon<YRAPMod>.Logger.Msg($"PROPERTY: {prop.PropertyType.FullName} {prop.Name} = {FormatDiagnosticValue(value)}");
                    }
                    catch (Exception ex)
                    {
                        Melon<YRAPMod>.Logger.Msg($"PROPERTY: {prop.PropertyType.FullName} {prop.Name} = <unreadable: {ex.GetType().Name}>");
                    }
                }

                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object value = field.GetValue(door);
                        Melon<YRAPMod>.Logger.Msg($"FIELD: {field.FieldType.FullName} {field.Name} = {FormatDiagnosticValue(value)}");
                    }
                    catch (Exception ex)
                    {
                        Melon<YRAPMod>.Logger.Msg($"FIELD: {field.FieldType.FullName} {field.Name} = <unreadable: {ex.GetType().Name}>");
                    }
                }
                Melon<YRAPMod>.Logger.Msg("========== END PAGIE DOOR DIAGNOSTIC ==========");
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Error($"Pagie Door diagnostic failed: {ex}");
            }
        }

        private static string FormatDiagnosticValue(object value)
        {
            if (value == null)
                return "<null>";
            try
            {
                return value.ToString();
            }
            catch
            {
                return $"<{value.GetType().FullName}>";
            }
        }

        private static int GetDoorTier(PagieDoor door)
        {
            if (door == null)
                return 0;

            // Replaylee identifies the four hub Pagie Doors by Index:
            //   0 = Glacier / 15 Pagies
            //   1 = 35 Pagies
            //   2 = 55 Pagies
            //   3 = 75 Pagies
            // Archipelago's Progressive Pagie Door count is therefore Index + 1.
            try
            {
                int index = door.Index;
                if (index >= 0 && index <= 3)
                    return index + 1;
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"Could not read PagieDoor.Index for {door.name}: {ex.Message}");
            }

            return 0;
        }

        private static string GetObjectPath(Transform transform)
        {
            if (transform == null) return "<null>";
            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }


    // Vanilla Pagie-door progression guard. Replaylee calls
    // TryPlayTargetReachedCutscene when the vanilla Pagie requirement is reached,
    // and SetPagieDoorOpen when the player subsequently uses the door. Block both
    // paths until Archipelago's Progressive Pagie Door count satisfies this tier.
    // Calls made by the AP progression code are allowed because the AP count will
    // already meet the corresponding tier requirement.
    [HarmonyPatch]
    private static class PagieDoorVanillaProgressionGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(PagieDoor).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method == null || method.IsAbstract || method.IsGenericMethodDefinition)
                    continue;

                if (method.Name == "TryPlayTargetReachedCutscene" ||
                    method.Name == "SetPagieDoorOpen")
                    yield return method;
            }
        }

        [HarmonyPrefix]
        private static bool BlockVanillaPagieDoorProgression(PagieDoor __instance, MethodBase __originalMethod)
        {
            try
            {
                if (__instance == null || __originalMethod == null)
                    return true;

                int index = -1;
                try { index = __instance.Index; } catch { }

                // Only the four hub Pagie Doors are AP-controlled.
                if (index < 0 || index > 3)
                    return true;

                int tier = index + 1;
                int apCount = APClient.GetProgressivePagieDoorCount();
                string name = "<unknown>";
                try
                {
                    name = __instance.gameObject != null ? __instance.gameObject.name : __instance.name;
                }
                catch { }

                if (apCount < tier)
                {
                    Melon<YRAPMod>.Logger.Msg(
                        $"Progressive Pagie Door vanilla guard: BLOCKED {__originalMethod.Name} on {name}, " +
                        $"Index={index}, Tier={tier}, AP Count={apCount}/{tier}.");

                    if (__originalMethod.Name == "SetPagieDoorOpen")
                    {
                        const string friendlyMessage =
                            "Nice try, but you need another Progressive Pagie Door in order to move on. " +
                            "Reboot the game and then when you have your Progressive Pagie Door it will automatically move you through the door!";

                        Melon<YRAPMod>.Logger.Msg(friendlyMessage);

                        // Show the same friendly guard message in-game when vanilla
                        // Pagies try to open an AP-controlled Pagie Door.
                        try
                        {
                            if (YRAPMod.gui != null)
                                YRAPMod.gui.ShowPopupMessage(friendlyMessage, 8f);
                        }
                        catch (Exception popupEx)
                        {
                            Melon<YRAPMod>.Logger.Warning(
                                $"Could not show Progressive Pagie Door friendly popup: {popupEx.Message}");
                        }
                    }
                    return false;
                }

                Melon<YRAPMod>.Logger.Msg(
                    $"Progressive Pagie Door vanilla guard: ALLOWED {__originalMethod.Name} on {name}, " +
                    $"Index={index}, Tier={tier}, AP Count={apCount}/{tier}.");
                return true;
            }
            catch (Exception ex)
            {
                // Fail open on unexpected diagnostic/runtime issues so this guard
                // cannot softlock unrelated game logic.
                Melon<YRAPMod>.Logger.Warning($"Progressive Pagie Door vanilla guard failed: {ex.Message}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerMoves))]
    private static class PlayerMovesHook
    {    
        [HarmonyPatch(nameof(PlayerMoves.Start))]
        [HarmonyPostfix]
        private static void CheckPlayerMoves(PlayerMoves __instance)
        {
            foreach (PlayerMoves.Moves move in __instance.mMoveDictionary.Keys)
            {
                __instance.GetMove(move).mIsEnabledInGame = APData.playerMoves[move];
                Melon<YRAPMod>.Logger.Msg($"MOVE: {move}, STATE: {APData.playerMoves[move]}");
            }

        }

        [HarmonyPatch(nameof(PlayerMoves.EnableMoveInGame),new Type[]{typeof(PlayerMoves.Moves), typeof(bool)})]
        [HarmonyPrefix]
        private static bool EnableMoveInGame(PlayerMoves __instance)
        {
            return false;
        }
    }
    
    [HarmonyPatch(typeof(SavegameManagerExtensions), nameof(SavegameManagerExtensions.CollectCoin))]
    private static class SavegameManagerExtensionsCollectCoinHook
    {
        [HarmonyPrefix]
        private static bool CollectCoin_Prefix()
        {
            // Physical Quills still report their AP location through the existing
            // pickup hooks, but the vanilla counter is AP-item-owned while an AP
            // session is active. Synthetic reconciliation calls explicitly opt in.
            return !ArchipelagoQuillModeActive || ApplyingArchipelagoQuill;
        }
    }

    [HarmonyPatch(typeof(CoinPickup))]
    public static class CoinPickupHooks
    {

        [HarmonyPatch(nameof(CoinPickup.GetCollectionStatus))]
        [HarmonyPostfix]
        private static void CoinPickup_GetCollectionStatus(CoinPickup __instance, ref CollectionStatus __result)
        {
            string sceneName = __instance.gameObject.scene.name;
            string locationName = string.Format("{0} - {1}", Data.GetWorld(sceneName), __instance.name);
            __result = APClient.IsLocationChecked(locationName) ? CollectionStatus.Collected : CollectionStatus.NotSpawned;
        }
        
        [HarmonyPatch(nameof(CoinPickup.Collect))]
        [HarmonyPrefix]
        private static bool CoinPickup_Collect(CoinPickup __instance)
        {
            string sceneName = __instance.gameObject.scene.name;
            string locationName = $"{Data.GetWorld(sceneName)} - {__instance.name}";

            // AP owns the collection state for this location. Do not let the
            // vanilla CoinPickup.Collect() run, because it can write the
            // collection to Replaylee's normal save data. That save data may
            // be shared by multiple game instances, which can make one
            // instance appear to have checked another instance's AP location.
            //
            // Instead, report the location to this process's Archipelago
            // session and reproduce only the local visual collection effects.
            // If no AP location listener is installed, the client is not
            // connected/logged in. Let Replaylee run CoinPickup.Collect()
            // normally so the vanilla counter/save behavior is preserved.
            if (LocationCollected == null)
                return true;

            // Connected AP session: report the check and preserve the existing
            // AP-owned CoinPickup behavior.
            LocationCollected(locationName);
            __instance.PlayCollectEffects();
            __instance.gameObject.SetActive(false);

            // Skip the original only while AP is actively handling this pickup.
            return false;
        }
    }

    // Quills are handled by Replaylee's level-specific collectible system.
    // Do not replace, suppress, or modify the game's collection call. The
    // vanilla method must be allowed to finish its normal counter/save-memory
    // work first. We only observe the completed collection and report the
    // corresponding Archipelago location one frame later.
    [HarmonyPatch(typeof(LevelSpecificCollectiblesManager))]
    public static class QuillHooks
    {
        [HarmonyPatch(nameof(LevelSpecificCollectiblesManager.OnInstanceCollected))]
        [HarmonyPostfix]
        private static void LevelSpecificCollectibleCollected(
            LevelSpecificCollectibleInstance __0,
            bool __1)
        {
            if (__0 == null || !__1)
                return;

            string sceneName = __0.gameObject.scene.name;
            string quillName = FindQuillName(__0.gameObject);

            if (string.IsNullOrEmpty(quillName))
                return;

            string locationName = $"{Data.GetWorld(sceneName)} - {quillName}";

            // Do not touch the collectible GameObject or the game's collection
            // state here. Delaying only the AP notification gives Replaylee's
            // original OnInstanceCollected() call a complete frame to finish
            // its normal persistent-memory processing.
            MelonCoroutines.Start(ReportQuillLocationNextFrame(
                locationName,
                sceneName,
                __0.gameObject.name));
        }

        private static IEnumerator ReportQuillLocationNextFrame(
            string locationName,
            string sceneName,
            string objectName)
        {
            yield return null;

            Melon<YRAPMod>.Logger.Msg(
                $"Quill collected: scene={sceneName}, object={objectName}, AP location={locationName}");

            LocationCollected(locationName);
        }

        private static string FindQuillName(GameObject collectible)
        {
            // The AP world names these checks "Quill 0", "Quill 1", etc.
            // Replaylee's runtime object names can use different separators,
            // so normalize any Quill object name that contains its numeric ID.
            string[] names =
            {
                collectible.name,
                collectible.transform.parent != null ? collectible.transform.parent.name : string.Empty
            };

            foreach (string name in names)
            {
                string normalized = NormalizeQuillName(name);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
            }

            // Also check immediate children in case the collectible component
            // is attached to a wrapper object around the actual Quill object.
            for (int i = 0; i < collectible.transform.childCount; i++)
            {
                string normalized = NormalizeQuillName(
                    collectible.transform.GetChild(i).name);

                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
            }

            return string.Empty;
        }

        private static string NormalizeQuillName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            int quillIndex = name.IndexOf("Quill", StringComparison.OrdinalIgnoreCase);
            if (quillIndex < 0)
                return string.Empty;

            string suffix = name.Substring(quillIndex + "Quill".Length);
            suffix = suffix.Replace("_", " ")
                           .Replace("-", " ")
                           .Replace("(", " ")
                           .Replace(")", " ")
                           .Trim();

            string[] parts = suffix.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (int.TryParse(part, out int index))
                    return $"Quill {index}";
            }

            return string.Empty;
        }
    }

    [HarmonyPatch(typeof(PlayerDeathManager))]
    public static class PlayerDeathManagerHooks
    {
        [HarmonyPatch(nameof(PlayerDeathManager.StartPostDeathSequence))]
        [HarmonyPrefix]
        public static bool StartPostDeathSequence(bool allowRespawnToLastSafePosition)
        {
            Melon<YRAPMod>.Logger.Msg("Starting post-death sequence");
            if (!APClient.DeathlinkEnabled)
                return true;

            if (APData.DeathlinkReceived)
            {
                Melon<YRAPMod>.Logger.Msg("Not sending deathlink");
                return true;
            }
            Melon<YRAPMod>.Logger.Msg("Sending deathlink");
            APClient.SendDeathlink();
            return true;
        }

        [HarmonyPatch(nameof(PlayerDeathManager.OnFadeInStartEvent))]
        [HarmonyPrefix]
        public static void OnFadeInStartEvent()
        {
            if (!APClient.DeathlinkEnabled)
                return;

            Melon<YRAPMod>.Logger.Msg("Clearing Deathlink Flag");
            APData.DeathlinkReceived = false;
        }
        
    }

    [HarmonyPatch(typeof(FrontendControllerBase))]
    public static class GameFrontendControllerHooks
    {
        [HarmonyPatch(nameof(FrontendControllerBase.Start))]
        [HarmonyPrefix]
        private static void Start(GameFrontendController __instance)
        {
            var apButton = __instance.transform.Find("MainMenuScreen.UI/Content/Buttons/Wishlist");
            var text = apButton.GetComponent<MainMenuItemController>().ItemTitle;
            text.Text = "Archipelago";
            
            apButton.gameObject.SetActive(true);
            //Melon<YRAPMod>.Logger.Msg(apButton.name);
            //Melon<YRAPMod>.Logger.Msg(text);
        }
    }

    [HarmonyPatch(typeof(LocalisationHelper))]
    public static class LocalisationHelperHooks
    {
        [HarmonyPatch(nameof(LocalisationHelper.OnTextReloadedEvent))]
        [HarmonyPostfix]
        private static void SetText(LocalisationHelper __instance)
        {
            string parentName = __instance.transform.parent.parent.name;
            if (parentName == "Wishlist")
            {
                Melon<YRAPMod>.Logger.Msg("Changing Wishlist text");
                __instance.SetText("Archipelago");
            }
            
        }
    }

    [HarmonyPatch(typeof(UnityEngine.EventSystems.EventTrigger))]
    public static class EventTriggerHooks
    {
        [HarmonyPatch(nameof(UnityEngine.EventSystems.EventTrigger.OnSubmit))]
        [HarmonyPrefix]
        private static bool OnSubmit(UnityEngine.EventSystems.EventTrigger __instance)
        {
            if (__instance.gameObject.name == "Wishlist")
            {
                YRAPMod.gui.ToggleAPUI();
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(GameStatManager))]
    public static class GameStatManagerHooks
    {
        private static bool _initializedDoorState;

        public static void ApplyProgressivePagieDoors()
        {
            // GameStatManager.SetPagieDoorOpen(int) is not exposed in this game's
            // generated IL2CPP wrapper. Apply progression to the actual PagieDoor
            // components instead.
            PagieDoorHook.RecheckAllLoadedPagieDoors();
        }

        [HarmonyPatch(nameof(GameStatManager.Start))]
        [HarmonyPostfix]
        private static void GetCurrentValue(GameStatManager __instance)
        {
            Melon<YRAPMod>.Logger.Msg("Started Game Stat Manager");

            if (_initializedDoorState)
                return;

            _initializedDoorState = true;

            try
            {
                // The generated IL2CPP GameStatManager wrapper does not expose
                // these two native methods as C# members, so invoke them by
                // reflection instead of creating compile-time references.
                MethodInfo setDoorOpen = typeof(GameStatManager).GetMethod(
                    "SetPagieDoorOpen",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int) },
                    null);

                MethodInfo save = typeof(GameStatManager).GetMethod(
                    "Save",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                if (setDoorOpen == null)
                {
                    Melon<YRAPMod>.Logger.Warning(
                        "GameStatManager.SetPagieDoorOpen(int) is not exposed by the generated wrapper; existing PagieDoor hooks will still open doors when they initialize.");
                }
                else
                {
                    // Door state 0 is the initial hub door and remains available.
                    // States 1-4 correspond to the 15/35/55/75 Pagie doors.
                    setDoorOpen.Invoke(null, new object[] { 0 });

                    int progressiveDoors = APClient.GetProgressivePagieDoorCount();
                    for (int doorIndex = 1; doorIndex <= progressiveDoors; doorIndex++)
                    {
                        setDoorOpen.Invoke(null, new object[] { doorIndex });
                    }

                    Melon<YRAPMod>.Logger.Msg(
                        $"Applied Progressive Pagie Door state: {progressiveDoors}/4 " +
                        "(1=15, 2=35, 3=55, 4=75).");
                }

                if (save == null)
                {
                    Melon<YRAPMod>.Logger.Warning(
                        "GameStatManager.Save() is not exposed by the generated wrapper; Pagie-door state will be left to the game's normal save operation.");
                }
                else
                {
                    save.Invoke(null, null);
                    Melon<YRAPMod>.Logger.Msg(
                        "Saved Pagie-door state after GameStatManager initialization.");
                }
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"Failed to initialize Pagie-door save state: {ex}");
            }
        }
    }


    // Temporary vanilla Pagie-door API isolation test.
    // Reads the generated GameStatManager Pagie APIs and then opens ONLY the
    // vanilla 55-Pagie door (PagieDoor Index 2). No Save() call is made.
    public static class PagieDoorApiIsolationDiagnostic
    {
        // Temporary API test is deliberately one-shot for the whole game session.
        // CapitalBOffice_Cutscenes and other additive scene changes must not call
        // SetPagieDoorOpen() again and retrigger the target-reached cutscene.
        private static bool _index2TestAppliedThisSession;

        public static void Run55DoorTest(string sceneName)
        {
            if (_index2TestAppliedThisSession)
            {
                Melon<YRAPMod>.Logger.Msg($"PAGIE DOOR API TEST: Index 2 already processed this session; skipping for scene '{sceneName}'.");
                return;
            }

            try
            {
                Melon<YRAPMod>.Logger.Msg($"========== PAGIE DOOR API TEST: {sceneName} ==========");

                Type gsm = typeof(GameStatManager);
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

                MethodInfo getPagieCount0 = gsm.GetMethods(flags).FirstOrDefault(m => m.Name == "GetPagieCount" && m.IsStatic && m.GetParameters().Length == 0);
                MethodInfo getPagieCount1 = gsm.GetMethods(flags).FirstOrDefault(m => m.Name == "GetPagieCount" && m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                MethodInfo getUnspent = gsm.GetMethods(flags).FirstOrDefault(m => m.Name == "GetUnspentPagies" && m.IsStatic && m.GetParameters().Length == 0);
                MethodInfo getDoorOpen = gsm.GetMethods(flags).FirstOrDefault(m => m.Name == "GetIsPagieDoorOpen" && m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                MethodInfo setDoorOpen = gsm.GetMethods(flags).FirstOrDefault(m => m.Name == "SetPagieDoorOpen" && m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));

                LogInvoke("GetPagieCount()", getPagieCount0, null);
                LogInvoke("GetUnspentPagies()", getUnspent, null);

                if (getPagieCount1 != null)
                {
                    for (int i = 0; i <= 5; i++)
                        LogInvoke($"GetPagieCount({i})", getPagieCount1, new object[] { i });
                }
                else
                {
                    Melon<YRAPMod>.Logger.Warning("PAGIE DOOR API: GetPagieCount(int) not exposed through reflection.");
                }

                if (getDoorOpen != null)
                {
                    for (int i = 0; i <= 3; i++)
                        LogInvoke($"GetIsPagieDoorOpen({i}) BEFORE", getDoorOpen, new object[] { i });
                }
                else
                {
                    Melon<YRAPMod>.Logger.Warning("PAGIE DOOR API: GetIsPagieDoorOpen(int) not exposed through reflection.");
                }

                // Log every generated GetPagieDoorRequirement signature we can see.
                try
                {
                    foreach (Type t in gsm.Assembly.GetTypes())
                    {
                        foreach (MethodInfo m in t.GetMethods(flags))
                        {
                            if (m.Name != "GetPagieDoorRequirement")
                                continue;
                            string parms = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.FullName + " " + x.Name));
                            Melon<YRAPMod>.Logger.Msg($"PAGIE DOOR API: requirement method = {t.FullName}.{m.Name}({parms}) -> {m.ReturnType.FullName}; static={m.IsStatic}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Melon<YRAPMod>.Logger.Warning($"PAGIE DOOR API: requirement signature scan failed: {ex.Message}");
                }

                const int targetDoorIndex = 2; // Replaylee hub Index 2 = vanilla 55-Pagie door.
                bool openedViaGameStatManager = false;
                if (setDoorOpen != null)
                {
                    try
                    {
                        setDoorOpen.Invoke(null, new object[] { targetDoorIndex });
                        openedViaGameStatManager = true;
                        _index2TestAppliedThisSession = true;
                        Melon<YRAPMod>.Logger.Msg("PAGIE DOOR API TEST: called GameStatManager.SetPagieDoorOpen(2) ONCE this session for ONLY the 55-Pagie door. Save() was NOT called.");
                    }
                    catch (Exception ex)
                    {
                        Melon<YRAPMod>.Logger.Error($"PAGIE DOOR API TEST: SetPagieDoorOpen(2) failed: {ex}");
                    }
                }
                else
                {
                    Melon<YRAPMod>.Logger.Warning("PAGIE DOOR API TEST: static GameStatManager.SetPagieDoorOpen(int) is not reflection-visible; trying the loaded PagieDoor Index 2 instance instead.");
                }

                if (!openedViaGameStatManager)
                {
                    try
                    {
                        PagieDoor target = PagieDoorHook.GetTrackedPagieDoorByIndex(targetDoorIndex);
                        if (target != null)
                        {
                            target.SetPagieDoorOpen();
                            _index2TestAppliedThisSession = true;
                            Melon<YRAPMod>.Logger.Msg($"PAGIE DOOR API TEST: called instance SetPagieDoorOpen() ONCE this session on '{target.gameObject.name}' (Index 2). Save() was NOT called.");
                        }
                        else
                        {
                            Melon<YRAPMod>.Logger.Warning("PAGIE DOOR API TEST: no loaded PagieDoor with Index 2 was found in this scene.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Melon<YRAPMod>.Logger.Error($"PAGIE DOOR API TEST: instance Index 2 fallback failed: {ex}");
                    }
                }

                if (getDoorOpen != null)
                {
                    for (int i = 0; i <= 3; i++)
                        LogInvoke($"GetIsPagieDoorOpen({i}) AFTER", getDoorOpen, new object[] { i });
                }

                Melon<YRAPMod>.Logger.Msg("PAGIE DOOR API TEST: only door Index 2 was intentionally changed; no Save() call was made.");
                Melon<YRAPMod>.Logger.Msg("========== END PAGIE DOOR API TEST ==========");
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Error($"PAGIE DOOR API TEST failed: {ex}");
            }
        }

        private static void LogInvoke(string label, MethodInfo method, object[] args)
        {
            if (method == null)
            {
                Melon<YRAPMod>.Logger.Warning($"PAGIE DOOR API: {label} = <method not exposed>");
                return;
            }
            try
            {
                object value = method.Invoke(null, args);
                Melon<YRAPMod>.Logger.Msg($"PAGIE DOOR API: {label} = {value ?? "<void/null>"}");
            }
            catch (Exception ex)
            {
                Melon<YRAPMod>.Logger.Warning($"PAGIE DOOR API: {label} failed: {ex.GetBaseException().Message}");
            }
        }
    }


    // Capital Cashino's 150 physical Casino Tokens are AP locations.
    // Replaylee exposes their persistent collection index directly as 1-150,
    // so the AP location name deliberately uses that exact index (no +1).
    public static void InstallCasinoTokenHooks(Harmony harmony)
    {
        try
        {
            MethodInfo postfix = AccessTools.Method(typeof(Hooks), nameof(CasinoTokenCollectionPostfix));
            int patched = 0;
            var seen = new HashSet<MethodBase>();

            foreach (Type type in new[] { typeof(SavegameManagerExtensions), typeof(GameStatManager) })
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method == null || method.Name.IndexOf("CasinoChip", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    bool hasIndex = parameters.Any(p => p.ParameterType == typeof(int));
                    bool hasStatus = parameters.Any(p => p.ParameterType == typeof(CollectionStatus));
                    if (!hasIndex || !hasStatus || !seen.Add(method))
                        continue;

                    harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    patched++;
                    Melon<YRAPMod>.Logger.Msg($"Casino Token AP checks: patched {type.FullName}.{method.Name}.");
                }
            }

            if (patched == 0)
                Melon<YRAPMod>.Logger.Warning("Casino Token AP checks: no CasinoChip status setter was found; token checks are not hooked.");
            else
                Melon<YRAPMod>.Logger.Msg($"Casino Token AP checks: installed {patched} hook(s) for exact indices 1-150.");
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error($"Casino Token AP checks: failed to install hooks: {ex}");
        }
    }

    private static void CasinoTokenCollectionPostfix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (LocationCollected == null || __args == null)
                return;

            string activeScene = SceneManager.GetActiveScene().name ?? string.Empty;
            if (!activeScene.StartsWith("Level_04_Casino", StringComparison.Ordinal))
                return;

            int index = -1;
            CollectionStatus? status = null;
            foreach (object arg in __args)
            {
                if (arg is int i) index = i;
                else if (arg is CollectionStatus cs) status = cs;
            }

            if (index < 1 || index > 150 || status != CollectionStatus.Collected)
                return;

            string locationName = $"CC - Casino Token {index}";
            if (APClient.IsLocationChecked(locationName))
                return;

            Melon<YRAPMod>.Logger.Msg(
                $"Casino Token collected: scene={activeScene}, index={index}, method={__originalMethod?.Name} -> {locationName}");
            LocationCollected(locationName);
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Warning($"Casino Token AP check failed: {ex.Message}");
        }
    }

    // Individual Pagie Piece checks (8 per Grand Tome / 40 total).
    // Installed dynamically because PagiePiecePickup lives in the generated IL2CPP
    // assembly and its wrapper name can vary slightly between generated builds.
    public static void InstallPagiePieceHooks(Harmony harmony)
    {
        try
        {
            // PagiePiecePickup is a compile-time generated IL2CPP wrapper in this
            // Replaylee build.  Resolve it directly instead of AccessTools.TypeByName().
            // TypeByName scans every loaded assembly; scanning Assembly-CSharp on this
            // IL2CPP build triggers ReflectionTypeLoadException warnings during boot.
            Type pieceType = typeof(PagiePiecePickup);

            MethodInfo collect = AccessTools.Method(pieceType, "Collect");
            if (collect == null)
            {
                Melon<YRAPMod>.Logger.Warning($"Pagie Piece AP checks: {pieceType.FullName}.Collect was not found; hook not installed.");
                return;
            }

            MethodInfo postfix = AccessTools.Method(typeof(Hooks), nameof(PagiePieceCollectPostfix));
            harmony.Patch(collect, postfix: new HarmonyMethod(postfix));
            Melon<YRAPMod>.Logger.Msg($"Pagie Piece AP checks: patched {pieceType.FullName}.Collect for 40 mapped checks.");
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error($"Pagie Piece AP checks: failed to install hook: {ex}");
        }
    }

    private static void PagiePieceCollectPostfix(object __instance)
    {
        try
        {
            if (__instance == null || LocationCollected == null)
                return;

            Component component = __instance as Component;
            if (component == null || component.gameObject == null)
                return;

            string scene = component.gameObject.scene.name;
            string objectName = component.gameObject.name ?? string.Empty;
            int identifier = ReadPagiePieceIdentifier(__instance, objectName);

            if (TryGetPagiePieceLocation(scene, identifier, objectName, out string locationName))
            {
                Melon<YRAPMod>.Logger.Msg($"Pagie Piece collected: scene={scene}, identifier={identifier}, object={objectName} -> {locationName}");
                LocationCollected(locationName);
            }
            else
            {
                Melon<YRAPMod>.Logger.Warning($"Unmapped Pagie Piece: scene={scene}, identifier={identifier}, object={objectName}");
            }
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Warning($"Pagie Piece AP check failed: {ex.Message}");
        }
    }

    private static int ReadPagiePieceIdentifier(object instance, string objectName)
    {
        Type type = instance.GetType();
        foreach (string name in new[] { "Identifier", "identifier", "mIdentifier", "Index", "index", "mIndex" })
        {
            try
            {
                PropertyInfo property = AccessTools.Property(type, name);
                if (property != null)
                    return Convert.ToInt32(property.GetValue(instance));
                FieldInfo field = AccessTools.Field(type, name);
                if (field != null)
                    return Convert.ToInt32(field.GetValue(instance));
            }
            catch { }
        }

        // Fallback to the trailing number in names such as PagiePiecePickup7 or
        // PagiePiecePickup 7. The mapped Glitterglaze exception is handled by name.
        for (int i = objectName.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(objectName[i]))
            {
                if (i < objectName.Length - 1 && int.TryParse(objectName.Substring(i + 1), out int parsed))
                    return parsed;
                break;
            }
        }
        return -1;
    }

    private static bool TryGetPagiePieceLocation(string scene, int identifier, string objectName, out string location)
    {
        location = null;
        string world;
        if (scene.StartsWith("Level_01_Jungle_", StringComparison.Ordinal)) world = "TT";
        else if (scene.StartsWith("Level_02_Glacier_", StringComparison.Ordinal)) world = "GlGl";
        else if (scene.StartsWith("Level_03_Swamp_", StringComparison.Ordinal)) world = "MM";
        else if (scene.StartsWith("Level_04_Casino_", StringComparison.Ordinal)) world = "CC";
        else if (scene.StartsWith("Level_05_Space_", StringComparison.Ordinal)) world = "GaGa";
        else return false;

        int checkNumber = identifier;

        // Replaylee's Glitterglaze PagiePiecePickup7 reports Identifier=0.
        // In Archipelago this is deliberately exposed as "GlGl - Pagie Piece 7".
        if (world == "GlGl" && scene == "Level_02_Glacier_Design" &&
            (identifier == 0 || objectName.Replace(" ", "").EndsWith("Pickup7", StringComparison.OrdinalIgnoreCase)))
            checkNumber = 7;

        if (checkNumber < 1 || checkNumber > 8)
            return false;

        location = $"{world} - Pagie Piece {checkNumber}";
        return true;
    }

}
// Defeat Capital B goal: suppress Replaylee's vanilla 120-Pagie eligibility
// before it can run the Capital B entrance logic. Values[291] is still held at
// zero by Main.ApplyCapitalBKeyGate(), but that happens in LateUpdate and is too
// late to stop the interaction that sets/consumes the flag in the same frame.
[HarmonyPatch]
internal static class CapitalBVanillaPagieEligibilityBoolGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (MethodInfo method in typeof(SavegameManagerExtensions).GetMethods(flags))
        {
            if (method == null || method.ReturnType != typeof(bool))
                continue;
            if (method.Name == "HasUnspentPagies")
                yield return method;
        }
    }

    [HarmonyPostfix]
    private static void BlockVanilla120PagieEligibility(MethodBase __originalMethod, ref bool __result)
    {
        if (!APClient.IsDefeatCapitalBGoal())
            return;
        int required = APClient.GetCapitalKeyGoal();
        if (required < 1 || APClient.GetCapitalKeyCount() >= required)
            return;

        if (__result)
        {
            __result = false;
            Melon<YRAPMod>.Logger.Msg(
                $"Capital B key gate: BLOCKED vanilla {__originalMethod.Name} eligibility while Capital Keys=" +
                $"{APClient.GetCapitalKeyCount()}/{required}. Values[291] remains AP-controlled.");
        }
    }
}

[HarmonyPatch]
internal static class CapitalBVanillaPagieCountGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (MethodInfo method in typeof(SavegameManagerExtensions).GetMethods(flags))
        {
            if (method == null || method.ReturnType != typeof(int))
                continue;
            if (method.Name == "GetPagieCount" || method.Name == "GetAllPagieCount" || method.Name == "GetUnspentPagies")
                yield return method;
        }
    }

    [HarmonyPostfix]
    private static void HideVanilla120PagieThreshold(MethodBase __originalMethod, ref int __result)
    {
        if (!APClient.IsDefeatCapitalBGoal())
            return;
        int required = APClient.GetCapitalKeyGoal();
        if (required < 1 || APClient.GetCapitalKeyCount() >= required)
            return;

        // Only interfere at the Capital-B threshold. Ordinary Pagie values below
        // 120 are left untouched, minimizing impact on unrelated UI/game logic.
        if (__result >= 120)
        {
            int old = __result;
            __result = 119;
            Melon<YRAPMod>.Logger.Msg(
                $"Capital B key gate: capped vanilla {__originalMethod.Name} result {old} -> 119 while Capital Keys=" +
                $"{APClient.GetCapitalKeyCount()}/{required}.");
        }
    }
}
