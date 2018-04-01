namespace ServiceProtocol
{
    public interface IServiceProtocolErrorLogger
    {
        void Error(string message);

        void Fatal(string message);
    }
}