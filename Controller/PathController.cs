using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace Controller
{
    public partial class PathController
    {
        private readonly double _acceleration = 0.05;
        private readonly double _accelerationMax = 0.5;

        private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(100);

        private double _velLinearMag = 0;
        private double _velAngleMag = 0;
        private Point3D _velTarget;
        private Point3D _velCommand;
        private Point3D _velActual;

        private Point3D _vector;

        private Path _path;

        private readonly Action<Point3D> _onVelocityUpdated;

        public PathController(Action<Point3D> onVelocityChanged)
        {
            this._onVelocityUpdated = onVelocityChanged;

            this._velTarget = new Point3D();
            this._velCommand = this._velTarget;
            this._velActual = null;

            Observable
                .Interval(this._interval)
                .Subscribe(this.SpinPath);
        }

        private void SpinPath(long iter)
        {
            if (this._path != null)
            {
                var time = this._path.PathTime.ElapsedMilliseconds;

                if (time >= this._path.T3.TotalMilliseconds ||
                    time < this._path.T0.TotalMilliseconds)
                {
                    //Path is finished
                    this._velTarget = new Point3D();
                }
                else if (time >= this._path.T2.TotalMilliseconds)
                {
                    //Path is in deceleration
                    this._velLinearMag -= this._acceleration;

                    this._velTarget.X -= this._acceleration * _vector.X;
                    this._velTarget.Y -= this._acceleration * _vector.Y;
                    this._velTarget.Z -= this._acceleration * _vector.Z;
                    this._velTarget.Yaw -= this._acceleration * _vector.Yaw;

                }
                else if (time >= this._path.T1.TotalMilliseconds)
                {
                    //Path is in constant velocity
                    //Nothing is required to update in constant velocity sector
                }
                else
                {
                    //Path is in acceleration
                    this._velLinearMag += this._acceleration;

                    this._velTarget.X += this._acceleration * _vector.X;
                    this._velTarget.Y += this._acceleration * _vector.Y;
                    this._velTarget.Z += this._acceleration * _vector.Z;
                    this._velTarget.Yaw += this._acceleration * _vector.Yaw;
                }
            }

            this._velCommand = new Point3D(this._velTarget.X, this._velTarget.Y, this._velTarget.Z, this._velTarget.Yaw);

            //Apply corrective maneuver
            this._velCommand.Yaw += this.CalculateYawCorrection(this._velActual, this._velTarget);
            var msStr = this._path?.PathTime.Elapsed.ToString(@"mm\:ss\.fff") ?? "null";
            
            Debug.WriteLine($"#{iter} [{msStr}] vX={this._velCommand.X}; vY={this._velCommand.Y}; vZ={this._velCommand.Z}; vYaw={this._velCommand.Yaw}");

            //Call externally provided velocity callback
            Task.Run(() => { this?._onVelocityUpdated(this._velCommand); });
        }

        private double CalculateYawCorrection(Point3D actual, Point3D target)
        {
            //Calculate corrective yaw
            double correctYaw;
            if (this._velActual == null)
            {
                //Use actual to assume no error and run as open loop
                correctYaw = this.CalculateYawCorrection(this._velTarget.Yaw, this._velTarget.Yaw);
            }
            else
            {
                correctYaw = this.CalculateYawCorrection(this._velActual.Yaw, this._velTarget.Yaw);
                this._velActual = null;
            }

            return correctYaw;
        }

        private double CalculateYawCorrection(double actual, double target)
        {
            const double kP = 0.10;
            const double ki = 0.00;
            const double kd = 0.00;

            var output = kP * (target - actual);

            return output;
        }

        /// <summary>
        /// Write detected yaw to controller
        /// </summary>
        /// <param name="yaw"></param>
        public void SetActualYaw(double yaw)
        {
            this._velActual = new Point3D
            {
                X = this._velTarget.X,
                Y = this._velTarget.Y,
                Z = this._velTarget.Z,
                Yaw = yaw
            };
        }

        public void StartPath(Point3D direction, double time)
        {
            var mag = Math.Pow(direction.X, 2) + Math.Pow(direction.Y, 2) + Math.Pow(direction.Z, 2);

            direction.X /= mag;
            direction.Y /= mag;
            direction.Z /= mag;
            this._vector = direction;
            this._path = new Path(time);
        }
    }
}
