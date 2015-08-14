﻿// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Kafka.Network;
using Kafka.Protocol;
using Kafka.Public;
using Kafka.Routing;

namespace Kafka.Cluster
{
    using RouterFactory = Func<IProduceRouter>;
    using NodeFactory = Func<string, int, INode>;

    interface ICluster
    {
        Task<RoutingTable> RequireNewRoutingTable();
        Statistics Statistics { get; }
    }

    class DevNullLogger : ILogger
    {
        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }

    class Cluster : ICluster
    {
        enum MessageType
        {
            Metadata,
            NodeEvent
        }

        [StructLayout(LayoutKind.Explicit)]
        struct MessageValue
        {
            [FieldOffset(0)]
            public TaskCompletionSource<RoutingTable> Promise;

            [FieldOffset(0)]
            public Action NodeEventProcessing;
        }

        struct ClusterMessage
        {
            public MessageType MessageType;
            public MessageValue MessageValue;
        }

        private readonly NodeFactory _nodeFactory;
        private readonly Dictionary<INode, BrokerMeta> _nodes = new Dictionary<INode, BrokerMeta>();
        private readonly Dictionary<int, INode> _nodesById = new Dictionary<int, INode>();
        private readonly Dictionary<string, INode> _nodesByHostPort = new Dictionary<string, INode>();
        private readonly ActionBlock<ClusterMessage> _agent;
        private readonly Random _random = new Random((int)(DateTime.Now.Ticks & 0xffffffff));
        private readonly string _seeds;

        private Timer _refreshMetadataTimer;
        private bool _started;
        private RoutingTable _routingTable;

        private long _successfulSent;
        private long _requestsSent;
        private long _responseReceived;
        private long _errors;
        private long _nodeDead;
        private long _expired;
        private long _discarded;
        private long _exited;
        private double _resolution = 1000.0;

        public Statistics Statistics
        {
            get
            {
                return new Statistics
                {
                    SuccessfulSent = _successfulSent,
                    RequestSent = _requestsSent,
                    ResponseReceived = _responseReceived,
                    Errors = _errors,
                    NodeDead = _nodeDead,
                    Expired = _expired,
                    Discarded = _discarded,
                    Exit = _exited
                };
            }
        }

        public IProduceRouter ProduceRouter { get; private set; }
        public ILogger Logger { get; private set; }

        public event Action<Exception> InternalError = _ => { };

        internal long PassedThrough
        {
            get { return _exited; }
        }

        public Cluster() : this(new Configuration(), new DevNullLogger(), null, null)
        {
        }

        public Cluster(Configuration configuration, ILogger logger) : this(configuration, logger, null, null)
        {
        }

        public Cluster(Configuration configuration, ILogger logger, NodeFactory nodeFactory, RouterFactory routerFactory)
        {
            _seeds = configuration.Seeds;

            Logger = logger;

            ProduceRouter = routerFactory != null ? routerFactory() : new ProduceRouter(this, configuration);
            ProduceRouter.MessageExpired += _ =>
            {
                Interlocked.Increment(ref _expired);
                Interlocked.Increment(ref _exited);
            };
            ProduceRouter.MessagesAcknowledged += (t, c) =>
            {
                Interlocked.Add(ref _successfulSent, c);
                Interlocked.Add(ref _exited, c);
            };
            ProduceRouter.MessagesDiscarded += (t, c) =>
            {
                Interlocked.Add(ref _discarded, c);
                Interlocked.Add(ref _exited, c);
            };

            var clientId = Encoding.UTF8.GetBytes(configuration.ClientId);
            var serializer = new Node.Serializer(clientId, configuration.RequiredAcks, configuration.RequestTimeoutMs,
                                                 configuration.CompressionCodec);
            _nodeFactory = nodeFactory ??
                           ((h, p) =>
                            new Node(string.Format("[{0}:{1}]", h, p),
                                     () =>
                                     new Connection(h, p, configuration.SendBufferSize, configuration.ReceiveBufferSize),
                                     serializer,
                                     configuration).SetResolution(_resolution));
            _nodeFactory = DecorateFactory(_nodeFactory);

            _agent = new ActionBlock<ClusterMessage>(r => ProcessMessage(r));

            // Bootstrap
            BuildNodesFromSeeds();
            if (_nodes.Count == 0)
            {
                throw new ArgumentException("Invalid seeds: " + _seeds);
            }
        }

        public Cluster SetResolution(double resolution)
        {
            _resolution = resolution;
            return this;
        }

        private void RefreshMetadata()
        {
            _agent.Post(new ClusterMessage {MessageType = MessageType.Metadata});
        }

        private NodeFactory DecorateFactory(NodeFactory nodeFactory)
        {
            return (h, p) => ObserveNode(nodeFactory(h, p));
        }

        // Connect all the INode events.
        private INode ObserveNode(INode node)
        {
            node.Dead += n => OnNodeEvent(() => ProcessDeadNode(n));
            node.ConnectionError += (n, e) => OnNodeEvent(() => ProcessNodeError(n, e));
            node.DecodeError += (n, e) => OnNodeEvent(() => ProcessDecodeError(n, e));
            node.RequestSent += _ => Interlocked.Increment(ref _requestsSent);
            node.ResponseReceived += _ => Interlocked.Increment(ref _responseReceived);
            node.Connected +=
                n => OnNodeEvent(() => Logger.LogInformation(string.Format("Connected to {0}", GetNodeName(n))));
            node.ProduceAcknowledgement += (n, ack) => ProduceRouter.Acknowledge(ack);
            return node;
        }

        // Events processing are serialized on to the internal actor
        // to avoid concurrency management.
        private void OnNodeEvent(Action processing)
        {
            _agent.Post(new ClusterMessage
            {
                MessageType = MessageType.NodeEvent,
                MessageValue = new MessageValue { NodeEventProcessing = processing }
            });
        }

        private const string UnknownNode = "[Unknown]";

        private string GetNodeName(INode node)
        {
            BrokerMeta bm;
            return _nodes.TryGetValue(node, out bm) ? bm.ToString() : UnknownNode;
        }

        private void ProcessDecodeError(INode node, Exception exception)
        {
            Interlocked.Increment(ref _errors);
            Logger.LogError(string.Format("A response could not be decoded for the node {0}: {1}", GetNodeName(node), exception));
        }

        private void ProcessNodeError(INode node, Exception exception)
        {
            Interlocked.Increment(ref _errors);
            var ex = exception as TransportException;
            var n = GetNodeName(node);
            if (ex != null)
            {
                switch (ex.Error)
                {
                    case TransportError.ConnectError:
                        Logger.LogWarning(string.Format("Failed to connect to {0}, retrying.", n));
                        break;

                    case TransportError.ReadError:
                    case TransportError.WriteError:
                        Logger.LogError(string.Format("Transport error to {0}: {1}", n, ex));
                        break;
                }
            }
            else
            {
                Logger.LogError(string.Format("An error occured in node {0}: {1}", n, exception));
            }
        }

        private static string BuildKey(string host, int port)
        {
            return host + ':' + port;
        }

        private void BuildNodesFromSeeds()
        {
            foreach (var seed in _seeds.Split(new []{','}, StringSplitOptions.RemoveEmptyEntries))
            {
                var hostPort = seed.Split(':');
                var broker = new BrokerMeta { Host = hostPort[0], Port = int.Parse(hostPort[1]) };
                var node = _nodeFactory(broker.Host, broker.Port);
                _nodes[node] = broker;
                _nodesByHostPort[BuildKey(broker.Host, broker.Port)] = node;
            }
        }

        public void Start()
        {
            Logger.LogInformation("Bootstraping with " + _seeds);
            RefreshMetadata();
            _refreshMetadataTimer = new Timer(_ => RefreshMetadata(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            _started = true;
        }

        public async Task Stop()
        {
            if (!_started) return;
            _refreshMetadataTimer.Dispose();
            await ProduceRouter.Stop();
            _agent.Complete();
            await _agent.Completion;
            foreach (var node in _nodes.Keys)
            {
                await node.Stop();
            }
            _started = false;
        }

        public Task<RoutingTable> RequireNewRoutingTable()
        {
            var promise = new TaskCompletionSource<RoutingTable>();
            _agent.Post(new ClusterMessage
                {
                    MessageType = MessageType.Metadata,
                    MessageValue = new MessageValue {Promise = promise}
                });
            return promise.Task;
        }

        private void CheckNoMoreNodes()
        {
            if (_nodes.Count == 0)
            {
                Logger.LogError("All nodes are dead, retrying from bootstrap seeds.");
                BuildNodesFromSeeds();
            }
        }

        private void ProcessDeadNode(INode deadNode)
        {
            Interlocked.Increment(ref _nodeDead);
            BrokerMeta m;
            if (!_nodes.TryGetValue(deadNode, out m))
            {
                Logger.LogError(string.Format("Kafka unknown node dead, the node makes itself known as: {0}.",
                                              deadNode.Name));
                return;
            }
            Logger.LogError(string.Format("Kafka node {0} is dead, refreshing metadata.", GetNodeName(deadNode)));
            _nodes.Remove(deadNode);
            _nodesByHostPort.Remove(BuildKey(m.Host, m.Port));
            _nodesById.Remove(m.Id);
            CheckNoMoreNodes();
            RefreshMetadata();
        }

        private async Task ProcessMessage(ClusterMessage message)
        {
            // Event occured on a node
            if (message.MessageType == MessageType.NodeEvent)
            {
                message.MessageValue.NodeEventProcessing();
                return;
            }

            // Metadata required
            try
            {
                Logger.LogInformation("Fetching metadata...");
                var node = _nodes.Keys.ElementAt(_random.Next(_nodes.Count));
                var response = await node.FetchMetadata();
                Logger.LogInformation("[Metadata][Brokers] " + string.Join("/", response.BrokersMeta.Select(bm => bm.ToString())));
                Logger.LogInformation("[Metadata][Topics] " +
                                      string.Join(" | ",
                                                  response.TopicsMeta.Select(
                                                      tm =>
                                                      tm.Partitions.Aggregate(tm.TopicName + ":" + tm.ErrorCode,
                                                                              (s, pm) =>
                                                                              s + " " +
                                                                              string.Join(":", pm.Id, pm.Leader,
                                                                                          pm.ErrorCode)))));
                ResponseToTopology(response);
                ResponseToRoutingTable(response);
                if (message.MessageValue.Promise == null)
                {
                    ProduceRouter.ChangeRoutingTable(_routingTable);
                }
                else
                {
                    message.MessageValue.Promise.SetResult(_routingTable);
                }
                CheckNoMoreNodes();
            }
            catch (OperationCanceledException ex)
            {
                if (message.MessageValue.Promise != null)
                {
                    message.MessageValue.Promise.SetException(ex);
                }
            }
            catch (Exception ex)
            {
                if (message.MessageValue.Promise != null)
                {
                    message.MessageValue.Promise.SetCanceled();
                }
                InternalError(ex);
            }
        }

        private readonly HashSet<string> _tmpNewNodes = new HashSet<string>();
        private readonly HashSet<int> _tmpNewNodeIds = new HashSet<int>();

        private void ResponseToTopology(MetadataResponse response)
        {
            // New stuff
            foreach (var bm in response.BrokersMeta)
            {
                var hostPort = BuildKey(bm.Host, bm.Port);
                _tmpNewNodes.Add(hostPort);
                _tmpNewNodeIds.Add(bm.Id);
                INode node;
                if (!_nodesByHostPort.TryGetValue(hostPort, out node))
                {
                    node = _nodeFactory(bm.Host, bm.Port);
                    _nodesByHostPort[hostPort] = node;
                }
                if (!_nodes.ContainsKey(node))
                {
                    _nodes[node] = bm;
                }
                _nodes[node].Id = bm.Id;
                _nodesById[bm.Id] = node;
            }

            // Clean old
            var idToClean = _nodesById.Keys.Where(id => !_tmpNewNodeIds.Contains(id)).ToList();
            foreach (var id in idToClean)
            {
                _nodesById.Remove(id);
            }

            var hostToClean = _nodesByHostPort.Keys.Where(host => !_tmpNewNodes.Contains(host)).ToList();
            foreach (var host in hostToClean)
            {
                var node = _nodesByHostPort[host];
                _nodesByHostPort.Remove(host);
                _nodes.Remove(node);
            }

            _tmpNewNodes.Clear();
            _tmpNewNodeIds.Clear();
        }

        private void ResponseToRoutingTable(MetadataResponse response)
        {
            var routes = new Dictionary<string, Partition[]>();
            foreach (var tm in response.TopicsMeta.Where(_ => Error.IsPartitionOkForProducer(_.ErrorCode)))
            {
                routes[tm.TopicName] =
                    tm.Partitions
                        .Where(_ => Error.IsPartitionOkForProducer(_.ErrorCode) && _.Leader >= 0)
                        .Select(_ => new Partition {Id = _.Id, Leader = _nodesById[_.Leader]})
                        .ToArray();
            }
            _routingTable = new RoutingTable(routes);
        }
    }

}
