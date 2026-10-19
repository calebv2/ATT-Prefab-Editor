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
using UnityEngine.Rendering;

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
            public int Identifier;
            public Vector3 Position;
            public float Yaw;
        }

        sealed class BlacklistEntry
        {
            public int Identifier;
            public string Name;
            public string Reason;
            public string Expires;
        }

        sealed class InfoBoardEntry
        {
            public string Key;
            public string Channel;
            public string Text;
            public bool HasRotation;
            public bool RotationEnabled;
            public int Interval;
            public string Unit;
            public readonly List<string> Messages = new List<string>();
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
        const int MaxSpawnBatchCount = 100;
        const float SpawnBatchSpacing = 1.5f;

        // resize amount is relative to the prefab's current serialized scale.
        // The slider previews locally and commits one server replacement on release.
        const float MinResizePercent = -50f;
        const float MaxResizePercent = 100f;
        string _resizePercentText = "10";
        float _resizePercent = 10f;
        readonly List<NetworkEntity> _resizePreviewRoots = new List<NetworkEntity>();
        readonly List<Vector3> _resizePreviewScales = new List<Vector3>();
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
        const float PickMaxColliderDim = 100f; // larger colliders must resolve to an editable prefab
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
        bool _adminTimeFocused, _boardTextFocused, _boardIntervalFocused, _boardMessageFocused;
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
        string _spawnCountText = "1";
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
        readonly List<uint> _spawnBatchIds = new List<uint>();
        readonly HashSet<string> _pouchableSpawnNames = new HashSet<string>(StringComparer.Ordinal);
        int _spawnBatchCount, _spawnBatchIndex, _spawnBatchFailures;
        bool _pouchableSpawnNamesLoaded, _spawnPouchFallback;
        float _pouchableSpawnNamesRetryAt;
        uint _spawnBatchHash;
        string _spawnBatchName = "";
        Vector3 _spawnBatchOrigin, _spawnBatchRight, _spawnBatchForward;
        Rect _spawnRect;
        Rect _previewRect;
        Vector2 _spawnScroll;
        readonly Dictionary<uint, Texture2D> _prefabImages = new Dictionary<uint, Texture2D>();
        readonly HashSet<uint> _missingPrefabImages = new HashSet<uint>();
        bool _searchFocused;

        // Named camera work areas; saved home recovery for temporary teleport destinations.
        readonly List<WorkArea> _workAreas = new List<WorkArea>();
        readonly List<OnlinePlayer> _onlinePlayers = new List<OnlinePlayer>();
        bool _playersLoading, _workBusy, _workAreaNameFocused;
        string _workAreaName = "Work area";
        string _selectedPlayer = "";
        string _workMessage = "";
        TravelState _travel;
        string _areasPath, _travelPath;
        int _selectedArea = -1;
        string _keepAlivePlayer = "";
        float _keepAliveNextAt;
        int _keepAliveDirection = 1;
        bool _keepAliveBusy;
        int _keepAliveGeneration;
        string _teleporterDestination = "-596.447,275.657,240.869";
        string _teleporterStatus = "";
        bool _teleporterBusy;
        uint _tradeId, _tradeHash;
        int _tradeGeneration;
        bool _tradeBusy, _tradeLoaded;
        string _tradeQuery = "", _tradeItemName = "", _tradePrice = "1", _tradeQuantity = "1", _tradeStock = "1", _tradeAddStock = "1", _tradeStatus = "";
        readonly CoordinateTextField _tradeSearchInput = new CoordinateTextField("TradeItemSearch", false);
        readonly CoordinateTextField _tradePriceInput = new CoordinateTextField("TradePrice");
        readonly CoordinateTextField _tradeQuantityInput = new CoordinateTextField("TradeQuantity");
        readonly CoordinateTextField _tradeStockInput = new CoordinateTextField("TradeStock");
        readonly CoordinateTextField _tradeAddStockInput = new CoordinateTextField("TradeAddStock");
        bool _teleporterDestinationFocused;
        readonly CoordinateTextField _teleporterInput = new CoordinateTextField();

        // Server-authoritative admin controls.
        string _adminTime = "12";
        string _adminStatus = "";
        string _adminKickPlayer = "";
        string _adminMovePlayer = "";
        string _adminDestinationPlayer = "";
        bool _adminBusy;
        float _adminKickUntil, _adminTeleportUntil;
        string _adminKickArmedPlayer = "";
        string _adminTeleportArmedFrom = "", _adminTeleportArmedTo = "";
        string _banHours = "24", _banReason = "Rule violation";
        float _adminKillUntil, _adminBanUntil, _adminUnbanUntil;
        string _adminKillArmedPlayer = "", _adminBanArmedPlayer = "";
        int _adminUnbanArmedId;
        readonly List<BlacklistEntry> _blacklistEntries = new List<BlacklistEntry>();
        bool _blacklistLoading;
        string _blacklistStatus = "";

        // Quiet watch streams a validated remote map chunk to the detached camera
        // while keeping the local avatar at its current position.
        bool _watchActive;
        OnlinePlayer _watchTarget;
        object _watchChunkManager;
        Vector3 _watchLocalPosition, _watchLastChunkPosition;
        Vector3 _watchSavedCameraPosition;
        Quaternion _watchSavedCameraRotation;
        float _watchNextPoll, _watchNextChunkLoad;

        // The LiveInfoBoards mod watches this server-side registry and applies edits live.
        readonly List<InfoBoardEntry> _infoBoards = new List<InfoBoardEntry>();
        bool _infoBoardsLoading, _infoBoardsLoaded, _infoBoardsConfigured, _infoBoardSaving;
        string _infoBoardStatus = "";
        string _infoBoardFormKey = "";
        string _infoBoardText = "";
        Vector2 _infoBoardTextScroll;
        string _infoBoardIntervalText = "2";
        string _infoBoardUnit = "minutes";
        bool _infoBoardHasRotation, _infoBoardRotationEnabled;
        readonly List<string> _infoBoardMessages = new List<string>();
        readonly List<Vector2> _infoBoardMessageScrolls = new List<Vector2>();

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
        bool _spawnCountFocused;
        string _objectLoaderPath = "";
        string _objectLoaderLoadedPath = "";
        string _objectLoaderStatus = "Enter an OBJ or GLB path, or browse for a model.";
        string _objectLoaderLoadedName = "";
        int _objectLoaderVertexCount;
        bool _objectLoaderPathFocused;
        Mesh _objectLoaderMesh;
        Texture2D _objectLoaderTexture;
        bool _objectLoaderSkybox;
        readonly List<GameObject> _objectLoaderSpawned = new List<GameObject>();
        int _objectLoaderSelected = -1;
        bool _objectLoaderGizmoSelected;

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
        public bool Watching { get { return _watchActive; } }

        // true while the spawn search field has focus - the freecam must not fly
        // off WASD keystrokes that are actually someone typing a prefab name
        public bool WantsKeyboard
        {
            get
            {
                return (_panelTab == 10 && ServerSettingsFocused())
                    || (_panelTab == 4 && (_broadcastInput.IsFocused || _spawnLocationInput.IsFocused))
                    || (_panelTab == 9 && (_tradeSearchInput.IsFocused || _tradePriceInput.IsFocused || _tradeQuantityInput.IsFocused || _tradeStockInput.IsFocused || _tradeAddStockInput.IsFocused))
                    || (_spawnOpen && _searchFocused) || _spawnCountFocused
                    || (_panelTab == 3 && _workAreaNameFocused)
                    || (_panelTab == 8 && _teleporterDestinationFocused)
                    || (_panelTab == 4 && _adminTimeFocused)
                    || (_panelTab == 5 && (_boardTextFocused || _boardIntervalFocused || _boardMessageFocused))
                    || (_panelTab == 6 && _objectLoaderPathFocused)
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
            _teleporterInput.Stop();
            _broadcastInput.Stop();
            _spawnLocationInput.Stop();
            foreach (ServerSettingField field in _serverSettings) field.Input.Stop();
            ResetTradeForm();
            _teleporterDestinationFocused = false;
            StopWatching();
            RestoreResizePreview();
            RestoreGroupPreview();
            _gizmo.Cancel(false); // restores the transform if a drag is mid-flight
            _has = false;
            _root = null;
            _info = null;
            _objectLoaderGizmoSelected = false;
            _group.Clear();
            _groupPreviewStarts.Clear();
            _deleteArmUntil = 0f;
            _deleteIds = "";
            _deleteConfirmCount = 0;
            _infoBoardFormKey = "";
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
            _adminTimeFocused = false;
            _spawnCountFocused = false;
            _boardTextFocused = _boardIntervalFocused = _boardMessageFocused = false;
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
            if (_busy || !_has || _group.Count == 0) return;
            if (!_resizePreviewActive)
            {
                _resizePreviewRoots.Clear();
                _resizePreviewScales.Clear();
                for (int i = 0; i < _group.Count; i++)
                {
                    NetworkEntity item = _group[i];
                    if (item == null) continue;
                    try
                    {
                        _resizePreviewRoots.Add(item);
                        _resizePreviewScales.Add(item.transform.localScale);
                    }
                    catch { }
                }
                if (_resizePreviewRoots.Count == 0) return;
                _resizePreviewActive = true;
            }
            try
            {
                float factor = 1f + percent / 100f;
                for (int i = 0; i < _resizePreviewRoots.Count; i++)
                    if (_resizePreviewRoots[i] != null)
                        _resizePreviewRoots[i].transform.localScale = _resizePreviewScales[i] * factor;
                SetStatus("resize preview " + _resizePreviewRoots.Count + " selected items "
                    + percent.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture)
                    + "% - release slider to apply");
            }
            catch { RestoreResizePreview(); }
        }

        void RestoreResizePreview()
        {
            if (_resizePreviewActive)
            {
                for (int i = 0; i < _resizePreviewRoots.Count; i++)
                {
                    try
                    {
                        if (_resizePreviewRoots[i] != null)
                            _resizePreviewRoots[i].transform.localScale = _resizePreviewScales[i];
                    }
                    catch { }
                }
            }
            _resizePreviewRoots.Clear();
            _resizePreviewScales.Clear();
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
            if(Time.unscaledTime>=_nextAccessAt && !_accessPending) RefreshAccess();
            _teleporterInput.Tick(_panelTab == 8);
            _broadcastInput.Tick(_panelTab == 4);
            _spawnLocationInput.Tick(_panelTab == 4);
            foreach (ServerSettingField field in _serverSettings) field.Input.Tick(_panelTab == 10);
            bool tradeActive = _panelTab == 9 && IsTradeDeckSelected();
            _tradeSearchInput.Tick(tradeActive);
            _tradePriceInput.Tick(tradeActive);
            _tradeQuantityInput.Tick(tradeActive);
            _tradeStockInput.Tick(tradeActive);
            _tradeAddStockInput.Tick(tradeActive);
            if (_tradeId != 0 && (!IsTradeDeckSelected() || _tradeId != _id)) ResetTradeForm();
            if (_resizeSliderDragging)
            {
                Mouse resizeMouse = Mouse.current;
                if (resizeMouse == null || !resizeMouse.leftButton.isPressed)
                    FinishResizeSlider();
            }
            if (Camera == null) return;
            TickWatch();
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
            GameObject localTarget = SelectedImportedObject();
            if (_has || localTarget != null)
            {
                Transform t = null;
                try
                {
                    if (_has && _root != null) t = _root.transform;
                    else if (localTarget != null) t = localTarget.transform;
                }
                catch { t = null; }
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
            if (hits == null) hits = new RaycastHit[0];
            Array.Sort(hits, CompareHitDistance);

            _cands.Clear();
            Dictionary<NetworkEntity, float> candidateDistances = new Dictionary<NetworkEntity, float>();
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

                int importedIndex = ImportedModelIndex(col.transform);
                if (importedIndex >= 0)
                {
                    if (_cands.Count > 0) break; // keep a nearer network prefab in priority
                    SelectImportedObject(importedIndex);
                    if (Mouse.current != null && Mouse.current.leftButton.isPressed)
                    {
                        GameObject imported = SelectedImportedObject();
                        if (imported != null)
                            _gizmo.BeginDirectDrag(Camera, imported.transform, hits[i].point, false);
                    }
                    return true;
                }

                // merged world meshes that slip through on unmasked layers - anything
                // this big is scenery, and its parent walk lands on a whole chunk
                try
                {
                    Vector3 bs = col.bounds.size;
                    float dim = Mathf.Max(bs.x, Mathf.Max(bs.y, bs.z));
                    if (dim > PickMaxColliderDim && !HasEditablePrefab(col))
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
                    candidateDistances[root] = hits[i].distance;
                    if (!hasGrabPoint) { grabPoint = hits[i].point; hasGrabPoint = true; }
                }
                if (_cands.Count >= PickMaxCandidates) break;
            }

            // Terrain exceeds the scenery-size cutoff; the enclosing sky sphere
            // intentionally has no collider. Pick their actual visible geometry.
            try
            {
                foreach (NetworkEntity root in PersistentPrefabSelection.ActiveRoots())
                {
                    float distance;
                    if (!PersistentPrefabSelection.Raycast(root, ray, 500f, out distance)) continue;
                    if (!_cands.Contains(root)) _cands.Add(root);
                    float existing;
                    if (!candidateDistances.TryGetValue(root, out existing) || distance < existing)
                        candidateDistances[root] = distance;
                }
                _cands.Sort(delegate(NetworkEntity a, NetworkEntity b) {
                    return candidateDistances[a].CompareTo(candidateDistances[b]);
                });
                if (_cands.Count > 0)
                {
                    grabPoint = ray.GetPoint(candidateDistances[_cands[0]]);
                    hasGrabPoint = true;
                }
            }
            catch (Exception e) { if (_log != null) _log("persistent mesh selection failed: " + e.Message); }

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
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim && !HasEditablePrefab(col)) continue;
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
                            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim && !HasEditablePrefab(col)) continue;
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
        static bool HasEditablePrefab(Collider collider)
        {
            try
            {
                NetworkEntity[] chain = collider.GetComponentsInParent<NetworkEntity>();
                foreach (NetworkEntity entity in chain)
                {
                    if (entity == null) continue;
                    NetworkEntity root = entity.PrefabRoot != null ? entity.PrefabRoot : entity;
                    if (!root.IsBeingDestroyed && root.Identifier != 0 && root.Prefab != null)
                        return true;
                }
            }
            catch { }
            return false;
        }

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
                        if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > PickMaxColliderDim && !HasEditablePrefab(col)) continue;
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
            _objectLoaderGizmoSelected = false;
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
            GameObject local = SelectedImportedObject();
            if (local != null)
            {
                SetStatus("moved imported model " + local.name, ToneOk);
                return;
            }
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
            GameObject local = SelectedImportedObject();
            if (local != null)
            {
                SetStatus("rotated imported model " + local.name, ToneOk);
                return;
            }
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

        // Scale is applied and recorded on the server, so remote users do
        // not need a separate workbench process listening on their own PC.
        void ResizeSelected(float factor)
        {
            if (_busy || !_has) return;
            if (factor < 0.5f || factor > 2f)
            {
                RestoreResizePreview();
                SetStatus("resize amount must be between -50% and +100%", ToneErr);
                return;
            }
            List<uint> ids = new List<uint>();
            for (int i = 0; i < _group.Count; i++)
            {
                try { if (_group[i] != null && _group[i].Identifier != 0) ids.Add(_group[i].Identifier); }
                catch { }
            }
            if (ids.Count == 0) { RestoreResizePreview(); SetStatus("selection has no live items", ToneErr); return; }
            RestoreResizePreview();
            _busy = true;
            if (ids.Count == 1)
            {
                SetStatus("scaling #" + ids[0] + " ...");
                _api.Scale(ids[0], factor, OnReplaced);
                return;
            }
            SetStatus("scaling " + ids.Count + " selected items...");
            ResizeNext(ids, new List<uint>(), 0, factor);
        }

        void ResizeNext(List<uint> ids, List<uint> replacements, int index, float factor)
        {
            if (index >= ids.Count)
            {
                FinishResizeMany(ids, replacements, ids.Count, null);
                return;
            }
            uint oldId = ids[index];
            _api.Scale(oldId, factor, delegate(EditResult r)
            {
                if (r.Ok && r.Ent != null && r.Ent.Id != 0)
                {
                    replacements.Add(r.Ent.Id);
                    SetStatus("scaled " + replacements.Count + " of " + ids.Count + " items...");
                    ResizeNext(ids, replacements, index + 1, factor);
                    return;
                }

                // Keep the unprocessed selection alongside completed replacements
                // so a partial server failure does not lose the user's selection.
                for (int i = index; i < ids.Count; i++) replacements.Add(ids[i]);
                FinishResizeMany(ids, replacements, index, Reason(r));
            });
        }

        void FinishResizeMany(List<uint> originalIds, List<uint> selectedIds, int scaledCount, string error)
        {
            _busy = false;
            Clear();
            AdoptManySoon(selectedIds, "scaled");
            RefreshHistory(false);
            if (error == null)
                SetStatus("scaled " + scaledCount + " items; restoring the full selection...", ToneOk);
            else
                SetStatus("scaled " + scaledCount + " of " + originalIds.Count
                    + " items; stopped after an error: " + error, ToneErr);
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

            if (_spawnCatalogLoaded)
            {
                if (_spawnQuery != _spawnSent) FilterSpawnCatalog(_spawnQuery);
                return;
            }

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
                LoadPackagedSpawnCatalog();
                if (_spawnCatalog.Count > 0)
                {
                    _spawnCatalogLoaded = true;
                    FilterSpawnCatalog(_spawnQuery);
                    SetStatus("showing bundled spawn catalog; a reachable server is needed to spawn items", ToneErr);
                    return;
                }
                _spawnStaticFallback = true;
                _spawnSent = null;
                SetStatus("live spawn list unavailable; requesting the panel catalog", ToneErr);
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
            // Include the Workbench's bundled, spawnable reference catalog too.
            // Live server values win on duplicate hashes; the server still validates
            // each spawn request, so this never turns the client into the authority.
            LoadPackagedSpawnCatalog();
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
            SetStatus("loaded " + _spawnCatalog.Count + " spawnables (server list + Workbench catalog)", ToneOk);
        }

        void LoadPackagedSpawnCatalog()
        {
            string file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "PrefabEditorCatalog.json");
            if (!File.Exists(file)) return;
            try
            {
                string json = File.ReadAllText(file);
                List<string> records = Json.Objects(Json.Value(json, "prefabs"));
                HashSet<uint> known = new HashSet<uint>();
                for (int i = 0; i < _spawnCatalog.Count; i++) known.Add(_spawnCatalog[i].Hash);
                for (int i = 0; i < records.Count; i++)
                {
                    string record = records[i];
                    if (!Json.Bool(record, "spawnable", false)) continue;
                    uint hash = Json.UInt(record, "hash");
                    string name = Json.Str(record, "name");
                    if (hash == 0 || string.IsNullOrEmpty(name) || !known.Add(hash)) continue;
                    _spawnCatalog.Add(new SpawnCatalogEntry { Name = name.Replace('_', ' '), Hash = hash });
                }
            }
            catch (Exception e)
            {
                if (_log != null) _log("[PrefabEditor] Could not load bundled Workbench catalog: " + e.Message);
            }
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
            SetStatus("placing " + name + " - click to spawn " + _spawnCountText
                + " items (pouchable items group in a pouch; Shift = keep placing), Esc / right-click cancels");
        }

        bool TrySpawnCount(out int count)
        {
            count = 0;
            return int.TryParse(_spawnCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                && count >= 1 && count <= MaxSpawnBatchCount;
        }

        bool StartSpawnBatch(string name, uint hash, Vector3 origin, bool closePicker)
        {
            if (_busy || Camera == null) return false;
            int count;
            if (!TrySpawnCount(out count))
            {
                SetStatus("enter a spawn count from 1 to " + MaxSpawnBatchCount, ToneErr);
                return false;
            }

            if (closePicker) _spawnOpen = false;
            _busy = true;
            ContinueSpawnBatch(name, hash, origin, count);
            return true;
        }

        void ContinueSpawnBatch(string name, uint hash, Vector3 origin, int count)
        {
            // Use the game's native counted spawn for multi-item requests. Its
            // count handling is what groups compatible items into pouches; the
            // editor's individual spawnat loop creates loose copies instead.
            // The exact form keeps the cursor/camera placement point.
            if (count > 1)
            {
                string exactName = (name ?? "").Replace(" ", "");
                if (string.IsNullOrEmpty(exactName))
                {
                    _busy = false;
                    SetStatus("could not spawn: prefab name is empty", ToneErr);
                    return;
                }
                string command = "spawn exact " + VecText(origin) + " 0,0,0 " + exactName + " "
                    + count.ToString(CultureInfo.InvariantCulture);
                SetStatus("spawning " + count + " " + name + " at the placement point...");
                _api.Command(command, delegate(EditResult spawned)
                {
                    _busy = false;
                    if (!spawned.Ok)
                    {
                        SetStatus("spawn failed: " + Reason(spawned), ToneErr);
                        return;
                    }
                    SetStatus("spawned " + count + " " + name + " with the game's counted spawn", ToneOk);
                });
                return;
            }

            _spawnBatchName = name;
            _spawnBatchHash = hash;
            _spawnBatchOrigin = origin;
            _spawnBatchCount = count;
            _spawnBatchIndex = 0;
            _spawnBatchFailures = 0;
            _spawnBatchIds.Clear();
            _spawnBatchRight = Vector3.ProjectOnPlane(Camera.transform.right, Vector3.up);
            if (_spawnBatchRight.sqrMagnitude < 0.001f) _spawnBatchRight = Vector3.right;
            _spawnBatchRight.Normalize();
            _spawnBatchForward = Vector3.Cross(_spawnBatchRight, Vector3.up).normalized;
            SetStatus("spawning " + count + " " + name + (count == 1 ? "" : " copies") + " ...");
            SpawnNextInBatch();
        }

        void SpawnAtCamera(string name, uint hash)
        {
            if (_busy || Camera == null) return;
            Vector3 p = Camera.transform.position + Camera.transform.forward * 3f;
            StartSpawnBatch(name, hash, p, true);
        }

        Vector3 SpawnBatchPosition(int index)
        {
            int columns = Mathf.CeilToInt(Mathf.Sqrt(_spawnBatchCount));
            int rows = Mathf.CeilToInt((float)_spawnBatchCount / columns);
            int column = index % columns;
            int row = index / columns;
            float horizontal = (column - (columns - 1) * 0.5f) * SpawnBatchSpacing;
            float depth = (row - (rows - 1) * 0.5f) * SpawnBatchSpacing;
            return _spawnBatchOrigin + _spawnBatchRight * horizontal + _spawnBatchForward * depth;
        }

        void SpawnNextInBatch()
        {
            if (_spawnBatchIndex >= _spawnBatchCount)
            {
                FinishSpawnBatch();
                return;
            }
            Vector3 p = SpawnBatchPosition(_spawnBatchIndex++);
            _api.SpawnAt(p.x, p.y, p.z, _spawnBatchHash, OnSpawnBatchItem);
        }

        void OnSpawnBatchItem(EditResult r)
        {
            if (r.Ok)
            {
                if (r.Ent != null && r.Ent.Id != 0) _spawnBatchIds.Add(r.Ent.Id);
            }
            else _spawnBatchFailures++;
            SpawnNextInBatch();
        }

        void FinishSpawnBatch()
        {
            _busy = false;
            if (_spawnBatchIds.Count == 1) AdoptSoon(_spawnBatchIds[0]);
            else if (_spawnBatchIds.Count > 1) AdoptManySoon(_spawnBatchIds, "spawned");
            int spawned = _spawnBatchCount - _spawnBatchFailures;

            if (_spawnBatchFailures == 0)
                SetStatus("spawned " + spawned + " " + _spawnBatchName
                    + (spawned == 1 ? "" : " copies"), ToneOk);
            else
                SetStatus("spawned " + spawned + " of " + _spawnBatchCount + " "
                    + _spawnBatchName + "; " + _spawnBatchFailures + " failed", ToneErr);
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
                if (StartSpawnBatch(_placeName, _placeHash, _placePos, false) && !more) _placing = false;
            }
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
            _objectLoaderGizmoSelected = false;
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
            List<NetworkEntity> entities = new List<NetworkEntity>();
            try { entities.AddRange(SceneEntities()); } catch { }
            // The client scene-manager walk sometimes omits streamed roots.
            // Include collider-free skyboxes and large terrain for adoption,
            // rectangle selection, nearby selection, and outlines as well.
            try
            {
                foreach (NetworkEntity root in PersistentPrefabSelection.ActiveRoots())
                    if (!entities.Contains(root)) entities.Add(root);
            }
            catch { }
            return entities;
        }

        static IEnumerable<NetworkEntity> SceneEntities()
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
            if (_has) _gizmo.Draw(cam, CurrentPos());
            else
            {
                GameObject local = SelectedImportedObject();
                if (local != null) _gizmo.Draw(cam, local.transform.position);
            }
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

            GUILayout.BeginHorizontal();
            GUILayout.Label("Copies", GUILayout.Width(54f));
            DrawSpawnCountField("SpawnWindowCountField", 58f);
            GUILayout.Label("per placement (1–100)", _mutedStyle);
            GUILayout.EndHorizontal();

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
            float headerHeight = 219f + (showConnectionMessage ? 28f : 0f);
            float footerHeight = 116f;
            float bodyHeight = Mathf.Max(0f, contentHeight - headerHeight - footerHeight - 8f);

            _positionFocused = _rotationFocused = _stepFocused = false;
            _arrangeSpacingFocused = _duplicateOffsetFocused = false;
            _resizePercentFocused = false;
            _importPathFocused = false;
            _objectLoaderPathFocused = false;
            _workAreaNameFocused = false;
            _teleporterDestinationFocused = false;
            _adminTimeFocused = _boardTextFocused = _boardIntervalFocused = _boardMessageFocused = false;
            _spawnCountFocused = false;

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
            DrawPanelTab(0, "Select");
            DrawPanelTab(1, "Arrange");
            DrawPanelTab(2, "Build");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawPanelTab(3, "World");
            DrawPanelTab(4, "Admin");
            DrawPanelTab(5, "Info boards");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawPanelTab(6, "Object Loader");
            DrawPanelTab(10, "Server Settings");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawPanelTab(8, "Teleporter");
            DrawPanelTab(9, "Trading");
            GUILayout.EndHorizontal();
            if (_panelTab != 8) _teleporterInput.Stop();
            if (_panelTab != _lastPanelTab)
            {
                _panelScroll = Vector2.zero;
                _lastPanelTab = _panelTab;
                if (_panelTab == 3 || _panelTab == 4) RefreshPlayers();
                if (_panelTab == 5) RefreshInfoBoards();
                if (_panelTab == 6) RefreshObjectLoaderStatus();
            }
            GUILayout.Space(3f);
            string selectionSummary;
            GameObject localSelection = SelectedImportedObject();
            if (!_has && localSelection != null)
                selectionSummary = "1 selected  ·  " + localSelection.name + " (local model)";
            else if (!_has) selectionSummary = "No selection  ·  click or drag objects in the world";
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
            if (!CanOpenTab(_panelTab)) GUILayout.Label(_accessStatus, _mutedStyle);
            else if (_panelTab == 0) DrawSelectionTab();
            else if (_panelTab == 1) DrawArrangeTab();
            else if (_panelTab == 2) DrawBuildTab();
            else if (_panelTab == 3) DrawWorldTab();
            else if (_panelTab == 4) DrawAdminTab();
            else if (_panelTab == 5) DrawInfoBoardsTab();
            else if (_panelTab == 6) DrawObjectLoaderTab();
            else if (_panelTab == 8) DrawTeleporterControls();
            else if (_panelTab == 9) DrawTradeDeckControls();
            else if (_panelTab == 10) DrawServerSettingsTab();
            GUILayout.EndScrollView();

            GUILayout.BeginVertical(GUILayout.Height(footerHeight));
            GUILayout.Space(3f);
            bool armed = Time.unscaledTime < _deleteArmUntil && !string.IsNullOrEmpty(_deleteIds);
            GUI.enabled = _has && !_busy && CanOpenTab(0);
            if (GUILayout.Button(armed ? "CONFIRM DELETE  " + _deleteConfirmCount + " ITEMS" :
                (_has ? "DELETE  " + _group.Count + " SELECTED ITEM" + (_group.Count == 1 ? "" : "S") : "DELETE  No selection"),
                armed ? _dangerButtonStyle : GUI.skin.button, GUILayout.Height(32f))) DeleteClick();
            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy && _undoCount > 0 && CanOpenTab(0) && CanOpenTab(1) && CanOpenTab(2);
            if (GUILayout.Button("Undo  Ctrl+Z" + (_undoCount > 0 ? "  (" + _undoCount + ")" : ""))) RunHistory(true);
            GUI.enabled = !_busy && _redoCount > 0 && CanOpenTab(0) && CanOpenTab(1) && CanOpenTab(2);
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

        readonly HashSet<string> _accessTabs = new HashSet<string>();
        bool _accessPending;
        float _nextAccessAt;
        string _accessStatus = "Checking your tab permissions...";
        static string TabPermission(int index)
        {
            switch(index)
            {
                case 0:return "select"; case 1:return "arrange"; case 2:return "build"; case 3:return "world";
                case 4:return "admin"; case 5:return "boards"; case 6:return "objectLoader";
                case 8:return "teleporter"; case 9:return "trading"; case 10:return "settings";
            }
            return "";
        }
        bool CanOpenTab(int index) { return _accessTabs.Contains(TabPermission(index)); }
        void RefreshAccess()
        {
            if(_accessPending) return;
            _accessPending=true;
            _api.Access(delegate(EditResult result)
            {
                _accessPending=false; _nextAccessAt=Time.unscaledTime+3f;
                _accessTabs.Clear();
                if(result.Ok)
                {
                    string data=Json.Value(result.Raw,"data");
                    foreach(string tab in Json.StrArray(Json.Value(data,"tabs"))) _accessTabs.Add(tab);
                    _accessStatus=_accessTabs.Count==0 ? "No tabs enabled. Ask the owner to update your access." : "";
                }
                else _accessStatus="Could not verify access. Reconnect or ask the owner to update the server mod. " + Reason(result);
                if(!CanOpenTab(_panelTab))
                {
                    _panelTab=-1;
                    foreach(int index in new[]{0,1,2,3,4,5,6,8,9,10}) if(CanOpenTab(index)) { _panelTab=index; break; }
                }
            });
        }

        void DrawPanelTab(int index, string label)
        {
            if(!CanOpenTab(index)) return;
            if (GUILayout.Button(_panelTab == index ? label.ToUpperInvariant() : label,
                _panelTab == index ? _selectedTabStyle : _tabStyle, GUILayout.Height(27f)))
                _panelTab = index;
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

            if (IsTradeDeckSelected() && GUILayout.Button("Open Trading settings")) _panelTab = 9;

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
                GameObject local = SelectedImportedObject();
                GUILayout.Label(local == null
                    ? "Select an object to move, rotate, scale, or duplicate it."
                    : "Imported model selected. Grab it or drag the gizmo to move or rotate it; resize it in Object Loader.", _mutedStyle);
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
                DrawResizeControls();
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
            GUILayout.Label("Spawn count", _mutedStyle, GUILayout.Width(78f));
            DrawSpawnCountField("BuildSpawnCountField", 58f);
            GUILayout.Label("items per click or Camera", _mutedStyle);
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

        void DrawSpawnCountField(string controlName, float width)
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = !_busy;
            GUI.SetNextControlName(controlName);
            _spawnCountText = GUILayout.TextField(_spawnCountText, GUILayout.Width(width));
            _spawnCountFocused = _spawnCountFocused || GUI.GetNameOfFocusedControl() == controlName;
            GUI.enabled = wasEnabled;
        }

        void DrawObjectLoaderTab()
        {
            GUILayout.Label("Import a 3D model", _titleStyle);
            GUILayout.Label("Built into Prefab Editor - no separate Object Loader mod is required.", _mutedStyle);
            GUILayout.Space(4f);
            GUILayout.Label(_objectLoaderStatus, _mutedStyle);
            GUILayout.Label("Model file (.obj or binary .glb)", _mutedStyle);
            GUI.SetNextControlName("ObjectLoaderPath");
            string path = GUILayout.TextField(_objectLoaderPath);
            if (path != _objectLoaderPath)
            {
                _objectLoaderPath = path;
                _objectLoaderLoadedPath = "";
                _objectLoaderLoadedName = "";
                _objectLoaderVertexCount = 0;
                if (_objectLoaderMesh != null) UnityEngine.Object.Destroy(_objectLoaderMesh);
                _objectLoaderMesh = null;
                _objectLoaderTexture = null;
                _objectLoaderSkybox = false;
            }
            _objectLoaderPathFocused = GUI.GetNameOfFocusedControl() == "ObjectLoaderPath";
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Browse model...")) BrowseObjectLoaderFile();
            GUI.enabled = !_busy && !string.IsNullOrWhiteSpace(_objectLoaderPath);
            if (GUILayout.Button("Load Model")) LoadObjectLoaderFile();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_objectLoaderLoadedName))
                GUILayout.Label("Loaded: " + _objectLoaderLoadedName + " (" + _objectLoaderVertexCount + " vertices)");
            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy && _objectLoaderLoadedPath == _objectLoaderPath
                && !string.IsNullOrEmpty(_objectLoaderLoadedPath);
            if (GUILayout.Button("Spawn 3 m ahead")) SpawnObjectLoaderFile();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            GUILayout.Label("Imported models are local decorative objects. They are not networked prefab entities and other players will not see them.", _mutedStyle);
            DrawImportedModelControls();
        }

        void BrowseObjectLoaderFile()
        {
            try
            {
                using (WinForms.OpenFileDialog dialog = new WinForms.OpenFileDialog())
                {
                    dialog.Title = "Choose a model to import";
                    dialog.Filter = "Supported models (*.obj;*.glb)|*.obj;*.glb|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        _objectLoaderPath = dialog.FileName;
                        _objectLoaderLoadedPath = "";
                        _objectLoaderLoadedName = "";
                        _objectLoaderVertexCount = 0;
                        if (_objectLoaderMesh != null) UnityEngine.Object.Destroy(_objectLoaderMesh);
                        _objectLoaderMesh = null;
                        _objectLoaderTexture = null;
                        _objectLoaderSkybox = false;
                    }
                }
            }
            catch (Exception e)
            {
                _objectLoaderStatus = "Could not open model picker: " + e.Message;
                SetStatus(_objectLoaderStatus, ToneErr);
            }
        }

        void RefreshObjectLoaderStatus()
        {
            if (_objectLoaderMesh == null && string.IsNullOrEmpty(_objectLoaderLoadedPath))
                _objectLoaderStatus = "Choose an OBJ or binary GLB file to import.";
        }

        void LoadObjectLoaderFile()
        {
            string path = (_objectLoaderPath ?? "").Trim().Trim('"', '\'');
            if (path.Length == 0)
            {
                _objectLoaderStatus = "Enter a model file path first.";
                SetStatus(_objectLoaderStatus, ToneErr);
                return;
            }
            try
            {
                Mesh mesh = IntegratedModelImporter.Load(path);
                Texture2D texture = LoadCompanionEquirectTexture(path);
                bool skybox = texture != null;
                if (skybox)
                {
                    Mesh remapped = ApplySphericalUvs(mesh);
                    UnityEngine.Object.Destroy(mesh);
                    mesh = remapped;
                }
                else texture = LoadObjDiffuseTexture(path);
                if (_objectLoaderMesh != null) UnityEngine.Object.Destroy(_objectLoaderMesh);
                _objectLoaderMesh = mesh;
                _objectLoaderTexture = texture;
                _objectLoaderSkybox = skybox;
                _objectLoaderPath = path;
                _objectLoaderLoadedPath = path;
                _objectLoaderLoadedName = Path.GetFileNameWithoutExtension(path);
                _objectLoaderVertexCount = mesh.vertexCount;
                _objectLoaderStatus = "Loaded " + _objectLoaderLoadedName
                    + (skybox ? " with 6 cubemap PNG faces converted to a 2D texture."
                        : texture == null ? " without a diffuse texture." : " with its MTL diffuse texture.");
                SetStatus(_objectLoaderStatus, ToneOk);
            }
            catch (Exception e)
            {
                _objectLoaderStatus = "Model import failed: " + e.Message;
                SetStatus(_objectLoaderStatus, ToneErr);
            }
        }

        static Texture2D LoadObjDiffuseTexture(string modelPath)
        {
            string imagePath = IntegratedModelImporter.FindObjDiffuseTexturePath(modelPath);
            if (imagePath == null) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(imagePath)))
                    throw new InvalidDataException("Could not decode OBJ diffuse texture: " + imagePath);
                texture.wrapMode = TextureWrapMode.Repeat;
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        static Texture2D LoadCompanionEquirectTexture(string modelPath)
        {
            string directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(directory)) return null;
            string prefix = Path.Combine(directory, "LoadingScreenCubemap_face_");
            string[] paths = new string[6];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = prefix + i.ToString(CultureInfo.InvariantCulture) + ".png";
                if (!File.Exists(paths[i])) return null;
            }

            Texture2D[] images = new Texture2D[6];
            Texture2D result = null;
            try
            {
                int size = 0;
                for (int i = 0; i < images.Length; i++)
                {
                    images[i] = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    images[i].hideFlags = HideFlags.HideAndDontSave;
                    if (!images[i].LoadImage(File.ReadAllBytes(paths[i])))
                        throw new InvalidDataException("could not decode cubemap PNG " + Path.GetFileName(paths[i]));
                    images[i].wrapMode = TextureWrapMode.Clamp;
                    if (images[i].width != images[i].height)
                        throw new InvalidDataException("cubemap PNG faces must be square");
                    if (size == 0) size = images[i].width;
                    else if (images[i].width != size)
                        throw new InvalidDataException("all cubemap PNG faces must have the same dimensions");
                }

                // The game build strips Skybox/Cubemap. Reproject the six faces to
                // a normal 2:1 texture so a shader already present in the game can
                // display it on the exported sphere mesh.
                int width = size * 2;
                int height = size;
                Color[] pixels = new Color[width * height];
                for (int y = 0; y < height; y++)
                {
                    float latitude = (((float)y + 0.5f) / height - 0.5f) * Mathf.PI;
                    float cosLatitude = Mathf.Cos(latitude);
                    for (int x = 0; x < width; x++)
                    {
                        float longitude = (((float)x + 0.5f) / width - 0.5f) * (Mathf.PI * 2f);
                        Vector3 direction = new Vector3(Mathf.Sin(longitude) * cosLatitude,
                            Mathf.Sin(latitude), Mathf.Cos(longitude) * cosLatitude);
                        pixels[y * width + x] = SampleCubemapFaces(images, direction);
                    }
                }
                result = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                result.name = "Imported Loading Screen Equirectangular Texture";
                result.hideFlags = HideFlags.HideAndDontSave;
                result.wrapMode = TextureWrapMode.Repeat;
                result.filterMode = FilterMode.Bilinear;
                result.SetPixels(pixels);
                result.Apply(false, true);
                return result;
            }
            catch
            {
                if (result != null) UnityEngine.Object.Destroy(result);
                throw;
            }
            finally
            {
                for (int i = 0; i < images.Length; i++)
                    if (images[i] != null) UnityEngine.Object.Destroy(images[i]);
            }
        }

        static Color SampleCubemapFaces(Texture2D[] faces, Vector3 direction)
        {
            float ax = Mathf.Abs(direction.x), ay = Mathf.Abs(direction.y), az = Mathf.Abs(direction.z);
            int face;
            float u, v, major;
            if (ax >= ay && ax >= az)
            {
                major = ax;
                if (direction.x >= 0f) { face = 0; u = -direction.z / major; }
                else { face = 1; u = direction.z / major; }
                v = -direction.y / major;
            }
            else if (ay >= az)
            {
                major = ay;
                if (direction.y >= 0f) { face = 2; u = direction.x / major; v = direction.z / major; }
                else { face = 3; u = direction.x / major; v = -direction.z / major; }
            }
            else
            {
                major = az;
                if (direction.z >= 0f) { face = 4; u = direction.x / major; }
                else { face = 5; u = -direction.x / major; }
                v = -direction.y / major;
            }
            return faces[face].GetPixelBilinear(u * 0.5f + 0.5f, v * 0.5f + 0.5f);
        }

        static Mesh ApplySphericalUvs(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int[] triangles = mesh.triangles;
            if (normals == null || normals.Length != vertices.Length)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }

            List<Vector3> outVertices = new List<Vector3>(triangles.Length);
            List<Vector3> outNormals = new List<Vector3>(triangles.Length);
            List<Vector2> outUvs = new List<Vector2>(triangles.Length);
            List<int> outTriangles = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector2[] uv = new Vector2[3];
                for (int j = 0; j < 3; j++)
                {
                    Vector3 p = vertices[triangles[i + j]];
                    float length = p.magnitude;
                    Vector3 d = length > 0.00001f ? p / length : Vector3.forward;
                    uv[j] = new Vector2(Mathf.Atan2(d.x, d.z) / (Mathf.PI * 2f) + 0.5f,
                        Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) / Mathf.PI + 0.5f);
                }
                float minU = Mathf.Min(uv[0].x, Mathf.Min(uv[1].x, uv[2].x));
                float maxU = Mathf.Max(uv[0].x, Mathf.Max(uv[1].x, uv[2].x));
                if (maxU - minU > 0.5f)
                    for (int j = 0; j < 3; j++) if (uv[j].x < 0.5f) uv[j].x += 1f;

                for (int j = 0; j < 3; j++)
                {
                    int source = triangles[i + j];
                    outTriangles.Add(outVertices.Count);
                    outVertices.Add(vertices[source]);
                    outNormals.Add(normals[source]);
                    outUvs.Add(uv[j]);
                }
            }

            Mesh remapped = new Mesh();
            remapped.name = mesh.name + " (equirectangular UVs)";
            if (outVertices.Count > 65535) remapped.indexFormat = IndexFormat.UInt32;
            remapped.SetVertices(outVertices);
            remapped.SetNormals(outNormals);
            remapped.SetUVs(0, outUvs);
            remapped.SetTriangles(outTriangles, 0);
            remapped.RecalculateBounds();
            return remapped;
        }

        void SpawnObjectLoaderFile()
        {
            try
            {
                if (_objectLoaderMesh == null) throw new InvalidOperationException("Load an OBJ or GLB model first.");
                GameObject spawned = new GameObject("Imported Model - " + _objectLoaderLoadedName);
                MeshFilter filter = spawned.AddComponent<MeshFilter>();
                filter.sharedMesh = UnityEngine.Object.Instantiate(_objectLoaderMesh) as Mesh;
                MeshRenderer renderer = spawned.AddComponent<MeshRenderer>();
                MeshCollider modelCollider = spawned.AddComponent<MeshCollider>();
                modelCollider.sharedMesh = filter.sharedMesh;
                modelCollider.convex = false;
                SphereCollider grabCollider = spawned.AddComponent<SphereCollider>();
                grabCollider.radius = _objectLoaderSkybox ? 0.01f : 0.12f;
                if (_objectLoaderTexture != null)
                {
                    Shader shader = Shader.Find("Standard");
                    if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Unlit/Texture");
                    if (shader == null) throw new InvalidOperationException("the game has no compatible textured material shader");
                    Material material = new Material(shader);
                    if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", _objectLoaderTexture);
                    else if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", _objectLoaderTexture);
                    else
                    {
                        UnityEngine.Object.Destroy(material);
                        throw new InvalidOperationException("the selected material shader has no 2D texture slot");
                    }
                    if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                    if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off);
                    if (_objectLoaderSkybox && material.HasProperty("_EmissionMap"))
                    {
                        material.SetTexture("_EmissionMap", _objectLoaderTexture);
                        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.white);
                        material.EnableKeyword("_EMISSION");
                    }
                    material.renderQueue = 2000;
                    renderer.sharedMaterial = material;
                    if (_objectLoaderSkybox)
                    {
                        renderer.receiveShadows = false;
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        spawned.transform.localScale = Vector3.one * 100f;
                    }
                }
                else
                {
                    Shader shader = Shader.Find("Standard");
                    if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Unlit/Color");
                    if (shader != null) renderer.sharedMaterial = new Material(shader);
                }
                spawned.transform.position = Camera.transform.position + Camera.transform.forward * 3f;
                spawned.transform.rotation = Camera.transform.rotation;
                _objectLoaderSpawned.Add(spawned);
                _objectLoaderSelected = _objectLoaderSpawned.Count - 1;
                _objectLoaderGizmoSelected = true;
                _gizmo.RotationEnabled = true;
                _gizmo.GroupRotationEnabled = false;
                _objectLoaderStatus = "Spawned " + _objectLoaderLoadedName + " 3 m ahead. This object is local to your client.";
                SetStatus(_objectLoaderStatus, ToneOk);
            }
            catch (Exception e)
            {
                _objectLoaderStatus = "Model spawn failed: " + e.Message;
                SetStatus(_objectLoaderStatus, ToneErr);
            }
        }

        GameObject SelectedImportedObject()
        {
            if (!_objectLoaderGizmoSelected || _objectLoaderSelected < 0
                || _objectLoaderSelected >= _objectLoaderSpawned.Count) return null;
            return _objectLoaderSpawned[_objectLoaderSelected];
        }

        int ImportedModelIndex(Transform hit)
        {
            if (hit == null) return -1;
            for (int i = 0; i < _objectLoaderSpawned.Count; i++)
            {
                GameObject item = _objectLoaderSpawned[i];
                if (item == null) continue;
                Transform root = item.transform;
                if (hit == root || hit.IsChildOf(root)) return i;
            }
            return -1;
        }

        void SelectImportedObject(int index)
        {
            if (index < 0 || index >= _objectLoaderSpawned.Count || _objectLoaderSpawned[index] == null) return;
            if (_objectLoaderGizmoSelected && _objectLoaderSelected == index) return;
            Clear();
            _objectLoaderSelected = index;
            _objectLoaderGizmoSelected = true;
            _gizmo.RotationEnabled = true;
            _gizmo.GroupRotationEnabled = false;
            _objectLoaderStatus = "Selected " + _objectLoaderSpawned[index].name + ". Grab it or drag the gizmo to move or rotate.";
            SetStatus(_objectLoaderStatus, ToneOk);
        }

        void OpenObjectLoaderManager()
        {
            if (_objectLoaderSpawned.Count > 0) _objectLoaderSelected = _objectLoaderSpawned.Count - 1;
            _objectLoaderStatus = _objectLoaderSpawned.Count == 0 ? "No imported models spawned yet." : "Selected " + _objectLoaderSpawned[_objectLoaderSelected].name + ".";
            SetStatus(_objectLoaderStatus, _objectLoaderSpawned.Count == 0 ? ToneErr : ToneOk);
        }

        void DrawImportedModelControls()
        {
            if (_objectLoaderSpawned.Count == 0) return;
            GUILayout.Space(5f);
            GUILayout.Label("Imported models: " + _objectLoaderSpawned.Count, _titleStyle);
            for (int i = 0; i < _objectLoaderSpawned.Count; i++)
            {
                GameObject item = _objectLoaderSpawned[i];
                if (item == null) continue;
                bool selected = GUILayout.Toggle(_objectLoaderGizmoSelected && _objectLoaderSelected == i, item.name);
                if (selected && (!_objectLoaderGizmoSelected || _objectLoaderSelected != i))
                    SelectImportedObject(i);
            }
            GameObject active = _objectLoaderSelected >= 0 && _objectLoaderSelected < _objectLoaderSpawned.Count
                ? _objectLoaderSpawned[_objectLoaderSelected] : null;
            if (active == null) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left")) active.transform.position += Vector3.left * 0.25f;
            if (GUILayout.Button("Right")) active.transform.position += Vector3.right * 0.25f;
            if (GUILayout.Button("Forward")) active.transform.position += Vector3.forward * 0.25f;
            if (GUILayout.Button("Back")) active.transform.position += Vector3.back * 0.25f;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Up")) active.transform.position += Vector3.up * 0.25f;
            if (GUILayout.Button("Down")) active.transform.position += Vector3.down * 0.25f;
            if (GUILayout.Button("Smaller")) active.transform.localScale *= 0.9f;
            if (GUILayout.Button("Bigger")) active.transform.localScale *= 1.1f;
            if (GUILayout.Button("Remove"))
            {
                MeshFilter filter = active.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) UnityEngine.Object.Destroy(filter.sharedMesh);
                UnityEngine.Object.Destroy(active);
                _objectLoaderSpawned.RemoveAt(_objectLoaderSelected);
                _objectLoaderSelected = _objectLoaderSpawned.Count - 1;
                _objectLoaderGizmoSelected = false;
            }
            GUILayout.EndHorizontal();
        }

        void DrawWorldTab()
        {
            GUILayout.Label("Player travel", _titleStyle);
            if (_travel != null) { DrawActiveTravel(); GUILayout.Label(_workMessage, _mutedStyle); }
            else
            {
                GUILayout.Label("Choose a player, then teleport them to an area or camera, go to them, or bring them to you.", _mutedStyle);
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

        sealed class ServerSettingField
        {
            internal string Name, Label, Value = "";
            internal bool Toggle;
            internal CoordinateTextField Input;
            internal ServerSettingField(string name, string label, bool toggle)
            { Name=name; Label=label; Toggle=toggle; Input=new CoordinateTextField("Setting" + name); }
        }
        readonly ServerSettingField[] _serverSettings = new ServerSettingField[] {
            new ServerSettingField("DropAllOnDeath", "Drop all on death", true),
            new ServerSettingField("RespawnTimeSeconds", "Seconds before respawn", false),
            new ServerSettingField("TimeSpeedMultiplier", "Time speed multiplier", false),
            new ServerSettingField("XPX", "Experience multiplier", false),
            new ServerSettingField("DamageX", "Global damage multiplier", false),
            new ServerSettingField("IsPVPEnabled", "PVP damage enabled", true),
            new ServerSettingField("PVPMultiplier", "PVP damage multiplier", false),
            new ServerSettingField("PVPCrippleMultiplier", "PVP cripple multiplier", false),
            new ServerSettingField("HungerDealsDamage", "Hunger deals damage", true),
            new ServerSettingField("HungerTick", "Hunger tick rate", false),
            new ServerSettingField("CommunityStorageMultiplier", "Community storage multiplier", false),
        };
        readonly CoordinateTextField _broadcastInput = new CoordinateTextField("AdminBroadcast", false);
        readonly CoordinateTextField _spawnLocationInput = new CoordinateTextField("AdminSpawnLocation");
        string _broadcastMessage = "", _serverSpawnLocation = "", _settingsStatus = "";
        bool _settingsBusy;
        bool ServerSettingsFocused()
        {
            foreach (ServerSettingField field in _serverSettings) if (field.Input.IsFocused) return true;
            return false;
        }

        void DrawAdminTab()
        {
            GUILayout.Label("Time of day", _titleStyle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("AdminTimeField");
            _adminTime = GUILayout.TextField(_adminTime, GUILayout.MinWidth(90f));
            _adminTimeFocused = GUI.GetNameOfFocusedControl() == "AdminTimeField";
            GUI.enabled = !_adminBusy && !string.IsNullOrEmpty((_adminTime ?? "").Trim());
            if (GUILayout.Button("Set time", GUILayout.Width(72f))) SetAdminTime(_adminTime);
            if (GUILayout.Button("Noon", GUILayout.Width(62f))) SetAdminTime("12");
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Enter a 24-hour hour (0-23) or time like 18:30. 12 is noon.", _mutedStyle);
            GUILayout.Space(10f);
            GUILayout.Label("Player actions", _titleStyle);
            GUI.enabled = !_playersLoading && !_adminBusy;
            if (GUILayout.Button(_playersLoading ? "Refreshing..." : "Refresh online players")) RefreshPlayers();
            foreach (OnlinePlayer player in _onlinePlayers)
                if (GUILayout.Button((_adminKickPlayer == player.Name ? "Selected: " : "") + player.Name)) _adminKickPlayer = player.Name;
            if (_onlinePlayers.Count == 0) GUILayout.Label("No players loaded. Refresh the list.", _mutedStyle);
            GUI.enabled = !_adminBusy && !string.IsNullOrEmpty(_adminKickPlayer);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Time.unscaledTime < _adminKickUntil && _adminKickArmedPlayer == _adminKickPlayer ? "CONFIRM KICK" : "Kick player")) AdminKickClick();
            if (GUILayout.Button(Time.unscaledTime < _adminKillUntil && _adminKillArmedPlayer == _adminKickPlayer ? "CONFIRM KILL" : "Kill player")) AdminKillClick();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.Space(10f);
            GUILayout.Label("Broadcast to all online players", _titleStyle);
            _broadcastMessage = _broadcastInput.Draw(_broadcastMessage);
            GUILayout.Label("Shown in game for 10 seconds. Maximum 500 characters.", _mutedStyle);
            GUI.enabled = !_adminBusy && !string.IsNullOrEmpty(_broadcastMessage.Trim()) && _broadcastMessage.Length <= 500;
            if (GUILayout.Button("Send broadcast"))
            {
                _adminBusy = true; _adminStatus = "Sending broadcast...";
                _api.Broadcast(_broadcastMessage, AdminActionDone);
            }
            GUI.enabled = true;
            GUILayout.Space(10f);
            GUILayout.Label("Server spawn location", _titleStyle);
            GUILayout.Label("New arrivals and default respawns use this location. Personal homes are unchanged. Saved on the server across restarts.", _mutedStyle);
            GUILayout.Label("X,Y,Z - choose a safe spot on solid ground.", _mutedStyle);
            _serverSpawnLocation = _spawnLocationInput.Draw(_serverSpawnLocation);
            if (GUILayout.Button("Use my last refreshed player position"))
            {
                OnlinePlayer me = FindOnlinePlayer(CurrentUsername());
                if (me != null) _serverSpawnLocation = me.Position.x.ToString(CultureInfo.InvariantCulture) + "," + me.Position.y.ToString(CultureInfo.InvariantCulture) + "," + me.Position.z.ToString(CultureInfo.InvariantCulture);
                else _adminStatus = "Refresh players while your player is connected, then try again.";
            }
            if (GUILayout.Button("Paste coordinates")) _serverSpawnLocation = CoordinateTextField.CleanPaste(GUIUtility.systemCopyBuffer);
            GUI.enabled = !_adminBusy;
            if (GUILayout.Button("Set server spawn location"))
            {
                string[] parts = _serverSpawnLocation.Split(',');
                Vector3 position = Vector3.zero;
                if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out position.x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out position.y)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out position.z)
                    || float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)
                    || float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
                    _adminStatus = "Enter three finite numbers separated by commas: X,Y,Z.";
                else { _adminBusy = true; _adminStatus = "Saving server spawn..."; _api.SetServerSpawn(position, AdminActionDone); }
            }
            if (GUILayout.Button("Restore default server spawn"))
            { _adminBusy = true; _adminStatus = "Restoring default spawn..."; _api.ResetServerSpawn(AdminActionDone); }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_adminStatus)) GUILayout.Label(_adminStatus, _mutedStyle);

        }

        void AdminActionDone(EditResult result)
        {
            _adminBusy = false;
            _adminStatus = result.Ok ? (result.Note ?? "Server action completed.") : "Action failed: " + Reason(result);
        }

        void DrawServerSettingsTab()
        {
            GUILayout.Label("Server Settings", _titleStyle);
            GUILayout.Label("Changes apply immediately to the server. Current values are not read back; empty boxes do not mean zero. Enter a value and click Set.", _mutedStyle);
            foreach (ServerSettingField field in _serverSettings)
            {
                GUILayout.Space(6f);
                GUILayout.Label(field.Label);
                GUI.enabled = !_settingsBusy;
                GUILayout.BeginHorizontal();
                if (field.Toggle)
                {
                    if (GUILayout.Button("On")) ApplyServerSetting(field, "true");
                    if (GUILayout.Button("Off")) ApplyServerSetting(field, "false");
                }
                else
                {
                    field.Value = field.Input.Draw(field.Value);
                    if (GUILayout.Button("Set", GUILayout.Width(60f))) ApplyServerSetting(field, field.Value);
                }
                GUILayout.EndHorizontal();
                GUI.enabled = true;
            }
            if (!string.IsNullOrEmpty(_settingsStatus)) GUILayout.Label(_settingsStatus, _mutedStyle);
        }

        void ApplyServerSetting(ServerSettingField field, string value)
        {
            if (_settingsBusy) return;
            if (!field.Toggle)
            {
                float number;
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    || float.IsNaN(number) || float.IsInfinity(number) || number < 0f)
                { _settingsStatus = "Enter a non-negative number for " + field.Label + "."; return; }
                value = number.ToString("R", CultureInfo.InvariantCulture);
            }
            _settingsBusy = true;
            _settingsStatus = "Applying " + field.Label + "...";
            _api.SetServerSetting(field.Name, value, delegate(EditResult result)
            {
                _settingsBusy = false;
                _settingsStatus = result.Ok ? field.Label + ": sent " + value + ". " + Json.Str(result.Raw, "text")
                    : "Setting failed: " + Reason(result);
            });
        }

        void DrawInfoBoardsTab()
        {
            GUILayout.Label("Live info boards", _titleStyle);
            GUILayout.Label("Select an Info Board in the world to edit its server text and rotation.", _mutedStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_infoBoardsLoading ? "Loading registry..." : "Registered: " + _infoBoards.Count,
                _mutedStyle);
            GUILayout.FlexibleSpace();
            GUI.enabled = !_infoBoardsLoading && !_infoBoardSaving;
            if (GUILayout.Button("Refresh", GUILayout.Width(74f))) RefreshInfoBoards();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!_infoBoardsLoaded)
            {
                if (!string.IsNullOrEmpty(_infoBoardStatus)) GUILayout.Label(_infoBoardStatus, _mutedStyle);
                return;
            }
            if (!_infoBoardsConfigured)
            {
                GUILayout.Label("Info board editing needs the server-only LiveInfoBoards.dll. If you do not have it, find it on the modding Discord and install it on the server.", _panelOnlyStyle);
                GUILayout.Label("Restart the server once so it creates UserData/InfoBoards.json, then refresh here. If it is already installed, check the panel's INFOBOARDS_CONFIG path.", _mutedStyle);
                return;
            }

            string key = CurrentInfoBoardKey();
            if (string.IsNullOrEmpty(key))
            {
                GUILayout.Space(8f);
                GUILayout.Label(!_has ? "No object selected." : (_info == null
                    ? "Waiting for the selected object's server details..."
                    : "The selected prefab is not an Info Board."), _panelOnlyStyle);
                if (!string.IsNullOrEmpty(_infoBoardStatus)) GUILayout.Label(_infoBoardStatus, _mutedStyle);
                return;
            }

            if (_infoBoardFormKey != key)
            {
                InfoBoardEntry selected = FindInfoBoard(key);
                LoadInfoBoardForm(key, selected);
            }
            InfoBoardEntry board = FindInfoBoard(key);
            if (board == null)
            {
                GUILayout.Space(8f);
                GUILayout.Label("This board is not in the server registry yet.", _panelOnlyStyle);
                GUILayout.Label("Keep a player near it until LiveInfoBoards registers it, then refresh.", _mutedStyle);
                GUILayout.Label("Position key: " + key, _mutedStyle);
                if (!string.IsNullOrEmpty(_infoBoardStatus)) GUILayout.Label(_infoBoardStatus, _mutedStyle);
                return;
            }

            GUILayout.Space(5f);
            GUILayout.Label("Board channel: " + board.Channel, _connectedStyle);
            GUILayout.Label("Position key: " + key, _mutedStyle);
            GUILayout.Label(_infoBoardRotationEnabled ? "Fallback text (rotation off)" : "Text", _titleStyle);
            if (_infoBoardRotationEnabled)
                GUILayout.Label("The rotation messages below are shown while rotation is on.", _mutedStyle);
            _infoBoardText = DrawInfoBoardTextArea("BoardTextField", _infoBoardText,
                ref _infoBoardTextScroll, 82f, 190f, Mathf.Max(140f, _panelRect.width - 70f));
            _boardTextFocused = GUI.GetNameOfFocusedControl() == "BoardTextField";

            bool rotate = ThemedToggle(_infoBoardRotationEnabled, "Rotate messages automatically");
            if (rotate != _infoBoardRotationEnabled)
            {
                _infoBoardRotationEnabled = rotate;
                _infoBoardHasRotation = true;
                if (rotate && _infoBoardMessages.Count == 0 && !string.IsNullOrEmpty(_infoBoardText))
                {
                    _infoBoardMessages.Add(_infoBoardText);
                    _infoBoardMessageScrolls.Add(Vector2.zero);
                }
            }
            if (_infoBoardHasRotation || _infoBoardRotationEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Every", GUILayout.Width(42f));
                GUI.SetNextControlName("BoardIntervalField");
                _infoBoardIntervalText = GUILayout.TextField(_infoBoardIntervalText, GUILayout.Width(64f));
                _boardIntervalFocused = GUI.GetNameOfFocusedControl() == "BoardIntervalField";
                if (GUILayout.Button(_infoBoardUnit == "seconds" ? "SECONDS" : "Seconds", _infoBoardUnit == "seconds" ? _selectedTabStyle : _tabStyle))
                    _infoBoardUnit = "seconds";
                if (GUILayout.Button(_infoBoardUnit == "minutes" ? "MINUTES" : "Minutes", _infoBoardUnit == "minutes" ? _selectedTabStyle : _tabStyle))
                    _infoBoardUnit = "minutes";
                GUILayout.EndHorizontal();
                GUILayout.Label("Messages", _titleStyle);
                for (int i = 0; i < _infoBoardMessages.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    Vector2 messageScroll = _infoBoardMessageScrolls[i];
                    _infoBoardMessages[i] = DrawInfoBoardTextArea("BoardMessage" + i,
                        _infoBoardMessages[i], ref messageScroll, 54f, 150f,
                        Mathf.Max(120f, _panelRect.width - 104f));
                    _infoBoardMessageScrolls[i] = messageScroll;
                    if (GUI.GetNameOfFocusedControl() == "BoardMessage" + i) _boardMessageFocused = true;
                    GUI.enabled = !_infoBoardSaving;
                    if (GUILayout.Button("×", GUILayout.Width(30f)))
                    {
                        _infoBoardMessages.RemoveAt(i);
                        _infoBoardMessageScrolls.RemoveAt(i);
                        GUILayout.EndHorizontal();
                        break;
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
                GUI.enabled = !_infoBoardSaving && _infoBoardMessages.Count < 20;
                if (GUILayout.Button("+ Add rotation message"))
                {
                    _infoBoardMessages.Add("");
                    _infoBoardMessageScrolls.Add(Vector2.zero);
                }
                GUI.enabled = true;
                if (_infoBoardRotationEnabled && _infoBoardMessages.Count == 0)
                    GUILayout.Label("Add at least one message before enabling rotation.", _panelOnlyStyle);
            }

            GUILayout.Space(5f);
            int interval;
            bool intervalValid = int.TryParse(_infoBoardIntervalText, out interval) && interval >= 1 && interval <= 86400;
            bool canSave = !_infoBoardSaving && intervalValid
                && (!_infoBoardRotationEnabled || _infoBoardMessages.Count > 0)
                && _infoBoardText.Length <= 5000;
            GUI.enabled = canSave;
            if (GUILayout.Button(_infoBoardSaving ? "Saving..." : "Save board", GUILayout.Height(32f))) SaveInfoBoard(key, interval);
            GUI.enabled = true;
            if (!intervalValid && (_infoBoardHasRotation || _infoBoardRotationEnabled))
                GUILayout.Label("Interval must be from 1 to 86400.", _panelOnlyStyle);
            if (_infoBoardText.Length > 5000) GUILayout.Label("Board text limit is 5000 characters.", _panelOnlyStyle);
            if (!string.IsNullOrEmpty(_infoBoardStatus)) GUILayout.Label(_infoBoardStatus, _mutedStyle);
        }

        void SetAdminTime(string time)
        {
            if (_adminBusy) return;
            string value = (time ?? "").Trim();
            if (value.Length == 0) { _adminStatus = "Enter a time first."; return; }
            _adminBusy = true;
            _adminStatus = "Sending time change to server...";
            _api.SetTime(value, delegate(EditResult r)
            {
                _adminBusy = false;
                string response = Json.Str(r.Raw, "text");
                _adminStatus = r.Ok ? "Time command sent." + (string.IsNullOrEmpty(response) ? "" : " " + response)
                    : "Time change failed: " + Reason(r);
            });
        }

        void AdminKickClick()
        {
            if (_adminBusy || string.IsNullOrEmpty(_adminKickPlayer)) return;
            bool armed = Time.unscaledTime < _adminKickUntil && _adminKickArmedPlayer == _adminKickPlayer;
            if (!armed)
            {
                _adminKickArmedPlayer = _adminKickPlayer;
                _adminKickUntil = Time.unscaledTime + DeleteArmSeconds;
                _adminStatus = "Click confirm within 6 seconds to kick " + _adminKickPlayer + ".";
                return;
            }
            _adminKickUntil = 0f;
            string player = _adminKickPlayer;
            _adminBusy = true;
            _adminStatus = "Kicking " + player + "...";
            _api.KickPlayer(player, delegate(EditResult r)
            {
                _adminBusy = false;
                _adminStatus = r.Ok ? (r.Note ?? ("Kicked " + player + ".")) : "Kick failed: " + Reason(r);
                if (r.Ok) RefreshPlayers();
            });
        }

        void AdminKillClick()
        {
            if (_adminBusy || string.IsNullOrEmpty(_adminKickPlayer)) return;
            bool armed = Time.unscaledTime < _adminKillUntil && _adminKillArmedPlayer == _adminKickPlayer;
            if (!armed)
            {
                _adminKillArmedPlayer = _adminKickPlayer;
                _adminKillUntil = Time.unscaledTime + DeleteArmSeconds;
                _adminStatus = "Click confirm within 6 seconds to kill " + _adminKickPlayer + ".";
                return;
            }
            string player = _adminKickPlayer;
            _adminKillUntil = 0f;
            _adminBusy = true;
            _adminStatus = "Killing " + player + "...";
            _api.KillPlayer(player, delegate(EditResult r)
            {
                _adminBusy = false;
                _adminStatus = r.Ok ? (r.Note ?? ("Killed " + player + ".")) : "Kill failed: " + Reason(r);
                if (r.Ok) RefreshPlayers();
            });
        }

        void AdminBanClick()
        {
            if (_adminBusy || string.IsNullOrEmpty(_adminKickPlayer)) return;
            int hours;
            if (!int.TryParse(_banHours, out hours) || hours < 0 || hours > 87600)
            { _adminStatus = "Duration must be 0 (permanent) or between 1 and 87600 hours."; return; }
            bool armed = Time.unscaledTime < _adminBanUntil && _adminBanArmedPlayer == _adminKickPlayer;
            if (!armed)
            {
                _adminBanArmedPlayer = _adminKickPlayer;
                _adminBanUntil = Time.unscaledTime + DeleteArmSeconds;
                _adminStatus = "Click confirm within 6 seconds to blacklist " + _adminKickPlayer + ".";
                return;
            }
            string player = _adminKickPlayer;
            string reason = (_banReason ?? "").Trim();
            if (reason.Length > 300) { _adminStatus = "Reason must be 300 characters or fewer."; return; }
            _adminBanUntil = 0f;
            _adminBusy = true;
            _adminStatus = "Saving blacklist entry and disconnecting " + player + "...";
            _api.BlacklistPlayer(player, hours, reason, delegate(EditResult r)
            {
                _adminBusy = false;
                _adminStatus = r.Ok ? (r.Note ?? ("Blacklisted " + player + ".")) : "Blacklist failed: " + Reason(r);
                RefreshBlacklist();
                if (r.Ok) RefreshPlayers();
            });
        }

        void AdminUnbanClick(int identifier)
        {
            if (_adminBusy || identifier <= 0) return;
            bool armed = Time.unscaledTime < _adminUnbanUntil && _adminUnbanArmedId == identifier;
            if (!armed)
            {
                _adminUnbanArmedId = identifier;
                _adminUnbanUntil = Time.unscaledTime + DeleteArmSeconds;
                _blacklistStatus = "Click confirm within 6 seconds to remove ID " + identifier + ".";
                return;
            }
            _adminUnbanUntil = 0f;
            _adminBusy = true;
            _blacklistStatus = "Removing blacklist entry...";
            _api.Unblacklist(identifier, delegate(EditResult r)
            {
                _adminBusy = false;
                _blacklistStatus = r.Ok ? (r.Note ?? "Blacklist entry removed.") : "Unblacklist failed: " + Reason(r);
                RefreshBlacklist();
            });
        }

        void RefreshBlacklist()
        {
            if (_blacklistLoading) return;
            _blacklistLoading = true;
            _blacklistStatus = "Reading server blacklist...";
            _api.Blacklist(delegate(EditResult r)
            {
                _blacklistLoading = false;
                _blacklistEntries.Clear();
                string data = Json.Value(r.Raw, "data");
                string bans = data == null ? null : Json.Value(data, "bans");
                if (r.Ok && bans != null)
                {
                    List<string> rows = Json.Objects(bans);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int id = (int)Json.Num(rows[i], "identifier", 0f);
                        if (id <= 0) continue;
                        _blacklistEntries.Add(new BlacklistEntry {
                            Identifier = id,
                            Name = Json.Str(rows[i], "name") ?? "Unknown",
                            Reason = Json.Str(rows[i], "reason") ?? "",
                            Expires = Json.Str(rows[i], "expires") ?? "Unknown"
                        });
                    }
                    _blacklistStatus = _blacklistEntries.Count == 0 ? "No blacklisted accounts." : "Blacklist loaded.";
                }
                else _blacklistStatus = "Could not read blacklist: " + Reason(r);
            });
        }

        void StartWatching(string playerName)
        {
            if (Camera == null || string.IsNullOrEmpty(playerName)) return;
            OnlinePlayer target = FindOnlinePlayer(playerName);
            if (target == null)
            {
                _adminStatus = "Refresh the online player list before starting watch.";
                RefreshPlayers();
                return;
            }

            object current = null;
            object manager = null;
            Vector3 localPosition;
            try
            {
                current = Player.Current;
                manager = ReadMember(current, "ChunkManager");
            }
            catch (Exception e) { _adminStatus = "Watch unavailable: " + e.Message; return; }
            if (manager == null || !TryPlayerPosition(current, out localPosition))
            {
                _adminStatus = "Watch unavailable: could not read your avatar's position or chunk manager.";
                return;
            }

            string error;
            if (!LoadValidatedChunk(manager, target.Position, out error))
            {
                _adminStatus = "Watch could not load that area safely: " + error;
                return;
            }

            _watchChunkManager = manager;
            _watchLocalPosition = localPosition;
            _watchLastChunkPosition = target.Position;
            _watchSavedCameraPosition = Camera.transform.position;
            _watchSavedCameraRotation = Camera.transform.rotation;
            _watchTarget = target;
            _watchActive = true;
            _watchNextPoll = Time.unscaledTime + 1.5f;
            _watchNextChunkLoad = Time.unscaledTime + 1.5f;
            _adminStatus = "Quiet camera watch started for " + target.Name + ". Your avatar was not moved.";
            FollowWatchTarget(true);
        }

        void StopWatching()
        {
            if (!_watchActive)
            {
                _watchTarget = null;
                _watchChunkManager = null;
                return;
            }
            _watchActive = false;
            string loadError = "local chunk manager is unavailable";
            bool restored = _watchChunkManager != null
                && LoadValidatedChunk(_watchChunkManager, _watchLocalPosition, out loadError);
            if (_watchChunkManager == null) loadError = "local chunk manager is no longer available";
            if (Camera != null)
            {
                Camera.transform.position = _watchSavedCameraPosition;
                Camera.transform.rotation = _watchSavedCameraRotation;
            }
            string stoppedPlayer = _watchTarget == null ? "player" : _watchTarget.Name;
            _watchTarget = null;
            _watchChunkManager = null;
            _adminStatus = restored ? "Stopped watching " + stoppedPlayer + "; local area restored."
                : "Stopped watching " + stoppedPlayer + "; could not re-request the local area: " + loadError;
        }

        void TickWatch()
        {
            if (!_watchActive) return;
            if (Camera == null || _watchTarget == null || _watchChunkManager == null)
            { StopWatching(); return; }
            if (Time.unscaledTime >= _watchNextPoll && !_playersLoading)
            {
                _watchNextPoll = Time.unscaledTime + 1.5f;
                RefreshPlayers();
            }
            if (Time.unscaledTime >= _watchNextChunkLoad
                && Vector3.Distance(_watchTarget.Position, _watchLastChunkPosition) > 20f)
            {
                _watchNextChunkLoad = Time.unscaledTime + 1.5f;
                string error;
                if (!LoadValidatedChunk(_watchChunkManager, _watchTarget.Position, out error))
                {
                    StopWatching();
                    _adminStatus = "Watch stopped safely: could not stream the player's next area: " + error;
                    return;
                }
                _watchLastChunkPosition = _watchTarget.Position;
            }
            FollowWatchTarget(false);
        }

        void FollowWatchTarget(bool immediate)
        {
            if (Camera == null || _watchTarget == null) return;
            Transform camera = Camera.transform;
            Quaternion facing = Quaternion.Euler(0f, _watchTarget.Yaw, 0f);
            Vector3 lookPoint = _watchTarget.Position + Vector3.up * 1.45f;
            Vector3 desiredPosition = _watchTarget.Position - facing * Vector3.forward * 3.2f + Vector3.up * 1.7f;
            Vector3 direction = lookPoint - desiredPosition;
            if (direction.sqrMagnitude < 0.01f) direction = facing * Vector3.forward;
            Quaternion desiredRotation = Quaternion.LookRotation(direction, Vector3.up);
            if (immediate)
            {
                camera.position = desiredPosition;
                camera.rotation = desiredRotation;
            }
            else
            {
                float blend = Mathf.Clamp01(Time.unscaledDeltaTime * 5f);
                camera.position = Vector3.Lerp(camera.position, desiredPosition, blend);
                camera.rotation = Quaternion.Slerp(camera.rotation, desiredRotation, blend);
            }
        }

        OnlinePlayer FindOnlinePlayer(string name)
        {
            for (int i = 0; i < _onlinePlayers.Count; i++)
                if (string.Equals(_onlinePlayers[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _onlinePlayers[i];
            return null;
        }

        static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = instance.GetType();
            try
            {
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance, null);
            }
            catch { }
            try
            {
                Type[] interfaces = type.GetInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    PropertyInfo property = interfaces[i].GetProperty(name);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(instance, null);
                }
            }
            catch { }
            try
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(instance);
            }
            catch { }
            return null;
        }

        static bool TryPlayerPosition(object player, out Vector3 position)
        {
            position = Vector3.zero;
            if (player == null) return false;
            object controller = ReadMember(player, "PlayerController");
            if (controller == null) controller = ReadMember(player, "Controller");
            object[] candidates = new object[] { controller, player };
            for (int i = 0; i < candidates.Length; i++)
            {
                object candidate = candidates[i];
                object value = ReadMember(candidate, "Position");
                if (value is Vector3) { position = (Vector3)value; return true; }
                object transform = ReadMember(candidate, "transform");
                if (transform == null) transform = ReadMember(candidate, "Transform");
                value = ReadMember(transform, "position");
                if (value is Vector3) { position = (Vector3)value; return true; }
            }
            return false;
        }

        static bool LoadValidatedChunk(object manager, Vector3 position, out string error)
        {
            error = "chunk API is unavailable";
            if (manager == null) { error = "player chunk manager is unavailable"; return false; }
            try
            {
                Type helper = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && helper == null; i++)
                    try { helper = assemblies[i].GetType("Alta.Chunks.LocationChunkHelper", false); } catch { }
                if (helper == null) return false;
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo ensure = helper.GetMethod("EnsureChunkLookupIsSetup", staticFlags, null, Type.EmptyTypes, null);
                if (ensure == null) { error = "chunk lookup setup method is unavailable"; return false; }
                ensure.Invoke(null, null);

                object currentChunk = ReadMember(manager, "Chunk");
                MethodInfo[] helperMethods = helper.GetMethods(staticFlags);
                bool found = false;
                for (int i = 0; i < helperMethods.Length; i++)
                {
                    MethodInfo method = helperMethods[i];
                    if (method.Name != "GetBestContaining") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Vector3)) continue;
                    if (currentChunk != null && !parameters[1].ParameterType.IsInstanceOfType(currentChunk)) continue;
                    object chunk = method.Invoke(null, new object[] { position, currentChunk });
                    if (chunk != null) { found = true; break; }
                }
                if (!found) { error = "no loaded map chunk contains that player position"; return false; }

                MethodInfo load = null;
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                for (Type type = manager.GetType(); type != null && load == null; type = type.BaseType)
                {
                    MethodInfo[] methods = type.GetMethods(instanceFlags | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        ParameterInfo[] parameters = methods[i].GetParameters();
                        if (methods[i].Name == "LoadFromPosition" && parameters.Length == 1
                            && parameters[0].ParameterType == typeof(Vector3)) { load = methods[i]; break; }
                    }
                }
                if (load == null) { error = "player chunk streaming method is unavailable"; return false; }
                load.Invoke(manager, new object[] { position });
                error = null;
                return true;
            }
            catch (Exception e)
            {
                Exception reason = e;
                if (reason is TargetInvocationException && reason.InnerException != null) reason = reason.InnerException;
                error = reason.Message;
                return false;
            }
        }

        void AdminTeleportClick(string from, string to)
        {
            if (_adminBusy || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
            bool armed = Time.unscaledTime < _adminTeleportUntil
                && _adminTeleportArmedFrom == from && _adminTeleportArmedTo == to;
            if (!armed)
            {
                _adminTeleportArmedFrom = from;
                _adminTeleportArmedTo = to;
                _adminTeleportUntil = Time.unscaledTime + DeleteArmSeconds;
                _adminStatus = "Click confirm within 6 seconds to move " + from + " beside " + to + ".";
                return;
            }
            _adminTeleportUntil = 0f;
            _adminBusy = true;
            _adminStatus = "Teleporting " + from + " beside " + to + "...";
            _api.TeleportPlayer(from, to, delegate(EditResult r)
            {
                _adminBusy = false;
                _adminStatus = r.Ok ? (r.Note ?? ("Teleported " + from + " beside " + to + "."))
                    : "Teleport failed: " + Reason(r);
                if (r.Ok) RefreshPlayers();
            });
        }

        void RefreshInfoBoards()
        {
            if (_infoBoardsLoading) return;
            _infoBoardsLoading = true;
            _infoBoardStatus = "Reading server board registry...";
            _api.InfoBoards(delegate(EditResult r)
            {
                _infoBoardsLoading = false;
                _infoBoards.Clear();
                if (!r.Ok)
                {
                    _infoBoardsLoaded = false;
                    _infoBoardStatus = "Could not read board registry: " + Reason(r);
                    return;
                }
                _infoBoardsLoaded = true;
                _infoBoardsConfigured = Json.Bool(r.Raw, "configured", false);
                string rows = Json.Value(r.Raw, "boards");
                List<string> entries = Json.Objects(rows);
                for (int i = 0; i < entries.Count; i++)
                {
                    string row = entries[i];
                    InfoBoardEntry entry = new InfoBoardEntry();
                    entry.Key = Json.Str(row, "key") ?? "";
                    entry.Channel = Json.Str(row, "channel") ?? "";
                    entry.Text = Json.Str(row, "text") ?? "";
                    string rotation = Json.Value(row, "rotation");
                    if (rotation != null && rotation != "null")
                    {
                        entry.HasRotation = true;
                        entry.RotationEnabled = Json.Bool(rotation, "enabled", false);
                        entry.Interval = (int)Json.Num(rotation, "interval", 2f);
                        entry.Unit = Json.Str(rotation, "unit") ?? "minutes";
                        string messages = Json.Value(rotation, "messages");
                        entry.Messages.AddRange(Json.StrArray(messages));
                    }
                    _infoBoards.Add(entry);
                }
                _infoBoardFormKey = "";
                string selectedKey = CurrentInfoBoardKey();
                if (!string.IsNullOrEmpty(selectedKey)) LoadInfoBoardForm(selectedKey, FindInfoBoard(selectedKey));
                _infoBoardStatus = _infoBoardsConfigured
                    ? "Board list refreshed from the server." : "No InfoBoards.json found. This tab requires LiveInfoBoards.dll from the modding Discord.";
            });
        }

        void LoadInfoBoardForm(string key, InfoBoardEntry entry)
        {
            _infoBoardFormKey = key;
            _infoBoardText = entry == null ? "" : entry.Text;
            _infoBoardTextScroll = Vector2.zero;
            _infoBoardHasRotation = entry != null && entry.HasRotation;
            _infoBoardRotationEnabled = _infoBoardHasRotation && entry.RotationEnabled;
            _infoBoardIntervalText = _infoBoardHasRotation ? entry.Interval.ToString(CultureInfo.InvariantCulture) : "2";
            _infoBoardUnit = _infoBoardHasRotation && entry.Unit == "seconds" ? "seconds" : "minutes";
            _infoBoardMessages.Clear();
            _infoBoardMessageScrolls.Clear();
            if (entry != null)
            {
                _infoBoardMessages.AddRange(entry.Messages);
                for (int i = 0; i < entry.Messages.Count; i++) _infoBoardMessageScrolls.Add(Vector2.zero);
            }
        }

        string DrawInfoBoardTextArea(string controlName, string text, ref Vector2 scroll,
            float minimumHeight, float maximumViewportHeight, float width)
        {
            string value = text ?? "";
            float contentHeight = GUI.skin.textArea.CalcHeight(new GUIContent(value), Mathf.Max(120f, width)) + 8f;
            contentHeight = Mathf.Max(minimumHeight, contentHeight);
            float viewportHeight = Mathf.Min(maximumViewportHeight, contentHeight);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandWidth(true), GUILayout.Height(viewportHeight));
            GUI.SetNextControlName(controlName);
            value = GUILayout.TextArea(value, GUILayout.ExpandWidth(true), GUILayout.Height(contentHeight));
            GUILayout.EndScrollView();
            return value;
        }

        InfoBoardEntry FindInfoBoard(string key)
        {
            for (int i = 0; i < _infoBoards.Count; i++)
                if (string.Equals(_infoBoards[i].Key, key, StringComparison.Ordinal)) return _infoBoards[i];
            return null;
        }

        string CurrentInfoBoardKey()
        {
            if (!_has || _root == null || _info == null || _info.Id != _id || string.IsNullOrEmpty(_info.Prefab)
                || !IsInfoBoardPrefab(_info.Prefab)) return "";
            return InfoBoardPositionKey(new Vector3(_info.X, _info.Y, _info.Z));
        }

        static bool IsInfoBoardPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            StringBuilder normalized = new StringBuilder(prefab.Length);
            for (int i = 0; i < prefab.Length; i++)
            {
                char c = prefab[i];
                if (Char.IsLetterOrDigit(c)) normalized.Append(Char.ToLowerInvariant(c));
            }
            return normalized.ToString().IndexOf("infoboard", StringComparison.Ordinal) >= 0;
        }

        static string InfoBoardPositionKey(Vector3 position)
        {
            return Math.Round(position.x, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + ","
                + Math.Round(position.y, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + ","
                + Math.Round(position.z, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
        }

        void SaveInfoBoard(string key, int interval)
        {
            if (_infoBoardSaving || FindInfoBoard(key) == null) return;
            if (_infoBoardRotationEnabled && _infoBoardMessages.Count == 0)
            { _infoBoardStatus = "Add at least one message before enabling rotation."; return; }
            for (int i = 0; i < _infoBoardMessages.Count; i++)
                if (string.IsNullOrEmpty(_infoBoardMessages[i]) || _infoBoardMessages[i].Length > 5000)
                { _infoBoardStatus = "Rotation messages must contain 1-5000 characters."; return; }
            _infoBoardSaving = true;
            _infoBoardStatus = "Saving board...";
            _api.UpdateInfoBoard(key, _infoBoardText, _infoBoardHasRotation || _infoBoardRotationEnabled,
                _infoBoardRotationEnabled, interval, _infoBoardUnit, _infoBoardMessages, delegate(EditResult r)
                {
                    _infoBoardSaving = false;
                    _infoBoardStatus = r.Ok ? "Saved. LiveInfoBoards will apply it within about half a second."
                        : "Save failed: " + Reason(r);
                    if (r.Ok) RefreshInfoBoards();
                });
        }

        string CurrentUsername()
        {
            try
            {
                if (Player.Current != null && Player.Current.UserInfo != null)
                    return Player.Current.UserInfo.Username ?? "";
            }
            catch { }
            return "";
        }

        // Recovery for old trips or a failed home restore; never moves the avatar.
        void DrawActiveTravel()
        {
            GUILayout.Label("Saved home restore pending for " + _travel.Player, _mutedStyle);
            GUI.enabled = !_workBusy;
            if (GUILayout.Button("Restore saved home"))
            { _workBusy = true; RestoreOriginalHome(); }
            GUI.enabled = true;
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
            bool valid = !string.IsNullOrEmpty(_selectedPlayer);
            bool area = _selectedArea >= 0 && _selectedArea < _workAreas.Count;
            GUILayout.Label("TP destination: " + (area ? _workAreas[_selectedArea].Name : "editor camera"), _mutedStyle);
            GUI.enabled = valid && !_workBusy && (area || Camera != null);
            if (GUILayout.Button("TP user to " + (area ? "area" : "camera"))) VisitWorkArea();
            GUI.enabled = valid && !_workBusy && Camera != null;
            if (area && GUILayout.Button("TP user to camera")) TeleportSelectedTo(Camera.transform.position);
            string me = CurrentUsername();
            GUI.enabled = valid && !_workBusy && !string.IsNullOrEmpty(me) && me != _selectedPlayer;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("TP to user")) TeleportBetweenPlayers(me, _selectedPlayer);
            if (GUILayout.Button("TP user to me")) TeleportBetweenPlayers(_selectedPlayer, me);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (_workBusy) GUILayout.Label("Teleport command in progress...", _mutedStyle);
            GUILayout.Space(6f);
            if (string.IsNullOrEmpty(_keepAlivePlayer))
            {
                GUI.enabled = valid && !_keepAliveBusy;
                if (GUILayout.Button("Keep selected player active every 5 minutes"))
                {
                    _keepAlivePlayer = _selectedPlayer;
                    _keepAliveDirection = 1;
                    _keepAliveNextAt = Time.unscaledTime + 300f;
                    _keepAliveGeneration++;
                }
                GUI.enabled = true;
            }
            else
            {
                int seconds = Mathf.Max(0, Mathf.CeilToInt(_keepAliveNextAt - Time.unscaledTime));
                GUILayout.Label("Keeping " + _keepAlivePlayer + " active. Next nudge: " + (seconds / 60) + ":" + (seconds % 60).ToString("00"), _mutedStyle);
                if (GUILayout.Button("Stop keeping player active")) StopKeepAlive();
            }
            GUILayout.Label("Moves the chosen player 0.12 m left/right every 5 minutes. Home stays unchanged. Keep F1 editor open; closing it stops nudges.", _mutedStyle);
        }

        public void StopKeepAlive()
        {
            _keepAlivePlayer = "";
            _keepAliveGeneration++;
        }

        void TickKeepAlive()
        {
            if (string.IsNullOrEmpty(_keepAlivePlayer) || _keepAliveBusy || _workBusy
                || Time.unscaledTime < _keepAliveNextAt) return;
            string player = _keepAlivePlayer;
            int generation = _keepAliveGeneration;
            _keepAliveBusy = true;
            _api.NudgePlayer(player, _keepAliveDirection, delegate(EditResult r)
            {
                _keepAliveBusy = false;
                if (generation != _keepAliveGeneration) return;
                if (!r.Ok)
                {
                    StopKeepAlive();
                    _workMessage = "Keep-alive stopped for " + player + ": " + Reason(r);
                    return;
                }
                _keepAliveDirection = -_keepAliveDirection;
                _keepAliveNextAt = Time.unscaledTime + 300f;
                _workMessage = player + " nudged. Next movement in 5 minutes.";
            });
        }

        bool IsTradeDeckSelected()
        {
            return _has && _id != 0 && _group.Count == 1 && _root != null
                && ((_info != null && _info.Hash == 30016)
                    || _root.GetComponentInChildren<Alta.Trading.TradeVendor>(true) != null);
        }

        void ResetTradeForm()
        {
            _tradeGeneration++;
            _tradeId = 0; _tradeHash = 0; _tradeBusy = false; _tradeLoaded = false;
            _tradeQuery = ""; _tradeItemName = ""; _tradePrice = "1"; _tradeQuantity = "1"; _tradeStock = "1"; _tradeAddStock = "1"; _tradeStatus = "";
            _tradeSearchInput.Stop(); _tradePriceInput.Stop(); _tradeQuantityInput.Stop(); _tradeStockInput.Stop(); _tradeAddStockInput.Stop();
        }

        void LoadTradeDeck()
        {
            uint id = _id;
            int generation = ++_tradeGeneration;
            _tradeId = id; _tradeBusy = true; _tradeLoaded = false;
            _tradeStatus = "Reading trade deck...";
            _api.TradeInfo(id, delegate(EditResult r) {
                if (generation != _tradeGeneration || !_has || _id != id) return;
                _tradeBusy = false;
                ReadTradeReply(r, "Loaded current settings.");
            });
        }

        static int TradeInteger(string data, string key)
        {
            int value;
            return int.TryParse(Json.Value(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        void ReadTradeReply(EditResult r, string success)
        {
            string data = Json.Value(r.Raw, "data");
            string trade = Json.Value(data, "trade");
            if (!r.Ok || trade == null)
            {
                _tradeLoaded = false;
                _tradeStatus = r.Ok ? "The server did not return trade settings. Update the Server Kit and restart the game server and panel." : "Trade deck failed: " + Reason(r);
                if (r.Err != null && r.Err.Contains("no such route"))
                    _tradeStatus = "Update the Server Kit, then restart the game server and panel to enable trade deck editing.";
                return;
            }
            _tradeLoaded = true;
            _tradeHash = Json.UInt(trade, "itemHash");
            _tradeItemName = Json.Str(trade, "itemName") ?? "";
            _tradeQuery = _tradeHash == 0 ? "" : _tradeHash.ToString(CultureInfo.InvariantCulture);
            _tradePrice = Math.Max(1, TradeInteger(trade, "price")).ToString(CultureInfo.InvariantCulture);
            _tradeQuantity = Math.Max(1, TradeInteger(trade, "perPurchase")).ToString(CultureInfo.InvariantCulture);
            _tradeStock = Math.Max(1, TradeInteger(trade, "stock")).ToString(CultureInfo.InvariantCulture);
            if (_tradeHash == 0) { _tradeQuantity = "1"; _tradeStock = "1"; }
            _tradeStatus = success + " Current stock: " + TradeInteger(trade, "stock") + ".";
            if (Json.Num(trade, "owner", 0) == 0) _tradeStatus += " Claim this deck in-game before selling.";
        }

        void DrawTradeDeckControls()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Trading", _titleStyle);
            if (!IsTradeDeckSelected()) {
                GUILayout.Label("Click one trade deck (prefab 30016) in the world, then edit it here.", _mutedStyle);
                if (_group.Count > 1) GUILayout.Label("Select only one deck at a time.", _mutedStyle);
                return;
            }
            GUILayout.Label("Selected trade deck #" + _id, _mutedStyle);
            if (_tradeId != _id) { ResetTradeForm(); LoadTradeDeck(); }
            GUILayout.Label(_tradeBusy ? "Reading or updating the game server..." : _tradeStatus, _mutedStyle);
            GUI.enabled = !_tradeBusy;
            if (GUILayout.Button("Reload current deck settings")) LoadTradeDeck();
            GUI.enabled = true;
            if (!_tradeLoaded && !_tradeBusy)
                GUILayout.Label("You can enter a new offer below. Apply sends it to the server for validation. Adding stock needs a successful reload first.", _mutedStyle);
            if (!_spawnCatalogAttempted && !_spawnBusy)
            {
                _spawnCatalogAttempted = true; _spawnBusy = true; _spawnCatalogLoading = true;
                _api.Prefabs(OnPrefabs);
            }
            GUILayout.Label("Item to sell - search by name or enter a prefab ID:", _mutedStyle);
            GUI.enabled = !_tradeBusy;
            string query = _tradeSearchInput.Draw(_tradeQuery);
            if (query != _tradeQuery) {
                _tradeQuery = query; _tradeItemName = "";
                if (!uint.TryParse(query.Trim(), out _tradeHash)) _tradeHash = 0;
            }
            int shown = 0;
            if (_tradeHash == 0 && !string.IsNullOrEmpty(_tradeQuery.Trim()))
                foreach (SpawnCatalogEntry entry in _spawnCatalog)
                {
                    if (entry.Name.IndexOf(_tradeQuery.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (GUILayout.Button(entry.Name + "  #" + entry.Hash)) {
                        _tradeHash = entry.Hash; _tradeItemName = entry.Name;
                        _tradeQuery = entry.Hash.ToString(CultureInfo.InvariantCulture);
                        GUI.FocusControl(null); _tradeSearchInput.Stop();
                    }
                    if (++shown >= 6) break;
                }
            if (_tradeHash != 0) GUILayout.Label("Item: " + _tradeItemName + "  #" + _tradeHash, _mutedStyle);
            else if (shown == 0 && _tradeQuery.Length > 0) GUILayout.Label("No matches yet. You can enter the item's prefab ID directly.", _mutedStyle);
            GUILayout.Label("Price in coins per purchase:", _mutedStyle);
            _tradePrice = _tradePriceInput.Draw(_tradePrice);
            GUILayout.Label("Items given per purchase:", _mutedStyle);
            _tradeQuantity = _tradeQuantityInput.Draw(_tradeQuantity);
            GUILayout.Label("Total stock available (sets the total, not an amount to add):", _mutedStyle);
            _tradeStock = _tradeStockInput.Draw(_tradeStock);
            int price, quantity, stock;
            bool valid = _tradeHash != 0 && int.TryParse(_tradePrice, out price) && price > 0 && price <= 1000000
                && int.TryParse(_tradeQuantity, out quantity) && quantity > 0
                && int.TryParse(_tradeStock, out stock) && stock >= quantity;
            if (!_tradeBusy && !valid)
                GUILayout.Label("Choose an item and enter whole numbers: coins 1-1000000, positive quantity, and stock at least the purchase quantity.", _mutedStyle);
            GUI.enabled = !_tradeBusy && valid;
            if (GUILayout.Button("Apply item, price and stock to selected deck")) ApplyTradeDeck();
            GUI.enabled = !_tradeBusy && _tradeLoaded;
            GUILayout.Label("Add more stock (keeps the current price and purchase quantity):", _mutedStyle);
            _tradeAddStock = _tradeAddStockInput.Draw(_tradeAddStock);
            int added;
            GUI.enabled = !_tradeBusy && _tradeLoaded && _tradeHash != 0
                && int.TryParse(_tradeAddStock, out added) && added > 0;
            if (GUILayout.Button("Add this many to current stock")) AddTradeStock();
            GUI.enabled = true;
            GUILayout.Label("Empty the deck's stock in-game before changing to a different item. Non-stackable items use 1 per purchase. The game enforces deck capacity. Changes are server-side and saved with the deck.", _mutedStyle);

        }

        void AddTradeStock()
        {
            if (_tradeBusy || !_tradeLoaded || !IsTradeDeckSelected() || _tradeId != _id) return;
            int amount;
            if (!int.TryParse(_tradeAddStock, out amount) || amount < 1) return;
            uint id = _id;
            int generation = ++_tradeGeneration;
            _tradeBusy = true;
            _api.TradeAddStock(id, _tradeHash, amount, delegate(EditResult r) {
                if (generation != _tradeGeneration || !_has || _id != id) return;
                _tradeBusy = false;
                ReadTradeReply(r, "Added " + amount + " items; price and items per purchase unchanged.");
            });
        }

        void ApplyTradeDeck()
        {
            if (_tradeBusy || !IsTradeDeckSelected() || _tradeId != _id) return;
            int price, quantity, stock;
            if (!int.TryParse(_tradePrice, out price) || !int.TryParse(_tradeQuantity, out quantity) || !int.TryParse(_tradeStock, out stock)) return;
            uint id = _id;
            int generation = ++_tradeGeneration;
            _tradeBusy = true;
            _api.TradeSet(id, _tradeHash, price, quantity, stock, delegate(EditResult r) {
                if (generation != _tradeGeneration || !_has || _id != id) return;
                _tradeBusy = false;
                ReadTradeReply(r, "Trade deck updated for everyone.");
            });
        }

        void DrawTeleporterControls()
        {
            GUILayout.Label("Teleporter destination", _titleStyle);
            GUILayout.Label("Spawn and place your teleporter, then click it in the world to select it. Enter its destination below and click Link.", _mutedStyle);
            GUILayout.Label("Destination X,Y,Z (click to type):", _mutedStyle);
            _teleporterDestination = _teleporterInput.Draw(_teleporterDestination);
            _teleporterDestinationFocused = GUI.GetNameOfFocusedControl() == "TeleporterDestination";
            if (GUILayout.Button("Paste coordinates"))
            {
                _teleporterInput.Stop();
                _teleporterDestination = CoordinateTextField.CleanPaste(GUIUtility.systemCopyBuffer);
                GUI.FocusControl(null);
                _teleporterDestinationFocused = false;
            }
            GUILayout.Label("Type or paste X,Y,Z, then click Link to apply. Ctrl+A selects all; Backspace deletes.", _mutedStyle);
            GUILayout.Label(_has && _id != 0 ? "Selected entity #" + _id : "No teleporter selected. Click your teleporter in the world first.", _mutedStyle);
            GUI.enabled = !_teleporterBusy && _has && _id != 0 && _group.Count == 1;
            if (GUILayout.Button(_teleporterBusy ? "Linking..." : "Link selected teleporter to destination")) ConfigureTeleporter();
            GUI.enabled = true;
            if (_group.Count > 1) GUILayout.Label("Select one teleporter at a time.", _mutedStyle);
            GUILayout.Label("If you move the teleporter afterward, link it again to keep these exact destination coordinates.", _mutedStyle);
            GUILayout.Label("Use its normal in-game teleport interaction. The game streams destination chunks. Platform links are saved on the server; relink after moving the platform.", _mutedStyle);
            if (!string.IsNullOrEmpty(_teleporterStatus)) GUILayout.Label(_teleporterStatus, _mutedStyle);
        }

        void ConfigureTeleporter()
        {
            if (_teleporterBusy || !_has || _id == 0 || _group.Count != 1) return;
            string[] values = _teleporterDestination.Split(',');
            Vector3 destination = Vector3.zero;
            if (values.Length != 3 || !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out destination.x)
                || !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out destination.y)
                || !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out destination.z)
                || float.IsNaN(destination.x) || float.IsNaN(destination.y) || float.IsNaN(destination.z)
                || float.IsInfinity(destination.x) || float.IsInfinity(destination.y) || float.IsInfinity(destination.z))
            { _teleporterStatus = "Enter three coordinates separated by commas, for example -596.447,275.657,240.869"; return; }
            _teleporterBusy = true;
            _teleporterStatus = "Linking selected teleporter...";
            Action<EditResult> done = delegate(EditResult r)
            {
                _teleporterBusy = false;
                _teleporterStatus = r.Ok ? "Teleporter linked to " + VecText(destination) + "." : "Teleporter failed: " + Reason(r);
                if (r.Ok && r.Ent != null) _teleporterStatus += " Entity #" + r.Ent.Id + ".";
            };
            _api.LinkTeleporter(_id, destination.x, destination.y, destination.z, done);
        }

        void TeleportBetweenPlayers(string from, string to)
        {
            if (_workBusy || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
            _workBusy = true;
            _workMessage = "Teleporting " + from + " to " + to + "...";
            _api.TeleportPlayer(from, to, delegate(EditResult r)
            {
                _workBusy = false;
                _workMessage = r.Ok ? "Teleported " + from + " to " + to + "." : "Teleport failed: " + Reason(r);
                if (r.Ok) RefreshPlayers();
            });
        }


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
                            Identifier = (int)Json.Num(rows[i], "id", 0f),
                            Position = new Vector3(Json.Num(rows[i], "x", 0f), Json.Num(rows[i], "y", 0f), Json.Num(rows[i], "z", 0f)),
                            Yaw = Json.Num(rows[i], "yaw", 0f) });
                    }
                }
                if (_watchActive)
                {
                    string watchName = _watchTarget == null ? "selected player" : _watchTarget.Name;
                    OnlinePlayer updated = FindOnlinePlayer(watchName);
                    if (updated == null)
                    {
                        StopWatching();
                        _adminStatus = "Watch stopped: " + watchName + " is no longer online.";
                    }
                    else _watchTarget = updated;
                }
                if (_onlinePlayers.Count == 0) _workMessage = r.Ok ? "No online players found." : "Player refresh failed: " + Reason(r);
                else
                {
                    bool found = false;
                    for (int i = 0; i < _onlinePlayers.Count; i++) if (_onlinePlayers[i].Name == _selectedPlayer) found = true;
                    if (!found) _selectedPlayer = _onlinePlayers[0].Name;
                    bool foundMove = false, foundDestination = false, foundKick = false;
                    for (int i = 0; i < _onlinePlayers.Count; i++)
                    {
                        if (string.Equals(_onlinePlayers[i].Name, _adminMovePlayer, StringComparison.OrdinalIgnoreCase)) foundMove = true;
                        if (string.Equals(_onlinePlayers[i].Name, _adminDestinationPlayer, StringComparison.OrdinalIgnoreCase)) foundDestination = true;
                        if (string.Equals(_onlinePlayers[i].Name, _adminKickPlayer, StringComparison.OrdinalIgnoreCase)) foundKick = true;
                    }
                    string local = CurrentUsername();
                    if (!foundMove)
                    {
                        bool localFound = false;
                        for (int i = 0; i < _onlinePlayers.Count; i++)
                            if (string.Equals(_onlinePlayers[i].Name, local, StringComparison.OrdinalIgnoreCase))
                            { _adminMovePlayer = _onlinePlayers[i].Name; localFound = true; break; }
                        if (!localFound) _adminMovePlayer = _onlinePlayers[0].Name;
                    }
                    if (!foundDestination) _adminDestinationPlayer = "";
                    if (!foundKick) _adminKickPlayer = "";
                    _workMessage = "Choose an online player.";
                }
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
                        Json.Num(rows[i], "x", 0f), Json.Num(rows[i], "y", 0f), Json.Num(rows[i], "z", 0f)),
                        Identifier = (int)Json.Num(rows[i], "id", 0f), Yaw = Json.Num(rows[i], "yaw", 0f) };
            }
            return null;
        }

        void VisitWorkArea()
        {
            if (_selectedArea >= 0 && _selectedArea < _workAreas.Count)
                TeleportSelectedTo(_workAreas[_selectedArea].Position);
            else if (Camera != null) TeleportSelectedTo(Camera.transform.position);
        }

        void TeleportSelectedTo(Vector3 destination)
        {
            if (_workBusy || string.IsNullOrEmpty(_selectedPlayer)) return;
            if (_travel != null)
            {
                _workBusy = true;
                RestoreOriginalHome(delegate { TeleportSelectedTo(destination); });
                return;
            }
            OnlinePlayer target = null;
            for (int i = 0; i < _onlinePlayers.Count; i++) if (_onlinePlayers[i].Name == _selectedPlayer) target = _onlinePlayers[i];
            if (target == null) { _workMessage = "Refresh and select an online player."; return; }
            _workBusy = true; _workMessage = "Reading " + target.Name + "'s saved home...";
            _api.Command("player get-home \"" + target.Name + "\"", delegate(EditResult r)
            {
                Vector3 home;
                if (!r.Ok) { _workBusy = false; _workMessage = "Could not read saved home: " + Reason(r); return; }
                if (!TryCommandVector(r.Raw, out home))
                { _workBusy = false; _workMessage = "Server returned an unreadable home position. Teleport canceled."; return; }
                _travel = new TravelState { Player = target.Name, ReturnPosition = target.Position,
                    OriginalHome = home, WorkPosition = destination, HasWorkPosition = true, Returned = false };
                SaveTravel();
                SetTravelHomeAndTeleport(destination);
            });
        }

        void SetTravelHomeAndTeleport(Vector3 destination)
        {
            string player = _travel.Player;
            _workMessage = "Setting teleport destination for " + player + "...";
            _api.Command("player set-home \"" + player + "\" \"" + VecText(destination) + "\"", delegate(EditResult set)
            {
                if (!set.Ok) { _workBusy = false; _workMessage = "Could not set temporary home: " + Reason(set); _travel = null; SaveTravel(); return; }
                _workMessage = "Teleporting " + player + "...";
                _api.Command("player teleport \"" + player + "\" Home", delegate(EditResult tp)
                {
                    string result = tp.Ok ? "Teleported " + player + "." : "Teleport failed: " + Reason(tp);
                    RestoreOriginalHome(delegate { _workMessage = result; RefreshPlayers(); });
                });
            });
        }

        void RestoreOriginalHome(Action after = null)
        {
            string p = _travel.Player;
            _workMessage = "Restoring " + p + "'s original home...";
            _api.Command("player set-home \"" + p + "\" \"" + VecText(_travel.OriginalHome) + "\"", delegate(EditResult r)
            {
                _workBusy = false;
                if (!r.Ok) { _workMessage = "Home restore failed: " + Reason(r) + ". Retry Restore saved home."; return; }
                _travel = null; SaveTravel(); _workMessage = p + "'s saved home restored.";
                if (after != null) after();
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
            bool enabled = _has && _group.Count > 0 && !_busy;
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_group.Count > 1 ? "Size Δ each" : "Size Δ", GUILayout.Width(78f));
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
            GUILayout.Label(_group.Count > 1
                ? "Drag to preview all selected items; release to apply (-50% to +100%)."
                : "Drag to preview; release to apply (-50% to +100%).");
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
