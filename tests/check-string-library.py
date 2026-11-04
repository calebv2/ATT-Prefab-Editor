"""Compile the real string-library reader and exercise bounded data-only imports.

An optional directory argument audits the user's example collection without
copying any of its contents into the repository or distribution.
"""
from pathlib import Path
import subprocess
import sys
import tempfile

source = Path(__file__).resolve().parents[1] / 'client/player-kit/mod/src/StringLibrary.cs'
harness = r'''
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using PrefabEditorMod;

class Test
{
    const string A = "123,0,0,1065353216,0,0,0,0,0,0,0,";
    const string B = "456,0,0,1065353216,0,0,0,0,0,0,0,";
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static void Reject(Action action, string expected)
    {
        try { action(); }
        catch (Exception error) { Check(error.Message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0, "friendly limit: " + error.Message); return; }
        throw new Exception("Expected rejection: " + expected);
    }
    static void WriteSized(string path, int length)
    {
        using (FileStream file = new FileStream(path, FileMode.Create)) file.SetLength(length);
    }
    static void Main(string[] args)
    {
        string root = args[0];
        Directory.CreateDirectory(root);
        Check(!StringLibrary.IsSaveString(null) && !StringLibrary.IsSaveString("") && !StringLibrary.IsSaveString("12345678"), "missing separator refused");
        Check(StringLibrary.IsSaveString(A) && StringLibrary.IsSaveString("1,2|-3.5,+4"), "existing save charset retained");
        Check(!StringLibrary.IsSaveString("123,4;quit") && !StringLibrary.IsSaveString("123, 456"), "code and embedded whitespace refused");
        string atLimit = new string('1', StringLibrary.MaxSaveStringChars - 1) + ",";
        Check(StringLibrary.IsSaveString(atLimit) && !StringLibrary.IsSaveString(atLimit + "1"), "200000 character limit retained");

        var one = StringLibrary.Parse("\uFEFF  " + A + "  \r\n", "Sword", "source.txt");
        Check(one.Count == 1 && one[0].Name == "Sword" && one[0].SaveString == A && one[0].SourcePath == "source.txt" && one[0].Warning == "", "BOM/trim and source metadata");
        var separate = StringLibrary.Parse(A + "\n" + B, "Gear", "source");
        Check(separate.Count == 2 && separate[0].Name == "Gear - variant 1" && separate[1].Name == "Gear - variant 2", "separate physical lines remain variants");
        var labeled = StringLibrary.Parse("OG: " + A + "\nFixed:\n\n" + B, "Gear", "source");
        Check(labeled.Count == 2 && labeled[0].Name == "Gear - OG" && labeled[1].Name == "Gear - Fixed", "inline and previous-line labels retained");
        var warning = StringLibrary.Parse("warning: author's note\n" + A + "\nWARNING another note\n", "Gear", "source");
        Check(warning.Count == 1 && warning[0].Warning.Contains("author's note") && warning[0].Warning.Contains("another note"), "human warnings attached without execution");
        var longWarning = StringLibrary.Parse("WARNING " + new string('w', 1000) + "\n" + A, "Gear", "source");
        Check(longWarning[0].Warning.Length == 500, "warning bound");

        string marker = Path.Combine(root, "must-not-exist.txt");
        string document = A + "\nusing System;\nclass Generator {\n" + B + "\nFile.WriteAllText(@\"" + marker + "\", \"bad\");\n}\nWARNING human note after code";
        var withSource = StringLibrary.Parse(document, "Gear", "source");
        Check(withSource.Count == 1 && withSource[0].SaveString == A && withSource[0].Warning.Contains("human note after code") && !File.Exists(marker), "generator is never executed or parsed as strings; human warnings retained");
        Check(StringLibrary.Parse("\"" + A + "\"\nstring x = \"" + A + "\";\nPlease read this first", "Gear", "source").Count == 0, "quoted code/prose are not payloads");
        Check(StringLibrary.Parse("```csharp\n" + A + "\n```\nOG: " + B, "Gear", "source").Count == 1, "code fence ignored, later data allowed");
        Check(StringLibrary.Parse("```\n" + A + "\n```\nOG: " + B, "Gear", "source").Count == 1, "ambiguous bare fence is opaque");
        Check(StringLibrary.Parse("```javascript\nvar values = [\n" + A + "\n];\n```\nOG: " + B, "Gear", "source").Count == 1, "numeric source inside code fence ignored");
        Check(StringLibrary.Parse("```text\n" + A + "\n```\n```strings\n" + B + "\n```", "Gear", "source").Count == 2, "explicit data fences retain separate prefab variants");
        Check(StringLibrary.Parse("```text\nusing System;\n" + A + "\n```", "Gear", "source").Count == 0, "source detection still applies in data fence");
        Check(StringLibrary.Parse("", "Gear", "source").Count == 0 && StringLibrary.Parse(null, "Gear", "source").Count == 0, "empty files harmless");
        Reject(delegate { StringLibrary.Parse(new string('x', 10 * 1024 * 1024 + 1), "Clipboard", "clipboard"); }, "10 MB");

        string library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "b.txt"), B, Encoding.UTF8);
        File.WriteAllText(Path.Combine(library, "A.txt"), "OG: " + A, Encoding.UTF8);
        File.WriteAllText(Path.Combine(library, "README.txt"), "Instructions only, no payloads.");
        File.WriteAllText(Path.Combine(library, "ignored.json"), A);
        Directory.CreateDirectory(Path.Combine(library, "nested")); File.WriteAllText(Path.Combine(library, "nested", "hidden.txt"), A);
        WriteSized(Path.Combine(library, "too-large.txt"), 10 * 1024 * 1024 + 1);
        int skipped;
        var folder = StringLibrary.ReadFolder(library, out skipped);
        Check(folder.Count == 2 && folder[0].Name == "A - OG" && folder[1].Name == "b" && skipped == 2, "top-level sorted folder import and skipped count");
        Check(Path.IsPathRooted(folder[0].SourcePath), "absolute source paths");
        Check(StringLibrary.ReadFile("\"" + Path.Combine(library, "b.txt") + "\"")[0].SaveString == B, "quoted paths accepted");
        Reject(delegate { StringLibrary.ReadFile(Path.Combine(root, "missing.txt")); }, "not found");
        Reject(delegate { StringLibrary.ReadFile(Path.Combine(library, "too-large.txt")); }, "10 MB");
        Reject(delegate { int ignored; StringLibrary.ReadFolder(Path.Combine(root, "missing"), out ignored); }, "not found");

        var many = new StringBuilder(); for (int i = 0; i < 1001; i++) many.AppendLine(A);
        Reject(delegate { StringLibrary.Parse(many.ToString(), "Too many", "source"); }, "1,000 strings");
        string tooManyEntries = Path.Combine(root, "too-many-entries"); Directory.CreateDirectory(tooManyEntries);
        var half = new StringBuilder(); for (int i = 0; i < 600; i++) half.AppendLine(A);
        File.WriteAllText(Path.Combine(tooManyEntries, "a.txt"), half.ToString()); File.WriteAllText(Path.Combine(tooManyEntries, "b.txt"), half.ToString());
        Reject(delegate { int ignored; StringLibrary.ReadFolder(tooManyEntries, out ignored); }, "1,000 strings");
        string tooManyFiles = Path.Combine(root, "too-many-files"); Directory.CreateDirectory(tooManyFiles);
        for (int i = 0; i < 1001; i++) File.WriteAllText(Path.Combine(tooManyFiles, i + ".txt"), "");
        Reject(delegate { int ignored; StringLibrary.ReadFolder(tooManyFiles, out ignored); }, "1,000 text files");
        string tooManyBytes = Path.Combine(root, "too-many-bytes"); Directory.CreateDirectory(tooManyBytes);
        WriteSized(Path.Combine(tooManyBytes, "a.txt"), 8 * 1024 * 1024); WriteSized(Path.Combine(tooManyBytes, "b.txt"), 8 * 1024 * 1024); WriteSized(Path.Combine(tooManyBytes, "c.txt"), 8 * 1024 * 1024);
        Reject(delegate { int ignored; StringLibrary.ReadFolder(tooManyBytes, out ignored); }, "20 MB");
        Console.WriteLine("PASS: numeric strings, variants/labels/warnings, generator exclusion, paths, sorted top-level folders and file/entry/scan limits.");
        if (args.Length > 1)
        {
            int ignored; var real = StringLibrary.ReadFolder(args[1], out ignored); int longest = 0; var files = new HashSet<string>();
            foreach (var entry in real) { files.Add(entry.SourcePath); longest = Math.Max(longest, entry.SaveString.Length); }
            Check(real.Count == 102 && files.Count == 81 && ignored == 1 && longest == 36104, "provided collection audit counts");
            Console.WriteLine("PASS: provided collection has 81 string files, 102 selectable variants, longest 36104 characters and 1 skipped instruction file.");
        }
    }
}
'''
with tempfile.TemporaryDirectory(prefix='string-library-test-') as folder:
    root = Path(folder)
    (root / 'Test.cs').write_text(harness, encoding='utf-8')
    exe = root / 'test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo', '/out:' + str(exe), str(source), str(root / 'Test.cs')], check=True)
    args = [str(exe), str(root / 'data')]
    if len(sys.argv) > 1:
        args.append(sys.argv[1])
    subprocess.run(args, check=True)
