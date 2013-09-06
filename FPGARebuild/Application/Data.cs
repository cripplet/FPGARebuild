using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/**
 * We are following the IEEE 802.3 Ethernet standard, where an ethernet frame consists of
 *		Preamble		(7 octets)
 *		SFD				(1 octet)
 *		MAC Source		(6 octets)
 *		MAC Destination	(6 octets)
 *		Length			(2 octets)
 *		Payload			(42 - 1500 octets)
 *		FCS				(4 octets)
 * 
 * The 802.1Q tag (4 octets) is an optional part of the standard, and is not included in the construction
 *		of our ethernet packets, and the interframe gap does not count towards total ethernet packet length
 *		
 * Utils.Show() therefore displays the 24 octets (i.e. 8-bit bytes) relating to the frame,
 *		plus the n-lengthed payload (i.e. data)
 * 
 * Here, we are constructing the payload to send to the FPGA,
 *		following the V5 specifications on the Martinis wiki (http://bit.ly/1ccaXXH)
 */
namespace FPGARebuild.Data {
	/**
	 * Base payload class
	 * 
	 * All register locations (data indices) here are 0-indexed, whereas
	 *		register locations are 1-indexed in the V5 specifications
	 *		i.e., data[3] here refers to d(4) in the V5
	 */
	abstract class Data {
		protected byte[] _data;
		public byte[] data {
			get { return(this._data); }
			set { this._data = value; }
		}

		protected Data(int n) {
			data = Utils.Utils.Zero(n);
		}
	}

	/**
	 * Waveforms are stored as word sequences in SRAM, to be executed with instructions written to the registers (see Data.RunSRAM)
	 * 
	 * For one word w:
	 *		w[31:28]		ECL output (trigger pulse)
	 *		w[27:14]		DAC B output
	 *		w[13:00]		DAC A output
	 * 
	 * Each 14-bit sequence in the word represents a two's complement integer, with 0x1fff corresponding to +1V, and
	 *		0x3fff corresponding to -1V
	 * 
	 * ex. 0x0fffdfff represents an untriggered word, with DAC B outputting -1V and DAC A outputting +1V
	 *		{ 0000, 1111, 1111, 1111, 1101, 1111, 1111, 1111 }
	 * 
	 * The 1GH clock in the FPGA controls the stream rate of the words into the DAC and ECL output;
	 *		that is, one word is outputted per nanosecond
	 * 
	 * See V5 or WaveForm.WaveForm for more information
	 */
	class SRAMData : Data {
		public const int length = 1026;

		/**
		 * maximum 256 words per page written to the SRAM at a time
		 *		("page" corresponding to "derp" in V5)
		 */
		public const int page_size = 256;

		/**
		 * SRAM addresses are three bytes long, and are given here as separated bytes
		 *		address 0x0248ff is given as { 0x02, 0x48, 0xff }
		 */
		byte[] _sram_start;
		byte[] sram_start {
			get { return(this._sram_start); }
			set { this._sram_start = value; }
		}

		public SRAMData(int start_address, byte[] sram_data) : base(n : SRAMData.length) {
			// the sram_start value in the packet only cares about the 16 most significant bits in the address
			this.sram_start = Utils.Utils.GetBytes(start_address, 2);
			this.Construct(sram_data);
		}

		private void Construct(byte[] sram_data) {
			System.Buffer.BlockCopy(Utils.Utils.BigEndian(sram_start), 0, this.data, 0, sram_start.Length);
			System.Buffer.BlockCopy(Utils.Utils.BigEndian(sram_data), 0, this.data, 2, sram_data.Length);
		}

		/**
		 * Static method for constructing long waveforms (those spanning multiple SRAM pages)
		 * 		"data" here refers to the ENTIRE set of data to be loaded into the SRAM
		 *		The constructor will handle the loading of different pages into the FPGA board
		 */
		public static Data[] LoadSRAMData(byte[] data) {
			// TODO : Check max data.Length < 10240 * 4 (10240 words, each of which are 32 bits long)
			// expected error : Exception

			/**
			 * V5 spec provides a way in which to execute SRAM sequences longer than 10240 words;
			 *		this has not been implemented yet - see V5 spec, "SRAM Write" section for more information
			 */
			List<Data> packets = new List<Data>();

			// load SRAM from address 0x000000
			int pointer = 0;
			while(pointer < data.Length) {
				// get length of data to be written into new packet
				int length = Math.Min(page_size * 4, data.Length - pointer);

				// create a new packet to load another page of SRAM
				byte[] byte_buffer = new byte[page_size * 4];
				Buffer.BlockCopy(data, pointer, byte_buffer, 0, length);
				packets.Add(new SRAMData(pointer, byte_buffer));

				// advance the current instruction pointer
				pointer += length;
			}
			return(packets.ToArray());
		}
	}

	/**
	 * Sequence control and I2C commands - length 56 bytes
	 */
	class RegisterWriteData : Data {
		public const int length = 56;
		/**
		 * If the start byte is set to a value other than 0x00, the board will attempt to load instructions
		 *		from either data[13] onwards, or run directly from the SRAM
		 */
		byte _start;
		public byte start {
			get { return(this._start); }
			set { this._start = value; }
		}
		
		/**
		 * Controls the timing of the readback sequence
		 */
		byte _readback;
		public byte readback {
			get { return(this._readback); }
			set { this._readback = value; }
		}

		public RegisterWriteData(byte start, byte readback) : base(n : RegisterWriteData.length) {
			this.start = start;
			this.readback = readback;
			this.Construct();
		}

		public virtual void Construct() {
			this.data[0] = start;
			this.data[1] = readback;
		}
	}

	/**
	 * Base response class
	 */
	abstract class Response : Data {
		public const int length = 70;

		public Response(byte[] data) : base(n : Response.length) {
			Buffer.BlockCopy(data, 14, this.data, 0, Response.length);
		}
	}

	class RegisterResponse : Response {
		byte _serial_dac;
		public byte serial_dac {
			get { return (this._serial_dac); }
			set { this._serial_dac = value; }
		}

		public RegisterResponse(byte[] data) : base(data : data) {
			this.Construct();
		}

		public void Construct() {
			this.serial_dac = this.data[56];
		}

		/**
		 * Given an array of byte[] responses (probably after a call to Server.SendAll()),
		 *		construct a series of classes to wrap around the byte data
		 * 
		 * Example:
		 * 
		 * Data.Data[] queries = ...;
		 * Data.RegisterResponse[] responses = Data.RegisterResponse.LoadRegisterResponse(server.SendAll(queries.Select(x => x.data).ToArray()));
		 */
		public static RegisterResponse[] LoadRegisterResponse(byte[][] packets) {
			List<RegisterResponse> responses = new List<RegisterResponse>();
			foreach(byte[] packet in packets) {
				responses.Add(new RegisterResponse(packet));
			}
			return(responses.ToArray());
		}
	}

	/**
	 * I2C command data is loaded between data[5] and data[12] inclusive, with data[12] being the first byte of data
	 *		http://bit.ly/frIs0D
	 */
	class I2C : RegisterWriteData {
		/**
		 * Each bit in the read_write byte corresponds to an I2C data byte,
		 *		with 1 indicating a read, and 0 indicating a write bit
		 * 
		 * The leading bit of the read_write byte corresponds to the first I2C data byte
		 */
		byte _read_write;
		byte read_write {
			get { return(this._read_write); }
			set { this._read_write = value; }
		}

		/**
		 * Each bit in the acknowledge byte corresponds to an I2C data byte,
		 *		with 0 indicating acknowledgement output
		 * 
		 * The leading bit of the acknowledge byte corresponds to the first I2C data byte
		 */
		byte _acknowledge;
		byte acknowledge {
			get { return(this._acknowledge); }
			set { this._acknowledge = value; }
		}

		public I2C(byte[] i2c_data, byte read_write, byte acknowledge, byte readback = 0x02) : base(start : 0x00, readback : readback) {
			this.read_write = read_write;
			this.acknowledge = acknowledge;
			this.Construct(i2c_data);
		}

		public void Construct(byte[] i2c_data) {
			// TODO : assert I2C data is at max 8 bytes here
			// expected error : Exception
			
			Array.Reverse(i2c_data);

			// data[2] is the stop byte - processing is paused for some amount of time for each bit position set to 1
			//		we wish to pause processing after the I2C data is read
			//		http://bit.ly/1cKlE1C
			this.data[2] = Utils.Utils.Byte((0x01 << (8 - i2c_data.Length)));
			this.data[3] = this.read_write;
			this.data[4] = this.acknowledge;
			
			// copies the I2C command to the payload
			//
			// The I2C command is always in the native, procedural format - it is the job of the constructor
			//		to alter the format to fit the V5 specifications (i.e. into big endian form)
			System.Buffer.BlockCopy(i2c_data, 0, this.data, 12 - (i2c_data.Length - 1), i2c_data.Length);
		}
	}

	/**
	 * Serial command is loaded between data[48] and data[50] inclusive, with data[48] being the first byte of data
	 */
	class SerialData : RegisterWriteData {
		byte _mode;
		byte mode {
			get { return(this._mode); }
			set { this._mode = value; }
		}

		/**
		 * command should be three bytes long, in sequential order;
		 *		that is, if command = 0x1FC093, 0x1F will be loaded into data[48]
		 * 
		 * Byte ordering will be handled by the constructor
		 */
		public SerialData(byte mode, int command, byte readback = 0x01) : base(start : 0x00, readback : readback) {
			this.mode = mode;
			this.Construct(command);
		}

		public void Construct(int command) {
			byte[] serial_data = Utils.Utils.GetBytes(command, 3);

			this.data[47] = this.mode;
			System.Buffer.BlockCopy(Utils.Utils.BigEndian(serial_data), 0, this.data, 48, serial_data.Length);
		}

		/**
		 * Construct a list of SerialData packets
		 */
		public static Data[] LoadSerialData(byte mode, int[] commands, byte readback = 0x01) {
			List<Data> packets = new List<Data>();
			foreach(int command in commands) {
				packets.Add(new SerialData(mode, command, readback : readback));
			}
			return(packets.ToArray());
		}
	}

	/**
	 * Packet to execute the waveform loaded into SRAM
	 */
	class RunSRAM : RegisterWriteData {
		/**
		 * SRAM addresses are three bytes long, and are given here as separated bytes
		 *		address 0x0248ff is given as { 0x02, 0x48, 0xff }
		 */
		byte[] _sram_start;
		byte[] sram_start {
			get { return(this._sram_start); }
			set { this._sram_start = value; }
		}

		byte[] _sram_end;
		byte[] sram_end {
			get { return(this._sram_end); }
			set { this._sram_end = value; }
		}

		/**
		 * Number of blocks (1024 addresses) of SRAM to skip
		 */
		byte _sram_offset;
		byte sram_offset {
			get { return(this._sram_offset); }
			set { this._sram_offset = value; }
		}

		byte _sync;
		byte sync {
			get { return(this._sync); }
			set { this._sync = value; }
		}
		
		/**
		 * Constrols conditional SRAM execution
		 */
		byte _slave;
		byte slave {
			get { return(this._slave); }
			set { this._slave = value; }
		}

		/**
		 * If continuous output is set to true, the SRAM sequence loaded between sram_start and sram_end
		 *		will be executed continuously; otherwise, the SRAM sequence will be executed once
		 * 
		 * NB: sram_start and sram_end are the WORD start and end addresses, not BYTE addresses
		 *		i.e., SRAM address is byte address (data length) divided by four
		 */
		public RunSRAM(int sram_start, int sram_end, bool continuous, int sram_offset, byte slave = 0x00, byte sync = 0xF9, byte readback = 0x00) : base(start : ((continuous) ? (byte) 0x03 : (byte) 0x04), readback : readback) {
			this.sram_start = Utils.Utils.GetBytes(sram_start, length: 3);
			this.sram_end = Utils.Utils.GetBytes((sram_end - 1) + Board.Board.sram_delay_length * sram_offset, length : 3);
			this.sram_offset = Utils.Utils.Byte(sram_offset);
			this.slave = slave;

			this.Construct();
		}

		public new void Construct() {
			// TODO : Assert sram_start and sram_end are at max three bytes long
			// expected error : Exception

			System.Buffer.BlockCopy(Utils.Utils.BigEndian(this.sram_start), 0, this.data, 13, this.sram_start.Length);
			System.Buffer.BlockCopy(Utils.Utils.BigEndian(this.sram_end), 0, this.data, 16, this.sram_end.Length);
			this.data[19] = this.sram_offset;
			this.data[43] = this.slave;
			this.data[45] = this.sync;

			base.Construct();
		}
	}

	/**
	 * Constructs a packet to flip clock polarity
	 */
	class ClockPolarity : RegisterWriteData {
		byte _shift;
		public byte shift {
			get { return(this._shift); }
			set { this._shift = value; }
		}

		byte _polarity;
		public byte polarity {
			get { return(this._polarity); }
			set { this._polarity = value; }
		}

		public ClockPolarity(byte shift, bool invert, byte readback = 0x01) : base(start : 0x00, readback : readback) {
			this.shift = shift;
			this.Construct(invert);
		}

		public void Construct(bool invert) {
			if(this.shift == Board.DAC.dac_a) {
				this.polarity = Utils.Utils.Byte((0x01 << 4) + (Convert.ToByte(invert && true) << 0));
			} else if(this.shift == Board.DAC.dac_b) {
				this.polarity = Utils.Utils.Byte((0x01 << 5) + (Convert.ToByte(invert && true) << 1));
			}
			this.data[46] = polarity;
		}
	}
}
