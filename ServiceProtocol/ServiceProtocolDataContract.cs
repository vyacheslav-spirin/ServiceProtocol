using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ServiceProtocol
{
    public sealed class ServiceProtocolDataContract
    {
        internal class RequestPackerInfo
        {
            internal readonly Action<BinaryWriter, ServiceProtocolRequest> packer;

            internal readonly byte kind;

            internal RequestPackerInfo(Action<BinaryWriter, ServiceProtocolRequest> packer, byte kind)
            {
                this.packer = packer;

                this.kind = kind;
            }
        }

        internal class ResponsePackerInfo
        {
            internal readonly Action<BinaryWriter, ServiceProtocolResponse> packer;

            internal readonly byte kind;

            internal ResponsePackerInfo(Action<BinaryWriter, ServiceProtocolResponse> packer, byte kind)
            {
                this.packer = packer;

                this.kind = kind;
            }
        }

        internal const byte InternalProtocolRequestTypesCount = 1;

        internal const uint RequestIdWithoutResponse = uint.MaxValue;

        internal readonly Dictionary<Type, RequestPackerInfo> requestPackers = new Dictionary<Type, RequestPackerInfo>();
        internal readonly Func<BinaryReader, ServiceProtocolRequest>[] requestUnpackers;

        internal readonly Dictionary<Type, ResponsePackerInfo> responsePackers = new Dictionary<Type, ResponsePackerInfo>();
        internal readonly Func<BinaryReader, ServiceProtocolResponse>[] responseUnpackers;

        internal readonly Encoding requestEncoding;
        internal readonly Encoding responseEncoding;

        internal readonly uint requestBufferSize = ServiceProtocolRequest.HeaderSize + ushort.MaxValue;
        internal readonly uint responseBufferSize = ServiceProtocolResponse.HeaderSize + ushort.MaxValue;

        public ServiceProtocolDataContract(Type rootClass, Encoding requestEncoding, Encoding responseEncoding)
        {
            var types = rootClass.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => typeof(ServiceProtocolRequest).IsAssignableFrom(t)).OrderBy(t => t.FullName).ToArray();

            Array.Resize(ref types, types.Length + InternalProtocolRequestTypesCount);
            types[types.Length - InternalProtocolRequestTypesCount] = typeof(ServiceProtocolInternalRequest);

            if (types.Length > 256) throw new Exception($"Maximum number of request types exceeded! Max: {256 - InternalProtocolRequestTypesCount}");

            requestUnpackers = new Func<BinaryReader, ServiceProtocolRequest>[types.Length];

            byte kind = 0;
            foreach (var requestType in types)
            {
                var packer = (Action<BinaryWriter, ServiceProtocolRequest>) ServiceProtocolSerializer.GeneratePackCode(requestType, typeof(ServiceProtocolRequest),
                    typeof(Action<BinaryWriter, ServiceProtocolRequest>));

                requestPackers.Add(requestType, new RequestPackerInfo(packer, kind));

                requestUnpackers[kind] = (Func<BinaryReader, ServiceProtocolRequest>) ServiceProtocolSerializer.GenerateUnpackCode(requestType, typeof(ServiceProtocolRequest),
                    typeof(Func<BinaryReader, ServiceProtocolRequest>));

                kind++;
            }

            types = rootClass.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => typeof(ServiceProtocolResponse).IsAssignableFrom(t)).OrderBy(t => t.FullName).ToArray();

            Array.Resize(ref types, types.Length + InternalProtocolRequestTypesCount);
            types[types.Length - InternalProtocolRequestTypesCount] = typeof(ServiceProtocolInternalResponse);

            if (types.Length > 256) throw new Exception($"Maximum number of response types exceeded! Max: {256 - InternalProtocolRequestTypesCount}");

            responseUnpackers = new Func<BinaryReader, ServiceProtocolResponse>[types.Length];

            kind = 0;
            foreach (var responseType in types)
            {
                var packer = (Action<BinaryWriter, ServiceProtocolResponse>) ServiceProtocolSerializer.GeneratePackCode(responseType, typeof(ServiceProtocolResponse),
                    typeof(Action<BinaryWriter, ServiceProtocolResponse>));

                responsePackers.Add(responseType, new ResponsePackerInfo(packer, kind));

                responseUnpackers[kind] = (Func<BinaryReader, ServiceProtocolResponse>) ServiceProtocolSerializer.GenerateUnpackCode(responseType, typeof(ServiceProtocolResponse),
                    typeof(Func<BinaryReader, ServiceProtocolResponse>));

                kind++;
            }

            this.requestEncoding = requestEncoding;
            this.responseEncoding = responseEncoding;
        }
    }
}