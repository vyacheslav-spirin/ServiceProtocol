using System;
using System.Linq;
using System.Net;
using System.Threading;
using ServiceProtocol;

namespace Benchmark
{
    internal static class Program
    {
        private static BenchmarkServiceApi api;

        private static long requestsCount;

        private static long successRequestsCount;

        private static long errorRequestsCount;

        private static long[] responseCodeStats;

        private static volatile int requestLoopStop;

        [ThreadStatic]
        private static Random random;

        private static void Main()
        {
            string modeString;

            do
            {
                Console.Write("Select application mode: s (Server) or c (Client): ");

                modeString = Console.ReadLine();

                if (modeString != "s" && modeString != "c")
                {
                    Console.WriteLine("Invalid application mode! Try again...");
                }
                else break;
            } while (true);

            if (modeString == "s") ServerMode();
            else ClientMode();
        }

        private static void ServerMode()
        {
            Console.WriteLine("Start in server mode.");

            ServiceProtocolServer benchmarkServer;

            while (true)
            {
                Console.Write("Select free port number (1-65535): ");

                var portString = Console.ReadLine();

                if (!ushort.TryParse(portString, out var port) || port == 0)
                {
                    Console.WriteLine("Incorrect port number! Try again...");
                }
                else
                {
                    benchmarkServer = new ServiceProtocolServer(BenchmarkServiceProtocol.DataContract, 6000, new SimpleConsoleErrorLogger());

                    benchmarkServer.SetHandler<BenchmarkServiceProtocol.ProcessStringRequest>(
                        request =>
                        {
                            var processedString = request.sourceString + "_processed";

                            var resp = new BenchmarkServiceProtocol.ProcessStringResponse
                            {
                                processedString = processedString
                            };

                            request.SendResponse(resp);
                        });

                    if (!benchmarkServer.Listen(port)) Console.WriteLine("Could not start server! Try to select a different port...");
                    else break;
                }
            }

            Console.WriteLine("Server started successfully! Press enter to stop...");

            Console.ReadLine();

            benchmarkServer.Stop();

            Console.WriteLine("Server stopped!");

            Console.ReadLine();
        }

        private static void ClientMode()
        {
            Console.WriteLine("Start in client mode.");

            responseCodeStats = new long[Enum.GetValues(typeof(ServiceProtocolResponseCode)).Length];

            IPEndPoint remoteEndPoint;

            do
            {
                do
                {
                    Console.Write("Input server address (ip:port): ");

                    var address = Console.ReadLine() ?? "";

                    var args = address.Split(new[] {':'}, StringSplitOptions.RemoveEmptyEntries);

                    if (args.Length < 2 || !IPAddress.TryParse(args[0], out var ip) || !ushort.TryParse(args[1], out var port) || port == 0)
                    {
                        Console.WriteLine("Incorrect server address! Try again...");
                    }
                    else
                    {
                        remoteEndPoint = new IPEndPoint(ip, port);
                        break;
                    }
                } while (true);

                Console.Write("Select threads count (1-255): ");

                var threadsCountString = Console.ReadLine();

                if (!byte.TryParse(threadsCountString, out var threadsCount) || threadsCount == 0) Console.WriteLine("Incorrect threads count number! Try again...");
                else
                {
                    api = new BenchmarkServiceApi();

                    api.Connect(remoteEndPoint);

                    for (var i = 0; i < threadsCount; i++)
                    {
                        new Thread(RequestLoop).Start();
                    }

                    Console.WriteLine($"Client started successfully! Threads count: {threadsCount}.");

                    break;
                }
            } while (true);

            new Thread(() =>
            {
                var lastRequestsCount = 0L;
                var lastCompleteRequests = 0L;

                var startTime = DateTime.Now;

                while (requestLoopStop < 2)
                {
                    Console.WriteLine();

                    Console.WriteLine($"Requests stats (Elapsed: {DateTime.Now - startTime:hh\\:mm\\:ss}):");

                    var completeTotal = successRequestsCount + errorRequestsCount;
                    var percentTotal = requestsCount == 0 ? 1 : (double) completeTotal / requestsCount;
                    if (percentTotal > 1) percentTotal = 1;

                    Console.WriteLine($"Total (out / complete): {requestsCount} / {completeTotal} ({percentTotal:0.##%})");

                    var errorsPercent = requestsCount == 0 ? (errorRequestsCount > 0 ? 1 : 0) : (double) errorRequestsCount / requestsCount;
                    if (errorsPercent > 1) errorsPercent = 1;

                    var outPerSec = requestsCount - lastRequestsCount;
                    var completePerSec = successRequestsCount + errorRequestsCount - lastCompleteRequests;
                    var percentPerSec = outPerSec == 0 ? 1 : (double) completePerSec / outPerSec;

                    Console.WriteLine($"Per second (out / complete): {outPerSec} / {completePerSec} ({percentPerSec:0.##%})");

                    Console.WriteLine($"Response code stats (Errors: {errorsPercent:0.##%}):");

                    for (byte i = 0; i < responseCodeStats.Length; i++)
                    {
                        var c = responseCodeStats[i];
                        if (c == 0) continue;

                        Console.WriteLine($" {(ServiceProtocolResponseCode) i}: {c}");
                    }

                    Console.WriteLine($"Client connection state: {api.ConnectionState}");

                    if (api.ConnectionState == ServiceProtocolClientConnectionState.ConnectionClosed) api.Connect(remoteEndPoint);

                    lastRequestsCount = requestsCount;
                    lastCompleteRequests = successRequestsCount + errorRequestsCount;

                    Thread.Sleep(1000);
                }
            }).Start();

            Console.ReadLine();

            requestLoopStop = 1;

            Console.ReadLine();

            requestLoopStop = 2;
        }

        public static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

            if (random == null) random = new Random(Thread.CurrentThread.ManagedThreadId);

            return new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private static void RequestLoop()
        {
            while (requestLoopStop < 1)
            {
                for (var i = 0; i < 100; i++) SendRequest();

                Thread.Sleep(1);
            }
        }

        private static async void SendRequest()
        {
            Interlocked.Increment(ref requestsCount);

            var sourceString = RandomString(16);

            var response = await api.ProcessString(sourceString);

            Interlocked.Increment(ref responseCodeStats[(byte) response.Code]);

            if (response.HasError)
            {
                Interlocked.Increment(ref errorRequestsCount);
            }
            else
            {
                if (response.processedString != sourceString + "_processed")
                {
                    throw new Exception($"Bad request logic! Expected: \"{sourceString}_processed\" Received: \"{response.processedString}\"");
                }

                Interlocked.Increment(ref successRequestsCount);
            }
        }
    }
}