﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

//
// Start over in NOTPSXSERIAL.CS to get your bearings
//

using System;
using System.Threading;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;

public class TransferLogic
{

	public static SerialPort activeSerial => Program.activeSerial;

	/// <summary>
	/// Read a 32 bit unsigned int from the serial connection
	/// (Takes care of endianness)
	/// </summary>	
	public static UInt32 read32(){

		UInt32 val = (UInt32)activeSerial.ReadByte();
		val += ((UInt32) activeSerial.ReadByte() << 8 );
		val += ((UInt32) activeSerial.ReadByte() << 16);
		val += ((UInt32) activeSerial.ReadByte() << 24);

		return val;

	}

	/// <summary>
	/// Upload bytes to the specified address
	/// does verify contents
	/// does not execute or act upon the data
	/// </summary>	
	public static bool Command_SendBin( UInt32 inAddr, byte[] inBytes ){

		UInt32 checkSum = CalculateChecksum(inBytes);

		if (!ChallengeResponse( CommandMode.SEND_BIN ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes( inAddr ), 0, 4);
		activeSerial.Write(BitConverter.GetBytes( inBytes.Length ), 0, 4);
		// In a pinch, Unirom will gloss over a null checksum. Don't though.
		activeSerial.Write(BitConverter.GetBytes( checkSum ), 0, 4);

		// then the actual contents.

		return WriteBytes( inBytes, false );

	}

	/// <summary>
	/// Upload a ROM and attempt to flash to EEPROM
	/// </summary>	
	public static bool Command_SendROM( UInt32 inAddr, byte[] inBytes ){

		UInt32 checkSum = CalculateChecksum(inBytes);

		if ( !ChallengeResponse( CommandMode.SEND_ROM ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inBytes.Length), 0, 4);
		activeSerial.Write(BitConverter.GetBytes(checkSum), 0, 4);

		string flashResponse = "";

		while (true)
		{

			if (activeSerial.BytesToRead != 0)
			{
				// Why the fuck does readchar return an int?
				flashResponse += (char)activeSerial.ReadByte();

				// filter any noise at the start of the response
				// seems to happen once in a while
				if (flashResponse.Length > 4)
					flashResponse = flashResponse.Remove(0, 1);


			}

			Console.Write("\r EEPROM Check: " + flashResponse);

			if (flashResponse == "FITS")
			{
				Console.WriteLine("\n\nRom will fit! \n Response: " + flashResponse + "!");
				break;
			}

			if (flashResponse == "NOPE")
			{
				Console.WriteLine("\n\nThis rom is too big for the EEPROM! \n Response: " + flashResponse + "!");
				return false;
			}

			if (flashResponse == "NONE")
			{
				Console.WriteLine("\n\nNo EEPROM detected! \n The response was: " + flashResponse + "!");
				return false;
			}

			if (flashResponse == "UNKN")
			{
				Console.WriteLine("\n\nUnknown EEPROM detected! \n The response was: " + flashResponse + "!");
				return false;
			}


		}

		Console.WriteLine( "Checks passed; sending ROM!" );

		return WriteBytes( inBytes, false );

	}

	/// <summary>
	/// Uploads an .exe to its final execution address
	/// and launches it. May or may not clear .bss depending on
	/// your Unirom Version.
	/// Note: does not upload the header or checksum that area
	/// </summary>
	/// <param name="inAddr">Make sure it's correct</param>
	/// <param name="inBytes">Raw bytes minus the header</param>	
	public static bool Command_SendEXE( UInt32 inAddr, byte[] inBytes ){
		
		UInt32 checkSum = CalculateChecksum(inBytes, true);

		int mod = inBytes.Length % 2048;

		// Pad .PS-EXE files up to the 2k sector boundary
		// 2MB max, 8MB for dev unit, the GC can handle this.
		if (mod != 0)
		{

			Console.WriteLine("Padding to 2048 bytes...\n\n");

			int paddingRequired = 2048 - mod;
			byte[] newArray = new byte[inBytes.Length + paddingRequired];
			for (int i = 0; i < newArray.Length; i++)
			{
				newArray[i] = (i < inBytes.Length) ? inBytes[i] : (byte)0;
			}
			inBytes = newArray;

		}


		if ( !ChallengeResponse( CommandMode.SEND_EXE )	)
			return false;

		// An .exe with in-tact header sends the actual header over
		// followed by some choice meta data.
		//skipFirstSectorHeader = true;
		activeSerial.Write(inBytes, 0, 2048);

		// Write in the header		
		activeSerial.Write(inBytes, 16, 4);      // the .exe jump address
		activeSerial.Write(inBytes, 24, 4);      // the base/write address, e.g. where the linker org'd it
												//serialPort.Write( inFile, 28, 4 );		// size
												// let's not use the header-defined length, instead the actual file length minus the header
		activeSerial.Write(BitConverter.GetBytes(inBytes.Length - 0x800), 0, 4);

		activeSerial.Write(BitConverter.GetBytes(checkSum), 0, 4);
		Console.WriteLine("__DEBUG__Expected checksum: 0x" + checkSum.ToString("X8"));

		// We could send over the initial values for the fp and gp register, but 
		// GP is set via LIBSN or your Startup.s/crt0 and it's never been an issue afaik

		return WriteBytes( inBytes, true);

	}


	/// <summary>
	/// Jump immediately to the given address without
	/// touching the stack or $ra
	/// </summary>	
	public static bool Command_JumpAddr( UInt32 inAddr ){

		if ( !ChallengeResponse( CommandMode.JUMP_JMP ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);

		return true;

	}

	/// <summary>
	/// Call an address with the possibility of returning
	/// Note! This may or may not be in a critical section
	/// depending on whether you're using the kernel-resident SIO debugger!
	/// </summary>	
	public static bool Command_CallAddr( UInt32 inAddr ){
		
		if ( !ChallengeResponse( CommandMode.JUMP_CALL ) )
			return false;

		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);

		return true;

	}


	//
	// Memcard Functions
	//

	/// <summary>
	/// Writes an entire memcard's contents
	/// </summary>
	/// <param name="inCard">0/1</param>	
	public static bool Command_MemcardUpload( UInt32 inCard, byte[] inFile ){

		if ( !TransferLogic.ChallengeResponse( CommandMode.MCUP ) ){
			return Program.Error( "No response from Unirom. Are you using 8.0.E or higher?" );
		}

		Console.WriteLine("Uploading card data...");

		// send the card number
		activeSerial.Write( BitConverter.GetBytes(inCard), 0, 4 );
		// file size in bytes, let unirom handle it
		activeSerial.Write( BitConverter.GetBytes( inFile.Length), 0, 4 );
		activeSerial.Write( BitConverter.GetBytes( CalculateChecksum(inFile) ), 0, 4);

		if (TransferLogic.WriteBytes(inFile, false))
		{
			Console.WriteLine("File uploaded, check your screen...");			
		}
		else
		{
			return Program.Error("Couldn't upload to unirom - no write attempt will be made", false);
		}

		return true;

	}

	/// <summary>
	/// Reads and dumps a memcard to disc
	/// </summary>
	/// <param name="inCard">0/1</param>	
	public static bool Command_MemcardDownload( UInt32 inCard, string fileName ){

		if ( !TransferLogic.ChallengeResponse(CommandMode.MCDOWN) ){
			return Program.Error( "No response from Unirom. Are you using 8.0.E or higher?" );
		}

		// send the card number
		activeSerial.Write(BitConverter.GetBytes(inCard), 0, 4);

		Console.WriteLine("Reading card to ram...");

		// it'll send this when it's done dumping to ram
		if (!TransferLogic.WaitResponse("MCRD", false))
		{
			return Program.Error("Please see screen or SIO for error!");
		}

		Console.WriteLine("Ready, reading....");

		UInt32 addr = TransferLogic.read32();
		Console.WriteLine("Data is 0x" + addr.ToString("x"));

		UInt32 size = TransferLogic.read32();
		Console.WriteLine("Size is 0x" + size.ToString("x"));


		Console.WriteLine("Dumping...");

		byte[] lastReadBytes = new byte[size];
		TransferLogic.ReadBytes(addr, size, lastReadBytes);

		
		if (System.IO.File.Exists(fileName))
		{
			string newFilename = fileName + Program.GetSpan().TotalSeconds.ToString();

			Console.Write("\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n");

			fileName = newFilename;
		}

		try
		{
			File.WriteAllBytes(fileName, lastReadBytes);
		}
		catch (Exception e)
		{
			return Program.Error("Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false);			
		}

		Console.WriteLine("File written to: " + fileName);
		Console.WriteLine("It is raw .mcd format used by PCSX-redux, no$psx, etc");
		return true;

	}

	//
	// Dump
	//

	/// <summary>
	/// Dump's a RAM/ROM region to disc, auto-named
	/// </summary>	
	public static bool Command_Dump( UInt32 inAddr, UInt32 inSize ){

		byte[] lastReadBytes = new byte[inSize];

		if ( !ReadBytes( inAddr, inSize, lastReadBytes) ){
			return Program.Error( "Couldn't ready bytes from Unirom!" );
		}

		string fileName = "DUMP_" + inAddr.ToString("X8") + "_to_" + inSize.ToString("X8") + ".bin";

		if (System.IO.File.Exists(fileName))
		{

			string newFilename = fileName + GetSpan().TotalSeconds.ToString();

			Console.Write("\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n");

			fileName = newFilename;

		}

		try
		{

			File.WriteAllBytes(fileName, lastReadBytes);

		}
		catch (Exception e)
		{

			Error("Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false);
			return false;

		}

	}

	//
	// Debug
	//

	/// <summary>
	/// Halt the system by entering a SIO-wait loop in an interrupt/critical section
	/// </summary>	
	public static bool Command_Halt(){
		
		return ChallengeResponse( CommandMode.HALT );

	}

	/// <summary>
	/// UnHalt
	/// </summary>	
	public static bool Command_CONT(){
		
		return ChallengeResponse( CommandMode.CONT );

	}
	
	/// <summary>
	/// Dumps the stored registers which are saved
	/// as an interrupt triggers. $K0 is lost.
	/// </summary>	
	public static bool Command_DumpRegs(){
		
		if ( GDB.GetRegs() ){
			GDB.DumpRegs();
			return true;
		} else {
			Console.WriteLine( "Couldn't get regs" );
			return false;
		}
		
	}

	/// <summary>
	/// Sets a register value
	/// Note: this will be applied as you /cont
	/// </summary>	
	public static bool Command_SetReg( string inReg, UInt32 inValue ){
		
		// Find the index of the string value and call that specific method
		for ( int i = 0; i < (int)GPR.COUNT; i++ ){
			if ( inReg.ToLowerInvariant() == ((GPR)i).ToString().ToLowerInvariant() ){				
				return Command_SetReg( (GPR)i, inValue );
			}
		}

		Console.WriteLine( "Unknown register: " + inReg );
		return false;

	}

	/// <summary>
	///  As above but typed
	/// </summary>	
	public static bool Command_SetReg( GPR inReg, UInt32 inValue ){
		
		Console.WriteLine( "---- Getting a copy of current registers ----" );

		if ( !GDB.GetRegs() ){
			Console.WriteLine( "Couldn't get regs" );
			return false;
		}

		GDB.tcb.regs[ (int)inReg ] = inValue;

		Console.WriteLine( "---- Done, writing regs back ----" );
		
		return GDB.SetRegs();
		
	}

	// Ping? Pong!
	public static void WriteChallenge(string inChallenge){

		activeSerial.Write(inChallenge);

	}

	private static bool didShowUpgradewarning = false;

	/// <summary>
	/// Wait for a response to see if this version of
	/// Unirom supports the V2 protocol
	/// </summary>	
	public static bool WaitResponse(string inResponse, bool verbose = true) {

		Program.protocolVersion = 1;

		// Dump the response into a buffer..
		// (byte by byte so we can compare the challenge/response)
		// e.g. it may start spewing data immediately after and we
		// have to catch that.
		// note: the attribute extensions use 40ish bytes of memory per pop

		string responseBuffer = "";

		if (verbose)
			Console.WriteLine("Waiting for response or protocol negotiation: ");
		
		while (true)
		{
			
			if (activeSerial.BytesToRead != 0)
			{
								
				responseBuffer += (char)activeSerial.ReadByte();

				// filter any noise at the start of the response
				// seems to happen once in a while
				if (responseBuffer.Length > 4)
					responseBuffer = responseBuffer.Remove(0, 1);

				if (verbose)
					Console.Write("\r InputBuffer: " + responseBuffer);

				// command unsupported in debug mode
				if (responseBuffer == "UNSP")
				{
					Console.WriteLine( "\nNot supported while Unirom is in debug mode!" );
					return false;
				}

				if ( responseBuffer == "HECK" ){
					Console.WriteLine("\nCouldn't read the memory card!");
					return false;
				}

				if (responseBuffer == "ONLY")
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine( "\nOnly supported while Unirom is in debug mode!" );
					return false;
				}

				if (
					!didShowUpgradewarning
					&& responseBuffer.Length >= 4
					&& responseBuffer.Substring(0, 3) == "OKV"
					&& (byte)responseBuffer[3] > (byte)'2'
				)
				{
					didShowUpgradewarning = true;
					Console.WriteLine();
					Console.Write("================================================================================\n");
					Console.Write("   Just a heads up!\n");
					Console.Write("   This version of Unirom appears to be much newer than your version of NoPS.\n");
					Console.Write("   Time for an upgrade? github.com/JonathanDotCel/ \n");
					Console.Write("================================================================================\n");
				}

				// upgrade to V2 with individual checksum
				if (responseBuffer == "OKV2" && Program.protocolVersion == 1)
				{
					Console.WriteLine("\nUpgraded to protocol V2!");
					activeSerial.Write("UPV2");
					Program.protocolVersion = 2;
				}

				// now whether we've upgraded protocol or not:
				if (responseBuffer == inResponse)
				{
					if (verbose)
						Console.WriteLine("\nGot response: " + responseBuffer );
					break;
				}

			} // while(1)




		}

		return true;

	}


	/// <summary>
	/// Deceptively small function, but one of the most important
	/// This is the one that sends e.g. "/poke" and  checks that Unirom is paying attention
	/// </summary>	
	public static bool ChallengeResponse( CommandMode inMode ){
		return ChallengeResponse( inMode.challenge(), inMode.response() );
	}

	public static bool ChallengeResponse( string inChallenge, string expectedResponse ){
		
		// Now send the challenge code and wait
		Console.WriteLine("Waiting for the PS1, C/R={0}/{1}....\n\n", inChallenge, expectedResponse );

		WriteChallenge(inChallenge);

		Thread.Sleep(50);

		return WaitResponse(expectedResponse);

	}



	// HEY!
	// Remember to tell the PSX to expect bytes first... BIN, ROM, EXE, etc
	// as this will attempt to use the V2 protocol rather than just spamming 
	// bytes into the void
	public static bool WriteBytes( byte[] inBytes, bool skipFirstSector ){


		// .exe files go [ header ][ meta ][ data @ write address ]
		// .rom files go [ meta ][ data @ 0x80100000 ]
		// .bin files go [ size ][ data @ 0xWRITEADR ]

		int start = skipFirstSector ? 2048 : 0;       // for .exes

		int chunkSize = 2048;                               // 2048 seems the most stable
		int numChunks = inBytes.Length / chunkSize + (inBytes.Length % chunkSize == 0 ? 0 : 1);

		int waityCakes = 0;                                 // Kinda extraneous, but it's interesting to watch


		// we already sent the first one?
		for (int i = start; i < inBytes.Length; i += chunkSize)
		{

		retryThisChunk:

			ulong chunkChecksum = 0;

			// Are we about to go out of range?
			// .NET doesn't care if you specify 2kb when you're only e.g. 1.7kb from the boundary
			// but it's best to declare explicityly			
			if ( i + chunkSize >= inBytes.Length )
				chunkSize = inBytes.Length - i;

			// write 1 chunk worth of bytes
			activeSerial.Write(inBytes, i, chunkSize);
			//Console.WriteLine( " " + i + " of " + inBytes.Length + " " + skipFirstSector );

			// update the expected checksum value
			for (int j = 0; j < chunkSize; j++)
			{
				chunkChecksum += inBytes[i + j];
			}

			while (activeSerial.BytesToWrite != 0)
			{
				waityCakes++;
			}

			Console.ForegroundColor = ConsoleColor.Green;
			int percent = (i + 1) * 100 / (inBytes.Length);
			Console.Write("\r Sending chunk {0} of {1} ({2})%", (i / chunkSize) + 1, numChunks, percent);

			Console.ForegroundColor = ConsoleColor.White;


			if ( Program.protocolVersion == 2)
			{

				// Format change as of 8.0.C
				// every 2k, we'll send back a "MORE" from Unirom

				Console.Write(" ... ");

				string more = "";

				TimeSpan startSpan = Program.GetSpan();
				while (more != "CHEK")
				{

					if (activeSerial.BytesToRead != 0)
					{
						
						more += (char)activeSerial.ReadByte();

					}
					while (more.Length > 4)
						more.Remove(0, 1);

				}

				// did it ask for a checksum?
				if (more == "CHEK")
				{

					Console.Write("Sending checksum...");

					activeSerial.Write(BitConverter.GetBytes(chunkChecksum), 0, 4);
					Thread.Sleep(1);

					startSpan = Program.GetSpan();

					while (more != "MORE" && more != "ERR!")
					{

						// keep sending it till the psx gets it?
						//serialPort.Write( BitConverter.GetBytes( chunkChecksum ), 0, 4 );

						if (activeSerial.BytesToRead != 0)
						{
							char readVal = (char)activeSerial.ReadByte();
							more += readVal;
							Console.Write(readVal);
						}
						while (more.Length > 4)
						{
							more = more.Remove(0, 1);
						}

					}

					if (more == "ERR!")
					{
						Console.WriteLine("... Retrying\n");
						goto retryThisChunk;
					}

					if (more == "MORE")
					{
						//Console.Write( "... OK\n" );							
					}

				}

				// if it didn't ask for one, crack on.


			} // corrective transfer

			Console.Write(" OK\n");

		}

		// might have to terminate previous line
		Console.WriteLine("\nSend finished!\n");

		return true;

	} // WriteBytes


	// C people: remember the byte[] is a pointer....
	/// <summary>
	/// Reads an array of bytes from the serial connection
	/// </summary>		
	public static bool ReadBytes(UInt32 inAddr, UInt32 inSize, byte[] inBytes )
	{
		
		if ( !ChallengeResponse( CommandMode.DUMP ) ){
			return false;
		}

		// the handshake is done, let's tell it where to start
		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);
		activeSerial.Write(BitConverter.GetBytes(inSize), 0, 4);

		// now go!
		int arrayPos = 0;
		//lastReadBytes = new byte[inSize];

		// Let the loop time out if something gets a bit fucky.			
		TimeSpan lastSpan = Program.GetSpan();
		TimeSpan currentSpan = Program.GetSpan();

		UInt32 checkSum = 0;

		while (true)
		{
			
			currentSpan = Program.GetSpan();

			if (activeSerial.BytesToRead != 0)
			{

				lastSpan = Program.GetSpan();

				byte responseByte = (byte)activeSerial.ReadByte();
				inBytes[arrayPos] = (responseByte);

				arrayPos++;

				checkSum += (UInt32)responseByte;

				if (arrayPos % 2048 == 0)
				{
					activeSerial.Write("MORE");
				}

				if (arrayPos % 1024 == 0)
				{
					long percent = (arrayPos * 100) / inSize;
					Console.Write("\r Offset {0} of {1} ({2})%\n", arrayPos, inSize, percent);
				}

				if (arrayPos >= inBytes.Length)
				{					
					break;
				}

			}

			// if we've been without data for more than 2 seconds, something's really up				
			if ((currentSpan - lastSpan).TotalMilliseconds > 2000)
			{
				if (arrayPos == 0)
				{
					Program.Error("There was no data for a long time! 0 bytes were read!", false);
					return false;
				}
				else
				{
					Program.Error("There was no data for a long time! Will try to dump the " + arrayPos + " (" + arrayPos.ToString("X8") + ") bytes that were read!", false);
				}

				return false;
			}


		}

		Console.WriteLine("Read Complete!");

		// Read 4 more bytes for the checksum

		// Let the loop time out if something gets a bit fucky.			
		lastSpan = Program.GetSpan();
		int expectedChecksum = 0;

		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("Checksumming the checksums for checksummyness.\n");

		try
		{

			for (int i = 0; i < 4; i++)
			{

				while (activeSerial.BytesToRead == 0)
				{

					currentSpan = Program.GetSpan();

					if ((currentSpan - lastSpan).TotalMilliseconds > 2000)
					{
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine("Error reading checksum byte " + i + " of 4!");
						break;
					}

				}

				lastSpan = Program.GetSpan();

				byte inByte = (byte)activeSerial.ReadByte();

				// and shift it ino the expected checksum
				expectedChecksum |= (inByte << (i * 8));

			}

		}
		catch (System.TimeoutException)
		{

			Console.ForegroundColor = ConsoleColor.Red;
			Program.Error("No checksum sent, continuing anyway!\n ", false);

		}

		if (expectedChecksum != checkSum)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Program.Error("Checksum missmatch! Expected: " + expectedChecksum.ToString("X8") + "    Calced: %x\n" + checkSum.ToString("X8"), false);
			Program.Error(" WILL ATTEMPT TO CONTINUE\n", false);
			return false;
		}
		else
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine(" Checksums match: " + expectedChecksum.ToString("X8") + "\n");			
		}


		if (activeSerial.BytesToRead > 0)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Program.Error("Extra bytes still being sent from the PSX! - Will attempt to save file anyway!", false);
		}

		Console.ForegroundColor = ConsoleColor.White;

		return true;

	} // DUMP


	#pragma warning disable CS0162

	/// <summary>
	/// Semi-supported: 
	/// Constantly reads from the address specified and dumps it to screen
	/// </summary>	
	public static bool Watch( UInt32 inAddr, UInt32 inSize ){

		if ( !ChallengeResponse( CommandMode.WATCH ) ) 
			return false;

		int bytesRead = 0;
		int arrayPos = 0;
		byte[] lastReadBytes = new byte[inSize];

		// the handshake is done, let's tell it where to start
		arrayPos = 0;
		activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);
		activeSerial.Write(BitConverter.GetBytes(inSize), 0, 4);

		while (true)
		{

			// Keep reading bytes until we've got as many back as we've asked for

			if (activeSerial.BytesToRead != 0)
			{

				// still bothers me that it reads an int...
				byte responseByte = (byte)activeSerial.ReadByte();
				lastReadBytes[arrayPos] = (responseByte);

				bytesRead++;
				arrayPos++;

				// filled the buffer? Print it

				if (arrayPos >= lastReadBytes.Length)
				{

					Console.Clear();
					Console.Write("Watching address range 0x" + inAddr.ToString("X8") + " to 0x" + (inAddr + inSize).ToString("X8") + "\n");
					Console.Write("Bytes read " + bytesRead + "\n\n");

					for (int i = 0; i < lastReadBytes.Length; i++)
					{

						Console.Write(lastReadBytes[i].ToString("X2") + " ");

						// Such a janky way to do it, but is saves appending
						// tons and tons of strings together
						if (i % 16 == 15)
						{

							// print the actual char values

							for (int j = i - 15; j <= i; j++)
							{

								Console.Write(" " + (char)lastReadBytes[j]);

							}

							// then draw the character data								
							Console.Write("\n");

						}

					}

					if (activeSerial.BytesToRead != 0)
					{
						Console.Write("\nTerminator bytes: ");
						while (activeSerial.BytesToRead != 0)
						{
							int x = activeSerial.ReadByte();
							Console.Write(x.ToString("X2") + " ");
						}
						Console.Write("\n");
					}


					// slow it down a touch

					// give the PSX time to do stuff
					Thread.Sleep(200);

					// Just start over...					
					ChallengeResponse(CommandMode.WATCH.challenge(), CommandMode.WATCH.response());

					// start over
					arrayPos = 0;
					activeSerial.Write(BitConverter.GetBytes(inAddr), 0, 4);
					activeSerial.Write(BitConverter.GetBytes(inSize), 0, 4);

				}

			} // bytestoread

		} // while true

		return true;

	}
	#pragma warning restore CS0162


	/// <summary>
	/// Leaves the serial connection open
	/// Will attempt to detect /HALT notifications from Unirom
	/// and catch crash/exception events
	/// </summary>
	public static void DoMonitor(){

		// a rolling buffer of the last 4 things recieved
		string lastMonitorBytes = "";

		while (true)
		{

			if (activeSerial.BytesToRead > 0)
			{

				// echo things to the screen

				int responseByte = activeSerial.ReadByte();
				Console.Write((char)(responseByte));
				
				// check if the PSX has crashed

				lastMonitorBytes += (char)responseByte;
				while ( lastMonitorBytes.Length > 4 )
					lastMonitorBytes = lastMonitorBytes.Remove( 0, 1 );

				if ( lastMonitorBytes == "HLTD" ){
					
					while ( true ){
						
						Console.WriteLine( "\nThe PSX may have crashed, enter debug mode? (y/n)" );
						ConsoleKeyInfo c = Console.ReadKey();
						if ( c.KeyChar == 'y' || c.KeyChar == 'Y' ){
							GDB.Init();
							return;
						}
						if ( c.KeyChar == 'n' || c.KeyChar == 'N' ){
							Console.WriteLine( "\nReturned to monitor mode." );
							break;
						}

					}


				}

			}

		}


	}


	/// <summary>
	/// Returns a (weak) checksum for the given bytes
	/// </summary>	
	/// <param name="skipFirstSector">Skip the first 0x800 header sector on an .exe as it won't be sent over SIO</param>	
	public static UInt32 CalculateChecksum(byte[] inBytes, bool skipFirstSector = false)
	{

		UInt32 returnVal = 0;
		for (int i = (skipFirstSector ? 2048 : 0); i < inBytes.Length; i++)
		{
			returnVal += (UInt32)inBytes[i];
		}
		return returnVal;

	}

}
