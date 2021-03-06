﻿using HidLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using NBitcoin;
using System.Diagnostics;

namespace LedgerWallet.Transports
{
	public class U2FException : Exception
	{
		public U2FException(byte errorCode) : base(GetErrorMessage(errorCode))
		{
			_U2FError = errorCode;
		}


		private readonly byte _U2FError;
		public byte U2FError
		{
			get
			{
				return _U2FError;
			}
		}

		const int ERR_NONE = 0;
		const int ERR_INVALID_CMD = 1;
		const int ERR_INVALID_PAR = 2;
		const int ERR_INVALID_LEN = 3;
		const int ERR_INVALID_SEQ = 4;
		const int ERR_MSG_TIMEOUT = 5;
		const int ERR_CHANNEL_BUSY = 6;
		const int ERR_LOCK_REQUIRED = 10;
		const int ERR_INVALID_CID = 11;
		const int ERR_OTHER = 127;

		public override string ToString()
		{
			return GetErrorMessage(U2FError);
		}

		private static string GetErrorMessage(byte U2FError)
		{
			switch(U2FError)
			{
				case ERR_NONE:
					return "No error";
				case ERR_INVALID_CMD:
					return "The command in the request is invalid";
				case ERR_INVALID_PAR:
					return "The parameter(s) in the request is invalid";
				case ERR_INVALID_LEN:
					return "The length field (BCNT) is invalid for the request";
				case ERR_INVALID_SEQ:
					return "The sequence does not match expected value";
				case ERR_MSG_TIMEOUT:
					return "The message has timed out";
				case ERR_CHANNEL_BUSY:
					return "The device is busy for the requesting channel";
				default:
					return "Unknown error";
			}
		}
	}

	public class HIDU2FTransport : HIDTransportBase
	{
		byte cmd = 0x03;
		const byte CMD_APDU = 0x03;
		const byte CMD_INIT = 0x06;
		byte[] cid = new byte[] { 0xff, 0xff, 0xff, 0xff };
		const byte TYPE_INIT = 0x80;
		const int HID_RPT_SIZE = 64;
		const int U2F_HID_PACKET_SIZE = 64;

		const int STAT_ERR = 0xbf;

		public static VendorProductIds[] WellKnownU2F = new VendorProductIds[]
		{
				new VendorProductIds(0x1050, 0x0200),  // Gnubby
				new VendorProductIds(0x1050, 0x0113),  // YubiKey NEO U2F
				new VendorProductIds(0x1050, 0x0114),  // YubiKey NEO OTP+U2F
				new VendorProductIds(0x1050, 0x0115),  // YubiKey NEO U2F+CCID
				new VendorProductIds(0x1050, 0x0116),  // YubiKey NEO OTP+U2F+CCID
				new VendorProductIds(0x1050, 0x0120),  // Security Key by Yubico
				new VendorProductIds(0x1050, 0x0410),  // YubiKey Plus
				new VendorProductIds(0x1050, 0x0402),  // YubiKey 4 U2F
				new VendorProductIds(0x1050, 0x0403),  // YubiKey 4 OTP+U2F
				new VendorProductIds(0x1050, 0x0406),  // YubiKey 4 U2F+CCID
				new VendorProductIds(0x1050, 0x0407),  // YubiKey 4 OTP+U2F+CCID
				new VendorProductIds(0x2581, 0xf1d0),  // Plug-Up U2F Security Key
				new VendorProductIds(0x2c97, 0x0001),  // Nano S
		};

		protected HIDU2FTransport(HidDevice device) : base(device, _UsageSpecification)
		{
			ReadTimeout = TimeSpan.FromSeconds(0.5);
		}

		static readonly HIDDeviceTransportRegistry<HIDU2FTransport> _Registry;
		static HIDU2FTransport()
		{
			_Registry = new HIDDeviceTransportRegistry<HIDU2FTransport>(d => new HIDU2FTransport(d));
		}

		protected override void Init()
		{
			using(this.Lock())
			{
				cmd = CMD_INIT;
				try
				{
					var nonce = RandomUtils.GetBytes(8);
					var readenNonce = nonce.ToArray();
					byte[] response;
					Write(nonce);
					do
					{
						response = Read();
						Array.Copy(response, 0, readenNonce, 0, nonce.Length);
					} while(!readenNonce.SequenceEqual(nonce));
					Array.Copy(response, 8, cid, 0, cid.Length);
				}
				finally
				{
					cmd = CMD_APDU;
				}
			}
		}

		static UsageSpecification[] _UsageSpecification = new[] { new UsageSpecification(0xf1d0, 0x01) };
		public static unsafe IEnumerable<HIDU2FTransport> GetHIDTransports(IEnumerable<VendorProductIds> ids = null)
		{
			ids = ids ?? WellKnownU2F;
			return _Registry.GetHIDTransports(ids, _UsageSpecification);
		}

		protected override byte[] WrapCommandAPDU(Stream command, ref int sequenceIdx)
		{
			MemoryStream output = new MemoryStream();
			int position = (int)output.Position;
			output.Write(cid, 0, cid.Length);
			if(sequenceIdx == 0)
			{
				output.WriteByte((byte)(TYPE_INIT | cmd));
				output.WriteByte((byte)((command.Length >> 8) & 0xff));
				output.WriteByte((byte)(command.Length & 0xff));
			}
			else
			{
				output.WriteByte((byte)((sequenceIdx - 1) & 0x7f));
			}
			var headerSize = (int)(output.Position - position);
			int blockSize = Math.Min(U2F_HID_PACKET_SIZE - headerSize, (int)command.Length - (int)command.Position);
			var commantPart = command.ReadBytes(blockSize);
			output.Write(commantPart, 0, commantPart.Length);
			while((output.Length % U2F_HID_PACKET_SIZE) != 0)
				output.WriteByte(0);
			sequenceIdx++;
			Debug.Assert(output.Length == U2F_HID_PACKET_SIZE);
			return output.ToArray();
		}

		protected override byte[] UnwrapReponseAPDU(byte[] data, ref int sequenceIdx, ref int remaining)
		{
			MemoryStream output = new MemoryStream();
			MemoryStream input = new MemoryStream(data);
			int position = (int)input.Position;
			if(!input.ReadBytes(cid.Length).SequenceEqual(cid))
				return null;
			var cmd = input.ReadByte();

			if(sequenceIdx == 0)
			{
				if(cmd != (TYPE_INIT | this.cmd) && cmd != STAT_ERR)
					return null;
				remaining = ((input.ReadByte()) << 8);
				remaining |= input.ReadByte();
			}

			if(cmd == STAT_ERR)
				throw new U2FException((byte)input.ReadByte());

			if(sequenceIdx != 0 && cmd != (0x7f & (sequenceIdx - 1)))
				return null;
			var headerSize = input.Position - position;
			var blockSize = (int)Math.Min(remaining, U2F_HID_PACKET_SIZE - headerSize);
			byte[] commandPart = new byte[blockSize];
			if(input.Read(commandPart, 0, commandPart.Length) != commandPart.Length)
				return null;
			output.Write(commandPart, 0, commandPart.Length);
			remaining -= blockSize;
			sequenceIdx++;
			return output.ToArray();
		}
	}
}
