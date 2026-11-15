using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TizenGlow
{
    /// <summary>
    /// Non-blocking UDP receiver for external TGVP v1 perimeter RGB frames on 127.0.0.1:19446.
    /// Allows an external capture helper (e.g. vp_bridge running outside the TPK SMACK domain)
    /// to feed high-FPS HDMI frames into TizenGlow with automatic fallback when idle.
    /// </summary>
    internal static class TgvpUdpReceiver
    {
        public const int DefaultListenPort = 19446;
        private const long DefaultStaleTimeoutMs = 500;
        private const int MaxPacketBytes = 6 + 255 * 3;

        private static readonly object _lock = new object();
        private static Socket _socket;
        private static int _listenPort = DefaultListenPort;
        private static long _staleTimeoutMs = DefaultStaleTimeoutMs;
        private static bool _initAttempted;
        private static bool _initFailed;
        private static readonly byte[] _rxBuf = new byte[MaxPacketBytes];
        private static readonly byte[] _latestRgb = new byte[255 * 3];
        private static int _latestCount;
        private static long _lastPacketTicks;
        private static volatile string _status = "idle";

        public static string Status { get { return _status; } }
        public static int BoundPort
        {
            get
            {
                var ep = _socket?.LocalEndPoint as IPEndPoint;
                return ep != null ? ep.Port : _listenPort;
            }
        }

        internal static void ConfigureForTests(int port, long staleTimeoutMs = DefaultStaleTimeoutMs)
        {
            lock (_lock)
            {
                try { _socket?.Close(); } catch { }
                _socket = null;
                _listenPort = port;
                _staleTimeoutMs = staleTimeoutMs;
                _initAttempted = false;
                _initFailed = false;
                _latestCount = 0;
                _lastPacketTicks = 0;
                _status = "idle";
            }
        }

        private static bool EnsureInit()
        {
            if (_socket != null) return true;
            if (_initFailed || _initAttempted) return false;

            lock (_lock)
            {
                if (_socket != null) return true;
                if (_initFailed || _initAttempted) return false;
                _initAttempted = true;

                try
                {
                    var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    sock.Blocking = false;
                    sock.Bind(new IPEndPoint(IPAddress.Loopback, _listenPort));
                    _socket = sock;
                    int bound = BoundPort;
                    _status = "listening (udp:" + bound + ")";
                    AppLog.Info("TGVP", "Listening on 127.0.0.1:" + bound);
                    return true;
                }
                catch (Exception ex)
                {
                    _initFailed = true;
                    _status = "bind failed (" + ex.Message + ")";
                    AppLog.Info("TGVP", "UDP bind failed on port " + _listenPort + ": " + ex.Message);
                    return false;
                }
            }
        }

        private static void DrainPackets()
        {
            if (_socket == null) return;

            try
            {
                while (_socket.Available > 0)
                {
                    int n = _socket.Receive(_rxBuf, 0, _rxBuf.Length, SocketFlags.None);
                    if (n < 9) continue;
                    if (_rxBuf[0] != (byte)'T' || _rxBuf[1] != (byte)'G' ||
                        _rxBuf[2] != (byte)'V' || _rxBuf[3] != (byte)'P')
                    {
                        continue;
                    }
                    if (_rxBuf[4] != 1) continue;

                    int count = _rxBuf[5];
                    if (count <= 0 || n < 6 + count * 3) continue;

                    Buffer.BlockCopy(_rxBuf, 6, _latestRgb, 0, count * 3);
                    _latestCount = count;
                    _lastPacketTicks = Stopwatch.GetTimestamp();
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock)
                {
                    AppLog.Info("TGVP", "Socket receive error: " + ex.SocketErrorCode);
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("TGVP", "Receive error: " + ex.Message);
            }
        }

        public static bool TryGetColors(CapturePoint[] points, out Color[] colors)
        {
            colors = null;
            if (points == null || points.Length == 0) return false;
            if (!EnsureInit()) return false;

            DrainPackets();

            int port = BoundPort;
            if (_latestCount <= 0 || _lastPacketTicks == 0)
            {
                _status = "waiting (TGVP UDP :" + port + ")";
                return false;
            }

            long ageMs = (Stopwatch.GetTimestamp() - _lastPacketTicks) * 1000L / Stopwatch.Frequency;
            if (ageMs > _staleTimeoutMs)
            {
                _status = "stale (TGVP UDP :" + port + " -> fallback)";
                return false;
            }

            _status = "active (TGVP UDP :" + port + ", " + _latestCount + " pts)";
            colors = MapPerimeterToPoints(_latestRgb, _latestCount, points);
            return true;
        }

        internal static Color[] MapPerimeterToPoints(byte[] rgb, int count, CapturePoint[] points)
        {
            var result = new Color[points.Length];

            int topCount, rightCount, bottomCount, leftCount;
            if (count == 24)
            {
                topCount = 7; rightCount = 5; bottomCount = 7; leftCount = 5;
            }
            else
            {
                topCount = Math.Max(1, (int)Math.Round(count * (7.0 / 24.0)));
                rightCount = Math.Max(1, (int)Math.Round(count * (5.0 / 24.0)));
                bottomCount = Math.Max(1, (int)Math.Round(count * (7.0 / 24.0)));
                leftCount = Math.Max(1, count - topCount - rightCount - bottomCount);
                while (topCount + rightCount + bottomCount + leftCount > count && bottomCount > 1) bottomCount--;
                while (topCount + rightCount + bottomCount + leftCount > count && topCount > 1) topCount--;
                while (topCount + rightCount + bottomCount + leftCount > count && rightCount > 1) rightCount--;
                while (topCount + rightCount + bottomCount + leftCount < count) leftCount++;
            }

            int rightStart = topCount;
            int bottomStart = rightStart + rightCount;
            int leftStart = bottomStart + bottomCount;

            for (int i = 0; i < points.Length; i++)
            {
                double x = points[i].X;
                double y = points[i].Y;
                if (x < 0.0) x = 0.0; else if (x > 1.0) x = 1.0;
                if (y < 0.0) y = 0.0; else if (y > 1.0) y = 1.0;

                double dTop = y;
                double dRight = 1.0 - x;
                double dBottom = 1.0 - y;
                double dLeft = x;

                int segStart, segCount;
                double t;

                if (dTop <= dRight && dTop <= dBottom && dTop <= dLeft)
                {
                    segStart = 0;
                    segCount = topCount;
                    t = x;
                }
                else if (dRight <= dTop && dRight <= dBottom && dRight <= dLeft)
                {
                    segStart = rightStart;
                    segCount = rightCount;
                    t = y;
                }
                else if (dBottom <= dTop && dBottom <= dRight && dBottom <= dLeft)
                {
                    segStart = bottomStart;
                    segCount = bottomCount;
                    t = 1.0 - x;
                }
                else
                {
                    segStart = leftStart;
                    segCount = leftCount;
                    t = 1.0 - y;
                }

                double pos = t * segCount - 0.5;
                if (pos < 0.0) pos = 0.0;
                if (pos > segCount - 1) pos = segCount - 1;

                int idx0 = segStart + (int)Math.Floor(pos);
                int idx1 = Math.Min(segStart + segCount - 1, idx0 + 1);
                double frac = pos - Math.Floor(pos);

                int o0 = idx0 * 3;
                int o1 = idx1 * 3;
                byte r = (byte)Math.Round(rgb[o0] * (1.0 - frac) + rgb[o1] * frac);
                byte g = (byte)Math.Round(rgb[o0 + 1] * (1.0 - frac) + rgb[o1 + 1] * frac);
                byte b = (byte)Math.Round(rgb[o0 + 2] * (1.0 - frac) + rgb[o1 + 2] * frac);
                result[i] = new Color { R = r, G = g, B = b };
            }

            return result;
        }
    }
}
