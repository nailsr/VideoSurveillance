Most Chinese DVR devices are fairly cheap and convenient, but completely unsafe. To access real-time video from a DVR, but to make the DVR inaccessible from the Internet and also to prohibit the DVR from accessing the Internet, this server was created. 

Server handles requests over HTTPS protocol. It requires BASIC authentication with user name and password to login. During logon it generates a session identifier whish is pretty secure and can be changed in private implementation. By using session identifier clients may request for H.264 stream from DVR/IP cameras.

Currently implements communication with XMEye DVR(HDR) in part of a streaming live video from cameras.


VSHub - Windows Service

VSMon.UWP - Sample client application for Universal Windows Platform 10.0
