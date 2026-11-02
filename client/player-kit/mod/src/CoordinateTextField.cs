using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PrefabEditorMod
{
    // Own keyboard input for this field: IMGUI key events are not reliably supplied
    // by the game's new Input System. Keep IMGUI's caret and mouse selection.
    internal sealed class CoordinateTextField
    {
        readonly string controlName;
        readonly bool numeric;
        public bool IsFocused { get { return focused; } }
        public CoordinateTextField(string name = "TeleporterDestination", bool numbersOnly = true)
        { controlName = name; numeric = numbersOnly; }
        Keyboard keyboard;
        bool focused;
        readonly Queue<string> pending = new Queue<string>();
        readonly Dictionary<string, int> primaryInput = new Dictionary<string, int>();
        readonly Dictionary<string, int> legacyInput = new Dictionary<string, int>();
        int inputFrame = -1;
        public void Stop()
        {
            if (keyboard != null) keyboard.onTextInput -= Character;
            keyboard = null;
            focused = false;
            pending.Clear();
            primaryInput.Clear();
            legacyInput.Clear();
            inputFrame = -1;
        }
        void Character(char c)
        {
            if (!focused || keyboard == null || keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed) return;
            if (numeric ? Allowed(c) : !char.IsControl(c)) QueueInput("text:" + c, false);
        }
        void QueueInput(string key, bool legacy)
        {
            if (inputFrame != Time.frameCount)
            {
                primaryInput.Clear();
                legacyInput.Clear();
                inputFrame = Time.frameCount;
            }
            // IMGUI may deliver the same key before or after the Input System,
            // including after a Layout/Repaint has already drained pending.
            Dictionary<string, int> other = legacy ? primaryInput : legacyInput;
            int count;
            if (other.TryGetValue(key, out count) && count > 0)
            {
                other[key] = count - 1;
                return;
            }
            Dictionary<string, int> own = legacy ? legacyInput : primaryInput;
            own.TryGetValue(key, out count);
            own[key] = count + 1;
            pending.Enqueue(key);
        }
        void LegacyKey(Event e)
        {
            if (e.control || e.command)
            {
                if (e.keyCode == KeyCode.A) QueueInput("all", true);
                else if (e.keyCode == KeyCode.V) QueueInput("paste", true);
                else if (e.keyCode == KeyCode.C) QueueInput("copy", true);
                else if (e.keyCode == KeyCode.X) QueueInput("cut", true);
                return;
            }
            if (e.alt) return;
            if (e.keyCode == KeyCode.Backspace) QueueInput("backspace", true);
            else if (e.keyCode == KeyCode.Delete) QueueInput("delete", true);
            else if (e.keyCode == KeyCode.LeftArrow) QueueInput(e.shift ? "selectLeft" : "left", true);
            else if (e.keyCode == KeyCode.RightArrow) QueueInput(e.shift ? "selectRight" : "right", true);
            else if (e.keyCode == KeyCode.Home) QueueInput("home", true);
            else if (e.keyCode == KeyCode.End) QueueInput("end", true);
            else if (e.character != '\0' && (numeric ? Allowed(e.character) : !char.IsControl(e.character)))
                QueueInput("text:" + e.character, true);
        }
        static bool Allowed(char c)
        {
            return (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == ',' || c == ' ';
        }
        public static string CleanPaste(string value)
        {
            return (value ?? "").Trim().Replace("\r", "").Replace("\n", " ").Replace("\t", " ");
        }
        public void Tick(bool active)
        {
            if (!active) { Stop(); return; }
            if (keyboard != Keyboard.current)
            {
                Stop(); keyboard = Keyboard.current;
                if (keyboard != null) keyboard.onTextInput += Character;
            }
            if (!focused || keyboard == null) return;
            if (keyboard.ctrlKey.isPressed)
            {
                if (keyboard.aKey.wasPressedThisFrame) QueueInput("all", false);
                if (keyboard.vKey.wasPressedThisFrame) QueueInput("paste", false);
                if (keyboard.cKey.wasPressedThisFrame) QueueInput("copy", false);
                if (keyboard.xKey.wasPressedThisFrame) QueueInput("cut", false);
            }
            if (keyboard.backspaceKey.wasPressedThisFrame) QueueInput("backspace", false);
            if (keyboard.deleteKey.wasPressedThisFrame) QueueInput("delete", false);
            if (keyboard.leftArrowKey.wasPressedThisFrame) QueueInput(keyboard.shiftKey.isPressed ? "selectLeft" : "left", false);
            if (keyboard.rightArrowKey.wasPressedThisFrame) QueueInput(keyboard.shiftKey.isPressed ? "selectRight" : "right", false);
            if (keyboard.homeKey.wasPressedThisFrame) QueueInput("home", false);
            if (keyboard.endKey.wasPressedThisFrame) QueueInput("end", false);
        }
        public string Draw(string value)
        {
            string name = controlName;
            // Some desktop setups supply IMGUI text but no onTextInput callback.
            // Accept either stream, then stop TextField applying a key twice.
            Event e = Event.current;
            if (GUI.enabled && GUI.GetNameOfFocusedControl() == name && e != null &&
                (e.type == EventType.KeyDown || e.type == EventType.KeyUp))
            {
                if (e.type == EventType.KeyDown) LegacyKey(e);
                e.Use();
            }
            GUI.SetNextControlName(name);
            value = GUILayout.TextField(value ?? "");
            focused = GUI.enabled && GUI.GetNameOfFocusedControl() == name;
            if (!focused) { pending.Clear(); return value; }
            TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = value;
            while (pending.Count > 0)
            {
                string key = pending.Dequeue();
                if (key.StartsWith("text:")) editor.Insert(key[5]);
                else if (key == "all") editor.SelectAll();
                else if (key == "paste") editor.ReplaceSelection(CleanPaste(GUIUtility.systemCopyBuffer));
                else if (key == "copy") editor.Copy();
                else if (key == "cut") editor.Cut();
                else if (key == "backspace") editor.Backspace();
                else if (key == "delete") editor.Delete();
                else if (key == "left") editor.MoveLeft();
                else if (key == "right") editor.MoveRight();
                else if (key == "selectLeft") editor.SelectLeft();
                else if (key == "selectRight") editor.SelectRight();
                else if (key == "home") editor.MoveTextStart();
                else if (key == "end") editor.MoveTextEnd();
            }
            return editor.text;
        }
    }
}
