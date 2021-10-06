using PKHeX.Core;
using SysBot.Base;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsets;

namespace SysBot.Pokemon
{
    public class RollingRaidBot : PokeRoutineExecutor8, ICountBot
    {
        private readonly PokeTradeHub<PK8> Hub;
        private readonly RollingRaidSettings Settings;
        public ICountSettings Counts => Settings;
        private readonly DenUtil.RaidData RaidInfo = new();

        public RollingRaidBot(PokeBotState cfg, PokeTradeHub<PK8> hub) : base(cfg)
        {
            Hub = hub;
            Settings = hub.Config.RollingRaid;
        }

        private int encounterCount;
        private bool deleteFriends;
        private bool addFriends;
        private readonly bool[] PlayerReady = new bool[4];
        private int raidBossSpecies = -1;
        private bool airplaneUsable = false;
        private bool softLock = false;
        private bool hardLock = false;
        private int airplaneLobbyExitCount;
        private int RaidLogCount;
        private uint denOfs = 0;

        public override async Task MainLoop(CancellationToken token)
        {
            if (Settings.MinTimeToWait is < 0 or > 180)
            {
                Log("Time to wait must be between 0 and 180 seconds.");
                return;
            }
            else if (Settings.DaysToRoll < 0)
            {
                Log("Can't go back to the past.");
                return;
            }

            try
            {
                Log("Identifying trainer data of the host console.");
                RaidInfo.TrainerInfo = await IdentifyTrainer(token).ConfigureAwait(false);
                await InitializeHardware(Settings, token).ConfigureAwait(false);

                Log("Reading den data.");
                await ReadDenData(token).ConfigureAwait(false);

                Log("Starting main RollingRaidBot loop.");
                await InnerLoop(token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception e)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(RollingRaidBot)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        private async Task InnerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.RollingRaid)
            {
                Config.IterateNextRoutine();
                addFriends = false;
                deleteFriends = false;

                // If they set this to 0, they want to add and remove friends before hosting any raids.
                if (Settings.InitialRaidsToHost == 0 && encounterCount == 0)
                {
                    if (Settings.NumberFriendsToAdd > 0)
                        addFriends = true;
                    if (Settings.NumberFriendsToDelete > 0)
                        deleteFriends = true;

                    if (addFriends || deleteFriends)
                    {
                        // Back out of the game.
                        await Click(B, 0_500, token).ConfigureAwait(false);
                        await Click(HOME, 4_000, token).ConfigureAwait(false);
                        await DeleteAddFriends(token).ConfigureAwait(false);
                        await Click(HOME, 1_000, token).ConfigureAwait(false);
                    }
                }

                if (await CheckIfDayRolled(token).ConfigureAwait(false))
                    return;

                encounterCount++;

                // Check if we're scheduled to delete or add friends after this raid is hosted.
                // If we're changing friends, we'll echo while waiting on the lobby to fill up.
                if (Settings.InitialRaidsToHost <= encounterCount)
                {
                    if (Settings.NumberFriendsToAdd > 0 && Settings.RaidsBetweenAddFriends > 0)
                        addFriends = (encounterCount - Settings.InitialRaidsToHost) % Settings.RaidsBetweenAddFriends == 0;
                    if (Settings.NumberFriendsToDelete > 0 && Settings.RaidsBetweenDeleteFriends > 0)
                        deleteFriends = (encounterCount - Settings.InitialRaidsToHost) % Settings.RaidsBetweenDeleteFriends ==
                                        0;
                }

                int code = Settings.GetRandomRaidCode();
                await AutoRollDen(code, token).ConfigureAwait(false);

                Log($"Raid host {encounterCount} finished.");
                Settings.AddCompletedRaids();

                if (airplaneUsable && (Settings.DaysToRoll == 0 || softLock || hardLock))
                    await ResetRaidAirplaneAsync(token).ConfigureAwait(false);
                else await ResetGameAsync(token).ConfigureAwait(false);
            }
        }

        public override async Task HardStop()
        {
            await CleanExit(Settings, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task AutoRollDen(int code, CancellationToken token)
        {
            for (int i = 0; i < Settings.DaysToRoll; i++)
            {
                await DaySkip(token).ConfigureAwait(false);
                await Task.Delay(0_500 + Settings.DateAdvanceDelay, token).ConfigureAwait(false);
                Log($"Roll {i + 1}...");
                if (i == Settings.DaysToRoll - 1)
                    await ResetTime(token).ConfigureAwait(false);
            }

            if (Settings.DaysToRoll > 0)
            {
                for (int i = 0; i < 2; i++)
                    await Click(A, 1_000 + Hub.Config.Timings.ExtraTimeAButtonClickAR, token).ConfigureAwait(false);
                await Click(A, 2_000 + Hub.Config.Timings.ExtraTimeLoadLobbyAR, token).ConfigureAwait(false);

                for (int i = 0; i < 3; i++)
                    await Click(B, 0_300, token).ConfigureAwait(false);
            }

            await CheckDen(token).ConfigureAwait(false);
            await HostRaidAsync(code, token).ConfigureAwait(false);
        }

        private async Task HostRaidAsync(int code, CancellationToken token)
        {
            // Connect to Y-Comm
            await EnsureConnectedToYComm(Hub.Config, token).ConfigureAwait(false);

            // Press A and stall out a bit for the loading
            await Click(A, 5_000 + Hub.Config.Timings.ExtraTimeLoadRaid, token).ConfigureAwait(false);

            if (raidBossSpecies == -1)
            {
                var data = await Connection.ReadBytesAsync(RaidBossOffset, 2, token).ConfigureAwait(false);
                raidBossSpecies = BitConverter.ToUInt16(data, 0);
            }
            Log($"Initializing raid for {(Species)raidBossSpecies}.");

            if (code >= 0)
            {
                // Set Link code
                await Click(PLUS, 1_000, token).ConfigureAwait(false);
                await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);
                await Click(PLUS, 2_000, token).ConfigureAwait(false);
                await Click(A, 2_000, token).ConfigureAwait(false);
            }

            if (addFriends && !string.IsNullOrEmpty(Settings.FriendCode))
                EchoUtil.Echo($"Send a friend request to Friend Code **{Settings.FriendCode}** to join in! Friends will be added after this raid.");

            // Invite others, confirm Pokémon and wait
            await Click(A, 7_000 + Hub.Config.Timings.ExtraTimeOpenRaid, token).ConfigureAwait(false);
            if (!softLock)
            {
                await Click(DUP, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }

            var linkcodemsg = code < 0 ? "no Link Code" : $"code **{code:0000 0000}**";

            string raiddescmsg = string.IsNullOrEmpty(Settings.RaidDescription) ? ((Species)raidBossSpecies).ToString() : Settings.RaidDescription;
            EchoUtil.Echo($"Raid lobby for {raiddescmsg} is open with {linkcodemsg}.");
            RaidLog(linkcodemsg, raiddescmsg);

            var timetowait = Settings.MinTimeToWait * 1_000;
            var timetojoinraid = 175_000 - timetowait;

            Log("Waiting on raid party...");
            // Wait the minimum timer or until raid party fills up.
            while (timetowait > 0 && !await GetRaidPartyReady(token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                timetowait -= 1_000;

                if ((PlayerReady[1] || PlayerReady[2] || PlayerReady[3]) && Config.Connection.Protocol == SwitchProtocol.USB && Settings.AirplaneQuitout) // Need at least one player to be ready
                    airplaneUsable = true;
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);
            EchoUtil.Echo($"Raid is starting now with {linkcodemsg}.");

            if (airplaneUsable && softLock) // Because we didn't ready up earlier if we're soft locked
            {
                await Click(DUP, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }
            else if (!airplaneUsable && softLock) // Don't waste time and don't risk losing soft lock; re-host.
                await AirplaneLobbyExit(code, token).ConfigureAwait(false);

            /* Press A and check if we entered a raid.  If other users don't lock in,
               it will automatically start once the timer runs out. If we don't make it into
               a raid by the end, something has gone wrong and we should quit trying. */
            while (timetojoinraid > 0 && !await IsInBattle(token).ConfigureAwait(false))
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
                timetojoinraid -= 0_500;

                if (await IsOnOverworld(Hub.Config, token).ConfigureAwait(false) && softLock) // If overworld, lobby disbanded.
                    await AirplaneLobbyRecover(code, token).ConfigureAwait(false);
            }

            for (int i = 0; i < 4; i++)
                PlayerReady[i] = false;

            Log("Finishing raid routine.");
            await Task.Delay(1_000 + Hub.Config.Timings.ExtraTimeEndRaid, token).ConfigureAwait(false);
        }

        private async Task<bool> GetRaidPartyReady(CancellationToken token)
        {
            bool ready = true;
            for (uint i = 0; i < 4; i++)
            {
                if (!await ConfirmPlayerReady(i, token).ConfigureAwait(false))
                    ready = false;
            }
            return ready;
        }

        private async Task<bool> ConfirmPlayerReady(uint player, CancellationToken token)
        {
            if (PlayerReady[player])
                return true;

            var ofs = RaidP0PokemonOffset + (0x30 * player);
            bool airplaneOverride = softLock && player == 0;
            // Check if the player has locked in.
            var data = await Connection.ReadBytesAsync(ofs + RaidLockedInIncr, 1, token).ConfigureAwait(false);
            if (data[0] == 0 && !airplaneOverride)
                return false;

            PlayerReady[player] = true;

            // If we get to here, they're locked in and should have a Pokémon selected.
            if (Settings.EchoPartyReady)
            {
                data = await Connection.ReadBytesAsync(ofs, 2, token).ConfigureAwait(false);
                var dexno = BitConverter.ToUInt16(data, 0);

                data = await Connection.ReadBytesAsync(ofs + RaidAltFormInc, 1, token).ConfigureAwait(false);
                var altformstr = data[0] == 0 ? "" : TradeCordHelperUtil<PK8>.FormOutput(dexno, data[0], out _);

                data = await Connection.ReadBytesAsync(ofs + RaidShinyIncr, 1, token).ConfigureAwait(false);
                var shiny = data[0] == 1 ? "★" : "";

                data = await Connection.ReadBytesAsync(ofs + RaidGenderIncr, 1, token).ConfigureAwait(false);
                var gender = data[0] == 0 ? " (M)" : (data[0] == 1 ? " (F)" : "");

                EchoUtil.Echo($"Player {player + 1} is ready with {shiny}{(Species)dexno}{altformstr}{gender}!");
            }

            return true;
        }

        private async Task ResetGameAsync(CancellationToken token)
        {
            Log("Resetting raid by restarting the game");
            await CloseGame(Hub.Config, token).ConfigureAwait(false);

            if (addFriends || deleteFriends)
                await DeleteAddFriends(token).ConfigureAwait(false);

            await StartGame(Hub.Config, token).ConfigureAwait(false);
        }

        private async Task DeleteAddFriends(CancellationToken token)
        {
            await NavigateToProfile(token).ConfigureAwait(false);

            // Delete before adding to avoid deleting new friends.
            if (deleteFriends)
            {
                Log("Deleting friends.");
                await NavigateFriendsMenu(true, token).ConfigureAwait(false);
                for (int i = 0; i < Settings.NumberFriendsToDelete; i++)
                    await DeleteFriend(token).ConfigureAwait(false);
            }

            // If we're deleting friends and need to add friends, it's cleaner to back out 
            // to Home and re-open the profile in case we ran out of friends to delete.
            if (deleteFriends && addFriends)
            {
                Log("Navigating back to add friends.");
                await Click(HOME, 2_000, token).ConfigureAwait(false);
                await NavigateToProfile(token).ConfigureAwait(false);
            }

            if (addFriends)
            {
                Log("Adding friends.");
                await NavigateFriendsMenu(false, token).ConfigureAwait(false);
                for (int i = 0; i < Settings.NumberFriendsToAdd; i++)
                    await AddFriend(token).ConfigureAwait(false);
            }

            addFriends = false;
            deleteFriends = false;
            airplaneLobbyExitCount = 0;
            await Click(HOME, 2_000, token).ConfigureAwait(false);
        }

        // Goes from Home screen hovering over the game to the correct profile
        private async Task NavigateToProfile(CancellationToken token)
        {
            int delay = Hub.Config.Timings.KeypressTime;

            await Click(DUP, delay, token).ConfigureAwait(false);
            for (int i = 1; i < Settings.ProfileNumber; i++)
                await Click(DRIGHT, delay, token).ConfigureAwait(false);
            await Click(A, 2_000, token).ConfigureAwait(false);
        }

        // Gets us on the friend card if it exists after HOME button has been pressed.
        // Should already be on either "Friend List" or "Add Friend"
        private async Task NavigateFriendsMenu(bool delete, CancellationToken token)
        {
            int delay = Hub.Config.Timings.KeypressTime;

            // Go all the way up, then down 1. Reverse for adding friends.
            if (delete)
            {
                for (int i = 0; i < 5; i++)
                    await Click(DUP, delay, token).ConfigureAwait(false);
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);

                await NavigateFriends(Settings.RowStartDeletingFriends, 4, token).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < 5; i++)
                    await Click(DDOWN, delay, token).ConfigureAwait(false);
                await Click(DUP, 1_000, token).ConfigureAwait(false);

                // Click into the menu.
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 2_500, token).ConfigureAwait(false);

                await NavigateFriends(Settings.RowStartAddingFriends, 5, token).ConfigureAwait(false);
            }
        }

        // Navigates to the specified row and column.
        private async Task NavigateFriends(int row, int column, CancellationToken token)
        {
            int delay = Hub.Config.Timings.KeypressTime;

            if (row == 1)
                return;

            for (int i = 1; i < row; i++)
                await Click(DDOWN, delay, token).ConfigureAwait(false);

            for (int i = 1; i < column; i++)
                await Click(DRIGHT, delay, token).ConfigureAwait(false);
        }

        // Deletes one friend. Should already be hovering over the friend card.
        private async Task DeleteFriend(CancellationToken token)
        {
            await Click(A, 1_500, token).ConfigureAwait(false);
            // Opens Options
            await Click(DDOWN, 0_600, token).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false);
            // Click "Remove Friend", confirm "Delete", return to next card.
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(A, 5_000 + Hub.Config.Timings.ExtraTimeDeleteFriend, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
        }

        // Adds one friend. Timing may need to be adjusted since delays vary with connection.
        private async Task AddFriend(CancellationToken token)
        {
            await Click(A, 3_500 + Hub.Config.Timings.ExtraTimeAddFriend, token).ConfigureAwait(false);
            await Click(A, 3_000 + Hub.Config.Timings.ExtraTimeAddFriend, token).ConfigureAwait(false);
        }

        private async Task ResetRaidAirplaneAsync(CancellationToken token)
        {
            airplaneUsable = false;
            var timer = 60_000;
            Log("Resetting raid by toggling airplane mode.");
            await ToggleAirplane(Hub.Config.Timings.ExtraTimeAirplane, token).ConfigureAwait(false);
            Log("Airplaned out!");

            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false) && timer > 45)
            {
                await Click(B, 1_000, token).ConfigureAwait(false);
                timer -= 1_000;
            }

            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false) && timer > 0) // If airplaned too late, we might be stuck in raid (move selection)
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                timer -= 1_000;
            }

            if (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false) && timer <= 0) // Something's gone wrong
            {
                softLock = false;
                Log("Something's gone wrong. Restarting by closing the game.");
                await ResetGameAsync(token).ConfigureAwait(false);
                return;
            }

            await Task.Delay(5_000 + Hub.Config.Timings.AirplaneConnectionFreezeDelay).ConfigureAwait(false);
            if (addFriends || deleteFriends)
            {
                await Click(HOME, 4_000, token).ConfigureAwait(false);
                await DeleteAddFriends(token).ConfigureAwait(false);
                await Click(HOME, 2_000, token).ConfigureAwait(false);
            }
            Log("Back in the overworld!");
        }

        private async Task AirplaneLobbyExit(int code, CancellationToken token)
        {
            Log("No players readied up in time; exiting lobby...");
            airplaneUsable = false;
            airplaneLobbyExitCount++;
            for (int i = 0; i < 4; i++)
                PlayerReady[i] = false; // Clear just in case.

            await Click(B, 2_000, token).ConfigureAwait(false);
            await Click(A, 2_000, token).ConfigureAwait(false);
            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
                await Click(B, 1_000, token).ConfigureAwait(false);

            if (Settings.NumberFriendsToAdd > 0 && Settings.RaidsBetweenAddFriends > 0)
                addFriends = (encounterCount + airplaneLobbyExitCount - Settings.InitialRaidsToHost) % Settings.RaidsBetweenAddFriends == 0;
            if (Settings.NumberFriendsToDelete > 0 && Settings.RaidsBetweenDeleteFriends > 0)
                deleteFriends = (encounterCount + airplaneLobbyExitCount - Settings.InitialRaidsToHost) % Settings.RaidsBetweenDeleteFriends == 0;

            if (addFriends || deleteFriends)
            {
                await Click(HOME, 2_000, token).ConfigureAwait(false);
                await DeleteAddFriends(token).ConfigureAwait(false);
                await Click(HOME, 2_000, token).ConfigureAwait(false);
            }
            Log("Back in the overworld! Re-hosting the raid.");
            await HostRaidAsync(code, token).ConfigureAwait(false);
        }

        private async Task AirplaneLobbyRecover(int code, CancellationToken token)
        {
            airplaneUsable = false;
            for (int i = 0; i < 4; i++)
                PlayerReady[i] = false; // Clear just in case.

            Log("Lobby disbanded! Recovering...");
            await Task.Delay(3_000).ConfigureAwait(false); // Wait in case we entered lobby again due to A spam.
            if (await IsOnOverworld(Hub.Config, token).ConfigureAwait(false)) // If still on Overworld, we don't need to do anything special.
            {
                Log("Re-hosting the raid.");
                await HostRaidAsync(code, token).ConfigureAwait(false);
            }
            else
            {
                await ToggleAirplane(0, token).ConfigureAwait(false); // We could be in lobby, or have invited others, or in a box. Conflicts with ldn_mitm, but we don't need it anyways.
                while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
                    await Click(B, 0_500, token).ConfigureAwait(false); // If we airplaned, need to clear errors and leave a box if we were stuck.
                Log("Back in the overworld! Re-hosting the raid.");
                await HostRaidAsync(code, token).ConfigureAwait(false);
            }
        }

        private void RaidLog(string linkcodemsg, string raiddescmsg)
        {
            if (Settings.RaidLog)
            {
                RaidLogCount++;
                File.WriteAllText("RaidCode.txt", $"{raiddescmsg} raid #{RaidLogCount}\n{Settings.FriendCode}\nHosting raid as: {Connection.Label.Split('-')[0]}\nRaid is open with {linkcodemsg}\n------------------------");
            }
        }

        private async Task<bool> CheckIfDayRolled(CancellationToken token)
        {
            if (!Settings.RolloverPrevention)
                return false;

            var denData = await Connection.ReadBytesAsync(DenUtil.GetDenOffset(Settings.DenID, Settings.DenType, out _), 0x18, token).ConfigureAwait(false);
            var den = new RaidSpawnDetail(denData, 0);
            if (!den.WattsHarvested)
            {
                if (encounterCount == 0)
                {
                    Log("For correct operation, start the bot with Watts cleared. If Watts are cleared and you see this message, make sure you've entered the correct den ID.");
                    return true;
                }

                softLock = false;
                Log("Watts appeared in den. Correcting for rollover...");
                await Click(B, 0_250, token).ConfigureAwait(false);
                await CloseGame(Hub.Config, token).ConfigureAwait(false);
                Log("Navigating to time settings...");
                await RolloverCorrection(token, true).ConfigureAwait(false);
                Log("Time sync turned off.");
                await StartGame(Hub.Config, token).ConfigureAwait(false);

                den = new RaidSpawnDetail(denData, 0);
                if (den.WattsHarvested)
                {
                    await SaveGame(Hub.Config, token).ConfigureAwait(false);
                    Log("Turning time sync back on...");
                    await RolloverCorrection(token).ConfigureAwait(false);
                    Log("Rollover correction complete, resuming hosting routine.");
                    await Task.Delay(2_000).ConfigureAwait(false);
                    return false;
                }
                Log("Failed to clear Watts, stopping execution...");
                return true;
            }
            return false;
        }

        private async Task RolloverCorrection(CancellationToken token, bool gameClosed = false)
        {
            if (!gameClosed)
                await Click(HOME, 2_000, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            if (Config.Connection.Protocol == SwitchProtocol.WiFi) // Scroll to system settings
                await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false);
            else await HoldUSB(DDOWN, 2_000, 0_250, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (Config.Connection.Protocol == SwitchProtocol.WiFi) // Scroll to date/time settings
                await PressAndHold(DDOWN, 0_750, 0_250, token).ConfigureAwait(false);
            else await HoldUSB(DDOWN, 0_750, 0_250, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);

            await Click(A, 1_250, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false); // Turn sync off/on
            if (gameClosed)
            {
                for (int i = 0; i < 2; i++)
                    await Click(DDOWN, 0_150, token).ConfigureAwait(false);
                await Click(A, 1_250, token).ConfigureAwait(false);
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
                for (int i = 0; i < 6; i++)
                    await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
                await Click(A, 0_750, token).ConfigureAwait(false);
            }

            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
            if (!gameClosed)
                await Click(HOME, 2_000, token).ConfigureAwait(false); // Back to game
        }

        private async Task CheckDen(CancellationToken token)
        {
            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
                await Task.Delay(0_500).ConfigureAwait(false);

            var denData = await Connection.ReadBytesAsync(denOfs, 0x18, token).ConfigureAwait(false);
            RaidInfo.Den = new RaidSpawnDetail(denData, 0);

            // Save some unnecessary reads.
            if (RaidInfo.Den.IsEvent)
                RaidInfo.RaidDistributionEncounter = DenUtil.GetSpawnEventShort(RaidInfo);
            else RaidInfo.RaidEncounter = DenUtil.GetSpawnShort(RaidInfo);

            Species species = RaidInfo.Den.IsEvent ? (Species)RaidInfo.RaidDistributionEncounter.Species : (Species)RaidInfo.RaidEncounter.Species;
            bool gmax = RaidInfo.Den.IsEvent ? RaidInfo.RaidDistributionEncounter.IsGigantamax : RaidInfo.RaidEncounter.IsGigantamax;

            TradeCordHelperUtil<PK8>.FormOutput((int)species, 0, out string[] forms);
            var list = forms.ToList();
            int index = list.IndexOf(list.Find(x => x.ToLower() == Settings.FormLock.ToLower()));
            bool formLock = Settings.FormLock == string.Empty || ((RaidInfo.Den.IsEvent ? (int)RaidInfo.RaidDistributionEncounter.AltForm : (int)RaidInfo.RaidEncounter.AltForm) == index);
            softLock = Settings.GmaxLock == gmax && species == Settings.SoftLockSpecies && formLock && Config.Connection.Protocol == SwitchProtocol.USB && Settings.AirplaneQuitout && Settings.HardLockSpecies == Species.None;
            hardLock = Settings.GmaxLock == gmax && species == Settings.HardLockSpecies && formLock && Settings.SoftLockSpecies == Species.None;

            raidBossSpecies = RaidInfo.Den.IsEvent ? (int)RaidInfo.RaidDistributionEncounter.Species : (int)RaidInfo.RaidEncounter.Species;
            if (softLock || hardLock)
                EchoUtil.Echo($"{(softLock ? "Soft" : "Hard")} locking on {(gmax ? species + "-Gmax" : species)}.");
            else EchoUtil.Echo($"Rolling complete. Raid for {(gmax ? species + "-Gmax" : species)} will be going up shortly!");

            if (hardLock)
                await SaveGame(Hub.Config, token).ConfigureAwait(false);
        }

        private async Task ReadDenData(CancellationToken token)
        {
            denOfs = DenUtil.GetDenOffset(Settings.DenID, Settings.DenType, out uint denID);
            RaidInfo.DenID = denID;

            var denData = await Connection.ReadBytesAsync(denOfs, 0x18, token).ConfigureAwait(false);
            RaidInfo.Den = new RaidSpawnDetail(denData, 0);

            if (RaidInfo.Den.IsEvent)
            {
                var eventOfs = DenUtil.GetEventDenOffset((int)Hub.Config.ConsoleLanguage, RaidInfo.DenID, Settings.DenType, out _);
                var eventData = await Connection.ReadBytesAsync(eventOfs, 0x23D4, token).ConfigureAwait(false);

                RaidInfo.RaidDistributionEncounter = DenUtil.GetSpawnEvent(RaidInfo, eventData, out FlatbuffersResource.NestHoleDistributionEncounter8Table table);
                RaidInfo.RaidDistributionEncounterTable = table;
            }
            else
            {
                RaidInfo.RaidEncounter = DenUtil.GetSpawn(RaidInfo, out FlatbuffersResource.EncounterNest8Table table);
                RaidInfo.RaidEncounterTable = table;
            }
        }
    }
}