using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClipBridge
{
    internal sealed class FileManifestEntry
    {
        public string RelativePath;
        public string SourcePath;
        public bool IsDirectory;
        public long Length;
        public FileAttributes Attributes;
        public long CreationFileTimeUtc;
        public long LastAccessFileTimeUtc;
        public long LastWriteFileTimeUtc;
    }

    internal sealed class FileManifest
    {
        public readonly List<FileManifestEntry> Entries = new List<FileManifestEntry>();
        public long TotalBytes;

        public static FileManifest Build(IList<string> roots, int maxEntries)
        {
            FileManifest manifest = new FileManifest();
            HashSet<string> topLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawRoot in roots)
            {
                if (String.IsNullOrEmpty(rawRoot)) continue;
                string root = rawRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;

                if (File.Exists(root))
                {
                    string name = Path.GetFileName(root);
                    if (String.IsNullOrEmpty(name)) throw new IOException("Could not determine file name for " + root);
                    EnsureUniqueTopLevel(topLevelNames, name);
                    AddFile(manifest, root, name, maxEntries);
                }
                else if (Directory.Exists(root))
                {
                    DirectoryInfo di = new DirectoryInfo(root);
                    string name = di.Name;
                    if (String.IsNullOrEmpty(name) || name.EndsWith(":\\", StringComparison.Ordinal))
                        name = di.Root.Name.TrimEnd('\\', '/').Replace(":", String.Empty);
                    if (String.IsNullOrEmpty(name)) name = "Drive";
                    EnsureUniqueTopLevel(topLevelNames, name);
                    AddDirectoryRecursive(manifest, root, name, maxEntries);
                }
                else
                {
                    throw new FileNotFoundException("Clipboard item no longer exists.", root);
                }
            }

            return manifest;
        }

        private static void EnsureUniqueTopLevel(HashSet<string> names, string name)
        {
            if (!names.Add(name))
                throw new IOException("Two selected clipboard items have the same top-level name: " + name);
        }

        private static void AddDirectoryRecursive(FileManifest manifest, string sourcePath, string relativePath, int maxEntries)
        {
            AddDirectory(manifest, sourcePath, relativePath, maxEntries);

            string[] dirs = Directory.GetDirectories(sourcePath);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
                AddDirectoryRecursive(manifest, dir, CombineRelative(relativePath, Path.GetFileName(dir)), maxEntries);

            string[] files = Directory.GetFiles(sourcePath);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
                AddFile(manifest, file, CombineRelative(relativePath, Path.GetFileName(file)), maxEntries);
        }

        private static string CombineRelative(string left, string right)
        {
            return String.IsNullOrEmpty(left) ? right : left + "\\" + right;
        }

        private static void AddDirectory(FileManifest manifest, string sourcePath, string relativePath, int maxEntries)
        {
            EnsureCanAdd(manifest, relativePath, maxEntries);
            DirectoryInfo di = new DirectoryInfo(sourcePath);
            FileManifestEntry entry = new FileManifestEntry();
            entry.RelativePath = relativePath;
            entry.SourcePath = sourcePath;
            entry.IsDirectory = true;
            entry.Length = 0;
            entry.Attributes = di.Attributes | FileAttributes.Directory;
            entry.CreationFileTimeUtc = SafeFileTime(di.CreationTimeUtc);
            entry.LastAccessFileTimeUtc = SafeFileTime(di.LastAccessTimeUtc);
            entry.LastWriteFileTimeUtc = SafeFileTime(di.LastWriteTimeUtc);
            manifest.Entries.Add(entry);
        }

        private static void AddFile(FileManifest manifest, string sourcePath, string relativePath, int maxEntries)
        {
            EnsureCanAdd(manifest, relativePath, maxEntries);
            FileInfo fi = new FileInfo(sourcePath);
            FileManifestEntry entry = new FileManifestEntry();
            entry.RelativePath = relativePath;
            entry.SourcePath = sourcePath;
            entry.IsDirectory = false;
            entry.Length = fi.Length;
            entry.Attributes = fi.Attributes & ~FileAttributes.Directory;
            entry.CreationFileTimeUtc = SafeFileTime(fi.CreationTimeUtc);
            entry.LastAccessFileTimeUtc = SafeFileTime(fi.LastAccessTimeUtc);
            entry.LastWriteFileTimeUtc = SafeFileTime(fi.LastWriteTimeUtc);
            manifest.Entries.Add(entry);
            checked { manifest.TotalBytes += fi.Length; }
        }

        private static void EnsureCanAdd(FileManifest manifest, string relativePath, int maxEntries)
        {
            if (manifest.Entries.Count >= maxEntries)
                throw new IOException("The selection contains more than " + maxEntries + " files/folders, which exceeds the configured ClipBridge manifest limit.");
            if (String.IsNullOrEmpty(relativePath) || relativePath.Length >= 260)
                throw new PathTooLongException("Virtual clipboard path is too long for Explorer/Windows XP: " + relativePath);
        }

        private static long SafeFileTime(DateTime value)
        {
            try { return value.ToFileTimeUtc(); }
            catch { return 0L; }
        }

        public byte[] Serialize()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Entries.Count);
                w.Write(TotalBytes);
                foreach (FileManifestEntry e in Entries)
                {
                    w.Write(e.RelativePath == null ? String.Empty : e.RelativePath);
                    w.Write(e.IsDirectory);
                    w.Write(e.Length);
                    w.Write((int)e.Attributes);
                    w.Write(e.CreationFileTimeUtc);
                    w.Write(e.LastAccessFileTimeUtc);
                    w.Write(e.LastWriteFileTimeUtc);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static FileManifest Deserialize(byte[] data, int maxEntries)
        {
            FileManifest manifest = new FileManifest();
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                int count = r.ReadInt32();
                long total = r.ReadInt64();
                if (count < 0 || count > maxEntries) throw new InvalidDataException("Invalid file manifest item count.");
                if (total < 0) throw new InvalidDataException("Invalid file manifest total size.");

                for (int i = 0; i < count; i++)
                {
                    FileManifestEntry e = new FileManifestEntry();
                    e.RelativePath = r.ReadString();
                    e.IsDirectory = r.ReadBoolean();
                    e.Length = r.ReadInt64();
                    e.Attributes = (FileAttributes)r.ReadInt32();
                    e.CreationFileTimeUtc = r.ReadInt64();
                    e.LastAccessFileTimeUtc = r.ReadInt64();
                    e.LastWriteFileTimeUtc = r.ReadInt64();
                    if (String.IsNullOrEmpty(e.RelativePath) || e.RelativePath.Length >= 260 || e.Length < 0)
                        throw new InvalidDataException("Invalid file manifest entry.");
                    manifest.Entries.Add(e);
                }
                manifest.TotalBytes = total;
            }
            return manifest;
        }
    }
}
