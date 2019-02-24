namespace Controller
{
    public class Point3D
    {
        public double X;

        public double Y;

        public double Z;

        public double Yaw;

        public Point3D()
        {
            this.X = 0;
            this.Y = 0;
            this.Z = 0;
            this.Yaw = 0;
        }

        public Point3D(double x = 0, double y = 0, double z = 0, double yaw = 0)
        {
            this.X = x;
            this.Y = y;
            this.Z = z;
            this.Yaw = yaw;
        }
    }
}