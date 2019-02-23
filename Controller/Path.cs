using System;
using System.Diagnostics;

namespace Controller
{
    public class Path
    {
        public Stopwatch PathTime { get; }

        public TimeSpan T0 { get; }
        public TimeSpan T1 { get; }
        public TimeSpan T2 { get; }
        public TimeSpan T3 { get; }

        public double TotalTimeMilliseconds { get; }

        public Path(double milliseconds)
        {
            this.PathTime = new Stopwatch();

            this.TotalTimeMilliseconds = this.T3.TotalMilliseconds;

            //TODO: Calculate intermediate points
            this.T3 = TimeSpan.FromMilliseconds(milliseconds);
            this.T0 = TimeSpan.FromMilliseconds(0);
            this.T1 = TimeSpan.FromMilliseconds(this.T3.TotalMilliseconds * 1/ 3);
            this.T2 = TimeSpan.FromMilliseconds(this.T3.TotalMilliseconds * 2 / 3);

            this.PathTime.Start();
        }
    }
}