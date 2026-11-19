"""Check that capturing a destination reads the avatar and leaves linking explicit."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] /
          'client/player-kit/mod/src/EditSelection.cs').read_text(encoding='utf-8-sig')


def methods(start, end):
    first = source.index('        ' + start)
    return source[first:source.index('        ' + end, first)]


harness = r'''
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

struct Vector3 {
    public float x, y, z;
    public static readonly Vector3 zero = new Vector3(0, 0, 0);
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
}
class Player {
    public static Player Current;
    public Vector3 Position;
}
class Entity { public uint Id; }
class EditResult { public bool Ok; public Entity Ent; }
class Api {
    public int Calls;
    public uint LinkedId;
    public Vector3 Destination;
    public void LinkTeleporter(uint id, float x, float y, float z, Action<EditResult> done) {
        Calls++;
        LinkedId = id;
        Destination = new Vector3(x, y, z);
        done(new EditResult { Ok = true, Ent = new Entity { Id = id } });
    }
}
class TextInput { public void Blur() {} }
class Test {
    string _teleporterDestination = "1,2,3", _teleporterStatus;
    bool _teleporterBusy, _has;
    uint _id;
    List<int> _group = new List<int>();
    Api _api = new Api();
    TextInput _textInput = new TextInput();

    static string Reason(EditResult r) { return "failed"; }
    static bool TryPlayerPosition(object current, out Vector3 position) {
        var player = current as Player;
        position = player == null ? Vector3.zero : player.Position;
        return player != null;
    }

    CAPTURE_AND_LINK
    FORMAT_COORDINATES

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    static void Main() {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
        var test = new Test();
        test.CaptureTeleporterDestination();
        Check(test._teleporterDestination == "1,2,3" && test._teleporterStatus.Contains("Join"),
            "unavailable avatar preserves the draft and gives a next step");
        Player.Current = new Player { Position = new Vector3(-596.447f, 275.657f, 240.869f) };
        test.CaptureTeleporterDestination();
        Check(test._teleporterDestination == "-596.447,275.657,240.869",
            "fresh avatar coordinates work even before selecting a teleporter and under a comma-decimal culture");
        Check(test._api.Calls == 0, "capture is local");
        test.ConfigureTeleporter();
        Check(test._api.Calls == 0, "link requires a selection");
        Player.Current.Position = new Vector3(float.PositiveInfinity, 1, 2);
        test.CaptureTeleporterDestination();
        Check(test._teleporterDestination == "-596.447,275.657,240.869", "invalid position does not replace the draft");
        Player.Current.Position = new Vector3(10, 20, 30);
        test._has = true;
        test._id = 4810;
        test._group.Add(1);
        test.ConfigureTeleporter();
        Check(test._api.Calls == 1 && test._api.LinkedId == 4810, "explicit link sends one request for the selected entity");
        var destination = test._api.Destination;
        Check(destination.x == -596.447f && destination.y == 275.657f && destination.z == 240.869f,
            "link saves the earlier capture after the avatar has moved");
        Console.WriteLine("PASS: location capture, invariant coordinates, invalid positions and explicit linking.");
    }
}
'''
harness = harness.replace('CAPTURE_AND_LINK',
                          methods('void CaptureTeleporterDestination()', 'void TeleportBetweenPlayers('))
harness = harness.replace('FORMAT_COORDINATES',
                          methods('static string VecText(', 'static bool TryCommandVector('))

with tempfile.TemporaryDirectory(prefix='teleporter-capture-test-') as folder:
    folder = Path(folder)
    test = folder / 'Test.cs'
    test.write_text(harness)
    exe = folder / 'Test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo',
                    '/out:' + str(exe), str(test)], check=True)
    subprocess.run([str(exe)], check=True)
