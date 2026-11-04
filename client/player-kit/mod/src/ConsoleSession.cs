using System;
using System.Collections.Generic;

namespace PrefabEditorMod
{
    // Session-only command history. Never write commands or replies to disk/logs.
    internal sealed class ConsoleSession
    {
        internal const int MaxCommandLength = 4096, MaxOutputLength = 32768, MaxHistory = 50;
        readonly List<string> history = new List<string>();
        int cursor;
        string unsent = "";
        public bool Busy { get; private set; }
        public string Output { get; private set; }
        public string Status { get; private set; }
        public bool HasPrevious { get { return cursor > 0; } }
        public bool HasNext { get { return cursor < history.Count; } }

        public ConsoleSession() { Output = ""; Status = "Enter one server command and click Run command."; }

        public bool Submit(string input, Action<string, Action<bool, string>> send)
        {
            if (Busy) return false;
            string command = (input ?? "").Trim();
            if (command.Length == 0) { Status = "Enter a command first."; return false; }
            if (command.Length > MaxCommandLength) { Status = "Maximum 4096 characters per command."; return false; }
            foreach (char c in command)
                if (char.IsControl(c)) { Status = "Enter one command on a single line."; return false; }
            if (history.Count == 0 || history[history.Count - 1] != command) history.Add(command);
            if (history.Count > MaxHistory) history.RemoveAt(0);
            cursor = history.Count;
            unsent = "";
            Append("> " + command);
            Busy = true;
            Status = "Running command...";
            bool completed = false;
            Action<bool, string> done = delegate(bool ok, string text)
            {
                if (completed) return;
                completed = true;
                Busy = false;
                Status = ok ? "Command completed." : "Command failed. Review the reply before trying again.";
                Append((ok ? "" : "ERROR: ") + (string.IsNullOrEmpty(text) ? "No output returned." : text));
            };
            try { send(command, done); }
            catch (Exception) { done(false, "Could not complete the request. The outcome may be unknown; check the server before retrying."); }
            return true;
        }

        void Append(string text)
        {
            Output += (Output.Length == 0 ? "" : "\n\n") + text;
            if (Output.Length > MaxOutputLength) Output = Output.Substring(Output.Length - MaxOutputLength);
        }
        public void ClearOutput() { Output = ""; }
        public string Previous(string draft)
        {
            if (!HasPrevious) return draft;
            if (cursor == history.Count) unsent = draft ?? "";
            return history[--cursor];
        }
        public string Next(string draft)
        {
            if (!HasNext) return draft;
            cursor++;
            return cursor == history.Count ? unsent : history[cursor];
        }
        public static string Reply(bool ok, string text, string structured, string exception, string error)
        {
            if (!string.IsNullOrEmpty(exception)) return exception;
            if (!string.IsNullOrEmpty(error)) return error;
            if (!string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(structured) && structured != "null") return structured;
            return ok ? "Command completed; no output returned." : "No reply confirming success. Check the server before retrying.";
        }
    }
}
