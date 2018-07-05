using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ChattyCathy.Test
{
    [TestClass]
    public class TickFactoryTest
    {
        [TestMethod]
        public async Task TestTickFactoryStartStop()
        {
            var tickCount = 0;
            var id = TickFactory.Instance.Subscribe((ts) =>
            {
                tickCount++;
            });
            TickFactory.Instance.Configure(400);
            TickFactory.Instance.Start();
            await Task.Delay(200);
            Assert.IsTrue(tickCount == 1);
            await Task.Delay(400);
            Assert.IsTrue(tickCount == 2);
            TickFactory.Instance.Stop();
            await Task.Delay(400);
            Assert.IsTrue(tickCount == 2);
        }
    }
}
