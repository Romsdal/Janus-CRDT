using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;

namespace BFTCRDT.DAG;

internal class CurrentRoundState
{
    public int round { get; set; } = 0;
    public List<Certificate> receivedCertificated { get; set; }
    public VertexBlock currentRoundBlock { get; set; } = null;
    // received acks for the current round block
    public ConcurrentDictionary<int, byte[]> receivedAcks { get; set; }
    public bool didConsensus { get; set; } = false;
    public int currentRoundReceivedCertCount = 0;

    public CurrentRoundState(int numberNodes)
    {
        receivedCertificated = new List<Certificate>(numberNodes);
        receivedAcks = new ConcurrentDictionary<int, byte[]>();
    }

    public void AdvanceRound(int round)
    {
        this.round = round;
        receivedCertificated.Clear();
        receivedAcks.Clear();
        currentRoundBlock = null;
        didConsensus = false;
        currentRoundReceivedCertCount = 0;
    }
}

internal struct Stats
{
    volatile public int certificateByTimeout = 0;
    volatile public int certificateByPreemptiveCheck = 0;
    volatile public int recievedUpdates = 0;
    //public List<int> numUpdatesPerbklock = new();
    volatile public int numConsens = 0;
    volatile public int numUpdatesfinishedConsensus = 0;
    volatile public int missingPredecessorCount = 0;
    volatile public int noCertifcatetoCheckCount = 0;
    volatile public int noAdvanceRoundBecauseNotThereYetCount = 0;
    volatile public int jumpingRoundCount = 0;
    volatile public int callingAdvanceRoundCount = 0;
    volatile public int actuallyCreatedBlockCount = 0;
    volatile public int checkCertifcateLockTimeoutCount = 0;
    volatile internal int rejectByInvalidSignatureCount = 0;

    volatile public int receivedBlockMsgCount = 0;
    volatile public int receivedCertMsgCount = 0;
    volatile public int receivedAckMsgCount = 0;
    volatile internal int receivedBlockQueryMsgCount = 0;
    volatile public int sentBlockMsgCount = 0;
    volatile public int sentCertMsgCount = 0;
    volatile public int sentAckMsgCount = 0;
    volatile public int sentBlockQueryMsgCount = 0;
    volatile internal int notEnoughCertToAdvanceCount = 0;

    public Stats()
    {
    }
}


public delegate void ReceivedBlockCallback(VertexBlock block);


/// <summary>
/// A consensus instance
/// </summary>
public class DAG
{
    // cluster infos
    public Replica self { get; }
    // <nodeid, DAGNode>
    public Dictionary<int, Replica> replicas { get; }
    private Dictionary<int, ECDsa> replicaKeys;
    public int f { get; }
    public bool isInit { get; private set; } = false;
    public bool active { get; private set; } = false;

    private LinkedList<UpdateMessage> updatesBuffer;

    private CurrentRoundState currentRoundState;

    // if I received a certificated, but not its predecessors, use a priority queue 
    // so that certificates from earlier rounds are processed first
    private PriorityQueue<Certificate, int> certificatesBuffer;

    // blocks index in the form of <round, <source, block>>
    private readonly List<ConcurrentDictionary<int, VertexBlock>> receivedBlocks;


    // blocks index in the form of <hash, Block>, only a block with a certificate can be indexed
    public ConcurrentDictionary<byte[], VertexBlock> blocksIndexedByHash { get; private set; }
    public ConcurrentDictionary<byte[], Certificate> certificatesIndexedByHash;

    public Consensus consensus { get; set; } = null;

    public ReceivedBlockCallback receivedBlockEvent { set; get; }

    // number of updates in the buffer before a new block is created
    private int maxBatchSize = 200;
    // time interval between two new blocks
    private long minimalNewBlockInterval = 100;
    private long lastBlockCreationTime;

    private volatile int latestCert = -1;

    // responsible for sending messages
    private readonly IDAGMsgSender msgSender;

    private readonly ILogger logger;
    private Stats stats;

    private SpinLock spinLock0 = new();

    private ReaderWriterLock canCheckCertLock = new ReaderWriterLock();


    public DAG(Replica selfNode, List<Replica> replicas, IDAGMsgSender msgSender, ILogger logger = null)
    {
        this.self = selfNode;
        this.replicas = new Dictionary<int, Replica>();
        foreach (var r in replicas)
        {
            this.replicas.Add(r.nodeid, r);
        }

        f = (replicas.Count - 1) / 3;

        this.msgSender = msgSender;

        this.currentRoundState = new CurrentRoundState(replicas.Count);

        this.certificatesBuffer = new PriorityQueue<Certificate, int>();
        this.receivedBlocks = new List<ConcurrentDictionary<int, VertexBlock>>
        {
            new(this.replicas.Count, this.replicas.Count) // round 0
        };
        this.blocksIndexedByHash = new ConcurrentDictionary<byte[], VertexBlock>(new ByteArrayComparer());
        this.certificatesIndexedByHash = new ConcurrentDictionary<byte[], Certificate>(new ByteArrayComparer());

        this.updatesBuffer = new LinkedList<UpdateMessage>();
        this.logger = logger ?? new NullLogger<DAG>();

        this.stats = new Stats();
        


    }

    public void DAGClusterInit()
    {
        msgSender.BroadCastInit(self.nodeid, self.publicKey);
    }

    public void Start()
    {
        // TODO:
        //consensus.stats = this.stats;

        logger.LogInformation("Replica {nodeid} started constructing DAG", self.nodeid);
        var genesisBlock = CreateBlock(new List<byte[]>(), new List<UpdateMessage>());
        msgSender.BroadcastBlock(genesisBlock);

        active = true;

        // run CheckTimeout in the background
        new Thread(() =>
        {
            CheckTimeOutLoop();
        }).Start();
    }


    /// <summary>
    /// Call this method to submit a new message to the DAG.
    /// </summary>
    /// <param name="msg"></param>
    public void SubmitMessage(UpdateMessage msg)
    {
        lock(updatesBuffer)
        {
            updatesBuffer.AddLast(msg);    
        }

        Interlocked.Increment(ref stats.recievedUpdates);
        //logger.LogTrace("Submitted update message");
    }

    /// <summary>
    /// Get the current round of the DAG
    /// </summary>
    /// <returns></returns>
    public int GetCurRound()
    {
        return currentRoundState.round;
    }

    /// <summary>
    /// Return the current state of the DAG
    /// </summary>
    /// <returns> A list of 
    /// [round, 
    /// hash of current round block
    /// # of received signature for the block,
    /// # of received current round blocks
    /// # of received current round certs, 
    /// if the node has reached consensus for current round
    /// ...]</returns>
    public string[] GetCurrStats()
    {

        int currentRoundReceivedBlocks;

        try
        {
            currentRoundReceivedBlocks = receivedBlocks[currentRoundState.round].Count;
        }
        catch (Exception)
        {
            currentRoundReceivedBlocks = 0;
        }

        string hash;
        try
        {
            hash = System.Text.Encoding.Default.GetString(currentRoundState.currentRoundBlock.digest);
        }
        catch (Exception)
        {
            hash = "null";
        }

        int currentRoundUpdateCount = currentRoundState.currentRoundBlock is null ? 0 : currentRoundState.currentRoundBlock.updateMsgs.Count;

        return new string[] {   $"{currentRoundState.round}",
                                $"{currentRoundState.receivedAcks.Count}",
                                $"{currentRoundReceivedBlocks}",
                                $"{currentRoundState.receivedCertificated.Count}",
                                $"{currentRoundState.didConsensus}",
                                $"{currentRoundUpdateCount}",
                                "|",
                                $"{stats.receivedBlockMsgCount}",
                                $"{stats.receivedCertMsgCount}",
                                $"{stats.receivedAckMsgCount}",
                                $"{stats.receivedBlockQueryMsgCount}",
                                "-",
                                $"{stats.sentBlockMsgCount}",
                                $"{stats.sentCertMsgCount}",
                                $"{stats.sentAckMsgCount}",
                                $"{stats.sentBlockQueryMsgCount}",
                                "|",
                                $"{stats.certificateByTimeout}",
                                $"{stats.certificateByPreemptiveCheck}",
                                $"{stats.recievedUpdates}",
                                $"{stats.noCertifcatetoCheckCount}",
                                $"{stats.missingPredecessorCount}",
                                "|",
                                $"{stats.noAdvanceRoundBecauseNotThereYetCount}",
                                $"{stats.jumpingRoundCount}",
                                $"{stats.callingAdvanceRoundCount}",
                                $"{stats.checkCertifcateLockTimeoutCount}",
                                $"{stats.notEnoughCertToAdvanceCount}"};

    }



    public void Stop()
    {
        active = false;
        logger.LogInformation("Replica {nodeid} stopped constructing DAG", self.nodeid);
    }

    
    public void HandleMessage(DAGMessage msg)
    {
        // Stopwatch sw = new();
        // sw.Start();
        int type = -1;
        try
        {
            canCheckCertLock.AcquireReaderLock(Timeout.Infinite);
            switch (msg)
            {
                case InitMessage initMessage:
                    ReceivedInit(initMessage);
                    type = 0;
                    break;
                case VertexBlockMessage vbmsg:
                    ReceivedBlock(vbmsg.GetBlock(), vbmsg.IsQueryReply);
                    stats.receivedBlockMsgCount++;
                    type = 1;
                    break;
                case CertificateMessage certificateBlock:
                    ReceivedCertificate(certificateBlock.GetCertificate());
                    stats.receivedCertMsgCount++;
                    type = 2;
                    break;
                case SignatureMessage signatureMessage:
                    ReceivedSignature(signatureMessage);
                    stats.receivedAckMsgCount++;
                    type = 3;
                    break;
                case BlockQueryMessage blockQueryMessage:
                    ReceivedBlockQuery(blockQueryMessage);
                    stats.receivedBlockQueryMsgCount++;
                    type = 4;
                    break;
                default:
                    break;
            }
        }
        finally
        {
            canCheckCertLock.ReleaseReaderLock();
        }
        // sw.Stop();
        // logger.LogTrace($"Handling Type {type} from {msg.source} for round {msg.round} took {sw.ElapsedMilliseconds} ms");
        // sw.Reset();
    }

    public void ReceivedInit(InitMessage msg)
    {
        // this needs to be synchronized to properly check if 
        // all replicas are initialized
        lock (this)
        {
            logger.LogTrace("Received init message from {s}", msg.source);

            if (isInit)
                throw new InvalidOperationException("Received init message after initialization");

            if (msg.source == self.nodeid)
                return;

            if (replicas[msg.source].publicKeyHandler is null)
                replicas[msg.source].publicKey = msg.publicKey;

            // check if all replicas are initialized by checking if replica field is not null
            if (!replicas.Values.Any(n => n.publicKeyHandler == null))
            {
                isInit = true;
                logger.LogInformation("All replicas are initialized");
                this.replicaKeys = replicas.ToDictionary(node => node.Value.nodeid, node => node.Value.publicKeyHandler);
            }
        }
    }


    public void ReceivedBlock(VertexBlock block, bool IsQueryReply)
    {

        string blockInfo = $"vertex block for round {block.round} from {block.source} with {block.updateMsgs.Count} updates";

        // check signature  
        if (!block.Verify(replicas[block.source].publicKeyHandler))
        {
            logger.LogWarning("Reject {blockInfo} due to invalid signature", blockInfo);
            return;
        }

        // if the block is the return of a query for a block
        if (IsQueryReply)
        {
            // if already received the block
            if (GetBlocks(block.round).ContainsKey(block.source))
            {
                return;
            }

            AddBlock(block);

            blocksIndexedByHash[block.digest] = block;

            logger.LogTrace("Received queried {blockInfo}", blockInfo);
        }
        else
        {
            bool flag = true;

            // if block is not on the same round, still stores it but not acknowledged
            if (block.round < currentRoundState.round)
            {
                logger.LogTrace("Reject {blockInfo} due to wrong round, currentRound {r}", blockInfo, currentRoundState.round);
                flag = false;
            }

            // check if there is already a block from the replica in this round
            if (flag && block.round < receivedBlocks.Count && GetBlocks(block.round).ContainsKey(block.source))
            {
                logger.LogWarning("Reject {blockInfo} due to duplicate", blockInfo);
                return;
            }

            // if we are at round 0 or check if it has 2f + 1 certificate for previous round
            if (block.prevCertificates.Count < 2 * f + 1 && block.round != 0)
            {
                logger.LogWarning("Reject {blockInfo} due to insufficient certificates", blockInfo);
                return;
            }

            AddBlock(block);

            if (flag)
            {
                logger.LogTrace("Received {blockInfo}", blockInfo);
                AcknowledgeBlock(block);
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cert"></param>
    public void ReceivedCertificate(Certificate cert)
    {
        if (!cert.CheckSignatures(replicaKeys))
        {
            stats.rejectByInvalidSignatureCount += 1;
            logger.LogWarning("Reject certificate for round {r} from {s} due to invalid signature", cert.round, cert.source);
            return;
        }

        lock (certificatesBuffer)
        {
            certificatesBuffer.Enqueue(cert, cert.round);
        }

        logger.LogTrace("Received certificate from {s} for round {r}", cert.source, cert.round);


        // if block has not been received, query for it
        VertexBlock block = GetBlock(cert.round, cert.source);
        if (cert.round >= receivedBlocks.Count || block == null)
        {
            logger.LogTrace($"The block of the certificate has not been received, querying");
            QueryForBlock(cert);
        }
        else
        {
            block.certificate = cert;
        }


        // update last cert to skip ahead
        if (latestCert == -1)
            latestCert = cert.round;
        else
        {
            if (cert.round > latestCert)
                latestCert = cert.round;
        }

        // preemptive check for certificates, if enough certificates are received for current round
        if (cert.round == currentRoundState.round)
            Interlocked.Increment(ref currentRoundState.currentRoundReceivedCertCount);

        if (currentRoundState.currentRoundReceivedCertCount >= 2 * f + 1)
        {
            LockCookie lc = canCheckCertLock.UpgradeToWriterLock(Timeout.Infinite); ;
            try
            {
                stats.certificateByPreemptiveCheck += 1;
                CheckCertificates();
            }
            finally
            {
                canCheckCertLock.DowngradeFromWriterLock(ref lc);
            }
        }
    }

    public void ReceivedBlockQuery(BlockQueryMessage blockQueryMessage)
    {

        if (!blocksIndexedByHash.TryGetValue(blockQueryMessage.blockHash, out VertexBlock block))
            return;

        msgSender.SendBlock(block, blockQueryMessage.source, true);
        stats.sentBlockMsgCount++;
        logger.LogTrace("Sending queried vertex block for round {r} to node {s}",
            blockQueryMessage.round,
            blockQueryMessage.source);
    }

    /// <summary>
    /// Handle received signature for broadcasted block
    /// </summary>
    /// <param name="signature"></param>
    public void ReceivedSignature(SignatureMessage signature)
    {
        byte[] blockHash = GetBlock(signature.round, self.nodeid).digest;

        // if the block already has certificate
        if (certificatesIndexedByHash.TryGetValue(blockHash, out _))
            return;

        if (signature.round != currentRoundState.round)
            return;

        if (!signature.Verify(blockHash, replicas[signature.source].publicKeyHandler))
            return;

        logger.LogTrace("Received signature from {s} for block for round {r} current round {r}",
            signature.source,
            signature.round,
            currentRoundState.round);

        // add to received acks if source is not in the dict
        currentRoundState.receivedAcks.TryAdd(signature.source, signature.signature);

        Certificate certificate = null;
        lock (currentRoundState)
        {
            if (currentRoundState.receivedAcks.Count >= (2 * f) + 1 && currentRoundState.currentRoundBlock.certificate is null)
            {
                logger.LogTrace("Received enough acks for block at round {r}", currentRoundState.round);

                certificate = new(
                   currentRoundState.currentRoundBlock.round,
                   self.nodeid,
                   currentRoundState.currentRoundBlock.digest,
                   new Dictionary<int, byte[]>(currentRoundState.receivedAcks) // make a copy of the current received acks
               );

                // this is from myself, so no need to do additional check
                currentRoundState.currentRoundBlock.certificate = certificate;
                currentRoundState.receivedCertificated.Add(certificate);
                bool check = certificatesIndexedByHash.TryAdd(certificate.blockHash, certificate);
                Debug.Assert(check, "Certificate already added");
                check = blocksIndexedByHash.TryAdd(certificate.blockHash, currentRoundState.currentRoundBlock);
                Debug.Assert(check, "Block already added");

            }
        }

        logger.LogTrace("Already Received {c} acks at round {r} ", currentRoundState.receivedAcks.Count, currentRoundState.round);

        if (certificate is not null)
        {
            msgSender.BroadcastCertificate(certificate);
            stats.sentCertMsgCount++;
            logger.LogTrace("Sent certificate for round {r}", certificate.round);
        }

    }


    // temporary hold certificates while checking for predecessors
    readonly List<Certificate> certificateCheckTempList = new();
    /// <summary>
    /// Check buffered certificates, if all it's predecessors are received.
    /// </summary>
    public void CheckCertificates()
    {
        certificateCheckTempList.Clear();

        if (certificatesBuffer.Count == 0)
            stats.noCertifcatetoCheckCount++;

        while (certificatesBuffer.Count > 0)
        {
            var cert = certificatesBuffer.Dequeue();

            // if the corresponding Vertexblock is not received, 
            var block = GetBlock(cert.round, cert.source);
            if (cert.round >= receivedBlocks.Count || block is null)
            {
                // put it back to the buffer
                certificateCheckTempList.Add(cert);

                continue;
            }

            bool missingPrev = false;
            // check if all predecessors certificates are received
            foreach (var prev in block.prevCertificates)
            {
                // if one of the predecessor is not received, put it back to the buffer
                if (!certificatesIndexedByHash.ContainsKey(prev))
                {
                    // print the hash of the predecessor
                    logger.LogTrace("Certificate at round {r} from {s} has missing predecessor", cert.round, cert.source);
                    stats.missingPredecessorCount++;

                    certificateCheckTempList.Add(cert);
                    missingPrev = true;
                    break;
                }
            }

            if (missingPrev)
                continue;

            // index block and certificate using block hash
            // block is only indexed if it has a certificate
            // block and certificate should only be added once
            block.certificate ??= cert;

            bool success;
            if (cert.source != self.nodeid)
            {
                success = blocksIndexedByHash.TryAdd(cert.blockHash, block);
                if (!success)
                    logger.LogDebug("block of round {r} from {s} already added", cert.round, cert.source);
                Debug.Assert(success);
            }

            success = certificatesIndexedByHash.TryAdd(cert.blockHash, cert);
            if (!success)
                logger.LogDebug("cert of round {r} from {s} already added", cert.round, cert.source);
            Debug.Assert(success);

            if (cert.round == currentRoundState.round)
            {
                // cert should only be added once
                Debug.Assert(!currentRoundState.receivedCertificated.Contains(cert),
                    $"Node {self.nodeid} cert from {cert.source} of round {cert.round} already added");
                currentRoundState.receivedCertificated.Add(cert);
            }

            if (receivedBlockEvent is not null)
                receivedBlockEvent(block);

        }

        // add remaining certificates back to the buffer
        foreach (var cert in certificateCheckTempList)
            certificatesBuffer.Enqueue(cert, cert.round);

        logger.LogTrace("Counted {c} certificates for round {r}", currentRoundState.receivedCertificated.Count, currentRoundState.round);

        if (currentRoundState.receivedCertificated.Count >= 2 * f + 1)
        {
            AdvanceRound();
        }
        else
        {
            stats.notEnoughCertToAdvanceCount++;
        }

        if (!currentRoundState.didConsensus &&
            consensus is not null &&
            consensus.isLastRoundOfWave(currentRoundState.round) &&
            currentRoundState.round != 0)
        {
            consensus.GetWave(currentRoundState.round, out int wave);
            // compute time to do this function
            WaveDecide(wave);
        }
    }


    // Query for a block if a certificate is received but the block is not
    private void QueryForBlock(Certificate certificateBlock)
    {
        logger.LogTrace("Querying for vertex block from node {s} at round {r}",
            certificateBlock.source,
            certificateBlock.round);

        // get list of nodes that have the block and store in an array
        int[] nodes = new int[replicas.Count];
        replicas.Keys.CopyTo(nodes, 0);
        msgSender.QueryForBlock(certificateBlock.blockHash, nodes);
        stats.sentBlockQueryMsgCount++;


    }

    bool catchup = false;
    // go to next round and clear relevant states
    private void AdvanceRound()
    {
        stats.callingAdvanceRoundCount += 1;
        if (!active)
            return;

        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool jumpingRoundFlag = false;

        VertexBlock block;
        List<UpdateMessage> updatesList = null;
        List<byte[]> prevCertificates = null;

        if (catchup)
        {
            goto AdditionalOps;
        }


        // Nodes without any updates to advance slower:
        // to prevent nodes with updates to be starved since they cannot generate new blocks 
        // fast enough.
        if (updatesBuffer.Count != 0)
        {
            minimalNewBlockInterval = Math.Min(1000 / updatesBuffer.Count, 1000);
        }
        else if (currentRoundState.currentRoundBlock is null) // jumped round
        {
            minimalNewBlockInterval = 1;
        }
        else
        {
            minimalNewBlockInterval = 1000;
        }

        lock (updatesBuffer)
        {
            if (currentRoundState.round > 0 &&
                lastBlockCreationTime + minimalNewBlockInterval > currentTimestamp)
            {
                logger.LogTrace("Not advancing round, because not enough updates in buffer and not enough time has passed");
                stats.noAdvanceRoundBecauseNotThereYetCount++;
                return;
            }

            logger.LogTrace("Advancing round now");


            // discard previous round's blocks if it does not received enough acks
            // ofc round 0 has no previous round
            if (currentRoundState.round > 0 && currentRoundState.currentRoundBlock != null && currentRoundState.currentRoundBlock.certificate == null)
            {
                logger.LogTrace("Discarding block at round {r}", currentRoundState.round);
                // to make sure the original order of updates are persevered
                LinkedListNode<UpdateMessage> lastmsg = null;
                foreach (var msg in currentRoundState.currentRoundBlock.updateMsgs)
                {
                    if (lastmsg is null)
                        lastmsg = updatesBuffer.AddFirst(msg);
                    else
                    {
                        updatesBuffer.AddAfter(lastmsg, msg);
                        lastmsg = lastmsg.Next;
                    }
                }

                //if (currentRoundState.round < latestCert)
                jumpingRoundFlag = true;
                
                // if discarding blocks, means you are too slow
                maxBatchSize = Math.Max(maxBatchSize / 5, 1);
            }
            else
            {
                if (maxBatchSize < updatesBuffer.Count)
                {
                    maxBatchSize = Math.Min(maxBatchSize * 2, updatesBuffer.Count);
                }
            }


            if (!jumpingRoundFlag)
            {
                // convert the updates buffer to a list to be used in the next round block
                // updatesList = updatesBuffer.Count > 0 ? updatesBuffer.ToList() : new List<UpdateMessage>();
                // updatesBuffer.Clear();
                updatesList = new List<UpdateMessage>(maxBatchSize);
                int count = 0;
                while (updatesBuffer.Count > 0 && count < maxBatchSize)
                {
                    updatesList.Add(updatesBuffer.First.Value);
                    updatesBuffer.RemoveFirst();
                    count++;
                }

                // gather current round's certificates to be the prev certificates of the next round block
                prevCertificates = new List<byte[]>(currentRoundState.receivedCertificated.Count);
                foreach (var cert in currentRoundState.receivedCertificated)
                    prevCertificates.Add(cert.blockHash);

                // reset current round state
                currentRoundState.AdvanceRound(currentRoundState.round + 1);
            }
            else
            {
                // if jumping round, advance to the latest cert
                // no new block is created, simply wait for the next round

                currentRoundState.AdvanceRound(latestCert);
                logger.LogTrace("Jumping to round {latestCert}", latestCert);
                
                stats.jumpingRoundCount++;
                catchup = true;
            }
        }

        AdditionalOps:

        if (catchup)
        {
            currentRoundState.AdvanceRound(currentRoundState.round + 1);
            catchup = false;
        }
        

        // if receivedBlocks do not have current round yet
        while (receivedBlocks.Count <= currentRoundState.round)
            receivedBlocks.Add(new ConcurrentDictionary<int, VertexBlock>(replicas.Count, replicas.Count));

        // if received certificates ahead
        if (currentRoundState.round < receivedBlocks.Count)
        {
            foreach (var b in receivedBlocks[currentRoundState.round].Values)
            {
                // only if the block is indexed
                if (blocksIndexedByHash.TryGetValue(b.digest, out _))
                {
                    currentRoundState.receivedCertificated.Add(b.certificate);
                }
            }
        }

        logger.LogTrace("Advanced to round {r} current {bs}", currentRoundState.round, maxBatchSize);


        if (!jumpingRoundFlag)
        {
            // creating new block
            block = CreateBlock(prevCertificates, updatesList);
            currentRoundState.currentRoundBlock = block;

            msgSender.BroadcastBlock(block);
            lastBlockCreationTime = currentTimestamp;
            logger.LogTrace("Broadcasted block at round {r} with {count} updates", block.round, block.updateMsgs.Count);
            stats.sentBlockMsgCount += 1;            
        }

        return;
    }

    // get a block from receivedBlocks, thread safe
    public VertexBlock GetBlock(int round, int nodeid)
    {
        bool lockTaken = false;

        try
        {
            spinLock0.Enter(ref lockTaken);

            if (round < receivedBlocks.Count)
            {
                if (receivedBlocks[round].TryGetValue(nodeid, out VertexBlock b))
                    return b;
            }

            return null;
        }
        finally
        {
            if (lockTaken)
                spinLock0.Exit();
        }
    }

    // get all blocks of a round, thread safe
    public ConcurrentDictionary<int, VertexBlock> GetBlocks(int round)
    {
        bool lockTaken = false;

        try
        {
            spinLock0.Enter(ref lockTaken);

            if (round < receivedBlocks.Count)
            {
                return receivedBlocks[round];
            }

            return null;
        }
        finally
        {
            if (lockTaken)
                spinLock0.Exit();
        }
    }

    // create a new block
    private VertexBlock CreateBlock(List<byte[]> prevCertificates, List<UpdateMessage> updatesList)
    {
        VertexBlock block = new(currentRoundState.round, self.nodeid, prevCertificates, updatesList);
        block.Sign(self.privateKey);
        receivedBlocks[currentRoundState.round].TryAdd(self.nodeid, block);
        //blocksIndexedByHash[block.digest] = block;
        currentRoundState.currentRoundBlock = block;
        currentRoundState.receivedAcks.TryAdd(self.nodeid, block.signature);

        return block;
    }

    // add a block to receivedBlocks, thread safe
    private void AddBlock(VertexBlock block)
    {
        bool lockTaken = false;

        try
        {
            spinLock0.Enter(ref lockTaken);
            // if the block is from future round, create new ones in receivedBlocks until it's there
            while (receivedBlocks.Count <= block.round)
                receivedBlocks.Add(new ConcurrentDictionary<int, VertexBlock>(replicas.Count, replicas.Count));

            receivedBlocks[block.round][block.source] = block;
        }
        finally
        {
            if (lockTaken)
                spinLock0.Exit();
        }
    }

    // An implementation of Tusk consensus algorithm
    private void WaveDecide(int wave)
    {
        currentRoundState.didConsensus = true;
        consensus.Commit(wave);
        stats.numConsens++;
    }

    // create a signature for the block
    private void AcknowledgeBlock(VertexBlock block)
    {
        byte[] sig = block.ComputeSignature(self.privateKey);
        msgSender.SendSignature(block.round, sig, block.source);
        stats.sentAckMsgCount++;
    }

    private void CheckTimeout()
    {
        try 
        {
            canCheckCertLock.AcquireWriterLock(200);

            try
            {
                CheckCertificates();
            }
            finally
            {
                canCheckCertLock.ReleaseWriterLock();
            }

        } 
        catch (ApplicationException)
        {
            // do nothing
            logger.LogTrace("Check certificate lock timeout");
            stats.checkCertifcateLockTimeoutCount += 1;
            return;
        }
    }

    private void CheckTimeOutLoop()
    {
        // a background thread that checks for timeout
        while (active)
        {
            stats.certificateByTimeout += 1;
            CheckTimeout();
            Thread.Sleep(50);
        }
    }

    private void GarbageCollect()
    {

    }
}