using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using VSHub.Configuration;

namespace VSHub
{
    public partial class VSService : ServiceBase
    {
        const string SESSION_COOKIE_NAME = "X-SILOGLLC-VSHUB-SESSIONID";

        HttpListener listener = null;

        Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        Dictionary<string, IDVR> devices = new Dictionary<string, IDVR>();

        VSHubConfigurationSectionHandler Configuration = (VSHubConfigurationSectionHandler)ConfigurationManager.GetSection("vshub") ?? new VSHubConfigurationSectionHandler();

        System.IO.FileSystemWatcher configurationChangesWatcher = null;

        private int refreshConfigurationtryCounter = 0;

        public VSService()
        {
            InitializeComponent();

            try
            {
                string configurationFileName = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;

                configurationChangesWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(configurationFileName), System.IO.Path.GetFileName(configurationFileName));

                configurationChangesWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite;

                configurationChangesWatcher.EnableRaisingEvents = true;
            }
            catch(Exception ex)
            {
                Logger.Error(ex);
            }

            if (configurationChangesWatcher != null)
            {
                configurationChangesWatcher.Changed += (s, e) =>
                {
                    refreshConfigurationtryCounter = 0;

                    RefreshConfiguration(null);
                };
            }
        }

        private void RefreshConfiguration(object state)
        {
            try
            {
                System.Configuration.ConfigurationManager.RefreshSection("vshub");
                Configuration = (VSHubConfigurationSectionHandler)ConfigurationManager.GetSection("vshub") ?? new VSHubConfigurationSectionHandler();

                Logger.Debug("Configuration changes detected. Reloaded.");
            }
            catch (Exception ex)
            {                
                if (refreshConfigurationtryCounter++ < 10) new System.Threading.Timer(RefreshConfiguration, null, 1000, System.Threading.Timeout.Infinite); else Logger.Error(ex);
            }
        }

        protected override void OnStart(string[] args)
        {
            var prefix = Configuration.Prefix;

            if (string.IsNullOrWhiteSpace(prefix)) prefix = "https://*:443/";

            if (!prefix.EndsWith("/")) prefix += "/";

            try
            {
                listener = new HttpListener();

                listener.AuthenticationSchemes = AuthenticationSchemes.Basic;

                listener.Prefixes.Add(prefix);

                listener.Start();

                new System.Threading.Thread(VSServiceRun).Start();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);

                throw new Exception("Could not start Http Listener with prefix " + prefix, ex);
            }
        }

        protected override void OnStop()
        {
            if (listener != null)
            {
                var l = listener;
                listener = null;

                l.Stop();
                l.Close();
            }
        }

        private void VSServiceRun()
        {
            Logger.Debug("VideoSurveillance Hub Service started");

            while (listener != null)
            {
                try
                {
                    var context = listener.GetContext();

                    Logger.Debug("Incoming request from " + context.Request.RemoteEndPoint.ToString());

                    new System.Threading.Thread(ProcessRequest).Start(context);

                    Logger.Debug("Request handler started");
                }
                catch (Exception ex)
                {
                    if (listener != null) Logger.Error(ex);
                }
            }

            Logger.Debug("VideoSurveillance Hub Service stopped");
        }

        private void CloseSession(string sessionId)
        {
            if (sessions.ContainsKey(sessionId))
            {
                var session = sessions[sessionId];

                sessions.Remove(sessionId);

                foreach (Channel ch in session.OpenedChannels)
                {
                    try
                    {
                        if (ch.Stream != null)
                        {
                            ch.Stream.Close();
                            ch.Stream.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex);
                    }
                }
            }
        }

        private void ProcessRequest(object data)
        {
            HttpListenerContext context = null;

            try
            {
                context = (HttpListenerContext)data;

                Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] URL:" + context.Request.RawUrl);

                if (!context.Request.IsAuthenticated || context.User == null || !(context.User.Identity is HttpListenerBasicIdentity))
                {
                    context.Response.StatusCode = 401;
                    context.Response.StatusDescription = "Authentication required";
                    context.Response.Close();

                    Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Not Authenticated!");

                    return;
                }

                string userName = null;
                bool authorized = false;

                try
                {
                    var identity = (HttpListenerBasicIdentity)context.User.Identity;

                    userName = identity.Name;

                    if (Configuration.Users != null && !string.IsNullOrWhiteSpace(userName))
                    {
                        foreach (User user in Configuration.Users)
                        {
                            if (!string.IsNullOrWhiteSpace(user.Name) && string.Compare(user.Name, userName) == 0 && string.Compare(user.Password, identity.Password ?? string.Empty) == 0)
                            {
                                authorized = true;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                if (!authorized)
                {
                    context.Response.StatusCode = 401;
                    context.Response.StatusDescription = "User name or password is incorrect";
                    context.Response.Close();

                    Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Not Authorized!");

                    return;
                }

                string sessionID = context.Request.Cookies[SESSION_COOKIE_NAME] == null ? null : context.Request.Cookies[SESSION_COOKIE_NAME].Value;

                Session session = null;

                if (!string.IsNullOrWhiteSpace(sessionID) && sessions.ContainsKey(sessionID))
                {
                    session = sessions[sessionID];

                    if (string.Compare(userName, session.UserName) != 0)
                    {
                        CloseSession(sessionID);

                        session = null;
                    }
                }

                if (session == null)
                {
                    sessionID = BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(Guid.NewGuid().ToByteArray())).Replace("-", string.Empty);

                    context.Response.Cookies.Add(new Cookie(SESSION_COOKIE_NAME, sessionID));

                    session = new Session(sessionID, userName);

                    Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Created session:" + sessionID + " (" + userName + ")");

                    sessions.Add(sessionID, session);
                }

                if (string.IsNullOrWhiteSpace(context.Request.Url.LocalPath) || string.Compare(context.Request.Url.LocalPath, "/") == 0)
                {
                    context.Response.ContentType = "text/html; charset=\"utf-8\"";

                    context.Response.StatusCode = 200;

                    var sb = new System.Text.StringBuilder();

                    sb.Append("<!DOCTYPE html><html lang=\"en\"><head><title>VideoSurveillance Hub</title></head><body>");

                    if (Configuration.Sources != null)
                    {
                        foreach (Source source in Configuration.Sources)
                        {
                            bool accessAllowed = false;

                            foreach (AccessRights r in source.AccessRights)
                            {
                                if (string.IsNullOrWhiteSpace(r.Users)) continue;

                                if (string.Compare(r.Users, "*", true) == 0 || string.Compare(r.Users, session.UserName, true) == 0)
                                {
                                    if (string.Compare(r.Verb, "allow", true) == 0) accessAllowed = true;
                                    else if (string.Compare(r.Verb, "deny", true) == 0) accessAllowed = false;
                                }
                            }

                            if (accessAllowed && string.Compare(source.Format, "MPEG2TS", true) == 0)
                            {
                                sb.Append("<p><a href=\"/stream/" + source.Name + "\">" + source.Description + "</a></p>");
                            }
                        }
                    }

                    sb.Append("</body></html>");

                    var response = Encoding.UTF8.GetBytes(sb.ToString());

                    context.Response.OutputStream.Write(response, 0, response.Length);

                    context.Response.Close();
                }
                else if (string.Compare(context.Request.Url.LocalPath, "/streams") == 0 || string.Compare(context.Request.Url.LocalPath, "/stream/") == 0)
                {
                    context.Response.ContentType = "text/plain";

                    context.Response.StatusCode = 200;

                    var sb = new System.Text.StringBuilder();

                    sb.Append("{\"SessionID\":\"" + sessionID + "\",\"Streams\":[");

                    if (Configuration.Sources != null)
                    {
                        bool needComma = false;

                        foreach (Source source in Configuration.Sources)
                        {
                            bool accessAllowed = false;

                            foreach (AccessRights r in source.AccessRights)
                            {
                                if (string.IsNullOrWhiteSpace(r.Users)) continue;

                                if (string.Compare(r.Users, "*", true) == 0 || string.Compare(r.Users, session.UserName, true) == 0)
                                {
                                    if (string.Compare(r.Verb, "allow", true) == 0) accessAllowed = true;
                                    else if (string.Compare(r.Verb, "deny", true) == 0) accessAllowed = false;
                                }
                            }

                            if (accessAllowed)
                            {
                                if (needComma) sb.Append(","); else needComma = true;

                                sb.Append("{\"Name\":\"" + source.Name + "\",\"Description\":\"" + source.Description + "\",\"URL\":\"/stream/" + source.Name + "\",\"Format\":\"" + source.Format + "\",\"FPS\":" + source.FPS + "}");
                            }
                        }
                    }

                    sb.Append("]}");

                    var response = Encoding.UTF8.GetBytes(sb.ToString());

                    context.Response.OutputStream.Write(response, 0, response.Length);

                    context.Response.Close();
                }
                else if (string.Compare(context.Request.Url.LocalPath, "/close") == 0)
                {
                    CloseSession(session.ID);

                    context.Response.ContentType = "text/plain";

                    context.Response.StatusCode = 200;

                    context.Response.Close();
                }
                else if (context.Request.Url.LocalPath.StartsWith("/stream/", StringComparison.CurrentCultureIgnoreCase))
                {
                    bool isRawStream = true;

                    Channel channel = null;

                    var sourceName = context.Request.Url.LocalPath.Substring(8);

                    Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Requested source:" + sourceName);

                    try
                    {
                        if (Configuration.Sources != null)
                        {
                            foreach (Source source in Configuration.Sources)
                            {
                                if (!string.IsNullOrWhiteSpace(source.Name) && string.Compare(source.Name, sourceName) == 0)
                                {
                                    if (source.AccessRights != null)
                                    {
                                        bool accessAllowed = false;

                                        foreach (AccessRights r in source.AccessRights)
                                        {
                                            if (string.IsNullOrWhiteSpace(r.Users)) continue;

                                            if (string.Compare(r.Users, "*", true) == 0 || string.Compare(r.Users, session.UserName, true) == 0)
                                            {
                                                if (string.Compare(r.Verb, "allow", true) == 0) accessAllowed = true;
                                                else if (string.Compare(r.Verb, "deny", true) == 0) accessAllowed = false;
                                            }
                                        }

                                        if (accessAllowed && !string.IsNullOrWhiteSpace(source.DeviceName))
                                        {
                                            if (devices.ContainsKey(source.DeviceName))
                                            {
                                                try
                                                {
                                                    var dvr = devices[source.DeviceName];

                                                    channel = dvr.OpenChannel(source.DeviceChannel, source.Format, source.FPS == 0 ? 25 : source.FPS);

                                                    if (string.Compare(source.Format, "MPEG2TS", true) == 0) isRawStream = false;
                                                }
                                                catch (Exception ex)
                                                {
                                                    context.Response.StatusCode = 500;
                                                    context.Response.StatusDescription = "Could not connect to DVR!";
                                                    context.Response.Close();

                                                    throw new ApplicationException("Could not open DVR channel " + source.DeviceChannel.ToString() + "!", ex);
                                                }
                                            }
                                            else
                                            {
                                                try
                                                {
                                                    if (Configuration.Devices != null)
                                                    {
                                                        foreach (Device device in Configuration.Devices)
                                                        {
                                                            if (!string.IsNullOrWhiteSpace(device.Name) && string.Compare(device.Name, source.DeviceName) == 0)
                                                            {
                                                                try
                                                                {
                                                                    var dvr = new XMEyeDVR();

                                                                    dvr.Connect(device.IPAddr, device.Port, device.Login, device.Password);

                                                                    dvr.OnDisconnected += (s, e) =>
                                                                    {
                                                                        devices.Remove(device.Name);
                                                                    };

                                                                    devices.Add(source.DeviceName, dvr);

                                                                    channel = dvr.OpenChannel(source.DeviceChannel, source.Format, source.FPS == 0 ? 25 : source.FPS);

                                                                    if (string.Compare(source.Format, "MPEG2TS", true) == 0) isRawStream = false;
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    context.Response.StatusCode = 500;
                                                                    context.Response.StatusDescription = "Could not connect to DVR!";
                                                                    context.Response.Close();

                                                                    throw new ApplicationException("Could not connect to DVR!", ex);
                                                                }
                                                                break;
                                                            }
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.Error(ex);
                                                }
                                            }

                                            session.OpenedChannels.Add(channel);
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex);
                    }                    

                    if (channel == null)
                    {
                        context.Response.ContentType = "text/plain";

                        context.Response.StatusCode = 404;
                        context.Response.StatusDescription = "Requested source has not be found or it is inaccessible due to ";
                    }
                    else
                    {
                        context.Response.SendChunked = true;

                        var buf = new byte[16384];

                        context.Response.StatusCode = 200;

                        if (isRawStream)
                        {
                            context.Response.ContentType = "video/h264";
                        }
                        else
                        {
                            context.Response.ContentType = "video/mp2t";
                        }

                        try
                        {
                            var s = channel.Stream;

                            uint fps = 25;

                            var start = (uint)DateTime.Now.TimeOfDay.Ticks;

                            uint incr = 90000/fps;

                            uint PCR = 0;
                            uint PTS = incr * fps / 2;
                            uint DTS = incr * fps / 3;

                            int out_p = 0;
                            byte out_frame = 0;

                            var needWritePCR = false;
                            var NALUHeaderFound = false;
                            var NALUType = (byte)0;


                            int out_n = 188;
                            var out_buf = new byte[out_n];

                            if (!isRawStream)
                            {
                                // TS
                                out_buf[0] = 0x47;
                                out_buf[1] = 0x40; // PID:0x00000, PUSI: 1
                                out_buf[2] = 0x00;
                                out_buf[3] = 0x1E; // No adaptation field
                                                   // PSI
                                out_buf[4] = 0x00; // Pointer
                                out_buf[5] = 0x00; // Table ID
                                out_buf[6] = 0xB0; // Contains Data
                                out_buf[7] = 0x0D; // Data Length (With CRC32)
                                out_buf[8] = 0x00; // Table ID Extension (High 8 bits)
                                out_buf[9] = 0x01; // Table ID Extension (Low 8 bits)
                                out_buf[10] = 0xC1; // Current Flag
                                out_buf[11] = 0x00; // Section Number
                                out_buf[12] = 0x00; // Last Section Number
                                                    // PAT
                                out_buf[13] = 0x00; // Program num
                                out_buf[14] = 0x01;
                                out_buf[15] = 0xE1; // PID: 0x00100
                                out_buf[16] = 0x00;

                                var crc32 = Crc32.Calcualte(out_buf, 5 /* Skip Pointer */, ((((int)out_buf[6] & 0x3) << 8) | out_buf[7]) - 1);

                                out_buf[17] = (byte)((crc32 >> 24) & 0xFF);
                                out_buf[18] = (byte)((crc32 >> 16) & 0xFF);
                                out_buf[19] = (byte)((crc32 >> 8) & 0xFF);
                                out_buf[20] = (byte)(crc32 & 0xFF);

                                for (int i = 21; i < out_n; i++) out_buf[i] = 0xFF;

                                context.Response.OutputStream.Write(out_buf, 0, out_n);

                                // TS
                                out_buf[0] = 0x47;
                                out_buf[1] = 0x41; // PID:0x00100, PUSI: 1
                                out_buf[2] = 0x00;
                                out_buf[3] = 0x1F; // No adaptation field
                                                   // PSI
                                out_buf[4] = 0x00; // Pointer
                                out_buf[5] = 0x02; // Table ID
                                out_buf[6] = 0xB0; // Contains Data
                                out_buf[7] = 0x12; // Data Length (With CRC32)
                                out_buf[8] = 0x00; // Table ID Extension (High 8 bits)
                                out_buf[9] = 0x01; // Table ID Extension (Low 8 bits)
                                out_buf[10] = 0xC1; // Current Flag
                                out_buf[11] = 0x00; // Section Number
                                out_buf[12] = 0x00; // Last Section Number
                                                    // PMT
                                out_buf[13] = 0xE1; // PCR PID: 0x00101
                                out_buf[14] = 0x01;
                                out_buf[15] = 0xF0;
                                out_buf[16] = 0x00; // Program info length: 0
                                                    // ES:1
                                out_buf[17] = 0x1B; // Stream Type: 0x1B (ITU-T Rec. H.264 and ISO/IEC 14496-10 (lower bit-rate video) in a packetized stream)
                                out_buf[18] = 0xE1;
                                out_buf[19] = 0x01; // Elementary PID: 0x00101
                                out_buf[20] = 0xF0;
                                out_buf[21] = 0x00; // Elementary stream descriptors length: 0

                                crc32 = Crc32.Calcualte(out_buf, 5 /* Skip Pointer */, ((((int)out_buf[6] & 0x3) << 8) | out_buf[7]) - 1);

                                out_buf[22] = (byte)((crc32 >> 24) & 0xFF);
                                out_buf[23] = (byte)((crc32 >> 16) & 0xFF);
                                out_buf[24] = (byte)((crc32 >> 8) & 0xFF);
                                out_buf[25] = (byte)(crc32 & 0xFF);

                                for (int i = 26; i < out_n; i++) out_buf[i] = 0xFF;

                                context.Response.OutputStream.Write(out_buf, 0, out_n);

                                out_p = 0;
                                needWritePCR = true;

                                out_frame = 0x1;
                            }

                            int zeroes = 0;

                            while (s.CanRead && context.Response.OutputStream.CanWrite)
                            {
                                channel.KeepAlive();

                                var p1 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                                var p2 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                                var p3 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                                var p4 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                                var sz = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);

                                if (buf.Length < sz) buf = new byte[sz];

                                //Logger.Debug("DVR: Read " + sz.ToString() + " bytes. " + p1.ToString() + " " + p2.ToString() + " " + p3.ToString() + " " + p4.ToString());

                                if (isRawStream)
                                {
                                    while (sz > 0 && s.CanRead && context.Response.OutputStream.CanWrite)
                                    {
                                        var n = s.Read(buf, 0, (int)sz); if (n == 0) break;

                                        context.Response.OutputStream.Write(buf, 0, n);

                                        //Logger.Debug("DVR: Received " + n.ToString() + " bytes");

                                        sz -= (uint)n;
                                    }
                                }
                                else
                                {
                                    while (sz > 0 && s.CanRead && context.Response.OutputStream.CanWrite)
                                    {
                                        var n = s.Read(buf, 0, (int)sz); if (n == 0) break;

                                        var p = 0;

                                        while (n > p)
                                        {
                                            if (out_p >= out_n || needWritePCR)
                                            {
                                                if (out_p > 0)
                                                {
                                                    context.Response.OutputStream.Write(out_buf, 0, out_n);
                                                }

                                                out_buf[0] = 0x47;
                                                out_buf[1] = 0x01;
                                                out_buf[2] = 0x01;
                                                out_buf[3] = (byte)(0x10 | (out_frame++ & 0xF));

                                                out_p = 4;

                                                if (needWritePCR)
                                                {
                                                    needWritePCR = false;

                                                    out_buf[1] |= 0x40; // PUSI: 1                                                   
                                                    out_buf[3] |= 0x30; // Adaptaion field present
                                                                        // TS: Adaptation field

                                                    out_buf[out_p++] = 0x07; // Adaptation Field Length
                                                    out_buf[out_p++] = 0x10; // PCR flag
                                                    out_buf[out_p++] = (byte)((PCR >> 25) & 0xFF); // PCR (Program clock reference, stored as 33 bits base, 6 bits reserved, 9 bits extension. The value is calculated as base * 300 + extension.)
                                                    out_buf[out_p++] = (byte)((PCR >> 17) & 0xFF); // PCR
                                                    out_buf[out_p++] = (byte)((PCR >> 9) & 0xFF); // PCR
                                                    out_buf[out_p++] = (byte)((PCR >> 1) & 0xFF); // PCR
                                                    out_buf[out_p++] = 0xFF; // PCR
                                                    out_buf[out_p++] = 0x11; // PCR
                                                    
                                                    // PES
                                                    out_buf[out_p++] = 0x00; // Packet start code prefix
                                                    out_buf[out_p++] = 0x00; // Packet start code prefix
                                                    out_buf[out_p++] = 0x01; // Packet start code prefix
                                                    out_buf[out_p++] = 0xE0; // Stream id (video stream 0xE0)
                                                    out_buf[out_p++] = 0x00; // PES Packet length (High)
                                                    out_buf[out_p++] = 0x00; // PES Packet length (Low)
                                                                        // PES Optional Header
                                                    out_buf[out_p++] = 0x84; // data_alignment_indicator (1 indicates that the PES packet header is immediately followed by the video start code)

                                                    out_buf[out_p++] = 0xC0; // PTS DTS indicator: 11 (11 = both present, 01 is forbidden, 10 = only PTS, 00 = no PTS or DTS)
                                                    out_buf[out_p++] = 0x0A; // PES header Optional Fields length 
                                                                        // PES Optional Header Optional Fields (PTS, DTS)
                                                    out_buf[out_p++] = (byte)(0x31 | ((PTS >> 29) & 0xE));
                                                    out_buf[out_p++] = (byte)((PTS >> 22) & 0xFF);
                                                    out_buf[out_p++] = (byte)(0x1 | ((PTS >> 14) & 0xFE));
                                                    out_buf[out_p++] = (byte)((PTS >> 7) & 0xFF);
                                                    out_buf[out_p++] = (byte)(0x1 | ((PTS << 1) & 0xFE));

                                                    out_buf[out_p++] = (byte)(0x11 | ((DTS >> 29) & 0xE));
                                                    out_buf[out_p++] = (byte)((DTS >> 22) & 0xFF);
                                                    out_buf[out_p++] = (byte)(0x1 | ((DTS >> 14) & 0xFE));
                                                    out_buf[out_p++] = (byte)((DTS >> 7) & 0xFF);
                                                    out_buf[out_p++] = (byte)(0x1 | ((DTS << 1) & 0xFE));

                                                    //PCR = ((uint)DateTime.Now.TimeOfDay.Ticks - start) / 111;
                                                    //PCR += 166666;
                                                    PCR += incr;
                                                    PTS += incr;
                                                    DTS += incr;

                                                    if (NALUHeaderFound)
                                                    {
                                                        if (NALUType == 0x65)
                                                        {
                                                            out_buf[out_p++] = 0;
                                                            out_buf[out_p++] = 0;
                                                            out_buf[out_p++] = 0;
                                                            out_buf[out_p++] = 1;
                                                            out_buf[out_p++] = 9;
                                                            out_buf[out_p++] = 0xF0;
                                                        }

                                                        out_buf[out_p++] = 0;
                                                        out_buf[out_p++] = 0;
                                                        out_buf[out_p++] = 0;
                                                        out_buf[out_p++] = 1;
                                                        out_buf[out_p++] = NALUType;

                                                        zeroes = 0;
                                                        NALUHeaderFound = false;
                                                        NALUType = 0;
                                                    }
                                                }
                                            }

                                            while (p<n && out_p<out_n)
                                            {
                                                var b = buf[p++];

                                                if (NALUHeaderFound)
                                                {
                                                    if (b == 0x65 || b == 0x61)
                                                    {
                                                        NALUType = b;

                                                        needWritePCR = true;
                                                        break;
                                                    }
                                                    else
                                                    {
                                                        out_buf[out_p++] = b;
                                                        NALUHeaderFound = false;
                                                    }
                                                }
                                                else
                                                {
                                                    out_buf[out_p++] = b;

                                                    if (b == 0) 
                                                    { 
                                                        zeroes++; 
                                                    }
                                                    else
                                                    {
                                                        if (b == 1 && zeroes > 2)
                                                        {
                                                            NALUHeaderFound = true;
                                                        }
                                                        zeroes = 0;
                                                    }
                                                }
                                            }

                                            if (needWritePCR)
                                            {
                                                while (out_p < out_n) out_buf[out_p++] = 0xFF;
                                            }
                                        }

                                        sz -= (uint)n;
                                    }
                                }
                            }

                            if (!isRawStream)
                            {
                                if (context.Response.OutputStream.CanWrite)
                                {
                                    for (int i = out_p; i < out_n; i++) out_buf[i] = 0xFF;

                                    context.Response.OutputStream.Write(out_buf, 0, out_n);
                                }
                            }
                        }
                        catch (ObjectDisposedException)
                        { }
                        catch (HttpListenerException)
                        { }
                        catch (Exception ex)
                        {
                            Logger.Error(ex);
                        }
                        finally
                        {
                            channel.Close();
                        }
                    }

                    context.Response.Close();
                }
                else
                {
                    context.Response.ContentType = "text/plain";

                    context.Response.StatusCode = 404;

                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}