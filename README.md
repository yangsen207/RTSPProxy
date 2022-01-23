# Live RTSP Streaming over Solace (RTSPProxy)
## Overview

This project is inspired by Robert Hsieh's work as at the blog post [here](https://solace.com/blog/use-cases/live-video-streaming-solace-part-2).  

where he demonstrated how live video can be streaed over Solace broker using UDP and SMF. In this project I am extending the work to support RTSP stream, which is a widely used protocol in video streaming domain.

The following diagram shows the flow of video stream from the live source to the receiving device:

![](https://github.com/yangsen207/RTSPProxy/master/resources/Diagrams.png)

Note: SMF stands for Solace Message Format and is the wireline message format used by the Solace Java API.

- The RTSPProxy act as an RTSP client, it initialize the RTSP request to an RTSP server. Upon establish the RTSP streaming, it will keep maintain the connection to the RTSP server. When the RTP packets are received, it encapsulate the packets into SMF as binary attachment and forwards them to the Solace Message Router on a topic.  In this scenario, a topic can be viewed as the 'channel name' where the video source is streaming the live video to. The delivery mode can be persistent or direct.

- If you have an IP camera which can provide an RTSP stream, you can use the RTSP stream from the camera for testing.
- Otherwise you can use [rtsp-simple-server](https://github.com/aler9/rtsp-simple-server) and ffmpeg to stream a video file to RTSP stream. An example command would be:

        ffmpeg.exe -re -stream_loop -1 -i my-video-file.mp4 -c copy -f rtsp rtsp://127.0.0.1:8554/mystream

- The same OutputProxy as in Robert's project can be used to get the video packets from Solace broker and output to an UDP port as RTP stream. Basically it creates a temporary queue with a topic (i.e. stream channel) subscription.  It then receives messages 
from its temporary queue, re-encapsulates the content into RTP stream packets and redirect to the specified host and port.

- Network stream viewing programes such as VLC can then be used to pick up the redirected live stream based on the
forwarding host and port specified by the OutputProxy.  To play the RTP stream, the video player will require an SDP file which describe the video stream. Currently there is no convenient way to get the SDP file. A walkaround is to use VLC to play the original RTSP stream and use Wireshark to capture the network traffic. The reply from the RTSP server for the DESCRIBE request contains the SDP information. Copy the content and save it as a .sdp file, update the port number to the one specified in PutputProxy.
- When you use VLC to play the SDP file, VLC will start to listen on the port and be able to decode the RTP packets correctly.

## Limitation
Currently the RTSPProxy is able to redirect video content only, the support for audio can be implemented similarly.


## Build the Project

Just clone and build using Visual Studio or from command line. For example:

  1. clone this GitHub repository
  1. `MSBuild RTSPProxy.sln /p:Configuration=Release /p:Platform=x64`


## Running the Project

You need to run the RTSPProxy first then the outputProxy(from Robert's project [here](https://github.com/roberthatwork/broadcastme)), like the following:

    RTSPProxy\proxy\bin\x64\Release\netcoreapp2.0\dotnet RTSPProxy.dll <msg_backbone_ip:port> <message-vpn> <username> <topic> rtsp_video_uri <verbose/none>

    ./build/staged/bin/outputProxy <msg_backbone_ip:port> <message-vpn> <username> <topic> <redirect_host_ip> <redirect_host_port> <verbose/none>


## License

This project is licensed under the Apache License, Version 2.0. - See the [LICENSE](LICENSE) file for details.


## Resources

If you want to learn more about Solace Technology try these resources:

- Robert Hsieh's Live Video Streaming over Solace: https://solace.com/blog/live-video-streaming-solace-part-2/
- The Solace Developer Portal website at: http://dev.solace.com
- Get a better understanding of [Solace technology](http://dev.solace.com/tech/).
- Check out the [Solace blog](http://dev.solace.com/blog/) for other interesting discussions around Solace technology
- Ask the [Solace community.](http://dev.solace.com/community/)
