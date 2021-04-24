// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

//
// GDB TCP->SIO bridge for Unirom 8
//
// NOTE!
//
// While all of the basic debug functionality is present, the GDB
// bridge is still very much a work in progress!
//

using System;
using System.Text;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

public enum GPR{
    
    stat,
    badv,       // repurposed for unirom

    // GPR
    r0, at, v0, v1, a0, a1, a2, a3,
    t0, t1, t2, t3, t4, t5, t6, t7,
    s0, s1, s2, s3, s4, s5, s6, s7,
    t8, t9, k0, k1, gp, sp, fp, ra,

    rapc,
    hi, lo,
    sr,
    caus,
    // GPR

    unknown0,    unknown1,
    unknown2,    unknown3,
    unknown4,    unknown5,
    unknown6,    unknown7,
    unknown9,

    COUNT // C# only, not present on the PSX struct

}

// The PSX's Thread Control Block (usually TCB[0])
public class TCB{
       
    public UInt32[] regs = new UInt32[ (int)GPR.COUNT ];

}


public enum PSXState{ normal, halted };

public class GDB{

    public static bool enabled = false;

    public static SerialPort serial => Program.activeSerial;
    public static TCB tcb = new TCB();
    public static PSXState psxState = PSXState.normal;

	public const int TCB_LENGTH_BYTES = (int)GPR.COUNT * 4;

	public static Socket socket;


    public static void DebugInit( UInt32 localPort ){

		Console.WriteLine( "Checking if Unirom is in debug mode..." );

		// if it returns true, we might enter /m (monitor) mode, etc
		if (
			!TransferLogic.ChallengeResponse( CommandMode.DEBUG )
		) {
			Console.WriteLine( "Couldn't determine if Unirom is in debug mode." );
			return;
		}

		Console.WriteLine( "Grabbing initial state..." );

		TransferLogic.Command_DumpRegs();

		Console.WriteLine( "Monitoring sio..." );

        Console.WriteLine( "********************** WARNING ***************************" );
        Console.WriteLine( "THE TCP BRIDGE DOES NOT CURRENTLY ACCEPT COMMANDS FROM GDB" );
        Console.WriteLine( "********************** ******* ***************************" );
        
        Init( localPort );

    }

    public static void Init( UInt32 localPort ){
		
		InitListenServer( localPort );
		MonitorSerialToSocket();

    }

	public const int socketBufferSize = 512;
	public static byte[] socketBuffer = new byte[socketBufferSize];
	public static StringBuilder sb = new StringBuilder();
	public static string socketString; // oh boy, this will be nuts on the GC

    public static Socket replySocket;

	public static void AcceptCallback( IAsyncResult result ){
		
		Socket whichSocket = (Socket)result.AsyncState;
		Socket newSocket = socket.EndAccept( result );
        
		//Console.WriteLine( "Remote connection to local socket accepted: " + whichSocket.LocalEndPoint );
		Console.WriteLine( "Remote connection to local socket accepted: " + newSocket.LocalEndPoint );

        replySocket = newSocket;

		// on the new socket or it'll moan. I don't like this setup.
		newSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( RecieveCallback ), newSocket );


	}

	private static void RecieveCallback( IAsyncResult ar ){
		
		//Console.WriteLine( "SOCKET: RCB " + ar.AsyncState );

		
		Socket recvSocket = (Socket)ar.AsyncState;
		
		//Console.WriteLine( "SOCKET RCB 1 " + recvSocket );

		int numBytesRead = recvSocket.EndReceive( ar );

		//Console.WriteLine( "SOCKET RCB 2 " + numBytesRead );

		if ( numBytesRead > 0 ){
			
			// copy the bytes (from the buffer specificed in BeginRecieve), into the stringbuilder
			string thisPacket = ASCIIEncoding.ASCII.GetString( socketBuffer, 0, numBytesRead );
			sb.Append( thisPacket );
			
			socketString = sb.ToString();
			//Console.WriteLine( "\rSOCKET: rec: " + thisPacket );

			if ( socketString.IndexOf("<EOF>") > -1 ){

				Console.WriteLine( "Read {0} bytes, done!: {1}", numBytesRead, socketString );

			} else {
				
				// echo it back
				//Send( recvSocket, thisPacket );
				// also echo it over SIO
				TransferLogic.activeSerial.Write( socketBuffer, 0, numBytesRead );

				//Console.WriteLine( "SOCKET: Grabbing more data" );

				recvSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( RecieveCallback ), recvSocket );

			}


		} else {
			Console.WriteLine( "Read 0 bytes..." );
		}

	}

	private static void Send( Socket inSocket, string inData ){
		
		byte[] bytes = ASCIIEncoding.ASCII.GetBytes( inData );

		inSocket.BeginSend( bytes, 0, bytes.Length, 0, new AsyncCallback( SendCallback ), inSocket );

	}

	private static void SendCallback( IAsyncResult ar ){
		
		Socket whichSocket = (Socket)ar.AsyncState;

		int bytesSent = whichSocket.EndSend( ar );
		Console.WriteLine( "Sent {0} bytes ", bytesSent );

		//socket.Shutdown( SocketShutdown.Both );
		//socket.Close();

	}

	private static void InitListenServer( UInt32 inPort ){
		
        
		IPAddress ip = IPAddress.Parse( "127.0.0.1" );

        Console.WriteLine( "Opening a listen server on " + ip + ":" + inPort );
		
		IPEndPoint localEndpoint = new IPEndPoint( ip, (int)inPort );

		socket = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );

		socket.Bind( localEndpoint );

		socket.Listen( 2 );

		socket.BeginAccept( new AsyncCallback( AcceptCallback ), socket );

	}

	public static bool GetRegs(){
		
		// read the pointer to TCB[0]
		byte[] ptrBuffer = new byte[4];
		if ( !TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ){
			return false;
		}

		UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );

		Console.WriteLine( "TCB PTR " + tcbPtr.ToString("X") );

		byte[] tcbBytes = new byte[TCB_LENGTH_BYTES];
		if ( !TransferLogic.ReadBytes( tcbPtr, (int)GPR.COUNT *4, tcbBytes ) ){
			return false;
		}
		
		Buffer.BlockCopy( tcbBytes, 0, tcb.regs, 0, tcbBytes.Length );
		
		return true;

	}

	public static bool SetRegs(){
		
		// read the pointer to TCB[0]
		byte[] ptrBuffer = new byte[4];
		if ( !TransferLogic.ReadBytes( 0x80000110, 4, ptrBuffer ) ){
			return false;
		}

		UInt32 tcbPtr = BitConverter.ToUInt32( ptrBuffer, 0 );
		Console.WriteLine( "TCB PTR " + tcbPtr.ToString("X") );

		// Convert regs back to a byte array and bang them back out
		byte[] tcbBytes = new byte[TCB_LENGTH_BYTES];
		Buffer.BlockCopy( tcb.regs, 0, tcbBytes, 0, TCB_LENGTH_BYTES );

		TransferLogic.Command_SendBin( tcbPtr, tcbBytes );
				
		return true;

	}

	public static void DumpRegs(){

		int tab = 0;

		for( int i = 0; i < (int)GPR.COUNT -9; i++ ){			
			Console.Write( "\t {0} =0x{1}", ((GPR)i).ToString().PadLeft(4), tcb.regs[ i ].ToString("X8") );
			// this format won't change, so there's no issue hardcoding them
			if ( tab++%4 == 3 || i == 1 || i == 33 || i == 34 ){
				Console.WriteLine();
				tab = 0;
			}
		}
		Console.WriteLine();

		UInt32 cause = ( tcb.regs[ (int)GPR.caus ] >> 2 ) & 0xFF;
		
		switch ( cause ) {
			case 0x04:
				Console.WriteLine("AdEL - Data Load or instr fetch (0x{0})\n", cause);
				break;
			case 0x05:
				Console.WriteLine("AdES - Data Store (unaligned?) (0x{0})\n", cause);
				break;
			case 0x06:
				Console.WriteLine("IBE - Bus Error on instr fetch (0x{0})\n", cause);
				break;
			case 0x07:
				Console.WriteLine("DBE - Bus Error on data load/store (0x{0})\n", cause);
				break;
			case 0x08:
				Console.WriteLine("SYS - Unconditional Syscall (0x{0})\n", cause);
				break;
			case 0x09:
				Console.WriteLine("BP - Break! (0x{0})\n", cause);
				break;
			case 0x0A:
				Console.WriteLine("RI - Reserved Instruction (0x{0})\n", cause);
				break;
			case 0x0B:
				Console.WriteLine("CpU - Coprocessor unavailable (0x{0})\n", cause);
				break;
			case 0x0C:
				Console.WriteLine("Ov - Arithmetic overflow (0x{0})\n", cause);
				break;

			default:
				Console.WriteLine("Code {0}!\n", cause);
				break;
		}
		

	}

    // Sockets are sent over serial as and when data arrives
    // This function polls the sio and sends it back because
    // mono hasn't implemented sio callbacks.
    public static void MonitorSerialToSocket(){
        
        // 10kb buffer?
        byte[] responseBytes = new byte[2048 * 10];
        int bytesInBuffer = 0;

        while( true ){

            while ( serial.BytesToRead != 0 && bytesInBuffer < responseBytes.Length ){
            
                byte val = (byte)serial.ReadByte();
                
                responseBytes[bytesInBuffer++] = (byte)val;
                
                // TODO: keep tabs on this for GDB mode but not Bridge mode
                /*
                // PSX telling us it was halted.
                if ( responseAsString == "HLTD" ){
					responseAsString = "";
                    Console.WriteLine( "PSX was halted" );
                }
                */
            }

            if ( bytesInBuffer > 0 ){
                // send it baaahk
                if ( replySocket != null ){
                    replySocket.Send( responseBytes, 0, bytesInBuffer, SocketFlags.None );
                    bytesInBuffer = 0;
                }
            }

        } // while
        

    }


}
