using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace Controller
{
    public class PathController
    {
        private readonly double _acceleration = 0.05;
        private readonly double _accelerationMax = 0.5;

        private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(100);

        //private readonly Point3D _velTarget;
        private readonly Point3D _posActual;
        private readonly Point3D _posTarget;

        private Path _path;

        private readonly Action<Point3D> _onVelocityUpdated;

        public PathController(Action<Point3D> onVelocityChanged)
        {
            this._onVelocityUpdated = onVelocityChanged;

            this._posActual = new Point3D();
            this._posTarget = new Point3D();

            this._path = new Path(new Point3D(), new Point3D(), 0);

            Observable
                .Interval(this._interval)
                .Subscribe(this.SpinPath);
        }

        private void SpinPath(long iter)
        {
            if (this._path == null ||
                this._path.PathTime.ElapsedMilliseconds > this._path.TotalTimeMilliseconds)
            {
                //Assume current is at target when no path for open loop axes
                this._posActual.X = this._posTarget.X;
                this._posActual.Y = this._posTarget.Y;
                //TODO: Comment axes with closed loop
                this._posActual.Z = this._posTarget.Z;
                //Debug.WriteLine($"#Pos Path in end state");
            }
            else
            {
                var portionElapse = Math.Min(this._path.TotalTimeMilliseconds, this._path.PathTime.ElapsedMilliseconds) / this._path.TotalTimeMilliseconds;
                var portionRemain = 1 - portionElapse;

                this._posTarget.X = portionElapse * this._path.End.X + portionRemain * this._path.Start.X;
                this._posTarget.Y = portionElapse * this._path.End.Y + portionRemain * this._path.Start.Y;
                this._posTarget.Z = portionElapse * this._path.End.Z + portionRemain * this._path.Start.Z;
                this._posTarget.Yaw = (portionElapse * (this._path.End.Yaw + 180) + portionRemain * (this._path.Start.Yaw + 180)) % 360 - 180;

                //Debug.WriteLine($"#Pos [{portionElapse}:{portionRemain}] pT=({_posTarget.X},{_posTarget.Y},{_posTarget.Z},{_posTarget.Yaw}); pA=({_posActual.X},{_posActual.Y},{_posActual.Z},{_posActual.Yaw})");
            }

            const double commandScale = 8;
            const double commandAngleScale = 0.01;
            var vCommand = new Point3D
            {
                X = (this._posTarget.X - this._posActual.X) * commandScale,
                Y = (this._posTarget.Y - this._posActual.Y) * commandScale,
                Z = (this._posTarget.Z - this._posActual.Z) * commandScale,
                Yaw = ((this._posTarget.Yaw - this._posActual.Yaw) * commandAngleScale + 180) % 360 - 180
            };


            //Apply corrective maneuver
            vCommand.Z += this.CalculateZCorrection(this._posActual.Z, this._posTarget.Z);
            vCommand.Yaw += this.CalculateYawCorrection(this._posActual.Yaw, this._posTarget.Yaw);

            this._posActual.X = this._posTarget.X;
            this._posActual.Y = this._posTarget.Y;
            //TODO: Comment axes with closed loop
            this._posActual.Z = this._posTarget.Z;
            this._posActual.Yaw = this._posTarget.Yaw;

            Task.Run(() =>
            {
                var msStr = this._path?.PathTime.Elapsed.ToString(@"mm\:ss\.fff") ?? "null";
                Debug.WriteLine(
                        $"#{iter} [{msStr}] vX={vCommand.X}; vY={vCommand.Y}; vZ={vCommand.Z}; vYaw={vCommand.Yaw}");
            });

            //Call externally provided velocity callback
            Task.Run(() => { this._onVelocityUpdated?.Invoke(vCommand); });
        }

        private double CalculateYawCorrection(double actual, double target)
        {
            //Use actual to assume no error and run as open loop
            const double kP = 0.01;
            const double ki = 0.00;
            const double kd = 0.00;

            var output = (kP * ((target - actual + 180) % 360 - 180));

            return output;
        }

        private double CalculateZCorrection(double actual, double target)
        {
            //Use actual to assume no error and run as open loop
            const double kP = 0.00;
            const double ki = 0.00;
            const double kd = 0.00;

            var output = kP * (target - actual);

            return output;
        }



        public void SetActualYaw(double yaw)
        {
            this._posActual.Yaw = yaw;
        }

        public void SetActualZ(double z)
        {
            this._posActual.Z = z;
        }

        public Task StartPath(Point3D end, double time)
        {
            var start = new Point3D(_posTarget.X, _posTarget.Y, _posTarget.Z, _posTarget.Yaw);
            //var mag = Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2);

            //if (Math.Abs(mag) > 0.00001)
            //{
            //    end.X /= Math.Sqrt(mag);
            //    end.Y /= Math.Sqrt(mag);
            //    end.Z /= Math.Sqrt(mag);
            //}
            //else
            //{
            //    end.X = 0;
            //    end.Y = 0;
            //    end.Z = 0;
            //}

            this._path = new Path(start, end, time);

            Debug.WriteLine(
                $"#Start S=({start.X},{start.Y},{start.Z},{start.Yaw}); E=({end.X},{end.Y},{end.Z},{end.Yaw})");


            return Task.Run(() =>
            {
                while (this._path.PathTime.ElapsedMilliseconds < this._path.TotalTimeMilliseconds)
                {
                    Task.Delay(_interval);
                }
            });
        }

        public void StopPath()
        {
            this._path = null;
        }
    }
}
