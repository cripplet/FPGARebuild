using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FPGARebuild.Utils {
	class Utils {
		// avoid dividing by zero
		public const double epsilon = 0.0001;

		/**
		 * Output to console
		 */
		public static void Write(string s, bool truncate = false, bool inline = false) {
			if(truncate) {
				s = s.Substring(0, Math.Min(Console.WindowWidth - 1, s.Length));
			}
			if(inline) {
				Console.Write(s);
			} else {
				Console.WriteLine(s);
			}
		}

		public static void Debug(string s, bool truncate = false, bool inline = false) {
			bool debug = true;
			if(debug) {
				Write("Debug : " + s, truncate, inline);
			}
		}

		/**
		 * Outputs a warning to console
		 */
		public static void Warn(string s) {
			Write("Warning : " + s);
		}

		/**
		 * Prompts user for input
		 * Returns an ASCII string response
		 */
		public static string Prompt(string s) {
			Write(s + " : ", inline : true);
			return(Console.ReadLine());
		}

		/**
		 * Create an n-length, 0x00-initialized byte array
		 */
		public static byte[] Zero(int n) {
			byte[] output = new byte[n];
			for(int i = 0; i < n; i++) {
				output[i] = 0x00;
			}
			return(output);
		}

		/**
		 * Converts a byte array into big-endian form
		 * 
		 * From the Martinis source code (utils.py), we are to invert the byte ordering within a word
		 *		without checking for endianness.
		 * 
		 * As most computers these days are little-endian, it would then mean that we are in fact
		 *		converting into big-endian form
		 * 
		 * This can be verified by monitoring the byte-stream when using the Martinis GHz_DAC_bringup.py script,
		 *		and this program on the same computer, and searching for the (big-endian) sequence { 0x93, 0xC0, 0x1F };
		 *		this sequence was given in dac.py in the inverted form.
		 * 
		 * The input byte array (bytes) here represents any contiguous segment of data
		 *		e.g. int32[], double[], boolean[], etc.
		 */
		public static byte[] BigEndian(byte[] bytes, int word_length = 4) {
			if(BitConverter.IsLittleEndian) {
				// if we wish to convert a single two or three-byte word
				//		we apply some niceties to ignore inputting the optional word_length
				word_length = Math.Min(word_length, bytes.Length);

				byte[] output = new byte[bytes.Length];
				for(int i = 0; i < bytes.Length / word_length; i++) {
					byte[] word = new byte[word_length];
					Buffer.BlockCopy(bytes, i * word_length, word, 0, word_length);
					Array.Reverse(word);
					Buffer.BlockCopy(word, 0, output, i * word_length, word.Length);
				}
				return(output);
			} else {
				return(bytes);
			}
		}

		/**
		 * Returns a byte array representing the data input, truncated to the appropriate length
		 */
		public static byte[] GetBytes(object obj, int length = 0) {
			byte[] full_representation = new byte[] {};

			if(obj is int) {
				full_representation = BitConverter.GetBytes((int) obj);
			} else if(obj is uint) {
				full_representation = BitConverter.GetBytes((uint) obj);
			} else if(obj is long) {
				full_representation = BitConverter.GetBytes((long) obj);
			} else if(obj is ulong) {
				full_representation = BitConverter.GetBytes((ulong) obj);
			} else if(obj is short) {
				full_representation = BitConverter.GetBytes((short) obj);
			} else if(obj is ushort) {
				full_representation = BitConverter.GetBytes((ushort) obj);
			} else if(obj is double) {
				full_representation = BitConverter.GetBytes((double) obj);
			} else if(obj is bool) {
				full_representation = BitConverter.GetBytes((bool) obj);
			} else if(obj is char) {
				full_representation = BitConverter.GetBytes((char) obj);
			} else {
				// throw exception here for non-supported type
			}

			// get the most significant n bytes from the full byte representation
			//
			// using BigEndian() ensures the byte representation is in the same order
			//		this may be too slow - if so, use an if / then and nest two BlockCopy() statements as needed instead
			full_representation = BigEndian(full_representation);
			if(length == 0) {
				return (full_representation);
			}
			byte[] trim_representation = Zero(length);
			if(BitConverter.IsLittleEndian) {
				Array.Reverse(full_representation);
			}
			Buffer.BlockCopy(full_representation, 0, trim_representation, 0, length);
			trim_representation = BigEndian(trim_representation);
			return(trim_representation);
		}

		/**
		 * Because littering multiple casts everywhere is bad
		 * 
		 * Use sparingly - integer values less than 255 can be implicitly casted into bytes
		 *		i.e. byte b = 0x32 is a valid expression, and is casted correctly into the correct byte value
		 *		http://bit.ly/19eTiNZ
		 * 
		 * Use cases should be limited to more complex expressions in which ints cannot be implicitly cast into bytes
		 */
		public static byte Byte(int n = 0x00) {
			// masking with 0xFF ensures 0 < n < 255
			return((byte) ((char) (n & 0xFF)));
		}

		/**
		 * Returns a string of the contents of a byte array in both hex and ASCII format
		 *		optionally, prints out the string
		 */
		public static string Show(byte[] data, bool verbose = true) {
			string output = "".PadRight(73, '-') + "\r\n";
			string text = System.Text.Encoding.ASCII.GetString(data);
			string hex_buffer = "";
			string txt_buffer = "";
			for(int i = 0; i < data.Length; i++) {
				hex_buffer += " " + data[i].ToString("X2");
				if(((uint) data[i] > 31) && ((uint) data[i] < 127)) {
					txt_buffer += text[i];
				} else {
					txt_buffer += " ";
				}
				if((i % 16) == 15) {
					// format : [ line number ] + [ 16 hex ] + [ 16 ASCII translation ]
					output += (i - 15).ToString().PadLeft(4, ' ') + "   " + hex_buffer + "  " + txt_buffer + "\r\n";
					hex_buffer = "";
					txt_buffer = "";
				}
			}
			if((hex_buffer != "") && (txt_buffer != "")) {
				output += ((data.Length / 16) * 16).ToString().PadLeft(4, ' ') + "   " + (hex_buffer + "  ").PadRight(50, ' ') + txt_buffer + "\r\n";
			}
			if(verbose) {
				Write(output);
			}
			return(output);
		}

		/**
		 * Appends line(s) into a log file
		 * 
		 * NB: only one StreamWriter file can be opened for a file at a time -
		 *		must employ locks invoke-side to prevent exceptions
		 */
		public static void Log(string message) {
			using(StreamWriter file = File.AppendText(Environment.CurrentDirectory + "\\debug_log.txt")) {
				file.WriteLine(DateTime.Now.ToShortTimeString() + " " + DateTime.Now.ToShortDateString());
				file.WriteLine(message);
			}
		}

		/**
		 * Writes a byte array into the log file
		 * 
		 * NB: must employ locks invoke-side to prevent exceptions
		 */
		public static void Log(byte[] data, string header) {
			Log(data.Length.ToString() + " bytes " + header + "\r\n" + Show(data, verbose : false));
		}

		/**
		 * Converts an input number (double, less than unity) into the SRAM representation
		 * 0x03fff masks the output, and 0x4001 (?)
		 * 
		 * Look to GHz_DAC_bringup.py : makeSines() for guidance
		 *		(normalization procedure in the Python script has been moved to Utils.Normalize())
		 * 
		 * 0x1fff : max int for a 14-bit 2's complement
		 * 0x3fff : mask by the last 14 bits
		 * 0x4001 : equivalent to (0x4000 + 0x0001)
		 *		multiplying by 0x4000 is equivalent to arithmetic left shift by 14 bits
		 *		multiplying by 0x0001 adds the original number to the end of the string
		 *		i.e. duplicating DAC A, B output
		 *		
		 * Look to V5 for specifications on SRAM word syntax
		 */
		public static byte[] SRAMInt(double n, byte shift = Board.DAC.dac_a | Board.DAC.dac_b) {
			int scaled_n = ((int) Math.Round(n * 0x1fff)) & 0x3fff;
			int output = 0x00;
			if((shift & Board.DAC.dac_a) == Board.DAC.dac_a) {
				output += scaled_n;
			}
			if((shift & Board.DAC.dac_b) == Board.DAC.dac_b) {
				output += scaled_n << 14;
			}
			return(GetBytes(output));
		}

		/**
		 * Normalizes a list of numbers to be between positive and negative unity
		 *		(as opposed to strictly positive normalization)
		 * 
		 * Look to GHz_DAC_bringup.py : makeSines() for guidance (maximum of the sine function is unity
		 *		dividing by the number of superimposed sine waves serves only to ensure the maximum of the final
		 *		result is still positive / negative unity)
		 */
		public static double[] Normalize(double[] waveform) {
			// http://bit.ly/1a2R5FT
			double norm = Math.Max(epsilon, waveform.Max(x => Math.Abs(x)));

			double[] output = waveform.Select(x => x / norm).ToArray();
			return(output);
		}
	}
}