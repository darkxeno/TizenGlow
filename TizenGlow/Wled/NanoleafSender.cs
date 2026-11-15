using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TizenGlow.Wled
{
    /// <summary>
    /// Streams the canonical top edge of WLED's rendered RGB buffer to Nanoleaf Aurora (NL22)
    /// over UDP extControl v2 (port 60222) with zero per-frame heap allocations.
    /// </summary>
    internal sealed class NanoleafSender : IDisposable
    {
        public readonly struct PanelDef
        {
            public readonly ushort PanelId;
            public readonly double NormX;

            public PanelDef(ushort panelId, double normX)
            {
                PanelId = panelId;
                NormX = normX;
            }
        }

        // Default configuration matching the 18-panel NL22 layout above the S95B.
        public static readonly PanelDef[] DefaultPanels =
        {
            new PanelDef(6, 0.0),
            new PanelDef(19, 0.0),
            new PanelDef(1, 0.1412),
            new PanelDef(3, 0.1412),
            new PanelDef(18, 0.1431),
            new PanelDef(4, 0.2844),
            new PanelDef(2, 0.2844),
            new PanelDef(15, 0.2844),
            new PanelDef(17, 0.2844),
            new PanelDef(5, 0.4275),
            new PanelDef(16, 0.4275),
            new PanelDef(8, 0.5706),
            new PanelDef(9, 0.5706),
            new PanelDef(7, 0.7137),
            new PanelDef(14, 0.7137),
            new PanelDef(11, 0.8569),
            new PanelDef(12, 0.8569),
            new PanelDef(13, 1.0),
        };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private volatile bool _enabled;
        private volatile string _ip;
        private readonly int _udpPort;
        private readonly int _httpPort;
        private volatile string _token;
        private volatile IPEndPoint _udpEndpoint;
        private readonly PanelDef[] _panels;
        private readonly byte[] _packet;
        private readonly ushort _transitionDs;
        private volatile int _edge;
        private double _startShare;
        private double _endShare;

        private UdpClient _udp;
        private LedLayout _cachedLayout;
        private int[] _slotToPhysical = Array.Empty<int>();
        private int _extControlStarted;
        private long _lastExtControlAttemptTicks;
        private long _lastFrameTicks;

        public NanoleafSender(
            string ip = "",
            int udpPort = 60222,
            int httpPort = 16021,
            string token = "",
            PanelDef[] panels = null,
            double startShare = 0.0,
            double endShare = 1.0,
            ushort transitionDs = 1,
            bool enabled = false,
            int edge = 0)
        {
            _udpPort = udpPort;
            _httpPort = httpPort;
            _panels = panels ?? DefaultPanels;
            _startShare = startShare;
            _endShare = endShare;
            _transitionDs = transitionDs;
            _enabled = enabled;
            _edge = edge >= 0 && edge <= 3 ? edge : 0;
            _packet = new byte[2 + _panels.Length * 8];
            _packet[0] = (byte)(_panels.Length >> 8);
            _packet[1] = (byte)(_panels.Length & 0xFF);

            TrySetIp(ip);
            TrySetToken(token);
        }

        public int PacketLength => _packet.Length;
        public bool Enabled => _enabled;
        public string Ip => _ip;
        public string Token => _token;
        public int Edge => _edge;
        public double StartShare => _startShare;
        public double EndShare => _endShare;

        public bool TrySetEnabled(string value)
        {
            if (value != "on" && value != "off") return false;
            _enabled = value == "on";
            if (!_enabled) ResetExtControl();
            return true;
        }

        public bool TrySetIp(string value)
        {
            string trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0)
            {
                _ip = "";
                _udpEndpoint = null;
                ResetExtControl();
                return true;
            }
            IPAddress addr;
            if (!IPAddress.TryParse(trimmed, out addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
            _ip = addr.ToString();
            _udpEndpoint = new IPEndPoint(addr, _udpPort);
            ResetExtControl();
            return true;
        }

        public bool TrySetToken(string value)
        {
            _token = (value ?? "").Trim();
            ResetExtControl();
            return true;
        }

        public bool TrySetEdge(string value)
        {
            switch (value)
            {
                case "top": _edge = 0; return true;
                case "right": _edge = 1; return true;
                case "bottom": _edge = 2; return true;
                case "left": _edge = 3; return true;
                default: return false;
            }
        }

        public bool TrySetRange(string value)
        {
            double start, end;
            if (!TryParseRange(value, out start, out end)) return false;
            _startShare = start;
            _endShare = end;
            return true;
        }

        public static bool TryParseRange(string value, out double startShare, out double endShare)
        {
            switch (value)
            {
                case "full": startShare = 0.0; endShare = 1.0; return true;
                case "center70": startShare = 10.0 / 120.0; endShare = 94.0 / 120.0; return true;
                case "left50": startShare = 0.0; endShare = 0.5; return true;
                case "right50": startShare = 0.5; endShare = 1.0; return true;
                case "right38": startShare = 0.625; endShare = 1.0; return true;
                case "left62": startShare = 0.0; endShare = 0.625; return true;
                default: startShare = 0.0; endShare = 1.0; return false;
            }
        }

        public static void GetEdgeSlotRange(LedLayout layout, int edge, out int edgeStart, out int edgeCount)
        {
            if (layout == null) { edgeStart = 0; edgeCount = 0; return; }
            switch (edge)
            {
                case 0: edgeStart = 0; edgeCount = layout.Top; break;
                case 1: edgeStart = layout.Top; edgeCount = layout.Right; break;
                case 2: edgeStart = layout.Top + layout.Right; edgeCount = layout.Bottom; break;
                case 3: edgeStart = layout.Top + layout.Right + layout.Bottom; edgeCount = layout.Left; break;
                default: edgeStart = 0; edgeCount = 0; break;
            }
        }

        /// <summary>
        /// Builds the extControl v2 UDP packet in-place from the rendered RGB frame and layout.
        /// Returns the pre-allocated packet buffer (no allocations).
        /// </summary>
        public byte[] BuildPacket(byte[] rgb, int length, LedLayout layout)
        {
            if (layout == null || rgb == null || length <= 0) return null;
            int edgeStart, edgeCount;
            GetEdgeSlotRange(layout, _edge, out edgeStart, out edgeCount);
            if (edgeCount <= 0) return null;

            EnsureLayoutCache(layout);

            int startSlot = Math.Max(0, (int)(_startShare * edgeCount));
            int endSlot = Math.Min(edgeCount - 1, (int)(_endShare * edgeCount));
            if (endSlot <= startSlot) { startSlot = 0; endSlot = edgeCount - 1; }

            int span = Math.Max(1, endSlot - startSlot);
            int halfWindow = Math.Max(2, span / Math.Max(1, _panels.Length));
            int maxByte = Math.Min(length, layout.Count * 3);

            int offset = 2;
            for (int i = 0; i < _panels.Length; i++)
            {
                PanelDef p = _panels[i];
                int center = startSlot + (int)(p.NormX * span);
                int lo = Math.Max(0, center - halfWindow);
                int hi = Math.Min(edgeCount - 1, center + halfWindow);

                int rSum = 0, gSum = 0, bSum = 0, count = 0;
                for (int slot = lo; slot <= hi; slot++)
                {
                    int byteIdx = _slotToPhysical[edgeStart + slot] * 3;
                    if (byteIdx + 2 < maxByte)
                    {
                        rSum += rgb[byteIdx];
                        gSum += rgb[byteIdx + 1];
                        bSum += rgb[byteIdx + 2];
                        count++;
                    }
                }

                byte r = count > 0 ? (byte)(rSum / count) : (byte)0;
                byte g = count > 0 ? (byte)(gSum / count) : (byte)0;
                byte b = count > 0 ? (byte)(bSum / count) : (byte)0;

                _packet[offset] = (byte)(p.PanelId >> 8);
                _packet[offset + 1] = (byte)(p.PanelId & 0xFF);
                _packet[offset + 2] = r;
                _packet[offset + 3] = g;
                _packet[offset + 4] = b;
                _packet[offset + 5] = 0;
                _packet[offset + 6] = (byte)(_transitionDs >> 8);
                _packet[offset + 7] = (byte)(_transitionDs & 0xFF);
                offset += 8;
            }

            return _packet;
        }

        /// <summary>
        /// Copies src[0..length] into dst and remaps the specified canonical edge (0=Top, 1=Right, 2=Bottom, 3=Left)
        /// to sample only [startShare..endShare].
        /// </summary>
        public static void RemapEdge(
            byte[] src,
            int length,
            LedLayout layout,
            int edge,
            byte[] dst,
            ref LedLayout cachedLayout,
            ref int[] slotToPhysical,
            double startShare = 0.0,
            double endShare = 1.0)
        {
            Buffer.BlockCopy(src, 0, dst, 0, length);
            if (layout == null || (startShare <= 0.0 && endShare >= 1.0)) return;

            int edgeStart, edgeCount;
            GetEdgeSlotRange(layout, edge, out edgeStart, out edgeCount);
            if (edgeCount <= 1) return;

            if (!ReferenceEquals(cachedLayout, layout) || slotToPhysical == null || slotToPhysical.Length != layout.Count)
            {
                var map = new int[layout.Count];
                for (int p = 0; p < layout.Count; p++)
                {
                    int slot = layout.Slot(p);
                    if (slot >= 0 && slot < layout.Count) map[slot] = p;
                }
                slotToPhysical = map;
                cachedLayout = layout;
            }

            double maxIdx = edgeCount - 1;
            double s0 = startShare * maxIdx;
            double span = (endShare - startShare) * maxIdx;

            for (int slot = 0; slot < edgeCount; slot++)
            {
                int srcSlot = (int)Math.Round(s0 + (slot / maxIdx) * span);
                if (srcSlot < 0) srcSlot = 0;
                else if (srcSlot >= edgeCount) srcSlot = edgeCount - 1;

                int sOff = slotToPhysical[edgeStart + srcSlot] * 3;
                int dOff = slotToPhysical[edgeStart + slot] * 3;
                if (sOff + 2 < length && dOff + 2 < length)
                {
                    dst[dOff] = src[sOff];
                    dst[dOff + 1] = src[sOff + 1];
                    dst[dOff + 2] = src[sOff + 2];
                }
            }
        }

        public void Send(byte[] rgb, int length, LedLayout layout)
        {
            if (!_enabled || _udpEndpoint == null || string.IsNullOrEmpty(_token)) return;
            byte[] pkt = BuildPacket(rgb, length, layout);
            if (pkt == null) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (_lastFrameTicks != 0 && nowTicks - _lastFrameTicks > TimeSpan.TicksPerSecond * 8)
            {
                ResetExtControl();
            }
            _lastFrameTicks = nowTicks;

            EnsureExtControlAsync();

            try
            {
                if (_udp == null) _udp = new UdpClient();
                _udp.Send(pkt, pkt.Length, _udpEndpoint);
            }
            catch
            {
                try { _udp?.Dispose(); } catch { }
                _udp = null;
            }
        }

        public void ResetExtControl()
        {
            Interlocked.Exchange(ref _extControlStarted, 0);
        }

        private void EnsureLayoutCache(LedLayout layout)
        {
            if (ReferenceEquals(_cachedLayout, layout) && _slotToPhysical.Length == layout.Count) return;
            var map = new int[layout.Count];
            for (int p = 0; p < layout.Count; p++)
            {
                int slot = layout.Slot(p);
                if (slot >= 0 && slot < layout.Count) map[slot] = p;
            }
            _slotToPhysical = map;
            _cachedLayout = layout;
        }

        private void EnsureExtControlAsync()
        {
            if (!_enabled || string.IsNullOrEmpty(_ip) || string.IsNullOrEmpty(_token)) return;
            if (Volatile.Read(ref _extControlStarted) == 1) return;
            long nowTicks = DateTime.UtcNow.Ticks;
            long prevTicks = Interlocked.Read(ref _lastExtControlAttemptTicks);
            if (nowTicks - prevTicks < TimeSpan.TicksPerSecond * 10) return;
            if (Interlocked.CompareExchange(ref _lastExtControlAttemptTicks, nowTicks, prevTicks) != prevTicks) return;

            string ipSnapshot = _ip;
            Task.Run(async () =>
            {
                if (await TryEnableExtControlAsync(ipSnapshot)) return;

                string discovered = DiscoverNanoleafIp();
                if (discovered != null && discovered != ipSnapshot)
                {
                    _ip = discovered;
                    _udpEndpoint = new IPEndPoint(IPAddress.Parse(discovered), _udpPort);
                    await TryEnableExtControlAsync(discovered);
                }
            });
        }

        private async Task<bool> TryEnableExtControlAsync(string ip)
        {
            string token = _token;
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(token)) return false;
            try
            {
                string url = $"http://{ip}:{_httpPort}/api/v1/{token}/effects";
                using (var content = new StringContent(
                    "{\"write\":{\"command\":\"display\",\"animType\":\"extControl\",\"extControlVersion\":\"v2\"}}",
                    Encoding.UTF8,
                    "application/json"))
                using (HttpResponseMessage resp = await Http.PutAsync(url, content))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        Interlocked.Exchange(ref _extControlStarted, 1);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string DiscoverNanoleafIp()
        {
            try
            {
                using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    sock.ReceiveTimeout = 2000;
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    var mcast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                    byte[] qAurora = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: nanoleaf_aurora:light\r\n\r\n");
                    byte[] qNl29 = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: nanoleaf:nl29\r\n\r\n");

                    sock.SendTo(qAurora, mcast);
                    sock.SendTo(qNl29, mcast);

                    var buf = new byte[2048];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int n = sock.ReceiveFrom(buf, ref remote);
                    if (n > 0 && remote is IPEndPoint ep)
                    {
                        return ep.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            try { _udp?.Dispose(); } catch { }
            _udp = null;
        }
    }
}
