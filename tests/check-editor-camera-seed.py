"""Test the editor's F1 camera seeding against modeled player and scene cameras."""
from pathlib import Path
import re
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] /
          'client/player-kit/mod/src/EditorMod.cs').read_text(encoding='utf-8-sig')
enable = source[source.index('        private void Enable()'):source.index('        private void Disable()')]
assert re.search(r'if\s*\(!SeedFromLocalPlayer\(\)\)', enable), 'F1 must stop when there is no local pose'
assert enable.index('SeedFromLocalPlayer()') < enable.index('_camGO.SetActive(true)')
seed = source[source.index('        private bool SeedFromLocalPlayer()'):source.index('        // ---- per-frame freecam')]

harness = r'''
using System;
using UnityEngine;

namespace UnityEngine {
    struct Vector3 {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x, a.y+b.y, a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x, a.y-b.y, a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.x*s, a.y*s, a.z*s); }
        public float sqrMagnitude { get { return x*x + y*y + z*z; } }
        public static float Distance(Vector3 a, Vector3 b) { return (float)Math.Sqrt((a-b).sqrMagnitude); }
    }
    struct Quaternion {
        public Vector3 eulerAngles;
        public static Quaternion Euler(float x, float y, float z) {
            return new Quaternion { eulerAngles = new Vector3(x, y, z) };
        }
    }
    sealed class Transform {
        public Vector3 position;
        public Quaternion rotation;
    }
    sealed class GameObject {
        public Transform transform = new Transform();
        public bool activeInHierarchy = true;
    }
    sealed class Camera {
        public static Camera main;
        public static Camera[] allCameras;
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
        public bool stereoEnabled;
        public bool enabled = true;
        public bool isActiveAndEnabled { get { return enabled && gameObject.activeInHierarchy; } }
        public object targetTexture;
    }
    static class Mathf {
        public static float Clamp(float n, float min, float max) { return Math.Max(min, Math.Min(n, max)); }
    }
    static class XRSettings { public static bool isDeviceActive; }
}

class PlayerController {
    public static PlayerController Current;
    public bool IsLocalPlayer;
    public virtual Transform Head { get; set; }
    public Camera Camera;
}

sealed class FlyCamPlayer : PlayerController {
    public override Transform Head { get { return Camera.transform; } set { throw new NotSupportedException(); } }
}

sealed class Test {
    readonly Camera _cam = new Camera();
    GameObject _camGO { get { return _cam.gameObject; } }
    float _pitch, _yaw;

    SEED_METHODS

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    static Camera View(float x, float y, float z, float yaw, bool stereo = false) {
        var view = new Camera { stereoEnabled = stereo };
        view.transform.position = new Vector3(x, y, z);
        view.transform.rotation = Quaternion.Euler(20, yaw, 8);
        return view;
    }
    void ExpectPose(float x, float y, float z, float yaw, string reason) {
        var pose = _camGO.transform;
        Check((pose.position - new Vector3(x, y, z)).sqrMagnitude < .000001f, reason + " position");
        Check(Math.Abs(pose.rotation.eulerAngles.y - yaw) < .001f, reason + " yaw");
        Check(Math.Abs(pose.rotation.eulerAngles.z) < .001f, reason + " roll stays level");
    }
    static void Main() {
        var test = new Test();
        var town = View(0, 50, 0, 10, true);
        var eye = View(101, 30, 50, 60, true);
        var closer = View(100.1f, 30, 50, 70, true);
        var offscreen = View(100.05f, 30, 50, 80, true);
        offscreen.targetTexture = new object();
        var disabled = View(100.01f, 30, 50, 85, true);
        disabled.enabled = false;
        Camera.main = town;
        Camera.allCameras = new[] { town, disabled, offscreen, closer, eye, test._cam };
        var player = new PlayerController { IsLocalPlayer = true, Camera = eye, Head = View(100, 30, 50, 45).transform };
        PlayerController.Current = player;

        XRSettings.isDeviceActive = false;
        Check(test.SeedFromLocalPlayer(), "flat screen finds the local avatar");
        test.ExpectPose(100, 30.5f, 50, 45, "flat screen ignores town and stereo cameras");
        player.Head.position = new Vector3(140, 40, 80);
        player.Head.rotation = Quaternion.Euler(10, 120, 12);
        Check(test.SeedFromLocalPlayer(), "reopen still finds the local avatar");
        test.ExpectPose(140, 40.5f, 80, 120, "reopening uses the moved avatar");

        player.Head = View(100, 30, 50, 45).transform;
        XRSettings.isDeviceActive = true;
        Check(test.SeedFromLocalPlayer(), "VR uses the player's stereo camera");
        test.ExpectPose(101, 30, 50, 60, "local stereo camera takes precedence over other cameras");
        player.Camera = town;
        test._cam.stereoEnabled = true;
        test._camGO.transform.position = player.Head.position;
        Check(test.SeedFromLocalPlayer(), "VR can find a nearby stereo camera");
        test.ExpectPose(100.1f, 30, 50, 70, "VR excludes distant, disabled, offscreen, and editor cameras");
        Camera.allCameras = new[] { town, offscreen, test._cam };
        Check(test.SeedFromLocalPlayer(), "VR without a usable headset still has an avatar position");
        test.ExpectPose(100, 30.5f, 50, 45, "fallback stays with the avatar");

        PlayerController.Current = null;
        Check(!test.SeedFromLocalPlayer(), "before the avatar loads, the editor stays closed");
        PlayerController.Current = player;
        player.IsLocalPlayer = false;
        Check(!test.SeedFromLocalPlayer(), "a remote avatar is never used");
        player.IsLocalPlayer = true;
        player.Head = null;
        Check(!test.SeedFromLocalPlayer(), "missing head waits for the avatar");
        player.Head = View(float.NaN, 30, 50, 45).transform;
        Check(!test.SeedFromLocalPlayer(), "invalid world position is rejected");
        test.ExpectPose(100, 30.5f, 50, 45, "unavailable avatar leaves camera transform alone");
        var fly = new FlyCamPlayer { IsLocalPlayer = true };
        PlayerController.Current = fly;
        Check(!test.SeedFromLocalPlayer(), "unloaded FlyCam avoids dereferencing its missing camera");
        fly.Camera = View(170f, 30f, -7f, 100f);
        Check(test.SeedFromLocalPlayer(), "loaded FlyCam uses the local camera as its head");
        test.ExpectPose(170, 30.5f, -7, 100, "FlyCam stays with the local player");
        Console.WriteLine("PASS: flat-screen avatar pose, reopening after moving, VR camera selection, and unloaded avatar guard.");
    }
}
'''.replace('SEED_METHODS', seed)

with tempfile.TemporaryDirectory(prefix='prefab-camera-seed-test-') as folder:
    root = Path(folder)
    exe = root / 'Test.exe'
    (root / 'Test.cs').write_text(harness, encoding='utf-8')
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo',
                    '/out:' + str(exe), str(root / 'Test.cs')], check=True)
    subprocess.run([str(exe)], check=True)
