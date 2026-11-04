using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PrefabEditorMod
{
    internal sealed class StringLibraryEntry
    {
        public string Name, SaveString, SourcePath, Warning;
    }

    // Text documents are data. Never execute a generator or join separate variants.
    internal static class StringLibrary
    {
        public const int MaxSaveStringChars = 200000;
        const long MaxFileBytes = 10L * 1024 * 1024;
        const long MaxFolderBytes = 20L * 1024 * 1024;
        const int MaxFiles = 1000, MaxEntries = 1000, MaxWarningChars = 500;

        sealed class LibraryLimitException : Exception
        {
            public LibraryLimitException(string message) : base(message) { }
        }

        public static bool IsSaveString(string value)
        {
            if (value == null || value.Length < 8 || value.Length > MaxSaveStringChars ||
                (value.IndexOf(',') < 0 && value.IndexOf('|') < 0)) return false;
            foreach (char c in value)
                if ((c < '0' || c > '9') && c != ',' && c != '|' && c != '.' && c != '-' && c != '+')
                    return false;
            return true;
        }

        static bool IsLabel(string label)
        {
            if (label.Length == 0 || label.Length > 80) return false;
            foreach (char c in label)
                if (char.IsControl(c) || ",:;=\"{}[]<>\\|".IndexOf(c) >= 0) return false;
            return true;
        }

        static bool StartsSource(string line)
        {
            return (line.StartsWith("using ", StringComparison.Ordinal) && line.EndsWith(";", StringComparison.Ordinal))
                || line.StartsWith("namespace ", StringComparison.Ordinal)
                || line.StartsWith("\"use strict\"", StringComparison.Ordinal)
                || line.StartsWith("'use strict'", StringComparison.Ordinal)
                || line.StartsWith("function ", StringComparison.Ordinal)
                || line.StartsWith("var ", StringComparison.Ordinal)
                || line.StartsWith("const ", StringComparison.Ordinal)
                || line.StartsWith("let ", StringComparison.Ordinal)
                || line.StartsWith("class ", StringComparison.Ordinal)
                || line.StartsWith("public class ", StringComparison.Ordinal)
                || line.StartsWith("public static class ", StringComparison.Ordinal)
                || line.StartsWith("import ", StringComparison.Ordinal)
                || line.StartsWith("export ", StringComparison.Ordinal);
        }

        public static List<StringLibraryEntry> Parse(string contents, string name, string sourcePath)
        {
            if (contents != null && contents.Length > MaxFileBytes)
                throw new InvalidDataException("String text is too large (10 MB text limit). Paste fewer strings or choose a smaller file.");
            List<StringLibraryEntry> entries = new List<StringLibraryEntry>();
            List<string> labels = new List<string>();
            string title = string.IsNullOrEmpty(name) ? "String" : name.Trim();
            if (title.Length == 0) title = "String";
            string previousLabel = null, warning = "";
            bool source = false, inFence = false, dataFence = false;
            using (StringReader reader = new StringReader(contents ?? ""))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Trim().TrimStart('\uFEFF').Trim();
                    if (line.StartsWith("```", StringComparison.Ordinal))
                    {
                        if (inFence) { inFence = false; dataFence = false; }
                        else
                        {
                            inFence = true;
                            string kind = line.Substring(3).Trim().ToLowerInvariant();
                            dataFence = kind == "text" || kind == "txt" || kind == "string" ||
                                kind == "strings" || kind == "prefab" || kind == "prefabs";
                        }
                        previousLabel = null;
                        continue;
                    }
                    if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                    {
                        warning += (warning.Length == 0 ? "" : "\n") + line;
                        if (warning.Length > MaxWarningChars) warning = warning.Substring(0, MaxWarningChars);
                        previousLabel = null;
                        continue;
                    }
                    if (source || (inFence && !dataFence)) continue;
                    if (StartsSource(line)) { source = true; previousLabel = null; continue; }
                    if (line.Length == 0) continue;
                    string data = line, label = null;
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        string candidate = line.Substring(0, colon).Trim();
                        if (IsLabel(candidate))
                        {
                            label = candidate;
                            data = line.Substring(colon + 1).Trim();
                            if (data.Length == 0) { previousLabel = label; continue; }
                        }
                    }
                    if (!IsSaveString(data)) { previousLabel = null; continue; }
                    if (entries.Count >= MaxEntries)
                        throw new LibraryLimitException("This file contains more than 1,000 strings. Split it into smaller files.");
                    entries.Add(new StringLibraryEntry { SaveString = data, SourcePath = sourcePath ?? "" });
                    labels.Add(label ?? previousLabel);
                    previousLabel = null;
                }
            }
            for (int i = 0; i < entries.Count; i++)
            {
                string label = labels[i];
                if (string.IsNullOrEmpty(label) && entries.Count > 1) label = "variant " + (i + 1);
                entries[i].Name = title + (string.IsNullOrEmpty(label) ? "" : " - " + label);
                entries[i].Warning = warning;
            }
            return entries;
        }

        static string FullPath(string path)
        {
            string value = (path ?? "").Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                (value[0] == '\'' && value[value.Length - 1] == '\''))) value = value.Substring(1, value.Length - 2).Trim();
            if (value.Length == 0) throw new ArgumentException("Choose a string file or folder first.");
            return Path.GetFullPath(value);
        }

        static List<StringLibraryEntry> ReadFile(string path, long remainingBytes, out long bytes)
        {
            bytes = 0;
            if (!File.Exists(path)) throw new FileNotFoundException("String file not found: " + path, path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxFileBytes) throw new InvalidDataException("String files must be no larger than 10 MB.");
                if (stream.Length > remainingBytes)
                    throw new LibraryLimitException("This folder contains more than 20 MB of text. Choose a smaller folder.");
                bytes = stream.Length;
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    return Parse(reader.ReadToEnd(), Path.GetFileNameWithoutExtension(path), path);
            }
        }

        public static List<StringLibraryEntry> ReadFile(string path)
        {
            long bytes;
            return ReadFile(FullPath(path), MaxFileBytes, out bytes);
        }

        public static List<StringLibraryEntry> ReadFolder(string path, out int skippedFiles)
        {
            skippedFiles = 0;
            string folder = FullPath(path);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("String folder not found: " + folder);
            string[] files = Directory.GetFiles(folder, "*.txt", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length > MaxFiles)
                throw new LibraryLimitException("This folder contains more than 1,000 text files. Choose a smaller folder.");
            List<StringLibraryEntry> result = new List<StringLibraryEntry>();
            long scanned = 0;
            foreach (string file in files)
            {
                long bytes = 0;
                List<StringLibraryEntry> entries;
                try { entries = ReadFile(file, MaxFolderBytes - scanned, out bytes); }
                catch (InvalidDataException) { scanned += bytes; skippedFiles++; continue; }
                catch (IOException) { scanned += bytes; skippedFiles++; continue; }
                catch (UnauthorizedAccessException) { scanned += bytes; skippedFiles++; continue; }
                scanned += bytes;
                if (entries.Count == 0) { skippedFiles++; continue; }
                if (result.Count + entries.Count > MaxEntries)
                    throw new LibraryLimitException("This folder contains more than 1,000 strings. Choose a smaller folder.");
                result.AddRange(entries);
            }
            return result;
        }
    }
}
