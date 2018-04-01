using System.Net;
using ServiceProtocol;

namespace Benchmark
{
    internal sealed class BenchmarkServiceApi
    {
        private readonly ServiceProtocolClient client;

        internal ServiceProtocolClientConnectionState ConnectionState => client.ConnectionState;

        internal BenchmarkServiceApi()
        {
            var mgr = new ServiceProtocolClientManager(new SimpleConsoleErrorLogger());

            client = mgr.CreateClient(BenchmarkServiceProtocol.DataContract, 1000000);
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

            return ServiceProtocolAwaiter<BenchmarkServiceProtocol.ProcessStringResponse>.Instance;
        }
    }
}