using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ChattyCathy
{
    /// <summary>
    /// RxCanon class
    /// Manages a queue of elements and call some subscribed observers when an element is popped out of the list.
    /// This class is making use of Reactive extensions
    /// </summary>
    /// <typeparam name="TElement">The type of the element.</typeparam>
    public class RxCanon<TElement> : IDisposable
    {
        /// <summary>
        /// The list of items to enqueue
        /// </summary>
        private ConcurrentQueue<TElement> _items = new ConcurrentQueue<TElement>();

        /// <summary>
        /// Locks the access to the item queue
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// The pointer to the disposal task of debug task
        /// </summary>
        private readonly Task<IAsyncDisposable> _disposeDebug;

        /// <summary>
        /// Initializes a new instance of the <see cref="RxCanon{TElement}"/> class.
        /// </summary>
        public RxCanon()
        {
            // 100 ms by default
            TimeInterval = 100;
            // Source observable
            Source = AsyncObservable
                .Interval(TimeSpan.FromMilliseconds(TimeInterval))
                .SelectMany(i => PullAll())
                .Timestamp();
            // For debug
            _disposeDebug = Subscribe(PrintAsync<Timestamped<TElement>>());
        }

        /// <summary>
        /// Gets or sets the time interval at which to time stamp the elements.
        /// </summary>
        public double TimeInterval { get; set; }

        /// <summary>
        /// Gets the source of time stamped elements.
        /// </summary>
        public IAsyncObservable<Timestamped<TElement>> Source { get; }

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
            _items.Enqueue(element);
        }

        /// <summary>
        /// Pulls the specified cancellation token.
        /// </summary>
        /// <returns></returns>
        public IAsyncObservable<TElement> PullAll()
        {
            var observable = AsyncObservable.Create<TElement>(obs =>
            {
                while (!_items.IsEmpty)
                {
                    var item = PullOne();
                    if (item == null)
                        break;
                    obs.OnNextAsync(item);
                }
                return Task.FromResult(AsyncDisposable.Nop); 
            });
            return observable;
        }

        /// <summary>
        /// Pulls one element and stops in case a cancellation token is sent.
        /// </summary>
        /// <returns></returns>
        public TElement PullOne()
        {
            bool result;
            TElement item;
            //lock (_lock)
            //{
                result = _items.TryDequeue(out item);
            //}
            return !result ? default(TElement) : item;
        }

        /// <summary>
        /// Subscribes the specified action to call when pulled.
        /// </summary>
        /// <param name="observer"></param>
        /// <returns>The id associated to the action being stored</returns>
        public Task<IAsyncDisposable> Subscribe(IAsyncObserver<Timestamped<TElement>> observer)
        {
            lock (_lock)
            {
                return Source.SubscribeSafeAsync(observer);
            }
        }

        /// <summary>
        /// Un-subscribes the specified identifier.
        /// </summary>
        /// <param name="disposable"></param>
        public void Unsubscribe(Task<IAsyncDisposable> disposable)
        {
            lock (_lock)
            {
                disposable.Dispose();
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Unsubscribe(_disposeDebug);
            _items = null;
        }

        /// <summary>
        /// Prints asynchronously source elements.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private static IAsyncObserver<T> PrintAsync<T>()
        {
            return AsyncObserver.Create<T>(
                async x =>
                {
                    await Task.Yield();
                    Console.WriteLine(x);
                },
                async ex =>
                {
                    await Task.Yield();
                    Console.WriteLine("Error: " + ex);
                },
                async () =>
                {
                    await Task.Yield();
                    Console.WriteLine("Completed");
                }
            );
        }
    }
}
