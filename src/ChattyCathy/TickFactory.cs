using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChattyCathy
{
    /// <summary>
    /// TickFactory class
    /// Generates ticks on a configurable interval
    /// Elements interested in getting ticks need to subscribe with an action
    /// </summary>
    /// <seealso cref="TimeSpan" />
    public class TickFactory: Canon<TimeSpan>, IDisposable
    {
        /// <summary>
        /// The singleton instance creation lock
        /// </summary>
        private static readonly object InstanceCreationLock = new object();

        /// <summary>
        /// The thread management lock avoids calling Start() and Stop() methods simultaneously
        /// </summary>
        private readonly object _threadManagementLock = new object();

        /// <summary>
        /// The instance of the singleton
        /// </summary>
        private static TickFactory _instance = null;

        /// <summary>
        /// The variable to determine if tick generation task is already running
        /// </summary>
        private bool _isRunning;

        /// <summary>
        /// The cancellation token source to cancel tick generation task
        /// </summary>
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// The send tick task that handles ticks generation
        /// </summary>
        private Task _sendTickTask = null;

        /// <summary>
        /// The thread count semaphore prevents creation of multiple threads in parallel
        /// </summary>
        private readonly SemaphoreSlim _threadCountSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// The tick delay (default is 100 ms)
        /// </summary>
        private int _tickDelay = 100;

        /// <summary>
        /// The current time
        /// </summary>
        private DateTime _currentTime;

        /// <summary>
        /// Prevents a default instance of the <see cref="TickFactory"/> class from being created.
        /// </summary>
        private TickFactory()
        {
            _isRunning = false;
            _currentTime = DateTime.Now;
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        public static TickFactory Instance
        {
            get
            {
                lock (InstanceCreationLock)
                {
                    return _instance ?? (_instance = new TickFactory());
                }
            }
        }

        /// <summary>
        /// Configures the specified tick delay.
        /// </summary>
        /// <param name="tickDelay">The tick delay.</param>
        /// <returns>a pointer to current instance</returns>
        public TickFactory Configure(int tickDelay)
        {
            _tickDelay = tickDelay;
            return this;
        }

        /// <summary>
        /// Starts sending ticks.
        /// </summary>
        public void Start()
        {
            lock (_threadManagementLock)
            {
                _isRunning = true;
                if (_sendTickTask != null 
                    && !(_sendTickTask.IsCompleted || _sendTickTask.IsCanceled || _sendTickTask.IsFaulted)
                    && _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) return;
                // Just in case the current task is still running but not canceled. Is it possible ?
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                    _cancellationTokenSource.Cancel();
                // If necessary, launch the task again
                _cancellationTokenSource = new CancellationTokenSource();
                try
                {
                    _sendTickTask = Task.Run(() => SendTickTask(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    // Because of the _threadManagementLock, this case should never happen
                    // We handle this however
                    _sendTickTask = null;
                    _isRunning = false;
                }
            }
        }

        /// <summary>
        /// Stops sending ticks.
        /// </summary>
        public void Stop()
        {
            lock (_threadManagementLock)
            {
                // Stop the task
                _isRunning = false;
                if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested) return;
                _cancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Sends ticks task.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async void SendTickTask(CancellationToken cancellationToken)
        {
            // Ensures that we do not start more than 1 thread in parallel
            await _threadCountSemaphore.WaitAsync(cancellationToken);
            try
            {
                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    var now = DateTime.Now;
                    Push(now - _currentTime);
                    _currentTime = now;
                    PullOne(cancellationToken);
                    try
                    {
                        await Task.Delay(_tickDelay, cancellationToken);
                    }
                    catch (TaskCanceledException) 
                    {
                        // Nothing to do as we don't have any new tick generated here
                    }
                }
            }
            finally
            {
                _sendTickTask = null;
                _threadCountSemaphore.Release();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
