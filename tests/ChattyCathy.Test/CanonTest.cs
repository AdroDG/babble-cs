using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ChattyCathy.Test
{
    [TestClass]
    public class CanonTest
    {
        private readonly Canon<int> _canon = new Canon<int>();
        private readonly List<int> _list = new List<int>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        [TestMethod]
        public void TestPushPull()
        {
            _canon.Subscribe(FillList);
            _canon.Push(1);
            _canon.Push(2);
            _canon.Push(3);
            foreach (var element in _canon.PullAll(_cancellationTokenSource.Token))
            {
                Assert.IsTrue(element > 0);
            }
            Assert.IsTrue(new List<int> { 1, 2, 3 }.SequenceEqual(_list));
        }

        private void FillList(int i)
        {
            _list.Add(i);
        }

        [TestMethod]
        public void TestUnsubscribe()
        {
            TestPushPull();
            _list.Clear();
            _canon.Unsubscribe(0);
            _canon.Push(1);
            Assert.IsTrue(_canon.PullAll(_cancellationTokenSource.Token).Any());
            Assert.IsFalse(_list.Any());
        }
    }
}
