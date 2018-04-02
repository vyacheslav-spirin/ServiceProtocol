using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolClient
    {
        private const int RequestStateInactive = 0;
        private const int RequestStateBeforeSend = 1;
        private const int RequestStateAfterSend = 2;
        private const int RequestStateProcessError = 3;

        [ThreadStatic]
        internal static ServiceProtocolClient localThreadClient;

        [ThreadStatic]
        internal static uint requestIndex;

        public readonly uint id;

        private readonly ServiceProtocolDataContract dataContract;

        private readonly SocketAsyncEventArgs sendArgs;
        private readonly SocketAsyncEventArgs receiveArgs;

        private readonly uint sendBufferSize;

        private readonly MemoryStream packStream;
        private readonly MemoryStream unpackStream;

        private readonly BinaryWriter packWriter;
        private readonly BinaryReader unpackReader;

        private readonly int[] requestStates;

        private readonly Action[] requestContinuations;
        private readonly uint maxConcurrentRequests;

        private readonly IServiceProtocolErrorLogger logger;

        private readonly ConcurrentQueue<ServiceProtocolRequest> requestsQueue = new ConcurrentQueue<ServiceProtocolRequest>();

        public ServiceProtocolClientConnectionState ConnectionState
        {
            get
            {
                if (connected) return ServiceProtocolClientConnectionState.Connected;
                Thread.MemoryBarrier();
                if (connectionState == 0) return ServiceProtocolClientConnectionState.ConnectionClosed;
                return ServiceProtocolClientConnectionState.Connecting;
            }
        }

        internal bool PingRequestInProgress => requestStates[maxConcurrentRequests] != RequestStateInactive;

        private uint[] currentRequestIds;
        private bool isNeedMoreCurrentRequestIdsSize;

        private int currentRequestsCount;

        private Socket socket;

        private int requestsQueueSize;

        private int connectionState;
        private volatile bool connected;

        private int lastRequestQueueIndex;

        private int processErrorSyncState;

        private int sendMethodRecursionDepth;

        internal int lastConnectionActiveTime;

        internal ServiceProtocolClient(uint id, ServiceProtocolDataContract dataContract, uint maxConcurrentRequests, IServiceProtocolErrorLogger logger)
        {
            this.id = id;

            this.dataContract = dataContract;

            sendBufferSize = dataContract.requestBufferSize;

            this.maxConcurrentRequests = maxConcurrentRequests;
            requestStates = new int[maxConcurrentRequests + ServiceProtocolDataContract.InternalProtocolRequestTypesCount];
            requestContinuations = new Action[maxConcurrentRequests + ServiceProtocolDataContract.InternalProtocolRequestTypesCount];

            this.logger = logger;

            sendArgs = new SocketAsyncEventArgs();
            sendArgs.Completed += SendingComplete;

            sendArgs.SetBuffer(new byte[sendBufferSize + ServiceProtocolRequest.HeaderSize + ServiceProtocolRequest.MaxSize], 0, (int) sendBufferSize);

            var currentRequestIdsSize = sendArgs.Buffer.Length / (ServiceProtocolRequest.HeaderSize + ServiceProtocolRequest.MaxSize);
            if (currentRequestIdsSize < 128) currentRequestIdsSize = 128;
            currentRequestIds = new uint[currentRequestIdsSize];

            receiveArgs = new SocketAsyncEventArgs();
            receiveArgs.Completed += Receive;
            receiveArgs.SetBuffer(new byte[dataContract.responseBufferSize], 0, (int) dataContract.responseBufferSize);

            packStream = new MemoryStream(sendArgs.Buffer, 0, sendArgs.Buffer.Length);
            unpackStream = new MemoryStream(receiveArgs.Buffer, 0, receiveArgs.Buffer.Length);

            packWriter = new BinaryWriter(packStream, dataContract.requestEncoding);
            unpackReader = new BinaryReader(unpackStream, dataContract.responseEncoding);
        }

        public void Connect(IPEndPoint remotEndPoint)
        {
            if (Interlocked.CompareExchange(ref connectionState, 1, 0) != 0) throw new InvalidOperationException("Connection already in progress!");

            var connectArgs = new SocketAsyncEventArgs();
            connectArgs.Completed += ConnectComplete;
            connectArgs.RemoteEndPoint = remotEndPoint;

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = false
            };

            Task.Run(() =>
            {
                if (!socket.ConnectAsync(connectArgs)) ConnectComplete(null, connectArgs);
            });
        }

        public ServiceProtocolAwaiter<T> SendRequest<T>(ServiceProtocolRequest request) where T : ServiceProtocolResponse, new()
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            localThreadClient = this;

            requestIndex = (uint) Interlocked.Increment(ref lastRequestQueueIndex) % maxConcurrentRequests;

            if (Interlocked.CompareExchange(ref requestStates[requestIndex], RequestStateBeforeSend, RequestStateInactive) != RequestStateInactive)
            {
                ServiceProtocolResponse.localThreadCodeForErrorResponses = ServiceProtocolResponseCode.RequestQueueOverflow;

                return ServiceProtocolAwaiter<T>.Instance;
            }

            ServiceProtocolResponse.localThreadCodeForErrorResponses = ServiceProtocolResponseCode.Success;

            request.id = requestIndex;

            Thread.MemoryBarrier();

            AddToSendQueue(request);

            return ServiceProtocolAwaiter<T>.Instance;
        }

        public void SendRequestWithoutResponse(ServiceProtocolRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            request.id = ServiceProtocolDataContract.RequestIdWithoutResponse;

            Thread.MemoryBarrier();

            AddToSendQueue(request);
        }

        internal void SendPing()
        {
            requestIndex = maxConcurrentRequests;

            if (Interlocked.CompareExchange(ref requestStates[requestIndex], RequestStateBeforeSend, RequestStateInactive) != RequestStateInactive) return;

            lastConnectionActiveTime = Environment.TickCount;

            var pingRequest = new ServiceProtocolInternalRequest
            {
                id = requestIndex
            };

            Thread.MemoryBarrier();

            AddToSendQueue(pingRequest);

            SetContinuation(() => { });
        }

        internal void SetContinuation(Action continuation)
        {
            requestContinuations[requestIndex] = continuation;
        }

        private void ConnectComplete(object sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                logger.Error($"Service protocol client {id}: Could not connect to server! Remote end point: {args.RemoteEndPoint} Reason: {args.SocketError}");

                args.Dispose();

                DisconnectLogic();

                return;
            }

            args.Dispose();

            Thread.MemoryBarrier();

            connected = true;

            lastConnectionActiveTime = Environment.TickCount;

            Task.Run(() =>
            {
                if (!socket.ReceiveAsync(receiveArgs)) Receive(null, receiveArgs);
            });
        }

        public void CloseConnection()
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
                socket.Disconnect(false);
            }
            catch
            {
                //Ignore all
            }
        }

        private void DisconnectLogic()
        {
            receiveArgs.SetBuffer(0, receiveArgs.Buffer.Length);

            connected = false;

            socket.Close();
            socket = null;

            while (Interlocked.CompareExchange(ref processErrorSyncState, 1, 0) != 0)
            {
                Thread.SpinWait(1);
            }

            for (uint i = 0; i < maxConcurrentRequests + ServiceProtocolDataContract.InternalProtocolRequestTypesCount; i++)
            {
                ProcessRequestError(i, ServiceProtocolResponseCode.ConnectionClosed, RequestStateAfterSend);
            }

            Thread.MemoryBarrier();

            Interlocked.Exchange(ref processErrorSyncState, 0);

            Interlocked.Exchange(ref connectionState, 0);
        }

        private void Receive(object sender, SocketAsyncEventArgs args)
        {
            if (args.BytesTransferred == 0 || args.SocketError != SocketError.Success)
            {
                if (args.SocketError != SocketError.Success) logger.Error($"Service protocol client {id}: Connection closed! Reason: {args.SocketError}");

                DisconnectLogic();

                return;
            }

            lastConnectionActiveTime = Environment.TickCount;

            var bytesAvailable = args.Offset + args.BytesTransferred;
            var offset = 0;

            while (true)
            {
                if (bytesAvailable < ServiceProtocolResponse.HeaderSize) goto ContinueReceive;

                ServiceProtocolResponse.ReadHeader(args.Buffer, offset, out var responseId, out var responseKind, out var responseCode, out var responseSize);

                if (bytesAvailable - ServiceProtocolResponse.HeaderSize < responseSize) goto ContinueReceive;

                offset += ServiceProtocolResponse.HeaderSize;
                bytesAvailable -= ServiceProtocolResponse.HeaderSize;

                unpackStream.Position = offset;

                if (responseCode == ServiceProtocolResponseCode.Success)
                {
                    if (responseKind >= dataContract.responseUnpackers.Length)
                    {
                        logger.Error($"Service protocol client {id}: Received invalid response kind!");

                        DisconnectLogic();

                        return;
                    }

                    var unpacker = dataContract.responseUnpackers[responseKind];
                    if (unpacker == null)
                    {
                        logger.Error($"Service protocol client {id}: Could not find response unpacker by kind {responseKind}!");

                        DisconnectLogic();

                        return;
                    }

                    ServiceProtocolResponse response;
                    try
                    {
                        response = unpacker(unpackReader);
                    }
                    catch (Exception e)
                    {
                        logger.Fatal($"Service protocol client {id}: Could not unpack response! Kind: {responseKind} Details: {e}");

                        DisconnectLogic();

                        return;
                    }

                    var realUnpackedSize = (int) (unpackStream.Position - offset);

                    if (realUnpackedSize != responseSize)
                    {
                        logger.Error($"Service protocol client {id}: Could not unpack response! Kind: {responseKind} Real unpacked size not equal with size in header!");

                        DisconnectLogic();

                        return;
                    }

                    bytesAvailable -= responseSize;
                    offset += responseSize;

                    ServiceProtocolResponse.localThreadResponse = response;
                }

                GetContinuation:

                var continuation = requestContinuations[responseId];

                if (continuation == null)
                {
                    Thread.SpinWait(1);

                    goto GetContinuation;
                }

                Thread.MemoryBarrier();

                while (Interlocked.CompareExchange(ref requestStates[responseId], RequestStateInactive, RequestStateAfterSend) != RequestStateAfterSend)
                {
                }

                Thread.MemoryBarrier();

                requestContinuations[responseId] = null;

                Thread.MemoryBarrier();

                ServiceProtocolResponse.localThreadCodeForErrorResponses = responseCode;

                try
                {
                    continuation();
                }
                catch (Exception e)
                {
                    logger.Fatal($"Service protocol client {id}: Could not process request continuation! Details: {e}");
                }

                if (bytesAvailable == 0) goto ContinueReceive;
            }

            ContinueReceive:

            if (bytesAvailable > 0) Array.Copy(args.Buffer, offset, args.Buffer, 0, bytesAvailable);
            args.SetBuffer(bytesAvailable, args.Buffer.Length - bytesAvailable);

            if (!socket.ReceiveAsync(args)) Receive(null, args);
        }

        private void AddToSendQueue(ServiceProtocolRequest request)
        {
            var newValue = Interlocked.Increment(ref requestsQueueSize);

            Thread.MemoryBarrier();

            requestsQueue.Enqueue(request);

            if (newValue == 1)
            {
                Task.Run(() => Send());
            }
        }

        private ServiceProtocolRequest GetNextRequest(bool wait)
        {
            ServiceProtocolRequest request;

            if (wait)
            {
                while (!requestsQueue.TryDequeue(out request))
                {
                    Thread.SpinWait(1);
                }

                return request;
            }

            if (!requestsQueue.TryDequeue(out request)) return null;

            return request;
        }

        private void Send()
        {
            currentRequestsCount = 0;

            packStream.Position = 0;

            if (isNeedMoreCurrentRequestIdsSize)
            {
                isNeedMoreCurrentRequestIdsSize = false;

                currentRequestIds = new uint[currentRequestIds.Length * 2];
            }

            while (currentRequestsCount < currentRequestIds.Length)
            {
                var lastPos = (int) packStream.Position;

                var request = GetNextRequest(currentRequestsCount == 0);
                if (request == null) goto ContinueSending;

                packStream.Position += ServiceProtocolRequest.HeaderSize;

                var startPos = (int) packStream.Position;

                if (!dataContract.requestPackers.TryGetValue(request.GetType(), out var packerInfo))
                {
                    logger.Fatal($"Service protocol client {id}: Could not find request packer by type {request.GetType()}!");

                    ProcessRequestError(request.id, ServiceProtocolResponseCode.InternalError, RequestStateBeforeSend);

                    packStream.Position = lastPos;

                    request.id = ServiceProtocolDataContract.RequestIdWithoutResponse;

                    goto AfterRequestPackError;
                }

                try
                {
                    packerInfo.packer(packWriter, request);
                }
                catch (Exception e)
                {
                    logger.Fatal($"Service protocol client {id}: Could not pack request! Type: {request.GetType()} Details: {e}");

                    ProcessRequestError(request.id, ServiceProtocolResponseCode.InternalError, RequestStateBeforeSend);

                    packStream.Position = lastPos;

                    request.id = ServiceProtocolDataContract.RequestIdWithoutResponse;

                    goto AfterRequestPackError;
                }

                var endPos = (int) packStream.Position;

                var requestSize = endPos - startPos;

                if (requestSize > ServiceProtocolRequest.MaxSize)
                {
                    logger.Fatal(
                        $"Service protocol client {id}: Could not send request! Request length exceeds allowed size! Type: {request.GetType()} Size: {requestSize} Max size: {ServiceProtocolRequest.MaxSize}");

                    ProcessRequestError(request.id, ServiceProtocolResponseCode.InternalError, RequestStateBeforeSend);

                    packStream.Position = lastPos;

                    request.id = ServiceProtocolDataContract.RequestIdWithoutResponse;

                    goto AfterRequestPackError;
                }

                ServiceProtocolRequest.WriteHeader(sendArgs.Buffer, lastPos, request.id, packerInfo.kind, (ushort) requestSize);

                AfterRequestPackError:

                currentRequestIds[currentRequestsCount++] = request.id;

                if (packStream.Position >= sendBufferSize) goto ContinueSending;
            }

            isNeedMoreCurrentRequestIdsSize = true;

            ContinueSending:

            Thread.MemoryBarrier();

            sendArgs.SetBuffer(0, (int) packStream.Position);

            bool asyncOp;

            try
            {
                var s = socket;

                if (s == null || !s.Connected)
                {
                    ContinueSendingAfterFailed();

                    return;
                }

                asyncOp = s.SendAsync(sendArgs);
            }
            catch (Exception e) when (e is ObjectDisposedException || e is SocketException || e is NullReferenceException || e is NotSupportedException)
            {
                ContinueSendingAfterFailed();

                return;
            }

            if (!asyncOp) SendingComplete(null, sendArgs);
        }

        private void ContinueSendingAfterFailed()
        {
            for (var i = 0; i < currentRequestsCount; i++)
            {
                var requestId = currentRequestIds[i];
                if (requestId == ServiceProtocolDataContract.RequestIdWithoutResponse) continue;
                ProcessRequestError(requestId, ServiceProtocolResponseCode.ConnectionClosed, RequestStateBeforeSend);
            }

            Task.Run(() => ContinueSending());
        }

        private void SendingComplete(object sender, SocketAsyncEventArgs args)
        {
            if (sender != null) sendMethodRecursionDepth = 0;

            if (args.SocketError != SocketError.Success)
            {
                Thread.MemoryBarrier();

                for (var i = 0; i < currentRequestsCount; i++)
                {
                    var requestId = currentRequestIds[i];
                    if (requestId == ServiceProtocolDataContract.RequestIdWithoutResponse) continue;
                    ProcessRequestError(requestId, ServiceProtocolResponseCode.ConnectionClosed, RequestStateBeforeSend);
                }
            }
            else
            {
                if (Interlocked.CompareExchange(ref processErrorSyncState, 1, 0) == 0)
                {
                    Thread.MemoryBarrier();

                    for (var i = 0; i < currentRequestsCount; i++)
                    {
                        var requestId = currentRequestIds[i];
                        if (requestId == ServiceProtocolDataContract.RequestIdWithoutResponse) continue;
                        Interlocked.Exchange(ref requestStates[requestId], RequestStateAfterSend);
                    }

                    Thread.MemoryBarrier();

                    Interlocked.Exchange(ref processErrorSyncState, 0);
                }
                else
                {
                    for (var i = 0; i < currentRequestsCount; i++)
                    {
                        var requestId = currentRequestIds[i];
                        if (requestId == ServiceProtocolDataContract.RequestIdWithoutResponse) continue;
                        ProcessRequestError(requestId, ServiceProtocolResponseCode.ConnectionClosed, RequestStateBeforeSend);
                    }
                }
            }

            ContinueSending();
        }

        private void ProcessRequestError(uint requestId, ServiceProtocolResponseCode code, int sourceState)
        {
            if (Interlocked.CompareExchange(ref requestStates[requestId], RequestStateProcessError, sourceState) != sourceState) return;

            GetContinuation:

            var continuation = requestContinuations[requestId];

            if (continuation == null)
            {
                Thread.SpinWait(1);

                goto GetContinuation;
            }

            Thread.MemoryBarrier();

            ServiceProtocolResponse.localThreadCodeForErrorResponses = code;

            try
            {
                continuation();
            }
            catch (Exception e)
            {
                logger.Fatal($"Service protocol client {requestId}: Could not process request continuation! Details: {e}");
            }

            requestContinuations[requestId] = null;

            Thread.MemoryBarrier();

            Interlocked.Exchange(ref requestStates[requestId], RequestStateInactive);
        }

        private void ContinueSending()
        {
            Thread.MemoryBarrier();

            var newValue = Interlocked.Add(ref requestsQueueSize, -currentRequestsCount);

            if (newValue == 0) return;

            if (sendMethodRecursionDepth < 1000)
            {
                sendMethodRecursionDepth++;

                Send();
            }
            else
            {
                Task.Run(() =>
                {
                    sendMethodRecursionDepth = 0;

                    Send();
                });
            }
        }
    }
}