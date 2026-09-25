// Build-session 2: pick + inspect + move/rotate/ground/delete (CLIENT-EDITOR.md §5.2).
// Selection, side panel, and world actions for the ATT Prefab Editor client mod.
// CLIENT renders + previews; the SERVER stays authority - every mutation is a call to the
// edit module through EditApi (the panel /api/edit/*). The gizmo previews on the local
// transform only (rolled back on refusal); the networked state changes when the server
// processes the ONE move/rotate sent on release and syncs it back.
//
//   Left-click            part-picker raycast: all hits, triggers
//                         ignored, world layers + player + oversized colliders dropped,
//                         then the distinct PrefabRoots along the ancestor chain become
//                         a candidate list - nearest first, Tab / panel rows cycle it.
//                         A docked item resolves to ITSELF before the bench under it.
//   Drag the item         camera-plane move; drag an arrow for fine one-axis control;
//                         drag a ring to rotate. Ctrl snaps, Alt slows translation, Esc cancels.
//   Ground / Yaw buttons  edit ground / edit rotate
//   Delete                two-click, server-confirmed (mounted parts + designated boxes veto)
//   Copy string / Paste   blueprint string -> OS clipboard + paste buffer; Paste
//                         respawns it at the center-screen point (edit paste) and falls
//                         back to the OS clipboard, so a string copied from the web
//                         panel / workbench pastes straight into the game
//   Outline toggle        wire boxes around every entity root near the camera
//   Spawn...              load the server's live spawn list, search by name/hash,
//                         pick one, aim a
//                         cursor ghost, click = edit spawnat (Shift keeps placing);
//                         the new entity auto-selects once it streams in
//
// Entity id = NetworkEntity.Identifier (uint), the networked id the server keys on
// PrefabRoot is the thing move/rotate/delete act on.
//
// C# 5 / net35 - no interpolation, no ?., no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using WinForms = System.Windows.Forms;
using Alta.Networking;   // NetworkEntity (whole-Managed csc ref covers it)
using Alta.Networking.Internal; // EntityManager (outline-all iteration)
using UnityEngine;
using UnityEngine.InputSystem;

namespace PrefabEditorMod
{
    public class EditSelection
    {
        sealed class ImportedPrefab
        {
            public string Name;
            public string SaveString;
            public Vector3 Position;
            public Vector3 Rotation;
        }

        sealed class LayoutPrefab
        {
            public uint Id;
            public string Name;
            public string SaveString;
            public Vector3 Position;
            public Vector3 Rotation;
        }

        sealed class SpawnCatalogEntry
        {
            public string Name;
            public uint Hash;
        }

        sealed class WorkArea
        {
            public string Name;
            public Vector3 Position;
        }

        sealed class OnlinePlayer
        {
            public string Name;
            public Vector3 Position;
        }

        sealed class TravelState
        {
            public string Player;
            public Vector3 ReturnPosition;
            public Vector3 OriginalHome;
            public Vector3 WorkPosition;
            public bool HasWorkPosition;
            public bool Returned;
        }

        readonly EditApi _api;
        readonly Action<string> _log;
        readonly EditGizmo _gizmo;

        public Camera Camera;

        // selection
        bool _has;
        NetworkEntity _root;
        uint _id;
        EditEntity _info;          // last server report (populated by edit info / move / etc.)
        readonly List<NetworkEntity> _group = new List<NetworkEntity>();
        readonly List<Vector3> _groupPreviewStarts = new List<Vector3>();
        readonly List<Vector3> _groupRotationPositions = new List<Vector3>();
        readonly List<Quaternion> _groupRotationRotations = new List<Quaternion>();
        Vector3 _groupMoveDelta;
        // The lake tavern reference contains 246 separate items. Keep room to
        // select the whole blueprint; server group-move is still limited to 100.
        const int MaxGroupSelection = 300;
        const int MaxGroupMove = 100;
        // Imported blueprint files can be larger than an interactive selection.
        const int MaxImportPrefabs = 1000;

        // resize amount is relative to the prefab's current serialized scale.
        // The slider previews locally and commits one server replacement on release.
        const float MinResizePercent = -50f;
        const float MaxResizePercent = 100f;
        string _resizePercentText = "10";
        float _resizePercent = 10f;
        NetworkEntity _resizePreviewRoot;
        Vector3 _resizePreviewScale;
        bool _resizePreviewActive;
        bool _resizeSliderDragging;
        bool _surfaceSnap;
        const float SurfaceSnapDistance = 0.35f;

        // part-picker candidates: every distinct PrefabRoot under the last click,
        // nearest hit first. >1 entry = the disambiguation list (Tab / panel rows).
        // Measured root causes this design answers: triggers intercepted the ray, the
        // player's own colliders sat at d=0.00, and 47 m merged chunk meshes ate
        // the parent walk - so the old hits[0]-only pick "always grabbed the same
        // part" no matter what was aimed at.
        readonly List<NetworkEntity> _cands = new List<NetworkEntity>();
        int _candAt;
        const int PickMaxCandidates = 8;
        const float PickMaxColliderDim = 8f;   // chunk meshes are 47 m, wagons are not
        static int _pickMask;                  // lazy - built once from live layer names

        // one in-flight action at a time, so a held button / double-fire can't spam the server
        bool _busy;

        // gizmo preview mode: pref (flip in MelonPreferences.cfg if placed objects fight
        // the server stream mid-drag) - veto-flagged entities force it per drag regardless
        bool _ghostPref;

        // two-click delete arm window (matches the panel's 6 s)
        float _deleteArmUntil;
        const float DeleteArmSeconds = 6f;
        string _deleteIds = "";
        int _deleteConfirmCount;

        // editor panel workflow state
        int _panelTab, _lastPanelTab = -1;
        bool _showAllSelected;
        bool _moveSectionOpen = true, _rotateSectionOpen;
        bool _scaleSectionOpen, _groupSectionOpen = true;
        bool _connected;
        bool _consoleStatusKnown;
        bool _consoleReachable;
        bool _checkingConnection;
        string _connectionMessage = "Not checked";
        float _nextHealthAt;
        int _undoCount;
        int _redoCount;
        string _undoLabel = "";
        string _redoLabel = "";
        float _nextHistoryAt;
        bool _historyRequestPending;
        string _positionX = "0", _positionY = "0", _positionZ = "0";
        string _rotationX = "0", _rotationY = "0", _rotationZ = "0";
        string _groupRotationX = "0", _groupRotationY = "0", _groupRotationZ = "0";
        string _rotationStep = "15";
        bool _positionFocused, _rotationFocused, _stepFocused;
        bool _resetPositionArmed;
        float _resetPositionUntil;
        bool _hasTransformClipboard;
        Vector3 _transformClipboardPosition;
        Vector3 _transformClipboardEuler;
        string _arrangeSpacingText = "1";
        string _duplicateOffsetX = "1", _duplicateOffsetY = "0", _duplicateOffsetZ = "0";
        bool _arrangeSpacingFocused, _duplicateOffsetFocused;

        // copy/paste: the last exported save string (mirrored to the OS clipboard,
        // so the web panel / workbench can share it both ways)
        string _copied;
        string _copiedLabel;

        // outline-all toggle: wire boxes around every entity root near the camera,
        // refreshed on a slow tick (bounds drift <0.5 s is fine for an overview aid)
        public float OutlineRadius = 40f;
        bool _outlines;
        float _outlineRefreshAt;
        float _outlineScanAt;
        int _outlineCount;
        readonly List<Bounds> _outlineBoxes = new List<Bounds>();
        readonly List<NetworkEntity> _outlineRoots = new List<NetworkEntity>();
        const float OutlineRefreshSeconds = 0.5f;
        const float OutlineScanSeconds = 3f;
        const int OutlineMaxRoots = 250;
        static FieldInfo _entityManagerField;

        // spawn-with-preview: panel-catalog search + a cursor ghost, click to place
        bool _spawnOpen;
        string _spawnQuery = "";
        string _spawnSent;
        float _spawnSendAt;
        bool _spawnBusy;
        bool _spawnCatalogLoading;
        bool _spawnCatalogAttempted;
        bool _spawnCatalogLoaded;
        bool _spawnStaticFallback;
        readonly List<SpawnCatalogEntry> _spawnCatalog = new List<SpawnCatalogEntry>();
        readonly List<string> _hitNames = new List<string>();
        readonly List<uint> _hitHashes = new List<uint>();
        Rect _spawnRect;
        Rect _previewRect;
        Vector2 _spawnScroll;
        readonly Dictionary<uint, Texture2D> _prefabImages = new Dictionary<uint, Texture2D>();
        readonly HashSet<uint> _missingPrefabImages = new HashSet<uint>();
        bool _searchFocused;

        // Named camera work areas and a persisted player teleport/return transaction.
        readonly List<WorkArea> _workAreas = new List<WorkArea>();
        readonly List<OnlinePlayer> _onlinePlayers = new List<OnlinePlayer>();
        bool _playersLoading, _workBusy, _workAreaNameFocused;
        string _workAreaName = "Work area";
        string _selectedPlayer = "";
        string _workMessage = "";
        TravelState _travel;
        string _areasPath, _travelPath;
        int _selectedArea = -1;
        string _travelArmedPlayer = "";
        string _travelArmedArea = "";
        bool _keepAlive;
        bool _keepAlivePositive = true;
        float _keepAliveNextAt;
        const float KeepAliveInterval = 240f;
        const float KeepAliveNudge = 0.12f;

        // Import a blueprint JSON array (name/string/position/rotation records).
        string _importPath = "";
        bool _importPathFocused;
        List<ImportedPrefab> _importEntries = new List<ImportedPrefab>();
        Queue<ImportedPrefab> _importQueue = new Queue<ImportedPrefab>();
        bool _importing;
        int _importTotal, _importDone, _importFailed;
        readonly List<uint> _importSpawnedIds = new List<uint>();
        List<LayoutPrefab> _layoutToSave = new List<LayoutPrefab>();
        string _layoutSavePath;
        int _layoutSaveAt;
        bool _layoutSaving;
        bool _importAtCamera;
        Vector3 _importOffset;
        bool _marqueeTracking, _marqueeActive, _marqueeAdditive;
        Vector2 _marqueeStart, _marqueeCurrent;
        const float MarqueeThresholdPixels = 12f;

        // Body-grab waits for the server info response so mounted/docked objects
        // can use the same safe ghost-preview decision as gizmo moves.
        bool _grabPending;
        NetworkEntity _grabPendingRoot;
        Vector3 _grabPendingPoint;

        bool _placing;
        string _placeName = "";
        uint _placeHash;
        Vector3 _placePos;
        bool _placeValid;
        readonly List<Bounds> _ghostBox = new List<Bounds>(1);

        // adopt a just-spawned/pasted id as the selection once it streams in
        uint _pendingId;
        float _pendingUntil;
        float _pendingNextScan;
        readonly List<uint> _pendingIds = new List<uint>();
        readonly List<NetworkEntity> _pendingRoots = new List<NetworkEntity>();
        string _pendingManyLabel = "import";

        // the status line colors green/red on success/refusal
        const int ToneNeutral = 0;
        const int ToneOk = 1;
        const int ToneErr = 2;
        int _statusTone;

        // status line + panel geometry (for click-through guard)
        string _status = "";
        Rect _panelRect;
        Vector2 _panelScroll;
        bool _mouseOverPanel;
        bool _resizePercentFocused;

        static Texture2D _px;
        static GUISkin _sourceSkin;
        static GUISkin _editorSkin;
        static GUIStyle _titleStyle, _mutedStyle, _tabStyle, _selectedTabStyle, _dangerButtonStyle;
        static GUIStyle _toggleOffStyle, _toggleOnStyle, _toggleLabelStyle;
        static GUIStyle _connectedStyle, _panelOnlyStyle, _disconnectedStyle;
        static Texture2D _panelTexture, _buttonTexture, _buttonHoverTexture, _buttonActiveTexture;
        static Texture2D _fieldTexture, _selectedTexture, _dangerTexture, _dangerHoverTexture;
        static Texture2D _toggleOffTexture, _toggleOnTexture;

        public EditSelection(EditApi api, Action<string> log)
        {
            _api = api;
            _log = log;
            _gizmo = new EditGizmo(SetStatus, CommitGizmoMove, CommitGizmoRotate, PreviewGroupMove);
            _gizmo.SnapMove = SnapMoveToSurface;
            _gizmo.PreviewGroupRotation = PreviewGroupRotation;
            _gizmo.CommitGroupRotation = CommitGroupRotation;
            string mods = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _areasPath = Path.Combine(mods, "PrefabEditorWorkAreas.txt");
            _travelPath = Path.Combine(mods, "PrefabEditorTravel.txt");
            LoadWorkAreas();
            LoadTravel();
        }

        public bool HasSelection { get { return _has; } }

        // true while the spawn search field has focus - the freecam must not fly
        // off WASD keystrokes that are actually someone typing a prefab name
        public bool WantsKeyboard
        {
            get
            {
                return (_spawnOpen && _searchFocused) || (_panelTab == 3 && _workAreaNameFocused)
                    || _importPathFocused || _resizePercentFocused
                    || _positionFocused || _rotationFocused || _stepFocused
                    || _arrangeSpacingFocused || _duplicateOffsetFocused;
            }
        }

        public void ConfigureGizmo(float gridSize, bool ghostPreview)
        {
            _gizmo.GridSize = Mathf.Clamp(gridSize, 0.01f, 50f);
            _ghostPref = ghostPreview;
        }

        public void Clear()
        {
            RestoreResizePreview();
            RestoreGroupPreview();
            _gizmo.Cancel(false); // restores the transform if a drag is mid-flight
            _has = false;
            _root = null;
            _info = null;
            _group.Clear();
            _groupPreviewStarts.Clear();
            _deleteArmUntil = 0f;
            _deleteIds = "";
            _deleteConfirmCount = 0;
            _resetPositionArmed = false;
            _pendingId = 0;
            _pendingIds.Clear();
            _pendingRoots.Clear();
            _cands.Clear();
            _candAt = 0;
            _marqueeTracking = _marqueeActive = false;
            _resizePercentFocused = false;
            _positionFocused = _rotationFocused = _stepFocused = false;
            _arrangeSpacingFocused = _duplicateOffsetFocused = false;
        }

        bool TryResizePercent(out float percent)
        {
            percent = 0f;
            if (!float.TryParse(_resizePercentText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out percent)) return false;
            return !float.IsNaN(percent) && !float.IsInfinity(percent)
                && percent >= MinResizePercent && percent <= MaxResizePercent;
        }

        void PreviewResize(float percent)
        {
            if (_busy || !_has || _group.Count != 1 || _root == null) return;
            if (_resizePreviewActive && _resizePreviewRoot != _root) RestoreResizePreview();
            if (!_resizePreviewActive)
            {
                _resizePreviewRoot = _root;
                try { _resizePreviewScale = _root.transform.localScale; }
                catch { _resizePreviewRoot = null; return; }
                _resizePreviewActive = true;
            }
            try
            {
                _resizePreviewRoot.transform.localScale = _resizePreviewScale * (1f + percent / 100f);
                SetStatus("resize preview " + percent.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture)
                    + "% - release slider to apply");
            }
            catch { RestoreResizePreview(); }
        }

        void RestoreResizePreview()
        {
            if (_resizePreviewActive)
            {
                try
                {
                    if (_resizePreviewRoot != null)
                        _resizePreviewRoot.transform.localScale = _resizePreviewScale;
                }
                catch { }
            }
            _resizePreviewRoot = null;
            _resizePreviewActive = false;
            _resizeSliderDragging = false;
        }

        void FinishResizeSlider()
        {
            if (!_resizeSliderDragging) return;
            _resizeSliderDragging = false;
            float percent;
            if (!TryResizePercent(out percent))
            {
                RestoreResizePreview();
                SetStatus("resize amount must be between -50% and +100%", ToneErr);
                return;
            }
            if (Mathf.Abs(percent) < 0.01f)
            {
                RestoreResizePreview();
                SetStatus("size unchanged");
                return;
            }
            ResizeSelected(1f + percent / 100f);
        }

        void ApplyResizePercent()
        {
            float percent;
            if (!TryResizePercent(out percent))
            {
                SetStatus("resize amount must be between -50% and +100%", ToneErr);
                return;
            }
            if (Mathf.Abs(percent) < 0.01f)
            {
                RestoreResizePreview();
                SetStatus("size unchanged");
                return;
            }
            ResizeSelected(1f + percent / 100f);
        }

        // Optional reachability line on enable, so "is it wired?" is answerable in-game.
        public void Ping()
        {
            ProbeConnection(true);
            RefreshHistory(false);
        }

        void ProbeConnection(bool showStatus)
        {
            if (_checkingConnection) return;
            _checkingConnection = true;
            if (showStatus) SetStatus("checking panel " + _api.BaseUrl + " ...");
            _api.Health(delegate(EditResult r)
            {
                _checkingConnection = false;
                _connected = r.Ok;
                _consoleStatusKnown = r.HasConsoleStatus;
                _consoleReachable = !r.HasConsoleStatus || r.ConsoleReachable;
                _connectionMessage = !r.Ok ? Reason(r)
                    : (r.HasConsoleStatus && !r.ConsoleReachable
                        ? "panel reachable, but the game server console is offline"
                        : "panel and game server connected");
                _nextHealthAt = Time.unscaledTime + 15f;
                if (showStatus)
                    SetStatus(!r.Ok ? "panel not reachable (" + Reason(r) + ")"
                        : (r.HasConsoleStatus && !r.ConsoleReachable
                            ? "panel is reachable, but its game server console is offline"
                            : "panel reachable - left-click an entity to select"), r.Ok ? ToneOk : ToneErr);
            });
        }

        void RefreshHistory(bool showStatus)
        {
            if (_historyRequestPending) return;
            _historyRequestPending = true;
            if (showStatus) SetStatus("reading undo history...");
            _api.History(delegate(EditResult r)
            {
                _historyRequestPending = false;
                _nextHistoryAt = Time.unscaledTime + 8f;
                if (r.Ok)
                {
                    _undoCount = r.UndoCount;
                    _redoCount = r.RedoCount;
                    _undoLabel = r.UndoLabel;
                    _redoLabel = r.RedoLabel;
                    if (showStatus) SetStatus("history updated", ToneOk);
                }
            });
        }

        void RunHistory(bool undo)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(undo ? "undoing last server edit..." : "redoing last server edit...");
            Action<EditResult> done = delegate(EditResult r)
            {
                _busy = false;
                if (!r.Ok)
                {
                    SetStatus((undo ? "undo: " : "redo: ") + Reason(r), ToneErr);
                    RefreshHistory(false);
                    return;
                }
                List<uint> ids = new List<uint>();
                for (int i = 0; i < r.Entities.Length; i++)
                    if (r.Entities[i] != null && r.Entities[i].Id != 0) ids.Add(r.Entities[i].Id);
                Clear();
                if (ids.Count > 0) AdoptManySoon(ids, "restored");
                RefreshHistory(false);
                SetStatus(string.IsNullOrEmpty(r.Note) ? (undo ? "undo complete" : "redo complete") : r.Note,
                    ToneOk);
            };
            if (undo) _api.Undo(done); else _api.Redo(done);
        }

        // ---- per-frame input (called from OnUpdate while the editor is active) ----

        public void Update()
        {
            if (_resizeSliderDragging)
            {
                Mouse resizeMouse = Mouse.current;
                if (resizeMouse == null || !resizeMouse.leftButton.isPressed)
                    FinishResizeSlider();
            }
            if (Camera == null) return;
            TickKeepAlive();
            Keyboard kb = Keyboard.current;
            if (!WantsKeyboard && !_gizmo.Dragging && !_marqueeActive && !_placing && kb != null)
            {
                bool control = kb[Key.LeftCtrl].isPressed || kb[Key.RightCtrl].isPressed;
                if (control && kb[Key.Z].wasPressedThisFrame) { RunHistory(true); return; }
                if (control && kb[Key.Y].wasPressedThisFrame) { RunHistory(false); return; }
                if (control && kb[Key.D].wasPressedThisFrame) { DuplicateSelection(); return; }
                if (_has && kb[Key.Delete].wasPressedThisFrame) { DeleteClick(); return; }
            }
            if (Time.unscaledTime >= _nextHealthAt && !_checkingConnection) ProbeConnection(false);
            if (Time.unscaledTime >= _nextHistoryAt) RefreshHistory(false);
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 mp = mouse.position.ReadValue();

            // click-through guard covers the side panel AND the spawn window
            Vector2 gui = new Vector2(mp.x, Screen.height - mp.y);
            _mouseOverPanel = (_panelRect.width > 0f && _panelRect.Contains(gui))
                || (_spawnOpen && _spawnRect.width > 0f && _spawnRect.Contains(gui))
                || (_previewRect.width > 0f && _previewRect.Contains(gui));

            if (_outlines && Time.unscaledTime >= _outlineRefreshAt)
            {
                _outlineRefreshAt = Time.unscaledTime + OutlineRefreshSeconds;
                RefreshOutlines();
            }

            if (_pendingId != 0 || _pendingIds.Count > 0) TryAdoptPending();
            if (_spawnOpen) TickSearch();

            if (_grabPending && !mouse.leftButton.isPressed)
            {
                _grabPending = false;
                _grabPendingRoot = null;
            }
            if (_marqueeTracking)
            {
                _marqueeCurrent = mp;
                if (mouse.leftButton.isPressed)
                {
                    if (!_marqueeActive && Vector2.Distance(_marqueeStart, mp) >= MarqueeThresholdPixels)
                    {
                        _marqueeActive = true;
                        _grabPending = false;
                        _grabPendingRoot = null;
                        if (_gizmo.Dragging) _gizmo.Cancel(false);
                        RestoreGroupPreview();
                        SetStatus(_marqueeAdditive
                            ? "drag to toggle items in the selection box"
                            : "drag to select items in a box");
                    }
                }
                else
                {
                    if (_marqueeActive) SelectRectangle(_marqueeStart, mp, _marqueeAdditive);
                    _marqueeTracking = false;
                    _marqueeActive = false;
                }
            }
            if (_importing) return;

            // placement owns the mouse: the ghost follows the cursor, left click
            // spawns (Shift = keep placing), Esc / right click cancels
            if (_placing)
            {
                TickPlacement(mp, mouse);
                return;
            }

            if (_spawnOpen && kb != null && kb[Key.Escape].wasPressedThisFrame && !_gizmo.Dragging)
            {
                CloseSpawn();
                return;
            }
            if (_marqueeActive && kb != null && kb[Key.Escape].wasPressedThisFrame)
            {
                _marqueeTracking = _marqueeActive = false;
                SetStatus("box selection canceled");
                return;
            }

            // the gizmo owns the mouse while a handle is hovered-and-clicked or dragged;
            // it previews locally and commits ONE move/rotate through us on release
            bool gizmoHasMouse = false;
            if (_grabPending) return;
            if (_has)
            {
                Transform t = null;
                try { if (_root != null) t = _root.transform; } catch { t = null; }
                bool wasDragging = _gizmo.Dragging;
                gizmoHasMouse = _gizmo.Update(Camera, mp, t, _mouseOverPanel, !_busy, GhostWanted());
                if (wasDragging && !_gizmo.Dragging && !_busy) RestoreGroupPreview();
            }

            // Tab cycles the last click's candidate list (never while typing a
            // prefab name or mid-drag)
            if (_cands.Count > 1 && kb != null && kb[Key.Tab].wasPressedThisFrame
                && !WantsKeyboard && !_gizmo.Dragging)
                SelectCandidate((_candAt + 1) % _cands.Count);

            if (!gizmoHasMouse && !_mouseOverPanel &&
                mouse.leftButton.wasPressedThisFrame && !mouse.rightButton.isPressed)
            {
                bool picked = Pick(mp);
                if (!picked)
                {
                    _marqueeTracking = true;
                    _marqueeActive = false;
                    _marqueeStart = _marqueeCurrent = mp;
                    _marqueeAdditive = ShiftHeld();
                }
            }
        }

        // Mounted/docked/held can't move (the server vetoes them) and their transforms
        // belong to a parent structure - preview those as ghost so we never yank the
        // real object around locally just to have it bounce back.
        bool GhostWanted()
        {
            if (_ghostPref) return true;
            EditEntity e = _info;
            return e != null && (e.HasFlag("mounted") || e.HasFlag("docked") || e.HasFlag("held"));
        }

        // The pick ray comes from the editor's own detached camera - never Camera.main,
        // which is permanently NULL in ATT (nothing is tagged MainCamera; 24 cameras exist).
        bool Pick(Vector2 mp)
        {
            Ray ray = Camera.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, 500f, PickMask(), QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) { SetStatus("drag on empty space to box-select items"); return false; }
            Array.Sort(hits, CompareHitDistance);

            _cands.Clear();
            _candAt = 0;
            Vector3 grabPoint = Vector3.zero;
            bool hasGrabPoint = false;
            string blockedBy = null;   // best "why not" name for the status line
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null) continue;

                // the player's own body is hits[0] at d=0.00 whenever the freecam
                // sits at the head (it seeds there); other players are unpickable too
                try { if (col.GetComponentInParent<PlayerController>() != null) continue; }
                catch { }

                // merged world meshes that slip through on unmasked layers - anything
                // this big is scenery, and its parent walk lands on a whole chunk
                try
                {
                    Vector3 bs = col.bounds.size;
                    float dim = Mathf.Max(bs.x, Mathf.Max(bs.y, bs.z));
                    if (dim > PickMaxColliderDim)
                    {
                        if (blockedBy == null) blockedBy = col.name;
                        continue;
                    }
                }
                catch { }

                // the ancestor chain, resolved to DISTINCT PrefabRoots: a docked
                // hammer lists [hammer, bench] (nearest first, hammer default); a
                // cart wheel lists just [cart] - same-root parts are not separately
                // addressable server-side, so they are not offered as fake choices
                NetworkEntity[] chain = null;
                try { chain = col.GetComponentsInParent<NetworkEntity>(); }
                catch { }
                if (chain == null || chain.Length == 0)
                {
                    if (blockedBy == null) blockedBy = col.name;
                    continue;
                }
                for (int c = 0; c < chain.Length && _cands.Count < PickMaxCandidates; c++)
                {
                    if (chain[c] == null) continue;
                    NetworkEntity root;
                    try { root = (chain[c].PrefabRoot != null) ? chain[c].PrefabRoot : chain[c]; }
                    catch { root = chain[c]; }
                    if (root == null || _cands.Contains(root)) continue;
                    _cands.Add(root);
                    if (!hasGrabPoint) { grabPoint = hits[i].point; hasGrabPoint = true; }
                }
                if (_cands.Count >= PickMaxCandidates) break;
            }

            if (_cands.Count == 0)
            {
                SetStatus(blockedBy == null
                    ? "no entity under the cursor"
                    : "no editable entity under the cursor (" + blockedBy + ")");
                return false;
            }
            bool addToGroup = ShiftHeld();
            Select(_cands[0], addToGroup);
            if (!addToGroup && hasGrabPoint && Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                _grabPending = true;
                _grabPendingRoot = _root;
                _grabPendingPoint = grabPoint;
            }
            return true;
        }

        void SelectRectangle(Vector2 a, Vector2 b, bool additive)
        {
            RestoreResizePreview();
            RestoreGroupPreview();
            Rect box = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            List<NetworkEntity> found = new List<NetworkEntity>();
            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            try
            {
                foreach (NetworkEntity e in AllEntities())
                {
                    if (e == null) continue;
                    NetworkEntity root;
                    uint id;
                    try
                    {
                        if (e.IsBeingDestroyed) continue;
                        root = e.PrefabRoot != null ? e.PrefabRoot : e;
                        id = root.Identifier;
                        if (id == 0 || seen.ContainsKey(id)) continue;
                        if (root.GetComponent<PlayerController>() != null) continue;
                    }
                    catch { continue; }
                    seen[id] = true;
                    Bounds bounds;
                    if (!TryBounds(root, out bounds) || !BoundsIntersectsScreenRect(bounds, box)) continue;
                    if (!found.Contains(root)) found.Add(root);
                    if (found.Count >= MaxGroupSelection) break;
                }
            }
            catch { }

            // Some ATT prefabs have renderer bounds that do not match their live
            // interaction colliders. If the projected-bounds pass found nothing,
            // inspect the actual scene colliders before resorting to screen samples.
            // This also works if the client NetworkScene entity-manager reflection
            // changes and AllEntities() returns no streamed roots.
            if (found.Count == 0) AddColliderRectangleHits(box, found);
            // Very small objects may have colliders narrower than the scan pitch.
            // Sample with the same physics ray used by ordinary picking as a final
            // fallback for those cases.
            if (found.Count == 0) SampleRectangleHits(box, found);

            if (found.Count == 0)
            {
                if (!additive) Clear();
                SetStatus("box did not contain any selectable items");
                return;
            }

            if (!additive)
            {
                _group.Clear();
                for (int i = 0; i < found.Count && _group.Count < MaxGroupSelection; i++)
                    if (!_group.Contains(found[i])) _group.Add(found[i]);
            }
            else
            {
                // Shift-box toggles membership: remove already-selected roots,
                // then add the rest so a full group can still trade members.
                List<NetworkEntity> removed = new List<NetworkEntity>();
                for (int i = 0; i < found.Count; i++)
                {
                    int existing = _group.IndexOf(found[i]);
                    if (existing < 0) continue;
                    removed.Add(found[i]);
                    _group.RemoveAt(existing);
                }
                for (int i = 0; i < found.Count && _group.Count < MaxGroupSelection; i++)
                    if (!removed.Contains(found[i]) && !_group.Contains(found[i])) _group.Add(found[i]);
            }
            if (_group.Count == 0)
            {
                Clear();
                SetStatus("box selection cleared");
                return;
            }
            _gizmo.Cancel(false);
            _root = _group[0];
            _has = true;
            _info = null;
            _deleteArmUntil = 0f;
            _groupPreviewStarts.Clear();
            _gizmo.RotationEnabled = _group.Count <= MaxGroupMove;
            _gizmo.GroupRotationEnabled = _group.Count > 1 && _group.Count <= MaxGroupMove;
            _cands.Clear();
            _candAt = 0;
            try { _id = _root.Identifier; } catch { _id = 0; }
            SetStatus("box-selected " + _group.Count + " item" + (_group.Count == 1 ? "" : "s") +
                (additive ? " (Shift toggled items in the box)" : ""), ToneOk);
            _api.Info(_id, OnInfo);
        }

        void AddColliderRectangleHits(Rect box, List<NetworkEntity> found)
        {
            Collider[] colliders = null;
            try { colliders = UnityEngine.Object.FindObjectsOfType<Collider>(); }
            catch { }
            if (colliders == null) return;

            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            for (int i = 0; i < colliders.Length && found.Count < MaxGroupSelection; i++)
            {
                Collider col = colliders[i];
                if (col == null || !col.enabled) continue;
                try
                {
                    if (!col.gameObject.activeInHierarchy) continue;
                    Vector3 size = col.bounds.size;
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim) continue;
                    if (!BoundsIntersectsScreenRect(col.bounds, box)) continue;
                    if (col.GetComponentInParent<PlayerController>() != null) continue;
                }
                catch { continue; }

                NetworkEntity[] chain = null;
                try { chain = col.GetComponentsInParent<NetworkEntity>(); }
                catch { }
                if (chain == null) continue;
                for (int c = 0; c < chain.Length && found.Count < MaxGroupSelection; c++)
                {
                    NetworkEntity entity = chain[c];
                    if (entity == null) continue;
                    NetworkEntity root;
                    uint id;
                    try
                    {
                        if (entity.IsBeingDestroyed) continue;
                        root = entity.PrefabRoot != null ? entity.PrefabRoot : entity;
                        id = root.Identifier;
                        if (id == 0 || seen.ContainsKey(id)) continue;
                        if (root.GetComponentInParent<PlayerController>() != null) continue;
                    }
                    catch { continue; }
                    seen[id] = true;
                    found.Add(root);
                }
            }
        }

        bool BoundsIntersectsScreenRect(Bounds bounds, Rect box)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            int visible = 0;
            Vector3 c = bounds.center, e = bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = Camera.WorldToScreenPoint(corner);
                if (p.z <= 0f) continue;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                visible++;
            }
            if (visible == 0) return false;
            return maxX >= box.xMin && minX <= box.xMax && maxY >= box.yMin && minY <= box.yMax;
        }

        void SampleRectangleHits(Rect box, List<NetworkEntity> found)
        {
            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            int columns = Mathf.Clamp(Mathf.CeilToInt(box.width / 32f), 2, 32);
            int rows = Mathf.Clamp(Mathf.CeilToInt(box.height / 32f), 2, 32);
            for (int y = 0; y <= rows && found.Count < MaxGroupSelection; y++)
                for (int x = 0; x <= columns && found.Count < MaxGroupSelection; x++)
                {
                    Vector2 point = new Vector2(box.xMin + box.width * x / columns,
                        box.yMin + box.height * y / rows);
                    Ray ray = Camera.ScreenPointToRay(new Vector3(point.x, point.y, 0f));
                    RaycastHit[] hits = Physics.RaycastAll(ray, 500f, PickMask(), QueryTriggerInteraction.Ignore);
                    if (hits == null || hits.Length == 0) continue;
                    Array.Sort(hits, CompareHitDistance);
                    for (int i = 0; i < hits.Length && found.Count < MaxGroupSelection; i++)
                    {
                        Collider col = hits[i].collider;
                        if (col == null) continue;
                        try { if (col.GetComponentInParent<PlayerController>() != null) continue; } catch { }
                        try
                        {
                            Vector3 size = col.bounds.size;
                            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim) continue;
                        }
                        catch { }
                        NetworkEntity[] chain = null;
                        try { chain = col.GetComponentsInParent<NetworkEntity>(); } catch { }
                        if (chain == null) continue;
                        for (int c = 0; c < chain.Length && found.Count < MaxGroupSelection; c++)
                        {
                            if (chain[c] == null) continue;
                            NetworkEntity root;
                            try { root = chain[c].PrefabRoot != null ? chain[c].PrefabRoot : chain[c]; }
                            catch { root = chain[c]; }
                            if (root == null) continue;
                            uint id;
                            try { id = root.Identifier; } catch { continue; }
                            if (id == 0 || seen.ContainsKey(id)) continue;
                            seen[id] = true;
                            found.Add(root);
                        }
                    }
                }
        }

        // everything except the world layers the measured raycast stacks showed eating
        // every pick: Terrain / Wall chunk meshes, the player's own particle-only
        // colliders, ambience trigger volumes on LocalPlayerIdentifier, players, UI
        static int PickMask()
        {
            if (_pickMask != 0) return _pickMask;
            string[] worldLayers = new string[] {
                "Terrain", "Wall", "ParticleOnlyCollisions", "LocalPlayerIdentifier", "Player", "UI" };
            int excluded = 0;
            for (int i = 0; i < worldLayers.Length; i++)
            {
                int l = LayerMask.NameToLayer(worldLayers[i]);
                if (l >= 0) excluded |= 1 << l;   // a renamed layer just isn't excluded;
            }                                     // the size gate still catches its meshes
            _pickMask = Physics.DefaultRaycastLayers & ~excluded;
            return _pickMask;
        }

        static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        void SelectCandidate(int i)
        {
            if (i < 0 || i >= _cands.Count) return;
            _candAt = i;
            NetworkEntity e = _cands[i];
            if (e == null) { SetStatus("that one is gone (streamed out?)"); return; }
            Select(e, false);
        }

        // "(2/3 - Tab cycles)" when the last click had rivals; "" otherwise
        string CandNote()
        {
            if (_cands.Count <= 1) return "";
            return "  (" + (_candAt + 1) + "/" + _cands.Count + " - Tab cycles)";
        }

        static string NiceName(NetworkEntity e)
        {
            try
            {
                string n = e.name;
                int k = n.IndexOf("(Clone)", StringComparison.Ordinal);
                if (k > 0) n = n.Substring(0, k).Trim();
                return n;
            }
            catch { return "?"; }
        }

        // Shared by the click pick and the post-spawn/post-paste adoption.
        bool ShiftHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && (kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed);
        }

        string SelectionIds()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < _group.Count; i++)
            {
                try
                {
                    uint id = _group[i] != null ? _group[i].Identifier : 0;
                    if (id != 0 && !ids.Contains(id.ToString(CultureInfo.InvariantCulture)))
                        ids.Add(id.ToString(CultureInfo.InvariantCulture));
                }
                catch { }
            }
            return string.Join(",", ids.ToArray());
        }

        string PrefabName(NetworkEntity root)
        {
            if (root == null) return "";
            try
            {
                string name = root.Prefab != null ? root.Prefab.name : null;
                return !string.IsNullOrEmpty(name) ? name : root.OriginalName;
            }
            catch { return NiceName(root); }
        }

        static string TypeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim();
            int separator = name.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0)
            {
                bool numericPrefix = true;
                for (int i = 0; i < separator; i++)
                    if (!char.IsDigit(name[i])) { numericPrefix = false; break; }
                if (numericPrefix) name = name.Substring(separator + 3).Trim();
            }
            if (name.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - "(Clone)".Length).Trim();
            return name;
        }

        void AddNearbyRoot(NetworkEntity entity, string typeName, bool invert, Vector3 eye, float radius2,
            HashSet<uint> seen, HashSet<uint> selected, List<NetworkEntity> result, ref int matchingCount)
        {
            try
            {
                if (entity == null || entity.IsBeingDestroyed) return;
                NetworkEntity root = entity.PrefabRoot != null ? entity.PrefabRoot : entity;
                uint id = root.Identifier;
                if (id == 0 || !seen.Add(id)) return;
                if (Vector3.SqrMagnitude(root.transform.position - eye) > radius2) return;
                if (root.GetComponentInParent<PlayerController>() != null) return;
                if (!string.IsNullOrEmpty(typeName) &&
                    !string.Equals(TypeName(PrefabName(root)), typeName, StringComparison.OrdinalIgnoreCase)) return;
                matchingCount++;
                if (!invert || !selected.Contains(id))
                    if (result.Count < MaxGroupSelection) result.Add(root);
            }
            catch { }
        }

        List<NetworkEntity> NearbyRoots(string typeName, bool invert, out int matchingCount, bool logScan = true)
        {
            List<NetworkEntity> result = new List<NetworkEntity>();
            matchingCount = 0;
            if (Camera == null) return result;
            Vector3 eye = Camera.transform.position;
            float radius2 = OutlineRadius * OutlineRadius;
            HashSet<uint> seen = new HashSet<uint>();
            HashSet<uint> selected = new HashSet<uint>();
            for (int i = 0; i < _group.Count; i++)
                try { if (_group[i] != null) selected.Add(_group[i].Identifier); } catch { }
            try
            {
                foreach (NetworkEntity e in AllEntities())
                {
                    AddNearbyRoot(e, typeName, invert, eye, radius2, seen, selected, result, ref matchingCount);
                    if (result.Count >= MaxGroupSelection) break;
                }
            }
            catch { }
            int sceneMatches = matchingCount;

            // The private scene walk can be empty on the client. Box selection already
            // finds streamed prefabs through their colliders, so include that path here.
            Collider[] colliders = null;
            if (result.Count < MaxGroupSelection)
                try { colliders = UnityEngine.Object.FindObjectsOfType<Collider>(); }
                catch (Exception ex) { if (_log != null) _log("selection collider scan failed: " + ex.Message); }
            if (colliders != null)
                for (int i = 0; i < colliders.Length && result.Count < MaxGroupSelection; i++)
                {
                    Collider col = colliders[i];
                    if (col == null || !col.enabled) continue;
                    try
                    {
                        if (!col.gameObject.activeInHierarchy) continue;
                        Vector3 size = col.bounds.size;
                        if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim) continue;
                        if (col.GetComponentInParent<PlayerController>() != null) continue;
                    }
                    catch { continue; }
                    NetworkEntity[] chain = null;
                    try { chain = col.GetComponentsInParent<NetworkEntity>(); } catch { }
                    if (chain == null) continue;
                    for (int c = 0; c < chain.Length && result.Count < MaxGroupSelection; c++)
                        AddNearbyRoot(chain[c], typeName, invert, eye, radius2, seen, selected, result, ref matchingCount);
                }
            if (logScan && _log != null) _log("selection scan " + (string.IsNullOrEmpty(typeName) ? "all" : typeName)
                + ": scene=" + sceneMatches + ", colliders=" + (matchingCount - sceneMatches)
                + ", selected=" + result.Count);
            return result;
        }

        void ReplaceSelection(List<NetworkEntity> roots, string message)
        {
            RestoreResizePreview();
            RestoreGroupPreview();
            _gizmo.Cancel(false);
            _group.Clear();
            if (roots != null)
                for (int i = 0; i < roots.Count && _group.Count < MaxGroupSelection; i++)
                    if (roots[i] != null && !_group.Contains(roots[i])) _group.Add(roots[i]);
            _deleteArmUntil = 0f;
            _deleteIds = "";
            _deleteConfirmCount = 0;
            if (_group.Count == 0) { Clear(); SetStatus("selection is empty", ToneOk); return; }
            _root = _group[0];
            _has = true;
            _info = null;
            _groupPreviewStarts.Clear();
            _gizmo.RotationEnabled = _group.Count <= MaxGroupMove;
            _gizmo.GroupRotationEnabled = _group.Count > 1 && _group.Count <= MaxGroupMove;
            try { _id = _root.Identifier; } catch { _id = 0; }
            if (_id != 0) _api.Info(_id, OnInfo);
            SetStatus(message + " (" + _group.Count + ")", ToneOk);
        }

        void MakePrimarySelection(NetworkEntity item)
        {
            if (_busy || item == null || _group.IndexOf(item) <= 0) return;
            List<NetworkEntity> ordered = new List<NetworkEntity>();
            ordered.Add(item);
            for (int i = 0; i < _group.Count; i++)
                if (_group[i] != item) ordered.Add(_group[i]);
            ReplaceSelection(ordered, "primary item changed");
        }

        void RemoveSelectedItem(NetworkEntity item)
        {
            if (_busy) return;
            List<NetworkEntity> remaining = new List<NetworkEntity>();
            for (int i = 0; i < _group.Count; i++)
                if (!System.Object.ReferenceEquals(_group[i], item)) remaining.Add(_group[i]);
            ReplaceSelection(remaining, "removed item from selection");
        }

        void SelectSameType()
        {
            if (!_has || _root == null) return;
            string name = TypeName(PrefabName(_root));
            if (name.Length == 0) { SetStatus("this item has no readable prefab type", ToneErr); return; }
            int matching;
            List<NetworkEntity> roots = NearbyRoots(name, false, out matching);
            if (roots.Count == 0)
            { SetStatus("no loaded " + name + " items within " + OutlineRadius.ToString("0") + " m; selection kept"); return; }
            ReplaceSelection(roots, "selected nearby " + name);
        }

        void SelectNearby()
        {
            int matching;
            List<NetworkEntity> roots = NearbyRoots(null, false, out matching);
            if (roots.Count == 0)
            { SetStatus("no loaded items within " + OutlineRadius.ToString("0") + " m; selection kept"); return; }
            ReplaceSelection(roots, "selected nearby items");
        }

        void InvertSelection()
        {
            if (!_has || Camera == null) return;
            int matching;
            List<NetworkEntity> roots = NearbyRoots(null, true, out matching);
            if (matching == 0)
            { SetStatus("no loaded items within " + OutlineRadius.ToString("0") + " m; selection kept"); return; }
            if (roots.Count == 0)
            { Clear(); SetStatus("inverted nearby selection: all nearby items deselected", ToneOk); return; }
            ReplaceSelection(roots, "inverted nearby selection");
        }

        void DuplicateSelection()
        {
            DuplicateSelectionByOffset(Vector3.zero);
        }

        bool TryDuplicateOffset(out Vector3 offset)
        {
            offset = Vector3.zero;
            float x = 0f, y = 0f, z = 0f;
            bool valid = float.TryParse(_duplicateOffsetX, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && float.TryParse(_duplicateOffsetY, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && float.TryParse(_duplicateOffsetZ, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            if (!valid || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)
                || Mathf.Abs(x) > 100000f || Mathf.Abs(y) > 100000f || Mathf.Abs(z) > 100000f) return false;
            offset = new Vector3(x, y, z);
            return true;
        }

        void DuplicateSelectionByOffset(Vector3 offset)
        {
            if (_busy || !_has || _group.Count == 0) return;
            string ids = SelectionIds();
            if (string.IsNullOrEmpty(ids)) { SetStatus("selection has no live entities", ToneErr); return; }
            _busy = true;
            _deleteArmUntil = 0f;
            bool inPlace = offset.sqrMagnitude < 0.000001f;
            SetStatus("duplicating " + _group.Count + " item(s)" + (inPlace ? " in place..." : " with offset " + VecText(offset) + " ..."));
            _api.DuplicateMany(ids, offset.x, offset.y, offset.z, delegate(EditResult r)
            {
                _busy = false;
                if (!r.Ok) { SetStatus("duplicate: " + Reason(r), ToneErr); return; }
                List<uint> copied = new List<uint>();
                for (int i = 0; i < r.Entities.Length; i++)
                    if (r.Entities[i] != null && r.Entities[i].Id != 0) copied.Add(r.Entities[i].Id);
                if (copied.Count > 0) AdoptManySoon(copied, "duplicate");
                RefreshHistory(false);
                SetStatus("duplicated " + copied.Count + " item(s)" + (inPlace ? " in place" : " with offset " + VecText(offset)) + "; selecting copies to move...", ToneOk);
            });
        }

        bool TryArrangeSpacing(out float spacing)
        {
            spacing = 0f;
            return float.TryParse(_arrangeSpacingText, NumberStyles.Float, CultureInfo.InvariantCulture, out spacing)
                && !float.IsNaN(spacing) && !float.IsInfinity(spacing) && spacing >= 0.001f && spacing <= 100f;
        }

        void ArrangeSelection(string axis, string mode)
        {
            if (_busy || !_has || _group.Count < 2) return;
            float spacing = 1f;
            if (mode == "space" && !TryArrangeSpacing(out spacing))
            {
                SetStatus("spacing must be 0.001 to 100 m", ToneErr);
                return;
            }
            string ids = SelectionIds();
            if (string.IsNullOrEmpty(ids)) { SetStatus("selection has no live entities", ToneErr); return; }
            _busy = true;
            _deleteArmUntil = 0f;
            SetStatus(mode == "align" ? "aligning selection on " + axis.ToUpperInvariant() + "..."
                : "spacing selection on " + axis.ToUpperInvariant() + "...", ToneOk);
            _api.ArrangeMany(ids, axis, mode, spacing, delegate(EditResult r)
            {
                _busy = false;
                if (!r.Ok) { SetStatus("arrange: " + Reason(r), ToneErr); return; }
                if (!string.IsNullOrEmpty(r.Note)) SetStatus(r.Note, ToneOk);
                else SetStatus(mode == "align" ? "aligned " + _group.Count + " items on " + axis.ToUpperInvariant()
                    : "spaced " + _group.Count + " items on " + axis.ToUpperInvariant(), ToneOk);
                if (_id != 0) _api.Info(_id, OnInfo);
                RefreshHistory(false);
            });
        }

        void Select(NetworkEntity root) { Select(root, false); }

        void Select(NetworkEntity root, bool addToGroup)
        {
            if (root == null) return;
            RestoreResizePreview();
            RestoreGroupPreview();
            if (addToGroup)
            {
                int existing = _group.IndexOf(root);
                if (existing >= 0)
                {
                    if (_group.Count == 1)
                    {
                        Clear();
                        SetStatus("selection cleared", ToneOk);
                        return;
                    }
                    _group.RemoveAt(existing);
                    root = _group.Contains(_root) ? _root : _group[0];
                }
                else
                {
                    if (_group.Count >= MaxGroupSelection)
                    { SetStatus("selection limit is " + MaxGroupSelection + " items", ToneErr); return; }
                    _group.Add(root);
                }
            }
            else
            {
                _group.Clear();
                _group.Add(root);
            }
            _gizmo.Cancel(false);
            _root = root;
            _has = true;
            _info = null;
            _groupPreviewStarts.Clear();
            _gizmo.RotationEnabled = _group.Count <= MaxGroupMove;
            _gizmo.GroupRotationEnabled = _group.Count > 1 && _group.Count <= MaxGroupMove;
            _deleteArmUntil = 0f;
            _deleteIds = "";
            _deleteConfirmCount = 0;
            _resetPositionArmed = false;
            try { _id = root.Identifier; } catch { _id = 0; }

            SetStatus("selected " + _group.Count + " item" + (_group.Count == 1 ? "" : "s") + " - fetching info...");
            _api.Info(_id, OnInfo);
        }

        void PreviewGroupMove(Vector3 delta)
        {
            _groupMoveDelta = delta;
            if (_group.Count < 2 || _root == null) return;
            if (_groupPreviewStarts.Count != _group.Count)
            {
                _groupPreviewStarts.Clear();
                for (int i = 0; i < _group.Count; i++)
                {
                    Vector3 p = Vector3.zero;
                    try { if (_group[i] != null) p = _group[i].transform.position; } catch { }
                    _groupPreviewStarts.Add(p);
                }
            }
            for (int i = 0; i < _group.Count; i++)
            {
                NetworkEntity item = _group[i];
                if (item == null || item == _root) continue;
                try { item.transform.position = _groupPreviewStarts[i] + delta; } catch { }
            }
        }

        void PreviewGroupRotation(Vector3 axis, float angle)
        {
            if (_group.Count < 2 || _root == null) return;
            if (_groupRotationPositions.Count != _group.Count)
            {
                _groupRotationPositions.Clear();
                _groupRotationRotations.Clear();
                for (int i = 0; i < _group.Count; i++)
                {
                    try
                    {
                        _groupRotationPositions.Add(_group[i].transform.position);
                        _groupRotationRotations.Add(_group[i].transform.rotation);
                    }
                    catch
                    {
                        _groupRotationPositions.Add(Vector3.zero);
                        _groupRotationRotations.Add(Quaternion.identity);
                    }
                }
            }
            Vector3 pivot = _root.transform.position;
            Quaternion delta = Quaternion.AngleAxis(angle, axis);
            for (int i = 0; i < _group.Count; i++)
            {
                NetworkEntity item = _group[i];
                if (item == null || item == _root) continue;
                try
                {
                    item.transform.position = pivot + delta * (_groupRotationPositions[i] - pivot);
                    item.transform.rotation = delta * _groupRotationRotations[i];
                }
                catch { }
            }
        }

        void RestoreGroupPreview()
        {
            if (_groupPreviewStarts.Count == _group.Count)
                for (int i = 0; i < _group.Count; i++)
                {
                    NetworkEntity item = _group[i];
                    if (item == null || item == _root) continue;
                    try { item.transform.position = _groupPreviewStarts[i]; } catch { }
                }
            _groupPreviewStarts.Clear();
            if (_groupRotationPositions.Count == _group.Count &&
                _groupRotationRotations.Count == _group.Count)
                for (int i = 0; i < _group.Count; i++)
                {
                    NetworkEntity item = _group[i];
                    if (item == null || item == _root) continue;
                    try
                    {
                        item.transform.position = _groupRotationPositions[i];
                        item.transform.rotation = _groupRotationRotations[i];
                    }
                    catch { }
                }
            _groupRotationPositions.Clear();
            _groupRotationRotations.Clear();
        }

        Vector3 SnapMoveToSurface(Vector3 desiredPosition, Vector2 mousePosition)
        {
            if (!_surfaceSnap || Camera == null || _root == null || _group.Count == 0) return desiredPosition;
            Bounds movingBounds = new Bounds();
            bool hasBounds = false;
            for (int i = 0; i < _group.Count; i++)
            {
                Bounds itemBounds;
                if (_group[i] == null || !TryBounds(_group[i], out itemBounds)) continue;
                if (!hasBounds) { movingBounds = itemBounds; hasBounds = true; }
                else movingBounds.Encapsulate(itemBounds);
            }
            if (!hasBounds) return desiredPosition;

            Vector3 currentPosition;
            try { currentPosition = _root.transform.position; }
            catch { return desiredPosition; }
            movingBounds.center += desiredPosition - currentPosition;

            Ray ray = Camera.ScreenPointToRay(new Vector3(mousePosition.x, mousePosition.y, 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return desiredPosition;
            Array.Sort(hits, CompareHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || IsSelectedCollider(col)) continue;
                try { if (col.GetComponentInParent<PlayerController>() != null) continue; }
                catch { }

                Vector3 normal = hits[i].normal;
                if (normal.sqrMagnitude < 0.5f) continue;
                normal.Normalize();
                Vector3 ext = movingBounds.extents;
                float support = Mathf.Abs(normal.x) * ext.x + Mathf.Abs(normal.y) * ext.y + Mathf.Abs(normal.z) * ext.z;
                float correction = Vector3.Dot(hits[i].point - movingBounds.center, normal) + support;
                if (Mathf.Abs(correction) > SurfaceSnapDistance) continue;
                return desiredPosition + normal * correction;
            }
            return desiredPosition;
        }

        bool IsSelectedCollider(Collider col)
        {
            Transform hit = col != null ? col.transform : null;
            if (hit == null) return false;
            for (int i = 0; i < _group.Count; i++)
            {
                try
                {
                    Transform selected = _group[i] != null ? _group[i].transform : null;
                    if (selected != null && (hit == selected || hit.IsChildOf(selected))) return true;
                }
                catch { }
            }
            return false;
        }

        void OnInfo(EditResult r)
        {
            if (!r.Ok) { _grabPending = false; _grabPendingRoot = null; SetStatus("info: " + Reason(r)); return; }
            if (r.Ent != null)
            {
                _info = r.Ent;
                SyncTransformFields(r.Ent);
                SetStatus("selected " + r.Ent.Prefab + " #" + r.Ent.Id +
                    (_group.Count > 1 ? " with " + (_group.Count - 1) + " others" : "") + CandNote());
                TryBeginPendingGrab();
            }
        }

        void TryBeginPendingGrab()
        {
            if (!_grabPending || _root == null || _root != _grabPendingRoot ||
                Mouse.current == null || !Mouse.current.leftButton.isPressed)
                return;
            Transform target = null;
            try { target = _root.transform; } catch { }
            if (target != null)
                _gizmo.BeginDirectDrag(Camera, target, _grabPendingPoint, GhostWanted());
            _grabPending = false;
            _grabPendingRoot = null;
        }

        // ---- gizmo commits: ONE server call per released drag ----

        void CommitGizmoMove(Vector3 p)
        {
            if (!_has || _busy) { _gizmo.RestoreOriginal(); return; }
            _busy = true;
            if (_group.Count > 1)
            {
                if (_group.Count > MaxGroupMove)
                {
                    _busy = false;
                    _gizmo.RestoreOriginal();
                    RestoreGroupPreview();
                    SetStatus("group move supports up to " + MaxGroupMove
                        + " items; selection kept", ToneErr);
                    return;
                }
                string ids = "";
                for (int i = 0; i < _group.Count; i++)
                {
                    uint id = 0;
                    try { if (_group[i] != null) id = _group[i].Identifier; } catch { }
                    if (id == 0) { _busy = false; _gizmo.RestoreOriginal(); RestoreGroupPreview(); SetStatus("a selected item streamed out", ToneErr); return; }
                    if (i > 0) ids += ",";
                    ids += id.ToString(CultureInfo.InvariantCulture);
                }
                SetStatus("moving " + _group.Count + " items together ...");
                _api.MoveMany(ids, _groupMoveDelta.x, _groupMoveDelta.y, _groupMoveDelta.z, OnGizmoPlaced);
            }
            else
            {
                SetStatus("moving #" + _id + " ...");
                _api.Move(_id, p.x, p.y, p.z, OnGizmoPlaced);
            }
        }

        void CommitGizmoRotate(Vector3 e)
        {
            if (!_has || _busy) { _gizmo.RestoreOriginal(); return; }
            _busy = true;
            SetStatus("rotating #" + _id + " ...");
            _api.Rotate(_id, e.x, e.y, e.z, OnGizmoPlaced);
        }

        string GroupRotationIds()
        {
            if (_group.Count < 2 || _group.Count > MaxGroupMove || _root == null) return null;
            string ids = "";
            for (int i = -1; i < _group.Count; i++)
            {
                NetworkEntity item = i < 0 ? _root : _group[i];
                if (i >= 0 && item == _root) continue;
                uint id = 0;
                try { if (item != null) id = item.Identifier; } catch { }
                if (id == 0) return null;
                if (ids.Length > 0) ids += ",";
                ids += id.ToString(CultureInfo.InvariantCulture);
            }
            return ids;
        }

        void CommitGroupRotation(Vector3 axis, float angle)
        {
            if (!_has || _busy) { _gizmo.RestoreOriginal(); RestoreGroupPreview(); return; }
            string ids = GroupRotationIds();
            if (ids == null)
            {
                _gizmo.RestoreOriginal();
                RestoreGroupPreview();
                SetStatus("group rotation needs 2 to " + MaxGroupMove + " loaded items", ToneErr);
                return;
            }
            Vector3 e = axis * angle;
            _busy = true;
            SetStatus("rotating " + _group.Count + " items around the primary item ...");
            _api.RotateMany(ids, e.x, e.y, e.z, OnGizmoPlaced);
        }

        // Like OnPlaced, but a refusal also rolls the local preview back, and success
        // pins the transform to the server's own answer (instant convergence - covers
        // ghost mode and a lagging sync; it's the authority's value, so still its call).
        void OnGizmoPlaced(EditResult r)
        {
            _busy = false;
            if (!r.Ok)
            {
                _gizmo.RestoreOriginal();
                RestoreGroupPreview();
                SetStatus("refused: " + Reason(r), ToneErr);
                return;
            }
            if (r.Ent != null)
            {
                _info = r.Ent;
                SyncTransformFields(r.Ent);
                try
                {
                    if (_root != null)
                    {
                        _root.transform.position = new Vector3(r.Ent.X, r.Ent.Y, r.Ent.Z);
                        _root.transform.eulerAngles = new Vector3(r.Ent.Ex, r.Ent.Ey, r.Ent.Ez);
                    }
                }
                catch { }
            }
            _groupPreviewStarts.Clear();
            _groupRotationPositions.Clear();
            _groupRotationRotations.Clear();
            string note = string.IsNullOrEmpty(r.Note) ? "" : "  (" + r.Note + ")";
            SetStatus("done #" + _id + note, ToneOk);
            RefreshHistory(false);
        }

        // ---- button actions (invoked from OnGUI) ----

        void Ground()
        {
            if (_busy || !_has) return;
            _busy = true;
            SetStatus("grounding #" + _id + " ...");
            _api.Ground(_id, OnPlaced);
        }

        void SyncTransformFields(EditEntity e)
        {
            if (e == null) return;
            _positionX = F(e.X); _positionY = F(e.Y); _positionZ = F(e.Z);
            _rotationX = F(e.Ex); _rotationY = F(e.Ey); _rotationZ = F(e.Ez);
        }

        void ApplyPositionFields()
        {
            if (_busy || !_has || _group.Count != 1) return;
            Vector3 position;
            if (!TryVector(_positionX, _positionY, _positionZ, out position)
                || float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)
                || float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
            { SetStatus("enter valid position values", ToneErr); return; }
            _busy = true;
            SetStatus("moving #" + _id + " to " + VecText(position) + "...");
            _api.Move(_id, position.x, position.y, position.z, OnPlaced);
        }

        void ApplyRotationFields()
        {
            if (_busy || !_has || _group.Count != 1) return;
            Vector3 euler;
            if (!TryEulerFields(out euler)) { SetStatus("enter valid rotation values", ToneErr); return; }
            _busy = true;
            SetStatus("applying rotation to #" + _id + "...");
            _api.Rotate(_id, euler.x, euler.y, euler.z, OnPlaced);
        }

        void ApplyGroupRotation(Vector3 delta)
        {
            if (_busy || !_has) return;
            if (delta.sqrMagnitude < 0.000001f)
            { SetStatus("enter a nonzero group rotation", ToneErr); return; }
            string ids = GroupRotationIds();
            if (ids == null) { SetStatus("group rotation needs 2 to " + MaxGroupMove + " loaded items", ToneErr); return; }
            if (Mathf.Abs(delta.x) > 3600f || Mathf.Abs(delta.y) > 3600f || Mathf.Abs(delta.z) > 3600f)
            { SetStatus("rotation must be within 3600 degrees per axis", ToneErr); return; }
            _busy = true;
            SetStatus("rotating " + _group.Count + " items around the primary item ...");
            _api.RotateMany(ids, delta.x, delta.y, delta.z, OnPlaced);
        }

        void ApplyGroupRotationFields()
        {
            float x, y, z;
            if (!float.TryParse(_groupRotationX, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(_groupRotationY, NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !float.TryParse(_groupRotationZ, NumberStyles.Float, CultureInfo.InvariantCulture, out z) ||
                float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
            { SetStatus("enter valid group rotation angles", ToneErr); return; }
            ApplyGroupRotation(new Vector3(x, y, z));
        }

        void StepGroupYaw(float direction)
        {
            float step;
            if (!TryRotationStep(out step))
            { SetStatus("yaw step must be between 1 and 180 degrees", ToneErr); return; }
            ApplyGroupRotation(new Vector3(0f, direction * step, 0f));
        }

        bool TryEulerFields(out Vector3 euler)
        {
            euler = Vector3.zero;
            float ex = 0f, ey = 0f, ez = 0f;
            bool valid = float.TryParse(_rotationX, NumberStyles.Float, CultureInfo.InvariantCulture, out ex)
                && float.TryParse(_rotationY, NumberStyles.Float, CultureInfo.InvariantCulture, out ey)
                && float.TryParse(_rotationZ, NumberStyles.Float, CultureInfo.InvariantCulture, out ez);
            if (!valid || float.IsNaN(ex) || float.IsNaN(ey) || float.IsNaN(ez)
                || float.IsInfinity(ex) || float.IsInfinity(ey) || float.IsInfinity(ez)) return false;
            euler = new Vector3(ex, ey, ez);
            return true;
        }

        void ResetPosition()
        {
            if (_busy || !_has || _group.Count != 1) return;
            if (!_resetPositionArmed || Time.unscaledTime > _resetPositionUntil)
            {
                _resetPositionArmed = true;
                _resetPositionUntil = Time.unscaledTime + DeleteArmSeconds;
                SetStatus("click Reset Position again to move this item to world origin", ToneErr);
                return;
            }
            _resetPositionArmed = false;
            Vector3 euler = CurrentEuler();
            _positionX = _positionY = _positionZ = "0";
            _busy = true;
            _api.Transform(_id, 0f, 0f, 0f, euler.x, euler.y, euler.z, OnPlaced);
        }

        void ResetRotation()
        {
            if (_busy || !_has || _group.Count != 1) return;
            _rotationX = _rotationY = _rotationZ = "0";
            _busy = true;
            Vector3 p = CurrentPos();
            _api.Transform(_id, p.x, p.y, p.z, 0f, 0f, 0f, OnPlaced);
        }

        void CopyTransform()
        {
            if (!_has || _info == null) return;
            _transformClipboardPosition = new Vector3(_info.X, _info.Y, _info.Z);
            _transformClipboardEuler = new Vector3(_info.Ex, _info.Ey, _info.Ez);
            _hasTransformClipboard = true;
            SetStatus("copied position and rotation from #" + _id, ToneOk);
        }

        void PasteTransform()
        {
            if (_busy || !_has) return;
            if (!_hasTransformClipboard) { SetStatus("copy a transform first", ToneErr); return; }
            Vector3 p = _transformClipboardPosition, e = _transformClipboardEuler;
            _positionX = F(p.x); _positionY = F(p.y); _positionZ = F(p.z);
            _rotationX = F(e.x); _rotationY = F(e.y); _rotationZ = F(e.z);
            _busy = true;
            SetStatus("pasting transform to " + (_group.Count > 1 ? "primary selection #" : "#") + _id + "...");
            _api.Transform(_id, p.x, p.y, p.z, e.x, e.y, e.z, OnPlaced);
        }

        bool TryRotationStep(out float step)
        {
            step = 0f;
            return float.TryParse(_rotationStep, NumberStyles.Float, CultureInfo.InvariantCulture, out step)
                && !float.IsNaN(step) && !float.IsInfinity(step) && step >= 1f && step <= 180f;
        }

        void SnapRotation()
        {
            float step;
            if (!TryRotationStep(out step)) { SetStatus("rotation snap must be between 1 and 180 degrees", ToneErr); return; }
            Vector3 e;
            if (!TryEulerFields(out e)) { SetStatus("enter valid rotation values", ToneErr); return; }
            e.x = Mathf.Round(e.x / step) * step;
            e.y = Mathf.Round(e.y / step) * step;
            e.z = Mathf.Round(e.z / step) * step;
            _rotationX = F(e.x); _rotationY = F(e.y); _rotationZ = F(e.z);
            ApplyRotationFields();
        }

        void StepYaw(float direction)
        {
            float step;
            if (!TryRotationStep(out step)) { SetStatus("rotation snap must be between 1 and 180 degrees", ToneErr); return; }
            Vector3 e;
            if (!TryEulerFields(out e)) { SetStatus("enter valid rotation values", ToneErr); return; }
            e.y += step * direction;
            _rotationX = F(e.x); _rotationY = F(e.y); _rotationZ = F(e.z);
            ApplyRotationFields();
        }

        void Yaw(float delta)
        {
            if (_busy || !_has) return;
            Vector3 e = CurrentEuler();
            _busy = true;
            SetStatus("rotating #" + _id + " ...");
            _api.Rotate(_id, e.x, e.y + delta, e.z, OnPlaced);
        }

        void OnPlaced(EditResult r)
        {
            _busy = false;
            if (!r.Ok) { SetStatus("refused: " + Reason(r), ToneErr); return; }
            if (r.Ent != null) { _info = r.Ent; SyncTransformFields(r.Ent); }
            string note = string.IsNullOrEmpty(r.Note) ? "" : "  (" + r.Note + ")";
            SetStatus("done #" + _id + note, ToneOk);
            RefreshHistory(false);
        }

        void DeleteClick()
        {
            if (_busy || !_has) return;
            float now = Time.unscaledTime;
            if (now < _deleteArmUntil && !string.IsNullOrEmpty(_deleteIds))
            {
                _deleteArmUntil = 0f;
                _busy = true;
                SetStatus("deleting " + _deleteConfirmCount + " selected item(s)...");
                _api.DeleteMany(_deleteIds, true, delegate(EditResult r)
                {
                    _busy = false;
                    _deleteIds = "";
                    if (!r.Ok)
                    {
                        SetStatus("delete refused: " + Reason(r), ToneErr);
                        RefreshHistory(false);
                        return;
                    }
                    int count = r.Count > 0 ? r.Count : _deleteConfirmCount;
                    _deleteConfirmCount = 0;
                    Clear();
                    RefreshHistory(false);
                    SetStatus("deleted " + count + " item(s) - Ctrl+Z to undo", ToneOk);
                });
            }
            else
            {
                string ids = SelectionIds();
                if (string.IsNullOrEmpty(ids)) { SetStatus("no live selected items to delete", ToneErr); return; }
                _busy = true;
                _deleteIds = ids;
                _deleteConfirmCount = _group.Count;
                SetStatus("checking that all " + _group.Count + " selected item(s) can be deleted...");
                _api.DeleteMany(ids, false, delegate(EditResult r)
                {
                    _busy = false;
                    if (!r.NeedsConfirm)
                    {
                        _deleteIds = "";
                        _deleteArmUntil = 0f;
                        SetStatus("delete refused: " + Reason(r), ToneErr);
                        return;
                    }
                    _deleteConfirmCount = r.Count > 0 ? r.Count : _group.Count;
                    _deleteArmUntil = Time.unscaledTime + DeleteArmSeconds;
                    SetStatus("ready to delete " + _deleteConfirmCount + " item(s); click confirm within 6 seconds");
                });
            }
        }

        // ---- copy / paste (save strings through the blueprint + edit modules) ----

        void CopyString()
        {
            if (_busy || !_has) return;
            _busy = true;
            SetStatus("exporting #" + _id + " ...");
            _api.ExportString(_id, OnExported);
        }

        void OnExported(EditResult r)
        {
            _busy = false;
            if (!r.Ok) { SetStatus("tostring: " + Reason(r), ToneErr); return; }
            string s = Json.Str(r.Raw, "string");
            if (string.IsNullOrEmpty(s)) { SetStatus("tostring: no string in the answer", ToneErr); return; }
            string label = Json.Str(r.Raw, "label");
            _copied = s;
            _copiedLabel = string.IsNullOrEmpty(label) ? ("#" + _id) : label;
            try { GUIUtility.systemCopyBuffer = s; } catch { }
            SetStatus("copied " + _copiedLabel + " (" + s.Length + " chars) - clipboard + Paste", ToneOk);
        }

        // Paste respawns the buffered string at the center-screen hit point (else a
        // few meters ahead), announced back with the NEW id - click it to gizmo it.
        // No buffer? Try the OS clipboard, so web-panel/workbench strings paste in.
        void Paste()
        {
            if (_busy) return;
            string s = _copied;
            string label = _copiedLabel;
            if (s == null)
            {
                s = ClipboardSaveString();
                label = "clipboard string";
            }
            if (s == null)
            {
                SetStatus("nothing to paste - Copy string first (or put a save string on the clipboard)");
                return;
            }
            Vector3 at;
            if (!PasteTarget(out at)) { SetStatus("no spot in view to paste at"); return; }
            _busy = true;
            SetStatus("pasting " + label + " ...");
            _api.Paste(at.x, at.y, at.z, s, OnPasted);
        }

        void OnPasted(EditResult r)
        {
            _busy = false;
            if (!r.Ok) { SetStatus("paste refused: " + Reason(r), ToneErr); return; }
            RefreshHistory(false);
            if (r.Ent != null)
            {
                SetStatus("pasted " + r.Ent.Prefab + " #" + r.Ent.Id, ToneOk);
                AdoptSoon(r.Ent.Id); // select it as soon as it streams in
            }
            else SetStatus("pasted", ToneOk);
        }

        // Replace swaps the SELECTED entity for the buffered/clipboard string at
        // its exact spot+rotation (in-place editing, lean form). The workflow:
        // Copy string -> tweak it in the workbench -> Replace. The server spawns
        // the new one before deleting the old, so a bad string costs nothing.
        void ReplaceSelected()
        {
            if (_busy || !_has) return;
            string s = _copied;
            string label = _copiedLabel;
            if (s == null)
            {
                s = ClipboardSaveString();
                label = "clipboard string";
            }
            if (s == null)
            {
                SetStatus("nothing to replace with - Copy string first (or put a save string on the clipboard)");
                return;
            }
            _busy = true;
            SetStatus("replacing #" + _id + " with " + label + " ...");
            _api.Replace(_id, s, OnReplaced);
        }

        // Scale is applied and recorded on the server, so remote Tailscale users do
        // not need a separate workbench process listening on their own PC.
        void ResizeSelected(float factor)
        {
            if (_busy || !_has) return;
            if (_group.Count != 1)
            {
                RestoreResizePreview();
                SetStatus("resize one item at a time", ToneErr);
                return;
            }
            if (factor < 0.5f || factor > 2f)
            {
                RestoreResizePreview();
                SetStatus("resize amount must be between -50% and +100%", ToneErr);
                return;
            }
            uint id = _id;
            _busy = true;
            SetStatus("scaling #" + id + " ...");
            _api.Scale(id, factor, OnReplaced);
        }

        void OnReplaced(EditResult r)
        {
            _busy = false;
            if (!r.Ok)
            {
                RestoreResizePreview();
                SetStatus("replace refused: " + Reason(r), ToneErr);
                return;
            }
            uint oldId = _id;
            Clear(); // the old entity is gone server-side
            if (r.Ent != null)
            {
                SetStatus("replaced #" + oldId + " -> " + r.Ent.Prefab + " #" + r.Ent.Id, ToneOk);
                AdoptSoon(r.Ent.Id); // reselect the successor as it streams in
            }
            else SetStatus("replaced #" + oldId, ToneOk);
            RefreshHistory(false);
        }

        // Accept the clipboard only if it looks exactly like a save string
        // (digits/commas/pipe/./+/- and at least one '|', the two-half joiner).
        static string ClipboardSaveString()
        {
            string s = null;
            try { s = GUIUtility.systemCopyBuffer; } catch { return null; }
            if (s == null) return null;
            s = s.Trim();
            if (!IsSaveString(s)) return null;
            return s;
        }

        static bool IsSaveString(string s)
        {
            // Older Tavern prefabs store the serialized object as a comma-only
            // numeric record; newer captures can append pipe-delimited data.
            // Both forms are accepted by SerializedSavedDynamicObject, so only
            // require a separator instead of insisting on the newer pipe form.
            if (s == null || s.Length < 8 || s.Length > 200000 ||
                (s.IndexOf(',') < 0 && s.IndexOf('|') < 0)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c < '0' || c > '9') && c != ',' && c != '|' && c != '.' && c != '-' && c != '+')
                    return false;
            }
            return true;
        }

        // ---- local named layouts: save a selection as the same JSON the importer reads ----

        void SaveSelectedLayout()
        {
            if (_busy || !_has || _group.Count == 0) return;
            using (WinForms.SaveFileDialog dialog = new WinForms.SaveFileDialog())
            {
                dialog.Title = "Save selected prefabs as a reusable layout";
                dialog.Filter = "Prefab layout (*.json)|*.json|All files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "prefab-layout.json";
                if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

                _layoutSavePath = dialog.FileName;
                _layoutSaveAt = 0;
                _layoutToSave = new List<LayoutPrefab>();
                for (int i = 0; i < _group.Count; i++)
                {
                    NetworkEntity root = _group[i];
                    if (root == null || root.IsBeingDestroyed)
                    {
                        SetStatus("layout save stopped: an item streamed out", ToneErr);
                        return;
                    }
                    uint id;
                    try { id = root.Identifier; } catch { id = 0; }
                    if (id == 0)
                    {
                        SetStatus("layout save stopped: a selected item has no live id", ToneErr);
                        return;
                    }
                    _layoutToSave.Add(new LayoutPrefab
                    {
                        Id = id,
                        Name = PrefabName(root),
                        Position = root.transform.position,
                        Rotation = root.transform.eulerAngles
                    });
                }
            }
            _busy = true;
            _layoutSaving = true;
            SetStatus("saving layout: reading " + _layoutToSave.Count + " prefab strings...");
            SaveLayoutNext();
        }

        void SaveLayoutNext()
        {
            if (!_layoutSaving) return;
            if (_layoutSaveAt >= _layoutToSave.Count)
            {
                WriteLayoutFile();
                return;
            }
            LayoutPrefab entry = _layoutToSave[_layoutSaveAt];
            _api.ExportString(entry.Id, delegate(EditResult r)
            {
                if (!r.Ok)
                {
                    _layoutSaving = false;
                    _busy = false;
                    SetStatus("layout save stopped on " + entry.Name + ": " + Reason(r), ToneErr);
                    return;
                }
                string save = Json.Str(r.Raw, "string");
                if (!IsSaveString(save))
                {
                    _layoutSaving = false;
                    _busy = false;
                    SetStatus("layout save stopped: server returned an invalid string for " + entry.Name, ToneErr);
                    return;
                }
                entry.SaveString = save;
                _layoutSaveAt++;
                SetStatus("saving layout: " + _layoutSaveAt + " / " + _layoutToSave.Count);
                SaveLayoutNext();
            });
        }

        void WriteLayoutFile()
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(_layoutSavePath);
                StringBuilder json = new StringBuilder();
                json.Append("{\"name\":\"").Append(JsonEscape(fileName)).Append("\",\"prefabs\":[");
                for (int i = 0; i < _layoutToSave.Count; i++)
                {
                    LayoutPrefab p = _layoutToSave[i];
                    if (i > 0) json.Append(',');
                    json.Append("{\"name\":\"").Append(JsonEscape(p.Name)).Append("\",\"string\":\"")
                        .Append(p.SaveString).Append("\",\"position\":{\"x\":").Append(JsonNumber(p.Position.x))
                        .Append(",\"y\":").Append(JsonNumber(p.Position.y)).Append(",\"z\":").Append(JsonNumber(p.Position.z))
                        .Append("},\"rotation\":{\"x\":").Append(JsonNumber(p.Rotation.x))
                        .Append(",\"y\":").Append(JsonNumber(p.Rotation.y)).Append(",\"z\":").Append(JsonNumber(p.Rotation.z))
                        .Append("}}");
                }
                json.Append("]}");
                if (json.Length > 10 * 1024 * 1024)
                    throw new InvalidOperationException("layout exceeds the 10 MB import limit; save a smaller selection");
                File.WriteAllText(_layoutSavePath, json.ToString(), new UTF8Encoding(false));
                _layoutSaving = false;
                _busy = false;
                SetStatus("saved layout " + Path.GetFileName(_layoutSavePath) + " (" + _layoutToSave.Count + " items)", ToneOk);
            }
            catch (Exception e)
            {
                _layoutSaving = false;
                _busy = false;
                SetStatus("could not save layout: " + e.Message, ToneErr);
            }
        }

        static string JsonEscape(string value)
        {
            if (value == null) return "";
            StringBuilder escaped = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') escaped.Append("\\\"");
                else if (c == '\\') escaped.Append("\\\\");
                else if (c == '\n') escaped.Append("\\n");
                else if (c == '\r') escaped.Append("\\r");
                else if (c == '\t') escaped.Append("\\t");
                else if (c < 32) escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else escaped.Append(c);
            }
            return escaped.ToString();
        }

        static string JsonNumber(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("a selected item has an invalid transform");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // ---- bulk import: blueprint JSON exported by the server kit ----

        void ReadImportFile()
        {
            _importEntries.Clear();
            string path = (_importPath ?? "").Trim();
            if (path.Length == 0) { SetStatus("enter the full path to a prefab JSON file", ToneErr); return; }
            if ((path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"') ||
                (path.Length >= 2 && path[0] == '\'' && path[path.Length - 1] == '\''))
                path = path.Substring(1, path.Length - 2).Trim();
            if (path.StartsWith("[") || path.StartsWith("{"))
            { SetStatus("that looks like JSON text, not a file path - save it as .json, then click Browse JSON", ToneErr); return; }
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) { SetStatus("file not found: " + path, ToneErr); return; }
                if (fi.Length <= 0 || fi.Length > 10 * 1024 * 1024)
                { SetStatus("JSON file must be between 1 byte and 10 MB", ToneErr); return; }

                string contents = File.ReadAllText(path);
                string entriesJson = contents.Trim();
                if (entriesJson.StartsWith("{"))
                {
                    string wrapped = Json.Value(entriesJson, "prefabs");
                    if (wrapped != null) entriesJson = wrapped;
                }
                List<string> objects = Json.Objects(entriesJson);
                if (objects.Count == 0 || objects.Count > MaxImportPrefabs)
                { SetStatus("expected a JSON blueprint with 1 to " + MaxImportPrefabs + " prefab entries", ToneErr); return; }

                List<ImportedPrefab> parsed = new List<ImportedPrefab>();
                for (int i = 0; i < objects.Count; i++)
                {
                    string obj = objects[i];
                    string save = Json.Str(obj, "string");
                    string pos = Json.Value(obj, "position");
                    if (pos == null) pos = Json.Value(obj, "Position");
                    string rot = Json.Value(obj, "rotation");
                    if (rot == null) rot = Json.Value(obj, "Rotation");
                    if (rot == null) rot = Json.Value(obj, "roteuler");
                    float x = Json.Num(pos, "x", float.NaN);
                    float y = Json.Num(pos, "y", float.NaN);
                    float z = Json.Num(pos, "z", float.NaN);
                    float ex = Json.Num(rot, "x", 0f);
                    float ey = Json.Num(rot, "y", 0f);
                    float ez = Json.Num(rot, "z", 0f);
                    if (!IsSaveString(save) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                        float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z) ||
                        float.IsInfinity(ex) || float.IsInfinity(ey) || float.IsInfinity(ez))
                    {
                        SetStatus("invalid prefab entry " + (i + 1) + " (needs save string and position)", ToneErr);
                        return;
                    }
                    ImportedPrefab entry = new ImportedPrefab();
                    entry.Name = Json.Str(obj, "name");
                    if (string.IsNullOrEmpty(entry.Name)) entry.Name = Json.Str(obj, "Name");
                    if (string.IsNullOrEmpty(entry.Name)) entry.Name = "prefab " + (i + 1);
                    entry.SaveString = save;
                    entry.Position = new Vector3(x, y, z);
                    entry.Rotation = new Vector3(ex, ey, ez);
                    parsed.Add(entry);
                }
                _importEntries = parsed;
                SetStatus("read " + parsed.Count + " prefabs; click Import to place at saved positions", ToneOk);
            }
            catch (ArgumentException) { SetStatus("invalid file path - use Browse JSON or paste a file path", ToneErr); }
            catch (Exception e) { SetStatus("could not read JSON file: " + e.Message, ToneErr); }
        }

        void BrowseImportFile()
        {
            try
            {
                using (WinForms.OpenFileDialog dialog = new WinForms.OpenFileDialog())
                {
                    dialog.Title = "Choose a prefab JSON file";
                    dialog.Filter = "JSON or text files (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        _importPath = dialog.FileName;
                        ReadImportFile();
                    }
                }
            }
            catch (Exception e) { SetStatus("could not open file picker: " + e.Message, ToneErr); }
        }

        void StartImport()
        {
            if (_importing || _importEntries == null || _importEntries.Count == 0) return;
            _importQueue = new Queue<ImportedPrefab>(_importEntries);
            _importTotal = _importQueue.Count;
            _importDone = 0;
            _importFailed = 0;
            _importSpawnedIds.Clear();
            _importOffset = Vector3.zero;
            if (_importAtCamera && Camera != null && _importEntries.Count > 0)
            {
                Vector3 destination = Camera.transform.position + Camera.transform.forward * 3f;
                _importOffset = destination - _importEntries[0].Position;
            }
            _importing = true;
            ImportNext();
        }

        void ImportNext()
        {
            if (_importQueue.Count == 0)
            {
                _importing = false;
                if (_importSpawnedIds.Count > 0) AdoptManySoon(_importSpawnedIds, "imported");
                SetStatus("import finished: " + _importDone + " placed, " + _importFailed + " failed",
                    _importFailed == 0 ? ToneOk : ToneErr);
                return;
            }
            ImportedPrefab entry = _importQueue.Dequeue();
            int number = _importTotal - _importQueue.Count;
            Vector3 position = entry.Position + _importOffset;
            SetStatus("importing " + number + "/" + _importTotal + " - " + entry.Name);
            _api.Paste(position.x, position.y, position.z, entry.SaveString,
                delegate(EditResult r)
                {
                    if (!r.Ok || r.Ent == null)
                    {
                        _importFailed++;
                        ImportNext();
                        return;
                    }
                    if (!_importSpawnedIds.Contains(r.Ent.Id)) _importSpawnedIds.Add(r.Ent.Id);
                    if (Mathf.Abs(entry.Rotation.x) > 0.01f || Mathf.Abs(entry.Rotation.y) > 0.01f ||
                        Mathf.Abs(entry.Rotation.z) > 0.01f)
                    {
                        _api.Rotate(r.Ent.Id, entry.Rotation.x, entry.Rotation.y, entry.Rotation.z,
                            delegate(EditResult rr)
                            {
                                if (rr.Ok) _importDone++;
                                else _importFailed++;
                                ImportNext();
                            });
                    }
                    else
                    {
                        _importDone++;
                        ImportNext();
                    }
                });
        }

        bool PasteTarget(out Vector3 at)
        {
            at = Vector3.zero;
            if (Camera == null) return false;
            try
            {
                Ray ray = Camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                RaycastHit hit;
                // triggers ignored: pasting "onto" an invisible ambience volume
                // left the object floating mid-air
                if (Physics.Raycast(ray, out hit, 400f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore))
                    at = hit.point + Vector3.up * 0.05f;
                else at = ray.origin + ray.direction * 4f;
                return true;
            }
            catch { return false; }
        }

        // ---- spawn-with-preview: catalog search -> cursor ghost -> edit spawnat ----

        void ToggleSpawn()
        {
            if (_spawnOpen) { CloseSpawn(); return; }
            _spawnOpen = true;
            _spawnSent = null;
            _spawnCatalogLoaded = false;
            _spawnCatalogAttempted = false;
            _spawnStaticFallback = false;
            _spawnCatalogLoading = false;
            _spawnBusy = false;
            _hitNames.Clear();
            _hitHashes.Clear();
        }

        void CloseSpawn()
        {
            _spawnOpen = false;
            _placing = false;
            _searchFocused = false;
        }

        // Load the server's live list once per window open, then search locally.
        void TickSearch()
        {
            if (_spawnBusy) return;
            if (Time.unscaledTime < _spawnSendAt) return;

            if (_spawnStaticFallback)
            {
                if (_spawnQuery == _spawnSent) return;
                _spawnBusy = true;
                string fallbackQuery = _spawnQuery;
                _api.Prefabs(fallbackQuery, 2000, delegate(EditResult r) { OnStaticPrefabs(fallbackQuery, r); });
                return;
            }

            if (!_spawnCatalogAttempted)
            {
                _spawnCatalogAttempted = true;
                _spawnBusy = true;
                _spawnCatalogLoading = true;
                _api.Prefabs(OnPrefabs);
                return;
            }

            if (!_spawnCatalogLoaded || _spawnQuery == _spawnSent) return;
            FilterSpawnCatalog(_spawnQuery);
        }

        void OnPrefabs(EditResult r)
        {
            _spawnBusy = false;
            _spawnCatalogLoading = false;
            string arr = Json.Value(r.Raw, "structured");
            List<string> objs = Json.Objects(arr);
            if (!r.Ok || objs.Count == 0)
            {
                _spawnStaticFallback = true;
                _spawnSent = null;
                SetStatus("live spawn list unavailable; using packaged catalog", ToneErr);
                return;
            }

            _spawnCatalog.Clear();
            for (int i = 0; i < objs.Count; i++)
            {
                string name = Json.Str(objs[i], "Name");
                uint hash = Json.UInt(objs[i], "Hash");
                if (name == null || hash == 0) continue;
                SpawnCatalogEntry entry = new SpawnCatalogEntry();
                entry.Name = name;
                entry.Hash = hash;
                _spawnCatalog.Add(entry);
            }
            _spawnCatalog.Sort(delegate(SpawnCatalogEntry a, SpawnCatalogEntry b)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });
            if (_spawnCatalog.Count == 0)
            {
                _spawnStaticFallback = true;
                _spawnSent = null;
                SetStatus("server returned an empty spawn list; using packaged catalog", ToneErr);
                return;
            }

            _spawnCatalogLoaded = true;
            FilterSpawnCatalog(_spawnQuery);
            SetStatus("loaded " + _spawnCatalog.Count + " live server spawnables", ToneOk);
        }

        void FilterSpawnCatalog(string q)
        {
            _spawnSent = q;
            _hitNames.Clear();
            _hitHashes.Clear();
            uint exactHash;
            bool hashSearch = uint.TryParse(q == null ? "" : q.Trim(), out exactHash);
            if (hashSearch)
            {
                for (int i = 0; i < _spawnCatalog.Count; i++)
                {
                    if (_spawnCatalog[i].Hash != exactHash) continue;
                    _hitNames.Add(_spawnCatalog[i].Name);
                    _hitHashes.Add(_spawnCatalog[i].Hash);
                    return;
                }
            }
            for (int i = 0; i < _spawnCatalog.Count; i++)
            {
                SpawnCatalogEntry entry = _spawnCatalog[i];
                if (!string.IsNullOrEmpty(q)
                    && !(hashSearch && entry.Hash == exactHash)
                    && entry.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _hitNames.Add(entry.Name);
                _hitHashes.Add(entry.Hash);
            }
        }

        void OnStaticPrefabs(string q, EditResult r)
        {
            _spawnBusy = false;
            _spawnSent = q;
            _hitNames.Clear();
            _hitHashes.Clear();
            string arr = Json.Value(r.Raw, "results");
            if (arr == null)
            {
                SetStatus("catalog search failed: " + Reason(r), ToneErr);
                return;
            }
            List<string> objs = Json.Objects(arr);
            for (int i = 0; i < objs.Count; i++)
            {
                string name = Json.Str(objs[i], "name");
                uint hash = Json.UInt(objs[i], "hash");
                if (name == null || hash == 0) continue;
                _hitNames.Add(name);
                _hitHashes.Add(hash);
            }
        }

        void BeginPlacement(string name, uint hash)
        {
            _placing = true;
            _placeName = name;
            _placeHash = hash;
            _placeValid = false;
            SetStatus("placing " + name + " - click to spawn (Shift = keep placing), Esc / right-click cancels");
        }

        void SpawnAtCamera(string name, uint hash)
        {
            if (_busy || Camera == null) return;
            Vector3 p = Camera.transform.position + Camera.transform.forward * 3f;
            _busy = true;
            _spawnOpen = false;
            SetStatus("spawning " + name + " 3 m in front of camera ...");
            _api.SpawnAt(p.x, p.y, p.z, hash, OnSpawned);
        }

        void TickPlacement(Vector2 mp, Mouse mouse)
        {
            Keyboard kb = Keyboard.current;
            if ((kb != null && kb[Key.Escape].wasPressedThisFrame) || mouse.rightButton.wasPressedThisFrame)
            {
                _placing = false;
                SetStatus("placement canceled");
                return;
            }
            _placeValid = CursorTarget(mp, out _placePos);
            if (_placeValid && !_mouseOverPanel && !_busy && mouse.leftButton.wasPressedThisFrame)
            {
                bool more = kb != null && (kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed);
                _busy = true;
                SetStatus("spawning " + _placeName + " ...");
                _api.SpawnAt(_placePos.x, _placePos.y, _placePos.z, _placeHash, OnSpawned);
                if (!more) _placing = false;
            }
        }

        void OnSpawned(EditResult r)
        {
            _busy = false;
            if (!r.Ok) { SetStatus("spawn refused: " + Reason(r), ToneErr); return; }
            if (r.Ent != null)
            {
                SetStatus("spawned " + r.Ent.Prefab + " #" + r.Ent.Id, ToneOk);
                AdoptSoon(r.Ent.Id);
            }
            else SetStatus("spawned", ToneOk);
        }

        // The ghost aims with the CURSOR (placement is a mouse activity); Paste
        // keeps the center-screen point (it's a one-click button).
        bool CursorTarget(Vector2 mp, out Vector3 at)
        {
            at = Vector3.zero;
            try
            {
                Ray ray = Camera.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
                RaycastHit hit;
                // same trigger-ignore as PasteTarget - the ghost must land on real geometry
                if (Physics.Raycast(ray, out hit, 400f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore))
                    at = hit.point + Vector3.up * 0.05f;
                else at = ray.origin + ray.direction * 4f;
                return true;
            }
            catch { return false; }
        }

        // ---- auto-select: adopt a server-spawned id once it streams in ----

        void AdoptSoon(uint id)
        {
            if (id == 0) return;
            _pendingIds.Clear();
            _pendingRoots.Clear();
            _pendingId = id;
            _pendingUntil = Time.unscaledTime + 4f;
            _pendingNextScan = 0f;
        }

        void AdoptManySoon(List<uint> ids, string label = "import")
        {
            _pendingId = 0;
            _pendingIds.Clear();
            _pendingRoots.Clear();
            _pendingManyLabel = label;
            if (ids == null) return;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] != 0 && !_pendingIds.Contains(ids[i])) _pendingIds.Add(ids[i]);
            if (_pendingIds.Count == 0) return;
            _pendingUntil = Time.unscaledTime + 60f;
            _pendingNextScan = 0f;
        }

        void TryAdoptPending()
        {
            if (_pendingIds.Count > 0)
            {
                if (Time.unscaledTime < _pendingNextScan) return;
                _pendingNextScan = Time.unscaledTime + 0.3f;
                try
                {
                    foreach (NetworkEntity e in AllEntities())
                    {
                        if (e == null) continue;
                        NetworkEntity root;
                        uint id;
                        try
                        {
                            if (e.IsBeingDestroyed) continue;
                            root = (e.PrefabRoot != null) ? e.PrefabRoot : e;
                            id = root.Identifier;
                        }
                        catch { continue; }
                        if (!_pendingIds.Contains(id)) continue;
                        if (!_pendingRoots.Contains(root)) _pendingRoots.Add(root);
                        _pendingIds.Remove(id);
                    }
                }
                catch { }
                if (_pendingIds.Count == 0 || Time.unscaledTime > _pendingUntil)
                    FinishAdoptMany();
                return;
            }
            if (Time.unscaledTime > _pendingUntil) { _pendingId = 0; return; }
            if (Time.unscaledTime < _pendingNextScan) return;
            _pendingNextScan = Time.unscaledTime + 0.3f;
            try
            {
                foreach (NetworkEntity e in AllEntities())
                {
                    if (e == null) continue;
                    NetworkEntity root;
                    try
                    {
                        if (e.IsBeingDestroyed) continue;
                        root = (e.PrefabRoot != null) ? e.PrefabRoot : e;
                        if (root.Identifier != _pendingId) continue;
                    }
                    catch { continue; }
                    _pendingId = 0;
                    _cands.Clear();   // a fresh spawn/paste retires the click's list
                    _candAt = 0;
                    Select(root);
                    return;
                }
            }
            catch { }
        }

        void FinishAdoptMany()
        {
            int missing = _pendingIds.Count;
            _pendingId = 0;
            _pendingIds.Clear();
            _cands.Clear();
            _candAt = 0;
            RestoreResizePreview();
            RestoreGroupPreview();
            _gizmo.Cancel(false);
            _group.Clear();
            for (int i = 0; i < _pendingRoots.Count && _group.Count < MaxGroupSelection; i++)
            {
                NetworkEntity root = _pendingRoots[i];
                if (root == null || _group.Contains(root)) continue;
                _group.Add(root);
            }
            _pendingRoots.Clear();
            if (_group.Count == 0)
            {
                SetStatus(_pendingManyLabel + " items did not stream in; selection unchanged", ToneErr);
                _pendingManyLabel = "import";
                return;
            }
            _root = _group[0];
            _has = true;
            _info = null;
            _groupPreviewStarts.Clear();
            _gizmo.RotationEnabled = _group.Count <= MaxGroupMove;
            _gizmo.GroupRotationEnabled = _group.Count > 1 && _group.Count <= MaxGroupMove;
            _deleteArmUntil = 0f;
            _deleteIds = "";
            _deleteConfirmCount = 0;
            try { _id = _root.Identifier; } catch { _id = 0; }
            if (_id != 0) _api.Info(_id, OnInfo);
            SetStatus("selected all " + _group.Count + " " + _pendingManyLabel + " items"
                + (missing > 0 ? " (" + missing + " still loading)" : ""),
                missing == 0 ? ToneOk : ToneErr);
            _pendingManyLabel = "import";
        }

        // ---- outline-all: every entity root near the camera as a wire box ----

        void RefreshOutlines()
        {
            _outlineBoxes.Clear();
            _outlineCount = 0;
            if (Camera == null) return;
            if (Time.unscaledTime >= _outlineScanAt)
            {
                _outlineScanAt = Time.unscaledTime + OutlineScanSeconds;
                int matching;
                _outlineRoots.Clear();
                _outlineRoots.AddRange(NearbyRoots(null, false, out matching, false));
            }
            for (int i = 0; i < _outlineRoots.Count && _outlineCount < OutlineMaxRoots; i++)
            {
                NetworkEntity root = _outlineRoots[i];
                if (root == null || (_has && root == _root)) continue;
                Bounds b;
                if (!TryBounds(root, out b)) continue;
                _outlineBoxes.Add(b);
                _outlineCount++;
            }
        }

        // Client copy of the server scene walk. NearbyRoots also scans colliders
        // because the private entityManager walk can be empty on the client.
        static IEnumerable<NetworkEntity> AllEntities()
        {
            NetworkScene scene = null;
            try { scene = NetworkSceneManager.Current as NetworkScene; } catch { }
            if (scene == null) return new NetworkEntity[0];
            if (_entityManagerField == null)
            {
                try
                {
                    _entityManagerField = typeof(NetworkScene).GetField("entityManager",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch { }
            }
            if (_entityManagerField == null) return new NetworkEntity[0];
            EntityManager em = null;
            try { em = _entityManagerField.GetValue(scene) as EntityManager; } catch { }
            if (em == null) return new NetworkEntity[0];
            return em.IterateEntities();
        }

        // ---- rendering: gizmo (GL, via GizmoRenderHook), highlight box, side panel ----

        // Called from the editor camera's OnPostRender (GizmoRenderHook on _camGO).
        public void DrawGizmo(Camera cam)
        {
            if (_outlines && _outlineBoxes.Count > 0)
                EditGizmo.DrawWireBoxes(cam, _outlineBoxes, new Color(0.25f, 0.9f, 0.95f, 0.55f));
            if (_placing && _placeValid)
            {
                _ghostBox.Clear();
                _ghostBox.Add(new Bounds(_placePos + Vector3.up * 0.35f, Vector3.one * 0.7f));
                EditGizmo.DrawWireBoxes(cam, _ghostBox, new Color(0.5f, 1f, 0.6f, 0.9f));
            }
            if (!_has) return;
            _gizmo.Draw(cam, CurrentPos());
        }

        public void OnGUI()
        {
            if (Camera == null) return;
            GUISkin previousSkin = GUI.skin;
            EnsureEditorSkin();
            GUI.skin = _editorSkin;
            EnsurePixel();
            try
            {
                DrawHighlight();
                DrawPanel();
                DrawSpawnWindow();
                DrawDragLabel();
                DrawPlacement();
                DrawMarquee();
            }
            finally { GUI.skin = previousSkin; }
        }

        void EnsureEditorSkin()
        {
            if (_editorSkin != null && _sourceSkin == GUI.skin) return;
            _sourceSkin = GUI.skin;
            _editorSkin = (GUISkin)UnityEngine.Object.Instantiate(GUI.skin);
            _editorSkin.name = "PrefabEditorSkin";
            _panelTexture = SolidTexture(new Color(0.055f, 0.075f, 0.09f, 0.97f));
            _buttonTexture = SolidTexture(new Color(0.12f, 0.16f, 0.19f, 1f));
            _buttonHoverTexture = SolidTexture(new Color(0.19f, 0.26f, 0.29f, 1f));
            _buttonActiveTexture = SolidTexture(new Color(0.09f, 0.31f, 0.32f, 1f));
            _fieldTexture = SolidTexture(new Color(0.035f, 0.05f, 0.065f, 1f));
            _selectedTexture = SolidTexture(new Color(0.08f, 0.30f, 0.30f, 1f));
            _dangerTexture = SolidTexture(new Color(0.34f, 0.12f, 0.14f, 1f));
            _dangerHoverTexture = SolidTexture(new Color(0.50f, 0.15f, 0.17f, 1f));
            _editorSkin.box = BuildStyle(_editorSkin.box, _panelTexture, new Color(0.91f, 0.93f, 0.94f, 1f), 13, false);
            _editorSkin.button = BuildStyle(_editorSkin.button, _buttonTexture, new Color(0.90f, 0.93f, 0.94f, 1f), 13, true);
            _editorSkin.button.hover.background = _buttonHoverTexture;
            _editorSkin.button.active.background = _buttonActiveTexture;
            _editorSkin.label = BuildStyle(_editorSkin.label, null, new Color(0.88f, 0.91f, 0.93f, 1f), 13, false);
            _editorSkin.textField = BuildStyle(_editorSkin.textField, _fieldTexture, new Color(0.95f, 0.96f, 0.96f, 1f), 13, false);
            _editorSkin.textField.focused.background = _fieldTexture;
            _editorSkin.toggle = BuildStyle(_editorSkin.toggle, _buttonTexture, new Color(0.88f, 0.91f, 0.93f, 1f), 13, false);
            _titleStyle = BuildStyle(_editorSkin.label, null, new Color(0.88f, 0.75f, 0.51f, 1f), 16, true);
            _mutedStyle = BuildStyle(_editorSkin.label, null, new Color(0.60f, 0.67f, 0.71f, 1f), 11, false);
            _tabStyle = BuildStyle(_editorSkin.button, _buttonTexture, new Color(0.67f, 0.74f, 0.77f, 1f), 12, true);
            _selectedTabStyle = BuildStyle(_editorSkin.button, _selectedTexture, new Color(0.30f, 0.89f, 0.84f, 1f), 12, true);
            _dangerButtonStyle = BuildStyle(_editorSkin.button, _dangerTexture, new Color(1f, 0.83f, 0.80f, 1f), 13, true);
            _dangerButtonStyle.hover.background = _dangerHoverTexture;
            _dangerButtonStyle.active.background = _dangerTexture;
            _connectedStyle = BuildStyle(_editorSkin.label, null, new Color(0.35f, 0.88f, 0.57f, 1f), 12, true);
            _panelOnlyStyle = BuildStyle(_editorSkin.label, null, new Color(1f, 0.72f, 0.31f, 1f), 12, true);
            _disconnectedStyle = BuildStyle(_editorSkin.label, null, new Color(1f, 0.48f, 0.42f, 1f), 12, true);
            _toggleOffTexture = ToggleIcon(false);
            _toggleOnTexture = ToggleIcon(true);
            _toggleOffStyle = BuildStyle(_editorSkin.button, _buttonTexture, Color.white, 12, false);
            _toggleOffStyle.hover.background = _buttonHoverTexture;
            _toggleOnStyle = BuildStyle(_editorSkin.button, _selectedTexture, Color.white, 12, false);
            _toggleOnStyle.hover.background = _buttonActiveTexture;
            _toggleLabelStyle = BuildStyle(_editorSkin.label, null, new Color(0.90f, 0.94f, 0.95f, 1f), 12, false);
            _toggleLabelStyle.padding = new RectOffset(0, 0, 0, 0);
            _toggleLabelStyle.alignment = TextAnchor.MiddleLeft;
        }

        static Texture2D ToggleIcon(bool checkedValue)
        {
            Texture2D icon = new Texture2D(20, 20, TextureFormat.RGBA32, false);
            icon.hideFlags = HideFlags.HideAndDontSave;
            icon.filterMode = FilterMode.Point;
            Color border = checkedValue ? new Color(0.36f, 0.91f, 0.85f, 1f)
                : new Color(0.40f, 0.57f, 0.60f, 1f);
            Color fill = new Color(0.035f, 0.08f, 0.10f, 1f);
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 20; x++)
                    icon.SetPixel(x, y, x < 2 || x > 17 || y < 2 || y > 17 ? border : fill);
            if (checkedValue)
            {
                for (int x = 4; x <= 8; x++)
                {
                    int y = 10 - (x - 4);
                    icon.SetPixel(x, y, border);
                    icon.SetPixel(x, y + 1, border);
                }
                for (int x = 8; x <= 15; x++)
                {
                    int y = 6 + (x - 8);
                    icon.SetPixel(x, y, border);
                    icon.SetPixel(x, y + 1, border);
                }
            }
            icon.Apply();
            return icon;
        }

        bool ThemedToggle(bool value, string label)
        {
            Rect row = GUILayoutUtility.GetRect(1f, 32f, GUILayout.ExpandWidth(true));
            bool clicked = GUI.Button(row, "", value ? _toggleOnStyle : _toggleOffStyle);
            Color oldColor = GUI.color;
            if (!GUI.enabled) GUI.color = new Color(oldColor.r, oldColor.g, oldColor.b, oldColor.a * 0.5f);
            GUI.DrawTexture(new Rect(row.x + 8f, row.y + 6f, 20f, 20f),
                value ? _toggleOnTexture : _toggleOffTexture);
            GUI.Label(new Rect(row.x + 38f, row.y, row.width - 46f, row.height), label, _toggleLabelStyle);
            GUI.color = oldColor;
            return clicked ? !value : value;
        }

        static Texture2D SolidTexture(Color color)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.SetPixel(0, 0, color);
            t.Apply();
            return t;
        }

        static GUIStyle BuildStyle(GUIStyle source, Texture2D background, Color text, int size, bool bold)
        {
            GUIStyle style = new GUIStyle(source);
            if (background != null) style.normal.background = background;
            style.normal.textColor = text;
            style.hover.textColor = text;
            style.active.textColor = text;
            style.focused.textColor = text;
            style.fontSize = size;
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            style.wordWrap = true;
            style.padding = new RectOffset(9, 9, 6, 6);
            return style;
        }

        void DrawMarquee()
        {
            if (!_marqueeActive) return;
            float left = Mathf.Min(_marqueeStart.x, _marqueeCurrent.x);
            float top = Screen.height - Mathf.Max(_marqueeStart.y, _marqueeCurrent.y);
            Rect rect = new Rect(left, top, Mathf.Abs(_marqueeCurrent.x - _marqueeStart.x),
                Mathf.Abs(_marqueeCurrent.y - _marqueeStart.y));
            Color previous = GUI.color;
            GUI.color = new Color(0.35f, 0.9f, 1f, 0.28f);
            GUI.Box(rect, "");
            GUI.color = previous;
        }

        void DrawSpawnWindow()
        {
            if (!_spawnOpen)
            {
                _spawnRect = new Rect(0f, 0f, 0f, 0f);
                _previewRect = new Rect(0f, 0f, 0f, 0f);
                _searchFocused = false;
                return;
            }
            float windowY = 136f;
            float windowHeight = Mathf.Min(600f, Mathf.Max(240f, Screen.height - windowY - 12f));
            if (windowY + windowHeight > Screen.height - 8f)
                windowY = Mathf.Max(8f, Screen.height - windowHeight - 8f);
            _spawnRect = new Rect(12f, windowY, 320f, windowHeight);
            GUILayout.BeginArea(_spawnRect, GUI.skin.box);
            GUILayout.Label("Spawn a prefab" + (_placing ? "  -  aiming " + _placeName : ""));

            GUI.SetNextControlName("VelSpawnQ");
            string q = GUILayout.TextField(_spawnQuery);
            if (q != _spawnQuery)
            {
                _spawnQuery = q;
                _spawnSendAt = Time.unscaledTime + 0.35f; // debounce while typing
            }
            _searchFocused = GUI.GetNameOfFocusedControl() == "VelSpawnQ";

            _spawnScroll = GUILayout.BeginScrollView(_spawnScroll, GUILayout.ExpandHeight(true));
            int hoverIndex = -1;
            if (_hitNames.Count == 0)
                GUILayout.Label(_spawnBusy ? (_spawnCatalogLoading ? "loading server spawn list..." : "searching...") : (_spawnQuery.Length == 0
                    ? "type to search the spawnable catalog" : "no matches"));
            for (int i = 0; i < _hitNames.Count; i++)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_hitNames[i] + "  #" + _hitHashes[i])) BeginPlacement(_hitNames[i], _hitHashes[i]);
                Rect nameRect = GUILayoutUtility.GetLastRect();
                if (nameRect.Contains(Event.current.mousePosition)) hoverIndex = i;
                if (GUILayout.Button("Camera", GUILayout.Width(66f))) SpawnAtCamera(_hitNames[i], _hitHashes[i]);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Label("name = aim + click; Camera = spawn 3 m ahead");
            if (GUILayout.Button("Close")) CloseSpawn();
            GUILayout.EndArea();

            _previewRect = new Rect(0f, 0f, 0f, 0f);
            if (hoverIndex >= 0 && Event.current.type == EventType.Repaint)
                DrawPrefabPreview(_hitHashes[hoverIndex], _hitNames[hoverIndex] + "  #" + _hitHashes[hoverIndex]);
        }

        Texture2D LoadPrefabImage(uint hash)
        {
            Texture2D cached;
            if (_prefabImages.TryGetValue(hash, out cached)) return cached;
            if (_missingPrefabImages.Contains(hash)) return null;

            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string imagePath = Path.Combine(Path.Combine(Path.GetDirectoryName(assemblyPath), "PrefabEditorImages"), hash.ToString(CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(imagePath))
                {
                    _missingPrefabImages.Add(hash);
                    return null;
                }
                byte[] bytes = File.ReadAllBytes(imagePath);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.hideFlags = HideFlags.HideAndDontSave;
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    _missingPrefabImages.Add(hash);
                    return null;
                }
                _prefabImages.Add(hash, texture);
                return texture;
            }
            catch (Exception ex)
            {
                _log("[PrefabEditor] Image load failed for " + hash + ": " + ex.Message);
                _missingPrefabImages.Add(hash);
                return null;
            }
        }

        void DrawPrefabPreview(uint hash, string name)
        {
            Texture2D texture = LoadPrefabImage(hash);
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 position = mouse.position.ReadValue();
            float x = Mathf.Clamp(position.x + 18f, 8f, Mathf.Max(8f, Screen.width - 252f));
            float y = Mathf.Clamp(Screen.height - position.y + 18f, 8f, Mathf.Max(8f, Screen.height - 286f));
            _previewRect = new Rect(x, y, 244f, 278f);
            GUI.Box(_previewRect, "");
            GUI.Label(new Rect(x + 8f, y + 6f, 228f, 34f), name, GUI.skin.box);
            if (texture != null)
                GUI.DrawTexture(new Rect(x + 8f, y + 46f, 228f, 224f), texture, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(new Rect(x + 8f, y + 46f, 228f, 224f), "No image available for this prefab", GUI.skin.box);
        }

        // Cursor crosshair + tag while a ghost is being aimed (green = will land
        // on the aimed point, red = no target under the cursor).
        void DrawPlacement()
        {
            if (!_placing) return;
            Mouse m = Mouse.current;
            if (m == null) return;
            Vector2 mp = m.position.ReadValue();
            float x = mp.x, y = Screen.height - mp.y;
            Color col = _placeValid ? new Color(0.5f, 1f, 0.6f, 0.95f) : new Color(1f, 0.4f, 0.4f, 0.95f);
            DrawRect(x - 9f, y - 1f, 18f, 2f, col);
            DrawRect(x - 1f, y - 9f, 2f, 18f, col);
            GUI.Label(new Rect(x + 14f, y + 10f, 360f, 24f),
                "place " + _placeName + "  (Shift = keep placing)", GUI.skin.box);
        }

        void DrawHighlight()
        {
            if (!_has) return;
            for (int i = 0; i < _group.Count; i++) DrawHighlightRoot(_group[i], i == 0);
        }

        void DrawHighlightRoot(NetworkEntity root, bool primary)
        {
            if (root == null) return;
            Bounds b;
            if (!TryBounds(root, out b)) return;

            Vector3 c = b.center, e = b.extents;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int shown = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 sp = Camera.WorldToScreenPoint(corner);
                if (sp.z <= 0f) continue;
                float gx = sp.x, gy = Screen.height - sp.y;
                if (gx < minX) minX = gx; if (gx > maxX) maxX = gx;
                if (gy < minY) minY = gy; if (gy > maxY) maxY = gy;
                shown++;
            }
            if (shown < 2) return;

            bool armed = Time.unscaledTime < _deleteArmUntil;
            Color col = armed ? new Color(1f, 0.35f, 0.35f, 0.95f) :
                (primary ? new Color(1f, 0.85f, 0.2f, 0.95f) : new Color(0.25f, 0.9f, 1f, 0.95f));
            DrawBox(minX, minY, maxX - minX, maxY - minY, col, 2f);
        }

        // Live delta readout that follows the cursor while a handle is held.
        void DrawDragLabel()
        {
            if (!_gizmo.Dragging || _gizmo.DragLabel.Length == 0) return;
            Mouse m = Mouse.current;
            if (m == null) return;
            Vector2 mp = m.position.ReadValue();
            GUI.Label(new Rect(mp.x + 16f, Screen.height - mp.y + 12f, 230f, 24f),
                _gizmo.DragLabel, GUI.skin.box);
        }

        void DrawPanel()
        {
            float pw = Mathf.Min(440f, Mathf.Max(340f, Screen.width - 24f));
            float ph = Mathf.Min(760f, Mathf.Max(300f, Screen.height - 24f));
            _panelRect = new Rect(Screen.width - pw - 12f, 12f, pw, ph);
            GUILayout.BeginArea(_panelRect, GUI.skin.box);

            bool panelOnly = _connected && _consoleStatusKnown && !_consoleReachable;
            bool showConnectionMessage = (!_connected || panelOnly) && !_checkingConnection
                && !string.IsNullOrEmpty(_connectionMessage);
            float contentHeight = ph - 20f;
            float headerHeight = 128f + (showConnectionMessage ? 28f : 0f);
            float footerHeight = 116f;
            float bodyHeight = Mathf.Max(0f, contentHeight - headerHeight - footerHeight - 8f);

            _positionFocused = _rotationFocused = _stepFocused = false;
            _arrangeSpacingFocused = _duplicateOffsetFocused = false;
            _resizePercentFocused = false;
            _importPathFocused = false;
            _workAreaNameFocused = false;

            GUILayout.BeginVertical();
            GUILayout.BeginVertical(GUILayout.Height(headerHeight));

            GUILayout.BeginHorizontal();
            GUILayout.Label("ATT PREFAB EDITOR", _titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_checkingConnection ? "● CHECKING"
                : (_connected ? (panelOnly ? "● PANEL ONLY" : "● CONNECTED") : "● OFFLINE"),
                !_connected ? _disconnectedStyle : (panelOnly ? _panelOnlyStyle : _connectedStyle),
                GUILayout.Width(105f));
            if (GUILayout.Button("Reconnect", GUILayout.Width(82f))) ProbeConnection(true);
            GUILayout.EndHorizontal();
            GUILayout.Label(_api.BaseUrl, _mutedStyle, GUILayout.Height(22f));
            if (showConnectionMessage)
            {
                string connectionMessage = _connectionMessage;
                if (connectionMessage.Length > 56) connectionMessage = connectionMessage.Substring(0, 53) + "...";
                GUILayout.Label(connectionMessage, _mutedStyle, GUILayout.Height(26f));
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_panelTab == 0 ? "SELECT" : "Select", _panelTab == 0 ? _selectedTabStyle : _tabStyle,
                GUILayout.Height(28f))) _panelTab = 0;
            if (GUILayout.Button(_panelTab == 1 ? "ARRANGE" : "Arrange", _panelTab == 1 ? _selectedTabStyle : _tabStyle,
                GUILayout.Height(28f))) _panelTab = 1;
            if (GUILayout.Button(_panelTab == 2 ? "BUILD" : "Build", _panelTab == 2 ? _selectedTabStyle : _tabStyle,
                GUILayout.Height(28f))) _panelTab = 2;
            if (GUILayout.Button(_panelTab == 3 ? "WORLD" : "World", _panelTab == 3 ? _selectedTabStyle : _tabStyle,
                GUILayout.Height(28f))) _panelTab = 3;
            GUILayout.EndHorizontal();
            if (_panelTab != _lastPanelTab)
            {
                _panelScroll = Vector2.zero;
                _lastPanelTab = _panelTab;
                if (_panelTab == 3) RefreshPlayers();
            }
            GUILayout.Space(3f);
            string selectionSummary;
            if (!_has) selectionSummary = "No selection  ·  click or drag objects in the world";
            else
            {
                string prefabName = _info != null ? _info.Prefab : "Loading selection";
                if (prefabName.Length > 25) prefabName = prefabName.Substring(0, 22) + "...";
                selectionSummary = _group.Count + " selected  ·  " + prefabName + "  #" + _id;
            }
            GUILayout.Label(selectionSummary, GUI.skin.box, GUILayout.Height(26f));
            GUILayout.EndVertical();

            _panelScroll = GUILayout.BeginScrollView(_panelScroll,
                GUILayout.Width(pw - 16f), GUILayout.Height(bodyHeight));
            if (_panelTab == 0) DrawSelectionTab();
            else if (_panelTab == 1) DrawArrangeTab();
            else if (_panelTab == 2) DrawBuildTab();
            else DrawWorldTab();
            GUILayout.EndScrollView();

            GUILayout.BeginVertical(GUILayout.Height(footerHeight));
            GUILayout.Space(3f);
            bool armed = Time.unscaledTime < _deleteArmUntil && !string.IsNullOrEmpty(_deleteIds);
            GUI.enabled = _has && !_busy;
            if (GUILayout.Button(armed ? "CONFIRM DELETE  " + _deleteConfirmCount + " ITEMS" :
                (_has ? "DELETE  " + _group.Count + " SELECTED ITEM" + (_group.Count == 1 ? "" : "S") : "DELETE  No selection"),
                armed ? _dangerButtonStyle : GUI.skin.button, GUILayout.Height(32f))) DeleteClick();
            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy && _undoCount > 0;
            if (GUILayout.Button("Undo  Ctrl+Z" + (_undoCount > 0 ? "  (" + _undoCount + ")" : ""))) RunHistory(true);
            GUI.enabled = !_busy && _redoCount > 0;
            if (GUILayout.Button("Redo  Ctrl+Y" + (_redoCount > 0 ? "  (" + _redoCount + ")" : ""))) RunHistory(false);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Ctrl+D duplicate  ·  Delete confirms  ·  Shift-click or drag selects", _mutedStyle,
                GUILayout.Height(24f));
            string footerStatus = _gizmo.Dragging ? _gizmo.DragLabel : _status;
            if (footerStatus != null && footerStatus.Length > 64)
                footerStatus = footerStatus.Substring(0, 61) + "...";
            DrawStatusLine(footerStatus);
            GUILayout.EndVertical();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        void DrawSelectionTab()
        {
            if (!_has)
            {
                GUILayout.Label("Select objects in the world", _titleStyle);
                GUILayout.Label("Click one item, or drag a box. Hold Shift to add or remove items.", _mutedStyle);
            }
            else
            {
                GUILayout.Label(_info != null ? _info.Prefab : "Loading selection...", _titleStyle);
                GUILayout.Label("#" + _id + "    " + _group.Count + " selected", _mutedStyle);
                if (_info != null)
                    GUILayout.Label("Hash " + _info.Hash + "   " +
                        (_info.Flags.Length > 0 ? string.Join(", ", _info.Flags) : "no flags"), _mutedStyle);
            }

            GUILayout.Label("Choose what Arrange will edit. Nearby searches use loaded items within "
                + OutlineRadius.ToString("0") + " m of the camera.", _mutedStyle);

            GUILayout.BeginHorizontal();
            GUI.enabled = _has && !_busy;
            if (GUILayout.Button("Same type nearby")) SelectSameType();
            if (GUILayout.Button("Invert nearby")) InvertSelection();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy;
            if (GUILayout.Button("Select nearby")) SelectNearby();
            GUI.enabled = _has && !_busy;
            if (GUILayout.Button("Deselect all")) Clear();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_has) DrawSelectedItems();

            if (_cands.Count > 1)
            {
                GUILayout.Space(4f);
                GUILayout.Label("Objects under cursor  •  Tab cycles", _mutedStyle);
                for (int i = 0; i < _cands.Count && i < 6; i++)
                {
                    NetworkEntity c = _cands[i];
                    string nm = (c == null) ? "(gone)" : NiceName(c);
                    uint cid = 0;
                    try { if (c != null) cid = c.Identifier; } catch { }
                    if (GUILayout.Button((i == _candAt ? "●  " : "   ") + nm + (cid != 0 ? "   #" + cid : "")))
                        if (i != _candAt) SelectCandidate(i);
                }
            }
        }

        void DrawSelectedItems()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Selected items", _titleStyle);
            GUILayout.Label("Click a row to make it primary; × removes it from the selection.", _mutedStyle);
            int shown = _showAllSelected ? _group.Count : Mathf.Min(_group.Count, 8);
            for (int i = 0; i < shown; i++)
            {
                NetworkEntity item = _group[i];
                uint id = 0;
                try { if (item != null) id = item.Identifier; } catch { }
                string name = item == null ? "(gone)" : PrefabName(item);
                if (string.IsNullOrEmpty(name)) name = "Item";
                if (name.Length > 27) name = name.Substring(0, 24) + "...";
                GUILayout.BeginHorizontal();
                GUI.enabled = !_busy;
                bool makePrimary = GUILayout.Button((i == 0 ? "●  " : "    ") + name + (id == 0 ? "" : "  #" + id),
                    i == 0 ? _selectedTabStyle : GUI.skin.button, GUILayout.Height(30f));
                bool remove = GUILayout.Button("×", GUILayout.Width(30f), GUILayout.Height(30f));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (remove) { RemoveSelectedItem(item); return; }
                if (makePrimary && i != 0) { MakePrimarySelection(item); return; }
            }
            if (_group.Count > 8 && GUILayout.Button(_showAllSelected ? "Show fewer items" : "Show all " + _group.Count + " items"))
                _showAllSelected = !_showAllSelected;
            if (_busy) GUILayout.Label("Finish the current edit before changing the selection.", _mutedStyle);
        }

        bool DrawArrangeSection(string label, ref bool open)
        {
            if (GUILayout.Button((open ? "▼  " : "▶  ") + label,
                open ? _selectedTabStyle : _tabStyle, GUILayout.Height(30f))) open = !open;
            if (open) GUILayout.Space(4f);
            return open;
        }

        void DrawArrangeTab()
        {
            if (!_has)
            {
                GUILayout.Label("Select an object to move, rotate, scale, or duplicate it.", _mutedStyle);
                return;
            }
            bool single = _group.Count == 1;
            GUILayout.Label(single ? "Arrange item" : "Arrange " + _group.Count + " items", _titleStyle);

            if (DrawArrangeSection("Move", ref _moveSectionOpen))
            {
                GUI.enabled = !_busy;
                _surfaceSnap = ThemedToggle(_surfaceSnap, "Surface snap while dragging");
                GUI.enabled = true;
                GUILayout.Label("Snap the moved object's face to a nearby surface (within 35 cm).", _mutedStyle);
                if (single)
                {
                    GUI.enabled = !_busy;
                    DrawVectorRow("Position", "Pos", ref _positionX, ref _positionY, ref _positionZ);
                    _positionFocused = GUI.GetNameOfFocusedControl().StartsWith("Pos", StringComparison.Ordinal);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Apply position")) ApplyPositionFields();
                    if (GUILayout.Button("Align to ground")) Ground();
                    GUILayout.EndHorizontal();
                    if (GUILayout.Button(_resetPositionArmed && Time.unscaledTime < _resetPositionUntil
                        ? "Confirm world origin" : "Reset position")) ResetPosition();
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("Drag the gizmo to move the group. Group drags are one undo step.", _mutedStyle);
                    GUI.enabled = !_busy;
                    if (GUILayout.Button("Ground primary")) Ground();
                    GUI.enabled = true;
                }
            }
            GUILayout.Space(6f);

            if (DrawArrangeSection("Rotate", ref _rotateSectionOpen))
            {
                if (!single)
                {
                    GUILayout.Label("Rotate all items around the primary item. One undo step; up to 100 items.", _mutedStyle);
                    GUI.enabled = !_busy && _group.Count <= MaxGroupMove;
                    DrawVectorRow("Add degrees", "GroupRot", ref _groupRotationX, ref _groupRotationY, ref _groupRotationZ);
                    _rotationFocused = GUI.GetNameOfFocusedControl().StartsWith("GroupRot", StringComparison.Ordinal);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Rotate group")) ApplyGroupRotationFields();
                    if (GUILayout.Button("Clear angles"))
                        _groupRotationX = _groupRotationY = _groupRotationZ = "0";
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Yaw step", _mutedStyle, GUILayout.Width(70f));
                    GUI.SetNextControlName("RotationStepField");
                    _rotationStep = GUILayout.TextField(_rotationStep, GUILayout.Width(68f));
                    _stepFocused = GUI.GetNameOfFocusedControl() == "RotationStepField";
                    if (GUILayout.Button("- step")) StepGroupYaw(-1f);
                    if (GUILayout.Button("+ step")) StepGroupYaw(1f);
                    GUILayout.EndHorizontal();
                    GUI.enabled = true;
                }
                else
                {
                    GUI.enabled = !_busy;
                    DrawVectorRow("Rotation", "Rot", ref _rotationX, ref _rotationY, ref _rotationZ);
                    _rotationFocused = GUI.GetNameOfFocusedControl().StartsWith("Rot", StringComparison.Ordinal);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Apply rotation")) ApplyRotationFields();
                    if (GUILayout.Button("Reset rotation")) ResetRotation();
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Snap increment", _mutedStyle);
                    GUILayout.BeginHorizontal();
                    GUI.SetNextControlName("RotationStepField");
                    _rotationStep = GUILayout.TextField(_rotationStep, GUILayout.Width(68f));
                    _stepFocused = GUI.GetNameOfFocusedControl() == "RotationStepField";
                    GUILayout.Label("degrees", _mutedStyle, GUILayout.Width(58f));
                    if (GUILayout.Button("- step")) StepYaw(-1f);
                    if (GUILayout.Button("+ step")) StepYaw(1f);
                    if (GUILayout.Button("Snap")) SnapRotation();
                    GUILayout.EndHorizontal();
                    GUI.enabled = true;
                }
            }
            GUILayout.Space(6f);

            if (DrawArrangeSection("Scale", ref _scaleSectionOpen))
            {
                if (single) DrawResizeControls();
                else GUILayout.Label("Select one item to change its size.", _mutedStyle);
            }
            GUILayout.Space(6f);

            if (DrawArrangeSection("Group & duplicate", ref _groupSectionOpen))
            {
                if (!single)
                {
                    GUILayout.Label("Align to the primary item's coordinate", _mutedStyle);
                    GUILayout.BeginHorizontal();
                    GUI.enabled = !_busy;
                    if (GUILayout.Button("Align X")) ArrangeSelection("x", "align");
                    if (GUILayout.Button("Align Y")) ArrangeSelection("y", "align");
                    if (GUILayout.Button("Align Z")) ArrangeSelection("z", "align");
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Space from the lowest position using this step", _mutedStyle);
                    GUILayout.BeginHorizontal();
                    GUI.enabled = !_busy;
                    GUILayout.Label("Step m", GUILayout.Width(48f));
                    GUI.SetNextControlName("ArrangeSpacingField");
                    _arrangeSpacingText = GUILayout.TextField(_arrangeSpacingText, GUILayout.Width(70f));
                    _arrangeSpacingFocused = GUI.GetNameOfFocusedControl() == "ArrangeSpacingField";
                    float spacing;
                    bool validSpacing = TryArrangeSpacing(out spacing);
                    GUI.enabled = !_busy && validSpacing;
                    if (GUILayout.Button("Space X")) ArrangeSelection("x", "space");
                    if (GUILayout.Button("Space Y")) ArrangeSelection("y", "space");
                    if (GUILayout.Button("Space Z")) ArrangeSelection("z", "space");
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    if (!validSpacing) GUILayout.Label("Enter a valid spacing amount in meters.", _mutedStyle);
                }
                else GUILayout.Label("Select two or more items to align or space them.", _mutedStyle);

                GUILayout.Space(6f);
                GUILayout.Label("Primary transform clipboard", _mutedStyle);
                GUILayout.BeginHorizontal();
                GUI.enabled = !_busy && _info != null;
                if (GUILayout.Button("Copy primary")) CopyTransform();
                GUI.enabled = !_busy && _hasTransformClipboard;
                if (GUILayout.Button("Paste to primary")) PasteTransform();
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (_info == null) GUILayout.Label("Loading primary item details before copying.", _mutedStyle);
                else if (!_hasTransformClipboard) GUILayout.Label("Copy a transform before pasting it.", _mutedStyle);
                DrawDuplicateOffsetControls();
            }
        }

        void DrawDuplicateOffsetControls()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Duplicate selected items", _titleStyle);
            GUILayout.Label("Offset moves every copy by the same X / Y / Z amount.", _mutedStyle);
            GUI.enabled = !_busy;
            DrawVectorRow("Offset", "Dup", ref _duplicateOffsetX, ref _duplicateOffsetY, ref _duplicateOffsetZ);
            _duplicateOffsetFocused = GUI.GetNameOfFocusedControl().StartsWith("Dup", StringComparison.Ordinal);
            Vector3 offset;
            bool validOffset = TryDuplicateOffset(out offset);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Duplicate in place")) DuplicateSelection();
            GUI.enabled = !_busy && validOffset;
            if (GUILayout.Button("Duplicate with offset")) DuplicateSelectionByOffset(offset);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!validOffset) GUILayout.Label("Enter valid X, Y, and Z offsets to duplicate with an offset.", _mutedStyle);
        }

        void DrawVectorRow(string label, string prefix, ref string x, ref string y, ref string z)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(64f));
            GUILayout.Label("X", _mutedStyle, GUILayout.Width(14f));
            GUI.SetNextControlName(prefix + "XField");
            x = GUILayout.TextField(x, GUILayout.Width(70f));
            GUILayout.Label("Y", _mutedStyle, GUILayout.Width(14f));
            GUI.SetNextControlName(prefix + "YField");
            y = GUILayout.TextField(y, GUILayout.Width(70f));
            GUILayout.Label("Z", _mutedStyle, GUILayout.Width(14f));
            GUI.SetNextControlName(prefix + "ZField");
            z = GUILayout.TextField(z, GUILayout.Width(70f));
            GUILayout.EndHorizontal();
        }

        void DrawBuildTab()
        {
            GUILayout.Label("Spawn & prefab", _titleStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_spawnOpen ? "Close spawn" : "Spawn prefab")) ToggleSpawn();
            if (GUILayout.Button(PasteLabel())) Paste();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = _has && !_busy;
            if (GUILayout.Button("Copy primary save string")) CopyString();
            GUI.enabled = _has && _group.Count == 1 && !_busy;
            if (GUILayout.Button(ReplaceLabel())) ReplaceSelected();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!_has) GUILayout.Label("Select an item to copy its save string or replace it.", _mutedStyle);
            else if (_group.Count > 1) GUILayout.Label("Select one item to replace it with a save string.", _mutedStyle);
            GUILayout.Space(8f);
            GUILayout.Label("Saved layouts", _titleStyle);
            GUI.enabled = _has && !_busy;
            if (GUILayout.Button("Save selected items as layout...")) SaveSelectedLayout();
            GUI.enabled = true;
            if (!_has) GUILayout.Label("Select items to enable layout saving.", _mutedStyle);
            GUILayout.Label("A layout stores each prefab's save string, position, and rotation.", _mutedStyle);
            GUILayout.Label("Load a saved layout here; import can keep its positions or place it ahead of the camera.", _mutedStyle);
            DrawImportControls();
        }

        void DrawWorldTab()
        {
            GUILayout.Label("Player travel", _titleStyle);
            if (_travel != null) DrawActiveTravel();
            else
            {
                GUILayout.Label("No active trip. Save an area, then choose a player to visit it.", _mutedStyle);
                if (!string.IsNullOrEmpty(_workMessage)) GUILayout.Label(_workMessage, _mutedStyle);
            }
            GUILayout.Space(8f);

            GUILayout.Label("Work areas", _titleStyle);
            DrawWorkAreaControls();
            GUILayout.Space(8f);

            GUILayout.Label("View", _titleStyle);
            OutlineToggle();
            GUILayout.Space(8f);
            GUILayout.Label("Editor camera", _titleStyle);
            GUILayout.Label("WASD move   Space up   Ctrl down   Shift fast\nRight mouse look   wheel changes speed\nF2 desktop mirror   F3 display   F1 close", _mutedStyle);
        }

        void DrawActiveTravel()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(_travel.Returned ? _travel.Player + " returned · restore their saved home"
                : _travel.Player + " · trip active", _connectedStyle);
            if (_travel.HasWorkPosition)
                GUILayout.Label("Destination  " + VecText(_travel.WorkPosition), _mutedStyle);
            GUILayout.Label("Saved return point  " + VecText(_travel.ReturnPosition), _mutedStyle);
            GUI.enabled = !_workBusy;
            if (GUILayout.Button(_travel.Returned ? "Retry home restore" : "Return " + _travel.Player,
                GUILayout.Height(34f))) ReturnFromWorkArea();
            GUI.enabled = true;
            if (_workBusy) GUILayout.Label("Travel command in progress.", _mutedStyle);
            if (!_travel.Returned && _travel.HasWorkPosition)
            {
                GUILayout.Space(6f);
                GUILayout.Label(_keepAlive ? "KEEP PLAYER ACTIVE · ON" : "KEEP PLAYER ACTIVE · OFF",
                    _keepAlive ? _connectedStyle : _panelOnlyStyle);
                GUI.enabled = !_workBusy;
                bool keepAlive = ThemedToggle(_keepAlive, "Keep " + _travel.Player + " active every 4 minutes");
                if (keepAlive != _keepAlive)
                {
                    _keepAlive = keepAlive;
                    _keepAliveNextAt = Time.unscaledTime + KeepAliveInterval;
                    _workMessage = keepAlive
                        ? "Keep player active is on. Leave the editor camera open."
                        : "Keep player active is off.";
                    if (_log != null) _log("work-area keep-alive " + (keepAlive ? "enabled" : "disabled"));
                }
                GUI.enabled = true;
                if (_keepAlive)
                {
                    int left = Mathf.Max(0, Mathf.CeilToInt(_keepAliveNextAt - Time.unscaledTime));
                    GUILayout.Label("Next activity nudge in " + (left / 60) + ":" + (left % 60).ToString("00"), _connectedStyle);
                }
                GUILayout.Label("Moves the avatar 12 cm to avoid idle disconnect. Turn it off while actively moving or standing near hazards.", _mutedStyle);
            }
            if (!string.IsNullOrEmpty(_workMessage)) GUILayout.Label(_workMessage, _mutedStyle);
            GUILayout.EndVertical();
        }

        void DrawWorkAreaControls()
        {
            GUILayout.Label("Save the camera here, or jump it to a saved area.", _mutedStyle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("WorkAreaName");
            _workAreaName = GUILayout.TextField(_workAreaName);
            _workAreaNameFocused = GUI.GetNameOfFocusedControl() == "WorkAreaName";
            if (GUILayout.Button("Save camera", GUILayout.Width(90f))) SaveWorkArea();
            GUILayout.EndHorizontal();
            if (_workAreas.Count == 0) GUILayout.Label("No saved work areas yet.", _mutedStyle);
            for (int i = 0; i < _workAreas.Count; i++)
            {
                WorkArea a = _workAreas[i];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((_selectedArea == i ? "● " : "") + a.Name + "  " + VecText(a.Position)))
                    _selectedArea = i;
                if (GUILayout.Button("Camera", GUILayout.Width(62f)))
                {
                    _selectedArea = i;
                    if (Camera != null) Camera.transform.position = a.Position;
                }
                if (GUILayout.Button("×", GUILayout.Width(30f))) RemoveWorkArea(i);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8f);
            GUILayout.Label("Choose an online player", _titleStyle);
            GUI.enabled = !_playersLoading;
            if (GUILayout.Button(_playersLoading ? "Refreshing players..." : "Refresh players")) RefreshPlayers();
            GUI.enabled = true;
            if (_onlinePlayers.Count == 0 && !_playersLoading)
                GUILayout.Label("No online players loaded. Refresh to try again.", _mutedStyle);
            for (int i = 0; i < _onlinePlayers.Count; i++)
            {
                OnlinePlayer p = _onlinePlayers[i];
                if (GUILayout.Button((_selectedPlayer == p.Name ? "● " : "○ ") + p.Name + "  " + VecText(p.Position)))
                    _selectedPlayer = p.Name;
            }
            if (_travel != null)
                GUILayout.Label("Return the active player above before starting another trip.", _mutedStyle);
            else
            {
                bool valid = _selectedArea >= 0 && _selectedArea < _workAreas.Count && !string.IsNullOrEmpty(_selectedPlayer);
                string areaName = _selectedArea >= 0 && _selectedArea < _workAreas.Count ? _workAreas[_selectedArea].Name : "";
                GUILayout.Label("Destination: " + (areaName.Length > 0 ? areaName : "Choose a work area")
                    + "  ·  Player: " + (string.IsNullOrEmpty(_selectedPlayer) ? "Choose a player" : _selectedPlayer), _mutedStyle);
                GUI.enabled = valid && !_workBusy;
                bool armed = Time.unscaledTime < _travelArmUntil && _travelArmedPlayer == _selectedPlayer && _travelArmedArea == areaName;
                if (GUILayout.Button(armed
                    ? "Confirm teleport " + _selectedPlayer : "Teleport " + _selectedPlayer + " to selected area", _dangerButtonStyle))
                    VisitWorkArea();
                GUI.enabled = true;
                if (!valid) GUILayout.Label("Choose a saved work area and an online player to enable teleport.", _mutedStyle);
                else if (_workBusy) GUILayout.Label("Travel command in progress.", _mutedStyle);
                GUILayout.Label("Teleport moves the selected player's avatar. Their original position and home are saved for Return.", _mutedStyle);
            }
        }

        float _travelArmUntil;

        void SaveWorkArea()
        {
            string name = (_workAreaName ?? "").Trim();
            if (Camera == null || name.Length == 0) { _workMessage = "Enter a name first."; return; }
            name = name.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
            for (int i = 0; i < _workAreas.Count; i++)
                if (string.Equals(_workAreas[i].Name, name, StringComparison.OrdinalIgnoreCase))
                { _workAreas[i].Position = Camera.transform.position; _selectedArea = i; SaveWorkAreas(); _workMessage = "Updated " + name; return; }
            _workAreas.Add(new WorkArea { Name = name, Position = Camera.transform.position });
            _selectedArea = _workAreas.Count - 1;
            SaveWorkAreas(); _workMessage = "Saved " + name;
        }

        void RemoveWorkArea(int at)
        {
            if (at < 0 || at >= _workAreas.Count) return;
            string name = _workAreas[at].Name; _workAreas.RemoveAt(at);
            if (_selectedArea >= _workAreas.Count) _selectedArea = _workAreas.Count - 1;
            SaveWorkAreas(); _workMessage = "Removed " + name;
        }

        void RefreshPlayers()
        {
            if (_playersLoading) return;
            _playersLoading = true;
            _api.Players(delegate(EditResult r)
            {
                _playersLoading = false;
                _onlinePlayers.Clear();
                string data = Json.Value(r.Raw, "data");
                string players = data == null ? null : Json.Value(data, "players");
                if (r.Ok && players != null)
                {
                    List<string> rows = Json.Objects(players);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        string n = Json.Str(rows[i], "name");
                        if (string.IsNullOrEmpty(n)) continue;
                        _onlinePlayers.Add(new OnlinePlayer { Name = n,
                            Position = new Vector3(Json.Num(rows[i], "x", 0f), Json.Num(rows[i], "y", 0f), Json.Num(rows[i], "z", 0f)) });
                    }
                }
                if (_onlinePlayers.Count == 0) _workMessage = r.Ok ? "No online players found." : "Player refresh failed: " + Reason(r);
                else
                {
                    bool found = false;
                    for (int i = 0; i < _onlinePlayers.Count; i++) if (_onlinePlayers[i].Name == _selectedPlayer) found = true;
                    if (!found) _selectedPlayer = _onlinePlayers[0].Name;
                    _workMessage = "Choose an online player.";
                }
            });
        }

        void TickKeepAlive()
        {
            if (!_keepAlive || _travel == null || _travel.Returned || !_travel.HasWorkPosition
                || _workBusy || Time.unscaledTime < _keepAliveNextAt) return;
            _workBusy = true;
            _workMessage = "Checking " + _travel.Player + " before the keep-alive nudge...";
            string playerName = _travel.Player;
            _api.Players(delegate(EditResult list)
            {
                OnlinePlayer player = FindPlayer(list, playerName);
                if (player == null)
                {
                    _workBusy = false;
                    _keepAlive = false;
                    _workMessage = list.Ok ? "Keep-alive stopped: " + playerName + " is no longer online."
                        : "Keep-alive stopped: player check failed (" + Reason(list) + ").";
                    if (_log != null) _log("work-area keep-alive stopped: player unavailable");
                    return;
                }

                Vector3 offset = new Vector3(_keepAlivePositive ? KeepAliveNudge : -KeepAliveNudge, 0f, 0f);
                Vector3 nudgePosition = player.Position + offset;
                _workMessage = "Moving " + playerName + " a few centimeters...";
                _api.Command("player set-home \"" + playerName + "\" \"" + VecText(nudgePosition) + "\"", delegate(EditResult set)
                {
                    if (!set.Ok)
                    {
                        _workBusy = false; _keepAlive = false;
                        _workMessage = "Keep-alive stopped: could not set temporary nudge point (" + Reason(set) + ").";
                        if (_log != null) _log("work-area keep-alive stopped: temporary home command failed");
                        return;
                    }
                    _api.Command("player teleport \"" + playerName + "\" Home", delegate(EditResult tp)
                    {
                        // Restore the work-area home even if the teleport itself failed.
                        _api.Command("player set-home \"" + playerName + "\" \"" + VecText(_travel.WorkPosition) + "\"", delegate(EditResult restore)
                        {
                            _workBusy = false;
                            if (!restore.Ok)
                            {
                                _keepAlive = false;
                                _workMessage = "Keep-alive stopped: could not restore work-area home. Use Return to restore the original home. " + Reason(restore);
                                if (_log != null) _log("work-area keep-alive failed to restore the work-area home");
                                return;
                            }
                            if (!tp.Ok)
                            {
                                _keepAlive = false;
                                _workMessage = "Keep-alive stopped: nudge teleport failed (" + Reason(tp) + ").";
                                if (_log != null) _log("work-area keep-alive stopped: nudge teleport failed");
                                return;
                            }
                            _keepAlivePositive = !_keepAlivePositive;
                            _keepAliveNextAt = Time.unscaledTime + KeepAliveInterval;
                            _workMessage = playerName + " nudged; work-area home restored. Next check in 4 minutes.";
                            if (_log != null) _log("work-area keep-alive nudge completed; work-area home restored");
                        });
                    });
                });
            });
        }

        static OnlinePlayer FindPlayer(EditResult result, string name)
        {
            if (result == null || !result.Ok) return null;
            string data = Json.Value(result.Raw, "data");
            string players = data == null ? null : Json.Value(data, "players");
            if (players == null) return null;
            List<string> rows = Json.Objects(players);
            for (int i = 0; i < rows.Count; i++)
            {
                string n = Json.Str(rows[i], "name");
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return new OnlinePlayer { Name = n, Position = new Vector3(
                        Json.Num(rows[i], "x", 0f), Json.Num(rows[i], "y", 0f), Json.Num(rows[i], "z", 0f)) };
            }
            return null;
        }

        void VisitWorkArea()
        {
            if (_workBusy || _selectedArea < 0 || _selectedArea >= _workAreas.Count || string.IsNullOrEmpty(_selectedPlayer)) return;
            string selectedAreaName = _workAreas[_selectedArea].Name;
            bool armed = Time.unscaledTime < _travelArmUntil && _travelArmedPlayer == _selectedPlayer && _travelArmedArea == selectedAreaName;
            if (!armed)
            {
                _travelArmedPlayer = _selectedPlayer; _travelArmedArea = selectedAreaName;
                _travelArmUntil = Time.unscaledTime + 6f;
                _workMessage = "Click confirm within 6 seconds to teleport " + _selectedPlayer + " to " + selectedAreaName + ".";
                return;
            }
            _travelArmUntil = 0f;
            OnlinePlayer target = null;
            for (int i = 0; i < _onlinePlayers.Count; i++) if (_onlinePlayers[i].Name == _selectedPlayer) target = _onlinePlayers[i];
            if (target == null) { _workMessage = "Refresh and select an online player."; return; }
            Vector3 destination = _workAreas[_selectedArea].Position;
            _workBusy = true; _workMessage = "Reading " + target.Name + "'s saved home...";
            _api.Command("player get-home \"" + target.Name + "\"", delegate(EditResult r)
            {
                Vector3 home;
                if (!r.Ok) { _workBusy = false; _workMessage = "Could not read saved home: " + Reason(r); return; }
                if (!TryCommandVector(r.Raw, out home))
                { _workBusy = false; _workMessage = "Server returned an unreadable home position. Teleport canceled."; return; }
                _keepAlive = false;
                _travel = new TravelState { Player = target.Name, ReturnPosition = target.Position,
                    OriginalHome = home, WorkPosition = destination, HasWorkPosition = true, Returned = false };
                SaveTravel();
                SetTravelHomeAndTeleport(destination);
            });
        }

        void SetTravelHomeAndTeleport(Vector3 destination)
        {
            string player = _travel.Player;
            _workMessage = "Setting temporary return point for " + player + "...";
            _api.Command("player set-home \"" + player + "\" \"" + VecText(destination) + "\"", delegate(EditResult set)
            {
                if (!set.Ok) { _workBusy = false; _workMessage = "Could not set temporary home: " + Reason(set); _travel = null; SaveTravel(); return; }
                _workMessage = "Teleporting " + player + "...";
                _api.Command("player teleport \"" + player + "\" Home", delegate(EditResult tp)
                {
                    _workBusy = false;
                    if (!tp.Ok) { _workMessage = "Teleport failed: " + Reason(tp) + ". Return is available to restore position and home."; return; }
                    _workMessage = "At work area. Return " + player + " when finished.";
                    SaveTravel();
                });
            });
        }

        void ReturnFromWorkArea()
        {
            if (_workBusy || _travel == null) return;
            _keepAlive = false;
            _workBusy = true;
            if (_travel.Returned) { RestoreOriginalHome(); return; }
            string p = _travel.Player;
            _workMessage = "Restoring " + p + "'s return point...";
            _api.Command("player set-home \"" + p + "\" \"" + VecText(_travel.ReturnPosition) + "\"", delegate(EditResult set)
            {
                if (!set.Ok) { _workBusy = false; _workMessage = "Return point setup failed: " + Reason(set); return; }
                _api.Command("player teleport \"" + p + "\" Home", delegate(EditResult tp)
                {
                    if (!tp.Ok) { _workBusy = false; _workMessage = "Return teleport failed: " + Reason(tp); return; }
                    _travel.Returned = true; SaveTravel();
                    RestoreOriginalHome();
                });
            });
        }

        void RestoreOriginalHome()
        {
            string p = _travel.Player;
            _workMessage = "Restoring " + p + "'s original home...";
            _api.Command("player set-home \"" + p + "\" \"" + VecText(_travel.OriginalHome) + "\"", delegate(EditResult r)
            {
                _workBusy = false;
                if (!r.Ok) { _workMessage = "Player is back; home restore failed: " + Reason(r) + ". Retry is available."; return; }
                _travel = null; SaveTravel(); _workMessage = p + " returned; original home restored.";
            });
        }

        static string VecText(Vector3 v)
        {
            return v.x.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", CultureInfo.InvariantCulture)
                + "," + v.z.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static bool TryCommandVector(string raw, out Vector3 v)
        {
            v = Vector3.zero;
            string structured = Json.Value(raw, "structured");
            if (structured == null) structured = Json.Value(raw, "data");
            if (structured != null && structured.Length > 1 && structured[0] == '[')
            {
                string[] values = structured.Trim('[', ']').Split(',');
                float x, y, z;
                if (values.Length == 3 && float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                { v = new Vector3(x, y, z); return true; }
            }
            if (structured != null && structured[0] == '"')
            {
                string s = Json.Str("{\"v\":" + structured + "}", "v");
                if (TryVectorText(s, out v)) return true;
            }
            if (structured != null && structured[0] == '{')
            {
                v = new Vector3(Json.Num(structured, "x", float.NaN), Json.Num(structured, "y", float.NaN), Json.Num(structured, "z", float.NaN));
                if (!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)) return true;
            }
            string text = Json.Str(raw, "text");
            return TryVectorText(text, out v);
        }

        static bool TryVectorText(string text, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Trim('(', ')', ' ', '\t', '\r', '\n').Split(',');
            float x, y, z;
            if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        void LoadWorkAreas()
        {
            try
            {
                if (!File.Exists(_areasPath)) return;
                string[] lines = File.ReadAllLines(_areasPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('\t'); float x, y, z;
                    if (p.Length != 4 || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        || !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                    _workAreas.Add(new WorkArea { Name = Encoding.UTF8.GetString(Convert.FromBase64String(p[0])), Position = new Vector3(x, y, z) });
                }
            }
            catch (Exception e) { if (_log != null) _log("could not load work areas: " + e.Message); }
        }

        void SaveWorkAreas()
        {
            try
            {
                List<string> lines = new List<string>();
                for (int i = 0; i < _workAreas.Count; i++)
                    lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(_workAreas[i].Name)) + "\t" + VecText(_workAreas[i].Position).Replace(',', '\t'));
                File.WriteAllLines(_areasPath, lines.ToArray());
            }
            catch (Exception e) { _workMessage = "Could not save work areas: " + e.Message; }
        }

        void LoadTravel()
        {
            try
            {
                if (!File.Exists(_travelPath)) return;
                string[] p = File.ReadAllText(_travelPath).Split('\t');
                Vector3 from, home, work;
                if (p.Length == 12 && p[0] == "v2" && TryVector(p[6], p[7], p[8], out from)
                    && TryVector(p[9], p[10], p[11], out home) && TryVector(p[3], p[4], p[5], out work))
                    _travel = new TravelState { Player = Encoding.UTF8.GetString(Convert.FromBase64String(p[1])),
                        Returned = p[2] == "1", WorkPosition = work, HasWorkPosition = true, ReturnPosition = from, OriginalHome = home };
                else if (p.Length == 8 && TryVector(p[2], p[3], p[4], out from) && TryVector(p[5], p[6], p[7], out home))
                    _travel = new TravelState { Player = Encoding.UTF8.GetString(Convert.FromBase64String(p[0])),
                        Returned = p[1] == "1", ReturnPosition = from, OriginalHome = home };
            }
            catch (Exception e) { if (_log != null) _log("could not load travel record: " + e.Message); }
        }

        void SaveTravel()
        {
            try
            {
                if (_travel == null) { if (File.Exists(_travelPath)) File.Delete(_travelPath); return; }
                File.WriteAllText(_travelPath, "v2\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_travel.Player))
                    + "\t" + (_travel.Returned ? "1" : "0") + "\t" + VecText(_travel.WorkPosition).Replace(',', '\t')
                    + "\t" + VecText(_travel.ReturnPosition).Replace(',', '\t')
                    + "\t" + VecText(_travel.OriginalHome).Replace(',', '\t'));
            }
            catch (Exception e) { _workMessage = "Could not save return record: " + e.Message; }
        }

        static bool TryVector(string x, string y, string z, out Vector3 v)
        {
            float a = 0f, b = 0f, c = 0f;
            bool ok = float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                && float.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out c);
            v = ok ? new Vector3(a, b, c) : Vector3.zero;
            return ok;
        }

        void DrawResizeControls()
        {
            bool enabled = _group.Count == 1 && !_busy;
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Size Δ", GUILayout.Width(54f));
            GUI.enabled = enabled;
            GUI.SetNextControlName("PrefabResizePercent");
            string typed = GUILayout.TextField(_resizePercentText, GUILayout.Width(68f));
            if (typed != _resizePercentText)
            {
                _resizePercentText = typed;
                float parsed;
                if (TryResizePercent(out parsed)) _resizePercent = parsed;
            }
            _resizePercentFocused = GUI.GetNameOfFocusedControl() == "PrefabResizePercent";
            GUILayout.Label("%", GUILayout.Width(14f));
            float percent;
            bool valid = TryResizePercent(out percent);
            GUI.enabled = enabled && valid;
            if (GUILayout.Button("Apply", GUILayout.Width(58f))) ApplyResizePercent();
            GUILayout.EndHorizontal();
            if (!valid) GUILayout.Label("Enter a size change from -50% to +100%.", _mutedStyle);

            GUI.enabled = enabled;
            GUILayout.Label("Drag to preview; release to apply (-50% to +100%).");
            float previous = _resizePercent;
            float changed = GUILayout.HorizontalSlider(_resizePercent, MinResizePercent, MaxResizePercent);
            if (Mathf.Abs(changed - previous) > 0.01f)
            {
                _resizePercent = changed;
                _resizePercentText = changed.ToString("0.0", CultureInfo.InvariantCulture);
                _resizeSliderDragging = true;
                PreviewResize(changed);
            }
            GUI.enabled = true;
        }

        void DrawImportControls()
        {
            GUILayout.Space(4f);
            GUILayout.Label("Import JSON: " + (_importAtCamera ? "3 m ahead; keep layout" : "original positions"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button((_importAtCamera ? "> " : "") + "At camera")) _importAtCamera = true;
            if (GUILayout.Button((!_importAtCamera ? "> " : "") + "Original positions")) _importAtCamera = false;
            GUILayout.EndHorizontal();
            GUI.SetNextControlName("PrefabImportPath");
            string path = GUILayout.TextField(_importPath);
            if (path != _importPath)
            {
                _importPath = path;
                _importEntries.Clear();
            }
            _importPathFocused = GUI.GetNameOfFocusedControl() == "PrefabImportPath";
            GUILayout.BeginHorizontal();
            GUI.enabled = !_importing;
            if (GUILayout.Button("Browse JSON...")) BrowseImportFile();
            if (GUILayout.Button("Read file")) ReadImportFile();
            if (_importEntries != null && _importEntries.Count > 0 &&
                GUILayout.Button(_importing ? "Importing..." : "Import " + _importEntries.Count + " prefabs"))
                StartImport();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (_importEntries == null || _importEntries.Count == 0)
                GUILayout.Label("Choose a JSON file and click Read file to enable import.", _mutedStyle);
        }

        void DrawStatusLine(string text)
        {
            Color prev = GUI.color;
            GUI.color = StatusColor();
            GUILayout.Label(text, GUILayout.Height(26f));
            GUI.color = prev;
        }

        string PasteLabel()
        {
            // what's buffered shows on the status line when it's copied
            return _copied != null ? "Paste" : "Paste (clip)";
        }

        string ReplaceLabel()
        {
            return _copied != null ? "Replace with copied string" : "Replace (clipboard string)";
        }

        void OutlineToggle()
        {
            string label = _outlines
                ? "Outline nearby entities · " + _outlineCount + " within " + OutlineRadius.ToString("0") + " m"
                : "Outline nearby entities (" + OutlineRadius.ToString("0") + " m)";
            bool now = ThemedToggle(_outlines, label);
            if (_outlines && _outlineCount == 0)
                GUILayout.Label("No loaded items near this camera. Move your player into the area to stream them in.", _mutedStyle);
            if (now == _outlines) return;
            _outlines = now;
            if (now) { _outlineRefreshAt = 0f; _outlineScanAt = 0f; }
            else { _outlineBoxes.Clear(); _outlineRoots.Clear(); _outlineCount = 0; }
        }

        // ---- helpers ----

        Vector3 CurrentPos()
        {
            try { if (_root != null) return _root.transform.position; } catch { }
            return (_info != null) ? new Vector3(_info.X, _info.Y, _info.Z) : Vector3.zero;
        }

        Vector3 CurrentEuler()
        {
            try { if (_root != null) return _root.transform.eulerAngles; } catch { }
            return (_info != null) ? new Vector3(_info.Ex, _info.Ey, _info.Ez) : Vector3.zero;
        }

        static bool TryBounds(NetworkEntity root, out Bounds b)
        {
            b = new Bounds();
            try
            {
                Renderer[] rs = root.GetComponentsInChildren<Renderer>();
                bool init = false;
                if (rs != null)
                {
                    for (int i = 0; i < rs.Length; i++)
                    {
                        if (rs[i] == null) continue;
                        if (!init) { b = rs[i].bounds; init = true; }
                        else b.Encapsulate(rs[i].bounds);
                    }
                }
                if (init) return true;
                b = new Bounds(root.transform.position, Vector3.one * 0.5f);
                return true;
            }
            catch { return false; }
        }

        void SetStatus(string s)
        {
            SetStatus(s, ToneNeutral);
        }

        void SetStatus(string s, int tone)
        {
            _status = s;
            _statusTone = tone;
            if (_log != null) _log(s);
        }

        Color StatusColor()
        {
            if (_statusTone == ToneOk) return new Color(0.55f, 1f, 0.65f, 1f);
            if (_statusTone == ToneErr) return new Color(1f, 0.55f, 0.5f, 1f);
            return new Color(1f, 1f, 1f, 0.9f);
        }

        string Reason(EditResult r)
        {
            return !string.IsNullOrEmpty(r.Err) ? r.Err : "(no reason given)";
        }

        static string F(float v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static void EnsurePixel()
        {
            if (_px != null) return;
            _px = new Texture2D(1, 1);
            _px.SetPixel(0, 0, Color.white);
            _px.Apply();
        }

        static void DrawRect(float x, float y, float w, float h, Color col)
        {
            Color prev = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(new Rect(x, y, w, h), _px);
            GUI.color = prev;
        }

        static void DrawBox(float x, float y, float w, float h, Color col, float t)
        {
            DrawRect(x, y, w, t, col);
            DrawRect(x, y + h - t, w, t, col);
            DrawRect(x, y, t, h, col);
            DrawRect(x + w - t, y, t, h, col);
        }
    }
}
