using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClipBridge
{
    internal sealed class RemoteVirtualFileDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private const int DV_E_FORMATETC = unchecked((int)0x80040064);
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
        private const int STG_E_MEDIUMFULL = unchecked((int)0x80030070);
        private const int DATADIR_GET = 1;
        private const int DVASPECT_CONTENT = 1;
        private const int TYMED_HGLOBAL_VALUE = 1;
        private const int TYMED_ISTREAM_VALUE = 4;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;
        private const uint FD_ATTRIBUTES = 0x00000004;
        private const uint FD_CREATETIME = 0x00000008;
        private const uint FD_ACCESSTIME = 0x00000010;
        private const uint FD_WRITESTIME = 0x00000020;
        private const uint FD_FILESIZE = 0x00000040;
        private const uint FD_PROGRESSUI = 0x00004000;
        private const uint FD_UNICODE = 0x80000000;
        private const uint DROPEFFECT_COPY = 1;
        private const int FILEDESCRIPTORW_SIZE = 592;
        private const int FILE_NAME_OFFSET = 72;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private readonly long _timestamp;
        private readonly Func<long, FileManifest> _manifestProvider;
        private readonly Func<long, int, long, int, byte[]> _reader;
        private readonly Func<long, long, bool> _spaceChecker;
        private readonly Action<string> _activity;
        private readonly int _chunkBytes;
        private readonly short _formatFileGroupDescriptor;
        private readonly short _formatFileContents;
        private readonly short _formatPreferredDropEffect;
        private readonly object _sync = new object();
        private FileManifest _manifest;
        private bool _spaceChecked;
        private bool _spaceAccepted;

        public RemoteVirtualFileDataObject(long timestamp,
                                           Func<long, FileManifest> manifestProvider,
                                           Func<long, int, long, int, byte[]> reader,
                                           Func<long, long, bool> spaceChecker,
                                           Action<string> activity,
                                           int chunkBytes)
        {
            _timestamp = timestamp;
            _manifestProvider = manifestProvider;
            _reader = reader;
            _spaceChecker = spaceChecker;
            _activity = activity;
            _chunkBytes = chunkBytes;
            _formatFileGroupDescriptor = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
            _formatFileContents = unchecked((short)RegisterClipboardFormat("FileContents"));
            _formatPreferredDropEffect = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));
        }

        private FileManifest Manifest
        {
            get
            {
                lock (_sync)
                {
                    if (_manifest == null)
                    {
                        _manifest = _manifestProvider(_timestamp);
                        if (_manifest == null) throw new COMException("Remote file list is unavailable.", DV_E_FORMATETC);
                    }
                    return _manifest;
                }
            }
        }

        private void EnsureSpaceBeforeContents()
        {
            lock (_sync)
            {
                if (_spaceChecked)
                {
                    if (!_spaceAccepted) throw new COMException("Not enough free space on the destination drive.", STG_E_MEDIUMFULL);
                    return;
                }

                FileManifest manifest = Manifest;
                _spaceAccepted = _spaceChecker == null || _spaceChecker(_timestamp, manifest.TotalBytes);
                _spaceChecked = true;
                if (!_spaceAccepted) throw new COMException("Not enough free space on the destination drive.", STG_E_MEDIUMFULL);
            }
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (format.cfFormat == _formatFileGroupDescriptor && SupportsTymed(format.tymed, TYMED.TYMED_HGLOBAL))
            {
                byte[] bytes = BuildFileGroupDescriptor(Manifest);
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = AllocateHGlobal(bytes);
                medium.pUnkForRelease = null;
                return;
            }

            if (format.cfFormat == _formatPreferredDropEffect && SupportsTymed(format.tymed, TYMED.TYMED_HGLOBAL))
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = AllocateHGlobal(BitConverter.GetBytes(DROPEFFECT_COPY));
                medium.pUnkForRelease = null;
                return;
            }

            if (format.cfFormat == _formatFileContents && SupportsTymed(format.tymed, TYMED.TYMED_ISTREAM))
            {
                FileManifest manifest = Manifest;
                if (format.lindex < 0 || format.lindex >= manifest.Entries.Count)
                    throw new COMException("Invalid virtual file index.", DV_E_FORMATETC);

                EnsureSpaceBeforeContents();
                FileManifestEntry entry = manifest.Entries[format.lindex];
                if (entry.IsDirectory)
                {
                    TransferLog.Write("FILECONTENTS requested for directory index " + format.lindex + ": " + entry.RelativePath + " (rejected as directory)");
                    throw new COMException("Directories do not expose FILECONTENTS streams.", DV_E_FORMATETC);
                }
                IStream stream = new RemoteFileComStream(_timestamp, format.lindex, entry, _reader, _activity, _chunkBytes);

                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
                medium.pUnkForRelease = null;
                if (_activity != null && !entry.IsDirectory)
                    _activity("Streaming remote file directly to Explorer: " + entry.RelativePath);
                return;
            }

            throw new COMException("Clipboard format is not supported.", DV_E_FORMATETC);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        {
            throw new COMException("GetDataHere is not implemented.", E_NOTIMPL);
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return DV_E_FORMATETC;
            if (format.cfFormat == _formatFileGroupDescriptor && SupportsTymed(format.tymed, TYMED.TYMED_HGLOBAL)) return 0;
            if (format.cfFormat == _formatPreferredDropEffect && SupportsTymed(format.tymed, TYMED.TYMED_HGLOBAL)) return 0;
            if (format.cfFormat == _formatFileContents && SupportsTymed(format.tymed, TYMED.TYMED_ISTREAM))
            {
                if (format.lindex >= 0)
                {
                    FileManifest manifest = Manifest;
                    if (format.lindex >= manifest.Entries.Count || manifest.Entries[format.lindex].IsDirectory) return DV_E_FORMATETC;
                }
                return 0;
            }
            return DV_E_FORMATETC;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = IntPtr.Zero;
            return unchecked((int)0x00040130); // DATA_S_SAMEFORMATETC
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            throw new COMException("SetData is not supported.", E_NOTIMPL);
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if ((int)direction != DATADIR_GET) throw new COMException("Only DATADIR_GET is supported.", E_NOTIMPL);
            FORMATETC[] formats = new FORMATETC[3];
            formats[0] = MakeFormat(_formatFileGroupDescriptor, TYMED.TYMED_HGLOBAL, -1);
            formats[1] = MakeFormat(_formatFileContents, TYMED.TYMED_ISTREAM, -1);
            formats[2] = MakeFormat(_formatPreferredDropEffect, TYMED.TYMED_HGLOBAL, -1);
            return new FormatEtcEnumerator(formats);
        }

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        public void DUnadvise(int connection)
        {
            throw new COMException("Advisory connections are not supported.", OLE_E_ADVISENOTSUPPORTED);
        }

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        private static bool SupportsTymed(TYMED offered, TYMED wanted)
        {
            return (offered & wanted) != 0;
        }

        private static FORMATETC MakeFormat(short format, TYMED tymed, int lindex)
        {
            FORMATETC f = new FORMATETC();
            f.cfFormat = format;
            f.dwAspect = DVASPECT.DVASPECT_CONTENT;
            f.lindex = lindex;
            f.ptd = IntPtr.Zero;
            f.tymed = tymed;
            return f;
        }

        private static IntPtr AllocateHGlobal(byte[] bytes)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, new UIntPtr((uint)bytes.Length));
            if (h == IntPtr.Zero) throw new OutOfMemoryException();
            bool ok = false;
            try
            {
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) throw new COMException("GlobalLock failed.", Marshal.GetHRForLastWin32Error());
                try { Marshal.Copy(bytes, 0, p, bytes.Length); }
                finally { GlobalUnlock(h); }
                ok = true;
                return h;
            }
            finally
            {
                if (!ok) GlobalFree(h);
            }
        }

        private static byte[] BuildFileGroupDescriptor(FileManifest manifest)
        {
            checked
            {
                byte[] data = new byte[4 + (manifest.Entries.Count * FILEDESCRIPTORW_SIZE)];
                WriteUInt32(data, 0, (uint)manifest.Entries.Count);
                TransferLog.Write("Building FileGroupDescriptorW: " + manifest.Entries.Count + " entries, " + manifest.TotalBytes + " bytes total");

                for (int i = 0; i < manifest.Entries.Count; i++)
                {
                    FileManifestEntry e = manifest.Entries[i];
                    int o = 4 + (i * FILEDESCRIPTORW_SIZE);
                    uint flags = FD_ATTRIBUTES | FD_PROGRESSUI | FD_UNICODE;
                    if (!e.IsDirectory) flags |= FD_FILESIZE;
                    if (e.CreationFileTimeUtc != 0) flags |= FD_CREATETIME;
                    if (e.LastAccessFileTimeUtc != 0) flags |= FD_ACCESSTIME;
                    if (e.LastWriteFileTimeUtc != 0) flags |= FD_WRITESTIME;
                    WriteUInt32(data, o + 0, flags);
                    WriteUInt32(data, o + 36, (uint)e.Attributes);
                    WriteInt64(data, o + 40, e.CreationFileTimeUtc);
                    WriteInt64(data, o + 48, e.LastAccessFileTimeUtc);
                    WriteInt64(data, o + 56, e.LastWriteFileTimeUtc);
                    ulong length = (ulong)e.Length;
                    WriteUInt32(data, o + 64, (uint)(length >> 32));
                    WriteUInt32(data, o + 68, (uint)(length & 0xffffffff));

                    byte[] name = Encoding.Unicode.GetBytes(e.RelativePath + "\0");
                    if (name.Length > 520) throw new PathTooLongException(e.RelativePath);
                    Buffer.BlockCopy(name, 0, data, o + FILE_NAME_OFFSET, name.Length);
                }
                return data;
            }
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            byte[] b = BitConverter.GetBytes(value);
            Buffer.BlockCopy(b, 0, buffer, offset, 4);
        }

        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            byte[] b = BitConverter.GetBytes(value);
            Buffer.BlockCopy(b, 0, buffer, offset, 8);
        }
    }

    internal sealed class RemoteFileComStream : IStream
    {
        private const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
        private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
        private readonly long _timestamp;
        private readonly int _index;
        private readonly FileManifestEntry _entry;
        private readonly Func<long, int, long, int, byte[]> _reader;
        private readonly Action<string> _activity;
        private readonly int _chunkBytes;
        private long _position;
        private byte[] _cache;
        private long _cacheOffset;
        private int _cacheLength;

        public RemoteFileComStream(long timestamp, int index, FileManifestEntry entry,
                                   Func<long, int, long, int, byte[]> reader, Action<string> activity, int chunkBytes)
        {
            _timestamp = timestamp;
            _index = index;
            _entry = entry;
            _reader = reader;
            _activity = activity;
            _chunkBytes = Math.Max(64 * 1024, chunkBytes);
            TransferLog.Write("OPEN stream index=" + index + " size=" + entry.Length + " path=" + entry.RelativePath);
        }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int total = 0;
            while (total < cb && _position < _entry.Length)
            {
                if (!CacheContains(_position))
                {
                    int wanted = Math.Max(_chunkBytes, cb - total);
                    long remaining = _entry.Length - _position;
                    if (remaining < wanted) wanted = (int)remaining;
                    try
                    {
                        _cache = _reader(_timestamp, _index, _position, wanted);
                    }
                    catch (Exception ex)
                    {
                        string msg = "READ FAILED index=" + _index + " offset=" + _position + " wanted=" + wanted + " path=" + _entry.RelativePath + ": " + ex.Message;
                        TransferLog.Write(msg);
                        if (_activity != null) _activity(msg);
                        throw;
                    }
                    _cacheOffset = _position;
                    _cacheLength = _cache == null ? 0 : _cache.Length;
                    if (_cacheLength == 0)
                    {
                        if (_position < _entry.Length)
                        {
                            string msg = "UNEXPECTED EOF index=" + _index + " offset=" + _position + " expectedSize=" + _entry.Length + " path=" + _entry.RelativePath;
                            TransferLog.Write(msg);
                            if (_activity != null) _activity(msg);
                            throw new IOException("Remote file ended before its advertised size: " + _entry.RelativePath);
                        }
                        break;
                    }
                }

                int cacheIndex = (int)(_position - _cacheOffset);
                int available = _cacheLength - cacheIndex;
                int toCopy = Math.Min(cb - total, available);
                Buffer.BlockCopy(_cache, cacheIndex, pv, total, toCopy);
                total += toCopy;
                _position += toCopy;
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
        }

        private bool CacheContains(long position)
        {
            return _cache != null && position >= _cacheOffset && position < _cacheOffset + _cacheLength;
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            throw new COMException("Remote clipboard streams are read-only.", STG_E_ACCESSDENIED);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long next;
            if (dwOrigin == 0) next = dlibMove;
            else if (dwOrigin == 1) next = _position + dlibMove;
            else if (dwOrigin == 2) next = _entry.Length + dlibMove;
            else throw new COMException("Invalid seek origin.", STG_E_INVALIDFUNCTION);
            if (next < 0) throw new COMException("Cannot seek before the beginning of the file.", STG_E_INVALIDFUNCTION);
            _position = next;
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
        }

        public void SetSize(long libNewSize) { throw new COMException("Read-only stream.", STG_E_ACCESSDENIED); }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            long readTotal = 0;
            long writtenTotal = 0;
            byte[] buffer = new byte[Math.Min(_chunkBytes, 1024 * 1024)];
            IntPtr readPtr = Marshal.AllocHGlobal(4);
            IntPtr writtenPtr = Marshal.AllocHGlobal(4);
            try
            {
                while (readTotal < cb)
                {
                    int wanted = (int)Math.Min((long)buffer.Length, cb - readTotal);
                    byte[] chunk = wanted == buffer.Length ? buffer : new byte[wanted];
                    Read(chunk, wanted, readPtr);
                    int n = Marshal.ReadInt32(readPtr);
                    if (n <= 0) break;
                    pstm.Write(chunk, n, writtenPtr);
                    int w = Marshal.ReadInt32(writtenPtr);
                    readTotal += n;
                    writtenTotal += w;
                    if (w < n) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(readPtr);
                Marshal.FreeHGlobal(writtenPtr);
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, readTotal);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, writtenTotal);
        }

        public void Commit(int grfCommitFlags) { }
        public void Revert() { throw new COMException("Not supported.", STG_E_INVALIDFUNCTION); }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG();
            pstatstg.type = 2; // STGTY_STREAM
            pstatstg.cbSize = _entry.Length;
            pstatstg.grfMode = 0;
            if ((grfStatFlag & 1) == 0) pstatstg.pwcsName = _entry.RelativePath;
        }

        public void Clone(out IStream ppstm)
        {
            RemoteFileComStream clone = new RemoteFileComStream(_timestamp, _index, _entry, _reader, _activity, _chunkBytes);
            clone._position = _position;
            ppstm = clone;
        }
    }

    internal sealed class EmptyComStream : IStream
    {
        public void Read(byte[] pv, int cb, IntPtr pcbRead) { if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, 0); }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) { throw new COMException("Read-only stream.", unchecked((int)0x80030005)); }
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) { if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, 0); }
        public void SetSize(long libNewSize) { }
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) { if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, 0); if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, 0); }
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag) { pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG(); pstatstg.type = 2; pstatstg.cbSize = 0; }
        public void Clone(out IStream ppstm) { ppstm = new EmptyComStream(); }
    }

    internal sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _index;

        public FormatEtcEnumerator(FORMATETC[] formats) : this(formats, 0) { }
        private FormatEtcEnumerator(FORMATETC[] formats, int index) { _formats = formats; _index = index; }

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            int fetched = 0;
            while (fetched < celt && _index < _formats.Length)
            {
                rgelt[fetched] = _formats[_index];
                fetched++;
                _index++;
            }
            if (pceltFetched != null && pceltFetched.Length > 0) pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _index = Math.Min(_formats.Length, _index + celt);
            return _index < _formats.Length ? 0 : 1;
        }

        public int Reset() { _index = 0; return 0; }
        public void Clone(out IEnumFORMATETC newEnum) { newEnum = new FormatEtcEnumerator(_formats, _index); }
    }

    internal static class DestinationSpaceChecker
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        public static bool TryGetActiveLocalExplorerDestination(out string path, out long availableBytes)
        {
            path = null;
            availableBytes = 0;
            IntPtr foreground = GetForegroundWindow();

            try
            {
                if (foreground == GetShellWindow())
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (TryGetFreeSpaceForPath(desktop, out availableBytes))
                    {
                        path = desktop;
                        return true;
                    }
                }

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                object shell = null;
                object windows = null;
                try
                {
                    shell = Activator.CreateInstance(shellType);
                    windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                    int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));
                    for (int i = 0; i < count; i++)
                    {
                        object window = null;
                        object document = null;
                        object folder = null;
                        object self = null;
                        try
                        {
                            window = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                            if (window == null) continue;
                            long hwnd = Convert.ToInt64(window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null));
                            if (new IntPtr(hwnd) != foreground) continue;

                            document = window.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, window, null);
                            if (document == null) continue;
                            folder = document.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, document, null);
                            if (folder == null) continue;
                            self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                            if (self == null) continue;
                            string candidate = Convert.ToString(self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null));
                            if (TryGetFreeSpaceForPath(candidate, out availableBytes))
                            {
                                path = candidate;
                                return true;
                            }
                        }
                        catch { }
                        finally
                        {
                            ReleaseCom(self); ReleaseCom(folder); ReleaseCom(document); ReleaseCom(window);
                        }
                    }
                }
                finally
                {
                    ReleaseCom(windows); ReleaseCom(shell);
                }
            }
            catch { }
            return false;
        }

        public static bool TryGetFreeSpaceForPath(string path, out long availableBytes)
        {
            availableBytes = 0;
            try
            {
                if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) return false;
                string root = Path.GetPathRoot(path);
                if (String.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return false;
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady) return false;
                availableBytes = drive.AvailableFreeSpace;
                return true;
            }
            catch { return false; }
        }

        private static void ReleaseCom(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); } catch { }
            }
        }
    }
}
