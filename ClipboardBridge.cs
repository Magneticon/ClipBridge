using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClipBridge
{
    internal sealed class ClipboardBridge : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("ole32.dll")]
        private static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject pDataObj);

        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint CF_TEXT = 1;
        private const uint CF_DIB = 8;
        private const uint CF_UNICODETEXT = 13;

        private readonly BridgeConfig _config;
        private readonly NetworkBridge _network;
        private readonly Control _uiControl;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly object _stateLock = new object();
        private readonly object _requestsLock = new object();
        private readonly Dictionary<int, PendingRequest> _pendingRequests = new Dictionary<int, PendingRequest>();
        private readonly Dictionary<long, string> _ownersByTimestamp = new Dictionary<long, string>();

        private uint _lastSequence;
        private bool _installingRemote;
        private long _currentTimestamp;
        private string _currentOwner;
        private SharedClipboardKind _currentKind;
        private bool _currentIsLocal;
        private long _clockOffsetTicks;
        private int _nextRequestId;
        private readonly Dictionary<long, LocalFileSession> _localFileSessions = new Dictionary<long, LocalFileSession>();
        private RemoteVirtualFileDataObject _remoteFileDataObject;
        private string _lastExplorerDestinationPath;
        private DateTime _lastExplorerDestinationSeenUtc = DateTime.MinValue;
        private DateTime _lastExplorerProbeUtc = DateTime.MinValue;

        public event Action<string> Activity;

        private sealed class PendingRequest
        {
            public readonly ManualResetEvent Event = new ManualResetEvent(false);
            public byte[] Payload;
            public bool Success;
            public string Error;
        }

        // A file selection must outlive the clipboard item that created it. Explorer may keep
        // consuming FILECONTENTS streams after the user has copied something else. Keeping
        // selections keyed by their shared timestamp also allows several remote paste operations
        // to run at the same time without invalidating one another.
        private sealed class LocalFileSession
        {
            public readonly object Sync = new object();
            public readonly List<string> Roots;
            public FileManifest Manifest;
            public DateTime LastAccessUtc;

            public LocalFileSession(List<string> roots)
            {
                Roots = new List<string>(roots);
                LastAccessUtc = DateTime.UtcNow;
            }
        }

        public ClipboardBridge(BridgeConfig config, NetworkBridge network, Control uiControl)
        {
            _config = config;
            _network = network;
            _uiControl = uiControl;
            _currentOwner = String.Empty;

            _network.PacketReceived += OnPacketReceived;
            _network.ConnectionChanged += OnConnectionChanged;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _config.PollMilliseconds;
            _timer.Tick += delegate { PollClipboard(); };
        }

        public void Start()
        {
            _lastSequence = GetClipboardSequenceNumber();
            _timer.Start();
        }

        public bool HandleWindowMessage(ref Message m)
        {
            if (m.Msg == 0x0305) // WM_RENDERFORMAT
            {
                RenderRemoteFormat((uint)m.WParam.ToInt64());
                return true;
            }

            if (m.Msg == 0x0306) // WM_RENDERALLFORMATS
                return true;

            return false;
        }

        private void PollClipboard()
        {
            RefreshDestinationHint();

            uint sequence = GetClipboardSequenceNumber();
            if (sequence == _lastSequence) return;
            _lastSequence = sequence;

            if (_installingRemote)
                return;

            // The Win32 delayed text/image owner is this window. OLE virtual-file installs are
            // guarded by updating _lastSequence immediately after OleSetClipboard.
            if (GetClipboardOwner() == _uiControl.Handle)
                return;

            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return;

                SharedClipboardKind kind = SharedClipboardKind.None;
                List<string> fileRoots = null;

                if (_config.SyncFiles && data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                    if (paths != null && paths.Length > 0)
                    {
                        fileRoots = new List<string>(paths);
                        kind = SharedClipboardKind.Files;
                    }
                }

                if (kind == SharedClipboardKind.None && _config.SyncText && data.GetDataPresent(DataFormats.UnicodeText))
                    kind = SharedClipboardKind.Text;
                else if (kind == SharedClipboardKind.None && _config.SyncImages && data.GetDataPresent(DataFormats.Bitmap))
                    kind = SharedClipboardKind.Image;

                if (kind == SharedClipboardKind.None)
                    return;

                long timestamp = NextSharedTimestamp();
                lock (_stateLock)
                {
                    _currentTimestamp = timestamp;
                    _currentOwner = _config.LocalMachineId;
                    _currentKind = kind;
                    _currentIsLocal = true;
                    _ownersByTimestamp[timestamp] = _config.LocalMachineId;
                    if (kind == SharedClipboardKind.Files && fileRoots != null)
                    {
                        // Do not discard older file selections here. An Explorer paste that already
                        // started may still be reading one while the user performs another copy/paste.
                        _localFileSessions[timestamp] = new LocalFileSession(fileRoots);
                        CleanupOldFileSessions_NoLock(timestamp);
                    }
                    _remoteFileDataObject = null;
                }

                SendAnnouncement(timestamp, _config.LocalMachineId, kind);
                RaiseActivity("Local " + KindName(kind) + " copied; announced only (no payload sent)");
            }
            catch (ExternalException)
            {
                // Clipboard temporarily busy. A subsequent clipboard change will be detected normally.
            }
            catch (Exception ex)
            {
                RaiseActivity("Clipboard error: " + ex.Message);
            }
        }

        private long NextSharedTimestamp()
        {
            long now = DateTime.UtcNow.Ticks + Interlocked.Read(ref _clockOffsetTicks);
            lock (_stateLock)
            {
                if (now <= _currentTimestamp)
                    now = _currentTimestamp + 1;
            }
            return now;
        }

        private void OnConnectionChanged(bool connected, string message)
        {
            if (!connected) return;

            SyncClockToElectedReference();

            long timestamp;
            string owner;
            SharedClipboardKind kind;
            lock (_stateLock)
            {
                timestamp = _currentTimestamp;
                owner = _currentOwner;
                kind = _currentKind;
            }

            if (timestamp > 0 && kind != SharedClipboardKind.None)
                SendAnnouncement(timestamp, owner, kind);
        }

        private void OnPacketReceived(string sourceMachineId, ClipboardPacket packet)
        {
            try
            {
                if (packet.Type == ClipboardPacketType.DataResponse) { HandleDataResponse(packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.FileManifestResponse) { HandleFileManifestResponse(packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.FileReadResponse) { HandleFileReadResponse(packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.TimeSyncResponse) { HandleTimeSyncResponse(packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.TimeSyncRequest) { HandleTimeSyncRequest(sourceMachineId, packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.FileManifestRequest) { HandleFileManifestRequest(sourceMachineId, packet.Payload); return; }
                if (packet.Type == ClipboardPacketType.FileReadRequest) { HandleFileReadRequest(sourceMachineId, packet.Payload); return; }

                _uiControl.BeginInvoke((MethodInvoker)delegate
                {
                    if (packet.Type == ClipboardPacketType.Announcement)
                        HandleAnnouncement(packet.Payload);
                    else if (packet.Type == ClipboardPacketType.DataRequest)
                        HandleDataRequest(sourceMachineId, packet.Payload);
                });
            }
            catch (Exception ex)
            {
                RaiseActivity("Packet handling error: " + ex.Message);
            }
        }

        private void SendAnnouncement(long timestamp, string owner, SharedClipboardKind kind)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(timestamp);
                w.Write(owner == null ? String.Empty : owner);
                w.Write((byte)kind);
                w.Flush();
                _network.Send(ClipboardPacketType.Announcement, ms.ToArray());
            }
        }

        private void HandleAnnouncement(byte[] payload)
        {
            long timestamp;
            string owner;
            SharedClipboardKind kind;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                timestamp = r.ReadInt64();
                owner = r.ReadString();
                kind = (SharedClipboardKind)r.ReadByte();
            }

            bool shouldApply;
            lock (_stateLock)
            {
                int compare = CompareVersion(timestamp, owner, _currentTimestamp, _currentOwner);
                shouldApply = compare > 0;
                if (shouldApply)
                {
                    _currentTimestamp = timestamp;
                    _currentOwner = owner;
                    _currentKind = kind;
                    _currentIsLocal = String.Equals(owner, _config.LocalMachineId, StringComparison.Ordinal);
                    _ownersByTimestamp[timestamp] = owner;
                }
            }

            if (!shouldApply || String.Equals(owner, _config.LocalMachineId, StringComparison.Ordinal))
                return;

            InstallRemoteClipboard(timestamp, kind);
            RaiseActivity("Remote " + KindName(kind) + " is newest; waiting for an application to paste/read it");
        }

        private static int CompareVersion(long ts1, string owner1, long ts2, string owner2)
        {
            if (ts1 < ts2) return -1;
            if (ts1 > ts2) return 1;
            return String.CompareOrdinal(owner1 == null ? String.Empty : owner1,
                                         owner2 == null ? String.Empty : owner2);
        }

        private void InstallRemoteClipboard(long timestamp, SharedClipboardKind kind)
        {
            if (kind == SharedClipboardKind.Files)
            {
                InstallRemoteVirtualFiles(timestamp);
                return;
            }

            IntPtr hwnd = _uiControl.Handle;
            _installingRemote = true;
            try
            {
                if (!OpenClipboard(hwnd))
                {
                    RaiseActivity("Remote clipboard pending, but clipboard is busy");
                    return;
                }

                try
                {
                    EmptyClipboard();
                    if (kind == SharedClipboardKind.Text)
                    {
                        SetClipboardData(CF_UNICODETEXT, IntPtr.Zero);
                        SetClipboardData(CF_TEXT, IntPtr.Zero);
                    }
                    else if (kind == SharedClipboardKind.Image)
                    {
                        SetClipboardData(CF_DIB, IntPtr.Zero);
                    }
                    _remoteFileDataObject = null;
                }
                finally
                {
                    CloseClipboard();
                }

                _lastSequence = GetClipboardSequenceNumber();
            }
            finally
            {
                _installingRemote = false;
            }
        }

        private void InstallRemoteVirtualFiles(long timestamp)
        {
            _installingRemote = true;
            try
            {
                RemoteVirtualFileDataObject obj = new RemoteVirtualFileDataObject(
                    timestamp,
                    RequestRemoteManifest,
                    RequestRemoteFileChunk,
                    CheckDestinationSpace,
                    RaiseActivity,
                    _config.FileReadChunkKilobytes * 1024);

                int hr = OleSetClipboard(obj);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                _remoteFileDataObject = obj; // Keep the COM callable wrapper alive while it owns the clipboard.
                _lastSequence = GetClipboardSequenceNumber();
            }
            catch (Exception ex)
            {
                RaiseActivity("Could not install remote virtual files: " + ex.Message);
            }
            finally
            {
                _installingRemote = false;
            }
        }

        private void RenderRemoteFormat(uint format)
        {
            long timestamp;
            SharedClipboardKind kind;
            bool isLocal;
            lock (_stateLock)
            {
                timestamp = _currentTimestamp;
                kind = _currentKind;
                isLocal = _currentIsLocal;
            }

            if (isLocal || timestamp <= 0 || kind == SharedClipboardKind.Files)
                return;

            byte[] payload = RequestRemoteData(timestamp, kind);
            if (payload == null)
            {
                RaiseActivity("Remote clipboard data unavailable (peer disconnected or request timed out)");
                return;
            }

            try
            {
                if (kind == SharedClipboardKind.Text)
                {
                    string text = Encoding.UTF8.GetString(payload);
                    if (format == CF_UNICODETEXT)
                        SetClipboardBytes(format, AddTerminator(Encoding.Unicode.GetBytes(text), 2));
                    else if (format == CF_TEXT)
                        SetClipboardBytes(format, AddTerminator(Encoding.Default.GetBytes(text), 1));
                }
                else if (kind == SharedClipboardKind.Image && format == CF_DIB)
                {
                    byte[] dib = PngToDib(payload);
                    SetClipboardBytes(format, dib);
                }
                RaiseActivity("Remote " + KindName(kind) + " payload transferred on demand");
            }
            catch (Exception ex)
            {
                RaiseActivity("Delayed clipboard render failed: " + ex.Message);
            }
        }

        private byte[] RequestRemoteData(long timestamp, SharedClipboardKind kind)
        {
            PendingRequest pending;
            int requestId = NewPendingRequest(out pending);
            try
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(requestId);
                    w.Write(timestamp);
                    w.Write((byte)kind);
                    w.Flush();
                    string owner = GetOwnerForTimestamp(timestamp);
                    if (String.IsNullOrEmpty(owner) || !_network.SendTo(owner, ClipboardPacketType.DataRequest, ms.ToArray())) return null;
                }
                if (!pending.Event.WaitOne(_config.RemoteRequestTimeoutMilliseconds)) return null;
                return pending.Success ? pending.Payload : null;
            }
            finally { RemovePendingRequest(requestId, pending); }
        }

        private void HandleDataRequest(string sourceMachineId, byte[] payload)
        {
            int requestId;
            long requestedTimestamp;
            SharedClipboardKind requestedKind;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                requestedTimestamp = r.ReadInt64();
                requestedKind = (SharedClipboardKind)r.ReadByte();
            }

            bool valid;
            lock (_stateLock)
                valid = _currentIsLocal && _currentTimestamp == requestedTimestamp && _currentKind == requestedKind;

            byte[] data = null;
            if (valid)
            {
                try
                {
                    if (requestedKind == SharedClipboardKind.Text)
                    {
                        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                        data = Encoding.UTF8.GetBytes(text == null ? String.Empty : text);
                    }
                    else if (requestedKind == SharedClipboardKind.Image)
                    {
                        using (Image image = Clipboard.GetImage())
                        {
                            if (image != null)
                            {
                                using (MemoryStream imageStream = new MemoryStream())
                                {
                                    image.Save(imageStream, ImageFormat.Png);
                                    if (imageStream.Length <= (long)_config.MaxImageMegabytes * 1024L * 1024L)
                                        data = imageStream.ToArray();
                                }
                            }
                        }
                    }
                }
                catch (ExternalException) { }
                catch (Exception ex) { RaiseActivity("Could not read local clipboard for remote paste: " + ex.Message); }
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(requestId);
                w.Write(requestedTimestamp);
                w.Write(data != null);
                if (data != null) { w.Write(data.Length); w.Write(data); }
                w.Flush();
                _network.SendTo(sourceMachineId, ClipboardPacketType.DataResponse, ms.ToArray());
            }
        }

        private void HandleDataResponse(byte[] payload)
        {
            int requestId;
            bool success;
            byte[] data = null;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                r.ReadInt64();
                success = r.ReadBoolean();
                if (success)
                {
                    int length = r.ReadInt32();
                    if (length < 0 || length > 256 * 1024 * 1024) success = false;
                    else data = r.ReadBytes(length);
                }
            }
            CompletePending(requestId, success, data, null);
        }

        private FileManifest RequestRemoteManifest(long timestamp)
        {
            PendingRequest pending;
            int requestId = NewPendingRequest(out pending);
            try
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(requestId);
                    w.Write(timestamp);
                    w.Flush();
                    string owner = GetOwnerForTimestamp(timestamp);
                    if (String.IsNullOrEmpty(owner) || !_network.SendTo(owner, ClipboardPacketType.FileManifestRequest, ms.ToArray())) return null;
                }

                if (!pending.Event.WaitOne(_config.RemoteRequestTimeoutMilliseconds))
                {
                    RaiseActivity("Remote file list request timed out");
                    return null;
                }
                if (!pending.Success)
                {
                    if (!String.IsNullOrEmpty(pending.Error)) RaiseActivity("Remote file list unavailable: " + pending.Error);
                    return null;
                }
                FileManifest manifest = FileManifest.Deserialize(pending.Payload, _config.MaxManifestEntries);
                TransferLog.Write("Remote manifest received: " + manifest.Entries.Count + " entries, " + manifest.TotalBytes + " bytes");
                return manifest;
            }
            finally { RemovePendingRequest(requestId, pending); }
        }

        private void HandleFileManifestRequest(string sourceMachineId, byte[] payload)
        {
            int requestId;
            long timestamp;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms))
            {
                requestId = r.ReadInt32();
                timestamp = r.ReadInt64();
            }

            string error;
            FileManifest manifest = GetLocalManifest(timestamp, out error);
            byte[] data = manifest == null ? null : manifest.Serialize();

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(requestId);
                w.Write(timestamp);
                w.Write(data != null);
                w.Write(error == null ? String.Empty : error);
                if (data != null) { w.Write(data.Length); w.Write(data); }
                w.Flush();
                _network.SendTo(sourceMachineId, ClipboardPacketType.FileManifestResponse, ms.ToArray());
            }
        }

        private void HandleFileManifestResponse(byte[] payload)
        {
            int requestId;
            bool success;
            string error;
            byte[] data = null;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                r.ReadInt64();
                success = r.ReadBoolean();
                error = r.ReadString();
                if (success)
                {
                    int len = r.ReadInt32();
                    if (len < 0 || len > 256 * 1024 * 1024) success = false;
                    else data = r.ReadBytes(len);
                }
            }
            CompletePending(requestId, success, data, error);
        }

        private byte[] RequestRemoteFileChunk(long timestamp, int index, long offset, int count)
        {
            PendingRequest pending;
            int requestId = NewPendingRequest(out pending);
            try
            {
                int max = _config.FileReadChunkKilobytes * 1024;
                count = Math.Max(1, Math.Min(count, max));
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(requestId);
                    w.Write(timestamp);
                    w.Write(index);
                    w.Write(offset);
                    w.Write(count);
                    w.Flush();
                    string owner = GetOwnerForTimestamp(timestamp);
                    if (String.IsNullOrEmpty(owner) || !_network.SendTo(owner, ClipboardPacketType.FileReadRequest, ms.ToArray()))
                        throw new IOException("Peer is disconnected.");
                }

                if (!pending.Event.WaitOne(_config.RemoteRequestTimeoutMilliseconds))
                {
                    TransferLog.Write("Remote file read timed out: index=" + index + " offset=" + offset + " count=" + count);
                    throw new IOException("Remote file read timed out.");
                }
                if (!pending.Success)
                {
                    TransferLog.Write("Remote file read failed: index=" + index + " offset=" + offset + " count=" + count + " error=" + pending.Error);
                    throw new IOException(String.IsNullOrEmpty(pending.Error) ? "Remote file read failed." : pending.Error);
                }
                return pending.Payload == null ? new byte[0] : pending.Payload;
            }
            finally { RemovePendingRequest(requestId, pending); }
        }

        private void HandleFileReadRequest(string sourceMachineId, byte[] payload)
        {
            int requestId;
            long timestamp;
            int index;
            long offset;
            int count;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms))
            {
                requestId = r.ReadInt32();
                timestamp = r.ReadInt64();
                index = r.ReadInt32();
                offset = r.ReadInt64();
                count = r.ReadInt32();
            }

            byte[] data = null;
            string error = null;
            try
            {
                string manifestError;
                FileManifest manifest = GetLocalManifest(timestamp, out manifestError);
                if (manifest == null) throw new IOException(manifestError == null ? "File clipboard is no longer current." : manifestError);
                if (index < 0 || index >= manifest.Entries.Count) throw new IOException("Invalid remote file index.");
                FileManifestEntry entry = manifest.Entries[index];
                if (entry.IsDirectory) { data = new byte[0]; }
                else
                {
                    if (offset < 0 || offset > entry.Length) throw new IOException("Invalid remote file offset.");
                    int max = _config.FileReadChunkKilobytes * 1024;
                    count = Math.Max(0, Math.Min(count, max));
                    long remaining = entry.Length - offset;
                    if (remaining < count) count = (int)remaining;
                    data = new byte[count];
                    int total = 0;
                    using (FileStream fs = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess))
                    {
                        fs.Position = offset;
                        while (total < count)
                        {
                            int n = fs.Read(data, total, count - total);
                            if (n <= 0) break;
                            total += n;
                        }
                    }
                    if (total != data.Length) Array.Resize(ref data, total);
                }
            }
            catch (Exception ex)
            {
                data = null;
                error = ex.Message;
                TransferLog.Write("Source read failed: index=" + index + " offset=" + offset + " count=" + count + ": " + ex.Message);
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(requestId);
                w.Write(timestamp);
                w.Write(data != null);
                w.Write(error == null ? String.Empty : error);
                if (data != null) { w.Write(data.Length); w.Write(data); }
                w.Flush();
                _network.SendTo(sourceMachineId, ClipboardPacketType.FileReadResponse, ms.ToArray());
            }
        }

        private void HandleFileReadResponse(byte[] payload)
        {
            int requestId;
            bool success;
            string error;
            byte[] data = null;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                r.ReadInt64();
                success = r.ReadBoolean();
                error = r.ReadString();
                if (success)
                {
                    int len = r.ReadInt32();
                    int max = (_config.FileReadChunkKilobytes * 1024) + 1024;
                    if (len < 0 || len > max) success = false;
                    else data = r.ReadBytes(len);
                }
            }
            CompletePending(requestId, success, data, error);
        }

        private FileManifest GetLocalManifest(long timestamp, out string error)
        {
            error = null;
            LocalFileSession session;
            lock (_stateLock)
            {
                if (!_localFileSessions.TryGetValue(timestamp, out session))
                {
                    error = "The requested file-copy session is no longer available.";
                    return null;
                }
                session.LastAccessUtc = DateTime.UtcNow;
            }

            lock (session.Sync)
            {
                session.LastAccessUtc = DateTime.UtcNow;
                if (session.Manifest != null) return session.Manifest;

                try
                {
                    FileManifest built = FileManifest.Build(session.Roots, _config.MaxManifestEntries);
                    TransferLog.Write("Local manifest built for session " + timestamp + ": " + built.Entries.Count + " entries, " + built.TotalBytes + " bytes");
                    session.Manifest = built;
                    session.LastAccessUtc = DateTime.UtcNow;
                    return built;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return null;
                }
            }
        }

        private void TouchLocalFileSession(long timestamp)
        {
            lock (_stateLock)
            {
                LocalFileSession session;
                if (_localFileSessions.TryGetValue(timestamp, out session))
                    session.LastAccessUtc = DateTime.UtcNow;
            }
        }

        private void CleanupOldFileSessions_NoLock(long keepTimestamp)
        {
            // There is no reliable OLE notification saying that Explorer has finished every stream.
            // Retain old selections for a generous period so long copies and overlapping pastes keep
            // working, while preventing an application left running for long time from growing forever.
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-_config.FileSessionRetentionMinutes);
            List<long> remove = null;
            foreach (KeyValuePair<long, LocalFileSession> pair in _localFileSessions)
            {
                if (pair.Key == keepTimestamp) continue;
                if (pair.Value.LastAccessUtc >= cutoff) continue;
                if (remove == null) remove = new List<long>();
                remove.Add(pair.Key);
            }
            if (remove != null)
            {
                foreach (long key in remove)
                {
                    _localFileSessions.Remove(key);
                    TransferLog.Write("Expired inactive local file-copy session " + key);
                }
            }
        }

        private bool CheckDestinationSpace(long timestamp, long requiredBytes)
        {
            // The IDataObject may belong to an older clipboard generation that Explorer is still
            // consuming. A newer copy must not cancel the free-space check or the transfer.
            string path;
            DateTime seen;
            lock (_stateLock)
            {
                path = _lastExplorerDestinationPath;
                seen = _lastExplorerDestinationSeenUtc;
            }

            long available;
            if (String.IsNullOrEmpty(path) || (DateTime.UtcNow - seen).TotalSeconds > 5.0 ||
                !DestinationSpaceChecker.TryGetFreeSpaceForPath(path, out available))
            {
                RaiseActivity("Destination path is not exposed reliably by Windows; free-space precheck skipped");
                return true;
            }

            if (available >= requiredBytes)
            {
                RaiseActivity("Destination free-space check OK: " + FormatBytes(available) + " available at " + path);
                return true;
            }

            ShowInsufficientSpace(path, requiredBytes, available);
            RaiseActivity("Transfer cancelled: destination does not have enough free space");
            return false;
        }


        private void RefreshDestinationHint()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastExplorerProbeUtc).TotalMilliseconds < 750.0) return;
            _lastExplorerProbeUtc = now;

            string path;
            long ignored;
            if (DestinationSpaceChecker.TryGetActiveLocalExplorerDestination(out path, out ignored))
            {
                lock (_stateLock)
                {
                    _lastExplorerDestinationPath = path;
                    _lastExplorerDestinationSeenUtc = now;
                }
            }
        }

        private void ShowInsufficientSpace(string path, long required, long available)
        {
            MethodInvoker show = delegate
            {
                MessageBox.Show(_uiControl,
                    "Not enough free space on the destination drive.\r\n\r\n" +
                    "Destination: " + path + "\r\n" +
                    "Required:     " + FormatBytes(required) + "\r\n" +
                    "Available:    " + FormatBytes(available) + "\r\n\r\n" +
                    "No file data was transferred by ClipBridge.",
                    "ClipBridge - Not enough disk space",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };
            try
            {
                if (_uiControl.InvokeRequired) _uiControl.Invoke(show);
                else show();
            }
            catch { }
        }

        private int NewPendingRequest(out PendingRequest pending)
        {
            int id = Interlocked.Increment(ref _nextRequestId);
            pending = new PendingRequest();
            lock (_requestsLock) _pendingRequests[id] = pending;
            return id;
        }

        private void RemovePendingRequest(int id, PendingRequest pending)
        {
            lock (_requestsLock) _pendingRequests.Remove(id);
            pending.Event.Close();
        }

        private void CompletePending(int requestId, bool success, byte[] payload, string error)
        {
            PendingRequest pending = null;
            lock (_requestsLock)
            {
                if (_pendingRequests.ContainsKey(requestId)) pending = _pendingRequests[requestId];
            }
            if (pending == null) return;
            pending.Payload = payload;
            pending.Success = success;
            pending.Error = error;
            pending.Event.Set();
        }

        private string GetOwnerForTimestamp(long timestamp)
        {
            lock (_stateLock)
            {
                string owner;
                if (_ownersByTimestamp.TryGetValue(timestamp, out owner)) return owner;
                if (_currentTimestamp == timestamp) return _currentOwner;
                return null;
            }
        }

        private void SyncClockToElectedReference()
        {
            string leader = _config.LocalMachineId;
            string[] peers = _network.GetPeerIds();
            for (int i = 0; i < peers.Length; i++)
                if (String.CompareOrdinal(peers[i], leader) < 0) leader = peers[i];

            if (leader == _config.LocalMachineId)
            {
                Interlocked.Exchange(ref _clockOffsetTicks, 0);
                return;
            }

            long sent = DateTime.UtcNow.Ticks;
            _network.SendTo(leader, ClipboardPacketType.TimeSyncRequest, BitConverter.GetBytes(sent));
        }

        private void HandleTimeSyncRequest(string sourceMachineId, byte[] payload)
        {
            if (payload == null || payload.Length < 8) return;
            long clientSent = BitConverter.ToInt64(payload, 0);
            long serverNow = DateTime.UtcNow.Ticks;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(clientSent);
                w.Write(serverNow);
                w.Flush();
                _network.SendTo(sourceMachineId, ClipboardPacketType.TimeSyncResponse, ms.ToArray());
            }
        }

        private void HandleTimeSyncResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 16) return;
            long received = DateTime.UtcNow.Ticks;
            long sent;
            long referenceTime;
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader r = new BinaryReader(ms))
            {
                sent = r.ReadInt64();
                referenceTime = r.ReadInt64();
            }
            long midpoint = sent + ((received - sent) / 2L);
            Interlocked.Exchange(ref _clockOffsetTicks, referenceTime - midpoint);
        }

        private static byte[] AddTerminator(byte[] bytes, int count)
        {
            byte[] result = new byte[bytes.Length + count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static byte[] PngToDib(byte[] png)
        {
            using (MemoryStream input = new MemoryStream(png))
            using (Image temp = Image.FromStream(input))
            using (Bitmap bitmap = new Bitmap(temp))
            using (MemoryStream bmpStream = new MemoryStream())
            {
                bitmap.Save(bmpStream, ImageFormat.Bmp);
                byte[] bmp = bmpStream.ToArray();
                if (bmp.Length <= 14) throw new InvalidDataException("Invalid BMP generated from PNG.");
                byte[] dib = new byte[bmp.Length - 14];
                Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
                return dib;
            }
        }

        private static void SetClipboardBytes(uint format, byte[] bytes)
        {
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)bytes.Length));
            if (hMem == IntPtr.Zero) throw new OutOfMemoryException();

            bool handedToClipboard = false;
            try
            {
                IntPtr ptr = GlobalLock(hMem);
                if (ptr == IntPtr.Zero) throw new ExternalException("GlobalLock failed.");
                try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                finally { GlobalUnlock(hMem); }

                IntPtr result = SetClipboardData(format, hMem);
                if (result == IntPtr.Zero) throw new ExternalException("SetClipboardData failed.");
                handedToClipboard = true;
            }
            finally
            {
                if (!handedToClipboard) GlobalFree(hMem);
            }
        }

        private static string KindName(SharedClipboardKind kind)
        {
            if (kind == SharedClipboardKind.Text) return "text";
            if (kind == SharedClipboardKind.Image) return "image";
            if (kind == SharedClipboardKind.Files) return "files/folders";
            return "clipboard item";
        }

        private static string FormatBytes(long value)
        {
            double n = value;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (n >= 1024.0 && i < units.Length - 1) { n /= 1024.0; i++; }
            return n.ToString(i == 0 ? "0" : "0.##") + " " + units[i];
        }

        private void RaiseActivity(string text)
        {
            TransferLog.Write(text);
            Action<string> handler = Activity;
            if (handler != null) handler(text);
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _network.PacketReceived -= OnPacketReceived;
            _network.ConnectionChanged -= OnConnectionChanged;

            lock (_requestsLock)
            {
                foreach (PendingRequest pending in _pendingRequests.Values)
                    pending.Event.Set();
            }
        }
    }
}
