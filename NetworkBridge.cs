using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace ClipBridge
{
    internal sealed class NetworkBridge : IDisposable
    {
        private const uint DiscoveryMagic = 0x43424434; // CBD4
        private const int DiscoveryVersion = 5;
        private readonly BridgeConfig _config;
        private readonly object _peersLock = new object();
        private readonly Dictionary<string, PeerConnection> _peers = new Dictionary<string, PeerConnection>(StringComparer.Ordinal);
        private readonly HashSet<string> _connecting = new HashSet<string>(StringComparer.Ordinal);
        private volatile bool _running;
        private TcpListener _listener;
        private UdpClient _udpRx;
        private Thread _acceptThread;
        private Thread _discoveryRxThread;
        private Thread _discoveryTxThread;

        public event Action<bool, string> ConnectionChanged;
        public event Action<string, ClipboardPacket> PacketReceived;
        public event Action<int, string> PeersChanged;


        internal sealed class PeerInfo
        {
            public string MachineId;
            public string ComputerName;
            public string Address;
            public string Groups;
        }

        private sealed class PeerConnection
        {
            public string MachineId;
            public string ComputerName;
            public string Address;
            public HashSet<string> SharedGroups;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
            public Thread ReaderThread;
        }

        private sealed class Handshake
        {
            public string MachineId;
            public string ComputerName;
            public int Port;
            public Dictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public NetworkBridge(BridgeConfig config) { _config = config; }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start();
            _acceptThread = StartThread(AcceptLoop, "ClipBridge TCP accept");
            if (_config.AutoDiscovery)
            {
                _discoveryRxThread = StartThread(DiscoveryReceiveLoop, "ClipBridge discovery rx");
                _discoveryTxThread = StartThread(DiscoverySendLoop, "ClipBridge discovery tx");
            }
            RaisePeersChanged();
        }

        private static Thread StartThread(ThreadStart start, string name)
        {
            Thread t = new Thread(start);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
            return t;
        }

        public bool Broadcast(ClipboardPacketType type, byte[] payload)
        {
            List<PeerConnection> peers = SnapshotPeers();
            bool any = false;
            foreach (PeerConnection p in peers)
                if (SendToPeer(p, type, payload)) any = true;
            return any;
        }

        // Backward-compatible semantic used by clipboard announcements.
        public bool Send(ClipboardPacketType type, byte[] payload) { return Broadcast(type, payload); }

        public bool SendTo(string machineId, ClipboardPacketType type, byte[] payload)
        {
            if (String.IsNullOrEmpty(machineId)) return false;
            PeerConnection p = null;
            lock (_peersLock) { if (_peers.ContainsKey(machineId)) p = _peers[machineId]; }
            return p != null && SendToPeer(p, type, payload);
        }

        public string[] GetPeerIds()
        {
            lock (_peersLock)
            {
                string[] ids = new string[_peers.Count];
                _peers.Keys.CopyTo(ids, 0);
                return ids;
            }
        }

        public int PeerCount { get { lock (_peersLock) return _peers.Count; } }
        public bool IsRunning { get { return _running; } }
        public bool IsDiscoveryListening { get { return _running && _config.AutoDiscovery && _udpRx != null; } }
        public bool IsTcpListening { get { return _running && _listener != null; } }

        public PeerInfo[] GetPeerInfos()
        {
            List<PeerInfo> list = new List<PeerInfo>();
            lock (_peersLock)
            {
                foreach (PeerConnection p in _peers.Values)
                {
                    PeerInfo i = new PeerInfo();
                    i.MachineId = p.MachineId;
                    i.ComputerName = p.ComputerName;
                    i.Address = p.Address;
                    StringBuilder groups = new StringBuilder();
                    foreach (ClipboardGroup g in _config.Groups)
                    {
                        if (!p.SharedGroups.Contains(g.Id)) continue;
                        if (groups.Length != 0) groups.Append(", ");
                        groups.Append(g.Name);
                    }
                    i.Groups = groups.ToString();
                    list.Add(i);
                }
            }
            list.Sort(delegate(PeerInfo a, PeerInfo b) { return String.Compare(a.ComputerName, b.ComputerName, StringComparison.OrdinalIgnoreCase); });
            return list.ToArray();
        }

        private bool SendToPeer(PeerConnection p, ClipboardPacketType type, byte[] payload)
        {
            try
            {
                lock (p.SendLock) FrameProtocol.Write(p.Stream, type, payload);
                return true;
            }
            catch { DropPeer(p); return false; }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { AcceptIncoming(c); });
                }
                catch { if (_running) Thread.Sleep(250); }
            }
        }

        private void AcceptIncoming(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream s = client.GetStream();
                Handshake remote = ReadHandshake(s);
                WriteHandshake(s);
                RegisterPeer(client, s, remote);
            }
            catch { try { client.Close(); } catch { } }
        }

        private void ConnectTo(string ip, int port, string expectedMachineId)
        {
            lock (_peersLock)
            {
                if (_peers.ContainsKey(expectedMachineId) || _connecting.Contains(expectedMachineId)) return;
                _connecting.Add(expectedMachineId);
            }
            try
            {
                TcpClient c = new TcpClient();
                c.NoDelay = true;
                c.Connect(ip, port);
                NetworkStream s = c.GetStream();
                WriteHandshake(s);
                Handshake remote = ReadHandshake(s);
                if (!String.Equals(remote.MachineId, expectedMachineId, StringComparison.Ordinal)) throw new IOException("Peer identity changed.");
                RegisterPeer(c, s, remote);
            }
            catch { }
            finally { lock (_peersLock) _connecting.Remove(expectedMachineId); }
        }

        private void RegisterPeer(TcpClient client, NetworkStream stream, Handshake remote)
        {
            if (remote == null || String.IsNullOrEmpty(remote.MachineId) || remote.MachineId == _config.LocalMachineId)
            { client.Close(); return; }

            HashSet<string> shared = SharedAuthenticatedGroups(remote);
            if (shared.Count == 0) { client.Close(); return; }

            PeerConnection p = new PeerConnection();
            p.MachineId = remote.MachineId;
            p.ComputerName = remote.ComputerName;
            p.Address = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            p.SharedGroups = shared;
            p.Client = client;
            p.Stream = stream;

            PeerConnection old = null;
            lock (_peersLock)
            {
                if (_peers.ContainsKey(p.MachineId)) old = _peers[p.MachineId];
                _peers[p.MachineId] = p;
            }
            if (old != null && old != p) ClosePeer(old);

            p.ReaderThread = new Thread(new ThreadStart(delegate { PeerReadLoop(p); }));
            p.ReaderThread.IsBackground = true;
            p.ReaderThread.Name = "ClipBridge peer " + p.ComputerName;
            p.ReaderThread.Start();
            RaisePeersChanged();
        }

        private HashSet<string> SharedAuthenticatedGroups(Handshake remote)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (ClipboardGroup local in _config.Groups)
            {
                string auth;
                if (remote.Groups.TryGetValue(local.Id, out auth) && String.Equals(auth, local.Auth, StringComparison.OrdinalIgnoreCase))
                    result.Add(local.Id);
            }
            return result;
        }

        private void PeerReadLoop(PeerConnection p)
        {
            try
            {
                while (_running)
                {
                    ClipboardPacket packet = FrameProtocol.Read(p.Stream);
                    Action<string, ClipboardPacket> h = PacketReceived;
                    if (h != null) h(p.MachineId, packet);
                }
            }
            catch { }
            finally { DropPeer(p); }
        }

        private void DropPeer(PeerConnection p)
        {
            bool removed = false;
            lock (_peersLock)
            {
                PeerConnection current;
                if (_peers.TryGetValue(p.MachineId, out current) && Object.ReferenceEquals(current, p))
                { _peers.Remove(p.MachineId); removed = true; }
            }
            ClosePeer(p);
            if (removed) RaisePeersChanged();
        }

        private static void ClosePeer(PeerConnection p)
        {
            try { if (p.Stream != null) p.Stream.Close(); } catch { }
            try { if (p.Client != null) p.Client.Close(); } catch { }
        }

        private List<PeerConnection> SnapshotPeers()
        {
            lock (_peersLock) return new List<PeerConnection>(_peers.Values);
        }

        private void DiscoverySendLoop()
        {
            while (_running)
            {
                byte[] data = BuildDiscoveryPacket(false);
                bool sent = false;
                try
                {
                    List<LocalBroadcastTarget> targets = GetLocalBroadcastTargets();
                    foreach (LocalBroadcastTarget target in targets)
                    {
                        try
                        {
                            using (UdpClient u = new UdpClient(new IPEndPoint(target.LocalAddress, 0)))
                            {
                                u.EnableBroadcast = true;
                                IPEndPoint ep = new IPEndPoint(target.BroadcastAddress, _config.Port);
                                u.Send(data, data.Length, ep);
                                sent = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TransferLog.Write("Discovery send failed via " + target.LocalAddress + " to " + target.BroadcastAddress + ":" + _config.Port + " - " + ex.Message);
                        }
                    }

                    // Keep the limited broadcast as a fallback for unusual adapters/subnets.
                    try
                    {
                        using (UdpClient u = new UdpClient())
                        {
                            u.EnableBroadcast = true;
                            IPEndPoint ep = new IPEndPoint(IPAddress.Broadcast, _config.Port);
                            u.Send(data, data.Length, ep);
                            sent = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TransferLog.Write("Limited discovery broadcast failed: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    TransferLog.Write("Discovery enumeration failed: " + ex.Message);
                }

                if (!sent) TransferLog.Write("Discovery packet could not be sent on any IPv4 interface.");
                Sleep(_config.DiscoveryIntervalMilliseconds);
            }
        }

        private sealed class LocalBroadcastTarget
        {
            public IPAddress LocalAddress;
            public IPAddress BroadcastAddress;
        }

        private static List<LocalBroadcastTarget> GetLocalBroadcastTargets()
        {
            List<LocalBroadcastTarget> result = new List<LocalBroadcastTarget>();
            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties props;
                try { props = adapter.GetIPProperties(); } catch { continue; }
                foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                {
                    if (uni.Address == null || uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    IPAddress mask = uni.IPv4Mask;
                    if (mask == null) continue;
                    byte[] ip = uni.Address.GetAddressBytes();
                    byte[] m = mask.GetAddressBytes();
                    if (ip.Length != 4 || m.Length != 4) continue;
                    byte[] b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | (byte)~m[i]);
                    LocalBroadcastTarget t = new LocalBroadcastTarget();
                    t.LocalAddress = uni.Address;
                    t.BroadcastAddress = new IPAddress(b);
                    result.Add(t);
                }
            }
            return result;
        }

        private void DiscoveryReceiveLoop()
        {
            try
            {
                _udpRx = new UdpClient();
                _udpRx.ExclusiveAddressUse = false;
                _udpRx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpRx.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Port));
                TransferLog.Write("Discovery listener active on UDP " + _config.Port + ". Local IP targets: " + DescribeLocalBroadcastTargets());
                while (_running)
                {
                    IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpRx.Receive(ref remoteEp);
                    string machineId; int port; List<string> groups; bool isResponse;
                    if (!ParseDiscoveryPacket(data, out machineId, out port, out groups, out isResponse)) continue;
                    if (machineId == _config.LocalMachineId) continue;
                    if (!SharesDiscoveryGroup(groups)) continue;

                    string ip = remoteEp.Address.ToString();
                    TransferLog.Write("Discovery peer seen: " + machineId + " at " + ip + ":" + port);

                    // Reply directly to the peer's listening UDP port. This makes discovery work even
                    // when broadcasts are only received in one direction on older/multi-NIC systems.
                    if (!isResponse)
                    {
                        try
                        {
                            byte[] reply = BuildDiscoveryPacket(true);
                            using (UdpClient u = new UdpClient())
                            {
                                u.Send(reply, reply.Length, new IPEndPoint(remoteEp.Address, port));
                            }
                        }
                        catch { }
                    }

                    // Deterministic initiator avoids duplicate persistent TCP connections.
                    if (String.CompareOrdinal(_config.LocalMachineId, machineId) < 0)
                        ThreadPool.QueueUserWorkItem(delegate { ConnectTo(ip, port, machineId); });
                }
            }
            catch { }
        }

        private bool SharesDiscoveryGroup(List<string> ids)
        {
            foreach (ClipboardGroup g in _config.Groups)
                for (int i = 0; i < ids.Count; i++) if (ids[i] == g.Id) return true;
            return false;
        }

        private static string DescribeLocalBroadcastTargets()
        {
            try
            {
                List<LocalBroadcastTarget> targets = GetLocalBroadcastTargets();
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < targets.Count; i++)
                {
                    if (i > 0) b.Append(", ");
                    b.Append(targets[i].LocalAddress);
                    b.Append(" -> ");
                    b.Append(targets[i].BroadcastAddress);
                }
                return b.Length == 0 ? "none" : b.ToString();
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        private byte[] BuildDiscoveryPacket(bool isResponse)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(DiscoveryMagic); w.Write(DiscoveryVersion); w.Write(isResponse); w.Write(_config.LocalMachineId); w.Write(_config.ComputerName); w.Write(_config.Port);
                w.Write(_config.Groups.Count);
                foreach (ClipboardGroup g in _config.Groups) w.Write(g.Id);
                w.Flush(); return ms.ToArray();
            }
        }

        private static bool ParseDiscoveryPacket(byte[] data, out string machineId, out int port, out List<string> groups, out bool isResponse)
        {
            machineId = null; port = 0; groups = new List<string>(); isResponse = false;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (r.ReadUInt32() != DiscoveryMagic || r.ReadInt32() != DiscoveryVersion) return false;
                    isResponse = r.ReadBoolean();
                    machineId = r.ReadString(); r.ReadString(); port = r.ReadInt32();
                    int n = r.ReadInt32(); if (n < 0 || n > 64) return false;
                    for (int i = 0; i < n; i++) groups.Add(r.ReadString());
                    return port > 0 && port <= 65535;
                }
            }
            catch { return false; }
        }

        private void WriteHandshake(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(_config.LocalMachineId); w.Write(_config.ComputerName); w.Write(_config.Port); w.Write(_config.Groups.Count);
                foreach (ClipboardGroup g in _config.Groups) { w.Write(g.Id); w.Write(g.Auth); }
                w.Flush(); byte[] b = ms.ToArray();
                BinaryWriter output = new BinaryWriter(s, Encoding.UTF8); output.Write(b.Length); output.Write(b); output.Flush();
            }
        }

        private static Handshake ReadHandshake(Stream s)
        {
            BinaryReader input = new BinaryReader(s, Encoding.UTF8);
            int len = input.ReadInt32(); if (len < 1 || len > 1024 * 1024) throw new IOException("Invalid handshake.");
            byte[] b = input.ReadBytes(len); if (b.Length != len) throw new EndOfStreamException();
            Handshake h = new Handshake();
            using (MemoryStream ms = new MemoryStream(b))
            using (BinaryReader r = new BinaryReader(ms, Encoding.UTF8))
            {
                h.MachineId = r.ReadString(); h.ComputerName = r.ReadString(); h.Port = r.ReadInt32();
                int n = r.ReadInt32(); if (n < 0 || n > 64) throw new IOException("Invalid groups.");
                for (int i = 0; i < n; i++) h.Groups[r.ReadString()] = r.ReadString();
            }
            return h;
        }

        private void RaisePeersChanged()
        {
            int count = PeerCount;
            string message = count == 0 ? "No peers discovered" : (count == 1 ? "1 peer connected" : count + " peers connected");
            Action<bool, string> c = ConnectionChanged; if (c != null) c(count > 0, message);
            Action<int, string> p = PeersChanged; if (p != null) p(count, message);
        }

        private void Sleep(int ms) { int left = ms; while (_running && left > 0) { int n = Math.Min(100, left); Thread.Sleep(n); left -= n; } }

        public void Restart()
        {
            Stop();
            Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_udpRx != null) _udpRx.Close(); } catch { }
            List<PeerConnection> peers = SnapshotPeers();
            foreach (PeerConnection p in peers) ClosePeer(p);
            lock (_peersLock) { _peers.Clear(); _connecting.Clear(); }

            JoinThread(_acceptThread);
            JoinThread(_discoveryRxThread);
            JoinThread(_discoveryTxThread);
            _acceptThread = null; _discoveryRxThread = null; _discoveryTxThread = null;
            _listener = null; _udpRx = null;
            RaisePeersChanged();
        }

        private static void JoinThread(Thread t)
        {
            if (t == null || t == Thread.CurrentThread) return;
            try { t.Join(1200); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
