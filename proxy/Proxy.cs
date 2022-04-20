using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Solace.DotNet.Rtsp;
using System.Threading.Tasks;
using System.Diagnostics;
using SolaceSystems.Solclient.Messaging;

namespace RtspClientExample {
	class Proxy {
		static void Main (string [] args)
		{
			var shooter = new Proxy ();
			var tasks = new List<Task> {
				shooter.Stream(args)
			};
			Task.WaitAll (tasks.ToArray ());
		}

		public async Task Stream (string [] args)
		{
			ContextProperties contextProps = new ContextProperties ();
			SessionProperties sessionProps = new SessionProperties ();

			sessionProps.Host = args [0];
			sessionProps.VPNName = args [1];
			sessionProps.UserName = args [2];
			sessionProps.Password = args [3];

			IContext context = null;
			ISession session = null;
			bool verbose = false;
			if (args.Length > 5)
			{
				if (args [5].Equals ("none")) {
					verbose = false;
				} else {
					verbose = true;
				}
			}
			ContextFactoryProperties cfp = new ContextFactoryProperties ();
			// Set log level.
			cfp.SolClientLogLevel = SolLogLevel.Warning;
			// Log errors to console.
			cfp.LogToConsoleError ();
			// Must init the API before using any of its artifacts.
			ContextFactory.Instance.Init (cfp);

			Console.WriteLine ("Solace initializing...");
			context = ContextFactory.Instance.CreateContext (contextProps, null);

			session = context.CreateSession (sessionProps, null, null);

			session.Connect ();
			Console.WriteLine ("Connected.");

			MemoryStream fs_v = null;
			var client = new RTSPClient ();
			var ts = DateTime.MaxValue;

			client.ParameterSetsReceived += async (byte [] sps, byte [] pps) => {
				if (fs_v == null) {
					fs_v = new MemoryStream (4 * 1024);
					//fs_v = new FileStream ($"{name.Replace (" ", "_")}.h264", FileMode.Create);
				}

				if (fs_v != null) {
					await fs_v.WriteAsync (new byte [] { 0x00, 0x00, 0x00, 0x01 }, 0, 4);  // Write Start Code
					await fs_v.WriteAsync (sps, 0, sps.Length);
					await fs_v.WriteAsync (new byte [] { 0x00, 0x00, 0x00, 0x01 }, 0, 4);  // Write Start Code
					await fs_v.WriteAsync (pps, 0, pps.Length);
				}
			};

			client.FrameReceived += async (byte [] nal_units) => {
				if (verbose) {
					Console.WriteLine ("About to send packet with length: " + nal_units.Length);
				}
				//send to solace
				IMessage message = ContextFactory.Instance.CreateMessage ();
				message.Destination = ContextFactory.Instance.CreateTopic (args [4]);
				message.DeliveryMode = MessageDeliveryMode.Direct;
				message.BinaryAttachment = nal_units;

				session.Send (message);
				message.Dispose ();
			};

			try {
				// Connect to RTSP Server
				Console.WriteLine ($"Connecting " + args [5]);

				client.Timeout = 3000;
				client.Connect (args [5], "admin", "password");

				while (!client.IsStreamingFinished ()) {
					await Task.Delay (100);
				}


				fs_v.Close ();
				fs_v.Dispose ();
				session.Dispose ();

				context.Dispose ();

				ContextFactory.Instance.Cleanup ();
			} catch (Exception ex) {
				Console.WriteLine ($"Message: {ex.Message}");
				Console.WriteLine ($"StackTrace: {ex.StackTrace}");
			}
		}
	}
}
