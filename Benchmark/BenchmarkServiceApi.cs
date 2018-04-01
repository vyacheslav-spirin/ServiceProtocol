using System.Net;
using ServiceProtocol;

namespace Benchmark
{
    internal sealed class BenchmarkServiceApi
    {
        private readonly ServiceProtocolClient client;

        private readonly ServiceProtocolAwaiter<BenchmarkServiceProtocol.ProcessStringResponse> processStringAwaiter;

        internal ServiceProtocolClientConnectionState ConnectionState => client.ConnectionState;

        internal BenchmarkServiceApi()
        {
            var mgr = new ServiceProtocolClientManager(new SimpleConsoleErrorLogger());

            client = mgr.CreateClient(BenchmarkServiceProtocol.DataContract, 1000000);

            processStringAwaiter = new ServiceProtocolAwaiter<BenchmarkServiceProtocol.ProcessStringResponse>();
        }

        internal void Connect(IPEndPoint remoteEndPoint)
        {
            client.Connect(remoteEndPoint);
        }

        internal ServiceProtocolAwaiter<BenchmarkServiceProtocol.ProcessStringResponse> ProcessString(string sourceString)
        {
            client.SendRequest(new BenchmarkServiceProtocol.ProcessStringRequest
            {
                sourceString = sourceString
            });

            return processStringAwaiter;
        }
    }
}