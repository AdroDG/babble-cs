﻿using System;
using System.Security.Cryptography;
using Dotnatter.Crypto;
using Dotnatter.Util;

namespace Dotnatter.HashgraphImpl
{
    public class EventBody
    {

        public byte[][] Transactions { get; set; } //the payload
        public string[] Parents { get; set; } //hashes of the event's parents, self-parent first
        public byte[] Creator { get; set; } //creator's public key
        public DateTime Timestamp { get; set; } //creator's claimed timestamp of the event's creation
        public int Index { get; set; } //index in the sequence of events created by Creator

        //wire
        //It is cheaper to send ints then hashes over the wire
        internal int SelfParentIndex { get; set; }
        internal int OtherParentCreatorId { get; set; }
        internal int OtherParentIndex { get; set; }
        internal int CreatorId { get; set; }


        public byte[] Marshal()
        {
            return this.SerializeToByteArray();
        }

        public static EventBody Unmarshal(byte[] data)
        {
            return data.DeserializeFromByteArray<EventBody>();
        }

 
        public byte[] Hash() =>  CryptoUtils.SHA256(Marshal());

    }

    public class EventCoordinates
    {
        public string Hash { get; set; }
        public int Index { get; set; }
    }

    public class Event
    {
        public EventBody Body { get; set; }
        //public (ulong R, ulong S) Signiture { get; set; } //creator's digital signature of body
        public byte[] Signiture { get; set; }
        public int TopologicalIndex { get; set; }
        public int RoundReceived { get; set; }
        public DateTime ConsensusTimestamp { get; set; }
        public EventCoordinates[] LastAncestors { get; set; } //[participant fake id] => last ancestor
        public EventCoordinates[] FirstDescendants { get; set; } //[participant fake id] => first descendant


        //sha256 hash of body and signature

        private string creator;
        private byte[] hash;
        private string hex;
        public string Creator => creator ?? (creator = Body.Creator.ToHex());
        public byte[] Hash() => hash ?? (hash = CryptoUtils.SHA256(Marhsal()));
        public string Hex() => hex ?? (hex = Hash().ToHex());


        public static Event NewEvent(byte[][] transactions, string[] parents, byte[] creator, int index)
        {
            var body = new EventBody
            {
                Transactions = transactions,
                Parents = parents,
                Creator = creator,
                Timestamp = DateTime.UtcNow, //strip monotonic time
                Index = index
            };

            var ev = new Event
            {
                Body = body
            };

            return ev;
        }

        public string SelfParent()
        {
            return Body.Parents[0];
        }

        public string OtherParent()
        {
            return Body.Parents[1];
        }

        public byte[][] Transactions()
        {
            return Body.Transactions;
        }

        public int Index()
        {
            return Body.Index;
        }

        //True if Event contains a payload or is the initial Event of its creator
        public bool IsLoaded()
        {
            if (Body.Index == 0)
            {
                return true;
            }

            return Body.Transactions?.Length > 0;
        }

        //ecdsa sig
        public void Sign(CngKey privKey)
        {
            var signBytes = Body.Hash();
            Signiture = CryptoUtils.Sign(privKey, signBytes);
        }

        public bool Verify()
        {
            var pubBytes = Body.Creator;
            var pubKey = CryptoUtils.ToECDSAPub(pubBytes);
            var signBytes = Body.Hash();

            return CryptoUtils.Verify(pubKey, signBytes, Signiture);
        }


        //json encoding of body and signature
        public byte[] Marhsal()
        {
            return this.SerializeToByteArray();
        }

        public static Event Unmarshal(byte[] data)
        {
            return data.DeserializeFromByteArray<Event>();
        }

        public void SetRoundReceived(int rr)
        {
            RoundReceived = rr;
        }

        public void SetWireInfo(int selfParentIndex, int otherParentCreatorId, int otherParentIndex, int creatorId)
        {
            Body.SelfParentIndex = selfParentIndex;

            Body.OtherParentCreatorId = otherParentCreatorId;

            Body.OtherParentIndex = otherParentIndex;

            Body.CreatorId = creatorId;
        }

        public WireEvent ToWire()
        {
            return new WireEvent
            {
                Body = new WireBody
                {
                    Transactions = Body.Transactions,
                    SelfParentIndex = Body.SelfParentIndex,
                    OtherParentCreatorId = Body.OtherParentCreatorId,
                    OtherParentIndex = Body.OtherParentIndex,
                    CreatorId = Body.CreatorId,
                    Timestamp = Body.Timestamp,
                    Index = Body.Index
                },
                Signiture = Signiture
                //R = Signiture.R,
               // S = Signiture.S
            };
        }
    }
    
    //Sorting
    //Todo: Sorting extensions

    //++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
    // WireEvent

    public class WireBody
    {
        public byte[][] Transactions { get; set; }

        public int SelfParentIndex { get; set; }
        public int OtherParentCreatorId { get; set; }
        public int OtherParentIndex { get; set; }
        public int CreatorId { get; set; }

        public DateTime Timestamp { get; set; }
        public int Index { get; set; }
    }

    public class WireEvent
    {
        public WireBody Body { get; set; }
        public byte[] Signiture { get; set; }

        //public ulong R { get; set; }
        //public ulong S { get; set; }
    }
}