﻿//-----------------------------------------------------------------------------
// Filename: STUNAttribute.cs
//
// Description: Implements STUN message attributes as defined in RFC5389.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 26 Nov 2010	Aaron Clauson	Created, Hobart, Australia.
// 26 Mar 2021  Aaron Clauson   Added ICE-CONTROLLED attribute.
//
// Notes:
//
// 15.  STUN Attributes
//
//   After the STUN header are zero or more attributes.  Each attribute
//   MUST be TLV encoded, with a 16-bit type, 16-bit length, and value.
//   Each STUN attribute MUST end on a 32-bit boundary.  As mentioned
//   above, all fields in an attribute are transmitted most significant
//   bit first.
//
//       0                   1                   2                   3
//       0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
//      +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
//      |         Type                  |            Length             |
//      +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
//      |                         Value (variable)                ....
//      +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
//
//                    Figure 4: Format of STUN Attributes
//
//
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.Net
{
    public enum STUNAttributeTypesEnum : ushort
    {
        Unknown = 0,
        MappedAddress = 0x0001,
        ResponseAddress = 0x0002,       // Not used in RFC5389.
        ChangeRequest = 0x0003,         // Not used in RFC5389.
        SourceAddress = 0x0004,         // Not used in RFC5389.
        ChangedAddress = 0x0005,        // Not used in RFC5389.
        Username = 0x0006,
        Password = 0x0007,              // Not used in RFC5389.
        MessageIntegrity = 0x0008,
        ErrorCode = 0x0009,
        UnknownAttributes = 0x000A,
        ReflectedFrom = 0x000B,         // Not used in RFC5389.
        Realm = 0x0014,
        Nonce = 0x0015,
        RequestedAddressFamily = 0x0017,// Added in RFC6156.
        XORMappedAddress = 0x0020,

        Software = 0x8022,              // Added in RFC5389.
        AlternateServer = 0x8023,       // Added in RFC5389.
        FingerPrint = 0x8028,           // Added in RFC5389.

        IceControlled = 0x8029,         // Added in RFC8445.
        IceControlling = 0x802a,        // Added in RFC8445.
        Priority = 0x0024,              // Added in RFC8445.

        UseCandidate = 0x0025,          // Added in RFC5245.

        // New attributes defined in TURN (RFC5766).
        ChannelNumber = 0x000C,
        Lifetime = 0x000D,
        XORPeerAddress = 0x0012,
        Data = 0x0013,
        XORRelayedAddress = 0x0016,
        EvenPort = 0x0018,
        RequestedTransport = 0x0019,
        DontFragment = 0x001A,
        ReservationToken = 0x0022,

        ConnectionId = 0x002a,          // Added in RFC6062.
    }

    public class STUNAttributeTypes
    {
        public static STUNAttributeTypesEnum GetSTUNAttributeTypeForId(int stunAttributeTypeId)
        {
            return (STUNAttributeTypesEnum)Enum.Parse(typeof(STUNAttributeTypesEnum), stunAttributeTypeId.ToString(), true);
        }
    }

    public class STUNAttributeConstants
    {
        public static readonly byte[] UdpTransportType = new byte[] { 0x11, 0x00, 0x00, 0x00 };     // The payload type for UDP in a RequestedTransport type attribute.
        public static readonly byte[] TcpTransportType = new byte[] { 0x06, 0x00, 0x00, 0x00 };     // The payload type for TCP in a RequestedTransport type attribute.

        /// <summary>
        /// The requested TURN relay ip address is IPv4 (RFC5389, Section 15.1)
        /// </summary>
        public static readonly byte[] IPv4AddressFamily = new byte[] { 0x01, 0x00, 0x00, 0x00 };
        /// <summary>
        /// The requested TURN relay ip address is IPv6 (RFC5389, Section 15.1)
        /// </summary>
        public static readonly byte[] IPv6AddressFamily = new byte[] { 0x02, 0x00, 0x00, 0x00 };
    }

    public class STUNAttribute
    {
        public const short STUNATTRIBUTE_HEADER_LENGTH = 4;

        private static ILogger logger = Log.Logger;

        public STUNAttributeTypesEnum AttributeType = STUNAttributeTypesEnum.Unknown;
        public byte[] Value;

        public virtual UInt16 PaddedLength
        {
            get
            {
                if (Value != null)
                {
                    return Convert.ToUInt16((Value.Length % 4 == 0) ? Value.Length : Value.Length + (4 - (Value.Length % 4)));
                }
                else
                {
                    return 0;
                }
            }
        }

        public STUNAttribute(STUNAttributeTypesEnum attributeType, byte[] value)
        {
            AttributeType = attributeType;
            Value = value;
        }

        public STUNAttribute(STUNAttributeTypesEnum attributeType, ushort value)
        {
            AttributeType = attributeType;
            Value = NetConvert.GetBytes(value);
        }

        public STUNAttribute(STUNAttributeTypesEnum attributeType, uint value)
        {
            AttributeType = attributeType;
            Value = NetConvert.GetBytes(value);
        }

        public STUNAttribute(STUNAttributeTypesEnum attributeType, ulong value)
        {
            AttributeType = attributeType;
            Value = NetConvert.GetBytes(value);
        }

        public static List<STUNAttribute> ParseMessageAttributes(byte[] buffer, int startIndex, int endIndex) => ParseMessageAttributes(buffer, startIndex, endIndex, null);

        public static List<STUNAttribute> ParseMessageAttributes(byte[] buffer, int startIndex, int endIndex, STUNHeader header)
        {
            if (buffer != null && buffer.Length > startIndex && buffer.Length >= endIndex)
            {
                List<STUNAttribute> attributes = new List<STUNAttribute>();
                int startAttIndex = startIndex;

                while (startAttIndex < endIndex - 4)
                {
                    UInt16 stunAttributeType = NetConvert.ParseUInt16(buffer, startAttIndex);
                    UInt16 stunAttributeLength = NetConvert.ParseUInt16(buffer, startAttIndex + 2);
                    byte[] stunAttributeValue = null;

                    STUNAttributeTypesEnum attributeType = STUNAttributeTypes.GetSTUNAttributeTypeForId(stunAttributeType);

                    if (stunAttributeLength > 0)
                    {
                        if (stunAttributeLength + startAttIndex + 4 > endIndex)
                        {
                            logger.LogWarning("The attribute length on a STUN parameter was greater than the available number of bytes. Type: {AttributeType}", attributeType);
                        }
                        else
                        {
                            stunAttributeValue = new byte[stunAttributeLength];
                            Buffer.BlockCopy(buffer, startAttIndex + 4, stunAttributeValue, 0, stunAttributeLength);
                        }
                    }

                    if(stunAttributeValue == null && stunAttributeLength > 0)
                    {
                        break;
                    }
                    STUNAttribute attribute = null;
                    if (attributeType == STUNAttributeTypesEnum.ChangeRequest)
                    {
                        attribute = new STUNChangeRequestAttribute(stunAttributeValue);
                    }
                    else if (attributeType == STUNAttributeTypesEnum.MappedAddress || attributeType == STUNAttributeTypesEnum.AlternateServer)
                    {
                        attribute = new STUNAddressAttribute(attributeType, stunAttributeValue);
                    }
                    else if (attributeType == STUNAttributeTypesEnum.ErrorCode)
                    {
                        attribute = new STUNErrorCodeAttribute(stunAttributeValue);
                    }
                    else if (attributeType == STUNAttributeTypesEnum.XORMappedAddress || attributeType == STUNAttributeTypesEnum.XORPeerAddress || attributeType == STUNAttributeTypesEnum.XORRelayedAddress)
                    {
                        attribute = new STUNXORAddressAttribute(attributeType, stunAttributeValue, header.TransactionId);
                    }
                    else if(attributeType == STUNAttributeTypesEnum.ConnectionId)
                    {
                        attribute = new STUNConnectionIdAttribute(stunAttributeValue);
                    }
                    else
                    {
                        attribute = new STUNAttribute(attributeType, stunAttributeValue);
                    }

                    attributes.Add(attribute);

                    // Attributes start on 32 bit word boundaries so where an attribute length is not a multiple of 4 it gets padded. 
                    int padding = (stunAttributeLength % 4 != 0) ? 4 - (stunAttributeLength % 4) : 0;

                    startAttIndex = startAttIndex + 4 + stunAttributeLength + padding;
                }

                return attributes;
            }
            else
            {
                return null;
            }
        }

        public virtual int ToByteBuffer(byte[] buffer, int startIndex)
        {
            if (BitConverter.IsLittleEndian)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(NetConvert.DoReverseEndian((ushort)AttributeType)), 0, buffer, startIndex, 2);

                if (Value != null && Value.Length > 0)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(NetConvert.DoReverseEndian(Convert.ToUInt16(Value.Length))), 0, buffer, startIndex + 2, 2);
                }
                else
                {
                    buffer[startIndex + 2] = 0x00;
                    buffer[startIndex + 3] = 0x00;
                }
            }
            else
            {
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)AttributeType), 0, buffer, startIndex, 2);

                if (Value != null && Value.Length > 0)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(Convert.ToUInt16(Value.Length)), 0, buffer, startIndex + 2, 2);
                }
                else
                {
                    buffer[startIndex + 2] = 0x00;
                    buffer[startIndex + 3] = 0x00;
                }
            }

            if (Value != null && Value.Length > 0)
            {
                Buffer.BlockCopy(Value, 0, buffer, startIndex + 4, Value.Length);
            }

            return STUNAttribute.STUNATTRIBUTE_HEADER_LENGTH + PaddedLength;
        }

        public new virtual string ToString()
        {
            string attrDescrString = "STUN Attribute: " + AttributeType.ToString() + ", length=" + PaddedLength + ".";

            return attrDescrString;
        }
    }
}
