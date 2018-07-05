using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Babble.Core.Common;
using Babble.Core.HashgraphImpl.Model;
using Babble.Core.HashgraphImpl.Stores;
using Babble.Core.NetImpl;
using Babble.Core.NetImpl.PeerImpl;
using Babble.Core.NetImpl.TransportImpl;
using Babble.Core.NodeImpl.PeerSelector;
using Babble.Core.ProxyImpl;
using Babble.Core.Util;
using Nito.AsyncEx;
using Serilog;

namespace Babble.Core.NodeImpl
{
    public class Node
    {
        private readonly NodeState nodeState;
        public Config Conf { get; }
        public int Id { get; }
        public IStore Store { get; }
        private readonly Peer[] participants;
        public ITransport Trans { get; }

        public IAppProxy Proxy { get; }
        public Controller Controller { get; }

        public string LocalAddr { get; }
        private readonly ILogger logger;
        public IPeerSelector PeerSelector { get; }

        private readonly AsyncLock coreLock;
        private readonly AsyncLock selectorLock;
        private readonly ControlTimer controlTimer;


        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Task nodeTask;


        private readonly AsyncProducerConsumerQueue<byte[]> submitCh;
        private readonly AsyncMonitor submitChMonitor;
        private readonly AsyncProducerConsumerQueue<Rpc> netCh;
        private readonly AsyncMonitor netChMonitor;
        private readonly AsyncProducerConsumerQueue<Block> commitCh;
        private readonly AsyncMonitor commitChMonitor;

        // Ensures there is only one background operation executed at a time
        private SemaphoreSlim _backgroundOperationLock = new SemaphoreSlim(1);
        private readonly object _operationLock = new object();
        private readonly object _selectorLock = new object();

        private Stopwatch nodeStart;
        private int syncRequests;
        private int syncErrors;

        public Node(Config conf, int id, CngKey key, Peer[] participants, IStore store, ITransport trans, IAppProxy proxy, ILogger loggerIn)

        {
            logger = loggerIn.AddNamedContext("Node", id.ToString());

            LocalAddr = trans.LocalAddr;

            var (pmap, _) = store.Participants();

            commitCh = new AsyncProducerConsumerQueue<Block>(400);
            commitChMonitor = new AsyncMonitor();

            Controller = new Controller(id, key, pmap, store, commitCh, logger);
            coreLock = new AsyncLock();
            PeerSelector = new RandomPeerSelector(participants, LocalAddr);

            selectorLock = new AsyncLock();
            Id = id;
            Store = store;
            Conf = conf;



            Trans = trans;

            netCh = trans.Consumer;
            netChMonitor = new AsyncMonitor();

            Proxy = proxy;

            this.participants = participants;

            submitCh = proxy.SubmitCh();
            submitChMonitor = new AsyncMonitor();

            controlTimer = ControlTimer.NewRandomControlTimer(conf.HeartbeatTimeout);

            nodeState = new NodeState();

            //Initialize as Babbling
            nodeState.SetStarting(true);
            nodeState.SetState(NodeStateEnum.Babbling);
        }

        public Task<Exception> Init(bool bootstrap)
        {
            var peerAddresses = new List<string>();
            foreach (var p in PeerSelector.Peers())
            {
                peerAddresses.Add(p.NetAddr);
            }

            logger.Debug("Init Node LocalAddr={LocalAddress}; Peers={@PeerAddresses}",LocalAddr, peerAddresses);

            if (bootstrap)
            {
                return Controller.Bootstrap();
            }

            return Controller.Init();
        }

        public void StartAsync(bool gossip, CancellationToken ct = default)
        {
            var tcsInit = new TaskCompletionSource<bool>();

            nodeStart = Stopwatch.StartNew();

            nodeTask = Task.Run(async () =>
            {
                //The ControlTimer allows the background routines to control the
                //heartbeat timer when the node is in the Babbling state. The timer should
                //only be running when there are uncommitted transactions in the system.
                var controlTimerTask = controlTimer.RunAsync(cts.Token);

                //Execute some background work regardless of the state of the node.
                //Process RPC requests as well as SumbitTx and CommitBlock requests
                DoBackgroundWord(cts.Token);

                //Execute Node State Machine
                var stateMachineTask = StateMachineRunAsync(gossip, cts.Token);

                // await all
                var runTask = Task.WhenAll(controlTimerTask, stateMachineTask);//, processingRpcTask, addingTransactions, commitBlocks);

                tcsInit.SetResult(true);

                await runTask;
            }, ct);

            //return tcsInit.Task;
        }

        private void DoBackgroundWord(CancellationToken ctsToken)
        {
            Task.Run(() => ProcessingRpc(ctsToken), ctsToken);
            Task.Run(() => AddingTransactions(ctsToken), ctsToken);
            Task.Run(() => CommitBlocks(ctsToken), ctsToken);
        }

        private async Task StateMachineRunAsync(bool gossip, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Run different routines depending on node state
                var state = nodeState.GetState();
                logger.Debug("Run Loop {state}", state);

                switch (state)
                {
                    case NodeStateEnum.Babbling:
                        Babble(gossip, ct);
                        break;

                    case NodeStateEnum.CatchingUp:
                        await FastForward();
                        break;

                    case NodeStateEnum.Shutdown:
                        return;
                }
            }
        }

        // Background work
        private async Task ProcessingRpc(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _backgroundOperationLock.WaitAsync(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    var rpc = await netCh.DequeueAsync(ct);
                    logger.Debug("Processing RPC");
                    await ProcessRpcAsync(rpc, ct);

                    //using (await netChMonitor.EnterAsync())
                    //{
                    //    netChMonitor.Pulse();
                    //}
                }
                finally
                {
                    _backgroundOperationLock.Release();
                }
            }
        }

        private async Task AddingTransactions(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _backgroundOperationLock.WaitAsync(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    var tx = await submitCh.DequeueAsync(ct);

                    logger.Debug("Adding Transaction");
                    AddTransaction(tx);

                    //using (await submitChMonitor.EnterAsync())
                    //{
                    //    submitChMonitor.Pulse();
                    //}
                }
                finally
                {
                    _backgroundOperationLock.Release();
                }
            }
        }

        public async Task AddingTransactionsCompleted()
        {
            using (await submitChMonitor.EnterAsync())
            {
                await submitChMonitor.WaitAsync();
            }
        }

        private async Task CommitBlocks(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _backgroundOperationLock.WaitAsync(ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    var block = await commitCh.DequeueAsync(ct);
                    logger.Debug("Committing Block Index={Index}; RoundReceived={RoundReceived}; TxCount={TxCount}", block.Index(), block.RoundReceived(), block.Transactions().Length);

                    var err = Commit(block);
                    if (err != null)
                    {
                        logger.Error("Committing Block", err);
                    }

                    //using (await commitChMonitor.EnterAsync())
                    //{
                    //    commitChMonitor.Pulse();
                    //}
                }
                finally
                {
                    _backgroundOperationLock.Release();
                }
            }
        }

        public async Task CommitBlocksCompleted()
        {
            using (await commitChMonitor.EnterAsync())
            {
                await commitChMonitor.WaitAsync();
            }
        }

        private void Babble(bool gossip, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var oldState = nodeState.GetState();

                controlTimer.TickCh.DequeueAsync(ct).Wait(ct);

                if (gossip)
                {
                    var (proceed, err) = PreGossip();
                    if (proceed && err == null)
                    {
                        logger.Debug("Time to gossip!");
                        var peer = PeerSelector.Next();
                        logger.Debug("gossip from {localAddr} to peer {peer}",LocalAddr, peer.NetAddr);
                        Gossip(peer.NetAddr);


                    }

                    if (!Controller.NeedGossip())
                    {
                        controlTimer.StopCh.Enqueue(true, ct);
                    }
                    else if (!controlTimer.Set)
                    {
                        controlTimer.ResetCh.Enqueue(true, ct);
                    }
                }

                var newState = nodeState.GetState();
                if (newState != oldState)
                {
                    break;
                }
            }
        }

        public async Task ProcessRpcAsync(Rpc rpc, CancellationToken ct)
        {
            var s = nodeState.GetState();

            if (s != NodeStateEnum.Babbling)
            {
                logger.Debug("Discarding RPC Request {state}", s);
                var resp = new RpcResponse {Error = new NetError($"not ready: {s}"), Response = new SyncResponse {FromId = Id}};
                await rpc.RespChan.EnqueueAsync(resp, ct);
            }
            else
            {
                switch (rpc.Command)
                {
                    case SyncRequest cmd:
                        ProcessSyncRequestAsync(rpc, cmd);
                        break;

                    case EagerSyncRequest cmd:
                        ProcessEagerSyncRequest(rpc, cmd);
                        break;

                    default:

                        logger.Error("Discarding RPC Request {@cmd}", rpc.Command);
                        var resp = new RpcResponse {Error = new NetError($"unexpected command"), Response = null};
                        await rpc.RespChan.EnqueueAsync(resp, ct);

                        break;
                }
            }
        }

        private void ProcessSyncRequestAsync(Rpc rpc, SyncRequest cmd)
        {
            logger.Debug("Process SyncRequest FromId={FromId}; Known={@Known};", cmd.FromId, cmd.Known);

            var resp = new SyncResponse
            {
                FromId = Id
            };

            Exception respErr = null;

            //Check sync limit
            bool overSyncLimit;
            lock (_operationLock)
            {
                overSyncLimit = Controller.OverSyncLimit(cmd.Known, Conf.SyncLimit).Result;
            }

            if (overSyncLimit)
            {
                logger.Debug("SyncLimit");
                resp.SyncLimit = true;
            }
            else
            {
                //Compute EventDiff
                var start = Stopwatch.StartNew();
                Event[] diff;
                Exception err;
                lock (_operationLock)
                {
                    (diff, err) = Controller.EventDiff(cmd.Known).Result;
                }

                logger.Debug("EventDiff() duration={duration}", start.Nanoseconds());
                if (err != null)
                {
                    logger.Error("Calculating EventDiff {err}", err);
                    respErr = err;
                }

                //Convert to WireEvents
                WireEvent[] wireEvents;
                (wireEvents, err) = Controller.ToWire(diff);
                if (err != null)
                {
                    logger.Debug("Converting to WireEvent {err}", err);
                    respErr = err;
                }
                else
                {
                    resp.Events = wireEvents;
                }
            }

            //Get Self KnownEvents
            Dictionary<int, int> known;
            lock (_operationLock)
            {
                known = Controller.KnownEvents().Result;
            }

            resp.Known = known;

            logger.Debug("Responding to SyncRequest {@SyncRequest}", new
            {
                Events = resp.Events.Length,
                resp.Known,
                resp.SyncLimit,
                Error = respErr
            });

            rpc.RespondAsync(resp, respErr != null ? new NetError(resp.FromId.ToString(), respErr) : null).Wait();
        }

        private void ProcessEagerSyncRequest(Rpc rpc, EagerSyncRequest cmd)
        {
            logger.Debug("EagerSyncRequest {EagerSyncRequest}", new
            {
                cmd.FromId,
                Events = cmd.Events.Length
            });

            var success = true;

            Exception respErr;
            lock (_operationLock)
            {
                respErr = Sync(cmd.Events).Result;
            }

            if (respErr != null)
            {
                logger.Error("Sync() {error}", respErr);
                success = false;
            }

            var resp = new EagerSyncResponse
            {
                FromId = Id,
                Success = success
            };

            rpc
                .RespondAsync(resp, respErr != null 
                                        ? new NetError(resp.FromId.ToString(), respErr) 
                                        : null)
                .Wait();
        }

        private (bool proceed, Exception error) PreGossip()
        {
            lock (_operationLock)
            {
                //Check if it is necessary to gossip
                var needGossip = Controller.NeedGossip() || nodeState.IsStarting();
                if (!needGossip)
                {
                    logger.Debug("Nothing to gossip");
                    return (false, null);
                }

                //If the transaction pool is not empty, create a new self-event and empty the
                //transaction pool in its payload
                var err = Controller.AddSelfEvent();
                if (err == null) return (true, null);
                logger.Error("Adding SelfEvent", err);
                return (false, err);
            }
        }

        public Exception Gossip(string peerAddr)
        {
            //Pull
            var (syncLimit, otherKnownEvents, err) = Pull(peerAddr);
            if (err != null)
            {
                return err;
            }

            //check and handle syncLimit
            if (syncLimit)
            {
                logger.Debug("SyncLimit {from}", peerAddr);
                nodeState.SetState(NodeStateEnum.CatchingUp);
                return null;
            }

            //Push
            err = Push(peerAddr, otherKnownEvents);
            if (err != null)
            {
                return err;
            }

            //update peer selector
            lock (_selectorLock)
            {
                PeerSelector.UpdateLast(peerAddr);
            }

            LogStats();

            nodeState.SetStarting(false);

            return null;
        }

        private (bool syncLimit, Dictionary<int, int> otherKnown, Exception err) Pull(string peerAddr)
        {
            //Compute KnownEvents
            Dictionary<int, int> knownEvents;
            lock (_operationLock)
            {
                knownEvents = Controller.KnownEvents().Result;
            }

            //Send SyncRequest
            var start = Stopwatch.StartNew();

            var (resp, err) = RequestSync(peerAddr, knownEvents).Result;
            var elapsed = start.Nanoseconds();
            logger.Debug("RequestSync() Duration = {duration}", elapsed);
            if (err != null)
            {
                logger.Error("RequestSync() {error}", err);
                return (false, null, err);
            }

            logger.Debug("SyncResponse {@SyncResponse}", new {resp.FromId, resp.SyncLimit, Events = resp.Events.Length, resp.Known});

            if (resp.SyncLimit)
            {
                return (true, null, null);
            }

            //Set Events to Hashgraph and create new Head if necessary
            lock (_operationLock)
            {
                err = Sync(resp.Events).Result;
            }

            if (err != null)
            {
                logger.Error("Sync() {error}", err);
                return (false, null, err);
            }

            return (false, resp.Known, null);
        }

        private Exception Push(string peerAddr, Dictionary<int, int> knownEvents)
        {
            //Check SyncLimit
            bool overSyncLimit;
            lock (_operationLock)
            {
                overSyncLimit = Controller.OverSyncLimit(knownEvents, Conf.SyncLimit).Result;
            }

            if (overSyncLimit)
            {
                logger.Debug("SyncLimit");
                return null;
            }

            //Compute EventDiff
            var start = Stopwatch.StartNew();

            Event[] diff;
            Exception err;
            lock (_operationLock)
            {
                (diff, err) = Controller.EventDiff(knownEvents).Result;
            }

            var elapsed = start.Nanoseconds();
            logger.Debug("EventDiff() {duration}", elapsed);
            if (err != null)
            {
                logger.Error("Calculating EventDiff {error}", err);
                return err;
            }

            //Convert to WireEvents
            WireEvent[] wireEvents;
            (wireEvents, err) = Controller.ToWire(diff);
            if (err != null)
            {
                logger.Debug("Converting to WireEvent", err);
                return err;
            }

            //Create and Send EagerSyncRequest
            start = Stopwatch.StartNew();

            EagerSyncResponse resp2;
            (resp2, err) = RequestEagerSync(peerAddr, wireEvents).Result;
            elapsed = start.Nanoseconds();
            logger.Debug("RequestEagerSync() {duration}", elapsed);
            if (err != null)
            {
                logger.Error("RequestEagerSync() {error}", err);
                return err;
            }

            logger.Debug("EagerSyncResponse {@EagerSyncResponse}", new {resp2.FromId, resp2.Success});

            return null;
        }

        private Task FastForward()
        {
            logger.Debug("IN CATCHING-UP STATE");
            logger.Debug("fast-sync not implemented yet");

            //XXX Work in Progress on fsync branch

            nodeState.SetState(NodeStateEnum.Babbling);

            return Task.FromResult(true);
        }

        private async Task<(SyncResponse resp, Exception err)> RequestSync(string target, Dictionary<int, int> known)
        {
            var args = new SyncRequest
            {
                FromId = Id,
                Known = known
            };

            var (resp, err) = await Trans.Sync(target, args);
            return (resp, err);
        }

        private async Task<(EagerSyncResponse resp, Exception err)> RequestEagerSync(string target, WireEvent[] events)
        {
            var args = new EagerSyncRequest
            {
                FromId = Id,
                Events = events
            };

            var (resp, err) = await Trans.EagerSync(target, args);
            return (resp, err);
        }

        public async Task<Exception> Sync(WireEvent[] events)
        {
            logger.Debug("Unknown events {@events}",events);

            //Insert Events in Hashgraph and create new Head if necessary
            var start = Stopwatch.StartNew();
            var err = await Controller.Sync(events);

            var elapsed = start.Nanoseconds();
            
            if (err != null)
            {
                return err;
            }

            logger.Debug("Processed Sync() {duration}", elapsed);

            //Run consensus methods
            start = Stopwatch.StartNew();
            err = await Controller.RunConsensus();

            elapsed = start.Nanoseconds();
            logger.Debug("Processed RunConsensus() {duration}", elapsed);
            return err;
        }

        private Exception Commit(Block block)
        {
            Exception err;
            byte[] stateHash;
            (stateHash, err) = Proxy.CommitBlock(block).Result;

            logger.Debug("CommitBlockResponse {@CommitBlockResponse}", new {Index = block.Index(), StateHash = stateHash.ToHex(), Err = err});

            block.Body.StateHash = stateHash;

            lock (_operationLock)
            {
                BlockSignature sig;
                (sig, err) = Controller.SignBlock(block).Result;
                if (err != null)
                {
                    return err;
                }

                Controller.AddBlockSignature(sig);

                return null;
            }
        }

        private void AddTransaction(byte[] tx)
        {
            lock (_operationLock)
            {
                Controller.AddTransactions(new[] {tx});
            }
        }

        public void Shutdown()

        {
            if (nodeState.GetState() == NodeStateEnum.Shutdown) return;
            logger.Debug("Shutdown");

            //Exit any non-shutdown state immediately
            nodeState.SetState(NodeStateEnum.Shutdown);

            //Stop and wait for concurrent operations
            cts.Cancel();

            try
            {
                nodeTask?.Wait();
            }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException)
            {
            }
            catch (AggregateException e)
            {
                logger.Error(e, "Application termination ");
            }
            finally
            {
                cts.Dispose();
            }

            //transport and store should only be closed once all concurrent operations
            //are finished otherwise they will panic trying to use close objects
            Trans.Close();
            Controller.Hg.Store.Close();
        }

        public Dictionary<string, string> GetStats()
        {
            var timeElapsed = nodeStart.Elapsed;
            var consensusEvents = Controller.GetConsensusEventsCount();
            var consensusEventsPerSecond = (decimal) consensusEvents / timeElapsed.Seconds;
            var lastConsensusRound = Controller.GetLastConsensusRoundIndex();
            decimal consensusRoundsPerSecond = 0;

            if (lastConsensusRound != null)
            {
                consensusRoundsPerSecond = (decimal) (lastConsensusRound) / timeElapsed.Seconds;
            }

            var s = new Dictionary<string, string>
            {

                {"last_consensus_round", lastConsensusRound.ToString()},
                {"consensus_events", consensusEvents.ToString()},
                {"consensus_transactions", Controller.GetConsensusTransactionsCount().ToString()},
                {"undetermined_events", Controller.GetUndeterminedEvents().Length.ToString()},
                {"transaction_pool", Controller.TransactionPool.Count.ToString()},
                {"num_peers", PeerSelector.Peers().Length.ToString()},
                {"sync_rate", SyncRate().ToString("0.00")},
                {"events_per_second", consensusEventsPerSecond.ToString("0.00")},
                {"rounds_per_second", consensusRoundsPerSecond.ToString("0.00")},
                {"round_events", Controller.GetLastCommitedRoundEventsCount().ToString()},
                {"id", Id.ToString()},
                {"state", nodeState.GetState().ToString()},
            };
            return s;
        }

        private void LogStats()
        {
            var stats = GetStats();
            logger.Debug("Stats {@stats}", stats);
        }

        public decimal SyncRate()
        {
            decimal syncErrorRate = 0;
            if (syncRequests != 0)
            {
                syncErrorRate = (decimal) syncErrors / syncRequests;
            }

            return 1 - syncErrorRate;
        }
    }
}