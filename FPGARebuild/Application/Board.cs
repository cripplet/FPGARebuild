using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FPGARebuild.Board {
	/**
	 * The actual digital-analog converter, which will output different waveforms
	 * 
	 * Each board has two DACs which can be independently controlled
	 */
	class DAC {
		public const byte dac_null = 0x00;
		public const byte dac_a = 0x01;
		public const byte dac_b = 0x02;

		byte _name;
		public byte name {
			get { return(this._name); }
			set { this._name = value; }
		}

		byte _mode;
		public byte mode {
			get { return(this._mode); }
			set { this._mode = value; }
		}

		public DAC(byte name, byte mode) {
			this.name = name;
			this.mode = mode;
		}
	}

	class Board {
		#region Constants
		/**
		 * Board-defined constants, as listed in the Hover registry files (board build V8)
		 * 
		 * These constants may not be correct
		 * 'SRAM_LEN'			18432
		 * 'SRAM_PAGE_LEN'		9216
		 * 'SRAM_DELAY_LEN'		1024
		 * 'SRAM_BLOCK0_LEN'	16384
		 * 'SRAM_BLOCK1_LEN'	2048
		 * 'SRAM_WRITE_PKT_LEN'	256
		 */
		public const int fifo_counter = 3;
		public const int lvds_sd = 3;
		public const int lvds_phase = 180;
		public const int sram_delay_length = 1024;

		/**
		 * Constants found in dac.py
		 */
		public const int max_fifo_tries = 5;
		#endregion

		#region Instance Variables
		string _name;
		public string name {
			get { return(this._name); }
			set { this._name = value; }
		}

		Server.Server _server;
		Server.Server server {
			get { return(this._server); }
			set { this._server = value; }
		}

		DAC[] _dacs;
		public DAC[] dacs {
			get { return(this._dacs); }
			set { this._dacs = value; }
		}

		string _mac_address;
		public string mac_address {
			get { return(this._mac_address); }
			set { this._mac_address = value; }
		}
		#endregion

		public Board(Server.Server server, string mac_address, bool full_initialize = true) {
			this.server = server;
			this.mac_address = mac_address;
			
			this.dacs = new DAC[] {
				new DAC(DAC.dac_a, 0x02),
				new DAC(DAC.dac_b, 0x03)
			};
			this.Initialize(full : full_initialize);
		}

		#region Utilities
		/**
		 * The ECL output is not syncing to the DAC for some reason - this may be a possible way to calibrate ECL / DAC output
		 * 
		 * ghz_fpga_server.py : dac_reset_phasor
		 * 
		 * @setting(1120, 'DAC Reset Phasor', returns='b: phase detector output')
		 * def dac_reset_phasor(self, c):
		 * fp.close
		 * """Resets the clock phasor. (DAC only)"""
		 * dev = self.selectedDAC(c)
		 * pkts = [
		 *		[152, 0, 127, 0],  # set I to 0 deg
         *		[152, 34, 254, 0], # set Q to 0 deg
		 *		[112, 65],         # set enable bit high
		 *		[112, 193],        # set reset high
		 *		[112, 65],         # set reset low
		 *		[112, 1],          # set enable low
		 *		[113, I2C_RB]]     # read phase detector
		 * r = yield dev.runI2C(pkts)
		 * returnValue((r[0] & 1) > 0)
		 */

		/**
		 * Initializes the 1GHz PLL, the DAC, and the LVDS SD
		 */
		public void Initialize(bool full) {
			this.Light(0);
			this.PLLReset();
			foreach(DAC dac in this.dacs) {
				this.DACInitialize(dac);
				if(full) {
					Utils.Utils.Debug(dac.name.ToString() + " LVDSReset : " + this.LVDSReset(dac).ToString());
					Utils.Utils.Debug(dac.name.ToString() + " FIFOReset : " + this.FIFOReset(dac).ToString());
					this.RunBIST(dac);
				}
			}
		}

		/**
		 * Note that the ping packet pattern as described in http://bit.ly/13VbJON is incorrect
		 */
		public void Ping() {
			Data.Data packet = new Data.RegisterWriteData(start : 0x00, readback : 0x01);
			while(!this.server.halt) {
				this.Delay(1000);
				this.server.Send(this.mac_address, packet.data);
			}
		}

		/**
		 * Reset the PLL loop for the 1GHz clock
		 */
		public void PLLReset() {
			List<Data.Data> packets = new List<Data.Data>() {
				new Data.RegisterWriteData(start : 0x00, readback : 0x00)
			};
			// activate 1GHz oscillating crystal
			packets.AddRange(Data.SerialData.LoadSerialData(0x01, new int[] { 0x1FC093, 0x1FC092, 0x100004, 0x000C11 }, readback: 0x00));

			// run test function
			packets.Add(new Data.RunSRAM(0x00, 0x00, continuous: false, sram_offset: 0, sync: 0xF9));
			
			// reset 1GHz PLL LED
			packets[0].data[46] = 0x80;
			this.server.SendAll(this.mac_address, packets.Select(x => x.data).ToArray());
		}

		/**
		 * Initializes the digital-analog converters
		 */
		public void DACInitialize(DAC dac) {
			Data.Data[] packets = Data.SerialData.LoadSerialData(dac.mode, new int[] { 0x000024, 0x000004, 0x001603, 0x000500 }, readback : 0x00);
			this.server.SendAll(this.mac_address, packets.Select(x => x.data).ToArray());
		}

		/**
		 * Sets the LVDS
		 * 
		 * dac.py : setLVDS
		 */
		public bool LVDSReset(DAC dac) {
			bool success = true;

			int t = lvds_sd & 0x0F;
			int msd = -1;
			int mhd = -1;
			bool set = true;

			int[] int_data = new int[65];
			int_data[0] = 0x000500 + (t << 4);
			for(int i = 0; i < 16; i++) {
				int_data[1 + i * 4 + 0] = 0x000400 + (i << 4);
				int_data[1 + i * 4 + 1] = 0x008500;
				int_data[1 + i * 4 + 2] = 0x000400 + i;
				int_data[1 + i * 4 + 3] = 0x008500;
			}

			Data.Data[] queries = Data.SerialData.LoadSerialData(dac.mode, int_data, readback : 0x01);
			Data.RegisterResponse[] responses = Data.RegisterResponse.LoadRegisterResponse(server.SendAll(this.mac_address, queries.Select(x => x.data).ToArray()));

			bool[] msd_bits = new bool[16];
			bool[] mhd_bits = new bool[16];
			for(int i = 0; i < 16; i++) {
				msd_bits[i] = (responses[i * 4 + 2].serial_dac & 0x01) == 0x01;
				mhd_bits[i] = (responses[i * 4 + 4].serial_dac & 0x01) == 0x01;
			}
			bool[] msd_switch = new bool[15];
			bool[] mhd_switch = new bool[15];
			for(int i = 0; i < 15; i++) {
				msd_switch[i] = msd_bits[i + 1] != msd_bits[i];
				mhd_switch[i] = mhd_bits[i + 1] != mhd_bits[i];
			}

			int lead_edge = System.Array.IndexOf(msd_switch, true);
			int last_edge = System.Array.IndexOf(mhd_switch, true);
			int msd_sum = msd_switch.Select(x => Convert.ToInt32(x)).Sum();
			int mhd_sum = mhd_switch.Select(x => Convert.ToInt32(x)).Sum();
			if(set) {
				if(msd_sum == 1) {
					msd = lead_edge;
				}
				if(mhd_sum == 1) {
					mhd = last_edge;
				}
			}

			if((Math.Abs(last_edge - lead_edge) <= 1) && (msd_sum == 1) && (mhd_sum == 1)) {
				success &= true;
			} else {
				success &= false;
			}
			return(success);
		}

		/**
		 * Sets FIFO
		 * 
		 * dac.py : setFIFO
		 */
		public bool FIFOReset(DAC dac) {
			int target_fifo = Board.fifo_counter;
			bool clock_invert = false;

			int tries = 1;
			bool success = false;

			server.Send(this.mac_address, new Data.ClockPolarity(dac.name, clock_invert, readback : 0x00).data);

			while(tries <= Board.max_fifo_tries && !success) {
				Data.Data[] queries = Data.SerialData.LoadSerialData(dac.mode, new int[] {
					0x000700, 0x008700, 0x000701, 0x008700, 0x000702, 0x008700, 0x000703, 0x008700
				}, readback : 0x01);
				Data.RegisterResponse[] responses = Data.RegisterResponse.LoadRegisterResponse(server.SendAll(this.mac_address, queries.Select(x => x.data).ToArray()));

				List<int> fifo_counters = new List<int>();
				foreach(int i in new int[] { 1, 3, 5, 7 }) {
					fifo_counters.Add((responses[i].serial_dac >> 4) & 0x0F);
				}

				success = this.CheckPHOF(dac, fifo_counters.ToArray(), target_fifo);
				if(success) {
					break;
				} else {
					clock_invert = !clock_invert;
					server.Send(this.mac_address, new Data.ClockPolarity(dac.name, clock_invert, readback: 0x00).data);
					tries += 1;
				}
			}
			return(success);
		}

		public bool CheckPHOF(DAC dac, int[] fifo_counters, int target_fifo) {
			bool success = true;
			int[] PHOFs = fifo_counters.Where(x => (x == target_fifo)).ToArray();
			if(PHOFs.Length > 0) {
				foreach(int PHOF in PHOFs) {
					Data.Data[] queries = Data.SerialData.LoadSerialData(dac.mode, new int[] { 0x000700 + PHOF, 0x008700 }, readback : 0x01);
					Data.RegisterResponse[] responses = Data.RegisterResponse.LoadRegisterResponse(this.server.SendAll(this.mac_address, queries.Select(x => x.data).ToArray()));
					if(((responses[0].serial_dac >> 4) & 0x0F) == target_fifo) {
						success = true;
						break;
					}
				}
			} else {
				success = false;
			}
			return(success);
		}

		/**
		 * Runs the BIST function (built-in self test)
		 * 
		 * Not completely implemented as of 09.06.2013
		 * 
		 * dac.py : runBIST
		 */
		public void RunBIST(DAC dac) {
			WaveForm.ZeroWave wave = new WaveForm.ZeroWave();
			Data.Data[] packets = wave.Prep().Run();
			this.server.SendAll(this.mac_address, packets.Select(x => x.data).ToArray());

			WaveForm.RandomWave bist = new WaveForm.RandomWave(1004);
			for(int i = 0; i < 4; i++) {
				bist.wave[i] = 0.0;
			}
			bist.Prep(shift : dac.name);
			Data.Data[] bist_packets = bist.Run();
			// send all but the last packet from bist_packets
			// this last packet executes the wave - this is sent later
			server.SendAll(this.mac_address, bist_packets.Take(bist_packets.Length - 1).Select(x => x.data).ToArray());

			Data.Data[] serial_packets = Data.SerialData.LoadSerialData(dac.mode, new int[] { 0x000004, 0x001107, 0x001106 }, readback : 0x00);
			this.server.SendAll(this.mac_address, serial_packets.Select(x => x.data).ToArray());

			// executes the wave
			server.Send(this.mac_address, bist_packets[bist_packets.Length - 1].data);

			Data.Data[] queries = Data.SerialData.LoadSerialData(dac.mode, new int[] {
				0x001126, 0x009200, 0x009300, 0x009400, 0x009500,
				0x001166, 0x009200, 0x009300, 0x009400, 0x009500,
				0x0011A6, 0x009200, 0x009300, 0x009400, 0x009500,
				0x0011E6, 0x009200, 0x009300, 0x009400, 0x009500
			}, readback : 0x01);

			Data.RegisterResponse[] responses = Data.RegisterResponse.LoadRegisterResponse(server.SendAll(this.mac_address, queries.Select(x => x.data).ToArray()));
		}

		/**
		 * Turn on LEDs Dx1 through Dx8 in binary representation of the 8-bit, unsigned integer n
		 *		where Dx8 represents the least significant bit
		 *		e.g. if n = 1, only Dx8 should light
		 */
		public void Light(int n) {
			byte[] command = new byte[] {
				0xC8, 0x44, Utils.Utils.Byte(n)
			};
			Data.Data packet = new Data.I2C(command, 0x00, 0x00, readback : 0x00);
			this.server.Send(this.mac_address, packet.data);
		}

		/**
		 * Delay on the FPGA side the next command for n milliseconds
		 */
		public void Delay(int n) {
			// placeholder code here - Thread.Sleep() very poorly replicates this behavior
			Thread.Sleep(n);
		}
		#endregion

		#region Demos
		public void LightShow() {
			System.Random dice = new System.Random();
			while(!this.server.halt) {
				for(int j = 0; j < 3; j++) {
					for(int i = 0; i < 8; i++) {
						this.Light(System.Convert.ToInt32(System.Math.Pow(2, i)));
					}
					for(int i = 0; i < 8; i++) {
						this.Light(System.Convert.ToInt32(System.Math.Pow(2, 7 - i)));
					}
				}
				for(int i = 0; i < 6; i++) {
					this.Light(255);
					this.Light(0);
				}
				for(int i = 0; i < 20; i++) {
					this.Light(dice.Next(0, 256));
				}
				this.Light(0);
			}
		}

		public void TestFunction(string mode, double[] heights = null) {
			if(heights == null) {
				heights = new double[] { -1.0, 0.0, 0.5 };
			}

			WaveForm.WaveForm sine_wave = new WaveForm.SineWave(.1);
			WaveForm.WaveForm step_wave = new WaveForm.StepWave(heights, new int[] { 10, 10, 10 });
			WaveForm.WaveForm abst_wave = new WaveForm.AbstractWave(new WaveForm.WaveForm[] { sine_wave, step_wave });

			Data.Data[] packets;
			switch(mode) {
				case "sine" :
					packets = sine_wave.Prep().Run();
					break;
				case "step" :
					packets = step_wave.Prep().Run();
					break;
				case "differentiate" :
					packets = abst_wave.Prep(shift : DAC.dac_a).Run(append : step_wave.Prep(shift : DAC.dac_b));
					break;
				case "abstract" :
				default :
					packets = abst_wave.Prep().Run();
					break;
			}

			this.server.SendAll(this.mac_address, packets.Select(x => x.data).ToArray());
		}
		#endregion
	}
}
