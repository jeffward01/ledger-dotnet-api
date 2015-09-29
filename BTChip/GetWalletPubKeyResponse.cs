﻿using NBitcoin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTChip
{
    public class GetWalletPubKeyResponse
    {
        public GetWalletPubKeyResponse(byte[] bytes)
        {
            MemoryStream ms = new MemoryStream(bytes);
            var len = ms.ReadByte();
            UncompressedPublicKey = new PubKey(ms.ReadBytes(len));
            len = ms.ReadByte();
            var addr = Encoding.ASCII.GetString(ms.ReadBytes(len));
            Address = BitcoinAddress.GetFromBase58Data(addr);
            ChainCode = ms.ReadBytes(32);
        }
        public PubKey UncompressedPublicKey
        {
            get;
            set;
        }
        public Base58Data Address
        {
            get;
            set;
        }
        public byte[] ChainCode
        {
            get;
            set;
        }
    }
}
