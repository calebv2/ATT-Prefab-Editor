"""Test the shared native field and its Input System fallback without launching Unity."""
from pathlib import Path
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[1]
source = repo / 'client/player-kit/mod/src/EditorTextInput.cs'
harness = r'''
using System;
using System.Collections.Generic;
using PrefabEditorMod;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityEngine.InputSystem {
    public class Button { public bool isPressed, wasPressedThisFrame; }
    public class Keyboard {
        public static Keyboard current = new Keyboard();
        public event Action<char> onTextInput;
        public void Type(char c) { if (onTextInput != null) onTextInput(c); }
        public Button ctrlKey = new Button(), shiftKey = new Button(), aKey = new Button(),
            vKey = new Button(), cKey = new Button(), xKey = new Button(), backspaceKey = new Button(),
            deleteKey = new Button(), leftArrowKey = new Button(), rightArrowKey = new Button(),
            homeKey = new Button(), endKey = new Button(), enterKey = new Button(), numpadEnterKey = new Button();
    }
}

namespace UnityEngine {
    public enum EventType { KeyDown, ExecuteCommand, Layout, Repaint, Used }
    public enum KeyCode { None, A, V, C, X, Backspace, Delete, LeftArrow, RightArrow, Home, End, Return, KeypadEnter }
    public static class Time { public static int frameCount; }
    public class Event {
        public static Event current = new Event();
        public EventType type;
        public char character;
        public KeyCode keyCode;
        public bool control, command, shift;
        public string commandName;
        public void Use() { type = EventType.Used; }
    }

    public static class GUI {
        public static bool enabled = true;
        public static string focus, next;
        public static string GetNameOfFocusedControl() { return focus; }
        public static void SetNextControlName(string name) { next = name; }
        public static void FocusControl(string name) { focus = name; GUIUtility.keyboardControl++; }
    }

    public class GUILayoutOption {}
    public static class GUILayout {
        public static string TextField(string value, params GUILayoutOption[] options) { return Native(value, false); }
        public static string TextArea(string value, params GUILayoutOption[] options) { return Native(value, true); }
        static string Native(string value, bool multiline) {
            if (!GUI.enabled || GUI.next != GUI.focus) return value;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = value;
            var e = Event.current;
            if (e.type == EventType.ExecuteCommand && e.commandName == "Paste") editor.Paste();
            else if (e.type != EventType.KeyDown) return value;
            else if ((e.control || e.command) && e.keyCode == KeyCode.A) editor.SelectAll();
            else if ((e.control || e.command) && e.keyCode == KeyCode.V) editor.Paste();
            else if (e.keyCode == KeyCode.Backspace) editor.Backspace();
            else if (e.keyCode == KeyCode.Return && multiline) editor.Insert('\n');
            else if (!char.IsControl(e.character)) editor.Insert(e.character);
            else return value;
            e.Use();
            return editor.text;
        }
    }

    public static class GUIUtility {
        public static int keyboardControl;
        public static string systemCopyBuffer;
        static Dictionary<int, TextEditor> editors = new Dictionary<int, TextEditor>();
        public static object GetStateObject(Type type, int id) {
            if (!editors.ContainsKey(id)) editors.Add(id, new TextEditor());
            return editors[id];
        }
    }

    public class TextEditor {
        public string text = "";
        bool all;
        public void Insert(char c) { ReplaceSelection(c.ToString()); }
        public void ReplaceSelection(string value) { text = all ? value : text + value; all = false; }
        public void SelectAll() { all = true; }
        public bool Paste() { ReplaceSelection(GUIUtility.systemCopyBuffer); return true; }
        public void Backspace() {
            if (all) { text = ""; all = false; }
            else if (text.Length > 0) text = text.Substring(0, text.Length - 1);
        }
        public void Delete() { Backspace(); }
        public void Copy() { GUIUtility.systemCopyBuffer = text; }
        public void Cut() { Copy(); SelectAll(); Backspace(); }
        public void MoveLeft() {}
        public void MoveRight() {}
        public void SelectLeft() {}
        public void SelectRight() {}
        public void MoveTextStart() {}
        public void MoveTextEnd() {}
    }
}

class Test {
    static EditorTextInput input = new EditorTextInput();
    static Keyboard kb = Keyboard.current;
    static string name, value;
    static bool multiline;

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    static void Pass(Event e) {
        Event.current = e;
        input.BeginGUI();
        value = input.Draw(name, value, multiline);
        input.EndGUI();
    }
    static void Frame() {
        Time.frameCount++;
        Pass(new Event { type = EventType.Layout });
        Pass(new Event { type = EventType.Repaint });
    }
    static void Focus(string field, string initial = "", bool area = false) {
        name = field;
        value = initial;
        multiline = area;
        GUI.FocusControl(name);
        Frame();
    }
    static void Native(char c = '\0', KeyCode key = KeyCode.None, bool control = false) {
        Pass(new Event { type = EventType.KeyDown, character = c, keyCode = key, control = control });
        Check(Event.current.type == EventType.Used, "the native field processed the key");
    }
    static void Main() {
        input.Tick();
        Focus("VelSpawnQ");
        foreach (char c in "Iron Ingot") Native(c);
        Check(value == "Iron Ingot", "existing item search still types natively");

        Focus("TeleporterDestination", "1,2,3");
        Native(key: KeyCode.A, control: true);
        foreach (char c in "-596.447,275.657,240.869") Native(c);
        Check(value == "-596.447,275.657,240.869", "native coordinates, including minus and commas");

        Focus("TradeItemSearch");
        foreach (char c in "Iron Ingot") kb.Type(c);
        Pass(new Event { type = EventType.Repaint });
        Check(value == "", "wait for the native events in this frame");
        Frame();
        Check(value == "Iron Ingot", "fallback types the item name inside a normal layout pass");

        foreach (string field in new[] { "TradePrice", "TradeQuantity", "TradeStock", "TradeAddStock" }) {
            Focus(field);
            kb.Type('1'); Native('1');
            Native('1'); kb.Type('1');
            Frame();
            Check(value == "11", field + " gets one digit per press in either event order");
        }

        Focus("AdminBroadcast");
        foreach (char c in "Hello, town!") kb.Type(c);
        Frame();
        Check(value == "Hello, town!", "fallback supports ordinary sentences");
        kb.backspaceKey.wasPressedThisFrame = true;
        input.Tick();
        Native(key: KeyCode.Backspace);
        kb.backspaceKey.wasPressedThisFrame = false;
        Frame();
        Check(value == "Hello, town", "backspace also deduplicates");
        kb.ctrlKey.isPressed = kb.aKey.wasPressedThisFrame = kb.vKey.wasPressedThisFrame = true;
        GUIUtility.systemCopyBuffer = "  From clipboard\r\n";
        input.Tick();
        kb.ctrlKey.isPressed = kb.aKey.wasPressedThisFrame = kb.vKey.wasPressedThisFrame = false;
        Frame();
        Check(value == "From clipboard", "select-all and paste work on the fallback path");

        Focus("BoardMessage0", "First", area: true);
        kb.enterKey.wasPressedThisFrame = true;
        input.Tick();
        kb.enterKey.wasPressedThisFrame = false;
        foreach (char c in "Second") kb.Type(c);
        Frame();
        Check(value == "First\nSecond", "text areas preserve line breaks");
        Native(key: KeyCode.Return);
        kb.enterKey.wasPressedThisFrame = true;
        input.Tick();
        kb.enterKey.wasPressedThisFrame = false;
        Frame();
        Check(value == "First\nSecond\n", "Return is not applied twice");

        Focus("TradePrice");
        kb.Type('9');
        Focus("TradeStock", "1");
        Check(value == "1", "queued typing cannot cross to the next field");
        kb.Type('9');
        GUI.enabled = false;
        Frame();
        GUI.enabled = true;
        Check(!input.IsFocused && value == "1", "disabled fields neither edit nor keep focus");

        Focus("TradePrice");
        kb.Type('9');
        input.BeginGUI();
        input.EndGUI();
        Frame();
        Check(!input.IsFocused && value == "", "hidden field drops focus and pending keys");
        Focus("ConsoleCommand");
        kb.Type('9');
        input.Stop();
        kb.Type('9');
        Frame();
        Check(value == "", "stopped editor discards input");
        Console.WriteLine("PASS: native typing, Input System fallback, dual event order, paste, multiline and focus.");
    }
}
'''

with tempfile.TemporaryDirectory(prefix='coordinate-input-test-') as folder:
    folder = Path(folder)
    (folder / 'Test.cs').write_text(harness)
    exe = folder / 'Test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo',
                    '/out:' + str(exe), str(source), str(folder / 'Test.cs')], check=True)
    subprocess.run([str(exe)], check=True)
