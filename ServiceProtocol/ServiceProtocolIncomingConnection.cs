using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceProtocol
{
    internal sealed class ServiceProtocolIncomingConnection
    {
        private struct Response
        {
            internal uint id;

            internal ServiceProtocolResponse response;
        }

        private readonly ServiceProtocolServer server;

        internal readonly int connectionVersion;

        private readonly Socket socket;

        private readonly SocketAsyncEventArgs sendArgs;

        private readonly MemoryStream packStream;
        private readonly BinaryWriter packWriter;

        internal readonly MemoryStream unpackStream;
        internal readonly BinaryReader unpackReader;

        private readonly ConcurrentQueue<Response> responsesQueue = new ConcurrentQueue<Response>();

        internal int concurrentRequestsCount;

        private int responsesQueueSize;

        private int currentResponsesCount;

        internal ServiceProtocolIncomingConnection(ServiceProtocolServer server, Socket socket, byte[] receiveBuffer)
        {
            this.server = server;

            connectionVersion = server.connectionVersion;

            this.socket = socket;

            sendArgs = new SocketAsyncEventArgs();
            sendArgs.Completed += SendingComplete;

            sendArgs.SetBuffer(new byte[server.dataContract.responseBufferSize + ServiceProtocolResponse.HeaderSize + ServiceProtocolResponse.MaxSize],
                0, (int) server.dataContract.responseBufferSize);

            packStream = new MemoryStream(sendArgs.Buffer, 0, sendArgs.Buffer.Length);
            packWriter = new BinaryWriter(packStream, server.dataContract.responseEncoding);

            unpackStream = new MemoryStream(receiveBuffer, 0, receiveBuffer.Length);
            unpackReader = new BinaryReader(unpackStream, server.dataContract.requestEncoding);
        }

        public void Dispose()
        {
            socket.Close();

            sendArgs.Dispose();

            packWriter.Dispose();
            packStream.Dispose();

            unpackReader.Dispose();
            unpackStream.Dispose();
        }

        internal void Send(uint responseId, ServiceProtocolResponse response)
        {
            var newValue = Interlocked.Increment(ref responsesQueueSize);

            Thread.MemoryBarrier();

            responsesQueue.Enqueue(new Response {id = responseId, response = response});

            if (newValue == 1) Task.Run(() => SendResponse());
        }

        private Response GetNextResponse(bool wait)
        {
            Response response;

            if (wait)
            {
                while (!responsesQueue.TryDequeue(out response))
                {
                    Thread.SpinWait(1);
                }

                return response;
            }

            if (!responsesQueue.TryDequeue(out response)) return default(Response);

            return response;
        }

        private void SendResponse()
        {
            try
            {
                currentResponsesCount = 0;

                packStream.Position = 0;

                while (true)
                {
                    var lastPos = (int) packStream.Position;

                    var response = GetNextResponse(currentResponsesCount == 0);
                    if (response.response == null) break;

                    packStream.Position += ServiceProtocolResponse.HeaderSize;

                    var startPos = (int) packStream.Position;

                    byte kind = 0;

                    if (response.response.Code == ServiceProtocolResponseCode.Success)
                    {
                        if (!server.dataContract.responsePackers.TryGetValue(response.response.GetType(), out var packerInfo))
                        {
                            server.logger.Fatal($"Service protocol server: Could not find response packer by type: {response.response.GetType()}");

                            packStream.Position = lastPos;

                            goto AfterResponsePackError;
                        }

                        kind = packerInfo.kind;

                        try
                        {
                            packerInfo.packer(packWriter, response.response);
                        }
                        catch (Exception e)
                        {
                            server.logger.Fatal($"Service protocol server: Could not pack response! Type: {response.response.GetType()} Details: {e}");

                            packStream.Position = lastPos;

                            goto AfterResponsePackError;
                        }
                    }

                    var endPos = (int) packStream.Position;

                    var responseSize = endPos - startPos;

                    if (responseSize > ServiceProtocolResponse.MaxSize)
                    {
                        server.logger.Fatal(
                            $"Service protocol server: Could not send response! Response length exceeds allowed size! Type: {response.response.GetType()} Size: {responseSize} Max size: {ServiceProtocolResponse.MaxSize}");

                        packStream.Position = lastPos;

                        goto AfterResponsePackError;
                    }

                    ServiceProtocolResponse.WriteHeader(sendArgs.Buffer, lastPos, response.id, kind, response.response.Code, (ushort) responseSize);

                    AfterResponsePackError:

                    currentResponsesCount++;

                    if (packStream.Position >= server.dataContract.responseBufferSize) break;
                }

                Thread.MemoryBarrier();

                sendArgs.SetBuffer(0, (int) packStream.Position);

                if (!socket.SendAsync(sendArgs)) SendingComplete(null, sendArgs);
            }
            catch (Exception e) when (e is ObjectDisposedException || e is SocketException || e is NotSupportedException)
            {
            }
            catch (Exception e)
            {
                server.logger.Fatal($"Service protocol server: Internal error! Details: {e}");
            }
        }

        private void SendingComplete(object sender, SocketAsyncEventArgs args)
        {
            Thread.MemoryBarrier();

            Interlocked.Add(ref concurrentRequestsCount, -currentResponsesCount);

            if (Interlocked.Add(ref responsesQueueSize, -currentResponsesCount) == 0) return;

            SendResponse();
        }
    }
}