using System;
using ServiceProtocol;

namespace Benchmark
{
    internal sealed class SimpleConsoleErrorLogger : IServiceProtocolErrorLogger
    {
        public void Error(string message)
        {
            Console.WriteLine("Service protocol ERROR: " + message);
        }

        public void Fatal(string message)
        {
            Console.WriteLine("Service protocol FATAL: " + message);
        }
    }
}