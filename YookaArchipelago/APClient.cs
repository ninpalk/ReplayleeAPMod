using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Il2Cpp;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YookaArchipelago;

using Archipelago.MultiClient.Net;

public class APClient
{
    private static ArchipelagoSession Session = null!;
    public static DeathLinkService DeathlinkService = null!;
    public static bool DeathlinkEnabled { get; private set; } = false;
    public static bool QuillsanityEnabled { get; private set; } = false;
    private static string deathlinkPlayerName = "Player1";

    // Supplied by the APWorld through slot_data.
    private static string selectedGoal = "triple_pagie_medal_hunt";
    private static int triplePagieMedalGoal = 0;
    private static int goldenPagieGoal = 0;
    private static int capitalKeyGoal = 0;

    // Local state is deliberately separate from the server's state.
    private static bool goalSendAttempted = false;
    private static bool goalConfirmed = false;

    // Used as a fallback if ItemReceived is not raised for an item that is
    // already present in the synchronized item list.
    private static int lastKnownGoalItemCount = 0;

    // Archipelago resends the received-item history when reconnecting.
    // Duplicate suppression is intentionally restricted to items whose names
    // contain "Quill". Movement items and other normal items are allowed to
    // pass through every ItemReceived callback.
    private static readonly HashSet<string> processedReceivedItems = new HashSet<string>();
    private static readonly HashSet<string> pendingReceivedItems = new HashSet<string>();
    private static bool locationHandlerHooked = false;

    public APClient(string host = "localhost", int port = 38281)
    {
        Session = ArchipelagoSessionFactory.CreateSession(host, port);

        Session.Items.ItemReceived += ReceiveItem;
        Session.MessageLog.OnMessageReceived += ReceiveMessage;

        Melon<YRAPMod>.Logger.Msg("APClient: Created Archipelago session.");
    }

    public void Connect(string player = "Player1", bool enableDeathlink = true)
    {
        Melon<YRAPMod>.Logger.Msg(
            $"APClient: Connecting to Session as {player}");

        var loginResult = Session.TryConnectAndLogin(
            "Yooka-Replaylee",
            player,
            ItemsHandlingFlags.AllItems,
            Version.Parse("0.6.5"));

        if (!loginResult.Successful)
        {
            var loginFailure = (LoginFailure)loginResult;
            Melon<YRAPMod>.Logger.Error(
                $"APClient: Login failed: {string.Join(" | ", loginFailure.Errors)}");
            return;
        }

        var loginSuccess = (LoginSuccessful)loginResult;

        Melon<YRAPMod>.Logger.Msg(
            $"APClient: Connected. Slot={Session.ConnectionInfo.Slot}, " +
            $"Team={Session.ConnectionInfo.Team}, Game={Session.ConnectionInfo.Game}");

        ResetGoalState();
        ReadGoalFromSlotData(loginSuccess.SlotData);

        APData.locationsChecked = Session.Locations.AllLocationsChecked.ToList();
        Hooks.ArchipelagoQuillModeActive = true;
        Melon<YRAPMod>.Logger.Msg(
            "APClient: AP Quill mode active; physical Quills are checks only, received world-Quill items own the vanilla counter.");
        if (!locationHandlerHooked)
        {
            Hooks.LocationCollected += SendLocation;
            locationHandlerHooked = true;
        }

        // DeathLink remains completely inactive unless the client option is enabled.
        // Remember the actual AP slot/player name so outgoing DeathLinks identify
        // the correct source instead of always claiming to be "Player1".
        deathlinkPlayerName = player;
        DeathlinkEnabled = false;
        DeathlinkService = null!;
        if (enableDeathlink)
            EnableDeathlinkService();
        else
            Melon<YRAPMod>.Logger.Msg("APClient: DeathLink disabled; send/receive code is inactive.");

        // Establish the initial synchronized medal count and immediately test
        // the goal. This handles a goal that was already reached before the
        // client connected/reconnected.
        UpdateGoalItemCountAndCheckGoal("initial connection");
    }

    private static void ResetGoalState()
    {
        selectedGoal = "triple_pagie_medal_hunt";
        triplePagieMedalGoal = 0;
        goldenPagieGoal = 0;
        capitalKeyGoal = 0;
        goalSendAttempted = false;
        goalConfirmed = false;
        lastKnownGoalItemCount = 0;
        QuillsanityEnabled = false;
    }

    private static void ReadGoalFromSlotData(IReadOnlyDictionary<string, object> slotData)
    {
        if (slotData == null)
        {
            Melon<YRAPMod>.Logger.Error("APClient: SlotData was null. Cannot determine goal.");
            return;
        }

        if (slotData.TryGetValue("goal", out var goalMode))
            selectedGoal = Convert.ToString(goalMode) ?? "triple_pagie_medal_hunt";

        if (slotData.TryGetValue("quillsanity", out var quillsanityValue))
        {
            try
            {
                QuillsanityEnabled = Convert.ToInt32(quillsanityValue) != 0;
            }
            catch
            {
                bool parsed;
                QuillsanityEnabled = bool.TryParse(Convert.ToString(quillsanityValue), out parsed) && parsed;
            }
        }
        Melon<YRAPMod>.Logger.Msg($"APClient: Quillsanity shop checks {(QuillsanityEnabled ? "enabled" : "disabled")}.");

        try
        {
            if (slotData.TryGetValue("triple_pagie_medal_goal", out var medalValue))
                triplePagieMedalGoal = Convert.ToInt32(medalValue);
            if (slotData.TryGetValue("golden_pagie_goal", out var goldenValue))
                goldenPagieGoal = Convert.ToInt32(goldenValue);
            if (slotData.TryGetValue("capital_key_goal", out var capitalKeyValue))
                capitalKeyGoal = Convert.ToInt32(capitalKeyValue);
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error($"APClient: Could not read goal values from slot_data: {ex}");
            return;
        }

        if (selectedGoal == "defeat_capital_b")
        {
            if (capitalKeyGoal < 1 || capitalKeyGoal > 300)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"APClient: Invalid Capital Key goal {capitalKeyGoal}. Expected 1-300.");
                capitalKeyGoal = 0;
            }
        }
        else if (selectedGoal == "golden_pagie_hunt")
        {
            if (goldenPagieGoal < 1 || goldenPagieGoal > 300)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"APClient: Invalid Golden Pagie goal {goldenPagieGoal}. Expected 1-300.");
                goldenPagieGoal = 0;
            }
        }
        else
        {
            selectedGoal = "triple_pagie_medal_hunt";
            if (triplePagieMedalGoal < 1 || triplePagieMedalGoal > 10)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"APClient: Invalid Triple Pagie Medal goal {triplePagieMedalGoal}. Expected 1-10.");
                triplePagieMedalGoal = 0;
            }
        }
    }

    public static void SendLocation(string location)
    {
        if (Session == null)
        {
            Melon<YRAPMod>.Logger.Error(
                $"APClient: Cannot send location '{location}': Session is null.");
            return;
        }

        Melon<YRAPMod>.Logger.Msg($"APClient: Sending location: {location}");

        var locationId = Session.Locations.GetLocationIdFromName(
            "Yooka-Replaylee",
            location);

        if (!APData.locationsChecked.Contains(locationId))
        {
            APData.locationsChecked.Add(locationId);
            Session.Locations.CompleteLocationChecks(locationId);
        }
    }

    public static int GetReceivedItemCount(string itemName)
    {
        if (Session == null || string.IsNullOrEmpty(itemName))
            return -1;

        try
        {
            return Session.Items.AllItemsReceived.Count(item =>
                string.Equals(item.ItemName, itemName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error(
                $"APClient: Failed to count received item '{itemName}': {ex}");
            return -1;
        }
    }


    public static bool IsDefeatCapitalBGoal() => selectedGoal == "defeat_capital_b";
    public static bool IsGoldenPagieHuntGoal() => selectedGoal == "golden_pagie_hunt";
    public static bool IsTriplePagieHuntGoal() => selectedGoal == "triple_pagie_medal_hunt" || selectedGoal == "triple_pagie_hunt";
    public static int GetCapitalKeyGoal() => capitalKeyGoal;
    public static int GetCapitalKeyCount()
    {
        int count = GetReceivedItemCount("Capital Key");
        return count < 0 ? 0 : count;
    }

    public static int GetProgressivePagieDoorCount()
    {
        int count = GetReceivedItemCount("Progressive Pagie Door");
        if (count < 0)
            return 0;
        return Math.Min(4, count);
    }

    public static void MarkReceivedWorldQuillApplied(string itemIdentity)
    {
        pendingReceivedItems.Remove(itemIdentity);
        processedReceivedItems.Add(itemIdentity);

        Melon<YRAPMod>.Logger.Msg(
            $"APClient: World Quill item {itemIdentity} marked applied; reconnects will not reapply it.");
    }

    public void ReceiveItem(ReceivedItemsHelper receivedItemsHelper)
    {
        Melon<YRAPMod>.Logger.Msg("APClient: === ItemReceived callback fired ===");

        var item = receivedItemsHelper.PeekItem();
        var itemReceivedId = item.ItemId;
        var itemReceivedName = item.ItemDisplayName;

        // LocationId + sender slot is the stable identity of a received AP
        // item. The item ID itself is not needed for reconnect de-duplication,
        // and using the location makes the identity survive any wrapper/object
        // changes made by the networking library during synchronization.
        string itemIdentity = $"{item.LocationId}:{item.Player}";

        bool useDuplicateProtection =
            itemReceivedName.IndexOf("Quill", StringComparison.OrdinalIgnoreCase) >= 0;

        if (useDuplicateProtection &&
            (processedReceivedItems.Contains(itemIdentity) ||
             pendingReceivedItems.Contains(itemIdentity)))
        {
            Melon<YRAPMod>.Logger.Msg(
                $"APClient: Ignoring duplicate/reconnect copy of Quill item " +
                $"'{itemReceivedName}' from location {item.LocationId} during synchronization.");
            receivedItemsHelper.DequeueItem();
            return;
        }

        Melon<YRAPMod>.Logger.Msg(
            $"APClient: Received item '{itemReceivedName}' (ID {itemReceivedId}, " +
            $"Location {item.LocationId}, Player {item.Player}).");

        // Queue normal movement items immediately. The actual Unity/IL2CPP
        // object update is performed from YRAPMod.OnLateUpdate on the game's
        // main thread. This avoids touching Unity from Archipelago's network
        // callback thread, which can otherwise cause intermittent delays or
        // missed move activations.
        if (Data.apNameToMoveName.ContainsKey(itemReceivedName))
        {
            APData.AddPlayerMove(itemReceivedName);
        }

        bool isWorldQuill = Data.apNameToQuillStat.ContainsKey(itemReceivedName);
        if (isWorldQuill)
        {
            // Do not mark a Quill as processed until Replaylee's actual Quill
            // counter path has successfully accepted it. This prevents a
            // reconnect from being able to create a duplicate while also
            // preventing a failed main-thread application from being lost.
            pendingReceivedItems.Add(itemIdentity);
            APData.AddWorldQuill(itemReceivedName, itemIdentity);
        }
        else if (useDuplicateProtection)
        {
            // Only Quill-named items participate in reconnect de-duplication.
            // Moves and all other items are deliberately not recorded here.
            processedReceivedItems.Add(itemIdentity);
        }

        receivedItemsHelper.DequeueItem();

        if (itemReceivedName == "Progressive Pagie Door")
        {
            Melon<YRAPMod>.Logger.Msg(
                $"APClient: Progressive Pagie Door received; total is now {GetProgressivePagieDoorCount()}/4.");
            Hooks.GameStatManagerHooks.ApplyProgressivePagieDoors();
        }

        if ((selectedGoal == "triple_pagie_medal_hunt" && itemReceivedName == "Triple Pagie Medal") ||
            (selectedGoal == "golden_pagie_hunt" && itemReceivedName == "Golden Pagie") ||
            (selectedGoal == "defeat_capital_b" && itemReceivedName == "Capital Key"))
        {
            UpdateGoalItemCountAndCheckGoal($"{itemReceivedName} ItemReceived callback");
        }
    }

    /// <summary>
    /// Polling-safe goal check. This can be called from the game's normal
    /// update loop, so the goal does not depend exclusively on ItemReceived.
    /// </summary>
    public static void UpdateGoalItemCountAndCheckGoal(string reason = "poll")
    {
        if (Session == null || goalConfirmed)
            return;

        string goalItem;
        int required;

        if (selectedGoal == "defeat_capital_b")
        {
            // Capital Keys only unlock the final-boss entrance. They are not victory.
            // Victory is reported after the real HT - Capital Beaten check is sent.
            if (IsLocationChecked("HT - Capital Beaten"))
            {
                lastKnownGoalItemCount = GetCapitalKeyCount();
                SendGoal();
            }
            return;
        }

        if (selectedGoal == "golden_pagie_hunt")
        {
            goalItem = "Golden Pagie";
            required = goldenPagieGoal;
            if (required < 1 || required > 100)
                return;
        }
        else
        {
            goalItem = "Triple Pagie Medal";
            required = triplePagieMedalGoal;
            if (required < 1 || required > 10)
                return;
        }

        int count;
        try
        {
            count = Session.Items.AllItemsReceived.Count(
                item => item.ItemDisplayName == goalItem);
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error(
                $"APClient: Exception while counting {goalItem} items: {ex}");
            return;
        }

        lastKnownGoalItemCount = count;
        if (count >= required)
            SendGoal();
    }

    private static void SendGoal()
    {
        if (goalConfirmed || goalSendAttempted)
            return;

        goalSendAttempted = true;

        string goalName = selectedGoal == "defeat_capital_b"
            ? "DEFEAT CAPITAL B"
            : selectedGoal == "golden_pagie_hunt" ? "GOLDEN PAGIE HUNT" : "TRIPLE PAGIE MEDAL HUNT";
        int required = selectedGoal == "defeat_capital_b"
            ? capitalKeyGoal
            : selectedGoal == "golden_pagie_hunt" ? goldenPagieGoal : triplePagieMedalGoal;

        Melon<YRAPMod>.Logger.Msg(
            $"APClient: *** {goalName} GOAL REACHED ({lastKnownGoalItemCount}/{required}) ***");

        try
        {
            // MultiClient.Net 6.7.0's canonical API for reporting completion.
            // This sends the Archipelago CLIENT_GOAL status update.
            Session.SetGoalAchieved();

            Melon<YRAPMod>.Logger.Msg(
                "APClient: Session.SetGoalAchieved() completed without exception.");
            Melon<YRAPMod>.Logger.Msg(
                "APClient: Waiting for Archipelago server/client-status confirmation.");
        }
        catch (Exception ex)
        {
            // Permit a later retry if the first send actually failed.
            goalSendAttempted = false;

            Melon<YRAPMod>.Logger.Error(
                $"APClient: FAILED to send CLIENT_GOAL: {ex}");
        }
    }

    public static void ReceiveMessage(LogMessage message)
    {
        switch (message)
        {
            case PlayerSpecificLogMessage playerMessage:
                if (!playerMessage.IsActivePlayer)
                    return;

                Melon<YRAPMod>.Logger.Msg(
                    $"APClient: {playerMessage}");
                break;

            default:
                Melon<YRAPMod>.Logger.Msg(
                    $"APClient: {message}");
                break;
        }
    }

    public static bool IsLocationChecked(string location)
    {
        if (Session == null || APData.locationsChecked == null)
            return false;

        try
        {
            long locationId = Session.Locations.GetLocationIdFromName(
                "Yooka-Replaylee",
                location);

            return APData.locationsChecked.Contains(locationId);
        }
        catch (Exception ex)
        {
            Melon<YRAPMod>.Logger.Error(
                $"APClient: Failed to query AP check state for '{location}': {ex}");
            return false;
        }
    }

    public static long GetLocationIdFromName(string location)
    {
        return Session.Locations.GetLocationIdFromName(
            "Yooka-Replaylee",
            location);
    }

    private static void EnableDeathlinkService()
    {
        if (DeathlinkService == null)
            DeathlinkService = Session.CreateDeathLinkService();

        DeathlinkService.EnableDeathLink();
        DeathlinkEnabled = true;
        Melon<YRAPMod>.Logger.Msg("APClient: DeathLink enabled; send/receive code is active.");
    }

    public static void SendDeathlink()
    {
        if (!DeathlinkEnabled)
        {
            Melon<YRAPMod>.Logger.Msg("APClient: DeathLink disabled; local death was not sent.");
            return;
        }

        if (DeathlinkService == null)
        {
            Melon<YRAPMod>.Logger.Error(
                "APClient: Cannot send DeathLink because the service is not initialized.");
            return;
        }

        var deathlink = new DeathLink(deathlinkPlayerName);
        DeathlinkService.SendDeathLink(deathlink);
        Melon<YRAPMod>.Logger.Msg($"APClient: Sent DeathLink from {deathlinkPlayerName}.");
    }

    public void ToggleDeathlink(bool enable)
    {
        if (enable)
        {
            EnableDeathlinkService();
            return;
        }

        DeathlinkEnabled = false;
        if (DeathlinkService != null)
            DeathlinkService.DisableDeathLink();

        Melon<YRAPMod>.Logger.Msg("APClient: DeathLink disabled; send/receive code is inactive.");
    }
}
