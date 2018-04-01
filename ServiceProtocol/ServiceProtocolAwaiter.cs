using System;
using System.Runtime.CompilerServices;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolAwaiter<T> : ICriticalNotifyCompletion where T : ServiceProtocolResponse, new()
    {
        public bool IsCompleted => ServiceProtocolResponse.localThreadCodeForErrorResponses != ServiceProtocolResponseCode.Success;

        public void OnCompleted(Action continuation)
        {
            ServiceProtocolClient.localThreadClient.SetContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            ServiceProtocolClient.localThreadClient.SetContinuation(continuation);
        }

        public ServiceProtocolAwaiter<T> GetAwaiter()
        {
            return this;
        }

        public T GetResult()
        {
            if (ServiceProtocolResponse.localThreadCodeForErrorResponses == ServiceProtocolResponseCode.Success) return (T) ServiceProtocolResponse.localThreadResponse;

            var response = new T
            {
                Code = ServiceProtocolResponse.localThreadCodeForErrorResponses
            };

            ServiceProtocolResponse.localThreadCodeForErrorResponses = ServiceProtocolResponseCode.Success;

            return response;
        }
    }
}