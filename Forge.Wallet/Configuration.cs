using Rocket.API;

namespace Forge.Wallet
{
    public class Configuration : IRocketPluginConfiguration
    {
        public ushort EffectID { get; set; }
        public short EffectKey { get; set; }
        public ushort WalletItemID { get; set; }
        public uint MinTransferAmount { get; set; }
        public uint MaxTransferAmount { get; set; }
        public int CooldownSeconds { get; set; }

        public void LoadDefaults()
        {
            EffectID = ushort.MaxValue;
            EffectKey = short.MinValue;
            WalletItemID = ushort.MaxValue;
            MinTransferAmount = 10;
            MaxTransferAmount = 1000;
            CooldownSeconds = 60;
        }
    }
}
