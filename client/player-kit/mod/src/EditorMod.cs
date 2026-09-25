// ATT Prefab Editor - client mod: flat desktop freecam + world editor for a VR game.
// Client = render + input only. NO world mutation this session (that lands session 2,
// through the server `edit` module). This mod adds a detached, non-stereo camera that
// renders the REAL in-process scene to the desktop window while the headset keeps its
// own view - "mechanism A" from docs/CLIENT-EDITOR.md. Default OFF; F1 toggles.
//
// INPUT: ATT has "Active Input Handling = Input System Package (New)" in Player Settings,
// so the legacy UnityEngine.Input class THROWS on every read (confirmed live from the
// MelonLoader log). All input goes through UnityEngine.InputSystem (Keyboard.current /
// Mouse.current) instead - same path UnityExplorer uses.
//
// The render path (the acceptance gate for this session):
//   1. XRSettings.showDeviceView = false  -> stop mirroring the HMD eye-texture to the
//      DESKTOP window. The headset is unaffected (it renders from its own XR pass).
//   2. our Camera (stereoTargetEye=None, targetDisplay=0, high depth, skybox clear)
//      renders the scene to Display.main -> it shows up on the monitor.
//   3. OnGUI draws the HUD on top of that desktop window.
// If the desktop mirror still wins, F2 flips the mirror lever live and F3 cycles the
// target display (0 -> 2 -> 1), so one launch can test mechanism A AND the targetDisplay
// routing fallback. Defaults come from MelonPreferences (StopVRMirror / TargetDisplay).
//
// C# 5 / net35 - no interpolation, no ?., no expression-bodied members.

using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(PrefabEditorMod.EditorMod), "ATT Prefab Editor", "1.7.0", "ATT Prefab Editor")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace PrefabEditorMod
{
    public class EditorMod : MelonMod
    {
        // --- config (MelonPreferences: <game>\UserData\MelonPreferences.cfg) ---
        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<Key> _toggleKey;       // default F1 (new Input System Key)
        private MelonPreferences_Entry<Key> _moveUpKey;
        private MelonPreferences_Entry<Key> _moveDownKey;
        private MelonPreferences_Entry<float> _moveSpeed;     // base units/sec
        private MelonPreferences_Entry<float> _fastMultiplier;// while holding Shift
        private MelonPreferences_Entry<float> _lookSens;      // mouse-look factor
        private MelonPreferences_Entry<bool> _invertY;
        private MelonPreferences_Entry<int> _targetDisplay;   // 0 = main desktop window
        private MelonPreferences_Entry<bool> _stopVRMirror;   // XRSettings.showDeviceView=false
        private MelonPreferences_Entry<string> _panelUrl;     // session 2: panel base URL (tunnel endpoint)
        private MelonPreferences_Entry<float> _gizmoGrid;     // session 3: Ctrl-snap grid (m)
        private MelonPreferences_Entry<bool> _gizmoGhost;     // session 3: ghost-preview drags
        private MelonPreferences_Entry<float> _outlineRadius; // outline-all radius (m)

        // live render-path switches (F2/F3) - find the working combo on any rig
        // without a rebuild. Not persisted; seeded from prefs on each Enable.
        private const Key K_MIRROR = Key.F2;
        private const Key K_DISPLAY = Key.F3;

        private const float LOOK_SCALE = 0.05f; // pixels-of-mouse-delta -> degrees, times sens

        // --- runtime state ---
        private bool _active;
        private GameObject _camGO;
        private Camera _cam;

        // session 2: server-authoritative edit transport + pick/inspect/move/rotate/delete
        private EditApi _api;
        private EditSelection _sel;
        private float _yaw;
        private float _pitch;
        private float _speed;               // live base speed (wheel-adjusted)
        private bool _mirrorStopped;        // effective XRSettings.showDeviceView==false
        private int _displayNow;            // effective camera targetDisplay

        // saved host state so toggling OFF restores the game exactly
        private bool _prevShowDeviceView = true;
        private bool _capturedSDV;
        private CursorLockMode _prevCursorLock = CursorLockMode.None;
        private bool _prevCursorVisible = true;
        private bool _savedCursor;

        // Fixed planar controls; vertical controls are user-configurable preferences.
        private const Key K_FWD = Key.W;
        private const Key K_BACK = Key.S;
        private const Key K_LEFT = Key.A;
        private const Key K_RIGHT = Key.D;

        public override void OnInitializeMelon()
        {
            _cfg = MelonPreferences.CreateCategory("PrefabEditor");
            _toggleKey = _cfg.CreateEntry<Key>("ToggleKey", Key.F1,
                "Toggle key", "Key that toggles the flat editor camera (new Input System Key names).");
            _moveSpeed = _cfg.CreateEntry<float>("MoveSpeed", 6f,
                "Move speed", "Base fly speed in units/second (mouse wheel adjusts live).");
            _moveUpKey = _cfg.CreateEntry<Key>("MoveUpKey", Key.Space,
                "Camera up key", "New Input System key that moves the editor camera straight up.");
            _moveDownKey = _cfg.CreateEntry<Key>("MoveDownKey", Key.LeftCtrl,
                "Camera down key", "New Input System key that moves the editor camera straight down.");
            _fastMultiplier = _cfg.CreateEntry<float>("FastMultiplier", 4f,
                "Fast multiplier", "Speed multiplier while holding Shift.");
            _lookSens = _cfg.CreateEntry<float>("LookSensitivity", 2f,
                "Look sensitivity", "Mouse-look sensitivity (hold right mouse to look).");
            _invertY = _cfg.CreateEntry<bool>("InvertLookY", false,
                "Invert look Y", "Invert vertical mouse-look.");
            _targetDisplay = _cfg.CreateEntry<int>("TargetDisplay", 0,
                "Target display", "Display the editor camera renders to (0 = main desktop window). F3 cycles it live; 2 is the multi-display fallback.");
            _stopVRMirror = _cfg.CreateEntry<bool>("StopVRMirror", true,
                "Stop VR mirror", "Set XRSettings.showDeviceView=false while active so the desktop shows the editor instead of the HMD mirror (headset unaffected). F2 flips it live.");
            _panelUrl = _cfg.CreateEntry<string>("PanelUrl", "http://127.0.0.1:1766",
                "Panel URL", "Base URL of the web panel (local loopback, SSH tunnel, or your Tailscale Serve URL). The server stays the authority for every mutation.");
            _gizmoGrid = _cfg.CreateEntry<float>("GizmoGridSize", 0.25f,
                "Gizmo grid size", "Translate snap grid in meters while holding Ctrl (add Shift for x4).");
            _gizmoGhost = _cfg.CreateEntry<bool>("GizmoGhostPreview", false,
                "Gizmo ghost preview", "Preview gizmo drags as a moving ghost target instead of moving the object locally. Flip on if placed objects visibly fight the server mid-drag.");
            _outlineRadius = _cfg.CreateEntry<float>("OutlineRadius", 40f,
                "Outline radius", "Radius (m) around the editor camera for the 'outline all entities' toggle.");

            _api = new EditApi(_panelUrl.Value, LogEdit);
            _sel = new EditSelection(_api, LogEdit);
            _sel.ConfigureGizmo(_gizmoGrid.Value, _gizmoGhost.Value);
            _sel.OutlineRadius = Mathf.Clamp(_outlineRadius.Value, 5f, 150f);

            _speed = _moveSpeed.Value;
            MelonLogger.Msg("ATT Prefab Editor loaded. Default OFF - press " +
                _toggleKey.Value.ToString() + " in-game to toggle the flat freecam.");
        }

        // Route selection/transport status to the MelonLoader console (handy on the live test).
        private static void LogEdit(string s)
        {
            MelonLogger.Msg("[edit] " + s);
        }

        // ---- input helpers (new Input System; null-safe if a device is absent) ----

        private static bool KeyDown(Key k)
        {
            Keyboard kb = Keyboard.current;
            return kb != null && kb[k].wasPressedThisFrame;
        }

        private static bool KeyHeld(Key k)
        {
            Keyboard kb = Keyboard.current;
            return kb != null && k != Key.None && kb[k].isPressed;
        }

        public override void OnUpdate()
        {
            if (KeyDown(_toggleKey.Value))
            {
                if (_active) Disable();
                else Enable();
                return;
            }

            if (!_active) return;

            if (KeyDown(K_MIRROR))
            {
                _mirrorStopped = !_mirrorStopped;
                ApplyMirror();
                MelonLogger.Msg("Editor: VR mirror " + (_mirrorStopped ? "stopped" : "on"));
            }
            if (KeyDown(K_DISPLAY))
            {
                SetTargetDisplay(NextDisplay(_displayNow));
                MelonLogger.Msg("Editor: target display -> " + _displayNow);
            }

            Fly();

            if (_sel != null) _sel.Update(); // pick / drag / actions (server-authoritative)
        }

        // ---- toggle ----

        private void Enable()
        {
            EnsureCamera();
            if (_camGO == null) return; // creation failed; stay off

            SeedFromMainCamera();
            _speed = _moveSpeed.Value;

            _displayNow = _targetDisplay.Value;
            SetTargetDisplay(_displayNow);
            _camGO.SetActive(true);

            // capture the mirror state once, then apply our choice (headset stays intact)
            try { _prevShowDeviceView = XRSettings.showDeviceView; _capturedSDV = true; }
            catch (Exception) { _capturedSDV = false; }
            _mirrorStopped = _stopVRMirror.Value;
            ApplyMirror();

            // free the cursor for the desktop window
            try
            {
                _prevCursorLock = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _savedCursor = true;
            }
            catch (Exception) { _savedCursor = false; }

            // hand the live editor camera to the selection subsystem + probe the panel
            if (_sel != null) { _sel.Camera = _cam; _sel.Clear(); _sel.Ping(); }

            _active = true;
            MelonLogger.Msg("Editor ON  (display=" + _displayNow + ", mirror " +
                (_mirrorStopped ? "stopped" : "on") + ")");
        }

        private void Disable()
        {
            if (_camGO != null) _camGO.SetActive(false);

            if (_capturedSDV)
            {
                try { XRSettings.showDeviceView = _prevShowDeviceView; }
                catch (Exception) { }
                _capturedSDV = false;
            }
            if (_savedCursor)
            {
                try
                {
                    Cursor.lockState = _prevCursorLock;
                    Cursor.visible = _prevCursorVisible;
                }
                catch (Exception) { }
                _savedCursor = false;
            }

            if (_sel != null) _sel.Clear();

            _active = false;
            MelonLogger.Msg("Editor OFF");
        }

        // ---- render path ----

        private void ApplyMirror()
        {
            // showDeviceView==false hides the HMD mirror on the DESKTOP only; the headset
            // keeps rendering from its own XR pass, so VR is unaffected either way.
            try { XRSettings.showDeviceView = !_mirrorStopped; }
            catch (Exception e) { MelonLogger.Warning("showDeviceView failed: " + e.Message); }
        }

        private void SetTargetDisplay(int d)
        {
            _displayNow = d;
            if (_cam != null) _cam.targetDisplay = d;
            if (d > 0)
            {
                // extra displays must be Activated before they render (Unity multi-display)
                try
                {
                    if (Display.displays != null && d < Display.displays.Length &&
                        !Display.displays[d].active)
                        Display.displays[d].Activate();
                }
                catch (Exception e) { MelonLogger.Warning("Display.Activate(" + d + ") failed: " + e.Message); }
            }
        }

        private static int NextDisplay(int d)
        {
            if (d == 0) return 2;   // 0 (main) -> 2 (the MapBoardRenderer fallback)
            if (d == 2) return 1;   // -> 1
            return 0;               // -> back to main
        }

        private void EnsureCamera()
        {
            if (_camGO != null && _cam != null) { ApplyCameraSettings(); return; }

            _camGO = new GameObject("PrefabEditorModCam");
            UnityEngine.Object.DontDestroyOnLoad(_camGO);
            _cam = _camGO.AddComponent<Camera>();
            // the gizmo paints in this camera's OnPostRender (GL over the scene)
            GizmoRenderHook hook = _camGO.AddComponent<GizmoRenderHook>();
            hook.Owner = _sel;
            ApplyCameraSettings();
            _camGO.SetActive(false);
        }

        private void ApplyCameraSettings()
        {
            _cam.stereoTargetEye = StereoTargetEyeMask.None; // never touch the HMD
            _cam.targetDisplay = _displayNow;
            _cam.depth = 99f;                                // draw over the mirror camera
            _cam.clearFlags = CameraClearFlags.Skybox;
            _cam.cullingMask = ~0;
            _cam.nearClipPlane = 0.03f;
            _cam.farClipPlane = 3000f;
            _cam.fieldOfView = 70f;
        }

        // spawn the desktop camera where the player ACTUALLY is when they toggle, so it
        // doesn't jump to town/world-origin and make you fly back to yourself.
        private void SeedFromMainCamera()
        {
            Camera src = FindHeadsetCamera();       // the live HMD position - where we are
            if (src == null) src = Camera.main;     // fallbacks if no stereo cam is found
            if (src == null)
            {
                Camera[] all = Camera.allCameras;
                if (all != null && all.Length > 0) src = all[0];
            }

            if (src != null)
            {
                _camGO.transform.position = src.transform.position;
                Vector3 e = src.transform.rotation.eulerAngles;
                _pitch = NormalizePitch(e.x);
                _yaw = e.y;
            }
            _camGO.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // the headset renders in stereo; Camera.main isn't tagged to it in ATT (it was
        // seeding us at town center), so pick the live stereo camera explicitly.
        private Camera FindHeadsetCamera()
        {
            Camera[] all = Camera.allCameras; // enabled cameras only
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                Camera c = all[i];
                if (c == null || c == _cam) continue;
                if (c.stereoEnabled) return c;
            }
            return null;
        }

        private static float NormalizePitch(float x)
        {
            if (x > 180f) x -= 360f;
            if (x > 89f) x = 89f;
            if (x < -89f) x = -89f;
            return x;
        }

        // ---- per-frame freecam ----

        private void Fly()
        {
            if (_camGO == null || _cam == null) return;
            // WASD in the spawn search field is someone typing a prefab name
            if (_sel != null && _sel.WantsKeyboard) return;
            Transform t = _camGO.transform;
            float dt = Time.unscaledDeltaTime;
            Mouse mouse = Mouse.current;

            // wheel adjusts base speed (sign only - scroll magnitude varies by platform)
            if (mouse != null)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel > 0f) _speed = Mathf.Clamp(_speed * 1.1f, 0.5f, 400f);
                else if (wheel < 0f) _speed = Mathf.Clamp(_speed * 0.9f, 0.5f, 400f);
            }

            // mouse-look while holding right mouse button
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                float f = _lookSens.Value * LOOK_SCALE;
                _yaw += d.x * f;
                _pitch += (_invertY.Value ? d.y : -d.y) * f;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
                t.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            // movement uses fixed WASD plus the user-configurable vertical keys;
            // world-up keeps vertical flight predictable regardless of camera pitch.
            Vector3 move = Vector3.zero;
            if (KeyHeld(K_FWD)) move += t.forward;
            if (KeyHeld(K_BACK)) move -= t.forward;
            if (KeyHeld(K_RIGHT)) move += t.right;
            if (KeyHeld(K_LEFT)) move -= t.right;
            if (KeyHeld(_moveUpKey.Value)) move += Vector3.up;
            if (KeyHeld(_moveDownKey.Value)) move += Vector3.down;

            if (move.sqrMagnitude > 0f)
            {
                float spd = _speed;
                if (KeyHeld(Key.LeftShift) || KeyHeld(Key.RightShift))
                    spd *= _fastMultiplier.Value;
                t.position += move.normalized * spd * dt;
            }
        }

        // ---- desktop HUD ----

        public override void OnGUI()
        {
            if (!_active) return;

            Transform t = (_camGO != null) ? _camGO.transform : null;
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.08f, 0.12f, 0.14f, 0.94f);
            GUILayout.BeginArea(new Rect(12f, 12f, 360f, 116f), GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("ATT PREFAB EDITOR");
            GUILayout.FlexibleSpace();
            GUILayout.Label("ACTIVE  •  " + _toggleKey.Value.ToString() + " closes");
            GUILayout.EndHorizontal();
            if (t != null)
            {
                Vector3 p = t.position;
                GUILayout.Label("Camera  " + p.x.ToString("F1") + ", " + p.y.ToString("F1") + ", " + p.z.ToString("F1")
                    + "     Yaw " + _yaw.ToString("F0") + "  Pitch " + _pitch.ToString("F0"));
            }
            GUILayout.Label("Speed " + _speed.ToString("F1") + "  •  Shift x" + _fastMultiplier.Value.ToString("F0")
                + "  •  Display " + _displayNow + "  •  Mirror " + (_mirrorStopped ? "off" : "on"));
            GUILayout.Label("WASD move  •  " + _moveUpKey.Value + " / " + _moveDownKey.Value + " vertical  •  RMB look  •  wheel speed");
            GUILayout.Label("F2 mirror  •  F3 display  •  Click or box-select in the Prefab Editor panel");
            GUILayout.EndArea();
            GUI.backgroundColor = previousBackground;

            if (_sel != null) _sel.OnGUI(); // selection highlight + side panel
        }

        // safety: if the mod unloads or the app quits while active, put the game back
        public override void OnApplicationQuit()
        {
            if (_active) Disable();
        }

        public override void OnDeinitializeMelon()
        {
            if (_active) Disable();
        }
    }
}
