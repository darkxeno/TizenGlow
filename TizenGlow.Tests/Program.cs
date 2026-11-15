using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using TizenGlow;
using TizenGlow.WebSocket;
using TizenGlow.Wled;

internal static class Program
{
    private static int Main()
    {
        TestActiveEdgeFilteringSpeedup();
        TestCinemaMode();
        TestNanoleafPacketCorrectness();
        TestWledTopRightRemap();
        TestNanoleafConfigAndDefaults();
        TestNanoleafMultiEdgePacket();
        TestRemapEdgeBottomAndLeftHalf();
        TestNanoleafSenderZeroAllocBenchmark();
        TestVideoPlaneSettingsValidation();
        TestVideoPlaneSpatialBlur();
        TestVideoPlaneHdrVsSdrSampling();
        TestVideoPlaneBlackBarsDetectionAndDisable();
        TestLetterboxStateMachine();
        TestWebUiServerResolutionAndHttpServing();
        TestTgvpUdpReceiverLifecycleAndMapping();
        Console.WriteLine("ALL TESTS PASSED.");
        return 0;
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new InvalidOperationException("ASSERTION FAILED: " + msg);
    }

    private static void TestActiveEdgeFilteringSpeedup()
    {
        PointSet before = CaptureMode.Detailed.Points;
        PointSet after = before.ForActiveEdges(top: true, right: true, bottom: false, left: false);

        const int perRound = 2;
        const int sleepMs = 20;

        int beforeRounds = (before.Count + perRound - 1) / perRound;
        int afterRounds = (after.Count + perRound - 1) / perRound;
        double beforeMs = beforeRounds * sleepMs;
        double afterMs = afterRounds * sleepMs;
        double beforeFps = before.CeilingFps(perRound, sleepMs).Value;
        double afterFps = after.CeilingFps(perRound, sleepMs).Value;
        double speedupPct = (afterFps - beforeFps) / beforeFps * 100.0;

        Console.WriteLine($"[Perf] Detailed before: {before.Count} pts ({before.Describe()}), {beforeRounds} rounds = {beforeMs} ms/frame -> {beforeFps} FPS");
        Console.WriteLine($"[Perf] Detailed after:  {after.Count} pts ({after.Describe()}), {afterRounds} rounds = {afterMs} ms/frame -> {afterFps} FPS (+{speedupPct:F1}%)");

        Assert(before.Count == 14, $"Expected 14 points before, got {before.Count}");
        Assert(after.Count == 7, $"Expected 7 points after, got {after.Count}");
        Assert(after.Describe() == "4/3/0/0", $"Expected 4/3/0/0, got {after.Describe()}");
        Assert(ReferenceEquals(after, before.ForActiveEdges(top: true, right: true, bottom: false, left: false)), "Expected ForActiveEdges to return cached PointSet instance");
        Assert(afterFps > beforeFps, "Expected higher ceiling FPS after active-edge filtering");
        Assert(CaptureMode.Detailed.TargetFps == 30, $"Expected Detailed TargetFps == 30, got {CaptureMode.Detailed.TargetFps}");
        Assert(CaptureMode.BalancedPro.TargetFps == 30, $"Expected BalancedPro TargetFps == 30, got {CaptureMode.BalancedPro.TargetFps}");
        Assert(CaptureMode.Optimized.TargetFps == 20, $"Expected Optimized TargetFps == 20, got {CaptureMode.Optimized.TargetFps}");
        Assert(CaptureMode.Find("optimized") == CaptureMode.Optimized, "Expected CaptureMode.Find(\"optimized\") to return Optimized");
        Assert(CaptureMode.Find("balancedpro") == CaptureMode.BalancedPro, "Expected CaptureMode.Find(\"balancedpro\") to return BalancedPro");
    }

    private static void TestCinemaMode()
    {
        CaptureMode cinema = CaptureMode.Cinema;
        Assert(cinema != null, "Expected CaptureMode.Cinema to be defined");
        Assert(cinema.TargetFps == 30, $"Expected Cinema TargetFps 30, got {cinema.TargetFps}");
        Assert(Math.Abs(cinema.Points.Inset - 0.20) < 1e-6, $"Expected Cinema Inset 0.20, got {cinema.Points.Inset}");
        Assert(cinema.SmoothingMs == 1500, $"Expected Cinema SmoothingMs 1500, got {cinema.SmoothingMs}");
        Assert(CaptureMode.Find("cinema") == cinema, "Expected CaptureMode.Find(\"cinema\") to return Cinema");

        CaptureMode balancedPro = CaptureMode.BalancedPro;
        Assert(balancedPro != null, "Expected CaptureMode.BalancedPro to be defined");
        Assert(Math.Abs(balancedPro.Points.Inset - 0.10) < 1e-6, $"Expected BalancedPro Inset 0.10, got {balancedPro.Points.Inset}");
        Assert(balancedPro.SmoothingMs == 350, $"Expected BalancedPro SmoothingMs 350, got {balancedPro.SmoothingMs}");

        CaptureMode cinemaPro = CaptureMode.CinemaPro;
        Assert(cinemaPro != null, "Expected CaptureMode.CinemaPro to be defined");
        Assert(cinemaPro.TargetFps == 30, $"Expected CinemaPro TargetFps 30, got {cinemaPro.TargetFps}");
        Assert(cinemaPro.Points.Describe() == "1/1/1/1", $"Expected CinemaPro 1/1/1/1 points, got {cinemaPro.Points.Describe()}");
        Assert( cinemaPro.EffectivePoints.Describe() == "1/1/1/1", $"Expected CinemaPro EffectivePoints 1/1/1/1, got {cinemaPro.EffectivePoints.Describe()}");
        Assert(CaptureMode.Find("pro_cinema") == cinemaPro && CaptureMode.Find("cinemapro") == cinemaPro, "Expected CaptureMode.Find(\"pro_cinema\"/\"cinemapro\") to return CinemaPro");

        PointSet filtered = cinema.Points.ForActiveEdges(top: true, right: true, bottom: false, left: false);
        Assert(filtered.Count == 4, $"Expected 4 active points for Cinema, got {filtered.Count}");
        Assert(filtered.Describe() == "2/2/0/0", $"Expected 2/2/0/0, got {filtered.Describe()}");
        Assert(Math.Abs(filtered.Inset - 0.20) < 1e-6, $"Expected filtered Inset 0.20, got {filtered.Inset}");

        double ceilingFps = filtered.CeilingFps(2, 20).Value;
        Console.WriteLine($"[Cinema] {filtered.Count} pts ({filtered.Describe()}), inset={filtered.Inset:F2}, smoothing={cinema.SmoothingMs}ms -> {ceilingFps:F1} FPS ceiling");
        Assert(Math.Abs(ceilingFps - 25.0) < 1e-6, $"Expected 25.0 FPS ceiling, got {ceilingFps}");
    }

    private static void TestNanoleafPacketCorrectness()
    {
        // Strip starts at bottom-right, goes up Right (0..119) then left across Top (120..239):
        // startCorner=BottomRight, clockwise=false (counter-clockwise).
        var layout = new LedLayout(120, 120, 0, 0, StartCorner.BottomRight, false, CaptureMode.Detailed.Points);
        Assert(layout.Count == 240, $"Expected 240 LEDs, got {layout.Count}");

        byte[] rgb = new byte[240 * 3];
        for (int p = 0; p < layout.Count; p++)
        {
            int slot = layout.Slot(p);
            if (slot < layout.Top)
            {
                // Canonical top edge: slot 0 (left) -> R=0, slot 119 (right) -> R=238
                rgb[p * 3] = (byte)(slot * 2);
                rgb[p * 3 + 1] = 20;
                rgb[p * 3 + 2] = 40;
            }
        }

        using var sender = new NanoleafSender();
        byte[] pkt = sender.BuildPacket(rgb, rgb.Length, layout);
        Assert(pkt != null, "Packet should not be null");
        Assert(pkt.Length == 2 + 18 * 8, $"Expected 146 bytes, got {pkt.Length}");
        Assert(pkt[0] == 0 && pkt[1] == 18, $"Expected header nPanels=18, got {pkt[0]},{pkt[1]}");

        // Panel 0 has norm_x=0.0 (left), Panel 17 has norm_x=1.0 (right)
        byte leftR = pkt[2 + 2];
        byte rightR = pkt[2 + 17 * 8 + 2];
        Console.WriteLine($"[Test] Nanoleaf packet len={pkt.Length} bytes, leftPanel R={leftR}, rightPanel R={rightR}");
        Assert(rightR > leftR, $"Expected right panel R ({rightR}) > left panel R ({leftR})");
    }

    private static void TestWledTopRightRemap()
    {
        var layout = new LedLayout(120, 120, 0, 0, StartCorner.BottomRight, false, CaptureMode.Detailed.Points);
        byte[] src = new byte[240 * 3];
        byte[] dst = new byte[240 * 3];

        // Paint left half of canonical Top (slots 0..59) RED, right half (slots 60..119) GREEN
        int topLeftPhys = -1;
        for (int p = 0; p < layout.Count; p++)
        {
            int slot = layout.Slot(p);
            if (slot < layout.Top)
            {
                if (slot == 0) topLeftPhys = p;
                if (slot < 60) { src[p * 3] = 255; src[p * 3 + 1] = 0; }
                else { src[p * 3] = 0; src[p * 3 + 1] = 255; }
            }
        }

        LedLayout cached = null;
        int[] slotMap = Array.Empty<int>();
        NanoleafSender.RemapEdge(src, src.Length, layout, 0, dst, ref cached, ref slotMap, 0.625, 1.0);

        // Even the leftmost physical slot of WLED's Top edge should now be GREEN (0, 255, 0), not RED
        byte r = dst[topLeftPhys * 3];
        byte g = dst[topLeftPhys * 3 + 1];
        Console.WriteLine($"[Test] WLED Top remap (top-left slot): RGB=({r},{g},{dst[topLeftPhys * 3 + 2]})");
        Assert(r == 0 && g == 255, $"Expected remapped WLED Top to be green (0,255,0), got ({r},{g},0)");

        // Full range (0.0..1.0) must be a plain copy (no remap)
        NanoleafSender.RemapEdge(src, src.Length, layout, 0, dst, ref cached, ref slotMap, 0.0, 1.0);
        Assert(dst[topLeftPhys * 3] == 255 && dst[topLeftPhys * 3 + 1] == 0, "Expected full-range RemapEdge to keep original RED at top-left");
    }

    private static void TestNanoleafConfigAndDefaults()
    {
        using var sender = new NanoleafSender();
        Assert(!sender.Enabled, "NanoleafSender should be disabled by default");
        Assert(sender.AutoDiscover, "NanoleafSender AutoDiscover should be enabled by default");
        Assert(sender.Ip == "", "NanoleafSender IP should be empty by default");
        Assert(sender.Token == "", "NanoleafSender token should be empty by default");
        Assert(sender.Edge == 0, "NanoleafSender edge should default to Top (0)");
        Assert(sender.StartShare == 0.0 && sender.EndShare == 1.0, "NanoleafSender range should default to full (0..1)");

        Assert(sender.TrySetEnabled("on") && sender.Enabled, "Should enable Nanoleaf");
        Assert(sender.TrySetEnabled("off") && !sender.Enabled, "Should disable Nanoleaf");
        Assert(!sender.TrySetEnabled("maybe"), "Should reject invalid enabled value");

        Assert(sender.TrySetAutoDiscover("off") && !sender.AutoDiscover, "Should disable AutoDiscover");
        Assert(sender.TrySetAutoDiscover("on") && sender.AutoDiscover, "Should enable AutoDiscover");
        Assert(!sender.TrySetAutoDiscover("auto"), "Should reject invalid AutoDiscover value");

        Assert(sender.TrySetIp("192.168.10.168") && sender.Ip == "192.168.10.168", "Should accept valid IPv4");
        Assert(!sender.TrySetIp("not-an-ip"), "Should reject invalid IP");
        Assert(sender.TrySetIp("") && sender.Ip == "", "Should allow clearing IP");

        Assert(sender.TrySetToken("  my-token  ") && sender.Token == "my-token", "Should trim and store token");

        Assert(sender.TrySetEdge("top") && sender.Edge == 0, "Should set edge top");
        Assert(sender.TrySetEdge("right") && sender.Edge == 1, "Should set edge right");
        Assert(sender.TrySetEdge("bottom") && sender.Edge == 2, "Should set edge bottom");
        Assert(sender.TrySetEdge("left") && sender.Edge == 3, "Should set edge left");
        Assert(!sender.TrySetEdge("diagonal"), "Should reject invalid edge");

        Assert(sender.TrySetRange("center70") && sender.StartShare > 0.08 && sender.EndShare < 0.79, "Should parse center70");
        Assert(sender.TrySetRange("left50") && sender.StartShare == 0.0 && sender.EndShare == 0.5, "Should parse left50");
        Assert(sender.TrySetRange("right50") && sender.StartShare == 0.5 && sender.EndShare == 1.0, "Should parse right50");
        Assert(sender.TrySetRange("full") && sender.StartShare == 0.0 && sender.EndShare == 1.0, "Should parse full");
        Assert(!sender.TrySetRange("bogus"), "Should reject invalid range");
        Console.WriteLine("[Test] NanoleafSender defaults & config validation OK");
    }

    private static void TestNanoleafMultiEdgePacket()
    {
        var layout = new LedLayout(20, 20, 20, 20, StartCorner.TopLeft, true, CaptureMode.Detailed.Points);
        byte[] rgb = new byte[layout.Count * 3];
        for (int p = 0; p < layout.Count; p++)
        {
            int slot = layout.Slot(p);
            if (slot < 20) { rgb[p * 3] = 255; } // Top: Red
            else if (slot < 40) { rgb[p * 3 + 1] = 255; } // Right: Green
            else if (slot < 60) { rgb[p * 3 + 2] = 255; } // Bottom: Blue
            else { rgb[p * 3] = 255; rgb[p * 3 + 1] = 255; } // Left: Yellow
        }

        using var sender = new NanoleafSender();
        Assert(sender.TrySetEdge("right"), "Set right edge");
        byte[] pktRight = sender.BuildPacket(rgb, rgb.Length, layout);
        Assert(pktRight[2 + 2] == 0 && pktRight[2 + 3] == 255 && pktRight[2 + 4] == 0, "Right edge packet should be Green");

        Assert(sender.TrySetEdge("bottom"), "Set bottom edge");
        byte[] pktBottom = sender.BuildPacket(rgb, rgb.Length, layout);
        Assert(pktBottom[2 + 2] == 0 && pktBottom[2 + 3] == 0 && pktBottom[2 + 4] == 255, "Bottom edge packet should be Blue");

        Assert(sender.TrySetEdge("left"), "Set left edge");
        byte[] pktLeft = sender.BuildPacket(rgb, rgb.Length, layout);
        Assert(pktLeft[2 + 2] == 255 && pktLeft[2 + 3] == 255 && pktLeft[2 + 4] == 0, "Left edge packet should be Yellow");
        Console.WriteLine("[Test] NanoleafSender multi-edge BuildPacket OK");
    }

    private static void TestRemapEdgeBottomAndLeftHalf()
    {
        var layout = new LedLayout(10, 10, 10, 10, StartCorner.TopLeft, true, CaptureMode.Detailed.Points);
        byte[] src = new byte[layout.Count * 3];
        byte[] dst = new byte[layout.Count * 3];
        // Top=Red, Bottom first half=Green, Bottom second half=Blue
        for (int p = 0; p < layout.Count; p++)
        {
            int slot = layout.Slot(p);
            if (slot < 10) src[p * 3] = 255;
            else if (slot >= 20 && slot < 25) src[p * 3 + 1] = 255;
            else if (slot >= 25 && slot < 30) src[p * 3 + 2] = 255;
        }

        LedLayout cached = null;
        int[] slotMap = Array.Empty<int>();
        NanoleafSender.RemapEdge(src, src.Length, layout, 2, dst, ref cached, ref slotMap, 0.0, 0.5);

        // Top untouched (Red)
        Assert(dst[0] == 255 && dst[1] == 0 && dst[2] == 0, "Top edge should stay Red");
        // Last slot of Bottom (slot 29 -> physical 29 in TopLeft CW) should now be Green from first half
        Assert(dst[29 * 3] == 0 && dst[29 * 3 + 1] == 255 && dst[29 * 3 + 2] == 0, "Bottom last slot should be Green after left50 remap");
        Console.WriteLine("[Test] RemapEdge bottom left50 OK");
    }

    private static void TestNanoleafSenderZeroAllocBenchmark()
    {
        var layout = new LedLayout(120, 120, 0, 0, StartCorner.BottomRight, false, CaptureMode.Detailed.Points);
        byte[] rgb = new byte[240 * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i & 0xFF);

        using var sender = new NanoleafSender();
        // Warmup layout cache
        sender.BuildPacket(rgb, rgb.Length, layout);

        const int iterations = 100_000;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            rgb[0] = (byte)i;
            sender.BuildPacket(rgb, rgb.Length, layout);
        }
        double elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long allocated = allocAfter - allocBefore;

        double nsPerFrame = elapsedMs * 1_000_000.0 / iterations;
        double fps = iterations / (elapsedMs / 1000.0);
        Console.WriteLine($"[Perf] NanoleafSender.BuildPacket x{iterations:N0}: {elapsedMs:F2} ms ({nsPerFrame:F0} ns/frame, {fps:N0} frames/s), heap alloc={allocated} bytes");

        Assert(allocated == 0, $"Expected 0 bytes allocated in hot loop, got {allocated}");
    }

    private static void TestVideoPlaneSettingsValidation()
    {
        Assert(VideoPlaneCapturer.HdrSetting == "on", "Default vpHdr should be on");
        Assert(VideoPlaneCapturer.BlackBarsSetting == "on", "Default vpBlackBars should be on");
        Assert(VideoPlaneCapturer.SpatialBlurSetting == 25, "Default vpSpatialBlur should be 25");

        Assert(!VideoPlaneCapturer.TrySetHdr("invalid"), "Invalid vpHdr should fail");
        Assert(VideoPlaneCapturer.TrySetHdr("off") && VideoPlaneCapturer.HdrSetting == "off", "vpHdr off should succeed");
        Assert(VideoPlaneCapturer.TrySetHdr("on") && VideoPlaneCapturer.HdrSetting == "on", "vpHdr on should succeed");

        Assert(!VideoPlaneCapturer.TrySetBlackBars("auto"), "Invalid vpBlackBars should fail");
        Assert(VideoPlaneCapturer.TrySetBlackBars("off") && VideoPlaneCapturer.BlackBarsSetting == "off", "vpBlackBars off should succeed");
        Assert(VideoPlaneCapturer.TrySetBlackBars("on") && VideoPlaneCapturer.BlackBarsSetting == "on", "vpBlackBars on should succeed");

        Assert(!VideoPlaneCapturer.TrySetSpatialBlur("-1"), "Negative spatial blur should fail");
        Assert(!VideoPlaneCapturer.TrySetSpatialBlur("51"), "Spatial blur > 50 should fail");
        Assert(!VideoPlaneCapturer.TrySetSpatialBlur("abc"), "Non-numeric spatial blur should fail");
        Assert(VideoPlaneCapturer.TrySetSpatialBlur("0") && VideoPlaneCapturer.SpatialBlurSetting == 0, "Spatial blur 0 (off) should succeed");
        Assert(VideoPlaneCapturer.TrySetSpatialBlur("25") && VideoPlaneCapturer.SpatialBlurSetting == 25, "Spatial blur 25 should succeed");
    }

    private static void TestVideoPlaneSpatialBlur()
    {
        var ring = new[]
        {
            new Color { R = 200, G = 0, B = 0 },
            new Color { R = 0, G = 0, B = 0 },
            new Color { R = 0, G = 0, B = 0 },
            new Color { R = 0, G = 0, B = 0 }
        };

        // 0% leaves colors untouched
        Assert(VideoPlaneCapturer.TrySetSpatialBlur("0"), "Set blur 0");
        var untouched = (Color[])ring.Clone();
        VideoPlaneCapturer.ApplySpatialBlur(untouched);
        Assert(untouched[0].R == 200 && untouched[1].R == 0, "0% blur must not alter colors");

        // 50% blends 50% self + 25% left + 25% right (wrapping around ring)
        Assert(VideoPlaneCapturer.TrySetSpatialBlur("50"), "Set blur 50");
        var blurred = (Color[])ring.Clone();
        VideoPlaneCapturer.ApplySpatialBlur(blurred);
        Assert(blurred[0].R == 100, $"Expected center 100, got {blurred[0].R}");
        Assert(blurred[1].R == 50, $"Expected right neighbor 50, got {blurred[1].R}");
        Assert(blurred[2].R == 0, $"Expected opposite 0, got {blurred[2].R}");
        Assert(blurred[3].R == 50, $"Expected wrap-around left neighbor 50, got {blurred[3].R}");

        Assert(VideoPlaneCapturer.TrySetSpatialBlur("25"), "Restore default blur 25");
    }

    private static void TestVideoPlaneHdrVsSdrSampling()
    {
        const int w = 960, h = 540;
        var y = new byte[w * h];
        var c = new byte[w * h / 2];

        // Fill neutral grey (Y=128, U=128, V=128)
        Array.Fill(y, (byte)128);
        Array.Fill(c, (byte)128);

        Assert(VideoPlaneCapturer.TrySetBlackBars("off"), "Disable black bars for color test");
        var pt = new[] { new CapturePoint(0.5, 0.5) };

        Assert(VideoPlaneCapturer.TrySetHdr("on"), "Enable HDR");
        Color[] hdrNeutral = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pt);
        Assert(hdrNeutral[0].R == hdrNeutral[0].G && hdrNeutral[0].G == hdrNeutral[0].B,
            $"HDR neutral should have R==G==B, got {hdrNeutral[0]}");

        Assert(VideoPlaneCapturer.TrySetHdr("off"), "Enable SDR");
        Color[] sdrNeutral = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pt);
        Assert(sdrNeutral[0].R == sdrNeutral[0].G && sdrNeutral[0].G == sdrNeutral[0].B,
            $"SDR neutral should have R==G==B, got {sdrNeutral[0]}");

        // Fill saturated red (Y=120, U=90, V=220)
        Array.Fill(y, (byte)120);
        for (int i = 0; i + 1 < c.Length; i += 2) { c[i] = 90; c[i + 1] = 220; }

        Color[] sdrRed = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pt);
        Assert(sdrRed[0].R > sdrRed[0].G && sdrRed[0].R > sdrRed[0].B, $"SDR red invalid: {sdrRed[0]}");

        Assert(VideoPlaneCapturer.TrySetHdr("on"), "Restore HDR");
        Color[] hdrRed = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pt);
        Assert(hdrRed[0].R > hdrRed[0].G && hdrRed[0].R > hdrRed[0].B, $"HDR red invalid: {hdrRed[0]}");

        Assert(VideoPlaneCapturer.TrySetBlackBars("on"), "Restore black bars");
    }

    private static void TestVideoPlaneBlackBarsDetectionAndDisable()
    {
        const int w = 960, h = 540;
        var y = new byte[w * h];
        var c = new byte[w * h / 2];

        // 80px black letterbox bars top & bottom (Y=16, U=128, V=128), bright green active middle (Y=180, U=60, V=60)
        for (int row = 0; row < h; row++)
        {
            byte luma = (row < 80 || row >= h - 80) ? (byte)16 : (byte)180;
            Array.Fill(y, luma, row * w, w);
        }
        for (int crow = 0; crow < h / 2; crow++)
        {
            int yRow = crow * 2;
            byte chroma = (yRow < 80 || yRow >= h - 80) ? (byte)128 : (byte)60;
            Array.Fill(c, chroma, crow * w, w);
        }

        var pts = new[] { new CapturePoint(0.5, 0.05), new CapturePoint(0.5, 0.95) };

        Assert(VideoPlaneCapturer.TrySetBlackBars("off"), "Disable black bars");
        Color[] raw = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pts);
        Assert(raw[0].G < 20 && raw[1].G < 20, $"With black bars off, expected dark edge, got {raw[0]} / {raw[1]}");

        Assert(VideoPlaneCapturer.TrySetBlackBars("on"), "Enable black bars");
        Color[] cropped = null;
        for (int f = 0; f < 10; f++)
        {
            cropped = VideoPlaneCapturer.SamplePointsFromPlanes(y, c, w, h, w, w, pts);
        }
        Assert(cropped[0].G > 80 && cropped[1].G > 80,
            $"With black bars on, expected edge remapped into green picture, got {cropped[0]} / {cropped[1]}");
    }

    private static void TestLetterboxStateMachine()
    {
        Assert(Letterbox.TrySetMode("off") && Letterbox.Mode == "off" && !Letterbox.Inset && Letterbox.State == "off", "Letterbox off failed");
        Assert(Letterbox.TrySetMode("on") && Letterbox.Mode == "on" && Letterbox.Inset && Letterbox.State == "always", "Letterbox on failed");
        Assert(Letterbox.TrySetMode("auto") && Letterbox.Mode == "auto" && !Letterbox.Inset && Letterbox.State == "watching", "Letterbox auto failed");
        Assert(!Letterbox.TrySetMode("bogus"), "Invalid letterbox mode should be rejected");
    }

    private static void TestWebUiServerResolutionAndHttpServing()
    {
        string res, ct;
        Assert(WebUiServer.TryResolveResource("/", out res, out ct) && res == "TizenGlowUI.index.html" && ct.StartsWith("text/html"), "Resolve / failed");
        Assert(WebUiServer.TryResolveResource("/index.html?v=1", out res, out ct) && res == "TizenGlowUI.index.html", "Resolve /index.html?v=1 failed");
        Assert(WebUiServer.TryResolveResource("/js/wsClient.js", out res, out ct) && res == "TizenGlowUI.js.wsClient.js" && ct.Contains("javascript"), "Resolve wsClient.js failed");
        Assert(WebUiServer.TryResolveResource("/main.css", out res, out ct) && res == "TizenGlowUI.main.css" && ct.StartsWith("text/css"), "Resolve main.css failed");
        Assert(!WebUiServer.TryResolveResource("/../secret", out res, out ct), "Unexpected path should return false");

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var cts = new CancellationTokenSource();
        var server = new WebUiServer($"http://127.0.0.1:{port}/", typeof(Program).Assembly);
        var serverTask = server.StartAsync(cts.Token);

        try
        {
            using var http = new HttpClient();
            var htmlResp = http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            Assert(htmlResp.StatusCode == HttpStatusCode.OK, $"Expected 200 for /, got {htmlResp.StatusCode}");
            string html = htmlResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert(html.Contains("TizenGlow") && html.Contains("nanoleafEnabled"), "index.html should contain TizenGlow UI");

            var jsResp = http.GetAsync($"http://127.0.0.1:{port}/js/wsClient.js").GetAwaiter().GetResult();
            Assert(jsResp.StatusCode == HttpStatusCode.OK, $"Expected 200 for wsClient.js, got {jsResp.StatusCode}");

            AppLog.Info("Test", "Hello from TizenGlow");
            var logsResp = http.GetAsync($"http://127.0.0.1:{port}/logs").GetAwaiter().GetResult();
            Assert(logsResp.StatusCode == HttpStatusCode.OK, $"Expected 200 for /logs, got {logsResp.StatusCode}");
            string logsText = logsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert(logsText.Contains("Hello from TizenGlow"), "Expected /logs to include AppLog entry");

            var missResp = http.GetAsync($"http://127.0.0.1:{port}/missing.txt").GetAwaiter().GetResult();
            Assert(missResp.StatusCode == HttpStatusCode.NotFound, $"Expected 404 for missing.txt, got {missResp.StatusCode}");
            Console.WriteLine($"[Test] WebUiServer HTTP serving + /logs on port {port} OK");
        }
        finally
        {
            cts.Cancel();
            server.Stop();
            try { serverTask.Wait(1000); } catch { }
        }
    }

    private static void TestTgvpUdpReceiverLifecycleAndMapping()
    {
        TgvpUdpReceiver.ConfigureForTests(0, staleTimeoutMs: 120);

        var pts = new[]
        {
            new CapturePoint(0.10, 0.03), // Top edge
            new CapturePoint(0.97, 0.50), // Right edge
            new CapturePoint(0.50, 0.97), // Bottom edge
            new CapturePoint(0.03, 0.50)  // Left edge
        };

        Color[] colors;
        Assert(!TgvpUdpReceiver.TryGetColors(pts, out colors), "Expected false before any TGVP packet");
        int boundPort = TgvpUdpReceiver.BoundPort;
        Assert(boundPort > 0, $"Expected ephemeral bound port > 0, got {boundPort}");
        Assert(TgvpUdpReceiver.Status == $"waiting (TGVP UDP :{boundPort})",
            $"Expected waiting status, got '{TgvpUdpReceiver.Status}'");

        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var target = new IPEndPoint(IPAddress.Loopback, boundPort);

        // 1. Malformed packets should be ignored
        sender.SendTo(new byte[] { (byte)'B', (byte)'A', (byte)'D', (byte)'!', 1, 1, 10, 20, 30 }, target);
        sender.SendTo(new byte[] { (byte)'T', (byte)'G', (byte)'V', (byte)'P', 2, 1, 10, 20, 30 }, target);
        Thread.Sleep(15);
        Assert(!TgvpUdpReceiver.TryGetColors(pts, out colors), "Expected malformed packets to be ignored");

        // 2. Send a valid 24-point TGVP v1 packet (7 top=Red, 5 right=Green, 7 bottom=Blue, 5 left=Yellow)
        byte[] pkt = new byte[6 + 24 * 3];
        pkt[0] = (byte)'T'; pkt[1] = (byte)'G'; pkt[2] = (byte)'V'; pkt[3] = (byte)'P';
        pkt[4] = 1;
        pkt[5] = 24;
        for (int i = 0; i < 24; i++)
        {
            int o = 6 + i * 3;
            if (i < 7) { pkt[o] = 250; pkt[o + 1] = 10; pkt[o + 2] = 20; }         // Top: Red
            else if (i < 12) { pkt[o] = 10; pkt[o + 1] = 240; pkt[o + 2] = 20; }   // Right: Green
            else if (i < 19) { pkt[o] = 20; pkt[o + 1] = 30; pkt[o + 2] = 245; }   // Bottom: Blue
            else { pkt[o] = 230; pkt[o + 1] = 220; pkt[o + 2] = 15; }              // Left: Yellow
        }
        sender.SendTo(pkt, target);
        Thread.Sleep(15);

        Assert(TgvpUdpReceiver.IsActive, "Expected TgvpUdpReceiver.IsActive to be true after valid TGVP packet");
        Assert(CaptureMode.Detailed.EffectivePoints.Describe() == "7/5/7/5", $"Expected Detailed EffectivePoints 7/5/7/5 when UDP active, got {CaptureMode.Detailed.EffectivePoints.Describe()}");
        Assert(CaptureMode.BalancedPro.EffectivePoints.Describe() == "7/5/7/5", $"Expected BalancedPro EffectivePoints 7/5/7/5 when UDP active, got {CaptureMode.BalancedPro.EffectivePoints.Describe()}");
        Assert(CaptureMode.Cinema.EffectivePoints.Describe() == "7/5/7/5", $"Expected Cinema EffectivePoints 7/5/7/5 when UDP active, got {CaptureMode.Cinema.EffectivePoints.Describe()}");
        PointSet active3Edge = CaptureMode.Cinema.EffectivePoints.ForActiveEdges(true, true, true, false);
        Assert(active3Edge.Describe() == "7/5/7/0" && active3Edge.Count == 19, $"Expected 19 active zones (7/5/7/0) on 3-edge layout, got {active3Edge.Count} ({active3Edge.Describe()})");

        Assert(TgvpUdpReceiver.TryGetColors(pts, out colors) && colors != null && colors.Length == 4,
            "Expected TryGetColors to succeed after valid TGVP packet");
        Assert(TgvpUdpReceiver.Status == $"active (TGVP UDP :{boundPort}, 24 pts)",
            $"Expected active status, got '{TgvpUdpReceiver.Status}'");

        Assert(colors[0].R == 250 && colors[0].G == 10 && colors[0].B == 20, $"Top color mismatch: {colors[0]}");
        Assert(colors[1].R == 10 && colors[1].G == 240 && colors[1].B == 20, $"Right color mismatch: {colors[1]}");
        Assert(colors[2].R == 20 && colors[2].G == 30 && colors[2].B == 245, $"Bottom color mismatch: {colors[2]}");
        Assert(colors[3].R == 230 && colors[3].G == 220 && colors[3].B == 15, $"Left color mismatch: {colors[3]}");

        // 3. Stale timeout transitions to fallback status
        Thread.Sleep(160);
        Assert(!TgvpUdpReceiver.IsActive, "Expected TgvpUdpReceiver.IsActive to be false after stale timeout");
        Assert(CaptureMode.Cinema.EffectivePoints.Describe() == "2/2/2/2", $"Expected Cinema EffectivePoints fallback 2/2/2/2 after timeout, got {CaptureMode.Cinema.EffectivePoints.Describe()}");
        Assert(!TgvpUdpReceiver.TryGetColors(pts, out colors), "Expected false after stale timeout");
        Assert(TgvpUdpReceiver.Status == $"stale (TGVP UDP :{boundPort} -> fallback)",
            $"Expected stale status, got '{TgvpUdpReceiver.Status}'");

        TgvpUdpReceiver.ConfigureForTests(TgvpUdpReceiver.DefaultListenPort);
        Console.WriteLine($"[Test] TgvpUdpReceiver lifecycle + 24-point mapping on port {boundPort} OK");
    }
}

