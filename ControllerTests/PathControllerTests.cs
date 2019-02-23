using System.Diagnostics;
using System.Threading;
using Controller;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerTests
{
    [TestClass]
    public class PathControllerTests
    {
        private PathController controller;

        [TestMethod]
        public void Test()
        {
            controller = new PathController(this.GetVelocity);
            controller.StartPath(new Point3D(1, 0, 2, -1), 5000);

            Thread.Sleep(7000);
        }

        public void GetVelocity(Point3D vel)
        {
            controller.SetActualYaw(vel.Yaw);
            Debug.WriteLine($"In callback");
        }
    }
}
