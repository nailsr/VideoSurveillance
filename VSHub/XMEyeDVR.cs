using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace VSHub
{
    public sealed class XMEyeDVR : IDVR, IDisposable
    {
        Socket c = null;

        NetworkStream s = null;

        int numberOfChannels = 0;

        int aliveInterval = 0;

        int sessionID = 0;

        private DateTime keepAliveDate = DateTime.MinValue;

        private enum CMD : uint
        {
            CONNECT = 0x03E80000,
            SYSTEMINFO = 0x03FC0000,
            BROWSERLANGUAGE = 0x04100000,
            SYSTEMFUNCTION = 0x05500000,
            SYSTEM_TIMEZONE = 0x04120000,
            LOGOUT = 0x05DC0000,
            DISCONNECT = 0x05DE0000,
            CHANNELTITLE = 0x04180000,
            KEEPALIVE = 0x03EE0000,
            OPMONITOR_CLAIM = 0x05850000,
            OPMONITOR_START_STOP = 0x05820000
        }

        private enum RSP : uint
        {
            CONNECT = 0x03E90000,
            SYSTEMINFO = 0x03FD0000,
            BROWSERLANGUAGE = 0x04110000,
            SYSTEMFUNCTION = 0x05510000,
            SYSTEM_TIMEZONE = 0x04130000,
            LOGOUT = 0x05DD0000,
            DISCONNECT = 0x05DF0000,
            CHANNELTITLE = 0x04190000,
            KEEPALIVE = 0x03EF0000,
            OPMONITOR_CLAIM = 0x05860000,
            OPMONITOR_START_STOP = 0x05830000
        }

        private System.Threading.Timer timerAlive;

        public event EventHandler OnDisconnected;

        private bool connected = false;

        public void Connect(string addr, int port, string login, string password)
        {
            c = new Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

            c.Connect(addr, port);

            s = new NetworkStream(c, false);

            Send(s, CMD.CONNECT, "{ \"EncryptType\" : \"MD5\", \"LoginType\" : \"VideoSurveillanceMonitor\", \"PassWord\" : \"" + password + "\", \"UserName\" : \"" + login + "\" }\n");

            var r = Recv(s, RSP.CONNECT);

            int responseCode = int.Parse(r["Ret"]);

            if (responseCode != 100) throw new Exception("Connection failed with return code " + responseCode.ToString() + "!");

            numberOfChannels = int.Parse(r["ChannelNum"]);

            aliveInterval = int.Parse(r["AliveInterval"]);

            var id = r["SessionID"];

            if (id.StartsWith("0x"))
            {
                sessionID = int.Parse(id.Substring(2), System.Globalization.NumberStyles.HexNumber);
            }
            else
            {
                sessionID = int.Parse(id);
            }

            connected = true;

            KeepAlive();

            if (aliveInterval > 0) timerAlive = new System.Threading.Timer(new System.Threading.TimerCallback(x =>
            {
                var dvr = (XMEyeDVR)x;

                if(dvr.keepAliveDate.AddSeconds(dvr.aliveInterval)<DateTime.Now)
                {
                    dvr.Disconnect();
                }
                else if (dvr.connected)
                {
                    dvr.Send(dvr.s, CMD.KEEPALIVE, "{ \"Name\" : \"KeepAlive\", \"SessionID\" : \"0x" + dvr.sessionID.ToString("X8") + "\" }\n");
                    dvr.Recv(dvr.s, RSP.KEEPALIVE);
                }
            }), this, TimeSpan.FromSeconds(aliveInterval), TimeSpan.FromSeconds(aliveInterval));
        }

        public void Disconnect()
        {
            if (connected)
            {
                connected = false;

                Send(s, CMD.DISCONNECT, "{ \"Name\" : \"\", \"SessionID\" : \"0x" + sessionID.ToString("X8") + "\" }\n");

                sessionID = 0;
            }

            if (timerAlive != null)
            {
                timerAlive.Dispose();
                timerAlive = null;
            }

            if (OnDisconnected != null) OnDisconnected(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disconnect();
        }

        public void CloseChannel(Channel ch)
        {
            if (connected)
            {
                Send(ch.Stream, CMD.OPMONITOR_START_STOP, "{ \"Name\" : \"OPMonitor\", \"OPMonitor\" : { \"Action\" : \"Stop\", \"Parameter\" : { \"Channel\" : " + ch.ID.ToString() + ", \"CombinMode\" : \"CONNECT_ALL\", \"StreamType\" : \"Main\", \"TransMode\" : \"TCP\" } }, \"SessionID\" : \"0x" + sessionID.ToString("X8") + "\" }\n");
                //Recv(ch.Stream, RSP.OPMONITOR_START_STOP);

                ch.Stream.Close();
                ch.Stream.Dispose();
            }
        }

        public Channel OpenChannel(int nChannel, string format)
        {
            if (!connected) throw new Exception("Not connected");

            var c = new Socket(SocketType.Stream, ProtocolType.Tcp);

            c.Connect(this.c.RemoteEndPoint);

            var s = new NetworkStream(c);

            Send(s, CMD.OPMONITOR_CLAIM, "{ \"Name\" : \"OPMonitor\", \"OPMonitor\" : { \"Action\" : \"Claim\", \"Parameter\" : { \"Channel\" : " + nChannel.ToString() + ", \"CombinMode\" : \"CONNECT_ALL\", \"StreamType\" : \"Main\", \"TransMode\" : \"TCP\" } }, \"SessionID\" : \"0x" + sessionID.ToString("X8") + "\" }\n");

            var r = Recv(s, RSP.OPMONITOR_CLAIM);

            int responseCode = int.Parse(r["Ret"]);

            if (responseCode != 100) throw new Exception("OpenChannel failed on Claim with return code " + responseCode.ToString() + "!");

            Send(s, CMD.OPMONITOR_START_STOP, "{ \"Name\" : \"OPMonitor\", \"OPMonitor\" : { \"Action\" : \"Start\", \"Parameter\" : { \"Channel\" : " + nChannel.ToString() + ", \"CombinMode\" : \"CONNECT_ALL\", \"StreamType\" : \"Main\", \"TransMode\" : \"TCP\" } }, \"SessionID\" : \"0x" + sessionID.ToString("X8") + "\" }\n");

            return new Channel(this) { ID = nChannel, Stream = s, Format = format };
        }

        private void Send(NetworkStream s, CMD cmdID, string cmd)
        {
            try
            {
                s.WriteByte(255); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);

                s.WriteByte((byte)((uint)sessionID &0xff)); s.WriteByte((byte)(((uint)sessionID >>8)&0xff)); s.WriteByte((byte)(((uint)sessionID >> 16) & 0xff)); s.WriteByte((byte)(((uint)sessionID >> 24) & 0xff));

                s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);

                s.WriteByte((byte)((uint)cmdID & 0xff)); s.WriteByte((byte)(((uint)cmdID >> 8) & 0xff)); s.WriteByte((byte)(((uint)cmdID >> 16) & 0xff)); s.WriteByte((byte)(((uint)cmdID >> 24) & 0xff));

                var bytes = Encoding.UTF8.GetBytes(cmd);
                var len = bytes.Length;

                s.WriteByte((byte)((uint)len & 0xff)); s.WriteByte((byte)(((uint)len >> 8) & 0xff)); s.WriteByte((byte)(((uint)len >> 16) & 0xff)); s.WriteByte((byte)(((uint)len >> 24) & 0xff));

                s.Write(bytes, 0, len);

                s.Flush();
            }
            catch (Exception)
            { }
        }

        private Dictionary<string,string> Recv(NetworkStream s, RSP rspID)
        {
            try
            {
                var p1 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                var p2 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                var p3 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                var p4 = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);
                var sz = (uint)s.ReadByte() | ((uint)s.ReadByte() << 8) | ((uint)s.ReadByte() << 16) | ((uint)s.ReadByte() << 24);

                if (sz == 0) return null;

                var buf = new byte[sz];

                using (var m = new System.IO.MemoryStream())
                {
                    while (sz > 0)
                    {
                        var n = s.Read(buf, 0, (int)sz); if (n == 0) break;

                        m.Write(buf, 0, n);

                        Logger.Debug("DVR: Received " + n.ToString() + " bytes: " + Encoding.UTF8.GetString(buf, 0, n));

                        sz -= (uint)n;
                    }

                    var ser = new DataContractJsonSerializer(typeof(Dictionary<string, string>), new DataContractJsonSerializerSettings()
                    {
                        UseSimpleDictionaryFormat = true
                    });

                    m.Seek(0, System.IO.SeekOrigin.Begin);

                    return (Dictionary<string, string>)ser.ReadObject(m);
                }
            }
            catch(Exception ex)
            {
                Logger.Error(ex);

                return null;
            }
        }

        public int NumberOfChannels
        {
            get
            {
                return numberOfChannels;
            }
        }        

        public void KeepAlive()
        {
            keepAliveDate = DateTime.Now;
        }
    }
}
