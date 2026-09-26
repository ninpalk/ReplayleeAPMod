using Il2Cpp;
namespace YookaArchipelago;

public static class Data
{
    private static Dictionary<string, string> sceneWorld = new Dictionary<string, string>()
    {
        { "Level_00", "HT" },
        { "Level_01", "TT" },
        {"Level_02", "GlGl"},
        {"Level_03", "MM"},
        {"Level_04", "CC"},
        {"Level_05", "GaGa"},
        {"Level_07", "HT"},
    };
    public static string GetWorld(string scene)
    {
        scene = scene.Substring(0, 8);
        return sceneWorld[scene];
    }

    public static Dictionary<string, PlayerMoves.Moves> apNameToMoveName = new Dictionary<string, PlayerMoves.Moves>()
    {
        {"Tail Twirl", PlayerMoves.Moves.BasicAttack},
        {"Glide", PlayerMoves.Moves.Glide},
        {"Invisibility", PlayerMoves.Moves.Invisibility},
        {"Aerial Tail Twirl", PlayerMoves.Moves.BasicAttackAir},
        {"Sonar Shot", PlayerMoves.Moves.SonarShot },
        {"Sonar Boom", PlayerMoves.Moves.SonarBoom },
        {"Sonar Shield", PlayerMoves.Moves.SonarShield },
        {"Roll", PlayerMoves.Moves.WheelRoll },
        {"Elemental Fruits", PlayerMoves.Moves.EatMk1 },
        {"Cloud Yooka", PlayerMoves.Moves.EatMk2 },
        {"EatMk3", PlayerMoves.Moves.EatMk3 },
        {"Wheel Spin Attack", PlayerMoves.Moves.WheelSpinAttack},
        {"Fly", PlayerMoves.Moves.Fly},
        {"Ground Pound", PlayerMoves.Moves.GroundPound },
        {"High Jump", PlayerMoves.Moves.HighJump },
        {"Air Bubble", PlayerMoves.Moves.FartBubble},
        {"Tongue Grapple Hook", PlayerMoves.Moves.TongueGrappleHook},
        {"Wheel Dash Attack", PlayerMoves.Moves.WheelDashAttack},
        {"Jump", PlayerMoves.Moves.Jump},
    };

    public static Dictionary<string, string> apNameToQuillStat = new Dictionary<string, string>()
    {
        {"TT Quill", "QuilliesCollectedJungle"},
        {"GlGl Quill", "QuilliesCollectedGlacier"},
        {"MM Quill", "QuilliesCollectedSwamp"},
        {"CC Quill", "QuilliesCollectedCasino"},
        {"GaGa Quill", "QuilliesCollectedSpace"},
    };

}