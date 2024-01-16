using Xunit;
using System;
using BFTCRDT;
using BFTCRDT.DAG;
using System.Collections.Generic;
using System.Threading.Tasks;
using MergeSharp;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Concurrent;
using Planetarium.Cryptography.BLS12_381;

namespace Tests;

[Collection("Network")]
public class KVStoreTests : IDisposable
{
    List<ILogger> loggers = new();
    List<ConfigParser> configs = new();
    List<Cluster> clusters = new();
    List<DAG> dags = new();
    List<ConnectionManager> cms = new();
    List<ReplicationManager> rms = new();
    List<KeySpaceManager> ksms = new();
    
    volatile bool safeUpdateComplete = false;

    int numNodes = 4;


    public KVStoreTests()
    {
        loggers = new();
        for (int i = 0; i < 4; i++)
        {

            var lvl = LogLevel.None;
            if (i == 0)
            {
                lvl = LogLevel.None;
            }
            loggers.Add(LoggerFactory.Create(builder =>
            {
                LogLevel logLevel = lvl;
                builder.AddConsole().SetMinimumLevel(logLevel);
                builder.AddDebug().SetMinimumLevel(logLevel);

            }).CreateLogger("Node" + i));
        }


        for (int i = 0; i < numNodes; i++)
        {
            configs.Add(new("../../../TestAssets/FourNodes/test_cluster." + i + ".json"));
        }

        for (int i = 0; i < numNodes; i++)
        {
            clusters.Add(configs[i].GetCluster(loggers[i]));
        }

        for (int i = 0; i < numNodes; i++)
        {
            dags.Add(CreateDAG(clusters[i], loggers[i]));
            Consensus consensus = new(dags[i], loggers[i]);
            dags[i].consensus = consensus;
        }
            

        for (int i = 0; i < numNodes; i++)
        {
            cms.Add(new(clusters[i], dags[i], loggers[i]));
        }

        ConcurrentBag<(int, ReplicationManager)> temp = new();

        Parallel.ForEach(cms, cm =>
        {
            temp.Add((cm.dag.self.nodeid, new(cm)));
        });

        rms = temp.OrderBy(x => x.Item1).Select(x => x.Item2).ToList();
        


        for (int i = 0; i < numNodes; i++)
        {
            ksms.Add(new(configs[i].selfNode.nodeid, configs[i].isPrimary(), cms[i], rms[i], loggers[i]));
            ksms[i].safeCRDTManager.safeUpdateCompleteClientNotifier += NotifySafeUpdateComplete;
        }
    }

    [Fact]
    public void TestInit()
    {
        // Test if all of them are init
        foreach (var c in cms)
        {
            Assert.True(c.cluster.isConnected);
        }

        Parallel.ForEach(cms, cm =>
        {
            cm.StartDAG();
        });

        System.Threading.Thread.Sleep(200);

        foreach (var dag in dags)
        {
            Assert.True(dag.isInit);
        }

        Parallel.ForEach(ksms, ksm =>
        {
            ksm.InitializeKeySpace();
        });

        System.Threading.Thread.Sleep(200);

        foreach (var ksm in ksms)
        {
            Assert.True(ksm.keySpaceInitialized);
        }

    }


    [Fact]
    public void TestCreateCRDT()
    {
        TestInit();

        ksms[0].CreateNewKVPair<PNCounter>("test");

        System.Threading.Thread.Sleep(1000);
        for (int j = 0; j < numNodes; j++)
        {
            ksms[j].GetKVPair("test", out SafeCRDT value);
            if (value is null)
                Console.WriteLine("Failed to get CRDT from node " + j);
            Assert.NotNull(value);

        }

    }

    [Fact]
    public void TestSimpleIncrement()
    {
        TestCreateCRDT();

        var kms0 = ksms[0];
        kms0.GetKVPair("test", out SafeCRDT value0);

        value0.Update("Increment", new object[] { 5 }, false);
        int p0 = (int) value0.QueryProspective();
        
        System.Threading.Thread.Sleep(500);
        foreach (var k in ksms)
        {
            k.GetKVPair("test", out SafeCRDT value);
            int p = (int) value.QueryProspective();
            Assert.Equal(p0, p);
        }
    }

    [Fact]
    public void TestCreateMultipleCRDTs()
    {
        TestCreateCRDT();

        var kms0 = ksms[0];
        List<SafeCRDT> values = new();

        for (int i = 0; i < 100; i++)
        {
            kms0.CreateNewKVPair<PNCounter>("test" + i);
        }

        System.Threading.Thread.Sleep(500);
        foreach (var k in ksms)
        {
            for (int i = 0; i < 100; i++)
            {
                k.GetKVPair("test" + i, out SafeCRDT value);
                Assert.NotNull(value);
            }
        }

        loggers[0].LogDebug("Created 100 CRDTs");
    }

    [Fact]
    public void TestMultipleIncrement()
    {
        TestCreateMultipleCRDTs();

        var kms0 = ksms[0];
        List<SafeCRDT> r0values = new();

        for (int i = 0; i < 100; i++)
        {
            kms0.GetKVPair("test" + i, out SafeCRDT r0value_i);
            r0values.Add(r0value_i);
        }

        for (int i = 0; i < 100; i++)
        {
            for (int j = 0; j < 10; j++)
            {
                Random rnd = new();
                int r = rnd.Next(0, 100);
                r0values[i].Update("Increment", new object[] { r }, false);
            }
        }

        System.Threading.Thread.Sleep(3000);

        foreach (var k in ksms)
        {
            for (int i = 0; i < 100; i++)
            {
                k.GetKVPair("test" + i, out SafeCRDT rk_value_i);
                int p = (int) rk_value_i.QueryProspective();
                Assert.Equal(r0values[i].QueryProspective(), p);
            }
        }

    }

    [Fact]
    public void TestStableConverge()
    {
        TestCreateCRDT();

        var kms0 = ksms[0];
        kms0.GetKVPair("test", out SafeCRDT value0);

        value0.Update("Increment", new object[] { 5 }, false);
        int p0 = (int) value0.QueryProspective();
        
        System.Threading.Thread.Sleep(1000);
        foreach (var k in ksms)
        {
            k.GetKVPair("test", out SafeCRDT value);
            int p = (int) value.QueryProspective();
            Assert.Equal(p0, p);
            int s = (int) value.QueryStable();
            Assert.Equal(p, s);
        }
    }

    [Fact]
    public void TestMultipleConverge()
    {        
        TestCreateMultipleCRDTs();

        var kms0 = ksms[0];
        List<SafeCRDT> r0values = new();

        for (int i = 0; i < 100; i++)
        {
            kms0.GetKVPair("test" + i, out SafeCRDT r0value_i);
            r0values.Add(r0value_i);
        }

        for (int i = 0; i < 100; i++)
        {
            for (int j = 0; j < 10; j++)
            {
                Random rnd = new();
                int r = rnd.Next(0, 100);
                r0values[i].Update("Increment", new object[] { r }, false);
            }
        }

        System.Threading.Thread.Sleep(1000);

        foreach (var k in ksms)
        {
            for (int i = 0; i < 100; i++)
            {
                k.GetKVPair("test" + i, out SafeCRDT rk_value_i);
                int p = (int) rk_value_i.QueryProspective();
                Assert.Equal(r0values[i].QueryProspective(), p);
                int s = (int) rk_value_i.QueryStable();
                Assert.Equal(p, s);
            }
        }

    }

    [Fact]
    public void TestSafeUpdate()
    {
        TestCreateCRDT();

        var kms0 = ksms[0];
        kms0.GetKVPair("test", out SafeCRDT value0);

        value0.Update("Increment", new object[] { 5 }, false);
        int p = (int) value0.QueryProspective();
        int s = (int) value0.QueryStable();
        // immediately after update, prospective and stable should be different
        Assert.NotEqual(p, s);

        value0.Update("Increment", new object[] { 3 }, true, (null, 1));
        
        // should be equal since it blocks until consensus is complete
        for (int i = 0; i < 50; i++)
        {
            if (safeUpdateComplete)
                break;
            else if (i == 49 && !safeUpdateComplete)
                Assert.True(false);
            
            System.Threading.Thread.Sleep(100);
        }

        p = (int) value0.QueryProspective();
        s = (int) value0.QueryStable();
        Assert.Equal(p, s);
    }

    [Fact]
    public void TestMultipleSafeUpdate()
    {
        TestCreateMultipleCRDTs();

        Random rnd = new();
        uint x = 1;
        foreach (var k in ksms)
        {
            for (int i = 0; i < 10; i++)
            {
                k.GetKVPair("test" + i, out SafeCRDT rk_value_i);
                // get random number
                Assert.NotNull(rk_value_i.Update("Increment", new object[] { rnd.Next(1, 100) }, true, (null, x)));
                x++;

                // should be equal since it blocks until consensus is complete
                for (int j = 0; j < 50; j++)
                {
                    if (safeUpdateComplete)
                        break;
                    else if (j == 49 && !safeUpdateComplete)
                        Assert.True(false);
                    
                    System.Threading.Thread.Sleep(100);
                }

                var p = (int) rk_value_i.QueryProspective();
                var s = (int) rk_value_i.QueryStable();
                // should be equal since it blocks until consensus is complete
                Assert.Equal(p, s);
                safeUpdateComplete = false;
            }
        }
    }

    private void NotifySafeUpdateComplete((Connection, uint) origin)
    {
        safeUpdateComplete = true;
    }

    private DAG CreateDAG(Cluster cluster, ILogger logger)
    {
        List<Replica> replicas = Replica.GenerateReplicas(cluster.nodes.Keys.ToList(), cluster.self.nodeid, out Replica selfReplica);
        DAGMsgSender msgSender = new(cluster.self.nodeid, cluster.GetNodesList(), logger);
        DAG dag = new(selfReplica, replicas, msgSender, logger);

        Consensus consensus = new(dag, logger);
        dag.consensus = consensus;

        return dag;
    }


    public void Dispose()
    {
        Parallel.ForEach(cms, cm =>
        {
            cm.Stop();
            cm.Dispose();
        });
    }
}
