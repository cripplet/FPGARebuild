using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/**
 * Classes to build data arrays to be loaded into the SRAM
 * 
 * Example:
 * 
 * WaveForm wave = new SineWave(0.1);		// construct the wave as double[]
 * Data[] packets = wave.Prep().Run();		// convert double[] into byte[], and construct appropriate packets
 * foreach(Data packet in packets) {
 *		Send(packet.data);					// loads wave into SRAM, executes wave
 * }
 * 
 * See Server.TestFunction() for a more in-depth example
 */
namespace FPGARebuild.WaveForm {
	/**
	 * Base class for all waveforms
	 */
	abstract class WaveForm {
		double[] _wave;
		public double[] wave {
			get { return(this._wave); }
			set { this._wave = value; }
		}

		byte[] _data;
		public byte[] data {
			get { return(this._data); }
			set { this._data = value; }
		}

		public WaveForm(double[] wave = null) {
			if(!(wave == null)) {
				this.wave = wave;
			}
		}

		/**
		 * Creates a data array of this.wave
		 * 
		 * trigger_offset refers to the position of the ECL trigger relative to the wave;
		 *		according to the Martinis wiki, the ECL output is somewhat unreliable, and
		 *		the offset may need to be tuned so that the wave triggers appropriately
		 *		i.e. trigger_offset = 1 means that trigger_signal will be added to the second word of SRAM
		 * 
		 * Here, we have defined the high ECL bits (0x80 and 0x40) to be used for triggering DAC B output,
		 *		and the low ECL bits (0x20 and 0x10) to be used for triggering DAC A output;
		 *		0x80 corresponds to S3, and 0x10 corresponds to S0
		 */
		public virtual WaveForm Prep(int trigger_offset = 0, byte shift = Board.DAC.dac_a | Board.DAC.dac_b, bool normalize = true) {
			byte trigger_signal = 0x00;
			if((shift & Board.DAC.dac_a) == Board.DAC.dac_a) {
				trigger_signal += 0x30;
			}
			if((shift & Board.DAC.dac_b) == Board.DAC.dac_b) {
				trigger_signal += 0x30 << 2;
			}
			// ensure the ECL output is limited to the first four bits of the SRAM word
			trigger_signal &= 0xF0;

			if(normalize) {
				this.wave = Utils.Utils.Normalize(this.wave);
			}

			// map this.wave to SRAM bit representation
			//		32-bit SRAM word for each nanosecond output (i.e. per element in this.wave)
			this.data = new byte[4 * this.wave.Length];
			for(int i = 0; i < this.wave.Length; i++) {
				Buffer.BlockCopy(Utils.Utils.SRAMInt(wave[i], shift: shift), 0, this.data, i * 4, 4);
			}

			/**
			 * Set the ECL output (trigger) at the appropriate waveform offset
			 * 
			 * As per V5, each on bit in the first four bits of a word sequence (the ECL bits)
			 *		controls the output of an S+, S- pair on the board, with 0x10 controlling S0,
			 *		0x20 controlling S1, and so on
			 * 
			 * The ECL bits set to 0xF0 indicates each ECL output port will flip unity at trigger_offset
			 *		position relative to the waveform
			 */
			trigger_offset = Math.Min(trigger_offset * 4, ((this.data.Length - 1) / 4) * 4);
			this.data[trigger_offset] = Utils.Utils.Byte(this.data[trigger_offset] + trigger_signal);

			// makes calling Run() easier (i.e. this.Prep().Run())
			return(this);
		}

		/**
		 * Returns an array of data packets to be sent to the board
		 *		in order to execute the waveform
		 * 
		 * "append" is a secondary waveform whose append.data may be added to the current waveform;
		 *		this is useful if this.data is intended to be output onto DAC A,
		 *		and append.data is intended to be output onto DAC B (or vice versa)
		 * 
		 * this.data and append.data must have the same length (according to V5, Data.RunSRAM()
		 *		calls upon a block of SRAM, each entry of which contains both information for DAC A and
		 *		DAC B output - thus, mismatching waveform lengths will result in errors in output)
		 * 
		 * In the case that this.data and append.data has differing waveform lengths,
		 *		this routine has chosen to copy as many bytes as possible from append.data to this.data;
		 *		we will employ zero-padding if append.data is shorter than this.data, and
		 *		we will trim this.append in the case that the array is longer than this.data
		 * 
		 * Example:
		 * 
		 * WaveForm output_a = new SineWave(length : 40);
		 * WaveForm output_b = new StepWave(new double[] { 0.1, 0.2 }, new int[] { 10, 20 });
		 * Data.Data[] packets = output_a.Prep(shift : Board.DAC.dac_a).Run(append : output_b.Prep(shift : Board.DAC.dac_b))
		 * 
		 * Here, we are outputting a sine wave on DAC A, and a step wave on DAC B;
		 *		note that output_a has an SRAM length of 40 words, whereas
		 *		output_b has an SRAM length of 30 words - here,
		 *		we are to extend output_b by ten 0x00000000 words, and copy the result into output_a
		 */
		public virtual Data.Data[] Run(bool continuous = true, WaveForm append = null, byte readback = 0x00) {
			if((this.data == null) || (append != null && append.data == null)) {
				// indicates Prep() has not been invoked yet
				// TODO : need to throw an exception here
			}
			if(append != null) {
				for(int i = 0; i < Math.Min(this.data.Length, append.data.Length); i++) {
					this.data[i] += append.data[i];
				}
			}
			// create the packet which actually will execute the waveform
			List<Data.Data> packets = new List<Data.Data>(Data.SRAMData.LoadSRAMData(this.data));
			Data.Data packet = new Data.RunSRAM(0, this.data.Length / 4, continuous, 0, readback : readback);
			packets.Add(packet);

			return(packets.ToArray());
		}
	}

	/**
	 * Resets the output sequence on the oscilloscope, and clears out a short segment of the SRAM
	 */
	class ZeroWave : WaveForm {
		public ZeroWave() : base(wave : new double[32]) {
		}

		public override WaveForm Prep(int trigger_offset = 0, byte shift = Board.DAC.dac_a | Board.DAC.dac_b, bool normalize = true) {
			return (base.Prep(shift : Board.DAC.dac_null));
		}

		public override Data.Data[] Run(bool continuous = true, WaveForm append = null, byte readback = 0x00) {
			return (base.Run(continuous : false));
		}
	}
	
	class SineWave : WaveForm {
		/**
		 * Frequency is in units of GHz, and
		 *		length refers to the temporal length of the waveform
		 */
		public SineWave(double frequency, int length = 40) : base() {
			this.wave = new double[length];
			for(int i = 0; i < length; i++) {
				this.wave[i] = Math.Sin(2.0 * Math.PI * i * frequency);
			}
		}
	}

	/**
	 * A step function which ranges over a double[] of heights, for a duration of
	 *		int[] lengths
	 * 
	 * Example:
	 * 
	 * heights = { 0.1, 0.2 };
	 * lengths = { 10, 20 };
	 * 
	 * Here, the wave will persist at 0.1V for 10 frames (10 nanoseconds),
	 *		then step to 0.2V for 20 frames (20 nanoseconds)
	 * 
	 * Rise / fall time was experimentally measured to be ~2.5 nanoseconds (analog standard definition)
	 *		http://bit.ly/hSoBUj
	 */
	class StepWave : WaveForm {
		public StepWave(double[] heights, int[] lengths) : base() {
			this.wave = new double[lengths.Sum()];
			for(int i = 0; i < heights.Length; i++) {
				double[] buffer = new double[lengths[i]].Select(x => heights[i]).ToArray();
				// In C#, doubles are 8 bytes long, and BlockCopy() is copying bytes
				//		Faster than using Array.Copy()
				Buffer.BlockCopy(buffer, 0, this.wave, lengths.Take(i).Sum() * sizeof(double), buffer.Length * sizeof(double));
			}
		}
	}

	/**
	 * Superposition of waves
	 */
	class AbstractWave : WaveForm {
		public AbstractWave(WaveForm[] wave_forms) : base() {
			double[][] waves = wave_forms.Select(x => x.wave).ToArray();

			// initialize the superpositioned wave with the longest wave available
			this.wave = new double[waves.Max(x => x.Length)];

			foreach(double[] wave in waves) {
				for(int i = 0; i < wave.Length; i++) {
					this.wave[i] += wave[i];
				}
			}
		}

		/**
		 * WaveForm.WaveForm is abstract - cannot be invoked;
		 *		AbstractWave(double[] data) acts as a wrapper class
		 */
		public AbstractWave(double[] data) : base(wave : data) {
		}
	}

	/**
	 * A random n-lengthed wave of non-negative numbers
	 */
	class RandomWave : WaveForm {
		public RandomWave(int length) : base() {
			this.wave = new double[length];

			Random dice = new Random();
			for(int i = 0; i < length; i++) {
				this.wave[i] = dice.NextDouble();
			}
		}
	}
}