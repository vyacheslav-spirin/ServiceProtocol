using System.Net;
using ServiceProtocol;
using static Benchmark.BenchmarkServiceProtocol;

namespace Benchmark
{
    internal sealed class BenchmarkServiceApi
    {
        private readonly ServiceProtocolClient client;

        internal ServiceProtocolClientConnectionState ConnectionState => client.ConnectionState;

        internal BenchmarkServiceApi()
        {
            var mgr = new ServiceProtocolClientManager(new SimpleConsoleErrorLogger());

            client = mgr.CreateClient(DataContract, 1000000);
        }

        internal void Connect(IPEndPoint remoteEndPoint)
        {
            client.Connect(remoteEndPoint);
        }

        internal ServiceProtocolAwaiter<ProcessStringResponse> ProcessString(string sourceString)
        {
            return client.SendRequest<ProcessStringResponse>(new ProcessStringRequest
            {
                sourceString = sourceString
            });
        }
    }
}