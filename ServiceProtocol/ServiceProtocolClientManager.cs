using System;
using System.Collections.Generic;
using System.Threading;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolClientManager
    {
        private readonly IServiceProtocolErrorLogger logger;

        private readonly ushort keepAliveTimeoutMilliseconds;

        private readonly ushort connectionTimeoutMilliseconds;

        private readonly List<ServiceProtocolClient> clients = new List<ServiceProtocolClient>(100);

        private uint lastId;

        public ServiceProtocolClientManager(IServiceProtocolErrorLogger logger, ushort keepAliveTimeoutMilliseconds = 10000, ushort connectionTimeoutMilliseconds = 3000)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            this.keepAliveTimeoutMilliseconds = keepAliveTimeoutMilliseconds;

            this.connectionTimeoutMilliseconds = connectionTimeoutMilliseconds;

            new Thread(ThreadLoop)
            {
                IsBackground = true
            }.Start();
        }

        public ServiceProtocolClient CreateClient(ServiceProtocolDataContract dataContract, uint maxConcurrentRequests)
        {
            if (dataContract == null) throw new ArgumentNullException(nameof(dataContract));

            if (maxConcurrentRequests == 0 || maxConcurrentRequests == uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));

            var client = new ServiceProtocolClient(++lastId, dataContract, maxConcurrentRequests, logger);

            lock (clients)
            {
                clients.Add(client);
            }

            return client;
        }

        private void ThreadLoop()
        {
            try
            {
                while (true)
                {
                    lock (clients)
                    {
                        var now = Environment.TickCount;

                        foreach (var client in clients)
                        {
                            if (client.ConnectionState != ServiceProtocolClientConnectionState.Connected) continue;

                            if (now - client.lastConnectionActiveTime > keepAliveTimeoutMilliseconds && !client.PingRequestInProgress) client.SendPing();

                            if (now - client.lastConnectionActiveTime > connectionTimeoutMilliseconds && client.PingRequestInProgress)
                            {
                                client.CloseConnection();
                            }
                        }
                    }

                    Thread.Sleep(1000);
                }
            }
            catch (Exception e)
            {
                logger.Fatal("Error in service protocol client manager thread. Details: " + e);
            }
        }
    }
}