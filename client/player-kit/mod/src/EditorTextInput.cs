using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PrefabEditorMod
{
    internal sealed class EditorTextInput
    {
        struct Edit
        {
            public string Key;
            public int Frame;
        }

        readonly List<Edit> pending = new List<Edit>();
        readonly List<string> nativeKeys = new List<string>();
        Keyboard keyboard;
        string focusedName;
        int focusedId, nativeFrame = -1;
        bool focusDrawn, multilineFocused;

        public bool IsFocused { get { return focusedName != null; } }

        public void Stop()
        {
            if (keyboard != null) keyboard.onTextInput -= Character;
            keyboard = null;
            ForgetFocus();
        }

        public void Blur()
        {
            GUI.FocusControl(null);
            ForgetFocus();
        }

        void ForgetFocus()
        {
            focusedName = null;
            focusedId = 0;
            multilineFocused = false;
            pending.Clear();
            nativeKeys.Clear();
        }

        public void BeginGUI() { focusDrawn = false; }

        public void EndGUI()
        {
            if (!IsFocused) return;
            if (GUIUtility.keyboardControl != focusedId || GUI.GetNameOfFocusedControl() != focusedName)
                ForgetFocus();
            else if (!focusDrawn)
                Blur();
        }

        public void Tick()
        {
            if (keyboard != Keyboard.current)
            {
                if (keyboard != null) keyboard.onTextInput -= Character;
                keyboard = Keyboard.current;
                if (keyboard != null) keyboard.onTextInput += Character;
            }
            if (!IsFocused || keyboard == null) return;
            if (keyboard.ctrlKey.isPressed)
            {
                if (keyboard.aKey.wasPressedThisFrame) Queue("all");
                if (keyboard.vKey.wasPressedThisFrame) Queue("paste");
                if (keyboard.cKey.wasPressedThisFrame) Queue("copy");
                if (keyboard.xKey.wasPressedThisFrame) Queue("cut");
            }
            bool shift = keyboard.shiftKey.isPressed;
            if (keyboard.backspaceKey.wasPressedThisFrame) Queue("backspace");
            if (keyboard.deleteKey.wasPressedThisFrame) Queue("delete");
            if (keyboard.leftArrowKey.wasPressedThisFrame) Queue(shift ? "selectLeft" : "left");
            if (keyboard.rightArrowKey.wasPressedThisFrame) Queue(shift ? "selectRight" : "right");
            if (keyboard.homeKey.wasPressedThisFrame) Queue("home");
            if (keyboard.endKey.wasPressedThisFrame) Queue("end");
            if (multilineFocused && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
                Queue("text:\n");
        }

        void Character(char c)
        {
            if (IsFocused && keyboard != null && !keyboard.ctrlKey.isPressed && !char.IsControl(c))
                Queue("text:" + c);
        }

        void StartInputFrame()
        {
            if (nativeFrame == Time.frameCount) return;
            nativeFrame = Time.frameCount;
            nativeKeys.Clear();
        }

        void Queue(string key)
        {
            StartInputFrame();
            if (!nativeKeys.Remove(key)) pending.Add(new Edit { Key = key, Frame = Time.frameCount });
        }

        void RecordNativeKey(string key)
        {
            StartInputFrame();
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Frame != Time.frameCount || pending[i].Key != key) continue;
                pending.RemoveAt(i);
                return;
            }
            nativeKeys.Add(key);
        }

        static string NativeKey(Event e, bool multiline)
        {
            if (e.type == EventType.ExecuteCommand)
            {
                switch (e.commandName)
                {
                    case "SelectAll": return "all";
                    case "Paste": return "paste";
                    case "Copy": return "copy";
                    case "Cut": return "cut";
                }
            }
            if (e.type != EventType.KeyDown) return null;
            if (e.control || e.command)
            {
                if (e.keyCode == KeyCode.A) return "all";
                if (e.keyCode == KeyCode.V) return "paste";
                if (e.keyCode == KeyCode.C) return "copy";
                if (e.keyCode == KeyCode.X) return "cut";
            }
            switch (e.keyCode)
            {
                case KeyCode.Backspace: return "backspace";
                case KeyCode.Delete: return "delete";
                case KeyCode.LeftArrow: return e.shift ? "selectLeft" : "left";
                case KeyCode.RightArrow: return e.shift ? "selectRight" : "right";
                case KeyCode.Home: return "home";
                case KeyCode.End: return "end";
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return multiline ? "text:\n" : null;
            }
            if (multiline && (e.character == '\r' || e.character == '\n')) return "text:\n";
            return char.IsControl(e.character) ? null : "text:" + e.character;
        }

        public static string CleanPaste(string value)
        {
            return (value ?? "").Trim().Replace("\r", "").Replace("\n", " ").Replace("\t", " ");
        }

        static void Apply(TextEditor editor, string key, bool multiline)
        {
            if (key.StartsWith("text:")) { editor.Insert(key[5]); return; }
            switch (key)
            {
                case "all": editor.SelectAll(); break;
                case "paste":
                    if (multiline) editor.Paste();
                    else editor.ReplaceSelection(CleanPaste(GUIUtility.systemCopyBuffer));
                    break;
                case "copy": editor.Copy(); break;
                case "cut": editor.Cut(); break;
                case "backspace": editor.Backspace(); break;
                case "delete": editor.Delete(); break;
                case "left": editor.MoveLeft(); break;
                case "right": editor.MoveRight(); break;
                case "selectLeft": editor.SelectLeft(); break;
                case "selectRight": editor.SelectRight(); break;
                case "home": editor.MoveTextStart(); break;
                case "end": editor.MoveTextEnd(); break;
            }
        }

        public string Draw(string name, string value, params GUILayoutOption[] options)
        {
            return Draw(name, value, false, options);
        }

        public string Draw(string name, string value, bool multiline, params GUILayoutOption[] options)
        {
            value = value ?? "";
            Event e = Event.current;
            if (!GUI.enabled && focusedName == name && GUIUtility.keyboardControl == focusedId)
                Blur();
            bool alreadyFocused = GUI.enabled && GUI.GetNameOfFocusedControl() == name;
            if (alreadyFocused && focusedName == name && GUIUtility.keyboardControl == focusedId &&
                e.type == EventType.Layout && pending.Count > 0 && pending[0].Frame < Time.frameCount)
            {
                // Give the native field the entire input frame to handle these keys.
                // Apply only undelivered keys, at Layout so dependent rows can resize.
                TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), focusedId);
                editor.text = value;
                int applied = 0;
                while (applied < pending.Count && pending[applied].Frame < Time.frameCount)
                    Apply(editor, pending[applied++].Key, multiline);
                pending.RemoveRange(0, applied);
                value = editor.text;
            }

            string native = alreadyFocused ? NativeKey(e, multiline) : null;
            GUI.SetNextControlName(name);
            value = multiline ? GUILayout.TextArea(value, options) : GUILayout.TextField(value, options);
            if (GUI.enabled && GUI.GetNameOfFocusedControl() == name)
            {
                if (focusedName != name || focusedId != GUIUtility.keyboardControl) ForgetFocus();
                focusedName = name;
                focusedId = GUIUtility.keyboardControl;
                focusDrawn = true;
                multilineFocused = multiline;
                if (native != null && e.type == EventType.Used) RecordNativeKey(native);
            }
            return value;
        }
    }
}
