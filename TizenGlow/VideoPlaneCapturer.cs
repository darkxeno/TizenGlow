using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TizenGlow
{
    /// <summary>
    /// Captures the decoded 960x540 NV12 video plane via libvideo-capture.so.0.1.0
    /// (secvideo_api_capture_video_main), adapted from TizenAmbilight (MIT).
    /// Returns false on DRM-protected content (rc = -4) or missing partner privileges
    /// so Capturer seamlessly falls back to libvideoenhance.so.
    /// </summary>
    internal static class VideoPlaneCapturer
    {
        private const string LibName = "libvideo-capture.so.0.1.0";
        private const string Symbol = "secvideo_api_capture_video_main";

        private const int PlaneW = 960;
        private const int PlaneH = 540;
        private const int YBytes = PlaneW * PlaneH;
        private const int CBytes = YBytes / 2;
        private const int StructBytes = 256;

        private const int OffYSize = 0x00;
        private const int OffCSize = 0x04;
        private const int OffRetWidth = 0x08;
        private const int OffRetHeight = 0x0c;
        private const int OffYAddr = 0x10;
        private const int OffCAddr = 0x14;
        private const int OffColorFormat = 0x1c;
        private const int OffYPitch = 0x2c;
        private const int OffCPitch = 0x30;

        private const int RTLD_LAZY = 0x00001;
        private const int RTLD_NOW = 0x00002;

        private const int MaxConsecutiveFailuresBeforeFallback = 3;
        private const long FallbackProbeIntervalMs = 2000;
        private const double HdrPeakNits = 203.0;
        private const int BarThresh = 18;
        private const int MinBarPx = 12;
        private const int BarHoldFrames = 8;

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureVideoMainFn(int reqW, int reqH, IntPtr param);

        private static bool _initAttempted;
        private static bool _unavailable;
        private static IntPtr _lib = IntPtr.Zero;
        private static IntPtr _param = IntPtr.Zero;
        private static IntPtr _bufY = IntPtr.Zero;
        private static IntPtr _bufC = IntPtr.Zero;
        private static CaptureVideoMainFn _call;

        private static readonly byte[] _y = new byte[YBytes];
        private static readonly byte[] _c = new byte[CBytes];
        private static readonly byte[] _pqTable = BuildPqToSdrTable();

        private static int _consecutiveFailures;
        private static long _nextProbeTimestamp;
        private static bool _inFallback;

        // Configurable features (with sensible defaults and ability to disable each).
        private static volatile bool _hdrEnabled = true;
        private static volatile bool _blackBarsEnabled = true;
        private static volatile int _spatialBlurPercent = 25;

        // Hysteresis state for Y-plane active area detection.
        private static int _activeTop, _activeBottom = PlaneH, _activeLeft, _activeRight = PlaneW;
        private static int _candTop, _candBottom = PlaneH, _candLeft, _candRight = PlaneW;
        private static int _barHoldCount;

        public static string HdrSetting { get { return _hdrEnabled ? "on" : "off"; } }
        public static string BlackBarsSetting { get { return _blackBarsEnabled ? "on" : "off"; } }
        public static int SpatialBlurSetting { get { return _spatialBlurPercent; } }

        public static bool TrySetHdr(string value)
        {
            if (value != "on" && value != "off") return false;
            _hdrEnabled = (value == "on");
            return true;
        }

        public static bool TrySetBlackBars(string value)
        {
            if (value != "on" && value != "off") return false;
            _blackBarsEnabled = (value == "on");
            if (!_blackBarsEnabled)
            {
                _activeTop = 0; _activeBottom = PlaneH;
                _activeLeft = 0; _activeRight = PlaneW;
                _barHoldCount = 0;
            }
            return true;
        }

        public static bool TrySetSpatialBlur(string value)
        {
            int pct;
            if (!int.TryParse(value, out pct) || pct < 0 || pct > 50) return false;
            _spatialBlurPercent = pct;
            return true;
        }

        public static bool IsActive { get { return _call != null && !_unavailable && !_inFallback; } }

        private static bool EnsureInit()
        {
            if (_call != null) return true;
            if (_unavailable || _initAttempted) return false;
            _initAttempted = true;

            try
            {
                _lib = dlopen(LibName, RTLD_NOW);
                if (_lib == IntPtr.Zero) _lib = dlopen(LibName, RTLD_LAZY);
                if (_lib == IntPtr.Zero) _lib = dlopen("/usr/lib/" + LibName, RTLD_LAZY);
                if (_lib == IntPtr.Zero) { _unavailable = true; return false; }

                IntPtr fn = dlsym(_lib, Symbol);
                if (fn == IntPtr.Zero) { _unavailable = true; return false; }

                _param = Marshal.AllocHGlobal(StructBytes);
                _bufY = Marshal.AllocHGlobal(YBytes);
                _bufC = Marshal.AllocHGlobal(CBytes);
                if (_param == IntPtr.Zero || _bufY == IntPtr.Zero || _bufC == IntPtr.Zero)
                {
                    Shutdown();
                    _unavailable = true;
                    return false;
                }

                for (int i = 0; i < StructBytes; i++) Marshal.WriteByte(_param, i, 0);
                Marshal.WriteInt32(_param, OffYSize, YBytes);
                Marshal.WriteInt32(_param, OffCSize, CBytes);
                Marshal.WriteInt32(_param, OffYAddr, _bufY.ToInt32());
                Marshal.WriteInt32(_param, OffCAddr, _bufC.ToInt32());
                Marshal.WriteInt32(_param, OffColorFormat, 0);

                _call = (CaptureVideoMainFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(CaptureVideoMainFn));
                return true;
            }
            catch
            {
                Shutdown();
                _unavailable = true;
                return false;
            }
        }

        public static bool TryCapturePoints(CapturePoint[] points, out Color[] colors)
        {
            colors = null;
            if (points == null || points.Length == 0) return false;
            if (!EnsureInit()) return false;

            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            if (_inFallback)
            {
                if (nowMs < _nextProbeTimestamp) return false;
            }

            int rc;
            try
            {
                rc = _call(PlaneW, PlaneH, _param);
            }
            catch
            {
                _unavailable = true;
                return false;
            }

            if (rc != 0)
            {
                // rc == -2 means the hardware capture scaler is wedged until reboot; stop calling it.
                if (rc == -2)
                {
                    _unavailable = true;
                    _inFallback = true;
                    return false;
                }

                _consecutiveFailures++;
                if (_inFallback || _consecutiveFailures >= MaxConsecutiveFailuresBeforeFallback)
                {
                    _inFallback = true;
                    _nextProbeTimestamp = nowMs + FallbackProbeIntervalMs;
                }
                return false;
            }

            _consecutiveFailures = 0;
            _inFallback = false;

            int aw = Marshal.ReadInt32(_param, OffRetWidth);
            int ah = Marshal.ReadInt32(_param, OffRetHeight);
            int w = (aw >= 64 && aw <= PlaneW) ? aw : PlaneW;
            int h = (ah >= 64 && ah <= PlaneH) ? ah : PlaneH;

            int yp = Marshal.ReadInt32(_param, OffYPitch);
            int cp = Marshal.ReadInt32(_param, OffCPitch);
            int yStride = (yp >= w && yp <= PlaneW) ? yp : w;
            int cStride = (cp >= w && cp <= PlaneW) ? cp : w;

            Marshal.Copy(_bufY, _y, 0, YBytes);
            Marshal.Copy(_bufC, _c, 0, CBytes);

            int activeLeft = 0, activeRight = w, activeTop = 0, activeBottom = h;
            if (_blackBarsEnabled)
            {
                UpdateActiveArea(w, h, yStride, out activeLeft, out activeRight, out activeTop, out activeBottom);
            }
            int activeW = Math.Max(1, activeRight - activeLeft);
            int activeH = Math.Max(1, activeBottom - activeTop);

            colors = new Color[points.Length];
            int halfBoxX = Math.Max(4, w / 80);
            int halfBoxY = Math.Max(4, h / 45);
            const int sampleStep = 3;
            bool useHdr = _hdrEnabled;

            for (int i = 0; i < points.Length; i++)
            {
                CapturePoint pt = points[i];
                int cx = activeLeft + (int)(pt.X * activeW);
                int cy = activeTop + (int)(pt.Y * activeH);
                int x0 = Math.Max(activeLeft, cx - halfBoxX);
                int x1 = Math.Min(activeRight, cx + halfBoxX + 1);
                int y0 = Math.Max(activeTop, cy - halfBoxY);
                int y1 = Math.Min(activeBottom, cy + halfBoxY + 1);
                if (x1 <= x0) x0 = Math.Max(0, x1 - 1);
                if (y1 <= y0) y0 = Math.Max(0, y1 - 1);

                long sy = 0, n = 0;
                for (int row = y0; row < y1; row += sampleStep)
                {
                    int yRow = row * yStride;
                    for (int x = x0; x < x1; x += sampleStep)
                    {
                        int yi = yRow + x;
                        if ((uint)yi < (uint)_y.Length)
                        {
                            sy += _y[yi];
                            n++;
                        }
                    }
                }

                long su = 0, sv = 0, nc = 0;
                for (int cyRow = (y0 >> 1); cyRow <= ((y1 - 1) >> 1); cyRow += sampleStep)
                {
                    int cRow = cyRow * cStride;
                    for (int cxCol = (x0 >> 1); cxCol <= ((x1 - 1) >> 1); cxCol += sampleStep)
                    {
                        int ci = cRow + (cxCol << 1);
                        if ((uint)(ci + 1) < (uint)_c.Length)
                        {
                            su += _c[ci];
                            sv += _c[ci + 1];
                            nc++;
                        }
                    }
                }

                if (n == 0) continue;

                int ym = (int)((sy + n / 2) / n);
                int um = nc > 0 ? (int)((su + nc / 2) / nc) : 128;
                int vm = nc > 0 ? (int)((sv + nc / 2) / nc) : 128;

                byte r, g, b;
                if (useHdr) YuvHdrToRgb709(ym, um, vm, out r, out g, out b);
                else YuvSdrToRgb(ym, um, vm, out r, out g, out b);
                colors[i] = new Color { R = r, G = g, B = b };
            }

            return true;
        }

        public static void ApplySpatialBlur(Color[] colors)
        {
            int pct = _spatialBlurPercent;
            if (colors == null || colors.Length < 3 || pct <= 0) return;

            int n = colors.Length;
            double amount = pct / 100.0;
            double k = 1.0 - amount;
            double m = amount * 0.5;
            var blurred = new Color[n];
            for (int i = 0; i < n; i++)
            {
                Color prev = colors[(i - 1 + n) % n];
                Color curr = colors[i];
                Color next = colors[(i + 1) % n];
                blurred[i] = new Color
                {
                    R = Clamp255(curr.R * k + (prev.R + next.R) * m),
                    G = Clamp255(curr.G * k + (prev.G + next.G) * m),
                    B = Clamp255(curr.B * k + (prev.B + next.B) * m)
                };
            }
            Array.Copy(blurred, colors, n);
        }

        private static void UpdateActiveArea(int w, int h, int yStride, out int left, out int right, out int top, out int bottom)
        {
            int newTop, newBottom, newLeft, newRight;
            DetectActiveAreaY(w, h, yStride, out newTop, out newBottom);
            DetectActiveAreaX(w, h, yStride, out newLeft, out newRight);

            if (newTop == _candTop && newBottom == _candBottom && newLeft == _candLeft && newRight == _candRight)
            {
                if (_barHoldCount < BarHoldFrames) _barHoldCount++;
                if (_barHoldCount >= BarHoldFrames)
                {
                    _activeTop = newTop; _activeBottom = newBottom;
                    _activeLeft = newLeft; _activeRight = newRight;
                }
            }
            else
            {
                _candTop = newTop; _candBottom = newBottom;
                _candLeft = newLeft; _candRight = newRight;
                _barHoldCount = 1;
            }

            top = Math.Max(0, Math.Min(_activeTop, h - 1));
            bottom = Math.Max(top + 1, Math.Min(_activeBottom, h));
            left = Math.Max(0, Math.Min(_activeLeft, w - 1));
            right = Math.Max(left + 1, Math.Min(_activeRight, w));
        }

        private static void DetectActiveAreaY(int w, int h, int yStride, out int yTop, out int yBottom)
        {
            const int step = 8;
            int maxBar = h / 4;
            int t = 0;
            while (t < maxBar && RowIsBar(t, w, yStride, step)) t++;
            yTop = (t >= maxBar) ? 0 : t;

            int b = h - 1, run = 0;
            while (run < maxBar && b > 0 && RowIsBar(b, w, yStride, step)) { b--; run++; }
            yBottom = (run >= maxBar) ? h : (b + 1);

            if (yBottom - yTop < h / 3) { yTop = 0; yBottom = h; }
            if (yTop < MinBarPx) yTop = 0;
            if (h - yBottom < MinBarPx) yBottom = h;
        }

        private static void DetectActiveAreaX(int w, int h, int yStride, out int xLeft, out int xRight)
        {
            const int step = 8;
            int maxBar = w / 4;
            int l = 0;
            while (l < maxBar && ColIsBar(l, h, yStride, step)) l++;
            xLeft = (l >= maxBar) ? 0 : l;

            int r = w - 1, run = 0;
            while (run < maxBar && r > 0 && ColIsBar(r, h, yStride, step)) { r--; run++; }
            xRight = (run >= maxBar) ? w : (r + 1);

            if (xRight - xLeft < w / 3) { xLeft = 0; xRight = w; }
            if (xLeft < MinBarPx) xLeft = 0;
            if (w - xRight < MinBarPx) xRight = w;
        }

        private static bool RowIsBar(int row, int w, int yStride, int step)
        {
            int baseIdx = row * yStride;
            if (baseIdx < 0 || baseIdx + w > _y.Length) return false;
            int dark = 0, cnt = 0;
            for (int x = 0; x < w; x += step)
            {
                if (_y[baseIdx + x] < BarThresh) dark++;
                cnt++;
            }
            return cnt > 0 && dark == cnt;
        }

        private static bool ColIsBar(int col, int h, int yStride, int step)
        {
            int dark = 0, cnt = 0;
            for (int row = 0; row < h; row += step)
            {
                int idx = row * yStride + col;
                if ((uint)idx >= (uint)_y.Length) return false;
                if (_y[idx] < BarThresh) dark++;
                cnt++;
            }
            return cnt > 0 && dark == cnt;
        }

        private static void YuvSdrToRgb(int y, int u, int v, out byte rOut, out byte gOut, out byte bOut)
        {
            int yy = Math.Max(0, y - 16);
            int uu = u - 128;
            int vv = v - 128;
            rOut = (byte)Clamp255((298 * yy + 409 * vv + 128) >> 8);
            gOut = (byte)Clamp255((298 * yy - 100 * uu - 208 * vv + 128) >> 8);
            bOut = (byte)Clamp255((298 * yy + 516 * uu + 128) >> 8);
        }

        private static void YuvHdrToRgb709(int y, int u, int v, out byte rOut, out byte gOut, out byte bOut)
        {
            double cb = u - 128.0;
            double cr = v - 128.0;
            int r0 = Clamp255(y + 1.4746 * cr);
            int g0 = Clamp255(y - 0.164553 * cb - 0.571353 * cr);
            int b0 = Clamp255(y + 1.8814 * cb);

            double l0 = 0.2627 * r0 + 0.6780 * g0 + 0.0593 * b0;
            int r, g, b;
            if (l0 <= 0.5)
            {
                int nn = _pqTable[Clamp255(l0)];
                r = nn; g = nn; b = nn;
            }
            else
            {
                double k = _pqTable[Clamp255(l0)] / l0;
                r = Clamp255(r0 * k);
                g = Clamp255(g0 * k);
                b = Clamp255(b0 * k);
            }

            ApplyGamut709(ref r, ref g, ref b);
            rOut = (byte)r;
            gOut = (byte)g;
            bOut = (byte)b;
        }

        private static void ApplyGamut709(ref int r, ref int g, ref int b)
        {
            double rl = Math.Pow(r / 255.0, 2.4);
            double gl = Math.Pow(g / 255.0, 2.4);
            double bl = Math.Pow(b / 255.0, 2.4);
            double r7 = 1.6605 * rl - 0.5876 * gl - 0.0728 * bl;
            double g7 = -0.1246 * rl + 1.1329 * gl - 0.0083 * bl;
            double b7 = -0.0182 * rl - 0.1006 * gl + 1.1187 * bl;

            double lum = 0.2126 * r7 + 0.7152 * g7 + 0.0722 * b7;
            double t = 0.0;
            t = UpdateGamutBlend(t, r7, lum);
            t = UpdateGamutBlend(t, g7, lum);
            t = UpdateGamutBlend(t, b7, lum);
            if (t > 1.0) t = 1.0;

            r7 = Clamp01(r7 + (lum - r7) * t);
            g7 = Clamp01(g7 + (lum - g7) * t);
            b7 = Clamp01(b7 + (lum - b7) * t);

            r = Clamp255(255.0 * Math.Pow(r7, 1.0 / 2.4));
            g = Clamp255(255.0 * Math.Pow(g7, 1.0 / 2.4));
            b = Clamp255(255.0 * Math.Pow(b7, 1.0 / 2.4));
        }

        private static double UpdateGamutBlend(double t, double ch, double lum)
        {
            if (ch < 0.0)
            {
                double need = (lum - ch) > 1e-9 ? (-ch) / (lum - ch) : 1.0;
                if (need > t) t = need;
            }
            else if (ch > 1.0)
            {
                double need = (lum - ch) < -1e-9 ? (ch - 1.0) / (ch - lum) : 1.0;
                if (need > t) t = need;
            }
            return t;
        }

        private static byte[] BuildPqToSdrTable()
        {
            const double m1 = 2610.0 / 16384.0;
            const double m2 = 2523.0 / 4096.0 * 128.0;
            const double c1 = 3424.0 / 4096.0;
            const double c2 = 2413.0 / 4096.0 * 32.0;
            const double c3 = 2392.0 / 4096.0 * 32.0;
            var table = new byte[256];
            for (int code = 0; code < 256; code++)
            {
                double n = code / 255.0;
                double nm = Math.Pow(n, 1.0 / m2);
                double num = nm - c1;
                if (num < 0.0) num = 0.0;
                double den = c2 - c3 * nm;
                double nits = den > 1e-9 ? 10000.0 * Math.Pow(num / den, 1.0 / m1) : 0.0;
                double x = nits / HdrPeakNits;
                double outp = x <= 0.0 ? 0.0 : 255.0 * Math.Pow(x, 1.0 / 2.4);
                if (outp > 255.0) outp = 255.0;
                table[code] = (byte)(outp + 0.5);
            }
            return table;
        }

        private static int Clamp255(double x)
        {
            if (x <= 0.0) return 0;
            if (x >= 255.0) return 255;
            return (int)(x + 0.5);
        }

        private static double Clamp01(double x)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;
            return x;
        }

        public static void Shutdown()
        {
            if (_param != IntPtr.Zero) { try { Marshal.FreeHGlobal(_param); } catch { } }
            if (_bufY != IntPtr.Zero) { try { Marshal.FreeHGlobal(_bufY); } catch { } }
            if (_bufC != IntPtr.Zero) { try { Marshal.FreeHGlobal(_bufC); } catch { } }
            _param = _bufY = _bufC = IntPtr.Zero;
            _call = null;
        }
    }
}
