using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolAwaiter<T> : ICriticalNotifyCompletion where T : ServiceProtocolResponse, new()
    {
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly ServiceProtocolAwaiter<T> Instance = new ServiceProtocolAwaiter<T>();

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool IsCompleted => ServiceProtocolResponse.localThreadCodeForErrorResponses != ServiceProtocolResponseCode.Success;

        private ServiceProtocolAwaiter()
        {
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void OnCompleted(Action continuation)
        {
            ServiceProtocolClient.localThreadClient.SetContinuation(continuation);
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void UnsafeOnCompleted(Action continuation)
        {
            ServiceProtocolClient.localThreadClient.SetContinuation(continuation);
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ServiceProtocolAwaiter<T> GetAwaiter()
        {
            return this;
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
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