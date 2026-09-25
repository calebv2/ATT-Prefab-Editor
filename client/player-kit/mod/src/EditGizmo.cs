// The Unity-style transform gizmo - direct object dragging + translate arrows +
// rotate rings + snapping. Scale is applied through the save-string workbench path.
//
// Same architecture rule as ever: CLIENT previews, SERVER decides. A drag writes the
// LOCAL transform only (never networked) and on release sends ONE edit move / edit
// rotate through EditApi; a server refusal rolls the preview back. If a placed object
// visibly fights the preview mid-drag (server stream overwriting us), set
// GizmoGhostPreview=true in MelonPreferences.cfg: the object stays put and the moving
// gizmo itself becomes the ghost target marker until commit. Mounted/docked/held
// selections force ghost preview automatically - the server vetoes their moves anyway,
// and yanking a mounted part off its parent structure mid-drag would just look broken.
//
// Render: GizmoRenderHook (a MonoBehaviour on the editor camera's GameObject, so Unity
// delivers that camera's OnPostRender) calls back into EditSelection.DrawGizmo, and we
// paint with GL immediate mode + Hidden/Internal-Colored, ZTest Always so handles stay
// visible (and grabbable) inside geometry. Everything is sized by distance-to-camera,
// so the gizmo keeps a constant on-screen size.
//
// Pick: screen-space - arrows translate on one axis, rings rotate; clicking the object
// body begins a camera-plane move. A grabbed mode is locked until mouse-up. Esc cancels.
//
// C# 5 / net35 - no interpolation, no ?., no expression-bodied members.

using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace PrefabEditorMod
{
    // Lives on the editor camera GameObject; forwards its OnPostRender to the
    // selection layer, which owns the gizmo state.
    public class GizmoRenderHook : MonoBehaviour
    {
        [NonSerialized] public EditSelection Owner;
        Camera _cam;

        void Awake()
        {
            _cam = GetComponent<Camera>();
        }

        void OnPostRender()
        {
            EditSelection o = Owner;
            if (o != null && _cam != null) o.DrawGizmo(_cam);
        }
    }

    public class EditGizmo
    {
        // handle ids: 0..2 translate along world X/Y/Z, 3..5 rotate rings about X/Y/Z
        const int H_NONE = -1;
        const int H_AXIS_X = 0;
        const int H_AXIS_Z = 2;
        const int H_RING_X = 3;

        // on-screen sizing in px, converted to world units per frame by distance
        const float AXIS_PX = 105f;       // arrow length
        const float RING_PX = 74f;        // ring radius (inside the arrow reach)
        const float PICK_PX = 12f;        // grab tolerance
        const float SHAFT_HALF_PX = 1.2f;
        const float CONE_LEN_PX = 22f;
        const float CONE_R_PX = 6.5f;
        const int RING_SEGS = 48;
        const float SNAP_ANGLE = 15f;         // Ctrl
        const float SNAP_ANGLE_COARSE = 45f;  // Ctrl+Shift

        public float GridSize = 0.25f;    // Ctrl translate snap in meters (Shift = x4)
        public Func<Vector3, Vector2, Vector3> SnapMove;

        readonly Action<string> _status;        // one-shot lines (grab/cancel/edge-on)
        readonly Action<Vector3> _commitMove;   // release -> ONE edit move (absolute pos)
        readonly Action<Vector3> _commitEuler;  // release -> ONE edit rotate (absolute euler)
        readonly Action<Vector3> _previewMove; // local group preview by anchor delta

        int _hover = H_NONE;
        int _locked = H_NONE;
        bool _dragging;
        bool _ghost;              // this drag: don't touch the transform; gizmo = ghost
        bool _directDrag;         // grab the object body and move on a camera-facing plane

        Transform _t;             // locked drag target
        Vector3 _origPos;
        Quaternion _origRot;
        Vector3 _previewPos;
        Quaternion _previewRot;

        // translate drag
        Vector3 _axisDir;
        float _grabS;             // axis parameter under the mouse at grab
        float _delta;
        Plane _dragPlane;
        Vector3 _grabOffset;

        // rotate drag
        Vector3 _ringAxis;
        Vector3 _spokeU;          // grab-spoke basis in the ring plane
        Vector3 _spokeV;
        float _rawPrev;
        float _accum;             // unwrapped raw angle, so multi-turn drags keep going
        float _angle;             // snapped angle actually applied

        // kept past release so a server refusal can roll the local preview back
        Transform _commitT;
        Vector3 _commitPos;
        Quaternion _commitRot;

        public string DragLabel = "";
        public bool RotationEnabled = true;
        public bool GroupRotationEnabled;
        public Action<Vector3, float> PreviewGroupRotation;
        public Action<Vector3, float> CommitGroupRotation;

        static Material _mat;
        static bool _matTried;

        public EditGizmo(Action<string> status, Action<Vector3> commitMove, Action<Vector3> commitEuler,
            Action<Vector3> previewMove)
        {
            _status = status;
            _commitMove = commitMove;
            _commitEuler = commitEuler;
            _previewMove = previewMove;
        }

        public bool Dragging { get { return _dragging; } }

        // ---- per-frame input ----

        // Returns true while the gizmo owns the mouse (grabbed this frame or dragging),
        // so the caller skips entity picking. target = the selected root's transform,
        // canGrab = no server call in flight, ghostWanted = pref or veto-flagged entity.
        public bool Update(Camera cam, Vector2 mp, Transform target, bool overPanel, bool canGrab, bool ghostWanted)
        {
            if (cam == null)
            {
                if (_dragging) Cancel(false);
                return false;
            }

            if (_dragging)
            {
                if (!_ghost && _t == null) { Abort(); return false; } // target died mid-drag
                Keyboard kb = Keyboard.current;
                if (kb != null && kb[Key.Escape].wasPressedThisFrame) { Cancel(true); return true; }
                if (_directDrag) UpdateDirectMove(cam, mp);
                else if (_locked <= H_AXIS_Z) UpdateTranslate(cam, mp);
                else UpdateRotate(cam, mp);
                Mouse held = Mouse.current;
                if (held == null || !held.leftButton.isPressed) Release();
                return true;
            }

            _hover = H_NONE;
            if (target == null || overPanel) return false;

            Vector3 c;
            try { c = target.position; } catch { return false; }
            _hover = PickHandle(cam, mp, c);
            if (_hover == H_NONE || !canGrab) return false;

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                return BeginDrag(cam, mp, target, c, ghostWanted);
            return false;
        }

        bool BeginDrag(Camera cam, Vector2 mp, Transform target, Vector3 c, bool ghost)
        {
            _directDrag = false;
            Ray ray = cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));

            if (_hover >= H_RING_X)
            {
                if (!RotationEnabled) return false;
                Vector3 axis = AxisVec(_hover - H_RING_X);
                float denom = Vector3.Dot(ray.direction, axis);
                if (Mathf.Abs(denom) < 0.04f)
                {
                    Say("that ring is edge-on - orbit the camera a little");
                    return false;
                }
                float t = Vector3.Dot(c - ray.origin, axis) / denom;
                if (t <= 0f) return false;
                Vector3 rel = ray.origin + ray.direction * t - c;
                if (rel.sqrMagnitude < 1e-8f) return false;
                _ringAxis = axis;
                _spokeU = rel.normalized;
                _spokeV = Vector3.Cross(axis, _spokeU).normalized;
                _rawPrev = 0f;
                _accum = 0f;
                _angle = 0f;
            }
            else
            {
                _axisDir = AxisVec(_hover);
                float s;
                if (!ClosestAxisParam(ray, c, _axisDir, out s)) return false; // axis parallel to the view ray
                _grabS = s;
                _delta = 0f;
            }

            _t = target;
            _origPos = c;
            try { _origRot = target.rotation; } catch { _origRot = Quaternion.identity; }
            _previewPos = _origPos;
            _previewRot = _origRot;
            _ghost = ghost;
            _locked = _hover;
            _dragging = true;
            DragLabel = "";
            if (GroupRotationEnabled && _locked >= H_RING_X && PreviewGroupRotation != null)
                PreviewGroupRotation(_ringAxis, 0f);
            Say((_locked <= H_AXIS_Z ? "move " : "rotate ") + HandleName(_locked) +
                (_ghost ? "  (ghost preview)" : "") + "  -  Esc cancels");
            return true;
        }

        // Start a move by grabbing the clicked point on the object. The initial
        // screen point is retained because selection info arrives asynchronously.
        public void BeginDirectDrag(Camera cam, Transform target, Vector3 hitPoint, bool ghost)
        {
            if (cam == null || target == null) return;
            _t = target;
            _origPos = target.position;
            try { _origRot = target.rotation; } catch { _origRot = Quaternion.identity; }
            _previewPos = _origPos;
            _previewRot = _origRot;
            _dragPlane = new Plane(cam.transform.forward, hitPoint);
            _grabOffset = _origPos - hitPoint;
            _ghost = ghost;
            _directDrag = true;
            _locked = H_NONE;
            _dragging = true;
            _delta = 0f;
            DragLabel = "Grab to move" + (_ghost ? "  (ghost preview)" : "");
            Say(DragLabel + "  -  Esc cancels");
        }

        void UpdateTranslate(Camera cam, Vector2 mp)
        {
            Ray ray = cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
            float s;
            if (ClosestAxisParam(ray, _origPos, _axisDir, out s))
            {
                float d = s - _grabS;
                bool snap = CtrlHeld();
                bool fine = AltHeld();
                if (fine) d *= 0.1f;
                if (snap)
                {
                    float g = GridSize;
                    if (g < 0.001f) g = 0.25f;
                    if (fine) g *= 0.1f;
                    if (ShiftHeld()) g *= 4f;
                    d = Mathf.Round(d / g) * g;
                }
                _delta = Mathf.Clamp(d, -300f, 300f);
                _previewPos = _origPos + _axisDir * _delta;
                DragLabel = HandleName(_locked) + "  " + Signed(_delta) + " m" +
                    (fine ? "  fine" : "") + (snap ? "  snap" : "");
                _previewPos = ApplySurfaceSnap(_previewPos, mp);
            }
            if (!_ghost) SafeSetPos(_previewPos);
            if (_previewMove != null) _previewMove(_previewPos - _origPos);
        }

        void UpdateDirectMove(Camera cam, Vector2 mp)
        {
            Ray ray = cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
            float enter;
            if (!_dragPlane.Raycast(ray, out enter) || enter < 0f) return;
            Vector3 point = ray.GetPoint(enter) + _grabOffset;
            Vector3 delta = point - _origPos;
            bool snap = CtrlHeld();
            if (snap)
            {
                float grid = GridSize;
                if (grid < 0.001f) grid = 0.25f;
                if (ShiftHeld()) grid *= 4f;
                delta.x = Mathf.Round(delta.x / grid) * grid;
                delta.y = Mathf.Round(delta.y / grid) * grid;
                delta.z = Mathf.Round(delta.z / grid) * grid;
            }
            delta = new Vector3(Mathf.Clamp(delta.x, -300f, 300f),
                Mathf.Clamp(delta.y, -300f, 300f), Mathf.Clamp(delta.z, -300f, 300f));
            _delta = delta.magnitude;
            _previewPos = _origPos + delta;
            DragLabel = "move " + Signed(delta.x) + ", " + Signed(delta.y) + ", " +
                Signed(delta.z) + " m" + (snap ? "  snap" : "");
            _previewPos = ApplySurfaceSnap(_previewPos, mp);
            if (!_ghost) SafeSetPos(_previewPos);
            if (_previewMove != null) _previewMove(_previewPos - _origPos);
        }

        Vector3 ApplySurfaceSnap(Vector3 position, Vector2 mousePosition)
        {
            if (SnapMove == null) return position;
            Vector3 snapped = SnapMove(position, mousePosition);
            if ((snapped - position).sqrMagnitude > 0.000001f)
                DragLabel += "  surface snap";
            return snapped;
        }

        void UpdateRotate(Camera cam, Vector2 mp)
        {
            Ray ray = cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
            float denom = Vector3.Dot(ray.direction, _ringAxis);
            if (Mathf.Abs(denom) > 0.03f) // edge-on mid-drag: hold the last angle
            {
                float t = Vector3.Dot(_origPos - ray.origin, _ringAxis) / denom;
                if (t > 0f)
                {
                    Vector3 rel = ray.origin + ray.direction * t - _origPos;
                    if (rel.sqrMagnitude > 1e-8f)
                    {
                        float raw = Mathf.Atan2(Vector3.Dot(rel, _spokeV), Vector3.Dot(rel, _spokeU)) * Mathf.Rad2Deg;
                        _accum += Mathf.DeltaAngle(_rawPrev, raw);
                        _rawPrev = raw;
                    }
                }
            }
            float a = _accum;
            bool snap = CtrlHeld();
            if (snap)
            {
                float step = ShiftHeld() ? SNAP_ANGLE_COARSE : SNAP_ANGLE;
                a = Mathf.Round(a / step) * step;
            }
            _angle = a;
            _previewRot = Quaternion.AngleAxis(a, _ringAxis) * _origRot;
            DragLabel = HandleName(_locked) + "  " + Signed(a) + " deg" + (snap ? "  snap" : "");
            if (!_ghost) SafeSetRot(_previewRot);
            if (GroupRotationEnabled && PreviewGroupRotation != null)
                PreviewGroupRotation(_ringAxis, a);
        }

        void Release()
        {
            _dragging = false;
            int h = _locked;
            _locked = H_NONE;
            DragLabel = "";

            bool moved = _directDrag ? _delta > 0.0005f :
                ((h <= H_AXIS_Z) ? Mathf.Abs(_delta) > 0.0005f : Mathf.Abs(_angle) > 0.01f);
            bool wasDirectDrag = _directDrag;
            _directDrag = false;
            if (!moved)
            {
                if (!_ghost) { SafeSetPos(_origPos); SafeSetRot(_origRot); }
                return;
            }

            _commitT = _t;
            _commitPos = _origPos;
            _commitRot = _origRot;

            if (wasDirectDrag || h <= H_AXIS_Z) _commitMove(_previewPos);
            else if (GroupRotationEnabled && CommitGroupRotation != null)
                CommitGroupRotation(_ringAxis, _angle);
            else _commitEuler(_previewRot.eulerAngles);
        }

        // Esc, deselect, or editor-off mid-drag: put the object back where it started.
        public void Cancel(bool sayIt)
        {
            _hover = H_NONE;
            if (!_dragging) return;
            _dragging = false;
            _locked = H_NONE;
            _directDrag = false;
            DragLabel = "";
            if (!_ghost) { SafeSetPos(_origPos); SafeSetRot(_origRot); }
            if (sayIt) Say("drag canceled");
        }

        void Abort() // target destroyed mid-drag; nothing to restore
        {
            _dragging = false;
            _locked = H_NONE;
            _directDrag = false;
            DragLabel = "";
        }

        // The server refused the committed move/rotate: undo the local preview.
        public void RestoreOriginal()
        {
            Transform t = _commitT;
            if (t == null) return;
            try { t.position = _commitPos; t.rotation = _commitRot; } catch { }
        }

        void SafeSetPos(Vector3 p)
        {
            if (_t == null) return;
            try { _t.position = p; } catch { }
        }

        void SafeSetRot(Quaternion q)
        {
            if (_t == null) return;
            try { _t.rotation = q; } catch { }
        }

        // ---- picking (screen space) ----

        int PickHandle(Camera cam, Vector2 mp, Vector3 c)
        {
            float dist = Vector3.Distance(cam.transform.position, c);
            if (dist < 0.05f) return H_NONE;
            float ptw = PxToWorld(cam, dist);
            float len = AXIS_PX * ptw;
            float ringR = RING_PX * ptw;

            int best = H_NONE;
            float bestScore = PICK_PX;

            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = AxisVec(i);
                Vector2 a, b;
                if (!ToScreen(cam, c + dir * (len * 0.18f), out a)) continue;
                if (!ToScreen(cam, c + dir * len, out b)) continue;
                float d = SegDist(mp, a, b) - 1f; // arrows win ties where they cross a ring
                if (d < bestScore) { bestScore = d; best = i; }
            }

            for (int i = 0; i < 3; i++)
            {
                if (!RotationEnabled) break;
                float d = RingDist(cam, mp, c, i, ringR);
                if (d < bestScore) { bestScore = d; best = H_RING_X + i; }
            }
            return best;
        }

        static float RingDist(Camera cam, Vector2 mp, Vector3 c, int axis, float r)
        {
            Vector3 u, v;
            RingBasis(axis, out u, out v);
            float best = float.MaxValue;
            Vector2 prev = Vector2.zero;
            bool prevOk = false;
            for (int k = 0; k <= RING_SEGS; k++)
            {
                float a = k * (2f * Mathf.PI / RING_SEGS);
                Vector3 w = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                Vector2 sp;
                bool ok = ToScreen(cam, w, out sp);
                if (ok && prevOk)
                {
                    float d = SegDist(mp, prev, sp);
                    if (d < best) best = d;
                }
                prev = sp;
                prevOk = ok;
            }
            return best;
        }

        static bool ToScreen(Camera cam, Vector3 world, out Vector2 sp)
        {
            Vector3 p = cam.WorldToScreenPoint(world);
            sp = new Vector2(p.x, p.y);
            return p.z > 0f;
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        // Closest-approach parameter s of the axis line (p0 + s*dir) to the mouse ray.
        // False when the axis runs (near-)parallel to the ray - no stable answer.
        static bool ClosestAxisParam(Ray ray, Vector3 p0, Vector3 dir, out float s)
        {
            Vector3 w0 = p0 - ray.origin;
            float b = Vector3.Dot(dir, ray.direction);
            float denom = 1f - b * b;
            s = 0f;
            if (denom < 1e-4f) return false;
            float d = Vector3.Dot(dir, w0);
            float e = Vector3.Dot(ray.direction, w0);
            s = (b * e - d) / denom;
            return true;
        }

        // ---- drawing (GL immediate mode, called from the camera's OnPostRender) ----

        // idleCenter = the selection's live position when not dragging; during a drag
        // the whole gizmo follows the preview position (in ghost mode that moving
        // gizmo IS the ghost marker while the object stays put).
        public void Draw(Camera cam, Vector3 idleCenter)
        {
            EnsureMat();
            if (_mat == null || cam == null) return;

            Vector3 c = _dragging ? _previewPos : idleCenter;
            float dist = Vector3.Distance(cam.transform.position, c);
            if (dist < 0.05f) return;
            float ptw = PxToWorld(cam, dist);
            float len = AXIS_PX * ptw;
            float ringR = RING_PX * ptw;

            _mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;

            GL.Begin(GL.LINES);
            for (int i = 0; RotationEnabled && i < 3; i++)
            {
                int id = H_RING_X + i;
                Vector3 u, v;
                RingBasis(i, out u, out v);
                DrawRingLoop(c, u, v, ringR, HandleColor(id));
                if (id == _hover || (_dragging && id == _locked))
                    DrawRingLoop(c, u, v, ringR * 1.012f, HandleColor(id)); // double-stroke = bold
            }
            if (_dragging && !_directDrag && _locked <= H_AXIS_Z) DrawTranslateGuides(len);
            if (_dragging && _locked >= H_RING_X) DrawSpokes(c, ringR);
            GL.End();

            Vector3 eye = cam.transform.position;
            GL.Begin(GL.QUADS);
            for (int i = 0; i < 3; i++) DrawShaft(c, i, len, ptw, eye);
            GL.End();

            GL.Begin(GL.TRIANGLES);
            for (int i = 0; i < 3; i++) DrawCone(c, i, len, ptw);
            GL.End();

            GL.PopMatrix();
        }

        static void DrawRingLoop(Vector3 c, Vector3 u, Vector3 v, float r, Color col)
        {
            GL.Color(col);
            Vector3 prev = c + u * r;
            for (int k = 1; k <= RING_SEGS; k++)
            {
                float a = k * (2f * Mathf.PI / RING_SEGS);
                Vector3 p = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                GL.Vertex(prev);
                GL.Vertex(p);
                prev = p;
            }
        }

        void DrawTranslateGuides(float len)
        {
            // the locked axis, extended far both ways, so there's a rail to aim along
            Color g = HandleColor(_locked);
            g.a = 0.35f;
            GL.Color(g);
            GL.Vertex(_origPos - _axisDir * (len * 40f));
            GL.Vertex(_origPos + _axisDir * (len * 40f));

            // from where to where
            GL.Color(new Color(1f, 1f, 1f, 0.75f));
            GL.Vertex(_origPos);
            GL.Vertex(_previewPos);

            // small cross at the start position
            float s = len * 0.06f;
            GL.Vertex(_origPos - Vector3.up * s); GL.Vertex(_origPos + Vector3.up * s);
            GL.Vertex(_origPos - Vector3.right * s); GL.Vertex(_origPos + Vector3.right * s);
            GL.Vertex(_origPos - Vector3.forward * s); GL.Vertex(_origPos + Vector3.forward * s);
        }

        void DrawSpokes(Vector3 c, float r)
        {
            GL.Color(new Color(1f, 1f, 1f, 0.65f));
            GL.Vertex(c);
            GL.Vertex(c + _spokeU * r); // where the grab started
            Vector3 cur = Quaternion.AngleAxis(_angle, _ringAxis) * _spokeU;
            GL.Color(new Color(1f, 0.92f, 0.25f, 0.95f));
            GL.Vertex(c);
            GL.Vertex(c + cur * r);     // where it is now
        }

        void DrawShaft(Vector3 c, int axis, float len, float ptw, Vector3 eye)
        {
            Vector3 dir = AxisVec(axis);
            Vector3 a = c + dir * (len * 0.10f);
            Vector3 b = c + dir * (len * 0.80f);
            Vector3 side = Vector3.Cross(dir, (a + b) * 0.5f - eye);
            if (side.sqrMagnitude < 1e-10f) return; // axis points straight at the eye
            float half = SHAFT_HALF_PX * ptw;
            if (axis == _hover || (_dragging && axis == _locked)) half *= 1.7f;
            side = side.normalized * half;
            GL.Color(HandleColor(axis));
            GL.Vertex(a - side);
            GL.Vertex(a + side);
            GL.Vertex(b + side);
            GL.Vertex(b - side);
        }

        void DrawCone(Vector3 c, int axis, float len, float ptw)
        {
            Vector3 dir = AxisVec(axis);
            Vector3 tip = c + dir * len;
            Vector3 baseC = c + dir * (len - CONE_LEN_PX * ptw);
            float r = CONE_R_PX * ptw;
            if (axis == _hover || (_dragging && axis == _locked)) r *= 1.25f;
            Vector3 u, v;
            RingBasis(axis, out u, out v); // any perpendicular pair works for the base
            GL.Color(HandleColor(axis));
            const int segs = 12;
            Vector3 prev = baseC + u * r;
            for (int k = 1; k <= segs; k++)
            {
                float a = k * (2f * Mathf.PI / segs);
                Vector3 p = baseC + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                GL.Vertex(tip); GL.Vertex(prev); GL.Vertex(p);   // side
                GL.Vertex(baseC); GL.Vertex(p); GL.Vertex(prev); // base
                prev = p;
            }
        }

        // Scene-wide wire boxes (the outline-all toggle). Same material + matrix
        // dance as Draw, kept here so every GL call site shares one shader setup.
        public static void DrawWireBoxes(Camera cam, System.Collections.Generic.List<Bounds> boxes, Color col)
        {
            if (cam == null || boxes == null || boxes.Count == 0) return;
            EnsureMat();
            if (_mat == null) return;

            _mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.LINES);
            GL.Color(col);
            for (int i = 0; i < boxes.Count; i++)
            {
                Vector3 a = boxes[i].min, b = boxes[i].max;
                // bottom rectangle
                Edge(a.x, a.y, a.z, b.x, a.y, a.z);
                Edge(b.x, a.y, a.z, b.x, a.y, b.z);
                Edge(b.x, a.y, b.z, a.x, a.y, b.z);
                Edge(a.x, a.y, b.z, a.x, a.y, a.z);
                // top rectangle
                Edge(a.x, b.y, a.z, b.x, b.y, a.z);
                Edge(b.x, b.y, a.z, b.x, b.y, b.z);
                Edge(b.x, b.y, b.z, a.x, b.y, b.z);
                Edge(a.x, b.y, b.z, a.x, b.y, a.z);
                // verticals
                Edge(a.x, a.y, a.z, a.x, b.y, a.z);
                Edge(b.x, a.y, a.z, b.x, b.y, a.z);
                Edge(b.x, a.y, b.z, b.x, b.y, b.z);
                Edge(a.x, a.y, b.z, a.x, b.y, b.z);
            }
            GL.End();
            GL.PopMatrix();
        }

        static void Edge(float ax, float ay, float az, float bx, float by, float bz)
        {
            GL.Vertex3(ax, ay, az);
            GL.Vertex3(bx, by, bz);
        }

        Color HandleColor(int id)
        {
            int axis = (id >= H_RING_X) ? id - H_RING_X : id;
            Color col = AxisColor(axis);
            if (_dragging)
            {
                if (id == _locked) return new Color(1f, 0.92f, 0.25f, 1f);
                col.a *= 0.28f; // fade the rest while a handle is held
                return col;
            }
            if (id == _hover) return Color.Lerp(col, Color.white, 0.5f);
            return col;
        }

        static void EnsureMat()
        {
            if (_mat != null || _matTried) return;
            _matTried = true;
            try
            {
                Shader sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) return;
                _mat = new Material(sh);
                _mat.hideFlags = HideFlags.HideAndDontSave;
                _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_ZTest", (int)CompareFunction.Always); // draw through geometry
            }
            catch { _mat = null; }
        }

        // ---- small helpers ----

        static Vector3 AxisVec(int i)
        {
            if (i == 0) return Vector3.right;
            if (i == 1) return Vector3.up;
            return Vector3.forward;
        }

        static void RingBasis(int axis, out Vector3 u, out Vector3 v)
        {
            if (axis == 0) { u = Vector3.up; v = Vector3.forward; }       // about X: YZ plane
            else if (axis == 1) { u = Vector3.right; v = Vector3.forward; } // about Y: XZ
            else { u = Vector3.right; v = Vector3.up; }                     // about Z: XY
        }

        static Color AxisColor(int i)
        {
            if (i == 0) return new Color(0.93f, 0.26f, 0.26f, 0.95f);
            if (i == 1) return new Color(0.30f, 0.85f, 0.33f, 0.95f);
            return new Color(0.25f, 0.55f, 0.96f, 0.95f);
        }

        static string HandleName(int id)
        {
            if (id == 0) return "X";
            if (id == 1) return "Y";
            if (id == 2) return "Z";
            if (id == 3) return "X ring";
            if (id == 4) return "Y ring";
            return "Z ring";
        }

        // world units per screen pixel at this distance (vertical fov)
        static float PxToWorld(Camera cam, float dist)
        {
            float h = cam.pixelHeight;
            if (h < 1f) h = 1f;
            return 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / h;
        }

        static bool CtrlHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && (kb[Key.LeftCtrl].isPressed || kb[Key.RightCtrl].isPressed);
        }

        static bool ShiftHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && (kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed);
        }

        static bool AltHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && (kb[Key.LeftAlt].isPressed || kb[Key.RightAlt].isPressed);
        }

        static string Signed(float v)
        {
            string s = v.ToString("0.##", CultureInfo.InvariantCulture);
            return (v >= 0f ? "+" : "") + s;
        }

        void Say(string s)
        {
            if (_status != null) _status(s);
        }
    }
}
