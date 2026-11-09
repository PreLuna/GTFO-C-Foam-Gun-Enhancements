using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PreLuna.FoamGunCustom.Control
{
    public sealed class FoamGunControlConflictException : Exception
    {
        public FoamGunControlConflictException(string message) : base(message) { }
    }

    public readonly struct FoamGunControlWrite
    {
        public FoamGunControlWrite(string section, string key, string value)
        {
            Section = section ?? string.Empty;
            Key = key ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string Section { get; }
        public string Key { get; }
        public string Value { get; }
    }

    public static class FoamGunControlFileStore
    {
        public static string Capture(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            var bytes = File.ReadAllBytes(path);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public static string Save(string path, string expectedHash, IEnumerable<FoamGunControlWrite> writes)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("config path is empty", nameof(path));
            var pending = (writes ?? Enumerable.Empty<FoamGunControlWrite>()).ToArray();

            var current = Capture(path);
            if (expectedHash != null && !string.Equals(expectedHash, current, StringComparison.OrdinalIgnoreCase))
                throw new FoamGunControlConflictException("config file changed since the last revision");

            if (pending.Length == 0) return current;

            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            foreach (var write in pending) Apply(lines, write);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                    try { File.Delete(backup); } catch { }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }

            return Capture(path);
        }

        static void Apply(List<string> lines, FoamGunControlWrite write)
        {
            var section = FindSection(lines, write.Section);
            if (section < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0) lines.Add(string.Empty);
                lines.Add("[" + write.Section + "]");
                lines.Add(write.Key + " = " + write.Value);
                return;
            }

            var keyIndex = FindKey(lines, section, write.Key);
            if (keyIndex >= 0)
            {
                lines[keyIndex] = write.Key + " = " + write.Value;
                return;
            }

            var insertAt = SectionEnd(lines, section);
            lines.Insert(insertAt, write.Key + " = " + write.Value);
        }

        static int FindSection(List<string> lines, string section)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']' &&
                    string.Equals(line.Substring(1, line.Length - 2), section, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return -1;
        }

        static int FindKey(List<string> lines, int section, string key)
        {
            for (var index = section + 1; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']') break;
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return -1;
        }

        static int SectionEnd(List<string> lines, int section)
        {
            var index = section + 1;
            while (index < lines.Count)
            {
                var line = lines[index].Trim();
                if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']') break;
                index++;
            }

            while (index > section + 1 && lines[index - 1].Trim().Length == 0) index--;
            return index;
        }
    }
}
