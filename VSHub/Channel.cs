using System.Net.Sockets;

namespace VSHub
{
    public class Channel
    {
        private IDVR Device;

        public int ID;

        public string Format;

        public NetworkStream Stream;

        public Channel(IDVR dvr)
        {
            Device = dvr;
        }

        public void KeepAlive()
        {
            Device.KeepAlive();
        }

        public void Close()
        {
            Device.CloseChannel(this);
        }
    }
}