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

        private volatile string _ip;
        private readonly int _udpPort;
        private readonly int _httpPort;
        private readonly string _token;
        private volatile IPEndPoint _udpEndpoint;
        private readonly PanelDef[] _panels;
        private readonly byte[] _packet;
        private readonly ushort _transitionDs;
        private readonly double _startShare;
        private readonly double _endShare;

        private UdpClient _udp;
        private LedLayout _cachedLayout;
        private int[] _topPhysicalIndices = Array.Empty<int>();
        private int _extControlStarted;
        private long _lastExtControlAttemptTicks;
        private long _lastFrameTicks;

        public NanoleafSender(
            string ip = "192.168.10.168",
            int udpPort = 60222,
            int httpPort = 16021,
            string token = "Ytq5Ps3jFpGP6DMtDaKUmmM6Dee3yZnw",
            PanelDef[] panels = null,
            double startShare = 10.0 / 120.0,
            double endShare = 94.0 / 120.0,
            ushort transitionDs = 1)
        {
            _ip = ip;
            _udpPort = udpPort;
            _httpPort = httpPort;
            _token = token;
            _udpEndpoint = new IPEndPoint(IPAddress.Parse(ip), udpPort);
            _panels = panels ?? DefaultPanels;
            _startShare = startShare;
            _endShare = endShare;
            _transitionDs = transitionDs;
            _packet = new byte[2 + _panels.Length * 8];
            _packet[0] = (byte)(_panels.Length >> 8);
            _packet[1] = (byte)(_panels.Length & 0xFF);
        }

        public int PacketLength => _packet.Length;

        /// <summary>
        /// Builds the extControl v2 UDP packet in-place from the rendered RGB frame and layout.
        /// Returns the pre-allocated packet buffer (no allocations).
        /// </summary>
        public byte[] BuildPacket(byte[] rgb, int length, LedLayout layout)
        {
            if (layout == null || layout.Top <= 0 || rgb == null || length <= 0) return null;
            EnsureLayoutCache(layout);

            int topCount = layout.Top;
            int startSlot = Math.Max(0, (int)(_startShare * topCount));
            int endSlot = Math.Min(topCount - 1, (int)(_endShare * topCount));
            if (endSlot <= startSlot) { startSlot = 0; endSlot = topCount - 1; }

            int span = Math.Max(1, endSlot - startSlot);
            int halfWindow = Math.Max(2, span / Math.Max(1, _panels.Length));
            int maxByte = Math.Min(length, layout.Count * 3);

            int offset = 2;
            for (int i = 0; i < _panels.Length; i++)
            {
                PanelDef p = _panels[i];
                int center = startSlot + (int)(p.NormX * span);
                int lo = Math.Max(0, center - halfWindow);
                int hi = Math.Min(topCount - 1, center + halfWindow);

                int rSum = 0, gSum = 0, bSum = 0, count = 0;
                for (int slot = lo; slot <= hi; slot++)
                {
                    int byteIdx = _topPhysicalIndices[slot] * 3;
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
        /// Copies src[0..length] into dst and remaps the canonical Top edge (0..layout.Top-1)
        /// to sample only [startShare..endShare] (default 0.625..1.0 = top-right 37.5%).
        /// </summary>
        public static void RemapTopRightCorner(
            byte[] src,
            int length,
            LedLayout layout,
            byte[] dst,
            ref LedLayout cachedLayout,
            ref int[] topBySlot,
            double startShare = 0.625,
            double endShare = 1.0)
        {
            Buffer.BlockCopy(src, 0, dst, 0, length);
            if (layout == null || layout.Top <= 1) return;

            if (!ReferenceEquals(cachedLayout, layout))
            {
                var map = new int[layout.Top];
                for (int p = 0; p < layout.Count; p++)
                {
                    int slot = layout.Slot(p);
                    if (slot >= 0 && slot < layout.Top) map[slot] = p;
                }
                topBySlot = map;
                cachedLayout = layout;
            }

            int top = layout.Top;
            double maxIdx = top - 1;
            double s0 = startShare * maxIdx;
            double span = (endShare - startShare) * maxIdx;

            for (int slot = 0; slot < top; slot++)
            {
                int srcSlot = (int)Math.Round(s0 + (slot / maxIdx) * span);
                if (srcSlot < 0) srcSlot = 0;
                else if (srcSlot >= top) srcSlot = top - 1;

                int sOff = topBySlot[srcSlot] * 3;
                int dOff = topBySlot[slot] * 3;
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
            if (ReferenceEquals(_cachedLayout, layout)) return;
            var map = new int[layout.Count > 0 ? layout.Top : 0];
            for (int p = 0; p < layout.Count; p++)
            {
                int slot = layout.Slot(p);
                if (slot >= 0 && slot < layout.Top) map[slot] = p;
            }
            _topPhysicalIndices = map;
            _cachedLayout = layout;
        }

        private void EnsureExtControlAsync()
        {
            if (Volatile.Read(ref _extControlStarted) == 1) return;
            long nowTicks = DateTime.UtcNow.Ticks;
            long prevTicks = Interlocked.Read(ref _lastExtControlAttemptTicks);
            if (nowTicks - prevTicks < TimeSpan.TicksPerSecond * 10) return;
            if (Interlocked.CompareExchange(ref _lastExtControlAttemptTicks, nowTicks, prevTicks) != prevTicks) return;

            Task.Run(async () =>
            {
                if (await TryEnableExtControlAsync(_ip)) return;

                string discovered = DiscoverNanoleafIp();
                if (discovered != null && discovered != _ip)
                {
                    _ip = discovered;
                    _udpEndpoint = new IPEndPoint(IPAddress.Parse(discovered), _udpPort);
                    await TryEnableExtControlAsync(discovered);
                }
            });
        }

        private async Task<bool> TryEnableExtControlAsync(string ip)
        {
            try
            {
                string url = $"http://{ip}:{_httpPort}/api/v1/{_token}/effects";
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
