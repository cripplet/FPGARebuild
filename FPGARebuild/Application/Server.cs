using PcapDotNet.Core;
using PcapDotNet.Packets;
using PcapDotNet.Core.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using PcapDotNet.Packets.Ethernet;

namespace FPGARebuild.Server {
	class Server {
		#region Instance Variables
		IList<LivePacketDevice> _devices;
		IList<LivePacketDevice> devices {
			get { return(this._devices); }
			set { this._devices = value; }
		}

		LivePacketDevice _active;
		LivePacketDevice active {
			get { return(this._active); }
			set { this._active = value; }
		}

		/**
		 * Actively filters packets by length
		 *		if incoming packet length does not match length filter, then do not enqueue into the read buffer
		 * 
		 * Note that length_filter refers to the payload length, not the total length of the incoming packet
		 */
		int _length_filter;
		public int length_filter {
			get { return(this._length_filter); }
			set { this._length_filter = value; }
		}

		/**
		 * Data buffers manage the queue of information flowing to and from an active interface (e.g. ethernet port)
		 *		each buffer is a queue of byte arrays
		 */
		Queue _r_buff = Queue.Synchronized(new Queue());
		Queue r_buff {
			get { return(this._r_buff); }
			set { this._r_buff = value; }
		}

		/**
		 * Data buffer for redirected output
		 */
		Queue _d_buff = Queue.Synchronized(new Queue());
		Queue d_buff {
			get { return(this._d_buff); }
			set { this._d_buff = value; }
		}

		/**
		 * If set to true, write the read buffer to data buffer
		 * 
		 * Reset the data buffer if redirect is reset from false to true
		 */
		bool _readback = false;
		bool readback {
			get { return(this._readback); }
			set {
				if(!this._readback && value) {
					this.d_buff = Queue.Synchronized(new Queue());
				}
				this._readback = value;
			}
		}

		#region access protection
		// write lock - locked during Server.Write() to prevent simultaneous writes to the board
		public object w_lock = new object();
		// logging lock - locked to protect Utils.Log() from simultaneous file accesses
		public object l_lock = new object();
		#endregion access protection

		#region server status
		bool _halt = false;
		public bool halt {
			get { return(this._halt); }
			set { this._halt = value; }
		}
		#endregion
		#endregion

		public Server(bool auto = false) {
			// readback packet has payload length 70 bytes, plus 14 header bytes (two MACs and a two-byte length designation)
			this.length_filter = 70;
			this.Detect();
			this.Select(auto);
		}

		/**
		 * Runs the PacketServer instance
		 */
		public void Run() {
			Console.CancelKeyPress += delegate {
				this.halt = true;
				Utils.Utils.Write("Shutting down...");
			};

			// l_thread - listening thread (adds to read buffer)
			// r_thread - reading thread (process the read buffer)
			Thread l_thread = new Thread(new ThreadStart(this.Listen));
			Thread r_thread = new Thread(new ThreadStart(this.Read));

			l_thread.Start();
			r_thread.Start();
		}

		/**
		 * Actively processes the read buffer
		 */
		void Read() {
			while(!this.halt) {
				if(this.r_buff.Count > 0) {
					byte[] response = (byte[]) this.r_buff.Dequeue();
					lock(this.l_lock) {
						Utils.Utils.Log(response, "received");
					}
				}
			}
		}

		/**
		 * Enqueues the data to be written to the write buffer
		 * 
		 * The FPGA board is in big-endian format - we wish to transmit all data to the board
		 *		in big-endian form; Send() assumes that all data has been pre-formatted as desired
		 * 
		 * Endianness should have been handled in the Data classes
		 * 
		 * mac_address here refers to the destination FPGA
		 */
		public byte[] Send(string mac_address, byte[] packet) {
			if(!this.halt) {
				// no other write commands can be queued and / or executed during this time
				lock(this.w_lock) {
					this.Speak(mac_address, packet);
					byte[] response = new byte[0];
					// wait for response
					if(this.readback) {
						while(this.d_buff.Count == 0);
						// flush response
						if(this.d_buff.Count > 0) {
							response = (byte[]) this.d_buff.Dequeue();
						}
					}
					this.readback = false;
					return(response);
				}
			}
			return(new byte[0]);
		}

		/**
		 * Wrapper to send a series of packets
		 */
		public byte[][] SendAll(string mac_address, byte[][] packets) {
			List<byte[]> responses = new List<byte[]>();
			foreach(byte[] packet in packets) {
				byte[] response = this.Send(mac_address, packet);
				if(response.Length > 0) {
					responses.Add(this.Send(mac_address, packet));
				}
			}
			return(responses.ToArray());
		}

		/**
		 * Invoked from Server.Send(); actively sends the data given
		 * 
		 * Can (and should) be modified to actively handle a buffer of data and run as a thread
		 * 
		 * mac_address refers to the destination FPGA
		 */
		void Speak(string mac_address, byte[] data) {
			this.Validate();

			// "using" keyword ensures the channel is disposed of properly (i.e. dropped) as soon as we exit scope
			using(PacketCommunicator channel = active.Open(65536, PacketDeviceOpenAttributes.Promiscuous, 1000)) {
				Utils.Utils.Write("Speaking on active channel " + active.Name, truncate: true);
				if(!this.halt) {
					EthernetLayer ethernet = new EthernetLayer {
						Source = active.GetMacAddress(),
						Destination = new MacAddress(mac_address),
						/**
						 * We are using the IEEE 802.3 protocol
						 *		the EtherType field designates instead the length of the payload
						 *		http://bit.ly/16szTDm
						 *
						 * http://bit.ly/14hiChO
						 * 
						 * In the V5 specifications sheet (http://bit.ly/1ccaXXH), the payload length designates the command type
						 *		in the FPGA board and is represented schematically by the l(1) and l(2)
						 *		registers
						 */
						EtherType = (PcapDotNet.Packets.Ethernet.EthernetType) data.Length
					};
					PayloadLayer payload = new PayloadLayer {
						Data = new Datagram(data)
					};
					PacketBuilder builder = new PacketBuilder(ethernet, payload);
					Packet packet = builder.Build(DateTime.Now);

					// determine if we are to pause until board transmits data
					this.readback = false;
					if((data.Length == Data.RegisterWriteData.length) && (packet.Buffer[15] != 0x00)) {
						this.readback = true;
					}

					channel.SendPacket(packet);
					lock(this.l_lock) {
						Utils.Utils.Log(packet.Buffer, "sent");
					}
				}
			}
		}

		/**
		 * Actively filters incoming packets
		 *		http://bit.ly/1cwbr8U
		 */
		void Listen() {
			this.Validate();

			// "using" keyword ensures the channel is disposed of properly (i.e. dropped) as soon as we exit scope
			using(PacketCommunicator channel = active.Open(65536, PacketDeviceOpenAttributes.Promiscuous, 1000)) {
				Utils.Utils.Write("Listening on active channel " + active.Name, truncate: true);
				while(!this.halt) {
					channel.ReceivePackets(10, this.PacketHandler);
				}
			}
		}

		/**
		 * Filters each incoming packet
		 * 
		 * Three conditions must be met in order to accept the packet (i.e. not drop it):
		 *		1.) packet payload length is the same as the payload length filter
		 *		2.) the server is currently accepting packets
		 *		3.) the packet destination MAC (first 12 bytes of the packet) matches the ethernet interface out MAC
		 *			this last check is not implemented, as (for some reason) the check dramatically slows down the packet accept rate
		 */
		void PacketHandler(Packet packet) {
			string notice = "";
			if(((int) packet.Ethernet.EtherType == this.length_filter) && this.readback) {
					this.r_buff.Enqueue(packet.Buffer);
					this.d_buff.Enqueue(packet.Buffer);
			} else {
				notice = " - Rejected";
			}
			Utils.Utils.Write(packet.Timestamp.ToString("hh:mm:ss.fff") + " incoming packet (" + packet.Length + " bytes)" + notice);
		}

		#region Device Detection
		/**
		 * Ensure that an active interface is enabled
		 */
		bool Validate() {
			if(this.active == null) {
				Utils.Utils.Warn("No active interface");
				this.halt = true;
				return (false);
			}
			return (true);
		}

		/**
		 * Set active interface to communicate with
		 */
		void Select(bool auto = false) {
			if(this.devices.Count == 0) {
				Utils.Utils.Warn("No devices found - ensure WinPCap (winpcap.org) is installed");
				return;
			}

			int key = 0;
			if(!auto) {
				string index = Utils.Utils.Prompt("Select device index");
				if(int.TryParse(index, out key)) {
					key = Math.Max(0, Math.Min(key, devices.Count - 1));
				}
			} else {
				key = 1;
			}
			this.Info(key, devices[key]);
			this.active = devices[key];
		}

		/**
		 * Detects all ethernet / wifi ports available on machine
		 * 		http://bit.ly/1ciI7E8
		 */
		void Detect() {
			devices = LivePacketDevice.AllLocalMachine;

			Utils.Utils.Write("Searching for interfaces...");
			if(devices.Count == 0) {
				Utils.Utils.Warn("No interfaces found");
				return;
			}

			for(int i = 0; i < devices.Count; i++) {
				Info(i, devices[i], verbose : false);
			}
		}

		/**
		 * Outputs information on each port
		 */
		void Info(int key, LivePacketDevice device, bool verbose = true) {
			Utils.Utils.Write("dev" + key.ToString() + " : " + device.Name, truncate : true);
			Utils.Utils.Write("    Description : " + ((device.Description == null) ? "No description available" : device.Description), truncate : true);
			if(verbose) {
				Utils.Utils.Write("    Loopback : " + (((device.Attributes & DeviceAttributes.Loopback) == DeviceAttributes.Loopback) ? "yes" : "no"));
				foreach(DeviceAddress address in device.Addresses) {
					Utils.Utils.Write("    Address Family : " + address.Address.Family);
					Utils.Utils.Write("    Address : " + ((address.Address == null) ? "null" : address.Address.ToString()));
					Utils.Utils.Write("    Netmask : " + ((address.Netmask == null) ? "null" : address.Netmask.ToString()));
					Utils.Utils.Write("    Broadcast : " + ((address.Broadcast == null) ? "null" : address.Broadcast.ToString()));
					Utils.Utils.Write("    Destination : " + ((address.Destination == null) ? "null" : address.Destination.ToString()));
				}
			}
		}
		#endregion
	}
}