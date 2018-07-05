using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ChattyCathy
{
    /// <summary>
    /// Canon class
    /// Manages a queue of elements and call some subscribed actions when an element is popped out of the list
    /// </summary>
    /// <typeparam name="TElement">The type of the element.</typeparam>
    public class Canon<TElement>
    {
        /// <summary>
        /// The list of items to enqueue
        /// </summary>
        private readonly ConcurrentQueue<TElement> _items = new ConcurrentQueue<TElement>();

        /// <summary>
        /// The list of actions to call when a pull occurs
        /// </summary>
        private readonly Dictionary<int, Action<TElement>> _actionsOnPull = new Dictionary<int, Action<TElement>>();

        /// <summary>
        /// Locks the access to the item queue
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// The next identifier to associate an action with
        /// </summary>
        private int _nextIdToAssociate;

        /// <summary>
        /// Pushes the specified elements.
        /// </summary>
        /// <param name="elements">The elements.</param>
        public void Push(IEnumerable<TElement> elements)
        {
            foreach (var element in elements)
            {
                Push(element);
            }
        }

        /// <summary>
        /// Pushes the specified element.
        /// </summary>
        /// <param name="element">The element.</param>
        public void Push(TElement element)
        {
            lock (_lock)
            {
                _items.Enqueue(element);
            }
        }

        /// <summary>
        /// Pulls the specified cancellation token.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public IEnumerable<TElement> PullAll(CancellationToken cancellationToken)
        {
            while (!_items.IsEmpty && !cancellationToken.IsCancellationRequested)
            {
                var item = PullOne(cancellationToken);
                if (item == null)
                    yield break;
                yield return item;
            }
        }

        /// <summary>
        /// Pulls one element and stops in case a cancellation token is sent.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public TElement PullOne(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var result = _items.TryDequeue(out var item);
                if (!result)
                    return default(TElement);
                foreach (var action in _actionsOnPull)
                {
                    action.Value?.Invoke(item);
                }
                return item;
            }
        }

        /// <summary>
        /// Subscribes the specified action to call when pulled.
        /// </summary>
        /// <param name="actionToCallWhenPulled">The action to call when pulled.</param>
        /// <returns>The id associated to the action being stored</returns>
        public int Subscribe(Action<TElement> actionToCallWhenPulled)
        {
            lock (_lock)
            {
                _actionsOnPull.Add(_nextIdToAssociate, actionToCallWhenPulled);
                return _nextIdToAssociate++;
            }
        }

        /// <summary>
        /// Un-subscribes the specified identifier.
        /// </summary>
        /// <param name="id">The identifier.</param>
        public void Unsubscribe(int id)
        {
            lock (_lock)
            {
                _actionsOnPull.Remove(id);
            }
        }
    }
}
