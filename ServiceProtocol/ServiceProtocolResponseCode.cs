namespace ServiceProtocol
{
    public enum ServiceProtocolResponseCode : byte
    {
        Success = 0,

        InternalError = 1,

        RequestQueueOverflow = 2,
        ConnectionClosed = 3,

        RemoteServiceInternalError = 4,
        RemoteServiceExternalError = 5
    }
}