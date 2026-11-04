"""Compile and exercise the real in-memory console state without Unity."""
from pathlib import Path
import subprocess
import sys
import tempfile

source = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / 'client/player-kit/mod/src/ConsoleSession.cs'
harness = r'''
using System;
using PrefabEditorMod;

class Test
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static void Complete(ConsoleSession session, string command)
    {
        Check(session.Submit(command, delegate(string sent, Action<bool, string> done) { done(true, "ok"); }), "command accepted: " + command);
        Check(!session.Busy, "synchronous completion releases busy");
    }
    static void Main()
    {
        var session = new ConsoleSession();
        int calls = 0;
        Action<string, Action<bool, string>> rejectSender = delegate { calls++; };
        foreach (string invalid in new string[] { null, "", "   ", "help\nplayer list", "help\rplayer list", "help\tplayer", "help\0player", new string('x', 4097) })
            Check(!session.Submit(invalid, rejectSender), "invalid command was accepted");
        Check(calls == 0 && !session.Busy && !session.HasPrevious && session.Output == "", "rejected commands cannot dispatch or enter history/output");

        Action<bool, string> first = null;
        string observed = null;
        Check(session.Submit("  player list  ", delegate(string sent, Action<bool, string> done) { calls++; observed = sent; first = done; }), "valid command accepted");
        Check(calls == 1 && observed == "player list" && session.Busy, "send once with trimmed command");
        Check(!session.Submit("save", rejectSender) && calls == 1, "one request at a time");
        Check(session.Output.Contains("> player list"), "command appears in session output");
        first(true, "Alice, Bob");
        Check(!session.Busy && session.Output.Contains("Alice, Bob") && session.Status == "Command completed.", "successful response displayed");
        string beforeDuplicate = session.Output;
        first(false, "must not appear");
        Check(session.Output == beforeDuplicate && !session.Busy, "duplicate callback ignored");

        Action<bool, string> second = null;
        Check(session.Submit("save", delegate(string sent, Action<bool, string> done) { calls++; second = done; }), "second request accepted");
        first(true, "late duplicate");
        Check(session.Busy && session.Output.IndexOf("late duplicate") < 0 && calls == 2, "old callback cannot finish a newer request");
        second(false, "Timed out; outcome may be unknown. Check the server before retrying.");
        Check(!session.Busy && session.Output.Contains("ERROR: Timed out") && calls == 2, "ambiguous failure displayed with no replay");
        string afterFailure = session.Output;
        Check(session.Output == afterFailure && calls == 2, "reading state does not resubmit");
        int syncFailures = 0;
        Check(session.Submit("help", delegate { syncFailures++; throw new InvalidOperationException("secret transport detail"); }), "attempt accepted before transport throws");
        Check(syncFailures == 1 && !session.Busy && session.Output.Contains("outcome may be unknown"), "synchronous send error releases busy without retry");
        Check(!session.Output.Contains("secret transport detail"), "transport exception internals are not echoed");

        Check(ConsoleSession.Reply(true, "plain text", "{\"count\":2}", null, null) == "plain text", "plain command output");
        Check(ConsoleSession.Reply(true, null, "{\"count\":2}", null, null) == "{\"count\":2}", "structured-only command output");
        Check(ConsoleSession.Reply(false, "ignored", "{}", "Native command failed", "transport") == "Native command failed", "native exception has priority");
        Check(ConsoleSession.Reply(false, null, null, null, "Connection lost; outcome may be unknown") == "Connection lost; outcome may be unknown", "transport failure output");
        Check(ConsoleSession.Reply(true, null, "null", null, null).Contains("no output"), "successful command with no output is clear");
        Check(ConsoleSession.Reply(false, null, "null", null, null).Contains("Check the server"), "failure without confirming reply does not claim success");

        var history = new ConsoleSession();
        Complete(history, "first"); Complete(history, "second");
        Check(history.Previous("unfinished draft") == "second" && history.Previous("second") == "first", "previous history navigation");
        Check(!history.HasPrevious && history.Previous("first") == "first", "history beginning stays bounded");
        Check(history.Next("first") == "second" && history.Next("second") == "unfinished draft", "forward navigation restores unsent draft");
        Check(!history.HasNext && history.Next("new draft") == "new draft", "history end preserves current draft");
        var duplicateHistory = new ConsoleSession();
        Complete(duplicateHistory, "help"); Complete(duplicateHistory, "help");
        Check(duplicateHistory.Previous("draft") == "help" && !duplicateHistory.HasPrevious, "consecutive duplicate history entries omitted without blocking execution");

        var boundedHistory = new ConsoleSession();
        for (int i = 0; i < 60; i++) Complete(boundedHistory, "command " + i);
        int count = 0; string item = "draft";
        while (boundedHistory.HasPrevious) { item = boundedHistory.Previous(item); count++; }
        Check(count == 50 && item == "command 10", "history keeps only latest fifty commands");
        var fresh = new ConsoleSession();
        Check(!fresh.HasPrevious && fresh.Output == "", "new game session has no retained history or output");

        var output = new ConsoleSession();
        Check(output.Submit("help", delegate(string sent, Action<bool, string> done) { done(true, new string('z', 40000) + "LATEST"); }), "large result accepted");
        Check(output.Output.Length == 32768 && output.Output.EndsWith("LATEST"), "output retains latest bounded text");
        output.ClearOutput();
        Check(output.Output == "" && output.HasPrevious, "clear output preserves command history");
        Action<bool, string> pending = null;
        output.Submit("status", delegate(string sent, Action<bool, string> done) { pending = done; });
        output.ClearOutput();
        Check(output.Busy, "clear output cannot replay or cancel pending command");
        pending(true, "ready");
        Check(!output.Busy && output.Output == "ready", "pending result arrives once after clear");
        Console.WriteLine("PASS: console validation, single in-flight dispatch, callback isolation, failure handling, replies, bounded history/draft and output.");
    }
}
'''
with tempfile.TemporaryDirectory(prefix='console-session-test-') as folder:
    root = Path(folder)
    test = root / 'Test.cs'
    test.write_text(harness, encoding='utf-8')
    exe = root / 'Test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo', '/out:' + str(exe), str(source), str(test)], check=True)
    subprocess.run([str(exe)], check=True)
