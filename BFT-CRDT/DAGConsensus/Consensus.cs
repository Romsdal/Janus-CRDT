
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BFTCRDT;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BFTCRDT.DAG;


public interface IDAGConsensus
{

}

public delegate void ConsensusCompleteEvent(List<List<UpdateMessage>> updates);

public class Consensus : IDAGConsensus
{
    private DAG dag;
    public List<Certificate> orderedBlocks;
    public int lastCommittedWave { set; get; }
    private Stack<Certificate> leaderStack;

    public ConsensusCompleteEvent consensusCompleteEvent { set; get; }
    
    private ILogger logger;
    //internal Stats stats;

    public Consensus(DAG dag, ILogger logger = null)
    {
        this.dag = dag;
        this.orderedBlocks = new();
        this.leaderStack = new();

        this.logger = logger ?? new NullLogger<Consensus>();
    }

    /// <summary>
    /// Return the position in a wave.
    /// </summary>
    /// <param name="round">input round</param>
    /// <param name="wave">the wave the round is in</param>
    /// <returns>the index of round in current wave</returns>
    public int GetWave(int round, out int wave)
    {
        wave = round / 2;
        return round % 3;
    }

    /// <summary>
    /// Return the first round of a wave
    /// </summary>
    /// <param name="wave"></param>
    /// <returns></returns>
    public int GetRound(int wave)
    {
        return wave * 2 - 2;
    }

    public bool isLastRoundOfWave(int round)
    {
        return round % 2 == 0;
    }

    /// <summary>
    /// Since the common-coin has no overhead, we here use a deterministic method for the leader selection for 
    /// simplicity. Plus, Narwhal didn't implement the common-coin either.
    /// </summary>
    /// <param name="wave"></param>
    /// <returns></returns>
    public int GetLeader(int wave)
    {
        // get a random number with seed
        int random = new Random(wave).Next();
        
        return random % dag.replicas.Count;
    }

    public void Commit(int wave)
    {   
        int r = GetRound(wave);
        var leader = dag.GetBlock(r, GetLeader(wave));
        
        if (leader is null || leader.certificate is null || !CheckEnoughSupport(leader))
            return;
            
        logger.LogTrace("Starting consensus at node {n} round {r} with leader {source}", dag.self.nodeid, r, leader.source);

        leaderStack.Push(leader.certificate);

        var currLeader = leader.certificate;

        for (int w = wave-1; w > lastCommittedWave; w--)
        {
            var prevLeader = dag.GetBlock(GetRound(w), GetLeader(w));
            if (!(prevLeader is null || prevLeader.certificate is null))
            {
                    // if there is a path from curr leader to prev leader.
                if (Path(currLeader, prevLeader.certificate))
                {
                    leaderStack.Push(prevLeader.certificate);
                    currLeader = prevLeader.certificate;
                }
            }
        }

        lastCommittedWave = wave;

        List<Certificate> toDeliver = new();
        // order vertices
        while (leaderStack.Count > 0)
        {
            var l = leaderStack.Pop();
            toDeliver.AddRange(Order(TraverseDAG(l)));
        } 

        orderedBlocks.AddRange(toDeliver);

        // store all toDeliver block in the form of {round}-{source} in a string
        //string toDeliverString = string.Join(",", toDeliver.Select(x => $"{x.round}-{x.source}"));
        //logger.LogTrace($"Consensus round {GetRound(wave)} with leader {leader.source} completed. Blocks: {toDeliverString}");
        
        if (this.consensusCompleteEvent is not null)
        {
            List<List<UpdateMessage>> updates = new();
            foreach (var cert in toDeliver)
            {
                var v = dag.blocksIndexedByHash[cert.blockHash];
                updates.Add(v.updateMsgs);
                //stats.numUpdatesfinishedConsensus += v.updateMsgs.Count;
            }

            consensusCompleteEvent(updates);
        }
    }

    /// <summary>
    /// Conduct depth-first traversal to find if there is a path
    /// </summary>
    /// <param name="start"></param>
    /// <param name="end"></param>
    /// <returns></returns>
    public bool Path(Certificate start, Certificate end)
    {
        if (start.round <= end.round)
            throw new Exception("Start round must be greater than r");

        HashSet<Certificate> visited = new();
        Stack<Certificate> stack = new();

        stack.Push(start);

        while (stack.Count > 0)
        {
            var cert = stack.Pop();

            if (cert == end)
                return true;
            
            if (cert.committed || visited.Contains(cert) || cert.round <= end.round)
                continue;
            
            visited.Add(cert);

            foreach (var hash in dag.blocksIndexedByHash[cert.blockHash].prevCertificates)
                stack.Push(dag.certificatesIndexedByHash[hash]);
        }

        return false;
    }

    public PriorityQueue<Certificate, int> TraverseDAG(Certificate start)
    {   

        PriorityQueue<Certificate, int> results = new();
        Stack<Certificate> stack = new();

        stack.Push(start);


        while (stack.Count > 0)
        {
            var cert = stack.Pop();
            
            if (cert.committed)
                continue;
            
            results.Enqueue(cert, cert.round);
            cert.committed = true;
            
            foreach (var hash in dag.blocksIndexedByHash[cert.blockHash].prevCertificates)
            {
                if (dag.certificatesIndexedByHash.TryGetValue(hash, out var prevCert))
                    stack.Push(prevCert);
                else
                {
                    logger.LogError("Cannot find predecessor for block at round {round} and source {source}", cert.round, cert.source);
                    throw new Exception("Cannot find predecessor");
                }
            }
        }

        return results;

    }

    private bool CheckEnoughSupport(VertexBlock b)
    {
        int numSupport = 0;
        foreach (var next in dag.GetBlocks(b.round + 1).Values)
        {
            if (!(next.certificate is null) && next.prevCertificates.Any(x => x.SequenceEqual(b.digest)))
                numSupport++;
        }


        if (numSupport >= 2 * dag.f + 1)
            return true;

        return false;
    }
    
    public List<Certificate> Order(PriorityQueue<Certificate, int> toCommit)
    {
        List<Certificate> results = new(toCommit.Count);

        List<Certificate> temp = new();
        if (!toCommit.TryPeek(out _, out int lastRound))
            return results;
            
        while (toCommit.TryDequeue(out Certificate b, out int round))
        {
            if (round != lastRound)
            {
                temp.Sort((x, y) => x.source.CompareTo(y.source));
                results.AddRange(temp);
                temp.Clear();
                lastRound = round;
            }
            
            temp.Add(b);
        }

        // add the remaining temps
        if (temp.Count > 0)
        {
            temp.Sort((x, y) => x.source.CompareTo(y.source));
            results.AddRange(temp);
        }

        return results;
    }
}