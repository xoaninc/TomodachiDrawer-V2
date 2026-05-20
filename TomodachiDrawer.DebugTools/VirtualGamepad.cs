using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;

namespace TomodachiDrawer.DebugTools
{
    public class VirtualGamepad
    {
        public bool IsConnected { get; private set; } = false;
        public IXbox360Controller? Controller { get; private set; } = null;

        private ViGEmClient? _client;

        private ViGEmClient EnsureClient() => _client ??= new();

        public bool CheckDriver()
        {
            try
            {
                EnsureClient();
                return true;
            }
            catch (VigemBusNotFoundException)
            {
                return false;
            }
        }

        public void Connect()
        {
            if (IsConnected)
                return;

            var client = EnsureClient();

            Controller ??= client.CreateXbox360Controller();
            Controller.Connect();
            IsConnected = true;
        }

        public void Disconnect()
        {
            if (!IsConnected || Controller == null)
                return;

            Controller.Disconnect();
            IsConnected = false;
        }
    }
}
