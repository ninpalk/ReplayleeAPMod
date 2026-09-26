using Il2Cpp;
using System.Collections.Generic;
namespace YookaArchipelago;

using MelonLoader;
public static class APData
{
    // Archipelago.MultiClient.Net raises ItemReceived from its networking
    // callback. Unity/IL2CPP objects must not be touched from that callback
    // thread, so newly received moves are queued here and applied from the
    // game's main thread on the next frame.
    private static readonly Queue<PlayerMoves.Moves> pendingMoves = new Queue<PlayerMoves.Moves>();
    private sealed class PendingWorldQuill
    {
        public string ItemName { get; }
        public string Identity { get; }

        public PendingWorldQuill(string itemName, string identity)
        {
            ItemName = itemName;
            Identity = identity;
        }
    }

    private static readonly Queue<PendingWorldQuill> pendingWorldQuills = new Queue<PendingWorldQuill>();
    private static readonly object pendingMovesLock = new object();
    // Location completion is populated only from the current Archipelago session.
    // It is never loaded from or written to Replaylee save data.
    public static List<long> locationsChecked = new List<long>();
    public static bool DeathlinkReceived = false;
    public static Dictionary<PlayerMoves.Moves, bool> playerMoves = new Dictionary<PlayerMoves.Moves, bool>()
    {
        {PlayerMoves.Moves.BasicAttack,false},
        {PlayerMoves.Moves.Glide,false},
        {PlayerMoves.Moves.Invisibility,false},
        {PlayerMoves.Moves.BasicAttackAir,false},
        {PlayerMoves.Moves.SonarShot,false},
        {PlayerMoves.Moves.SonarBoom, false},
        {PlayerMoves.Moves.SonarShield, false},
        {PlayerMoves.Moves.WheelRoll,false},
        {PlayerMoves.Moves.EatMk1,false},
        {PlayerMoves.Moves.EatMk2,false},
        {PlayerMoves.Moves.EatMk3,false},
        {PlayerMoves.Moves.WheelSpinAttack,false},
        {PlayerMoves.Moves.Fly,false},
        {PlayerMoves.Moves.GroundPound,false},
        {PlayerMoves.Moves.HighJump,false},
        {PlayerMoves.Moves.FartBubble,false},
        {PlayerMoves.Moves.TongueGrappleHook,false},
        {PlayerMoves.Moves.WheelDashAttack,false},
        {PlayerMoves.Moves.Jump,false}
};

    public static void AddPlayerMove(string move)
    {
        if (!Data.apNameToMoveName.TryGetValue(move, out var moveName))
        {
            Melon<YRAPMod>.Logger.Error($"ADDPLAYERMOVE: Unknown Archipelago move '{move}'.");
            return;
        }

        // Update our logical AP inventory immediately. This part is safe on
        // the network callback thread because it does not touch Unity.
        playerMoves[moveName] = true;

        lock (pendingMovesLock)
        {
            // Avoid queueing the same move multiple times if the server sends
            // duplicate copies or reconnect synchronization repeats it.
            if (!pendingMoves.Contains(moveName))
                pendingMoves.Enqueue(moveName);
        }

        Melon<YRAPMod>.Logger.Msg($"ADDPLAYERMOVE: {moveName} queued for main-thread application");
    }


    public static void AddWorldQuill(string itemName, string identity)
    {
        if (!Data.apNameToQuillStat.ContainsKey(itemName))
        {
            Melon<YRAPMod>.Logger.Error(
                $"ADDWORLDQUILL: Unknown Archipelago world quill item '{itemName}'.");
            return;
        }

        lock (pendingMovesLock)
        {
            // Reconnect synchronization can deliver the same AP item while
            // the original copy is still waiting for the Unity main thread.
            // Keep only one pending copy of that exact AP item.
            foreach (PendingWorldQuill pending in pendingWorldQuills)
            {
                if (pending.Identity == identity)
                    return;
            }

            pendingWorldQuills.Enqueue(new PendingWorldQuill(itemName, identity));
        }

        Melon<YRAPMod>.Logger.Msg(
            $"ADDWORLDQUILL: {itemName} queued for main-thread application ({identity})");
    }

    public static int ApplyPendingWorldQuills(System.Func<string, string, bool> applyQuill)
    {
        if (applyQuill == null)
            return 0;

        int appliedCount = 0;

        while (true)
        {
            PendingWorldQuill pendingItem;

            lock (pendingMovesLock)
            {
                if (pendingWorldQuills.Count == 0)
                    break;

                pendingItem = pendingWorldQuills.Peek();
            }

            string itemName = pendingItem.ItemName;

            bool applied = false;
            try
            {
                applied = applyQuill(itemName, pendingItem.Identity);
            }
            catch (System.Exception ex)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"APData: Exception while applying world quill {itemName}: {ex.Message}");
            }

            if (!applied)
                break;

            lock (pendingMovesLock)
            {
                if (pendingWorldQuills.Count > 0 &&
                    pendingWorldQuills.Peek().Identity == pendingItem.Identity)
                {
                    pendingWorldQuills.Dequeue();
                }
                else
                {
                    break;
                }
            }

            appliedCount++;
        }

        return appliedCount;
    }

    // Must be called from the Unity/Melon main thread. The supplied callback
    // must return true only when the move was successfully applied to the
    // current Unity player. Returning false keeps the move queued for the
    // next frame instead of losing it when a scene/player object is not ready.
    public static int ApplyPendingMoves(System.Func<PlayerMoves.Moves, bool> applyMove)
    {
        if (applyMove == null)
            return 0;

        int appliedCount = 0;

        while (true)
        {
            PlayerMoves.Moves move;

            lock (pendingMovesLock)
            {
                if (pendingMoves.Count == 0)
                    break;

                // Peek rather than dequeue. If the player is not ready yet,
                // leave this move at the front and try it again next frame.
                move = pendingMoves.Peek();
            }

            bool applied = false;
            try
            {
                applied = applyMove(move);
            }
            catch (System.Exception ex)
            {
                Melon<YRAPMod>.Logger.Error(
                    $"APData: Exception while applying move {move}: {ex.Message}");
            }

            if (!applied)
                break;

            lock (pendingMovesLock)
            {
                // Remove the exact move we just successfully applied.
                if (pendingMoves.Count > 0 && pendingMoves.Peek() == move)
                    pendingMoves.Dequeue();
                else
                    break;
            }

            appliedCount++;
        }

        return appliedCount;
    }

}
