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
        public void Stop()
        {
            if (keyboard != null) keyboard.onTextInput -= Character;
            keyboard = null;
            focused = false;
            pending.Clear();
        }
        void Character(char c)
        {
            if (!focused || keyboard == null || keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed) return;
            if (numeric ? Allowed(c) : !char.IsControl(c)) pending.Enqueue("text:" + c);
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
                if (keyboard.aKey.wasPressedThisFrame) pending.Enqueue("all");
                if (keyboard.vKey.wasPressedThisFrame) pending.Enqueue("paste");
                if (keyboard.cKey.wasPressedThisFrame) pending.Enqueue("copy");
                if (keyboard.xKey.wasPressedThisFrame) pending.Enqueue("cut");
            }
            if (keyboard.backspaceKey.wasPressedThisFrame) pending.Enqueue("backspace");
            if (keyboard.deleteKey.wasPressedThisFrame) pending.Enqueue("delete");
            if (keyboard.leftArrowKey.wasPressedThisFrame) pending.Enqueue(keyboard.shiftKey.isPressed ? "selectLeft" : "left");
            if (keyboard.rightArrowKey.wasPressedThisFrame) pending.Enqueue(keyboard.shiftKey.isPressed ? "selectRight" : "right");
            if (keyboard.homeKey.wasPressedThisFrame) pending.Enqueue("home");
            if (keyboard.endKey.wasPressedThisFrame) pending.Enqueue("end");
        }
        public string Draw(string value)
        {
            string name = controlName;
            // Do not also apply Unity's legacy key event when it happens to exist.
            if (GUI.GetNameOfFocusedControl() == name &&
                (Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp)) Event.current.Use();
            GUI.SetNextControlName(name);
            value = GUILayout.TextField(value ?? "");
            focused = GUI.GetNameOfFocusedControl() == name;
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
