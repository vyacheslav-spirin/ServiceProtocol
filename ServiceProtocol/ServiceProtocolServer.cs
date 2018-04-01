using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolServer
    {
        public delegate void Handler<in T>(T request) where T : ServiceProtocolRequest;

        private readonly object lockObject = new object();

        internal readonly ServiceProtocolDataContract dataContract;

        private readonly uint maxConcurrentRequestsPerClient;

        internal readonly IServiceProtocolErrorLogger logger;

        private readonly Action<ServiceProtocolIncomingConnection, ServiceProtocolRequest>[] handlers;

        internal int connectionVersion;

        private Socket listenSocket;

        public ServiceProtocolServer(ServiceProtocolDataContract dataContract, uint maxConcurrentRequestsPerClient, IServiceProtocolErrorLogger logger)
        {
            this.dataContract = dataContract ?? throw new ArgumentNullException(nameof(dataContract));

            if (maxConcurrentRequestsPerClient == 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequestsPerClient));

            this.maxConcurrentRequestsPerClient = maxConcurrentRequestsPerClient;

            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            handlers = new Action<ServiceProtocolIncomingConnection, ServiceProtocolRequest>[dataContract.requestUnpackers.Length];

            SetHandler<ServiceProtocolInternalRequest>(pingRequest => { pingRequest.SendResponse(new ServiceProtocolInternalResponse()); });
        }

        public void Stop()
        {
            lock (lockObject)
            {
                if (listenSocket == null) throw new Exception("Server not in listening mode!");

                try
                {
                    listenSocket.Close();

                    listenSocket = null;
                }
                catch
                {
                    //Ignore all
                }
            }
        }

        public void SetHandler<T>(Handler<T> handler) where T : ServiceProtocolRequest
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (!dataContract.requestPackers.TryGetValue(typeof(T), out var packerInfo))
                throw new InvalidOperationException($"Could not set handler! Type \"{typeof(T)}\" is not registered!");

            handlers[packerInfo.kind] = (connection, request) => { handler((T) request); };
        }

        public bool Listen(ushort port, uint backlog = 32)
        {
            lock (lockObject)
            {
                try
                {
                    if (listenSocket != null) throw new Exception($"{nameof(Listen)} already in progress!");

                    connectionVersion++;

                    listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));

                    listenSocket.NoDelay = false;

                    listenSocket.Listen((int) backlog);

                    var acceptArgs = new SocketAsyncEventArgs();
                    acceptArgs.Completed += Accept;

                    if (!listenSocket.AcceptAsync(acceptArgs)) Accept(listenSocket, acceptArgs);

                    return true;
                }
                catch (Exception e)
                {
                    listenSocket = null;

                    logger.Error($"Service protocol server: Could not start listen! Details: {e}");
                }
            }

            return false;
        }

        private void Accept(object sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                if (args.SocketError == SocketError.OperationAborted)
                {
                    args.Dispose();

                    return;
                }

                args.AcceptSocket = null;
            }
            else
            {
                var acceptedSocket = args.AcceptSocket;

                var receiveArgs = new SocketAsyncEventArgs();
                receiveArgs.Completed += Receive;
                receiveArgs.SetBuffer(new byte[dataContract.requestBufferSize], 0, (int) dataContract.requestBufferSize);
                receiveArgs.UserToken = new ServiceProtocolIncomingConnection(this, acceptedSocket, receiveArgs.Buffer);

                if (!acceptedSocket.ReceiveAsync(receiveArgs)) Receive(acceptedSocket, receiveArgs);

                args.AcceptSocket = null;
            }

            try
            {
                if (!listenSocket.AcceptAsync(args)) Accept(listenSocket, args);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                logger.Fatal($"Service protocol server: Accept error! Details: {e}");
            }
        }

        private void Receive(object sender, SocketAsyncEventArgs args)
        {
            var socket = (Socket) sender;

            var connection = (ServiceProtocolIncomingConnection) args.UserToken;

            if (args.BytesTransferred == 0 || args.SocketError != SocketError.Success || listenSocket == null || connection.connectionVersion != connectionVersion)
            {
                connection.Dispose();

                args.Dispose();

                return;
            }

            var bytesAvailable = args.Offset + args.BytesTransferred;
            var offset = 0;

            while (true)
            {
                if (bytesAvailable < ServiceProtocolRequest.HeaderSize) goto ContinueReceive;

                ServiceProtocolRequest.ReadHeader(args.Buffer, offset, out var requestId, out var requestKind, out var requestSize);

                if (bytesAvailable - ServiceProtocolRequest.HeaderSize < requestSize) goto ContinueReceive;

                offset += ServiceProtocolRequest.HeaderSize;
                bytesAvailable -= ServiceProtocolRequest.HeaderSize;

                connection.unpackStream.Position = offset;

                if (requestKind >= dataContract.requestUnpackers.Length)
                {
                    logger.Error($"Service protocol server: Received invalid request kind! Client: {socket.RemoteEndPoint}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                var unpacker = dataContract.requestUnpackers[requestKind];
                if (unpacker == null)
                {
                    logger.Error($"Service protocol server: Could not find request unpacker by kind {requestKind}! Client: {socket.RemoteEndPoint}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                ServiceProtocolRequest request;
                try
                {
                    request = unpacker(connection.unpackReader);
                }
                catch (Exception e)
                {
                    logger.Fatal($"Service protocol server: Could not unpack request! Kind: {requestKind} Client: {socket.RemoteEndPoint} Details: {e}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                var realUnpackedSize = (int) (connection.unpackStream.Position - offset);

                if (realUnpackedSize != requestSize)
                {
                    logger.Error(
                        $"Service protocol server: Could not unpack request! Kind: {requestKind} Real unpacked size not equal with size in header! Client: {socket.RemoteEndPoint}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                bytesAvailable -= requestSize;
                offset += requestSize;

                var handler = handlers[requestKind];

                if (handler == null)
                {
                    logger.Error($"Service protocol server: Could not find handler by type: {request.GetType()}! Client: {socket.RemoteEndPoint}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                request.id = requestId;

                var count = Interlocked.Increment(ref connection.concurrentRequestsCount);

                if (count > maxConcurrentRequestsPerClient)
                {
                    logger.Error(
                        $"Service protocol server: Client {socket.RemoteEndPoint} exceeded limit of concurrent requests - force disconnect! Limit: {maxConcurrentRequestsPerClient}");

                    connection.Dispose();

                    args.Dispose();

                    return;
                }

                request.connection = connection;

                try
                {
                    handler(connection, request);
                }
                catch (Exception e)
                {
                    logger.Fatal($"Service protocol server: Could not process response! Client: {socket.RemoteEndPoint} Details: {e}");
                }

                if (bytesAvailable == 0) goto ContinueReceive;
            }

            ContinueReceive:

            if (bytesAvailable > 0) Array.Copy(args.Buffer, offset, args.Buffer, 0, bytesAvailable);
            args.SetBuffer(bytesAvailable, args.Buffer.Length - bytesAvailable);

            bool asyncOp;

            try
            {
                asyncOp = socket.ReceiveAsync(args);
            }
            catch
            {
                ((ServiceProtocolIncomingConnection) args.UserToken).Dispose();

                args.Dispose();

                return;
            }

            if (!asyncOp) Receive(socket, args);
        }
    }
}