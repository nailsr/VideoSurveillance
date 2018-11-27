using System;

namespace VSHub
{
    public interface IDVR : IDisposable
    {
        void Connect(string addr, int port, string login, string password);

        void Disconnect();

        void CloseChannel(Channel ch);

        Channel OpenChannel(int nChannel, string format);

        event EventHandler OnDisconnected;

        int NumberOfChannels
        {
            get;
        }

        void KeepAlive();
    }
}
