using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerTests
{
    [TestClass]
    public class PathControllerTests
    {
        private readonly PathController controller = new PathController(vel =>
        {

        });

        [TestMethod]
        public void Test()
        {
            this.controller.StartPath(new Point3D(0, 1, 1, 90), 5000);
            Thread.Sleep(5000);
        }
    }
}
