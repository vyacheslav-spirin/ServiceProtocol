using System;

namespace ServiceProtocol
{
    public abstract class ServiceProtocolResponse
    {
        /// <summary>
        ///     Request Id + Kind + Code + Size
        /// </summary>
        internal const byte HeaderSize = 4 + 1 + 1 + 2;

        internal const ushort MaxSize = ushort.MaxValue;

        [ThreadStatic]
        internal static ServiceProtocolResponseCode localThreadCodeForErrorResponses;

        [ThreadStatic]
        internal static ServiceProtocolResponse localThreadResponse;

        public bool HasError => Code != 0;

        public ServiceProtocolResponseCode Code { get; internal set; }

        internal static void WriteHeader(byte[] buffer, int offset, uint id, byte kind, ServiceProtocolResponseCode code, ushort size)
        {
            buffer[offset] = (byte) id;
            buffer[offset + 1] = (byte) (id >> 8);
            buffer[offset + 2] = (byte) (id >> 16);
            buffer[offset + 3] = (byte) (id >> 24);

            buffer[offset + 4] = kind;

            buffer[offset + 5] = (byte) code;

            buffer[offset + 6] = (byte) size;
            buffer[offset + 7] = (byte) (size >> 8);
        }

        internal static void ReadHeader(byte[] buffer, int offset, out uint id, out byte kind, out ServiceProtocolResponseCode code, out ushort size)
        {
            id = (uint) (buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

            kind = buffer[offset + 4];

            code = (ServiceProtocolResponseCode) buffer[offset + 5];

            size = (ushort) (buffer[offset + 6] | (buffer[offset + 7] << 8));
        }
    }
}