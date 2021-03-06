using Solace.DotNet.Rtsp.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Net.Sockets;

namespace Solace.DotNet.Rtsp {
	public class RTSPClient {
		static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger ();          

		// Events that applications can receive
		public event FrameReceivedDelegate FrameReceived;
		public event ParameterSetsReceivedDelegate ParameterSetsReceived;

		// Delegated functions (essentially the function prototype)
		public delegate void FrameReceivedDelegate (byte [] nalUnits);
		public delegate void ParameterSetsReceivedDelegate (byte [] sps, byte [] pps);

		volatile bool is_connected = false;
		RtspTcpTransport rtsp_socket = null; // RTSP connection
		RtspListener rtsp_client = null;   // this wraps around a the RTSP tcp_socket stream
		string auth_type = null;         // cached from most recent WWW-Authenticate reply
		string realm = null;             // cached from most recent WWW-Authenticate reply
		string nonce = null;             // cached from most recent WWW-Authenticate reply
		int video_payload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value)
		int video_data_channel = -1;     // RTP Channel Number used for the video RTP stream or the UDP port number
		int video_rtcp_channel = -1;     // RTP Channel Number used for the video RTCP status report messages OR the UDP port number
		string video_codec = "";         // Codec used with Payload Types 96..127 (eg "H264")
		readonly uint ssrc = (uint)new Random ().Next (1, int.MaxValue);

		System.Timers.Timer keepalive_timer = null;
		H264Payload h264Payload = new H264Payload ();

		public string Url { get; private set; }
		public string Username { get; private set; }
		public string Password { get; private set; }
		public string Hostname { get; private set; }
		public int Port { get; private set; }
		public int Timeout { get; set; } = 1000;


		public string RtspSession { get; private set; } // RTSP Session
		public HashSet<string> SupportedMethods { get; private set; } = new HashSet<string> ();

		static RTSPClient ()
		{
			RtspUtils.RegisterUri ();
		}

		public void Connect (string url)
		{
			string username = null;
			string password = null;
			var uri = new Uri (url);

			// Use URI to extract username and password
			if (uri.UserInfo.Length > 0) {
				username = uri.UserInfo.Split (new char [] { ':' }) [0];
				password = uri.UserInfo.Split (new char [] { ':' }) [1];
			}

			Connect (url, username, password);
		}

		public void Connect (string url, string username, string password)
		{
			var uri = new Uri (url);

			// Use URI to make a new URL without the username and password
			if (uri.UserInfo.Length > 0) {
				Url = uri.GetComponents (UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
			} else {
				Url = url;
			}

			Username = username;
			Password = password;
			Hostname = uri.Host;
			Port = uri.Port;

			// Connect to a RTSP Server. The RTSP session is a TCP connection
			try {
				var connection = new TcpClient ();
				var result = connection.BeginConnect (Hostname, Port, null, null);
				var success = result.AsyncWaitHandle.WaitOne (TimeSpan.FromMilliseconds (Timeout));

				if (!success) {
					throw new Exception ("Failed to connect (timeout).");
				}

				connection.EndConnect (result);

				rtsp_socket = new RtspTcpTransport (connection);
			} catch {
				is_connected = false;
				logger.Warn ($"Error - did not connect (Hostname: {Hostname} Port: {Port})");
				return;
			}

			if (!rtsp_socket.Connected) {
				is_connected = false;
				logger.Warn ($"Error - did not connect (Hostname: {Hostname} Port: {Port})");
				return;
			}

			is_connected = true;

			// Connect a RTSP Listener to the RTSP Socket (or other Stream) to send RTSP messages and listen for RTSP replies
			rtsp_client = new RtspListener (rtsp_socket) {
				AutoReconnect = false
			};

			rtsp_client.MessageReceived += Rtsp_MessageReceived;
			rtsp_client.DataReceived += Rtp_DataReceived;
			rtsp_client.Start (); // start listening for messages from the server (messages fire the MessageReceived event)

			// Send OPTIONS
			// In the Received Message handler we will send DESCRIBE, SETUP and PLAY
			RtspRequest options_message = new Solace.DotNet.Rtsp.Messages.RtspRequestOptions ();

			options_message.RtspUri = new Uri (Url);
			rtsp_client.SendMessage (options_message);
		}

		// return true if this connection failed, or if it connected but is no longer connected.
		public bool IsStreamingFinished ()
		{
			if (!is_connected)
				return true;

			if (!rtsp_socket.Connected)
				return true;
			
			return false;
		}

		public void Pause ()
		{
			if (rtsp_client == null) {
				return;
			}

			// Send PAUSE
			var pause_message = new RtspRequestPause {
				RtspUri = new Uri (Url),
				Session = RtspSession
			};

			if (auth_type != null) {
				AddAuthorization (pause_message, Username, Password, auth_type, realm, nonce, Url);
			}

			rtsp_client.SendMessage (pause_message);
		}

		public void Play ()
		{
			if (rtsp_client == null) {
				return;
			}

			// Send PLAY
			var play_message = new RtspRequestPlay {
				RtspUri = new Uri (Url),
				Session = RtspSession
			};

			if (auth_type != null) {
				AddAuthorization (play_message, Username, Password, auth_type, realm, nonce, Url);
			}

			rtsp_client.SendMessage (play_message);
		}

		public void Stop ()
		{
			// Stop the keepalive timer
			if (keepalive_timer != null) {
				keepalive_timer.Stop ();
				keepalive_timer = null;
			}

			if (rtsp_client == null) {
				return;
			}

			// Send TEARDOWN
			var teardown_message = new RtspRequestTeardown {
				RtspUri = new Uri (Url),
				Session = RtspSession
			};

			if (auth_type != null) {
				AddAuthorization (teardown_message, Username, Password, auth_type, realm, nonce, Url);
			}

			rtsp_client.SendMessage (teardown_message);

			// Drop the RTSP session
			rtsp_client.Stop ();
		}

		// int rtp_count = 0; // used for statistics
		// RTP packet (or RTCP packet) has been received.
		void Rtp_DataReceived (object sender, RtspChunkEventArgs e)
		{
			var data_received = e.Message as RtspData;

			// Check which channel the Data was received on.
			// eg the Video Channel or the Video Control Channel (RTCP)

			if (data_received.Channel == video_rtcp_channel) {
				//logger.Debug ("Received a RTCP message on channel " + data_received.Channel);

				// RTCP Packet
				// - Version, Padding and Receiver Report Count
				// - Packet Type
				// - Length
				// - SSRC
				// - payload

				// There can be multiple RTCP packets transmitted together. Loop ever each one

				long packetIndex = 0;
				while (packetIndex < e.Message.Data.Length) {
					//int rtcp_version = (e.Message.Data [packetIndex + 0] >> 6);
					//int rtcp_padding = (e.Message.Data [packetIndex + 0] >> 5) & 0x01;
					//int rtcp_reception_report_count = (e.Message.Data [packetIndex + 0] & 0x1F);
					byte rtcp_packet_type = e.Message.Data [packetIndex + 1]; // Values from 200 to 207
					uint rtcp_length = ((uint)e.Message.Data [packetIndex + 2] << 8) + (uint)(e.Message.Data [packetIndex + 3]); // number of 32 bit words
					//uint rtcp_ssrc = ((uint)e.Message.Data [packetIndex + 4] << 24) + (uint)(e.Message.Data [packetIndex + 5] << 16)
							//+ (uint)(e.Message.Data [packetIndex + 6] << 8) + (uint)(e.Message.Data [packetIndex + 7]);

					// 200 = SR = Sender Report
					// 201 = RR = Receiver Report
					// 202 = SDES = Source Description
					// 203 = Bye = Goodbye
					// 204 = APP = Application Specific Method
					// 207 = XR = Extended Reports

					//logger.Debug ("RTCP Data. PacketType=" + rtcp_packet_type + " SSRC=" + rtcp_ssrc);

					if (rtcp_packet_type == 200) {
						// Send a Receiver Report
						try {
							SendEmptyReceiverReport ();
						} catch {
							logger.Warn ("Error writing RTCP packet");
						}
					}

					packetIndex = packetIndex + ((rtcp_length + 1) * 4);
				}

				return;
			}

			if (data_received.Channel == video_data_channel) {
				// Received some Video Data on the correct channel.

				// RTP Packet Header
				// 0 - Version, P, X, CC, M, PT and Sequence Number
				//32 - Timestamp
				//64 - SSRC
				//96 - CSRCs (optional)
				//nn - Extension ID and Length
				//nn - Extension header

				//int rtp_version = (e.Message.Data [0] >> 6);
				//int rtp_padding = (e.Message.Data [0] >> 5) & 0x01;
				int rtp_extension = (e.Message.Data [0] >> 4) & 0x01;
				int rtp_csrc_count = (e.Message.Data [0] >> 0) & 0x0F;
				int rtp_marker = (e.Message.Data [1] >> 7) & 0x01;
				int rtp_payload_type = (e.Message.Data [1] >> 0) & 0x7F;
				//uint rtp_sequence_number = ((uint)e.Message.Data [2] << 8) + (uint)(e.Message.Data [3]);
				//uint rtp_timestamp = ((uint)e.Message.Data [4] << 24) + (uint)(e.Message.Data [5] << 16) + (uint)(e.Message.Data [6] << 8) + (uint)(e.Message.Data [7]);
				//uint rtp_ssrc = ((uint)e.Message.Data [8] << 24) + (uint)(e.Message.Data [9] << 16) + (uint)(e.Message.Data [10] << 8) + (uint)(e.Message.Data [11]);

				int rtp_payload_start = 4 // V,P,M,SEQ
						      + 4 // time stamp
						      + 4 // ssrc
						      + (4 * rtp_csrc_count); // zero or more csrcs

				uint rtp_extension_id = 0;
				uint rtp_extension_size = 0;

				if (rtp_extension == 1) {
					rtp_extension_id = ((uint)e.Message.Data [rtp_payload_start + 0] << 8) + (uint)(e.Message.Data [rtp_payload_start + 1] << 0);
					rtp_extension_size = ((uint)e.Message.Data [rtp_payload_start + 2] << 8) + (uint)(e.Message.Data [rtp_payload_start + 3] << 0) * 4; // units of extension_size is 4-bytes
					rtp_payload_start += 4 + (int)rtp_extension_size;  // extension header and extension payload
				}

				//logger.Debug ("RTP Data"
					//+ " V=" + rtp_version
					//+ " P=" + rtp_padding
					//+ " X=" + rtp_extension
					//+ " CC=" + rtp_csrc_count
					//+ " M=" + rtp_marker
					//+ " PT=" + rtp_payload_type
					//+ " Seq=" + rtp_sequence_number
					//+ " Time (MS)=" + rtp_timestamp / 90 // convert from 90kHZ clock to ms
					//+ " SSRC=" + rtp_ssrc
					//+ " Size=" + e.Message.Data.Length);

				// Check the payload type in the RTP packet matches the Payload Type value from the SDP
				if (rtp_payload_type != video_payload) {
					logger.Warn ("Ignoring this Video RTP payload");
					return; // ignore this data
				}

				if (rtp_payload_type >= 96 && rtp_payload_type <= 127 && video_codec.Equals ("H264")) {
					// H264 RTP Packet

					// If rtp_marker is '1' then this is the final transmission for this packet.
					// If rtp_marker is '0' we need to accumulate data with the same timestamp

					// ToDo - Check Timestamp
					// Add the RTP packet to the tempoary_rtp list until we have a complete 'Frame'

					//byte [] rtp_payload = new byte [e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
					//Array.Copy (e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

					//List<byte []> nal_units = h264Payload.Process_H264_RTP_Packet (rtp_payload, rtp_marker); // this will cache the Packets until there is a Frame

					// we have not passed in enough RTP packets to make a Frame of video
					// we have a frame of NAL Units. Write them to the file
					//if (nal_units != null) {
					//	FrameReceived?.Invoke (nal_units);
					FrameReceived?.Invoke (e.Message.Data);
				//}
				} else {
					logger.Warn ("No parser for RTP payload " + rtp_payload_type);
				}
			}
		}

		// RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY etc
		void Rtsp_MessageReceived (object sender, RtspChunkEventArgs e)
		{
			var message = e.Message as RtspResponse;

			//logger.Debug ("Received " + message.OriginalRequest.Method);

			// If message has a 401 - Unauthorised Error, then we re-send the message with Authorization
			// using the most recently received 'realm' and 'nonce'
			if (!message.IsOk) {
				logger.Warn ("Got Error in RTSP Reply " + message.ReturnCode + " " + message.ReturnMessage);

				if (message.ReturnCode == 401 && (message.OriginalRequest.Headers.ContainsKey (RtspHeaderNames.Authorization) == true)) {
					// the authorization failed.
					Stop ();
					return;
				}

				// Check if the Reply has an Authenticate header.
				if (message.ReturnCode == 401 && message.Headers.ContainsKey (RtspHeaderNames.WWWAuthenticate)) {

					// Process the WWW-Authenticate header
					// EG:   Basic realm="AProxy"
					// EG:   Digest realm="AXIS_WS_ACCC8E3A0A8F", nonce="000057c3Y810622bff50b36005eb5efeae118626a161bf", stale=FALSE

					string www_authenticate = message.Headers [RtspHeaderNames.WWWAuthenticate];
					string [] items = www_authenticate.Split (new char [] { ',', ' ' });

					foreach (string item in items) {
						if (item.ToLower ().Equals ("basic")) {
							auth_type = "Basic";
						} else if (item.ToLower ().Equals ("digest")) {
							auth_type = "Digest";
						} else {
							// Split on the = symbol and update the realm and nonce
							string [] parts = item.Split (new char [] { '=' }, 2); // max 2 parts in the results array
							if (parts.Count () >= 2 && parts [0].Trim ().Equals ("realm")) {
								realm = parts [1].Trim (new char [] { ' ', '\"' }); // trim space and quotes
							} else if (parts.Count () >= 2 && parts [0].Trim ().Equals ("nonce")) {
								nonce = parts [1].Trim (new char [] { ' ', '\"' }); // trim space and quotes
							}
						}
					}

					//logger.Debug ("WWW Authorize parsed for " + auth_type + " " + realm + " " + nonce);
				}

				var resend_message = message.OriginalRequest.Clone () as RtspMessage;

				if (auth_type != null) {
					AddAuthorization (resend_message, Username, Password, auth_type, realm, nonce, Url);
				}

				rtsp_client.SendMessage (resend_message);

				return;

			}

			// If we get a reply to OPTIONS then start the Keepalive Timer and send DESCRIBE
			if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestOptions) {
				string public_methods = message.Headers [RtspHeaderNames.Public];

				SupportedMethods.Clear ();

				foreach (string method in public_methods.Split (',')) {
					SupportedMethods.Add (method.Trim ());
				}

				if (keepalive_timer == null) {
					// Start a Timer to send an OPTIONS command (for keepalive) every 20 seconds
					keepalive_timer = new System.Timers.Timer ();
					keepalive_timer.Elapsed += Timer_Elapsed;
					keepalive_timer.Interval = 5 * 1000;
					keepalive_timer.Enabled = true;

					// Send DESCRIBE
					var describe_message = new RtspRequestDescribe ();
					describe_message.RtspUri = new Uri (Url);
					describe_message.AddAccept ("application/sdp");

					if (auth_type != null) {
						AddAuthorization (describe_message, Username, Password, auth_type, realm, nonce, Url);
					}

					rtsp_client.SendMessage (describe_message);
				}
			}

			// If we get a reply to DESCRIBE (which was our second command), then prosess SDP and send the SETUP
			if (message.OriginalRequest != null && message.OriginalRequest is Solace.DotNet.Rtsp.Messages.RtspRequestDescribe) {
				// Got a reply for DESCRIBE
				if (!message.IsOk) {
					logger.Warn ("Got Error in DESCRIBE Reply " + message.ReturnCode + " " + message.ReturnMessage);
					return;
				}

				// Examine the SDP
				//logger.Debug (Encoding.UTF8.GetString (message.Data));

				Sdp.SdpFile sdp_data;
				using (var sdp_stream = new StreamReader (new MemoryStream (message.Data))) {
					sdp_data = Sdp.SdpFile.Read (sdp_stream);
				}
				File.WriteAllText ("video.sdp", System.Text.Encoding.Default.GetString (message.Data));

				// Process each 'Media' Attribute in the SDP (each sub-stream)
				for (int x = 0; x < sdp_data.Medias.Count; x++) {
					bool video = (sdp_data.Medias [x].MediaType == Solace.DotNet.Rtsp.Sdp.Media.MediaTypes.video);

					if (video && video_payload != -1) continue; // have already matched an video payload

					if (video) {
						// search the attributes for control, rtpmap and fmtp
						// (fmtp only applies to video)
						string control = "";  // the "track" or "stream id"
						Sdp.FmtpAttribute fmtp = null; // holds SPS and PPS in base64 (h264 video)

						foreach (var attrib in sdp_data.Medias [x].Attributs) {
							if (attrib.Key.Equals ("control")) {
								string sdp_control = attrib.Value;
								if (sdp_control.ToLower ().StartsWith ("rtsp://", StringComparison.Ordinal)) {
									control = sdp_control; //absolute path
								} else if (message.Headers.ContainsKey (RtspHeaderNames.ContentBase)) {
									control = message.Headers [RtspHeaderNames.ContentBase] + sdp_control; // relative path
								} else {
									control = Url + "/" + sdp_control; // relative path
								}
							}
							if (attrib.Key.Equals ("fmtp")) {
								fmtp = attrib as Sdp.FmtpAttribute;
							}
							if (attrib.Key.Equals ("rtpmap")) {
								var rtpmap = attrib as Sdp.RtpMapAttribute;

								// Check if the Codec Used (EncodingName) is one we support
								string [] valid_video_codecs = { "H264" };

								if (video && Array.IndexOf (valid_video_codecs, rtpmap.EncodingName) >= 0) {
									// found a valid codec
									video_codec = rtpmap.EncodingName;
									video_payload = sdp_data.Medias [x].PayloadType;
								}
							}
						}

						// If the rtpmap contains H264 then split the fmtp to get the sprop-parameter-sets which hold the SPS and PPS in base64
						if (video && video_codec.Contains ("H264") && fmtp != null) {
							var param = Sdp.H264Parameters.Parse (fmtp.FormatParameter);
							var sps_pps = param.SpropParameterSets;
							if (sps_pps.Count () >= 2) {
								byte [] sps = sps_pps [0];
								byte [] pps = sps_pps [1];
								ParameterSetsReceived?.Invoke (sps, pps);
							}
						}

						// Send the SETUP RTSP command if we have a matching Payload Decoder
						if (video && video_payload == -1) continue;

						// Server interleaves the RTP packets over the RTSP connection
						// TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
						video_data_channel = 0;
						video_rtcp_channel = 1;

						var transport = new RtspTransport () {
							LowerTransport = RtspTransport.LowerTransportType.TCP,
							Interleaved = new PortCouple (video_data_channel, video_rtcp_channel), // Eg Channel 0 for video. Channel 1 for RTCP status reports
						};

						// Send SETUP
						var setup_message = new RtspRequestSetup ();
						setup_message.RtspUri = new Uri (control);
						setup_message.AddTransport (transport);

						if (auth_type != null) {
							AddAuthorization (setup_message, Username, Password, auth_type, realm, nonce, Url);
						}

						rtsp_client.SendMessage (setup_message);
					}
				}
			}

			// If we get a reply to SETUP (which was our third command), then process and then send PLAY
			if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestSetup) {
				// Got Reply to SETUP
				if (!message.IsOk) {
					logger.Warn ("Got Error in SETUP Reply " + message.ReturnCode + " " + message.ReturnMessage);
					return;
				}

				//logger.Debug ("Got reply from SETUP Session=" + message.Session);

				RtspSession = message.Session; // Session value used with Play, Pause, Teardown

				// Send PLAY
				RtspRequest play_message = new RtspRequestPlay ();
				play_message.RtspUri = new Uri (Url);
				play_message.Session = RtspSession;

				if (auth_type != null) {
					AddAuthorization (play_message, Username, Password, auth_type, realm, nonce, Url);
				}

				rtsp_client.SendMessage (play_message);
			}

			// If we get a reply to PLAY (which was our fourth command), then we should have video being received
			if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestPlay) {
				// Got Reply to PLAY
				if (!message.IsOk) {
					logger.Warn ("Got Error in PLAY Reply " + message.ReturnCode + " " + message.ReturnMessage);
					return;
				}

				//logger.Debug ("Got reply from PLAY " + message.Command);
			}
		}

		// Send Keepalive message
		void Timer_Elapsed (object sender, System.Timers.ElapsedEventArgs e)
		{
			RtspRequest message;

			if (SupportedMethods.Contains ("GET_PARAMETER")) {
				message = new RtspRequestGetParameter ();
			} else {
				message = new RtspRequestOptions ();
			}

			message.RtspUri = new Uri (Url);
			message.Session = RtspSession;

			if (auth_type != null) {
				AddAuthorization (message, Username, Password, auth_type, realm, nonce, Url);
			}

			rtsp_client.SendMessage (message);
		}

		void SendEmptyReceiverReport ()
		{
			int version = 2;
			int paddingBit = 0;
			int reportCount = 0; // an empty report
			int packetType = 201; // Receiver Report
			byte [] rtcp_receiver_report = new byte [8];
			int length = (rtcp_receiver_report.Length / 4) - 1; // num 32 bit words minus 1

			rtcp_receiver_report [0] = (byte)((version << 6) + (paddingBit << 5) + reportCount);
			rtcp_receiver_report [1] = (byte)(packetType);
			rtcp_receiver_report [2] = (byte)((length >> 8) & 0xFF);
			rtcp_receiver_report [3] = (byte)((length >> 0) & 0XFF);
			rtcp_receiver_report [4] = (byte)((ssrc >> 24) & 0xFF);
			rtcp_receiver_report [5] = (byte)((ssrc >> 16) & 0xFF);
			rtcp_receiver_report [6] = (byte)((ssrc >> 8) & 0xFF);
			rtcp_receiver_report [7] = (byte)((ssrc >> 0) & 0xFF);

			rtsp_client.SendData (video_rtcp_channel, rtcp_receiver_report);
		}

		// Generate Basic or Digest Authorization
		public void AddAuthorization (RtspMessage message, string username, string password, string auth_type, string realm, string nonce, string url)
		{
			if (string.IsNullOrEmpty (username)) return;
			if (string.IsNullOrEmpty (password)) return;
			if (string.IsNullOrEmpty (realm)) return;
			if (auth_type.Equals ("Digest") && (string.IsNullOrEmpty (nonce))) return;

			if (auth_type.Equals ("Basic")) {
				byte [] credentials = System.Text.Encoding.UTF8.GetBytes (username + ":" + password);
				string credentials_base64 = Convert.ToBase64String (credentials);
				string basic_authorization = "Basic " + credentials_base64;

				message.Headers.Add (RtspHeaderNames.Authorization, basic_authorization);

				return;
			}

			if (auth_type.Equals ("Digest")) {
				var md5 = MD5.Create ();
				string method = message.Method; // DESCRIBE, SETUP, PLAY etc
				string hashA1 = CalculateMD5Hash (md5, username + ":" + realm + ":" + password);
				string hashA2 = CalculateMD5Hash (md5, method + ":" + url);
				string response = CalculateMD5Hash (md5, hashA1 + ":" + nonce + ":" + hashA2);

				const string quote = "\"";
				string digest_authorization = "Digest username=" + quote + username + quote + ", "
				    + "realm=" + quote + realm + quote + ", "
				    + "nonce=" + quote + nonce + quote + ", "
				    + "uri=" + quote + url + quote + ", "
				    + "response=" + quote + response + quote;

				message.Headers.Add (RtspHeaderNames.Authorization, digest_authorization);
			}
		}

		// MD5 (lower case)
		public string CalculateMD5Hash (MD5 md5_session, string input)
		{
			var output = new StringBuilder ();
			byte [] inputBytes = Encoding.UTF8.GetBytes (input);
			byte [] hash = md5_session.ComputeHash (inputBytes);

			for (int i = 0; i < hash.Length; i++) {
				output.Append (hash [i].ToString ("x2"));
			}

			return output.ToString ();
		}
	}
}
