﻿using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsets;

namespace SysBot.Pokemon
{
    public class PokeTradeBot : PokeRoutineExecutor
    {
        public static ISeedSearchHandler<PK8> SeedChecker = new NoSeedSearchHandler<PK8>();
        private readonly PokeTradeHub<PK8> Hub;

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }

        public PokeTradeBot(PokeTradeHub<PK8> hub, PokeBotConfig cfg) : base(cfg)
        {
            Hub = hub;
            DumpSetting = hub.Config.Folder;
        }

        private const int InjectBox = 0;
        private const int InjectSlot = 0;

        protected override async Task MainLoop(CancellationToken token)
        {
            Log("Identifying trainer data of the host console.");
            var sav = await IdentifyTrainer(token).ConfigureAwait(false);

            Log("Starting main TradeBot loop.");
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    PokeRoutineType.SurpriseTrade => DoSurpriseTrades(sav, token),
                    _ => DoTrades(sav, token),
                };
                await task.ConfigureAwait(false);
            }
            Hub.Bots.Remove(this);
        }

        private async Task DoNothing(CancellationToken token)
        {
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            {
                if (waitCounter == 0)
                    Log("No task assigned. Waiting for new task assignment.");
                    
                waitCounter++;
                if (waitCounter % 10 == 0 && Hub.Config.AntiIdle)
                    await Click(B, 1_000, token).ConfigureAwait(false);
                else
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task DoTrades(SAV8SWSH sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                if (!Hub.Queues.TryDequeue(type, out var detail, out var priority) && !Hub.Queues.TryDequeueLedy(out detail))
                {
                    if (waitCounter == 0)
                    {
                        // Updates the assets.
                        Hub.Config.Stream.IdleAssets(this);
                        Log("Nothing to check, waiting for new users...");
                    }
                    waitCounter++;
                    if (waitCounter % 10 == 0 && Hub.Config.AntiIdle)
                        await Click(B, 1_000, token).ConfigureAwait(false);
                    else
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                if (type != PokeRoutineType.LanTrade)
                    if (type != PokeRoutineType.LanRoll)
                        await EnsureConnectedToYComm(Hub.Config, token).ConfigureAwait(false);

                if (Hub.Config.LanTrade.BootLanBeforeEachTrade && (type == PokeRoutineType.LanTrade || type == PokeRoutineType.LanRoll))
                {
                    await Task.Delay(2_000, token).ConfigureAwait(false);
                    Log("Rebooting into LAN Mode Just in Case We Got Disconnected");
                    await Click(X, 2_000, token).ConfigureAwait(false);
                    await Click(A, 4_000, token).ConfigureAwait(false);

                    await BootLanMode(4_000, token).ConfigureAwait(false);

                    // Give time to connect
                    await Task.Delay(8_000, token).ConfigureAwait(false);

                    await Click(A, 1_000, token).ConfigureAwait(false); // Click out of the Boot prompt
                    await Click(B, 1_000, token).ConfigureAwait(false);
                    await Click(B, 1_000, token).ConfigureAwait(false);
                    await Click(B, 1_000, token).ConfigureAwait(false);
                }

                var result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result != PokeTradeResult.Success) // requeue
                {
                    if (result.AttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
                    {
                        detail.IsRetry = true;
                        detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
                        Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradeQueue<PK8>.Tier2));
                    }
                    else
                    {
                        detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                        detail.TradeCanceled(this, result);
                    }
                }
            }

            UpdateBarrier(false);
        }

        private async Task DoSurpriseTrades(SAV8SWSH sav, CancellationToken token)
        {
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.SurpriseTrade)
            {
                var pkm = Hub.Ledy.Pool.GetRandomSurprise();
                await EnsureConnectedToYComm(Hub.Config, token).ConfigureAwait(false);
                var _ = await PerformSurpriseTrade(sav, pkm, token).ConfigureAwait(false);
            }
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV8SWSH sav, PokeTradeDetail<PK8> poke, CancellationToken token)
        {
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            Hub.Config.Stream.EndEnterCode(this);

            if (await CheckIfSoftBanned(token).ConfigureAwait(false))
                await Unban(token).ConfigureAwait(false);

            var pkm = poke.TradeData;

            if (pkm.Species != 0)
            {
                if (CheckForAdOT(pkm) && !Hub.Config.Legality.AllowAds)
                    pkm.OT_Name = $"{Hub.Config.Legality.GenerateOT}";

                if (CheckForAdNickname(pkm) && !Hub.Config.Legality.AllowAds)
                    pkm.Nickname = pkm.ClearNickname();

                await SetBoxPokemon(pkm, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);
            }

            if (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            while (await CheckIfSearchingForLinkTradePartner(token).ConfigureAwait(false))
            {
                Log("Still searching, reset bot position.");
                await ResetTradePosition(Hub.Config, token).ConfigureAwait(false);
            }

            Log("Opening Y-Comm Menu");
            await Click(Y, 2_000, token).ConfigureAwait(false);

            Log("Selecting Link Trade");
            await Click(A, 1_500, token).ConfigureAwait(false);

            Log("Selecting Link Trade Code");
            await Click(DDOWN, 500, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(A, 1_500, token).ConfigureAwait(false);

            // All other languages require an extra A press at this menu.
            if (GameLang != LanguageID.English && GameLang != LanguageID.Spanish)
                await Click(A, 1_500, token).ConfigureAwait(false);

            // Loading Screen
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (poke.Type != PokeTradeType.Random)
                Hub.Config.Stream.StartEnterCode(this);
            await Task.Delay(1_000, token).ConfigureAwait(false);

            var code = poke.Code;
            Log($"Entering Link Trade Code: {code:0000 0000}...");
            await EnterTradeCode(code, Hub.Config, token).ConfigureAwait(false);

            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);
            await Click(PLUS, 1_000, token).ConfigureAwait(false);

            Hub.Config.Stream.EndEnterCode(this);

            // Confirming and return to overworld.
            var delay_count = 0;
            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
            {
                if (delay_count >= 5)
                {
                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.RecoverPostLinkCode;
                }

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_800, token).ConfigureAwait(false);
                delay_count++;
            }

            poke.TradeSearching(this);
            await Task.Delay(0_500, token).ConfigureAwait(false);

            // Wait for a Trainer...
            Log("Waiting for trainer...");
            bool partnerFound = await WaitForPokemonChanged(LinkTradePartnerPokemonOffset, Hub.Config.Trade.TradeWaitTime * 1_000, 0_200, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;
            if (!partnerFound)
            {
                await ResetTradePosition(Hub.Config, token).ConfigureAwait(false);
                return PokeTradeResult.NoTrainerFound;
            }

            // Select Pokemon
            // pkm already injected to b1s1
            await Task.Delay(5_500, token).ConfigureAwait(false); // necessary delay to get to the box properly

            var TrainerName = await GetTradePartnerName(TradeMethod.LinkTrade, token).ConfigureAwait(false);
            Log($"Found Trading Partner: {TrainerName}...");

            if (poke.RequestedIgn != string.Empty && TrainerName != poke.RequestedIgn && (poke.Type == PokeTradeType.LanTrade || poke.Type == PokeTradeType.LanRoll))
            {
                poke.SendNotification(this, $"Found Trading Partner: {TrainerName}. This does not match your Requested IGN ({poke.RequestedIgn}).");
                Log("IGN Requested does not match Trading Partner.");
                await ExitTrade(Hub.Config, false, token).ConfigureAwait(false);
                return PokeTradeResult.IncorrectIGN;
            }

            if (GetAltAccount(poke, TrainerName) != "")
                Log($"<@{Hub.Config.Discord.PingUserOnAltDetection}> Potential Alt Detected! I have matched an IGN with 2 different Discord accounts. IGN: {TrainerName} | New User ID: {poke.DiscordUserId} | Old User ID: {GetAltAccount(poke, TrainerName)}");

            if (!await IsInBox(token).ConfigureAwait(false))
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverOpenBox;
            }
            
            // Confirm Box 1 Slot 1
            if (poke.Type == PokeTradeType.Specific || poke.Type == PokeTradeType.EggRoll || poke.Type == PokeTradeType.LanTrade || poke.Type == PokeTradeType.LanRoll)
            {
                for (int i = 0; i < 5; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }

            poke.SendNotification(this, $"Found Trading Partner: {TrainerName}. Waiting for a Pokémon...");

            if (poke.Type == PokeTradeType.Dump)
                return await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);

            // Wait for User Input...
            var pk = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 25_000, 1_000, token).ConfigureAwait(false);
            var oldEC = await Connection.ReadBytesAsync(LinkTradePartnerPokemonOffset, 4, token).ConfigureAwait(false);
            if (pk == null)
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerTooSlow;
            }

            if (poke.Type == PokeTradeType.Seed)
            {
                // Immediately exit, we aren't trading anything.
                return await EndSeedCheckTradeAsync(poke, pk, token).ConfigureAwait(false);
            }

            if (poke.Type == PokeTradeType.Random) // distribution
            {
                // Allow the trade partner to do a Ledy swap.
                var trade = Hub.Ledy.GetLedyTrade(pk, Hub.Config.Distribution.LedySpecies);
                if (trade != null)
                {
                    pkm = trade.Receive;
                    poke.TradeData = pkm;

                    poke.SendNotification(this, "Injecting the requested Pokémon.");
                    await Click(A, 0_800, token).ConfigureAwait(false);
                    await SetBoxPokemon(pkm, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);
                    await Task.Delay(2_500, token).ConfigureAwait(false);
                }

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }
            else if (poke.Type == PokeTradeType.FixOT)
            {
                var clone = (PK8)pk.Clone();

                if ((CheckForAdOT(clone) || CheckForAdNickname(clone)) && clone.OT_Name != $"{TrainerName}")
                {
                    clone.OT_Name = $"{TrainerName}";
                    clone.ClearNickname();
                    clone.PKRS_Infected = false;
                    clone.PKRS_Cured = false;
                    clone.PKRS_Days = 0;
                    clone.PKRS_Strain = 0;
                    poke.SendNotification(this, $"```fix\nDetected an advertisement OT/Nickname with your {(Species)clone.Species}!```");
                }
                else
                {
                    poke.SendNotification(this, "```fix\nNo advertisement detected in Nickname or OT. Exiting trade...```");
                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.IllegalTrade;
                }

                var la = new LegalityAnalysis(clone);
                if (!la.Valid)
                {
                    Log($"FixOT request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {(Species)clone.Species}");
                    if (DumpSetting.Dump)
                        DumpPokemon(DumpSetting.DumpFolder, "hacked", clone);

                    var report = la.Report();
                    Log(report);
                    poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from fixing this. Exiting trade.");
                    poke.SendNotification(this, report);

                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.IllegalTrade;
                }

                if (Hub.Config.Legality.ResetHOMETracker)
                    clone.Tracker = 0;

                poke.SendNotification(this, $"```fix\nFixed your {(Species)clone.Species}! Now confirm the trade!```");
                Log($"Fixed Nickname/OT for {(Species)clone.Species}.");

                await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 1_000, token).ConfigureAwait(false);
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(clone, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);
                pkm = clone;

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }
            else if (poke.Type == PokeTradeType.PowerUp)
            {
                var clone = (PK8)pk.Clone();

                clone.MaximizeLevel();
                clone.SetRecordFlags();
                clone.SetMaximumPPUps();
                if (poke.TradeData.EVTotal != 0) // If no EVs specified in command (=0), make them the original EVs.
                    clone.EVs = poke.TradeData.EVs;
                clone.SetSuggestedHyperTrainingData(clone.IVs);
                if (clone.CanToggleGigantamax(clone.Species, clone.SpecForm))
                    clone.CanGigantamax = true;
                if (clone is IDynamaxLevel d)
                    d.DynamaxLevel = (byte)(d.CanHaveDynamaxLevel(clone) ? 10 : 0);

                var la = new LegalityAnalysis(clone);
                if (!la.Valid)
                {
                    Log($"PowerUp request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {(Species)clone.Species}");
                    if (DumpSetting.Dump)
                        DumpPokemon(DumpSetting.DumpFolder, "hacked", clone);

                    var report = la.Report();
                    Log(report);
                    poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from modifying this. Exiting trade.");
                    poke.SendNotification(this, report);

                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.IllegalTrade;
                }

                if (Hub.Config.Legality.ResetHOMETracker)
                    clone.Tracker = 0;

                poke.SendNotification(this, $"```pu\nPowered up your {(Species)clone.Species}! Now confirm the trade!```");
                Log($"Powered up {(Species)clone.Species}.");

                await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 1_000, token).ConfigureAwait(false);
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(clone, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);
                pkm = clone;

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }
            else if (poke.Type == PokeTradeType.Clone)
            {
                // Inject the shown Pokémon.
                var clone = (PK8)pk.Clone();

                if (Hub.Config.Discord.ReturnPK8s)
                    poke.SendNotification(this, clone, "Here's what you showed me!");

                var la = new LegalityAnalysis(clone);
                if (!la.Valid)
                {
                    Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {(Species)clone.Species}");
                    if (DumpSetting.Dump)
                        DumpPokemon(DumpSetting.DumpFolder, "hacked", clone);

                    var report = la.Report();
                    Log(report);
                    poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                    poke.SendNotification(this, report);

                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.IllegalTrade;
                }

                if (Hub.Config.Legality.ResetHOMETracker)
                    clone.Tracker = 0;

                poke.SendNotification(this, $"**Cloned your {(Species)clone.Species}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
                Log($"Cloned a {(Species)clone.Species}. Waiting for user to change their Pokémon...");

                // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
                partnerFound = await ReadUntilChanged(LinkTradePartnerPokemonOffset, oldEC, 15_000, 0_200, false, token).ConfigureAwait(false);

                if (!partnerFound)
                {
                    poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
                    // They get one more chance.
                    partnerFound = await ReadUntilChanged(LinkTradePartnerPokemonOffset, oldEC, 15_000, 0_200, false, token).ConfigureAwait(false);
                }

                var pk2 = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 1_000, token).ConfigureAwait(false);
                if (!partnerFound || pk2 == null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(pk))
                {
                    Log("Trading partner did not change their Pokémon.");
                    await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(clone, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);
                pkm = clone;

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }

            await Click(A, 3_000, token).ConfigureAwait(false);
            for (int i = 0; i < 5; i++)
                await Click(A, 1_500, token).ConfigureAwait(false);

            Log("In Trade Animation...");

            delay_count = 0;
            while (!await IsInBox(token).ConfigureAwait(false))
            {
                await Click(A, 3_000, token).ConfigureAwait(false);
                delay_count++;
                if (delay_count >= 50)
                    break;
                if (await IsOnOverworld(Hub.Config, token).ConfigureAwait(false)) // In case we are in a Trade Evolution/PokeDex Entry and the Trade Partner quits we land on the Overworld
                    break;
            }

            await Task.Delay(1_000 + Util.Rand.Next(0_700, 1_000), token).ConfigureAwait(false);

            await ExitTrade(Hub.Config, false, token).ConfigureAwait(false);
            Log("Exited Trade!");

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            // Trade was Successful!
            var traded = await ReadBoxPokemon(InjectBox, InjectSlot, token).ConfigureAwait(false);
            // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
            if (poke.Type != PokeTradeType.FixOT && poke.Type != PokeTradeType.PowerUp && SearchUtil.HashByDetails(traded) == SearchUtil.HashByDetails(pkm))
            {
                Log("User did not complete the trade.");
                return PokeTradeResult.TrainerTooSlow;
            }
            else
            {
                // As long as we got rid of our inject in b1s1, assume the trade went through.
                Log("User completed the trade.");
                poke.TradeFinished(this, traded);

                // Only log if we completed the trade.
                var counts = Hub.Counts;
                if (poke.Type == PokeTradeType.Random)
                    counts.AddCompletedDistribution();
                else if (poke.Type == PokeTradeType.Clone)
                    counts.AddCompletedClones();
                else if (poke.Type == PokeTradeType.FixOT)
                    counts.AddCompletedFixOTs();
                else if (poke.Type == PokeTradeType.PowerUp)
                    counts.AddCompletedPowerUps();
                else if (poke.Type == PokeTradeType.EggRoll)
                    counts.AddCompletedEggRolls();
                else if (poke.Type == PokeTradeType.LanTrade)
                    counts.AddCompletedLanTrades();
                else if (poke.Type == PokeTradeType.LanRoll)
                    counts.AddCompletedLanRolls();
                else
                    Hub.Counts.AddCompletedTrade();

                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                {
                    var subfolder = poke.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, traded); // received
                    if (poke.Type == PokeTradeType.Specific || poke.Type == PokeTradeType.Clone || poke.Type == PokeTradeType.FixOT || poke.Type == PokeTradeType.PowerUp || poke.Type == PokeTradeType.EggRoll || poke.Type == PokeTradeType.LanRoll)
                        DumpPokemon(DumpSetting.DumpFolder, "traded", pkm); // sent to partner
                }
            }

            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK8> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;
            var pkprev = new PK8();
            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                var pk = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 1_000, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = la.Report(true);
                Log($"Shown Pokémon is {(la.Valid ? "Valid" : "Invalid")}.");

                detail.SendNotification(this, pk, verbose);
                ctr++;
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon");
            await ExitSeedCheckTrade(Hub.Config, token).ConfigureAwait(false);
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            Hub.Counts.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank pk8
            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> PerformSurpriseTrade(SAV8SWSH sav, PK8 pkm, CancellationToken token)
        {
            // General Bot Strategy:
            // 1. Inject to b1s1
            // 2. Send out Trade
            // 3. Clear received PKM to skip the trade animation
            // 4. Repeat

            // Inject to b1s1
            if (await CheckIfSoftBanned(token).ConfigureAwait(false))
                await Unban(token).ConfigureAwait(false);

            Log("Starting next Surprise Trade. Getting data...");
            await SetBoxPokemon(pkm, InjectBox, InjectSlot, token, sav).ConfigureAwait(false);

            if (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            if (await CheckIfSearchingForSurprisePartner(token).ConfigureAwait(false))
            {
                Log("Still searching, reset.");
                await ResetTradePosition(Hub.Config, token).ConfigureAwait(false);
            }

            Log("Opening Y-Comm Menu");
            await Click(Y, 1_500, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            Log("Selecting Surprise Trade");
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
            await Click(A, 2_000, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            await Task.Delay(0_750, token).ConfigureAwait(false);

            if (!await IsInBox(token).ConfigureAwait(false))
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverPostLinkCode;
            }

            Log("Selecting Pokémon");
            // Box 1 Slot 1; no movement required.
            await Click(A, 0_700, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            Log("Confirming...");
            while (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
                await Click(A, 0_800, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            // Let Surprise Trade be sent out before checking if we're back to the Overworld.
            await Task.Delay(3_000, token).ConfigureAwait(false);

            if (!await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
            {
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverReturnOverworld;
            }

            // Wait 30 Seconds for Trainer...
            Log("Waiting for Surprise Trade Partner...");

            // Wait for an offer...
            var oldEC = await Connection.ReadBytesAsync(SurpriseTradeSearchOffset, 4, token).ConfigureAwait(false);
            var partnerFound = await ReadUntilChanged(SurpriseTradeSearchOffset, oldEC, Hub.Config.Trade.TradeWaitTime * 1_000, 0_200, false, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            if (!partnerFound)
            {
                await ResetTradePosition(Hub.Config, token).ConfigureAwait(false);
                return PokeTradeResult.NoTrainerFound;
            }

            // Let the game flush the results and de-register from the online surprise trade queue.
            await Task.Delay(7_000, token).ConfigureAwait(false);

            var TrainerName = await GetTradePartnerName(TradeMethod.SupriseTrade, token).ConfigureAwait(false);
            var SurprisePoke = await ReadSurpriseTradePokemon(token).ConfigureAwait(false);

            Log($"Found Surprise Trade Partner: {TrainerName}, Pokémon: {(Species)SurprisePoke.Species}");

            // Clear out the received trade data; we want to skip the trade animation.
            // The box slot locks have been removed prior to searching.

            await Connection.WriteBytesAsync(BitConverter.GetBytes(SurpriseTradeSearch_Empty), SurpriseTradeSearchOffset, token).ConfigureAwait(false);
            await Connection.WriteBytesAsync(PokeTradeBotUtil.EMPTY_SLOT, SurpriseTradePartnerPokemonOffset, token).ConfigureAwait(false);

            // Let the game recognize our modifications before finishing this loop.
            await Task.Delay(5_000, token).ConfigureAwait(false);

            // Clear the Surprise Trade slot locks! We'll skip the trade animation and reuse the slot on later loops.
            // Write 8 bytes of FF to set both Int32's to -1. Regular locks are [Box32][Slot32]

            await Connection.WriteBytesAsync(BitConverter.GetBytes(ulong.MaxValue), SurpriseTradeLockBox, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.Aborted;

            if (await IsOnOverworld(Hub.Config, token).ConfigureAwait(false))
                Log("Trade complete!");
            else
                await ExitTrade(Hub.Config, true, token).ConfigureAwait(false);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "surprise", SurprisePoke);
            Hub.Counts.AddCompletedSurprise();

            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> EndSeedCheckTradeAsync(PokeTradeDetail<PK8> detail, PK8 pk, CancellationToken token)
        {
            await ExitSeedCheckTrade(Hub.Config, token).ConfigureAwait(false);

            detail.TradeFinished(this, pk);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "seed", pk);

            // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
#pragma warning disable 4014
            Task.Run(() =>
            {
                try
                {
                    ReplyWithSeedCheckResults(detail, pk);
                }
                catch (Exception ex)
                {
                    detail.SendNotification(this, $"Unable to calculate seeds: {ex.Message}\r\n{ex.StackTrace}");
                }
            }, token);
#pragma warning restore 4014

            Hub.Counts.AddCompletedSeedCheck();

            return PokeTradeResult.Success;
        }

        private void ReplyWithSeedCheckResults(PokeTradeDetail<PK8> detail, PK8 result)
        {
            detail.SendNotification(this, "Calculating your seed(s)...");

            if (result.IsShiny)
            {
                Log("The Pokémon is already shiny!"); // Do not bother checking for next shiny frame
                detail.SendNotification(this, "This Pokémon is already shiny! Raid seed calculation was not done.");

                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                    DumpPokemon(DumpSetting.DumpFolder, "seed", result);

                detail.TradeFinished(this, result);
                return;
            }

            SeedChecker.CalculateAndNotify(result, detail, Hub.Config.SeedCheck, this);
            Log("Seed calculation completed.");
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        private bool CheckForAdOT(PK8 pkm) => System.Text.RegularExpressions.Regex.Match(pkm.OT_Name, @"(YT$)|(YT\w*$)|(Lab$)|(\.\w*)|(TV$)|(PKHeX)|(FB:)|(SysBot)|(AuSLove)").Value != "";

        private bool CheckForAdNickname(PK8 pkm) => System.Text.RegularExpressions.Regex.Match(pkm.Nickname, @"(YT$)|(YT\w*$)|(Lab$)|(\.\w*)|(TV$)|(PKHeX)|(FB:)|(SysBot)|(AuSLove)").Value != "";

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

        private async Task<bool> WaitForPokemonChanged(uint offset, int waitms, int waitInterval, CancellationToken token)
        {
            var oldEC = await Connection.ReadBytesAsync(offset, 4, token).ConfigureAwait(false);
            return await ReadUntilChanged(offset, oldEC, waitms, waitInterval, false, token).ConfigureAwait(false);
        }

        private string GetAltAccount(PokeTradeDetail<PK8> poke, string TrainerName)
        {
            if (Hub.Config.Discord.PingUserOnAltDetection == string.Empty)
                return "";

            string invalid = new string(System.IO.Path.GetInvalidFileNameChars()) + new string(System.IO.Path.GetInvalidPathChars());

            foreach (char c in invalid)
            {
                TrainerName = TrainerName.Replace(c.ToString(), "");
            }

            string folderPath = @"AltDetection\";
            string filePath = @"AltDetection\" + TrainerName + ".txt";

            if (!System.IO.Directory.Exists(folderPath))
                System.IO.Directory.CreateDirectory(folderPath);

            if (!System.IO.File.Exists(filePath))
                System.IO.File.Create(filePath).Close();

            List<string> content = System.IO.File.ReadAllLines(filePath).ToList();

            var id = poke.DiscordUserId;

            if (content.Count == 0)
            {
                content.Add($"{id}");
            }
            else if(content[0] != id.ToString())
            {
                return $"{content[0]}";
            }

            System.IO.File.WriteAllLines(filePath, content);

            return "";
        }
    }
}
