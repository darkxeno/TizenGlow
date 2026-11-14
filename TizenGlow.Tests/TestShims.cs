namespace TizenGlow
{
    public struct Color
    {
        public int R;
        public int G;
        public int B;
        public override string ToString() { return R + "," + G + "," + B; }
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
        public double X;
        public double Y;
    }
}
