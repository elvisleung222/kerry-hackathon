using System;
using System.Diagnostics;

namespace Controller
{
    public class Path
    {
        public Stopwatch PathTime { get; }

        public Point3D Start { get; }
        public Point3D End { get; }

        public double TotalTimeMilliseconds { get; }

        public Path(Point3D start, Point3D end, double milliseconds)
        {
            this.PathTime = new Stopwatch();
            this.TotalTimeMilliseconds = milliseconds;

            this.Start = start;
            this.End = end;

            this.PathTime.Start();
        }
    }
}