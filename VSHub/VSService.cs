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
        HttpListener listener = null;

        Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        Dictionary<string, IDVR> devices = new Dictionary<string, IDVR>();

        VSHubConfigurationSectionHandler Configuration = (VSHubConfigurationSectionHandler)ConfigurationManager.GetSection("vshub") ?? new VSHubConfigurationSectionHandler();

        public VSService()
        {
            InitializeComponent();            
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
            catch(Exception ex)
            {
                Logger.Error(ex);

                throw new Exception("Could not start Http Listener with prefix " + prefix, ex);
            }
        }

        protected override void OnStop()
        {
            if (listener != null)
            {
                listener.Stop();
                listener.Close();
                listener = null;
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
                catch(Exception ex)
                {
                    Logger.Error(ex);
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
                    if (ch.Stream != null)
                    {
                        ch.Stream.Close();
                        ch.Stream.Dispose();
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

                if (context.Request.RawUrl.StartsWith("/login", StringComparison.CurrentCultureIgnoreCase))
                {
                    string sessionID = BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(Guid.NewGuid().ToByteArray())).Replace("-", string.Empty);

                    if (!context.Request.IsAuthenticated || context.User == null || !(context.User.Identity is HttpListenerBasicIdentity))
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();

                        throw new ApplicationException("[" + context.Request.RemoteEndPoint.ToString() + "] Not Authenticated!");
                    }

                    var identity = (HttpListenerBasicIdentity)context.User.Identity;

                    bool authenticated = false;

                    try
                    {
                        if (Configuration.Users != null)
                        {
                            foreach (User user in Configuration.Users)
                            {
                                if (!string.IsNullOrWhiteSpace(user.Name) && string.Compare(user.Name, identity.Name) == 0 && string.Compare(user.Password, identity.Password ?? string.Empty) == 0)
                                {
                                    authenticated = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        Logger.Error(ex);
                    }

                    if (!authenticated)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.StatusDescription = "User name or password is incorrect";
                        context.Response.Close();

                        throw new ApplicationException("[" + context.Request.RemoteEndPoint.ToString() + "] Not Authenticated!");
                    }

                    Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Created session:" + sessionID);

                    var session = new Session(sessionID, identity.Name);

                    sessions.Add(sessionID, session);

                    context.Response.ContentType = "text/plain";

                    context.Response.StatusCode = 200;

                    var sb = new System.Text.StringBuilder();

                    sb.Append("{\"SessionID\":\""+sessionID+"\",\"Streams\":[");

                    if(Configuration.Sources!=null)
                    {
                        bool needComma = false;

                        foreach (Source source in Configuration.Sources)
                        {
                            bool accessAllowed = false;

                            foreach (AccessRights r in source.AccessRights)
                            {
                                if (string.IsNullOrWhiteSpace(r.Users)) continue;

                                if (string.Compare(r.Users, "*", true) == 0 || string.Compare(r.Users, session.User, true) == 0)
                                {
                                    if (string.Compare(r.Verb, "allow", true) == 0) accessAllowed = true;
                                    else if (string.Compare(r.Verb, "deny", true) == 0) accessAllowed = false;
                                }
                            }

                            if(accessAllowed)
                            {
                                if (needComma) sb.Append(","); else needComma = true;

                                sb.Append("{\"Name\":\""+source.Name+ "\",\"Description\":\"" + source.Description + "\",\"URL\":\"/" + sessionID + "/stream/" + source.Name + "\"}");
                            }
                        }
                    }

                    sb.Append("]}");

                    var response = Encoding.UTF8.GetBytes(sb.ToString());

                    context.Response.OutputStream.Write(response, 0, response.Length);

                    context.Response.Close();
                }
                else
                {
                    var tokens = context.Request.RawUrl.Split(new char[] { '/' });

                    if (!string.IsNullOrWhiteSpace(tokens[0]) || tokens.Length < 3)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();

                        throw new ApplicationException("Bad request! " + context.Request.RawUrl);
                    }

                    var sessionID = tokens[1];

                    if (string.IsNullOrWhiteSpace(sessionID) || !sessions.ContainsKey(sessionID))
                    {
                        context.Response.StatusCode = 403;
                        context.Response.Close();

                        throw new ApplicationException("Not authorized");
                    }

                    var session = sessions[sessionID];

                    if (string.Compare(tokens[2], "logout", true) == 0)
                    {
                        CloseSession(sessionID);

                        context.Response.ContentType = "text/plain";

                        context.Response.StatusCode = 200;

                        context.Response.Close();
                    }
                    else if (string.Compare(tokens[2], "stream", true) == 0)
                    {
                        Channel channel = null;

                        if (tokens.Length>3)
                        {
                            try
                            {
                                if (Configuration.Sources != null)
                                {
                                    foreach (Source source in Configuration.Sources)
                                    {
                                        if (!string.IsNullOrWhiteSpace(source.Name) && string.Compare(source.Name, tokens[3]) == 0)
                                        {
                                            Logger.Debug("[" + context.Request.RemoteEndPoint.ToString() + "] Requested source:" + tokens[3]);

                                            if (source.AccessRights != null)
                                            {
                                                bool accessAllowed = false;

                                                foreach (AccessRights r in source.AccessRights)
                                                {
                                                    if (string.IsNullOrWhiteSpace(r.Users)) continue;

                                                    if(string.Compare(r.Users,"*",true) == 0 || string.Compare(r.Users, session.User, true) == 0)
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

                                                            channel = dvr.OpenChannel(source.DeviceChannel, source.Format);
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

                                                                            channel = dvr.OpenChannel(source.DeviceChannel, source.Format);
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
                            context.Response.ContentType = "video/mp4";

                            try
                            {
                                var s = channel.Stream;

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

                                    while (sz > 0 && s.CanRead && context.Response.OutputStream.CanWrite)
                                    {
                                        var n = s.Read(buf, 0, (int)sz); if (n == 0) break;

                                        context.Response.OutputStream.Write(buf, 0, n);

                                        //Logger.Debug("DVR: Received " + n.ToString() + " bytes");

                                        sz -= (uint)n;
                                    }

//                                    context.Response.OutputStream.Flush();
                                }
                            }
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
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }        
    }
}
