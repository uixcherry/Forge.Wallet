using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core.Commands;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace Forge.Wallet
{
    public class Plugin : RocketPlugin<Configuration>
    {
        #region Properties

        public static Plugin Instance { get; private set; }

        #endregion

        #region Fields

        private uint _transferAmount;
        private CSteamID _receiverID;
        private Dictionary<CSteamID, DateTime> _lastTransferTime = new Dictionary<CSteamID, DateTime>();

        #endregion

        #region Lifecycle Methods

        protected override void Load()
        {
            Instance = this;

            EffectManager.onEffectButtonClicked += OnEffectButtonClicked;
            EffectManager.onEffectTextCommitted += OnEffectTextCommitted;
            U.Events.OnPlayerDisconnected += OnPlayerDisconnected;
            UseableConsumeable.onPerformingAid += OnPerformingAid;

            Logger.Log($"{Name} v{Assembly.GetName().Version} загружен!");
        }

        protected override void Unload()
        {
            EffectManager.onEffectButtonClicked -= OnEffectButtonClicked;
            EffectManager.onEffectTextCommitted -= OnEffectTextCommitted;
            U.Events.OnPlayerDisconnected -= OnPlayerDisconnected;
            UseableConsumeable.onPerformingAid -= OnPerformingAid;

            Instance = null;
            Logger.Log($"{Name} выгружен!");
        }

        #endregion

        #region Event Handlers

        private void OnPlayerDisconnected(UnturnedPlayer player)
        {
            if (player.CSteamID == _receiverID)
            {
                UnturnedPlayer sender = GetSenderForReceiver(_receiverID);
                if (sender != null)
                {
                    UnturnedChat.Say(sender, Translate("command_player_disconnected_cancel"), Color.yellow);
                    CloseWalletUI(sender);
                }

                _receiverID = CSteamID.Nil;
            }
        }

        private void OnEffectTextCommitted(Player player, string buttonName, string input)
        {
            UnturnedPlayer uPlayer = UnturnedPlayer.FromPlayer(player);

            if (!HasWalletPermission(uPlayer)) return;

            if (buttonName != "forge.inputAmount_wallet") return;

            if (!uint.TryParse(input, out uint amount))
            {
                UnturnedChat.Say(uPlayer, Translate("command_error_invalid_amount"), Color.red);
                return;
            }

            uint minAmount = Configuration.Instance.MinTransferAmount;
            uint maxAmount = Configuration.Instance.MaxTransferAmount;

            if (amount < minAmount || amount > maxAmount)
            {
                UnturnedChat.Say(uPlayer, Translate("command_error_amount_range", minAmount, maxAmount), Color.red);
                return;
            }

            _transferAmount = amount;
        }

        private void OnEffectButtonClicked(Player player, string buttonName)
        {
            UnturnedPlayer uPlayer = UnturnedPlayer.FromPlayer(player);

            if (!HasWalletPermission(uPlayer)) return;

            switch (buttonName)
            {
                case "forge.close_wallet":
                    CloseWalletUI(uPlayer);
                    break;

                case "forge.transfer_wallet":
                    HandleTransferRequest(uPlayer);
                    break;

                default:
                    HandlePlayerSelectionButton(uPlayer, buttonName);
                    break;
            }
        }

        private void OnPerformingAid(Player instigator, Player target, ItemConsumeableAsset asset, ref bool shouldAllow)
        {
            if (asset.id != Configuration.Instance.WalletItemID) return;

            UnturnedPlayer uInstigator = UnturnedPlayer.FromPlayer(instigator);
            UnturnedPlayer uTarget = UnturnedPlayer.FromPlayer(target);

            if (uInstigator != null && uTarget != null)
            {
                if (!HasWalletPermission(uInstigator))
                {
                    UnturnedChat.Say(uInstigator, Translate("error_no_permission"), Color.red);
                    return;
                }

                OpenWalletUI(uInstigator, uTarget);
            }
        }

        #endregion

        #region UI Management

        private void OpenWalletUI(UnturnedPlayer sender, UnturnedPlayer receiver)
        {
            try
            {
                sender.Player.enablePluginWidgetFlag(EPluginWidgetFlags.Modal);
                EffectManager.sendUIEffect(Configuration.Instance.EffectID, Configuration.Instance.EffectKey,
                    sender.Player.channel.owner.transportConnection, true);
                EffectManager.sendUIEffectText(Configuration.Instance.EffectKey,
                    sender.Player.channel.owner.transportConnection, true, "forge.playerName_wallet",
                    receiver.DisplayName);

                _receiverID = receiver.CSteamID;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при открытии UI кошелька: {ex.Message}");
                _receiverID = CSteamID.Nil;
            }
        }

        private void CloseWalletUI(UnturnedPlayer player)
        {
            player.Player.disablePluginWidgetFlag(EPluginWidgetFlags.Modal);
            EffectManager.askEffectClearByID(Configuration.Instance.EffectID,
                player.Player.channel.owner.transportConnection);

            _receiverID = CSteamID.Nil;
        }

        private void HandlePlayerSelectionButton(UnturnedPlayer player, string buttonName)
        {
            if (!buttonName.StartsWith("forge.transfer_wallet_")) return;

            string idPart = buttonName.Substring("forge.transfer_wallet_".Length);

            if (!ulong.TryParse(idPart, out ulong receiverID))
            {
                UnturnedChat.Say(player, Translate("command_invalid_player_selected"), Color.red);
                return;
            }

            UnturnedPlayer receiver = UnturnedPlayer.FromCSteamID(new CSteamID(receiverID));
            if (receiver == null)
            {
                UnturnedChat.Say(player, Translate("command_player_not_found"), Color.red);
                return;
            }

            OpenWalletUI(player, receiver);
        }

        #endregion

        #region Permission System

        private bool HasWalletPermission(UnturnedPlayer player)
        {
            if (player.HasPermission("forge.wallet") || player.HasPermission("forge.wallet.use"))
            {
                return true;
            }

            UnturnedChat.Say(player, Translate("error_no_permission"), Color.red);
            return false;
        }

        #endregion

        #region Transfer Logic

        private void HandleTransferRequest(UnturnedPlayer player)
        {
            try
            {
                if (_receiverID == CSteamID.Nil)
                {
                    UnturnedChat.Say(player, Translate("command_no_player_selected"), Color.red);
                    return;
                }

                UnturnedPlayer receiver = UnturnedPlayer.FromCSteamID(_receiverID);
                if (receiver == null)
                {
                    UnturnedChat.Say(player, Translate("command_player_not_found"), Color.red);
                    CloseWalletUI(player);
                    return;
                }

                if (player.CSteamID == receiver.CSteamID)
                {
                    UnturnedChat.Say(player, Translate("command_same_player_transfer"), Color.red);
                    return;
                }

                uint amount = _transferAmount;
                uint minAmount = Configuration.Instance.MinTransferAmount;
                uint maxAmount = Configuration.Instance.MaxTransferAmount;

                if (amount < minAmount || amount > maxAmount)
                {
                    UnturnedChat.Say(player, Translate("command_error_amount_range", minAmount, maxAmount), Color.red);
                    return;
                }

                if (player.Experience < amount)
                {
                    UnturnedChat.Say(player, Translate("command_not_enough_experience"), Color.red);
                    return;
                }

                if (!CanTransfer(player))
                {
                    return;
                }

                ExecuteTransfer(player, receiver, amount);
                CloseWalletUI(player);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при обработке запроса на передачу: {ex.Message}");
                CloseWalletUI(player);
            }
        }

        private bool CanTransfer(UnturnedPlayer player)
        {
            if (_lastTransferTime.TryGetValue(player.CSteamID, out DateTime lastTransfer))
            {
                TimeSpan elapsed = DateTime.Now - lastTransfer;
                int cooldownSeconds = Configuration.Instance.CooldownSeconds;

                if (elapsed.TotalSeconds < cooldownSeconds)
                {
                    int remainingSeconds = cooldownSeconds - (int)elapsed.TotalSeconds;
                    UnturnedChat.Say(player, Translate("command_transfer_cooldown", remainingSeconds), Color.yellow);
                    return false;
                }
            }
            return true;
        }

        private void ExecuteTransfer(UnturnedPlayer sender, UnturnedPlayer receiver, uint amount)
        {
            sender.Experience -= amount;
            receiver.Experience += amount;

            _lastTransferTime[sender.CSteamID] = DateTime.Now;

            UnturnedChat.Say(sender, Translate("command_transfer_success_sender", amount, receiver.DisplayName), Color.green);
            UnturnedChat.Say(receiver, Translate("command_transfer_success_receiver", amount, sender.DisplayName), Color.green);

            LogTransaction(sender, receiver, amount);
        }

        private void LogTransaction(UnturnedPlayer sender, UnturnedPlayer receiver, uint amount)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Logger.Log($"[WALLET] [{timestamp}] {sender.CharacterName} ({sender.CSteamID}) передал {amount} опыта игроку {receiver.CharacterName} ({receiver.CSteamID})");
        }

        #endregion

        #region Utility Methods

        private UnturnedPlayer GetSenderForReceiver(CSteamID receiverID)
        {
            foreach (SteamPlayer steamPlayer in Provider.clients)
            {
                UnturnedPlayer player = UnturnedPlayer.FromSteamPlayer(steamPlayer);
                if (player.Player.pluginWidgetFlags.HasFlag(EPluginWidgetFlags.Modal))
                {
                    return player;
                }
            }
            return null;
        }

        #endregion

        #region Commands

        [RocketCommand("wallet", "Открыть кошелек для передачи опыта", "wallet [player]", AllowedCaller.Player)]
        public void WalletCommand(IRocketPlayer caller, string[] command)
        {
            UnturnedPlayer player = (UnturnedPlayer)caller;

            if (!HasWalletPermission(player)) return;

            if (command.Length == 0)
            {
                UnturnedChat.Say(player, Translate("command_wallet_usage"), Color.yellow);
                return;
            }

            string targetName = command[0];
            UnturnedPlayer target = UnturnedPlayer.FromName(targetName);

            if (target == null)
            {
                UnturnedChat.Say(player, Translate("command_player_not_found"), Color.red);
                return;
            }

            OpenWalletUI(player, target);
        }

        #endregion

        #region Translations

        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "command_error_amount_range", "Сумма должна быть от {0} до {1}." },
            { "command_no_player_selected", "Не выбран игрок для передачи опыта." },
            { "command_invalid_player_selected", "Выбран неверный игрок." },
            { "command_player_not_found", "Игрок не найден." },
            { "command_same_player_transfer", "Вы не можете передать опыт самому себе." },
            { "command_error_invalid_amount", "Неверная сумма для передачи." },
            { "command_not_enough_experience", "Недостаточно опыта для передачи." },
            { "command_player_disconnected_cancel", "Передача отменена, игрок отключился." },
            { "command_transfer_success_sender", "Успешно передано {0} опыта игроку {1}." },
            { "command_transfer_success_receiver", "Получено {0} опыта от игрока {1}." },
            { "command_transfer_cooldown", "Вы сможете совершить следующую передачу через {0} секунд." },
            { "error_no_permission", "У вас нет разрешения на использование кошелька." },
            { "command_wallet_usage", "Использование: /wallet <игрок>" }
        };

        #endregion
    }
}