using System;

namespace ServiceProtocol
{
    public abstract class ServiceProtocolRequest
    {
        /// <summary>
        ///     Request Id + Kind + Size
        /// </summary>
        internal const byte HeaderSize = 4 + 1 + 2;

        internal const ushort MaxSize = ushort.MaxValue;

        [NonSerialized]
        internal uint id;

        [NonSerialized]
        internal ServiceProtocolIncomingConnection connection;

        internal static void WriteHeader(byte[] buffer, int offset, uint id, byte kind, ushort size)
        {
            buffer[offset] = (byte) id;
            buffer[offset + 1] = (byte) (id >> 8);
            buffer[offset + 2] = (byte) (id >> 16);
            buffer[offset + 3] = (byte) (id >> 24);

            buffer[offset + 4] = kind;

            buffer[offset + 5] = (byte) size;
            buffer[offset + 6] = (byte) (size >> 8);
        }

        internal static void ReadHeader(byte[] buffer, int offset, out uint id, out byte kind, out ushort size)
        {
            id = (uint) (buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

            kind = buffer[offset + 4];

            size = (ushort) (buffer[offset + 5] | (buffer[offset + 6] << 8));
        }

        public void SendResponse(ServiceProtocolResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));

            if (connection == null) throw new InvalidOperationException("Access denied!");

            connection.Send(id, response);
            connection = null;
        }

        public void SendServiceInternalError()
        {
            if (connection == null) throw new InvalidOperationException("Access denied!");

            connection.Send(id, new ServiceProtocolInternalResponse {Code = ServiceProtocolResponseCode.RemoteServiceInternalError});
            connection = null;
        }

        public void SendServiceExternalError()
        {
            if (connection == null) throw new InvalidOperationException("Access denied!");

            connection.Send(id, new ServiceProtocolInternalResponse {Code = ServiceProtocolResponseCode.RemoteServiceExternalError});
            connection = null;
        }
    }
}