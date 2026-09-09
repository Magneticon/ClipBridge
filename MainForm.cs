using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClipBridge
{
    internal sealed class StatusLamp : Control
    {
        private Color _lampColor = Color.Red;
        public Color LampColor { get { return _lampColor; } set { _lampColor = value; Invalidate(); } }
        public StatusLamp() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Size = new Size(16, 16); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (SolidBrush b = new SolidBrush(_lampColor)) e.Graphics.FillEllipse(b, r);
            using (Pen p = new Pen(Color.FromArgb(70, 70, 70))) e.Graphics.DrawEllipse(p, r);
            Rectangle hi = new Rectangle(4, 3, Math.Max(2, Width / 4), Math.Max(2, Height / 4));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(180, Color.White))) e.Graphics.FillEllipse(b, hi);
        }
    }

    internal sealed class GettingStartedPanel : Panel
    {
        private readonly string[] _steps = new string[]
        {
            "Run ClipBridge on every computer that should share the clipboard.",
            "Make sure the computers are on the same LAN and can reach each other.",
            "Use the same GroupName:SharedKey on computers that should communicate.",
            "Allow TCP and UDP on the configured port (default 8080) in the local firewall.",
            "Wait for Peer connection to turn green, or click Refresh.",
            "Copy text, images, files or folders and paste on another peer."
        };

        private readonly Label[] _numbers;
        private readonly Label[] _texts;
        private readonly Panel _separator;
        private readonly Label _tipsTitle;
        private readonly Label _tipsText;

        public GettingStartedPanel()
        {
            BackColor = SystemColors.Window;
            AutoScroll = true;
            DoubleBuffered = true;

            _numbers = new Label[_steps.Length];
            _texts = new Label[_steps.Length];

            Font numberFont = new Font(Font, FontStyle.Bold);
            for (int i = 0; i < _steps.Length; i++)
            {
                Label number = new Label();
                number.Text = (i + 1).ToString();
                number.TextAlign = ContentAlignment.MiddleCenter;
                number.BackColor = Color.FromArgb(0, 0, 160);
                number.ForeColor = Color.White;
                number.BorderStyle = BorderStyle.FixedSingle;
                number.Font = numberFont;
                number.Size = new Size(26, 26);
                _numbers[i] = number;
                Controls.Add(number);

                Label text = new Label();
                text.Text = _steps[i];
                text.AutoSize = false;
                text.UseMnemonic = false;
                text.ForeColor = SystemColors.WindowText;
                _texts[i] = text;
                Controls.Add(text);
            }

            _separator = new Panel();
            _separator.Height = 1;
            _separator.BackColor = SystemColors.ControlDark;
            Controls.Add(_separator);

            _tipsTitle = new Label();
            _tipsTitle.Text = "Tips";
            _tipsTitle.Font = new Font(Font, FontStyle.Bold);
            _tipsTitle.ForeColor = Color.FromArgb(0, 0, 160);
            _tipsTitle.AutoSize = false;
            Controls.Add(_tipsTitle);

            _tipsText = new Label();
            _tipsText.Text =
                "• A local copy is announced to every group this computer belongs to.\r\n" +
                "• Clipboard payloads are transferred only when a remote application actually requests them.\r\n" +
                "• File/folder transfers stream directly; ClipBridge does not create a full temporary copy.\r\n" +
                "• TCP and UDP use the same configured port number.";
            _tipsText.AutoSize = false;
            _tipsText.UseMnemonic = false;
            Controls.Add(_tipsText);

            SizeChanged += delegate { LayoutContent(); };
            FontChanged += delegate { LayoutContent(); };
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            BeginInvoke((MethodInvoker)delegate { LayoutContent(); });
        }

        private void LayoutContent()
        {
            if (IsDisposed || ClientSize.Width <= 0) return;

            int left = 12;
            int numberWidth = 26;
            int textLeft = left + numberWidth + 10;
            int rightMargin = 18;
            int textWidth = Math.Max(90, ClientSize.Width - textLeft - rightMargin);
            int y = 12;
            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

            for (int i = 0; i < _steps.Length; i++)
            {
                Size measured = TextRenderer.MeasureText(_steps[i], Font, new Size(textWidth, 1000), flags);
                int textHeight = Math.Max(30, measured.Height + 4);
                _numbers[i].Location = new Point(left, y);
                _texts[i].Location = new Point(textLeft, y - 1);
                _texts[i].Size = new Size(textWidth, textHeight);
                y += Math.Max(26, textHeight) + 10;
            }

            _separator.Location = new Point(left, y + 2);
            _separator.Width = Math.Max(40, ClientSize.Width - left - rightMargin);
            y += 12;

            _tipsTitle.Location = new Point(left, y);
            _tipsTitle.Size = new Size(Math.Max(90, ClientSize.Width - left - rightMargin), 22);
            y += 24;

            int tipsWidth = Math.Max(90, ClientSize.Width - left - rightMargin);
            Size tipsMeasured = TextRenderer.MeasureText(_tipsText.Text, Font, new Size(tipsWidth, 2000), flags);
            int tipsHeight = Math.Max(90, tipsMeasured.Height + 8);
            _tipsText.Location = new Point(left, y);
            _tipsText.Size = new Size(tipsWidth, tipsHeight);
            y += tipsHeight + 14;

            AutoScrollMinSize = new Size(0, y);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly BridgeConfig _config;
        private readonly bool _startHidden;
        private readonly EventWaitHandle _showEvent;
        private readonly NetworkBridge _network;
        private readonly ClipboardBridge _clipboard;
        private NumericUpDown _port;
        private CheckBox _autoDiscovery;
        private CheckBox _autostart;
        private CheckBox _showTray;
        private CheckBox _syncText;
        private CheckBox _syncImages;
        private CheckBox _syncFiles;
        private TextBox _groupsEdit;
        private Label _saveStatus;
        private Button _connectButton;
        private Label _activity;
        private Label _identity;
        private StatusLamp _discoveryLamp;
        private StatusLamp _tcpLamp;
        private StatusLamp _peerLamp;
        private Label _discoveryText;
        private Label _tcpText;
        private Label _peerText;
        private ListView _peers;
        private NotifyIcon _tray;
        private Thread _showThread;
        private volatile bool _closing;
        private bool _started;

        public MainForm(BridgeConfig config, bool forceVisible, bool forceSilent, EventWaitHandle showEvent)
        {
            _config = config;
            _showEvent = showEvent;
            _startHidden = forceSilent || (_config.Silent && !forceVisible);

            Text = "ClipBridge v1.0";
            ClientSize = new Size(1100, 635);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadApplicationIcon();

            BuildConnectionPanel();
            BuildPeersPanel();
            BuildActivityPanel();
            BuildSettingsPanel();
            BuildHelpPanel();

            _network = new NetworkBridge(_config);
            _network.ConnectionChanged += OnConnectionChanged;
            _network.PeersChanged += OnPeersChanged;

            _clipboard = new ClipboardBridge(_config, _network, this);
            _clipboard.Activity += OnActivity;

            Load += delegate { StartBridges(); StartShowWatcher(); };
            Shown += delegate { if (_startHidden) BeginInvoke((MethodInvoker)delegate { Hide(); }); };
            Resize += OnFormResize;
            FormClosed += delegate { DisposeBridges(); };

            UpdateTrayIcon();
            RefreshAllStatus();
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream stream = asm.GetManifestResourceStream("ClipBridge.ClipBridge.ico"))
                {
                    if (stream != null)
                    {
                        using (Icon embedded = new Icon(stream))
                            return (Icon)embedded.Clone();
                    }
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        private void BuildConnectionPanel()
        {
            GroupBox box = NewGroupBox("Connection status", 12, 10, 430, 185);
            Controls.Add(box);

            _discoveryLamp = NewLamp(18, 29); box.Controls.Add(_discoveryLamp);
            Label d = NewLabel(43, 26, 150, 20); d.Text = "Discovery (UDP)"; box.Controls.Add(d);
            _discoveryText = NewLabel(205, 26, 205, 20); box.Controls.Add(_discoveryText);

            _tcpLamp = NewLamp(18, 57); box.Controls.Add(_tcpLamp);
            Label t = NewLabel(43, 54, 150, 20); t.Text = "Peer listener (TCP)"; box.Controls.Add(t);
            _tcpText = NewLabel(205, 54, 205, 20); box.Controls.Add(_tcpText);

            _peerLamp = NewLamp(18, 85); box.Controls.Add(_peerLamp);
            Label p = NewLabel(43, 82, 150, 20); p.Text = "Peer connection"; box.Controls.Add(p);
            _peerText = NewLabel(205, 82, 205, 20); box.Controls.Add(_peerText);

            _identity = NewLabel(18, 116, 390, 52); box.Controls.Add(_identity);

            Button refresh = new Button();
            refresh.Text = "Refresh";
            refresh.TextAlign = ContentAlignment.MiddleCenter;
            refresh.Location = new Point(265, 138);
            refresh.Size = new Size(143, 30);
            refresh.Click += delegate { RefreshConnection(); };
            box.Controls.Add(refresh);
        }

        private void BuildPeersPanel()
        {
            GroupBox box = NewGroupBox("Peers", 12, 205, 430, 205);
            Controls.Add(box);
            _peers = new ListView();
            _peers.Location = new Point(12, 23);
            _peers.Size = new Size(406, 170);
            _peers.View = View.Details;
            _peers.FullRowSelect = true;
            _peers.GridLines = true;
            _peers.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _peers.Columns.Add("Status", 55);
            _peers.Columns.Add("Machine", 120);
            _peers.Columns.Add("IP address", 105);
            _peers.Columns.Add("Groups", 120);
            box.Controls.Add(_peers);
        }

        private void BuildActivityPanel()
        {
            GroupBox box = NewGroupBox("Clipboard activity", 12, 420, 430, 205);
            Controls.Add(box);
            _activity = NewLabel(14, 25, 400, 162);
            _activity.Text = "Waiting for clipboard activity.";
            _activity.BorderStyle = BorderStyle.Fixed3D;
            _activity.Padding = new Padding(5);
            box.Controls.Add(_activity);
        }

        private void BuildSettingsPanel()
        {
            GroupBox box = NewGroupBox("Settings (applied without restart)", 452, 10, 315, 615);
            Controls.Add(box);

            Label portLabel = NewLabel(14, 29, 105, 20); portLabel.Text = "TCP/UDP port:"; box.Controls.Add(portLabel);
            _port = new NumericUpDown(); _port.Location = new Point(125, 26); _port.Size = new Size(90, 22); _port.Minimum = 1; _port.Maximum = 65535; _port.Value = _config.Port; box.Controls.Add(_port);

            _autoDiscovery = NewCheckBox("Automatic LAN discovery", 14, 61, _config.AutoDiscovery); box.Controls.Add(_autoDiscovery);
            _autostart = NewCheckBox("Start with Windows (silent)", 14, 88, _config.StartWithWindows); box.Controls.Add(_autostart);

            Label groupLabel = NewLabel(14, 122, 245, 20); groupLabel.Text = "Clipboard groups (one per line):"; box.Controls.Add(groupLabel);
            _groupsEdit = new TextBox(); _groupsEdit.Multiline = true; _groupsEdit.ScrollBars = ScrollBars.Vertical; _groupsEdit.Location = new Point(14, 145); _groupsEdit.Size = new Size(285, 175); _groupsEdit.Text = GroupsForEditor(); box.Controls.Add(_groupsEdit);
            Label format = NewLabel(14, 324, 285, 52); format.AutoEllipsis = false; format.UseMnemonic = false; format.AutoSize = false; format.TextAlign = ContentAlignment.TopLeft; format.Text = "Format: GroupName:SharedKey\r\nCopies are announced to every joined group."; box.Controls.Add(format);

            _showTray = NewCheckBox("Show tray icon", 14, 379, _config.ShowTrayIcon); box.Controls.Add(_showTray);
            _syncText = NewCheckBox("Sync text", 14, 406, _config.SyncText); box.Controls.Add(_syncText);
            _syncImages = NewCheckBox("Sync images", 14, 433, _config.SyncImages); box.Controls.Add(_syncImages);
            _syncFiles = NewCheckBox("Sync files/folders", 14, 460, _config.SyncFiles); box.Controls.Add(_syncFiles);

            Button apply = new Button(); apply.Text = "Apply && Save"; apply.Location = new Point(14, 496); apply.Size = new Size(135, 30); apply.Click += delegate { ApplySettings(); }; box.Controls.Add(apply);
            Button refresh = new Button(); refresh.Text = "Refresh"; refresh.TextAlign = ContentAlignment.MiddleCenter; refresh.Location = new Point(158, 496); refresh.Size = new Size(141, 30); refresh.Click += delegate { RefreshConnection(); }; box.Controls.Add(refresh);

            _connectButton = new Button(); _connectButton.Text = "Disconnect"; _connectButton.Location = new Point(14, 534); _connectButton.Size = new Size(135, 30); _connectButton.Click += delegate { ToggleConnection(); }; box.Controls.Add(_connectButton);
            Button exit = new Button(); exit.Text = "Exit ClipBridge"; exit.Location = new Point(158, 534); exit.Size = new Size(141, 30); exit.Click += delegate { Close(); }; box.Controls.Add(exit);

            _saveStatus = NewLabel(14, 572, 285, 20); _saveStatus.Text = "Settings loaded."; box.Controls.Add(_saveStatus);
        }

        private void BuildHelpPanel()
        {
            GroupBox box = NewGroupBox("Help", 777, 10, 311, 615);
            Controls.Add(box);
            TabControl tabs = new TabControl(); tabs.Location = new Point(10, 21); tabs.Size = new Size(291, 582); box.Controls.Add(tabs);

            TabPage getting = new TabPage("Getting Started");
            GettingStartedPanel gp = new GettingStartedPanel(); gp.Dock = DockStyle.Fill; getting.Controls.Add(gp); tabs.TabPages.Add(getting);
            tabs.TabPages.Add(MakeHelpPage("Settings", "Port\r\nTCP and UDP use the same port number. Changing it restarts the networking layer automatically when connected.\r\n\r\nAutomatic LAN discovery\r\nFinds peers on the local IPv4 subnet.\r\n\r\nConnect / Disconnect\r\nTemporarily enables or disables all ClipBridge network activity without closing the application. This state is not saved; ClipBridge starts connected next time.\r\n\r\nStart with Windows\r\nRuns ClipBridge silently for the current Windows user.\r\n\r\nShow tray icon\r\nMinimizing sends the app to the tray when enabled."));
            tabs.TabPages.Add(MakeHelpPage("Groups", "Each line uses:\r\nGroupName:SharedKey\r\n\r\nA computer can belong to multiple groups. Every local copy is announced to every group configured on that computer.\r\n\r\nOnly peers with a matching group name and shared key can connect through that group."));
            tabs.TabPages.Add(MakeHelpPage("Troubleshooting", "No peer detected?\r\n\r\n1. Confirm both PCs use the same port and at least one matching group/key.\r\n2. Allow TCP and UDP on that port in both firewalls.\r\n3. Confirm the PCs can reach each other on the LAN.\r\n4. Click Refresh.\r\n5. If the problem remains, verify the peer status and firewall/network settings on both computers."));
            tabs.TabPages.Add(MakeHelpPage("About", "ClipBridge v1.0\r\n\r\nShared on-demand clipboard for Windows XP and newer Windows systems using .NET Framework 4.0.\r\n\r\nSupports text, images, files and folders, automatic peer discovery, multiple clipboard groups and concurrent file transfers."));
        }

        private TabPage MakeHelpPage(string title, string text)
        {
            TabPage page = new TabPage(title);
            TextBox tb = new TextBox(); tb.Multiline = true; tb.ReadOnly = true; tb.BorderStyle = BorderStyle.None; tb.BackColor = SystemColors.Window; tb.ScrollBars = ScrollBars.Vertical; tb.Dock = DockStyle.Fill; tb.Text = text;
            page.Controls.Add(tb);
            return page;
        }

        private static GroupBox NewGroupBox(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox(); g.Text = text; g.Location = new Point(x, y); g.Size = new Size(w, h); return g;
        }

        private static Label NewLabel(int x, int y, int w, int h)
        {
            Label l = new Label(); l.Location = new Point(x, y); l.Size = new Size(w, h); l.AutoEllipsis = true; return l;
        }

        private static StatusLamp NewLamp(int x, int y)
        {
            StatusLamp l = new StatusLamp(); l.Location = new Point(x, y); return l;
        }

        private static CheckBox NewCheckBox(string text, int x, int y, bool value)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.Location = new Point(x, y); c.AutoSize = true; c.Checked = value; return c;
        }

        private string GroupsForEditor()
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < _config.Groups.Count; i++)
            {
                if (i != 0) b.AppendLine();
                b.Append(_config.Groups[i].Name); b.Append(':'); b.Append(_config.Groups[i].Key);
            }
            return b.ToString();
        }

        private string GroupNames()
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < _config.Groups.Count; i++)
            {
                if (i != 0) b.Append(", ");
                b.Append(_config.Groups[i].Name);
            }
            return b.ToString();
        }

        private void StartBridges()
        {
            if (_started) return;
            _started = true;
            IntPtr h = Handle;
            _network.Start();
            _clipboard.Start();
            RefreshAllStatus();
        }

        private void StartShowWatcher()
        {
            if (_showEvent == null || _showThread != null) return;
            _showThread = new Thread(new ThreadStart(delegate
            {
                while (!_closing)
                {
                    try
                    {
                        if (!_showEvent.WaitOne(500)) continue;
                        if (_closing) break;
                        try { BeginInvoke((MethodInvoker)delegate { ShowSettingsWindow(); }); } catch { }
                    }
                    catch { break; }
                }
            }));
            _showThread.IsBackground = true;
            _showThread.Name = "ClipBridge show-window signal";
            _showThread.Start();
        }

        private void ShowSettingsWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ApplySettings()
        {
            int oldPort = _config.Port;
            bool oldDiscovery = _config.AutoDiscovery;
            string oldGroups = _config.GroupsText;
            try
            {
                _config.Port = Decimal.ToInt32(_port.Value);
                _config.AutoDiscovery = _autoDiscovery.Checked;
                _config.StartWithWindows = _autostart.Checked;
                _config.ShowTrayIcon = _showTray.Checked;
                _config.SyncText = _syncText.Checked;
                _config.SyncImages = _syncImages.Checked;
                _config.SyncFiles = _syncFiles.Checked;
                _config.SetGroups(_groupsEdit.Text);
                _config.SaveUserEditableSettings();
                Program.ApplyStartupSetting(_config.StartWithWindows);
                bool networkChanged = oldPort != _config.Port || oldDiscovery != _config.AutoDiscovery || !String.Equals(oldGroups, _config.GroupsText, StringComparison.Ordinal);
                if (networkChanged && _network.IsRunning) _network.Restart();
                UpdateTrayIcon();
                _groupsEdit.Text = GroupsForEditor();
                _saveStatus.Text = "Saved " + DateTime.Now.ToString("HH:mm:ss");
                RefreshAllStatus();
            }
            catch (Exception ex)
            {
                _saveStatus.Text = "Not applied";
                MessageBox.Show(this, ex.Message, "ClipBridge settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshConnection()
        {
            if (!_network.IsRunning)
            {
                _saveStatus.Text = "Disconnected - click Connect first.";
                RefreshAllStatus();
                return;
            }
            try
            {
                _saveStatus.Text = "Refreshing...";
                _network.Restart();
                RefreshAllStatus();
                _saveStatus.Text = "Refreshed " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                _saveStatus.Text = "Refresh failed";
                MessageBox.Show(this, ex.Message, "ClipBridge connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ToggleConnection()
        {
            if (_network.IsRunning) DisconnectNetwork(); else ConnectNetwork();
        }

        private void DisconnectNetwork()
        {
            try
            {
                _saveStatus.Text = "Disconnecting...";
                _network.Stop();
                RefreshAllStatus();
                _saveStatus.Text = "Disconnected.";
            }
            catch (Exception ex)
            {
                _saveStatus.Text = "Disconnect failed";
                MessageBox.Show(this, ex.Message, "ClipBridge connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConnectNetwork()
        {
            try
            {
                _saveStatus.Text = "Connecting...";
                _network.Start();
                RefreshAllStatus();
                _saveStatus.Text = "Connected / listening.";
            }
            catch (Exception ex)
            {
                _saveStatus.Text = "Connect failed";
                RefreshAllStatus();
                MessageBox.Show(this, ex.Message, "ClipBridge connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshAllStatus()
        {
            RefreshConnectionStatus();
            RefreshPeerList();
            string ip = GetLocalIPv4Summary();
            _identity.Text = "This computer: " + _config.ComputerName + "\r\nLocal IP: " + ip;
        }

        private void RefreshConnectionStatus()
        {
            if (_network == null) return;

            if (!_network.IsRunning)
            {
                _discoveryLamp.LampColor = Color.Red;
                _discoveryText.Text = "Disconnected";
                _tcpLamp.LampColor = Color.Red;
                _tcpText.Text = "Disconnected";
                _peerLamp.LampColor = Color.Red;
                _peerText.Text = "Disconnected by user";
                if (_connectButton != null) _connectButton.Text = "Connect";
                return;
            }

            if (_connectButton != null) _connectButton.Text = "Disconnect";
            if (_config.AutoDiscovery)
            {
                _discoveryLamp.LampColor = _network.IsDiscoveryListening ? Color.LimeGreen : Color.Red;
                _discoveryText.Text = _network.IsDiscoveryListening ? "Listening on UDP " + _config.Port : "Not listening";
            }
            else
            {
                _discoveryLamp.LampColor = Color.Goldenrod;
                _discoveryText.Text = "Disabled";
            }
            _tcpLamp.LampColor = _network.IsTcpListening ? Color.LimeGreen : Color.Red;
            _tcpText.Text = _network.IsTcpListening ? "Listening on TCP " + _config.Port : "Not listening";
            int count = _network.PeerCount;
            _peerLamp.LampColor = count > 0 ? Color.LimeGreen : Color.Red;
            _peerText.Text = count > 0 ? "Connected (" + count + " peer" + (count == 1 ? "" : "s") + ")" : "Not connected";
        }

        private void RefreshPeerList()
        {
            if (_peers == null || _network == null) return;
            NetworkBridge.PeerInfo[] infos = _network.GetPeerInfos();
            _peers.BeginUpdate();
            _peers.Items.Clear();
            for (int i = 0; i < infos.Length; i++)
            {
                NetworkBridge.PeerInfo p = infos[i];
                ListViewItem item = new ListViewItem("●");
                item.ForeColor = Color.Green;
                item.SubItems.Add(String.IsNullOrEmpty(p.ComputerName) ? p.MachineId : p.ComputerName);
                item.SubItems.Add(p.Address);
                item.SubItems.Add(p.Groups);
                _peers.Items.Add(item);
            }
            _peers.EndUpdate();
        }

        private static string GetLocalIPv4Summary()
        {
            try
            {
                IPAddress[] a = Dns.GetHostAddresses(Dns.GetHostName());
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i].AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(a[i])) continue;
                    if (b.Length != 0) b.Append(", ");
                    b.Append(a[i].ToString());
                }
                return b.Length == 0 ? "unknown" : b.ToString();
            }
            catch { return "unknown"; }
        }

        private void UpdateTrayIcon()
        {
            if (_config.ShowTrayIcon)
            {
                if (_tray == null) CreateTrayIcon();
                _tray.Visible = true;
            }
            else if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }

        private void CreateTrayIcon()
        {
            _tray = new NotifyIcon();
            try { _tray.Icon = new Icon(Icon, 16, 16); } catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = "ClipBridge";
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Status / Settings", delegate { ShowSettingsWindow(); });
            menu.MenuItems.Add("Connect / Disconnect", delegate { ToggleConnection(); });
            menu.MenuItems.Add("Refresh", delegate { RefreshConnection(); });
            menu.MenuItems.Add("Hide", delegate { Hide(); });
            menu.MenuItems.Add("Exit", delegate { Close(); });
            _tray.ContextMenu = menu;
            _tray.DoubleClick += delegate { ShowSettingsWindow(); };
        }

        private void OnFormResize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && _config.ShowTrayIcon)
                BeginInvoke((MethodInvoker)delegate { Hide(); });
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (_clipboard != null && _clipboard.HandleWindowMessage(ref m)) return;
            base.WndProc(ref m);
        }

        private void OnConnectionChanged(bool connected, string message) { OnPeersChanged(_network.PeerCount, message); }

        private void OnPeersChanged(int count, string message)
        {
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)delegate { OnPeersChanged(count, message); }); } catch { } return; }
            RefreshConnectionStatus();
            RefreshPeerList();
            if (_tray != null) _tray.Text = "ClipBridge - " + count + " peer" + (count == 1 ? "" : "s");
        }

        private void OnActivity(string text)
        {
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)delegate { OnActivity(text); }); } catch { } return; }
            _activity.Text = DateTime.Now.ToString("HH:mm:ss") + "   " + text;
        }

        private void DisposeBridges()
        {
            _closing = true;
            try { if (_showEvent != null) _showEvent.Set(); } catch { }
            try { if (_showThread != null) _showThread.Join(750); } catch { }
            try { _clipboard.Dispose(); } catch { }
            try { _network.Dispose(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        }
    }
}
