using System.Text;
using ServiceProtocol;

namespace Benchmark
{
    internal static class BenchmarkServiceProtocol
    {
        internal class ProcessStringRequest : ServiceProtocolRequest
        {
            internal string sourceString;
        }

        internal class ProcessStringResponse : ServiceProtocolResponse
        {
            internal string processedString;
        }

        internal static readonly ServiceProtocolDataContract DataContract = new ServiceProtocolDataContract(typeof(BenchmarkServiceProtocol), Encoding.UTF8, Encoding.UTF8);
    }
}