using Il2Cpp;
using UnityEngine;
using MelonLoader;
using Il2Cpp;

namespace YookaArchipelago
{
    public class ArchipelagoGUI
    {
        private APClient _client = null!;
        private bool showGui = false;
        private bool showDebugMenu = false;
        private System.Action<PlayerMoves.Moves> onMoveToggled = null!;

        // Connection UI fields
        private string server = "localhost";
        private string port = "38281";
        private string name = "";
        private string password = "";
        private bool enableDeathlink = false;
        private bool previousEnableDeathlink = false;
        private Rect windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 125, 400, 280);
        private bool isDragging = false;
        private Vector2 dragOffset = Vector2.zero;

        // Debug menu fields
        private Vector2 debugMenuScrollPosition = Vector2.zero;

        // Short, friendly in-game notification used by gameplay gates.
        private string popupMessage = "";
        private float popupUntil = 0f;
        private GUIStyle popupStyle = null!;

        // GUI Styles
        private GUIStyle boxStyle = null!;
        private Texture2D backgroundTexture = null!;

        // Preferences
        private static MelonPreferences_Category preferenceCategory = null!;
        private MelonPreferences_Entry<string> serverPref = null!;
        private MelonPreferences_Entry<string> portPref = null!;
        private MelonPreferences_Entry<string> namePref = null!;
        private MelonPreferences_Entry<string> passwordPref = null!;
        private MelonPreferences_Entry<bool> deathlinkPref = null!;

        public ArchipelagoGUI()
        {
            InitializePreferences();
            LoadSettings();
            MelonEvents.OnGUI.Subscribe(DrawArchipelagoUI, 100);
            MelonEvents.OnGUI.Subscribe(DrawDebugMenu, 99);
            MelonEvents.OnGUI.Subscribe(DrawPopupMessage, 101);
        }

        private void InitializePreferences()
        {
            preferenceCategory = MelonPreferences.CreateCategory("ArchipelagoConnection");
            serverPref = preferenceCategory.CreateEntry<string>("server", "localhost", description: "Archipelago server address");
            portPref = preferenceCategory.CreateEntry<string>("port", "38281", description: "Archipelago server port");
            namePref = preferenceCategory.CreateEntry<string>("name", "", description: "Player name");
            passwordPref = preferenceCategory.CreateEntry<string>("password", "", description: "Server password");
            deathlinkPref = preferenceCategory.CreateEntry<bool>("deathlink", false, description: "Enable Deathlink");

        }

        private void LoadSettings()
        {
            server = serverPref.Value;
            port = portPref.Value;
            name = namePref.Value;
            password = passwordPref.Value;
            enableDeathlink = deathlinkPref.Value;
        }

        private void SaveSettings()
        {
            serverPref.Value = server;
            portPref.Value = port;
            namePref.Value = name;
            passwordPref.Value = password;
            deathlinkPref.Value = enableDeathlink;
            MelonPreferences.Save();
        }

        public void SetAPClient(APClient client)
        {
            _client = client;
        }

        public void SetMoveToggledCallback(System.Action<PlayerMoves.Moves> callback)
        {
            onMoveToggled = callback;
        }

        public void ToggleAPUI()
        {
            showGui = !showGui;
            Cursor.visible = showGui;
        }

        public void ToggleDebugMenu()
        {
            showDebugMenu = !showDebugMenu;
            Cursor.visible = showDebugMenu;
        }


        public void ShowPopupMessage(string message, float seconds = 5f)
        {
            popupMessage = message ?? "";
            popupUntil = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
        }

        private void DrawPopupMessage()
        {
            if (string.IsNullOrEmpty(popupMessage) || Time.realtimeSinceStartup > popupUntil)
                return;

            if (popupStyle == null)
            {
                popupStyle = new GUIStyle(GUI.skin.box);
                popupStyle.wordWrap = true;
                popupStyle.fontSize = 22;
                popupStyle.normal.textColor = Color.white;
            }

            float width = Mathf.Min(760f, Screen.width - 40f);
            var rect = new Rect((Screen.width - width) * 0.5f, 45f, width, 70f);
            GUI.Box(rect, popupMessage, popupStyle);
        }

        private void DrawArchipelagoUI()
        {
            if (showGui)
            {
                // Initialize or reinitialize boxStyle if texture was destroyed
                if (boxStyle == null || backgroundTexture == null)
                {
                    // Create a simple solid color texture
                    backgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    backgroundTexture.SetPixel(0, 0, new Color(0.2f, 0.2f, 0.2f, 0.8f)); // Dark gray
                    backgroundTexture.Apply();

                    boxStyle = new GUIStyle(GUI.skin.box);
                    boxStyle.normal.background = backgroundTexture;
                    boxStyle.border = new RectOffset(0, 0, 0, 0);
                }

                // Draw window background
                GUI.Box(windowRect, "", boxStyle);

                // Draw title bar for dragging
                Rect titleBar = new Rect(windowRect.x, windowRect.y, windowRect.width, 20);
                if (GUI.RepeatButton(titleBar, "Archipelago Connection"))
                {
                    if (!isDragging)
                    {
                        isDragging = true;
                        dragOffset = new Vector2(Input.mousePosition.x - windowRect.x, (Screen.height - Input.mousePosition.y) - windowRect.y);
                    }
                }

                // Handle dragging
                if (isDragging)
                {
                    Vector3 mousePos = Input.mousePosition;
                    windowRect.x = mousePos.x - dragOffset.x;
                    windowRect.y = (Screen.height - mousePos.y) - dragOffset.y;

                    if (!Input.GetMouseButton(0))
                    {
                        isDragging = false;
                    }
                }

                // Draw content
                GUI.Label(new Rect(windowRect.x + 10, windowRect.y + 30, 80, 20), "Server:");
                server = GUI.TextField(new Rect(windowRect.x + 100, windowRect.y + 30, 270, 20), server, 256);

                GUI.Label(new Rect(windowRect.x + 10, windowRect.y + 55, 80, 20), "Port:");
                port = GUI.TextField(new Rect(windowRect.x + 100, windowRect.y + 55, 270, 20), port, 256);

                GUI.Label(new Rect(windowRect.x + 10, windowRect.y + 80, 80, 20), "Name:");
                name = GUI.TextField(new Rect(windowRect.x + 100, windowRect.y + 80, 270, 20), name, 256);

                GUI.Label(new Rect(windowRect.x + 10, windowRect.y + 105, 80, 20), "Password:");
                password = GUI.PasswordField(new Rect(windowRect.x + 100, windowRect.y + 105, 270, 20), password, '*', 256);

                bool newDeathlinkState = GUI.Toggle(new Rect(windowRect.x + 10, windowRect.y + 130, 360, 20), enableDeathlink, "Enable Deathlink");
                if (newDeathlinkState != enableDeathlink)
                {
                    enableDeathlink = newDeathlinkState;
                    SaveSettings();
                    if (_client != null)
                    {
                        _client.ToggleDeathlink(enableDeathlink);
                    }
                }
                else
                {
                    enableDeathlink = newDeathlinkState;
                }

                if (GUI.Button(new Rect(windowRect.x + 10, windowRect.y + 155, 100, 30), "Connect"))
                {
                    MelonLogger.Msg($"Connecting to {server}:{port} as {name}");
                    SaveSettings();
                    _client = new APClient(server, int.Parse(port));
                    _client.Connect(name, enableDeathlink);
                }

                if (GUI.Button(new Rect(windowRect.x + 120, windowRect.y + 155, 100, 30), "Close"))
                {
                    showGui = false;
                }
            }
        }

        private void DrawDebugMenu()
        {
            if (showDebugMenu)
            {
                GUI.Box(new Rect(10, 10, 400, Screen.height - 20), "Debug Menu");

                debugMenuScrollPosition = GUI.BeginScrollView(new Rect(10, 30, 400, Screen.height - 50), debugMenuScrollPosition, new Rect(0, 0, 380, 25 * APData.playerMoves.Count));

                int index = 0;
                foreach (var moveName in Data.apNameToMoveName)
                {
                    bool previousValue = APData.playerMoves[moveName.Value];
                    APData.playerMoves[moveName.Value] = GUI.Toggle(new Rect(20, 10 + (index * 25), 360, 20), APData.playerMoves[moveName.Value], moveName.Key);

                    if (APData.playerMoves[moveName.Value] != previousValue && onMoveToggled != null)
                    {
                        onMoveToggled.Invoke(moveName.Value);
                    }
                    index++;
                }

                GUI.EndScrollView();
            }
        }
    }
}
