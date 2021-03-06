# Live RTSP Streaming over Solace (RTSPProxy)
## Overview

This project is inspired by Robert Hsieh's work as at the blog post [here](https://solace.com/blog/use-cases/live-video-streaming-solace-part-2).  

where he demonstrated how live video can be streaed over Solace broker using UDP and SMF. In this project I am extending the work to support RTSP stream, which is a widely used protocol in video streaming domain.

The following diagram shows the flow of video stream from the live source to the receiving device:

![](https://github.com/yangsen207/RTSPProxy/blob/main/resources/Diagrams.png)

Note: SMF stands for Solace Message Format and is the wireline message format used by the Solace Java API.

- The RTSPProxy act as an RTSP client, it initialize the RTSP request to an RTSP server. Upon establish the RTSP streaming, it will keep maintain the connection to the RTSP server. When the RTP packets are received, it encapsulate the packets into SMF as binary attachment and forwards them to the Solace Message Router on a topic.  In this scenario, a topic can be viewed as the 'channel name' where the video source is streaming the live video to. The delivery mode can be persistent or direct.

- If you have an IP camera which can provide an RTSP stream, you can use the RTSP stream from the camera for testing.
- Otherwise you can use [rtsp-simple-server](https://github.com/aler9/rtsp-simple-server) and ffmpeg to stream a video file to RTSP stream. An example command would be:

        ffmpeg.exe -re -stream_loop -1 -i my-video-file.mp4 -c copy -f rtsp rtsp://127.0.0.1:8554/mystream

- The same OutputProxy as in Robert's project can be used to get the video packets from Solace broker and output to an UDP port as RTP stream. Basically it subscribe to a topic where the video packets are streamed to. After receiving data from the topic, it re-encapsulates the content into RTP stream packets and redirect to the specified host and port.

- Network stream viewing programes such as VLC can then be used to pick up the redirected live stream based on the
forwarding host and port specified by the OutputProxy.  To play the RTP stream, the video player will require an Session Description Protocol(SDP) file which describe the video stream. This RTSPProxy will save the SDP into a file named video.sdp when it receives the server reply for DESCRIBE request. 
- You will need to update the sdp file to the correct UDP port number before the player can play the RTP stream, search for the line starting with 
	`m=video 0 RTP/AVP`
and update the number 0 to the port number OutputProxy uses.
	
- When you use VLC to play the SDP file, VLC will start to listen on the port and be able to decode the RTP packets correctly. Check the sample_video.sdp in the resources directory for a reference.

## Limitation
Currently the RTSPProxy is able to stream video content only, the support for audio can be implemented similarly.


## Build the Project
This project requires .NET Core 3.1 to build.
You can clone and build using Visual Studio or from command line. For example:

  1. clone this GitHub repository
  2. `MSBuild RTSPProxy.sln /p:Configuration=Release /p:Platform=x64`


## Running the Project

You need to run the RTSPProxy first then the outputProxy(from Robert's project [here](https://github.com/roberthatwork/broadcastme)), like the following:

    RTSPProxy\proxy\bin\x64\Release\netcoreapp2.0\dotnet RTSPProxy.dll <msg_backbone_ip:port> <message-vpn> <username> <password> <topic> rtsp_video_uri <verbose/none>

    \build\staged\bin\outputProxy <msg_backbone_ip:port> <message-vpn> <username> <topic> <redirect_host_ip> <redirect_host_port> <verbose/none>
	(you can update the program to accept password for the username)


## License

This project is licensed under the Apache License, Version 2.0. - See the [LICENSE](LICENSE) file for details.


## Resources

If you want to learn more about Solace Technology try these resources:

- Robert Hsieh's Live Video Streaming over Solace: https://solace.com/blog/live-video-streaming-solace-part-2/
- The Solace Developer Portal website at: http://dev.solace.com
- Get a better understanding of [Solace technology](http://dev.solace.com/tech/).
- Check out the [Solace blog](http://dev.solace.com/blog/) for other interesting discussions around Solace technology
- Ask the [Solace community.](http://dev.solace.com/community/)
