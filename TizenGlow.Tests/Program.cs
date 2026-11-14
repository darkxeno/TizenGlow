using System;
using System.Diagnostics;
using TizenGlow;
using TizenGlow.Wled;

internal static class Program
{
    private static int Main()
    {
        TestActiveEdgeFilteringSpeedup();
        TestCinemaMode();
        TestNanoleafPacketCorrectness();
        TestWledTopRightRemap();
        TestNanoleafSenderZeroAllocBenchmark();
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
        Assert(afterFps > beforeFps, "Expected higher ceiling FPS after active-edge filtering");
        Assert(CaptureMode.Detailed.TargetFps == 15, $"Expected original Detailed TargetFps == 15, got {CaptureMode.Detailed.TargetFps}");
        Assert(CaptureMode.Optimized.TargetFps == 20, $"Expected Optimized TargetFps == 20, got {CaptureMode.Optimized.TargetFps}");
        Assert(CaptureMode.Find("optimized") == CaptureMode.Optimized, "Expected CaptureMode.Find(\"optimized\") to return Optimized");
    }

    private static void TestCinemaMode()
    {
        CaptureMode cinema = CaptureMode.Cinema;
        Assert(cinema != null, "Expected CaptureMode.Cinema to be defined");
        Assert(Math.Abs(cinema.Points.Inset - 0.20) < 1e-6, $"Expected Cinema Inset 0.20, got {cinema.Points.Inset}");
        Assert(cinema.SmoothingMs == 1500, $"Expected Cinema SmoothingMs 1500, got {cinema.SmoothingMs}");
        Assert(CaptureMode.Find("cinema") == cinema, "Expected CaptureMode.Find(\"cinema\") to return Cinema");

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
        int[] topSlots = Array.Empty<int>();
        NanoleafSender.RemapTopRightCorner(src, src.Length, layout, dst, ref cached, ref topSlots);

        // Even the leftmost physical slot of WLED's Top edge should now be GREEN (0, 255, 0), not RED
        byte r = dst[topLeftPhys * 3];
        byte g = dst[topLeftPhys * 3 + 1];
        Console.WriteLine($"[Test] WLED Top remap (top-left slot): RGB=({r},{g},{dst[topLeftPhys * 3 + 2]})");
        Assert(r == 0 && g == 255, $"Expected remapped WLED Top to be green (0,255,0), got ({r},{g},0)");
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
}
