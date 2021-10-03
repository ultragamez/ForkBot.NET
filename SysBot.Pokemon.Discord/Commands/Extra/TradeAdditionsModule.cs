using PKHeX.Core;
using Discord;
using Discord.Commands;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    [Summary("Generates and queues various silly trade additions")]
    public class TradeAdditionsModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        private static TradeQueueInfo<T> Info => SysCord<T>.Runner.Hub.Queues.Info;
        private readonly PokeTradeHub<T> Hub = SysCord<T>.Runner.Hub;
        private readonly ExtraCommandUtil<T> Util = new();

        [Command("giveawayqueue")]
        [Alias("gaq")]
        [Summary("Prints the users in the giveway queues.")]
        [RequireSudo]
        public async Task GetGiveawayListAsync()
        {
            string msg = Info.GetTradeList(PokeRoutineType.LinkTrade);
            var embed = new EmbedBuilder();
            embed.AddField(x =>
            {
                x.Name = "Pending Giveaways";
                x.Value = msg;
                x.IsInline = false;
            });
            await ReplyAsync("These are the users who are currently waiting:", embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("giveawaypool")]
        [Alias("gap")]
        [Summary("Show a list of Pokémon available for giveaway.")]
        [RequireQueueRole(nameof(DiscordManager.RolesGiveaway))]
        public async Task DisplayGiveawayPoolCountAsync()
        {
            var pool = Info.Hub.Ledy.Pool;
            if (pool.Count > 0)
            {
                var test = pool.Files;
                var lines = pool.Files.Select((z, i) => $"{i + 1}: {z.Key} = {(Species)z.Value.RequestInfo.Species}");
                var msg = string.Join("\n", lines);
                await Util.ListUtil(Context, "Giveaway Pool Details", msg).ConfigureAwait(false);
            }
            else await ReplyAsync("Giveaway pool is empty.").ConfigureAwait(false);
        }

        [Command("giveaway")]
        [Alias("ga", "giveme", "gimme")]
        [Summary("Makes the bot trade you the specified giveaway Pokémon.")]
        [RequireQueueRole(nameof(DiscordManager.RolesGiveaway))]
        public async Task GiveawayAsync([Remainder] string content)
        {
            var code = Info.GetRandomTradeCode();
            await GiveawayAsync(code, content).ConfigureAwait(false);
        }

        [Command("giveaway")]
        [Alias("ga", "giveme", "gimme")]
        [Summary("Makes the bot trade you the specified giveaway Pokémon.")]
        [RequireQueueRole(nameof(DiscordManager.RolesGiveaway))]
        public async Task GiveawayAsync([Summary("Giveaway Code")] int code, [Remainder] string content)
        {
            T pk;
            content = ReusableActions.StripCodeBlock(content);
            var pool = Info.Hub.Ledy.Pool;
            if (pool.Count == 0)
            {
                await ReplyAsync("Giveaway pool is empty.").ConfigureAwait(false);
                return;
            }
            else if (content.ToLower() == "random") // Request a random giveaway prize.
                pk = Info.Hub.Ledy.Pool.GetRandomSurprise();
            else if (Info.Hub.Ledy.Distribution.TryGetValue(content, out LedyRequest<T> val))
                pk = val.RequestInfo;
            else
            {
                await ReplyAsync($"Requested Pokémon not available, use \"{Info.Hub.Config.Discord.CommandPrefix}giveawaypool\" for a full list of available giveaways!").ConfigureAwait(false);
                return;
            }

            var sig = Context.User.GetFavor();
            await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, pk, PokeRoutineType.LinkTrade, PokeTradeType.Specific).ConfigureAwait(false);
        }

        [Command("fixOT")]
        [Alias("fix", "f")]
        [Summary("Fixes OT and Nickname of a Pokémon you show via Link Trade if an advert is detected.")]
        [RequireQueueRole(nameof(DiscordManager.RolesFixOT))]
        public async Task FixAdOT()
        {
            var code = Info.GetRandomTradeCode();
            var sig = Context.User.GetFavor();
            await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, new T(), PokeRoutineType.FixOT, PokeTradeType.FixOT).ConfigureAwait(false);
        }

        [Command("fixOT")]
        [Alias("fix", "f")]
        [Summary("Fixes OT and Nickname of a Pokémon you show via Link Trade if an advert is detected.")]
        [RequireQueueRole(nameof(DiscordManager.RolesFixOT))]
        public async Task FixAdOT([Summary("Trade Code")] int code)
        {
            var sig = Context.User.GetFavor();
            await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, new T(), PokeRoutineType.FixOT, PokeTradeType.FixOT).ConfigureAwait(false);
        }

        [Command("fixOTList")]
        [Alias("fl", "fq")]
        [Summary("Prints the users in the FixOT queue.")]
        [RequireSudo]
        public async Task GetFixListAsync()
        {
            string msg = Info.GetTradeList(PokeRoutineType.FixOT);
            var embed = new EmbedBuilder();
            embed.AddField(x =>
            {
                x.Name = "Pending Trades";
                x.Value = msg;
                x.IsInline = false;
            });
            await ReplyAsync("These are the users who are currently waiting:", embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("itemTrade")]
        [Alias("it", "item")]
        [Summary("Makes the bot trade you a Pokémon holding the requested item, or Ditto if stat spread keyword is provided.")]
        [RequireQueueRole(nameof(DiscordManager.RolesSupportTrade))]
        public async Task ItemTrade([Remainder] string item)
        {
            var code = Info.GetRandomTradeCode();
            await ItemTrade(code, item).ConfigureAwait(false);
        }

        [Command("itemTrade")]
        [Alias("it", "item")]
        [Summary("Makes the bot trade you a Pokémon holding the requested item.")]
        [RequireQueueRole(nameof(DiscordManager.RolesSupportTrade))]
        public async Task ItemTrade([Summary("Trade Code")] int code, [Remainder] string item)
        {
            Species species = Info.Hub.Config.Trade.ItemTradeSpecies == Species.None ? Species.Delibird : Info.Hub.Config.Trade.ItemTradeSpecies;
            var set = new ShowdownSet($"{SpeciesName.GetSpeciesNameGeneration((int)species, 2, 8)} @ {item.Trim()}");
            var template = AutoLegalityWrapper.GetTemplate(set);
            var sav = AutoLegalityWrapper.GetTrainerInfo(8);
            var pkm = sav.GetLegal(template, out var result);
            pkm = PKMConverter.ConvertToType(pkm, typeof(PK8), out _) ?? pkm;
            if (pkm.HeldItem == 0 && !Info.Hub.Config.Trade.Memes)
            {
                await ReplyAsync($"{Context.User.Username}, the item you entered wasn't recognized.").ConfigureAwait(false);
                return;
            }

            var la = new LegalityAnalysis(pkm);
            if (Info.Hub.Config.Trade.Memes && await TrollAsync(Context, pkm is not PK8 || !la.Valid, template, true).ConfigureAwait(false))
                return;
            else if (pkm is not PK8 || !la.Valid)
            {
                var reason = result == "Timeout" ? "That set took too long to generate." : "I wasn't able to create something from that.";
                var imsg = $"Oops! {reason} Here's my best attempt for that {species}!";
                await Context.Channel.SendPKMAsync(pkm, imsg).ConfigureAwait(false);
                return;
            }

            pkm.ResetPartyStats();
            var sig = Context.User.GetFavor();
            await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, (T)pkm, PokeRoutineType.LinkTrade, PokeTradeType.SupportTrade).ConfigureAwait(false);
        }

        [Command("dittoTrade")]
        [Alias("dt", "ditto")]
        [Summary("Makes the bot trade you a Ditto with a requested stat spread and language.")]
        [RequireQueueRole(nameof(DiscordManager.RolesSupportTrade))]
        public async Task DittoTrade([Summary("A combination of \"ATK/SPA/SPE\" or \"6IV\"")] string keyword, [Summary("Language")] string language, [Summary("Nature")] string nature)
        {
            var code = Info.GetRandomTradeCode();
            await DittoTrade(code, keyword, language, nature).ConfigureAwait(false);
        }

        [Command("dittoTrade")]
        [Alias("dt", "ditto")]
        [Summary("Makes the bot trade you a Ditto with a requested stat spread and language.")]
        [RequireQueueRole(nameof(DiscordManager.RolesSupportTrade))]
        public async Task DittoTrade([Summary("Trade Code")] int code, [Summary("A combination of \"ATK/SPA/SPE\" or \"6IV\"")] string keyword, [Summary("Language")] string language, [Summary("Nature")] string nature)
        {
            keyword = keyword.ToLower().Trim();
            language = language.Trim().Substring(0, 1).ToUpper() + language.Trim()[1..].ToLower();
            nature = nature.Trim().Substring(0, 1).ToUpper() + nature.Trim()[1..].ToLower();
            var set = new ShowdownSet($"{keyword}(Ditto)\nLanguage: {language}\nNature: {nature}");
            var template = AutoLegalityWrapper.GetTemplate(set);
            var sav = AutoLegalityWrapper.GetTrainerInfo(8);
            var pkm = sav.GetLegal(template, out var result);
            pkm = PKMConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;
            TradeExtensions.DittoTrade((T)pkm);

            var la = new LegalityAnalysis(pkm);
            if (Info.Hub.Config.Trade.Memes && await TrollAsync(Context, pkm is not PK8 || !la.Valid, template).ConfigureAwait(false))
                return;
            else if (pkm is not PK8 || !la.Valid)
            {
                var reason = result == "Timeout" ? "That set took too long to generate." : "I wasn't able to create something from that.";
                var imsg = $"Oops! {reason} Here's my best attempt for that Ditto!";
                await Context.Channel.SendPKMAsync(pkm, imsg).ConfigureAwait(false);
                return;
            }

            pkm.ResetPartyStats();
            var sig = Context.User.GetFavor();
            await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, (T)pkm, PokeRoutineType.LinkTrade, PokeTradeType.SupportTrade).ConfigureAwait(false);
        }

        [Command("peek")]
        [Summary("Take and send a screenshot from the specified Switch.")]
        [RequireOwner]
        public async Task Peek(string address)
        {
            var source = new System.Threading.CancellationTokenSource();
            var token = source.Token;

            var bot = SysCord<T>.Runner.GetBot(address);
            if (bot == null)
            {
                await ReplyAsync($"No bot found with the specified address ({address}).").ConfigureAwait(false);
                return;
            }

            var c = bot.Bot.Connection;
            var bytes = await c.Screengrab(token).ConfigureAwait(false);
            if (bytes.Length == 1)
            {
                await ReplyAsync($"Failed to take a screenshot for bot at {address}. Is the bot connected?").ConfigureAwait(false);
                return;
            }
            MemoryStream ms = new(bytes);
            await Context.Channel.SendFileAsync(ms, "cap.png");
        }

        public static async Task<bool> TrollAsync(SocketCommandContext context, bool invalid, IBattleTemplate set, bool itemTrade = false)
        {
            var rng = new Random();
            bool noItem = set.HeldItem == 0 && itemTrade;
            var path = Info.Hub.Config.Trade.MemeFileNames.Split(',');
            if (Info.Hub.Config.Trade.MemeFileNames == "" || path.Length == 0)
                path = new string[] { "https://i.imgur.com/qaCwr09.png" }; //If memes enabled but none provided, use a default one.

            if (invalid || !ItemRestrictions.IsHeldItemAllowed(set.HeldItem, 8) || noItem || (set.Nickname.ToLower() == "egg" && !Breeding.CanHatchAsEgg(set.Species)))
            {
                var msg = $"{(noItem ? $"{context.User.Username}, the item you entered wasn't recognized." : $"Oops! I wasn't able to create that {GameInfo.Strings.Species[set.Species]}.")} Here's a meme instead!\n";
                await context.Channel.SendMessageAsync($"{(invalid || noItem ? msg : "")}{path[rng.Next(path.Length)]}").ConfigureAwait(false);
                return true;
            }
            return false;
        }
    }
}