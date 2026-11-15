using System;

namespace TizenGlow
{
    /// <summary>
    /// The screen positions one frame samples: a few points along each edge, evenly spaced, a little way in
    /// from the edge. The firmware measures ScreenCapturePoints of them per round and then waits SleepMS, so
    /// a frame takes rounds x wait: fewer points, faster frames (Capture modes pick the count). With black
    /// bars (letterboxed films) the top and bottom rows move in by the bar height, so they sample the
    /// picture instead of the bar; the sides stay where they are.
    /// </summary>
    public sealed class PointSet
    {
        /// <summary>How far in from the edge the rows and columns sit, as a share of the screen.</summary>
        public const double EdgeInset = 0.05;

        public readonly CapturePoint[] Points;

        /// <summary>Indices into Points per edge, in canonical order: top left to right, right top to bottom, bottom right to left, left bottom to top.</summary>
        public readonly int[][] Edges;

        /// <summary>Points per edge: top, right, bottom, left.</summary>
        public readonly int[] PerEdge;

        /// <summary>Height of each black bar the top and bottom rows allow for, as a share of the screen (0 = none).</summary>
        public readonly double Bars;

        /// <summary>How far in from the edge the sample points sit, as a share of the screen.</summary>
        public readonly double Inset;

        public int Count { get { return Points.Length; } }

        public PointSet(int top, int right, int bottom, int left, double bars = 0, double inset = EdgeInset)
        {
            foreach (int n in new[] { top, right, bottom, left })
                if (n < 0 || n > 16) throw new ArgumentOutOfRangeException(nameof(top), "0-16 points per edge");
            if (top + right + bottom + left == 0) throw new ArgumentException("a point set needs at least one point");
            if (bars < 0 || bars > 0.4) throw new ArgumentOutOfRangeException(nameof(bars));
            if (inset <= 0 || inset >= 0.45) throw new ArgumentOutOfRangeException(nameof(inset));

            PerEdge = new[] { top, right, bottom, left };
            Bars = bars;
            Inset = inset;
            Points = new CapturePoint[top + right + bottom + left];
            Edges = new int[4][];
            double rowInset = bars + inset * (1 - 2 * bars);
            int k = 0;
            for (int edge = 0; edge < 4; edge++)
            {
                Edges[edge] = new int[PerEdge[edge]];
                for (int i = 0; i < PerEdge[edge]; i++, k++)
                {
                    double t = (i + 0.5) / PerEdge[edge];
                    Edges[edge][i] = k;
                    switch (edge)
                    {
                        case 0: Points[k] = new CapturePoint(t, rowInset); break;
                        case 1: Points[k] = new CapturePoint(1 - inset, t); break;
                        case 2: Points[k] = new CapturePoint(1 - t, 1 - rowInset); break;
                        default: Points[k] = new CapturePoint(inset, 1 - t); break;
                    }
                }
            }
        }

        /// <summary>The same counts with the rows moved in for bars of this height.</summary>
        public PointSet WithBars(double bars)
        {
            return bars == Bars ? this : new PointSet(PerEdge[0], PerEdge[1], PerEdge[2], PerEdge[3], bars, Inset);
        }

        private readonly PointSet[] _activeEdgeCache = new PointSet[16];

        /// <summary>Returns a PointSet keeping only edges with LEDs; avoids wasting capture rounds on 0-LED edges.</summary>
        public PointSet ForActiveEdges(bool top, bool right, bool bottom, bool left)
        {
            int t = top ? PerEdge[0] : 0;
            int r = right ? PerEdge[1] : 0;
            int b = bottom ? PerEdge[2] : 0;
            int l = left ? PerEdge[3] : 0;
            if (t + r + b + l == 0) return this;
            if (t == PerEdge[0] && r == PerEdge[1] && b == PerEdge[2] && l == PerEdge[3]) return this;
            int mask = (top ? 8 : 0) | (right ? 4 : 0) | (bottom ? 2 : 0) | (left ? 1 : 0);
            PointSet cached = _activeEdgeCache[mask];
            if (cached == null)
            {
                cached = new PointSet(t, r, b, l, Bars, Inset);
                _activeEdgeCache[mask] = cached;
            }
            return cached;
        }

        /// <summary>"2/2/2/2": points on the top, right, bottom and left edges.</summary>
        public string Describe()
        {
            return PerEdge[0] + "/" + PerEdge[1] + "/" + PerEdge[2] + "/" + PerEdge[3];
        }

        /// <summary>Position of a point along its edge in canonical direction, 0..1.</summary>
        public static double Along(int edge, CapturePoint p)
        {
            switch (edge)
            {
                case 0: return p.X;
                case 1: return p.Y;
                case 2: return 1 - p.X;
                default: return 1 - p.Y;
            }
        }

        /// <summary>Index of the point on `edge` nearest to position t (0..1) along it, or -1 when the edge has none.</summary>
        public int Nearest(int edge, double t)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            foreach (int i in Edges[edge])
            {
                double d = Math.Abs(Along(edge, Points[i]) - t);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        /// <summary>Frames per second the firmware allows for this many points: rounds of `perRound` points, `sleepMs` each. Null when it reports no wait.</summary>
        public double? CeilingFps(int perRound, int sleepMs)
        {
            if (perRound <= 0 || sleepMs <= 0) return null;
            int rounds = (Count + perRound - 1) / perRound;
            return Math.Round(1000.0 / (rounds * sleepMs), 1);
        }
    }
}
