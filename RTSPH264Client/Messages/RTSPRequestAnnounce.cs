namespace Solace.DotNet.Rtsp.Messages {
	public class RtspRequestAnnounce : RtspRequest {
		// constructor
		public RtspRequestAnnounce ()
		{
			Command = "ANNOUNCE * RTSP/1.0";
		}
	}
}