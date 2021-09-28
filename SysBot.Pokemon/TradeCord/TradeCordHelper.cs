﻿using PKHeX.Core;
using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Data.SQLite;

namespace SysBot.Pokemon
{
    public class TradeCordHelper : TradeCordDatabase
    {
        private readonly TradeCordSettings Settings;
        private readonly TradeCordHelperUtil Util = new();

        public static bool TCInitialized;
        public static bool VacuumLock;
        private static readonly object _sync = new();
        public static Dictionary<ulong, int> TradeCordTrades = new();
        private static readonly Dictionary<ulong, TCUser> UserDict = new();

        public TradeCordHelper(TradeCordSettings settings) : base()
        {
            Settings = settings;
        }

        public sealed class Results
        {
            public string EmbedName { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;

            public bool Success { get; set; }
            public bool FailedCatch { get; set; }

            public int PokeID { get; set; }
            public PK8 Poke { get; set; } = new();

            public int EggPokeID { get; set; }
            public PK8 EggPoke { get; set; } = new();

            public PK8? Shedinja { get; set; }

            public TCUser User { get; set; } = new();
            public TCUser Giftee { get; set; } = new();
            public string Item { get; set; } = string.Empty;

            public List<SQLCommand> SQLCommands { get; set; } = new();
        }

        public class TC_CommandContext
        {
            public string Username { get; set; } = string.Empty;
            public ulong ID { get; set; }
            public string GifteeName { get; set; } = string.Empty;
            public ulong GifteeID { get; set; }
            public TCCommandContext Context { get; set; }
        }

        public static void CleanDB() => CleanDatabase();

        public Results ProcessTradeCord(TC_CommandContext ctx, string[] input)
        {
            if (!TCInitialized)
            {
                var current = Process.GetCurrentProcess();
                var all = Process.GetProcessesByName(current.ProcessName);
                bool sameExe = all.Count(x => x.MainModule.FileName == current.MainModule.FileName) > 1;
                if (!sameExe)
                    TCInitialized = true;
                else
                {
                    Base.LogUtil.LogText("Another TradeCord instance is already running! Killing the process.");
                    Environment.Exit(0);
                }

                if (!CreateDB())
                    throw new SQLiteException();

                if (Settings.ClearInactive)
                    ClearInactiveUsers();
            }

            while (VacuumLock)
                Thread.Sleep(100);

            lock (_sync)
            {
                try
                {
                    TCUser user = new();
                    TCUser giftee = new();
                    if (ctx.Context != TCCommandContext.DeleteUser)
                    {
                        if (UserDict.ContainsKey(ctx.ID))
                            user = UserDict[ctx.ID];
                        else
                        {
                            user = GetCompleteUser(ctx.ID, ctx.Username);
                            UserDict.Add(ctx.ID, user);
                        }

                        HandleTradedCatches(ctx.ID, false, user);
                        if (ctx.Context == TCCommandContext.Gift || ctx.Context == TCCommandContext.GiftItem)
                        {
                            if (UserDict.ContainsKey(ctx.GifteeID))
                                giftee = UserDict[ctx.GifteeID];
                            else
                            {
                                giftee = GetCompleteUser(ctx.GifteeID, ctx.GifteeName, true);
                                UserDict.Add(ctx.GifteeID, giftee);
                            }
                        }
                    }

                    var task = ctx.Context switch
                    {
                        TCCommandContext.Catch => CatchHandler(user),
                        TCCommandContext.Trade => TradeHandler(user, input[0]),
                        TCCommandContext.List => ListHandler(user, input[0]),
                        TCCommandContext.Info => InfoHandler(user, input[0]),
                        TCCommandContext.MassRelease => MassReleaseHandler(user, input[0]),
                        TCCommandContext.Release => ReleaseHandler(user, input[0]),
                        TCCommandContext.DaycareInfo => DaycareInfoHandler(user.Daycare, user.UserInfo.UserID),
                        TCCommandContext.Daycare => DaycareHandler(user, input[0], input[1]),
                        TCCommandContext.Gift => GiftHandler(user, giftee, input[0]),
                        TCCommandContext.TrainerInfoSet => TrainerInfoSetHandler(user, input),
                        TCCommandContext.TrainerInfo => TrainerInfoHandler(user.TrainerInfo, user.Items, user.UserInfo.TimeZoneOffset),
                        TCCommandContext.FavoritesInfo => FavoritesInfoHandler(user.Catches),
                        TCCommandContext.Favorites => FavoritesHandler(user, input[0]),
                        TCCommandContext.Dex => DexHandler(user.Dex, user.Perks, input[0]),
                        TCCommandContext.Perks => PerkHandler(user, input[0]),
                        TCCommandContext.Boost => SpeciesBoostHandler(user, input[0]),
                        TCCommandContext.Buddy => BuddyHandler(user, input[0]),
                        TCCommandContext.Nickname => NicknameHandler(user, input[0]),
                        TCCommandContext.Evolution => EvolutionHandler(user, input[0]),
                        TCCommandContext.GiveItem => GiveItemHandler(user, input[0]),
                        TCCommandContext.GiftItem => GiftItemHandler(user, giftee, input[0], input[1]),
                        TCCommandContext.TakeItem => TakeItemHandler(user),
                        TCCommandContext.ItemList => ItemListHandler(user, input[0]),
                        TCCommandContext.DropItem => ItemDropHandler(user, input[0]),
                        TCCommandContext.TimeZone => TimeZoneHandler(user, input[0]),
                        TCCommandContext.DeleteUser => DeleteUserData(input[0]),
                        _ => throw new NotImplementedException(),
                    };

                    var result = Task.Run(() => task).Result;
                    InfoProcessing(ctx, user, result);
                    return result;
                }
                catch (Exception ex)
                {
                    Base.LogUtil.LogError($"Something went wrong during {ctx.Context} execution for {ctx.Username}.\nMessage: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException}", "[TradeCord]");
                    return new Results()
                    {
                        EmbedName = "Oops!",
                        Message = $"Something went wrong then executing command {ctx.Context} for user {ctx.Username}!",
                    };
                }
            }
        }

        private void InfoProcessing(TC_CommandContext ctx, TCUser user, Results result)
        {
            if (result.Success && (ctx.Context == TCCommandContext.Catch || (ctx.Context == TCCommandContext.Evolution && result.Shedinja != null)))
                user = HandleNewCatches(result);

            if (result.Success && result.SQLCommands.Count > 0)
                ProcessBulkCommands(result.SQLCommands, ctx.Context == TCCommandContext.DeleteUser);

            if (result.Success && ctx.Context != TCCommandContext.DeleteUser)
                UserDict[ctx.ID] = user;

            if (ctx.Context == TCCommandContext.Gift || ctx.Context == TCCommandContext.GiftItem)
                UserDict[ctx.GifteeID] = result.Giftee;
        }

        public static void HandleTradedCatches(ulong id, bool delete, TCUser? user = null)
        {
            bool exists = TradeCordTrades.TryGetValue(id, out int catchID);
            if (exists && user == null)
            {
                user = UserDict[id];
                if (delete)
                {
                    RemoveRows(id, "catches", $"and id = {catchID}");
                    RemoveRows(id, "binary_catches", $"and id = {catchID}");
                    user.Catches.Remove(catchID);
                }
                else
                {
                    UpdateRows(id, "catches", "was_traded = 0", $"and id = {catchID}");
                    user.Catches[catchID].Traded = false;
                }
                TradeCordTrades.Remove(id);
            }
            else if (!exists && user != null)
            {
                var traded = user.Catches.ToList().FindAll(x => x.Value.Traded == true);
                int[] arr = new int[traded.Count];
                for (int i = 0; i < traded.Count; i++)
                {
                    arr[i] = traded[i].Value.ID;
                    user.Catches[traded[i].Key].Traded = false;
                }
                UpdateRows(id, "catches", "was_traded = 0", $"and id in ({string.Join(",", arr)})");
            }
        }

        private Results CatchHandler(TCUser user)
        {
            Results result = new();
            string eggMsg = string.Empty;
            string buddyMsg = string.Empty;
            bool FuncCatch()
            {
                PerkBoostApplicator(user);
                List<string> trainerInfo = new();
                trainerInfo.AddRange(new string[]
                {
                    $"OT: {user.TrainerInfo.OTName}",
                    $"OTGender: {user.TrainerInfo.OTGender}",
                    $"TID: {user.TrainerInfo.TID}",
                    $"SID: {user.TrainerInfo.SID}",
                    $"Language: {user.TrainerInfo.Language}",
                });

                var buddyAbil = user.Buddy.Ability;
                if (buddyAbil == Ability.FlameBody || buddyAbil == Ability.SteamEngine)
                    Util.Rng.EggRNG += 10;
                else if (buddyAbil == Ability.Pickup || buddyAbil == Ability.Pickpocket)
                    Util.Rng.ItemRNG += 10;

                EvoCriteria evo1 = new(0, 0), evo2 = new(0, 0);
                int ball1 = 0, ball2 = 0;
                bool egg = Util.Rng.EggRNG >= 100 - Settings.EggRate && Util.CanGenerateEgg(user.Daycare, user.UserInfo.UserID, out evo1, out evo2, out ball1, out ball2);
                if (egg)
                {
                    result.EggPoke = EggProcess(user.Daycare, evo1, evo2, ball1, ball2, string.Join("\n", trainerInfo), out eggMsg);
                    if (!new LegalityAnalysis(result.EggPoke).Valid)
                    {
                        result.Message = $"Oops, something went wrong when generating an egg!\nEgg 1: {evo1.Species}-{evo1.Form}\nEgg 2: {evo2.Species}-{evo2.Form}\nID {user.Daycare.ID1}: {user.Daycare.Species1}{user.Daycare.Form1}\nID {user.Daycare.ID2}: {user.Daycare.Species2}{user.Daycare.Form2}";
                        return false;
                    }
                    else
                    {
                        result.EggPoke.ResetPartyStats();
                        eggMsg += DexCount(user, result, result.EggPoke.Species);
                    }
                }

                DateTime.TryParse(Settings.EventEnd, out DateTime endTime);
                bool ended = endTime != default && DateTime.Now > endTime;
                bool boostProc = user.Perks.SpeciesBoost != 0 && Util.Rng.SpeciesBoostRNG >= 99;
                MysteryGift? mg = default;
                int eventForm = -1;

                if (Settings.EnableEvent && !ended)
                    Util.EventHandler(Settings, out mg, out eventForm);
                else if (boostProc)
                    Util.Rng.SpeciesRNG = user.Perks.SpeciesBoost;

                if (Util.Rng.CatchRNG >= 100 - Settings.CatchRate)
                {
                    var speciesName = SpeciesName.GetSpeciesNameGeneration(Util.Rng.SpeciesRNG, 2, 8);
                    var mgRng = mg == default ? Util.MysteryGiftRng(Settings) : mg;
                    bool melmetalHack = Util.Rng.SpeciesRNG == (int)Species.Melmetal && Util.Rng.GmaxRNG >= 100 - Settings.GmaxRate;
                    if ((Util.CherishOnly.Contains(Util.Rng.SpeciesRNG) || Util.Rng.CherishRNG >= 100 - Settings.CherishRate || mg != default || melmetalHack) && mgRng != default)
                    {
                        Enum.TryParse(user.TrainerInfo.OTGender, out Gender gender);
                        Enum.TryParse(user.TrainerInfo.Language, out LanguageID language);
                        var info = new SimpleTrainerInfo { Gender = (int)gender, Language = (int)language, OT = user.TrainerInfo.OTName, TID = user.TrainerInfo.TID, SID = user.TrainerInfo.SID };
                        result.Poke = TradeExtensions.CherishHandler(mgRng, info);
                    }

                    if (result.Poke.Species == 0)
                        result.Poke = Util.SetProcess(speciesName, trainerInfo, eventForm, Settings);

                    if (!new LegalityAnalysis(result.Poke).Valid)
                        return false;

                    result.Poke.ResetPartyStats();
                    result.Message = $"It put up a fight, but you caught {(result.Poke.IsShiny ? $"**{speciesName}**" : $"{speciesName}")}!";
                    result.Message += DexCount(user, result, result.Poke.Species);
                }

                if (Util.Rng.CatchRNG < 100 - Settings.CatchRate)
                    result.FailedCatch = true;

                if (Util.Rng.ItemRNG >= 100 - Settings.ItemRate)
                {
                    TCItems item;
                    if (Util.Rng.ShinyCharmRNG > 10)
                    {
                        var vals = Enum.GetValues(typeof(TCItems));
                        do
                        {
                            item = (TCItems)vals.GetValue(new Random().Next(vals.Length));
                        } while (item <= 0);
                    }
                    else
                    {
                        var sc = user.Items.FirstOrDefault(x => x.Item == TCItems.ShinyCharm);
                        if (sc == default || (sc != default && sc.ItemCount < 20))
                            item = TCItems.ShinyCharm;
                        else item = TCItems.LoveSweet;
                    }

                    result.Item = item == TCItems.ShinyCharm ? "★**Shiny Charm**★" : Util.GetItemString((int)item);
                    var userItem = user.Items.FirstOrDefault(x => x.Item == item);
                    if (userItem == default)
                    {
                        user.Items.Add(new() { Item = item, ItemCount = 1 });
                        var names = ItemsValues.Replace(" ", "").Split(',');
                        var obj = new object[] { user.UserInfo.UserID, (int)item, 1 };
                        result.SQLCommands.Add(DBCommandConstructor("items", ItemsValues, "", names, obj, SQLTableContext.Insert));
                    }
                    else
                    {
                        userItem.ItemCount++;
                        var names = new string[] { "@count", "@user_id", "@id" };
                        var obj = new object[] { userItem.ItemCount, user.UserInfo.UserID, (int)item };
                        result.SQLCommands.Add(DBCommandConstructor("items", "count = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                    }
                }

                return true;
            }

            result.Success = FuncCatch();
            if (result.Success)
            {
                if (result.Poke.Species != 0)
                    user.UserInfo.CatchCount++;

                BuddySystem(user, result, out buddyMsg);
                result.Message += buddyMsg;

                if (result.EggPoke.Species != 0)
                {
                    user.UserInfo.CatchCount++;
                    result.Message += eggMsg;
                }

                var namesC = new string[] { "@catch_count", "@user_id" };
                var objC = new object[] { user.UserInfo.CatchCount, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("users", "catch_count = ?", "where user_id = ?", namesC, objC, SQLTableContext.Update));

                if (result.Item != string.Empty)
                {
                    bool article = TradeCordHelperUtil.ArticleChoice(result.Item[0]);
                    result.Message += result.FailedCatch ? $"&^&\nAs it fled it dropped {(article ? "an" : "a")} {result.Item}! Added to the items pouch." : $"&^&\nOh? It was holding {(article ? "an" : "a")} {result.Item}! Added to the items pouch.";
                }
            }

            result.User = user;
            result.EmbedName += $"Results{(result.EggPoke.Species != 0 ? "&^&\nEggs" : "")}{(result.Item != string.Empty ? "&^&\nItems" : "")}";
            return result;
        }

        private Results TradeHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncTrade()
            {
                if (!int.TryParse(input, out int id))
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                var found = user.Catches.TryGetValue(id, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "There is no Pokémon with this ID.";
                    return false;
                }

                var dcfavCheck = user.Daycare.ID1 == id || user.Daycare.ID2 == id || match.Favorite || user.Buddy.ID == id;
                if (dcfavCheck)
                {
                    result.Message = "Please remove your Pokémon from favorites and daycare before trading!";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }
                result.Poke = pk;

                var la = new LegalityAnalysis(result.Poke);
                if (!la.Valid || !(result.Poke is PK8))
                {
                    result.Message = "Oops, I cannot trade this Pokémon!";
                    return false;
                }

                result.PokeID = match.ID;
                user.Catches[match.ID].Traded = true;
                var names = new string[] { "@was_traded", "@user_id", "@id" };
                var obj = new object[] { true, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("catches", "was_traded = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                return true;
            }

            result.Success = FuncTrade();
            result.User = user;
            return result;
        }

        private Results ListHandler(TCUser user, string input)
        {
            Results result = new();
            result.EmbedName = $"{user.UserInfo.Username}'s List";
            bool FuncList()
            {
                List<string> filters = input.Contains("=") ? input.Split('=').ToList() : new();
                if (filters.Count > 0)
                {
                    filters.RemoveAt(0);
                    input = input.Split('=')[0].Trim();
                }

                for (int i = 0; i < filters.Count; i++)
                {
                    filters[i] = filters[i].ToLower().Trim();
                    filters[i] = Util.ListNameSanitize(filters[i]);
                }

                string nickname = input;
                input = Util.ListNameSanitize(input);
                bool speciesAndForm = input.Contains("-");
                bool isSpecies = SpeciesName.GetSpeciesID(speciesAndForm ? input.Split('-')[0] : input) > 0;
                bool isBall = Enum.TryParse(input, true, out Ball enumBall);
                bool isShiny = filters.FirstOrDefault(x => x == "Shiny") != default;
                var filterBall = filters.FirstOrDefault(x => x != "Shiny");
                var strings = GameInfo.GetStrings(LanguageID.English.GetLanguage2CharName()).forms;
                bool isForm = strings.Contains(input);

                if (input == "")
                {
                    result.Message = "In order to filter a Pokémon, we need to know which Pokémon to filter.";
                    return false;
                }

                string def = filters.Count == 0 ? $"and {(input == "All" ? "species != ''" : input == "Legendaries" ? "is_legendary = 1" : input == "Events" ? "is_event = 1" : input == "Eggs" ? "is_egg = 1" : input == "Shinies" ? "is_shiny = 1" : isBall ? $"ball = '{enumBall}'" : (speciesAndForm ? $"species||form = '{input}'" : isSpecies ? $"species = '{input}'" : isForm ? $"form = '-{input}'" : $"nickname = '{nickname}'"))} and was_traded = 0" : "";
                string one = filters.Count == 1 ? $"and {(input == "All" ? "species != ''" : input == "Legendaries" ? "is_legendary = 1" : input == "Events" ? "is_event = 1" : input == "Eggs" ? "is_egg = 1" : input == "Shinies" ? "is_shiny = 1" : (speciesAndForm ? $"species||form = '{input}'" : isSpecies ? $"species = '{input}'" : isForm ? $"form = '-{input}'" : $"nickname = '{nickname}'"))} and {(isShiny ? "is_shiny = 1" : $"ball = '{filters[0]}'")} and was_traded = 0" : "";
                string two = filters.Count == 2 ? $"and {(input == "All" ? "species != ''" : input == "Legendaries" ? "is_legendary = 1" : input == "Events" ? "is_event = 1" : input == "Eggs" ? "is_egg = 1" : (speciesAndForm ? $"species||form = '{input}'" : isSpecies ? $"species = '{input}'" : isForm ? $"form = '-{input}'" : $"nickname = '{nickname}'"))} and ball = '{filterBall}' and is_shiny = 1 and was_traded = 0" : "";

                Dictionary<int, TCCatch> matches = filters.Count switch
                {
                    1 => GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, "catches", one),
                    2 => GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, "catches", two),
                    _ => GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, "catches", def),
                };

                if (matches.Count == 0)
                {
                    result.Message = "No results found.";
                    return false;
                }

                HashSet<string> count = new(), countSh = new();
                if (input == "Shinies")
                {
                    foreach (var result in matches)
                        countSh.Add($"(__{result.Value.ID}__) {result.Value.Species}{result.Value.Form}");
                }
                else
                {
                    foreach (var result in matches)
                    {
                        var speciesString = result.Value.Shiny ? $"(__{result.Value.ID}__) {result.Value.Species}{result.Value.Form}" : $"({result.Value.ID}) {result.Value.Species}{result.Value.Form}";
                        if (result.Value.Shiny)
                            countSh.Add(speciesString);
                        count.Add(speciesString);
                    }
                }

                result.Message = string.Join(", ", input == "Shinies" ? countSh.OrderBy(x => int.Parse(x.Split(' ')[0].Trim(new char[] { '(', '_', ')' }))) : count.OrderBy(x => int.Parse(x.Split(' ')[0].Trim(new char[] { '(', '_', ')' }))));
                var listName = input == "Shinies" ? "Shiny Pokémon" : input == "All" ? "Pokémon" : input == "Egg" ? "Eggs" : $"{input} List";
                var listCount = input == "Shinies" ? $"★{countSh.Count}" : $"{count.Count}, ★{countSh.Count}";
                result.EmbedName = $"{user.UserInfo.Username}'s {listName} (Total: {listCount})";
                return true;
            }

            result.Success = FuncList();
            return result;
        }

        private Results InfoHandler(TCUser user, string input)
        {
            Results result = new();
            result.EmbedName = $"{user.UserInfo.Username}'s Pokémon Info";
            bool FuncInfo()
            {
                if (!int.TryParse(input, out int id))
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                var found = user.Catches.TryGetValue(id, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Could not find this ID.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }

                result.Poke = pk;
                result.EmbedName = $"{user.UserInfo.Username}'s {(match.Shiny ? "★" : "")}{match.Species}{match.Form} (ID: {match.ID})";
                return true;
            }

            result.Success = FuncInfo();
            return result;
        }

        private Results MassReleaseHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncMassRelease()
            {
                Dictionary<int, TCCatch> matches;
                input = Util.ListNameSanitize(input);
                Enum.TryParse(input, out Ball ball);
                bool speciesAndForm = input.Contains("-");
                string tableJoin = "catches c inner join daycare d on c.user_id = d.user_id inner join buddy b on c.user_id = b.user_id";
                string ballSearch = $"and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 0 and c.species != 'Ditto' and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.ball = '{ball}' and c.is_legendary = 0";
                string shinySearch = "and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 1 and c.species != 'Ditto' and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.is_event = 0 and c.is_legendary = 0";
                string legendarySearch = "and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 0 and c.species != 'Ditto' and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.is_event = 0 and c.is_legendary = 1";
                string eventSearch = "and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 0 and c.species != 'Ditto' and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.is_event = 1 and c.is_legendary = 0";
                string defaultSearch = "and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 0 and c.species != 'Ditto' and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.is_event = 0 and c.is_legendary = 0";

                if (ball != Ball.None)
                    matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, ballSearch, true);
                else if (input == "Shinies")
                    matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, shinySearch, true);
                else if (input == "Legendaries")
                    matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, legendarySearch, true);
                else if (input == "Events")
                    matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, eventSearch, true);
                else if (input != "")
                {
                    string speciesSearch = $"and {(speciesAndForm ? $"c.species||c.form = '{input}'" : $"c.species = '{input}' and c.form = ''")} and c.is_favorite = 0 and c.was_traded = 0 and c.is_shiny = 0 and c.id != d.id1 and c.id != d.id2 and c.id != b.id and c.ball != 'Cherish'";
                    matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, speciesSearch, true);
                }
                else matches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(user.UserInfo.UserID, tableJoin, defaultSearch, true);

                if (matches.Count() == 0)
                {
                    result.Message = input == "" ? "Cannot find any more non-shiny, non-Ditto, non-favorite, non-event, non-buddy, non-legendary Pokémon to release." : "Cannot find anything that could be released with the specified criteria.";
                    return false;
                }

                List<int> arr = new();
                foreach (var val in matches)
                {
                    user.Catches.Remove(val.Key);
                    arr.Add(val.Value.ID);
                }

                var names = new string[arr.Count + 1];
                var obj = new object[arr.Count + 1];
                names[0] = "@user_id";
                obj[0] = user.UserInfo.UserID;

                string questionM = string.Empty;
                for (int i = 0; i < arr.Count; i++)
                {
                    names[i + 1] = $"@id{i}";
                    obj[i + 1] = arr[i];
                    questionM += i + 1 == arr.Count ? "?" : "?, ";
                }
                result.SQLCommands.Add(DBCommandConstructor("catches", "", $"where user_id = ? and id in ({questionM})", names, obj, SQLTableContext.Delete));
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "", $"where user_id = ? and id in ({questionM})", names, obj, SQLTableContext.Delete));

                bool isLegend = Util.IsLegendaryOrMythical(speciesAndForm ? input.Split('-')[0] : input);
                string ballStr = ball != Ball.None ? $"Pokémon in {ball} Ball" : "";
                string generalOutput = input == "Shinies" ? "shiny Pokémon" : input == "Events" ? "non-shiny event Pokémon" : input == "Legendaries" ? "non-shiny legendary Pokémon" : ball != Ball.None ? ballStr : $"non-shiny {input}";
                string exclude = ball == Ball.Cherish || input == "Events" ? ", legendaries," : input == "Legendaries" ? ", events," : $", events,{(isLegend ? "" : " legendaries,")}";
                result.Message = input == "" ? "Every non-shiny Pokémon was released, excluding Ditto, favorites, events, buddy, legendaries, and those in daycare." : $"Every {generalOutput} was released, excluding favorites, buddy{exclude} and those in daycare.";
                return true;
            }

            result.Success = FuncMassRelease();
            result.User = user;
            return result;
        }

        private Results ReleaseHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncRelease()
            {
                if (!int.TryParse(input, out int id))
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                var found = user.Catches.TryGetValue(id, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Cannot find this Pokémon.";
                    return false;
                }

                if (user.Daycare.ID1 == id || user.Daycare.ID2 == id || match.Favorite || user.Buddy.ID == id)
                {
                    result.Message = "Cannot release a Pokémon in daycare, favorites, or if it's your buddy.";
                    return false;
                }

                var names = new string[] { "@user_id", "@id" };
                var obj = new object[] { user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("catches", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                user.Catches.Remove(match.ID);

                result.Message = $"You release your {(match.Shiny ? "★" : "")}{match.Species}{match.Form}.";
                return true;
            }

            result.Success = FuncRelease();
            result.User = user;
            return result;
        }

        private Results DaycareInfoHandler(TCDaycare dc, ulong userID)
        {
            Results result = new();
            if (dc.ID1 == 0 && dc.ID2 == 0)
                result.Message = "You do not have anything in daycare.";
            else
            {
                var dcSpecies1 = dc.ID1 == 0 ? "" : $"(ID: {dc.ID1}) {(dc.Shiny1 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(dc.Species1, 2, 8)}{(dc.Species1 == 29 || dc.Species1 == 32 ? "" : dc.Form1)} ({(Ball)dc.Ball1})";
                var dcSpecies2 = dc.ID2 == 0 ? "" : $"(ID: {dc.ID2}) {(dc.Shiny2 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(dc.Species2, 2, 8)}{(dc.Species2 == 29 || dc.Species2 == 32 ? "" : dc.Form2)} ({(Ball)dc.Ball2})";

                if (dc.ID1 != 0 && dc.ID2 != 0)
                    result.Message = $"{dcSpecies1}\n{dcSpecies2}{(Util.CanGenerateEgg(dc, userID, out _, out _, out _, out _) ? "\n\nThey seem to really like each other." : "\n\nThey don't really seem to be fond of each other. Make sure they're of the same evolution tree, can be eggs, and have been hatched!")}";
                else if (dc.ID1 == 0 || dc.ID2 == 0)
                    result.Message = $"{(dc.ID1 == 0 ? dcSpecies2 : dcSpecies1)}\n\nIt seems lonely.";
            }

            result.Success = true;
            return result;
        }

        private Results DaycareHandler(TCUser user, string action, string id)
        {
            Results result = new();
            result.EmbedName = $"{user.UserInfo.Username}'s Daycare";
            bool FuncDaycare()
            {
                id = id.ToLower();
                action = action.ToLower();
                if (!int.TryParse(id, out int _id) && id != "all")
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                string speciesString = string.Empty;
                bool deposit = action == "d" || action == "deposit";
                bool withdraw = action == "w" || action == "withdraw";
                bool found = user.Catches.TryGetValue(_id, out TCCatch match);
                if (deposit && (!found || match.Traded))
                {
                    result.Message = "There is no Pokémon with this ID.";
                    return false;
                }

                var names = new string[] { "@id1", "@species1", "@form1", "@ball1", "@shiny1", "@id2", "@species2", "@form2", "@ball2", "@shiny2", "@user_id" };
                if (withdraw)
                {
                    if (user.Daycare.ID1 == 0 && user.Daycare.ID2 == 0)
                    {
                        result.Message = "You do not have anything in daycare.";
                        return false;
                    }

                    var form1 = user.Daycare.Species1 == 29 || user.Daycare.Species1 == 32 ? "" : user.Daycare.Form1;
                    var form2 = user.Daycare.Species1 == 29 || user.Daycare.Species1 == 32 ? "" : user.Daycare.Form1;
                    if (id != "all")
                    {
                        if (user.Daycare.ID1.Equals(int.Parse(id)))
                        {
                            speciesString = $"(ID: {user.Daycare.ID1}) {(user.Daycare.Shiny1 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(user.Daycare.Species1, 2, 8)}{form1}";
                            user.Daycare = new() { ID2 = user.Daycare.ID2, Species2 = user.Daycare.Species2, Form2 = user.Daycare.Form2, Ball2 = user.Daycare.Ball2, Shiny2 = user.Daycare.Shiny2 };
                            var obj = new object[] { 0, 0, string.Empty, 0, 0, user.Daycare.ID2, user.Daycare.Species2, user.Daycare.Form2, user.Daycare.Ball2, user.Daycare.Shiny2, user.UserInfo.UserID };
                            result.SQLCommands.Add(DBCommandConstructor("daycare", "id1 = ?, species1 = ?, form1 = ?, ball1 = ?, shiny1 = ?, id2 = ?, species2 = ?, form2 = ?, ball2 = ?, shiny2 = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
                        }
                        else if (user.Daycare.ID2.Equals(int.Parse(id)))
                        {
                            speciesString = $"(ID: {user.Daycare.ID2}) {(user.Daycare.Shiny2 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(user.Daycare.Species2, 2, 8)}{form2}";
                            user.Daycare = new() { ID1 = user.Daycare.ID1, Species1 = user.Daycare.Species1, Form1 = user.Daycare.Form1, Ball1 = user.Daycare.Ball1, Shiny1 = user.Daycare.Shiny1 };
                            var obj = new object[] { user.Daycare.ID1, user.Daycare.Species1, user.Daycare.Form1, user.Daycare.Ball1, user.Daycare.Shiny1, 0, 0, string.Empty, 0, 0, user.UserInfo.UserID };
                            result.SQLCommands.Add(DBCommandConstructor("daycare", "id1 = ?, species1 = ?, form1 = ?, ball1 = ?, shiny1 = ?, id2 = ?, species2 = ?, form2 = ?, ball2 = ?, shiny2 = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
                        }
                        else
                        {
                            result.Message = "You do not have that Pokémon in daycare.";
                            return false;
                        }
                    }
                    else
                    {
                        bool fullDC = user.Daycare.ID1 != 0 && user.Daycare.ID2 != 0;
                        speciesString = !fullDC ? $"(ID: {(user.Daycare.ID1 != 0 ? user.Daycare.ID1 : user.Daycare.ID2)}) {(user.Daycare.ID1 != 0 && user.Daycare.Shiny1 ? "★" : user.Daycare.ID2 != 0 && user.Daycare.Shiny2 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(user.Daycare.ID1 != 0 ? user.Daycare.Species1 : user.Daycare.Species2, 2, 8)}{(user.Daycare.ID1 != 0 ? form1 : form2)}" :
                            $"(ID: {user.Daycare.ID1}) {(user.Daycare.Shiny1 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(user.Daycare.Species1, 2, 8)}{form1} and (ID: {user.Daycare.ID2}) {(user.Daycare.Shiny2 ? "★" : "")}{SpeciesName.GetSpeciesNameGeneration(user.Daycare.Species2, 2, 8)}{form2}";
                        user.Daycare = new();
                        var obj = new object[] { 0, 0, string.Empty, 0, 0, 0, 0, string.Empty, 0, 0, user.UserInfo.UserID };
                        result.SQLCommands.Add(DBCommandConstructor("daycare", "id1 = ?, species1 = ?, form1 = ?, ball1 = ?, shiny1 = ?, id2 = ?, species2 = ?, form2 = ?, ball2 = ?, shiny2 = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
                    }
                }
                else if (deposit)
                {
                    if (user.Daycare.ID1 != 0 && user.Daycare.ID2 != 0)
                    {
                        result.Message = "Daycare full, please withdraw something first.";
                        return false;
                    }

                    var speciesStr = string.Join("", match.Species.Split('-', ' ', '’', '.'));
                    speciesStr += match.Species + match.Form == "Nidoran-M" ? "M" : match.Species + match.Form == "Nidoran-F" ? "F" : "";
                    Enum.TryParse(match.Ball, out Ball ball);
                    Enum.TryParse(speciesStr, out Species species);
                    if ((user.Daycare.ID1 == 0 && user.Daycare.ID2 == 0) || (user.Daycare.ID1 == 0 && user.Daycare.ID2 != _id))
                        user.Daycare = new() { Ball1 = (int)ball, Form1 = match.Form, ID1 = match.ID, Shiny1 = match.Shiny, Species1 = (int)species, Ball2 = user.Daycare.Ball2, Form2 = user.Daycare.Form2, ID2 = user.Daycare.ID2, Shiny2 = user.Daycare.Shiny2, Species2 = user.Daycare.Species2 };
                    else if (user.Daycare.ID2 == 0 && user.Daycare.ID1 != _id)
                        user.Daycare = new() { Ball2 = (int)ball, Form2 = match.Form, ID2 = match.ID, Shiny2 = match.Shiny, Species2 = (int)species, Ball1 = user.Daycare.Ball1, Form1 = user.Daycare.Form1, ID1 = user.Daycare.ID1, Shiny1 = user.Daycare.Shiny1, Species1 = user.Daycare.Species1 };
                    else
                    {
                        result.Message = "You've already deposited that Pokémon to daycare.";
                        return false;
                    }
                    var obj = new object[] { user.Daycare.ID1, user.Daycare.Species1, user.Daycare.Form1, user.Daycare.Ball1, user.Daycare.Shiny1, user.Daycare.ID2, user.Daycare.Species2, user.Daycare.Form2, user.Daycare.Ball2, user.Daycare.Shiny2, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("daycare", "id1 = ?, species1 = ?, form1 = ?, ball1 = ?, shiny1 = ?, id2 = ?, species2 = ?, form2 = ?, ball2 = ?, shiny2 = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
                }
                else
                {
                    result.Message = "Invalid command.";
                    return false;
                }

                result.EmbedName = $"{(deposit ? " Deposit" : " Withdraw")}";
                result.Message = deposit && found ? $"Deposited your {(match.Shiny ? "★" : "")}{match.Species}{match.Form}({match.Ball}) to daycare!" : $"You withdrew your {speciesString} from the daycare.";
                return true;
            }

            result.Success = FuncDaycare();
            result.User = user;
            return result;
        }

        private Results GiftHandler(TCUser user, TCUser m_user, string input)
        {
            Results result = new();
            bool FuncGift()
            {
                if (!int.TryParse(input, out int id))
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                var found = user.Catches.TryGetValue(id, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Cannot find this Pokémon.";
                    return false;
                }

                var dcfavCheck = user.Daycare.ID1 == id || user.Daycare.ID2 == id || match.Favorite || user.Buddy.ID == id;
                if (dcfavCheck)
                {
                    result.Message = "Please remove your Pokémon from favorites, daycare, and make sure it's not an active buddy before gifting!";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Cannot find this Pokémon.";
                    return false;
                }

                HashSet<int> newIDParse = new();
                foreach (var caught in m_user.Catches)
                    newIDParse.Add(caught.Key);

                var newID = Util.Indexing(newIDParse.OrderBy(x => x).ToArray());
                bool isLegend = Util.IsLegendaryOrMythical(match.Species);

                var names = CatchValues.Replace(" ", "").Split(',');
                var obj = new object[] { m_user.UserInfo.UserID, newID, match.Shiny, match.Ball, match.Nickname, match.Species, match.Form, match.Egg, false, false, isLegend, match.Event };
                result.SQLCommands.Add(DBCommandConstructor("catches", CatchValues, "", names, obj, SQLTableContext.Insert));

                names = BinaryCatchesValues.Replace(" ", "").Split(',');
                obj = new object[] { m_user.UserInfo.UserID, newID, pk.DecryptedPartyData };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", BinaryCatchesValues, "", names, obj, SQLTableContext.Insert));
                m_user.Catches.Add(newID, new() { Ball = match.Ball, Egg = match.Egg, Form = match.Form, ID = newID, Shiny = match.Shiny, Species = match.Species, Nickname = match.Nickname, Favorite = false, Traded = false, Legendary = isLegend, Event = match.Event });

                names = new string[] { "@user_id", "@id" };
                obj = new object[] { user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("catches", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                user.Catches.Remove(match.ID);

                var specID = SpeciesName.GetSpeciesID(match.Species);
                var dex = (int[])Enum.GetValues(typeof(Gen8Dex));
                var missingEntries = Util.GetMissingDexEntries(dex, m_user.Dex.Entries).Count;

                result.Message = $"You gifted your {(match.Shiny ? "★" : "")}{match.Species}{match.Form} to {m_user.UserInfo.Username}. New ID is {newID}.";
                if (m_user.Dex.DexCompletionCount == 0 || (m_user.Dex.DexCompletionCount < 20 && missingEntries <= 50))
                    result.Message += DexCount(m_user, result, specID, m_user.UserInfo.Username);

                return true;
            }

            result.Success = FuncGift();
            result.User = user;
            result.Giftee = m_user;
            return result;
        }

        private Results TrainerInfoSetHandler(TCUser user, string[] input)
        {
            Results result = new();
            user.TrainerInfo.OTName = input[0];
            user.TrainerInfo.OTGender = input[1];
            user.TrainerInfo.TID = int.Parse(input[2]);
            user.TrainerInfo.SID = int.Parse(input[3]);
            user.TrainerInfo.Language = input[4];
            var names = new string[] { "@ot", "@ot_gender", "@tid", "@sid", "@language", "@user_id" };
            var obj = new object[] { user.TrainerInfo.OTName, user.TrainerInfo.OTGender, user.TrainerInfo.TID, user.TrainerInfo.SID, user.TrainerInfo.Language, user.UserInfo.UserID };
            result.SQLCommands.Add(DBCommandConstructor("trainerinfo", "ot = ?, ot_gender = ?, tid = ?, sid = ?, language = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

            result.Message = $"\nYour trainer info was set to the following:" +
                             $"\n**OT:** {user.TrainerInfo.OTName}" +
                             $"\n**OTGender:** {user.TrainerInfo.OTGender}" +
                             $"\n**TID:** {user.TrainerInfo.TID}" +
                             $"\n**SID:** {user.TrainerInfo.SID}" +
                             $"\n**Language:** {user.TrainerInfo.Language}";
            result.Success = true;
            result.User = user;
            return result;
        }

        private Results TrainerInfoHandler(TCTrainerInfo info, List<TCItem> items, int offset)
        {
            Results result = new();
            var sc = items.FirstOrDefault(x => x.Item == TCItems.ShinyCharm);
            var count = sc == default ? 0 : sc.ItemCount;
            result.Message = $"\n**OT:** {info.OTName}" +
                             $"\n**OTGender:** {info.OTGender}" +
                             $"\n**TID:** {info.TID}" +
                             $"\n**SID:** {info.SID}" +
                             $"\n**Language:** {info.Language}" +
                             $"\n**Shiny Charm:** {count}" +
                             $"\n**UTC Time Offset:** {offset}";
            result.Success = true;
            return result;
        }

        private Results FavoritesInfoHandler(Dictionary<int, TCCatch> catches)
        {
            Results result = new();
            bool FuncFavoritesInfo()
            {
                var favs = catches.Values.ToList().FindAll(x => x.Favorite);
                if (favs.Count == 0)
                {
                    result.Message = "You don't have anything in favorites yet!";
                    return false;
                }

                List<string> names = new();
                foreach (var fav in favs)
                {
                    var match = catches[fav.ID];
                    names.Add(match.Shiny ? $"(__{match.ID}__) {match.Species}{match.Form}" : $"({match.ID}) {match.Species}{match.Form}");
                }

                result.Message = string.Join(", ", names.OrderBy(x => int.Parse(x.Split(' ')[0].Trim(new char[] { '(', '_', ')' }))));
                return true;
            }

            result.Success = FuncFavoritesInfo();
            return result;
        }

        private Results FavoritesHandler(TCUser user, string input)
        {
            Results result = new();
            result.EmbedName = $"{user.UserInfo.Username}'s Favorite";
            bool FuncFavorites()
            {
                var arg = input.ToLower();
                if (!int.TryParse(input, out int id) && arg != "clear")
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }

                if (arg == "clear")
                {
                    var names = new string[] { "@is_favorite", "@user_id" };
                    var obj = new object[] { false, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("catches", "is_favorite = ?", "where user_id = ? and is_favorite = 1", names, obj, SQLTableContext.Update));

                    var favs = user.Catches.ToList().FindAll(x => x.Value.Favorite == true);
                    for (int i = 0; i < favs.Count; i++)
                        user.Catches[favs[i].Key].Favorite = false;

                    result.Message = $"{user.UserInfo.Username}, all of your favorites were cleared!";
                    result.EmbedName += " Clear";
                    return true;
                }

                var found = user.Catches.TryGetValue(id, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Cannot find this Pokémon.";
                    return false;
                }

                if (!match.Favorite)
                {
                    var names = new string[] { "@is_favorite", "@user_id", "@id" };
                    var obj = new object[] { true, user.UserInfo.UserID, id };
                    result.SQLCommands.Add(DBCommandConstructor("catches", "is_favorite = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                    match.Favorite = true;
                    result.Message = $"{user.UserInfo.Username}, added your {(match.Shiny ? "★" : "")}{match.Species}{match.Form} to favorites!";
                    result.EmbedName += " Addition";
                }
                else
                {
                    var names = new string[] { "@is_favorite", "@user_id", "@id" };
                    var obj = new object[] { false, user.UserInfo.UserID, id };
                    result.SQLCommands.Add(DBCommandConstructor("catches", "is_favorite = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                    match.Favorite = false;
                    result.Message = $"{user.UserInfo.Username}, removed your {(match.Shiny ? "★" : "")}{match.Species}{match.Form} from favorites!";
                    result.EmbedName += " Removal";
                }

                return true;
            }

            result.Success = FuncFavorites();
            result.User = user;
            return result;
        }

        private Results DexHandler(TCDex dex, TCPerks perks, string input)
        {
            Results result = new();
            var entries = (int[])Enum.GetValues(typeof(Gen8Dex));
            var speciesBoost = perks.SpeciesBoost != 0 ? $"\n**Pokémon Boost:** {SpeciesName.GetSpeciesNameGeneration(perks.SpeciesBoost, 2, 8)}" : "\n**Pokémon Boost:** N/A";

            if (input == "missing")
            {
                List<string> missing = Util.GetMissingDexEntries(entries, dex.Entries);
                result.Message = string.Join(", ", missing.OrderBy(x => x));
                result.Success = true;
                return result;
            }

            result.Message = $"\n**Pokédex:** {dex.Entries.Count}/{entries.Length}\n**Level:** {dex.DexCompletionCount + perks.ActivePerks.Count}{speciesBoost}";
            result.Success = true;
            return result;
        }

        private Results PerkHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncDexPerks()
            {
                if (input == "" && (user.Dex.DexCompletionCount > 0 || user.Perks.ActivePerks.Count > 0))
                {
                    result.Message = $"**CatchBoost:** {user.Perks.ActivePerks.FindAll(x => x == DexPerks.CatchBoost).Count}\n" +
                                     $"**ItemBoost:** {user.Perks.ActivePerks.FindAll(x => x == DexPerks.ItemBoost).Count}\n" +
                                     $"**SpeciesBoost:** {user.Perks.ActivePerks.FindAll(x => x == DexPerks.SpeciesBoost).Count}\n" +
                                     $"**GmaxBoost:** {user.Perks.ActivePerks.FindAll(x => x == DexPerks.GmaxBoost).Count}\n" +
                                     $"**CherishBoost:** {user.Perks.ActivePerks.FindAll(x => x == DexPerks.CherishBoost).Count}";
                    return true;
                }
                else if (input == "clear")
                {
                    user.Dex.DexCompletionCount += user.Perks.ActivePerks.Count;
                    user.Perks.ActivePerks = new();
                    user.Perks.SpeciesBoost = 0;
                    var namesC = new string[] { "@perks", "@species_boost", "@user_id" };
                    var objC = new object[] { string.Empty, 0, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("perks", "perks = ?, species_boost = ?", "where user_id = ?", namesC, objC, SQLTableContext.Update));

                    namesC = new string[] { "@dex_count", "@user_id" };
                    objC = new object[] { user.Dex.DexCompletionCount, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("dex", "dex_count = ?", "where user_id = ?", namesC, objC, SQLTableContext.Update));

                    result.Message = "All active perks cleared!";
                    return true;
                }

                if (user.Dex.DexCompletionCount == 0)
                {
                    result.Message = "No perks available. Unassign a perk or complete the Dex to get more!";
                    return false;
                }

                string[] perk = input.Split(',', ' ');
                if (perk.Length < 2)
                {
                    result.Message = "Not enough parameters provided.";
                    return false;
                }

                if (!int.TryParse(perk[1], out int count))
                {
                    result.Message = "Incorrect input, could not parse perk point amount.";
                    return false;
                }
                else if (count > user.Dex.DexCompletionCount)
                {
                    result.Message = "Not enough points available to assign all requested perks.";
                    return false;
                }
                else if (count == 0)
                {
                    result.Message = "Please enter a non-zero amount";
                    return false;
                }

                if (!Enum.TryParse(perk[0], true, out DexPerks perkVal) || perkVal == DexPerks.ShinyBoost || perkVal == DexPerks.EggRateBoost)
                {
                    result.Message = "Perk name was not recognized.";
                    return false;
                }

                var activeCount = user.Perks.ActivePerks.FindAll(x => x == perkVal).Count;
                if (activeCount + count > 5)
                    count = 5 - activeCount;

                if (count == 0)
                {
                    result.Message = "Perk is already maxed out.";
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    user.Perks.ActivePerks.Add(perkVal);
                    user.Dex.DexCompletionCount -= 1;
                }

                var arrStr = Util.ArrayStringify(user.Perks.ActivePerks.ToArray());
                var names = new string[] { "@perks", "@user_id" };
                var obj = new object[] { arrStr, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("perks", "perks = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                names = new string[] { "@dex_count", "@user_id" };
                obj = new object[] { user.Dex.DexCompletionCount, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("dex", "dex_count = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                result.Message = $"{(count > 1 ? $"Added {count} perk {(count > 1 ? "points" : "point")} to {perkVal}!" : $"{perkVal} perk added!")}";
                return true;
            }

            result.Success = FuncDexPerks();
            result.User = user;
            return result;
        }

        private Results SpeciesBoostHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncSpeciesBoost()
            {
                if (!user.Perks.ActivePerks.Contains(DexPerks.SpeciesBoost))
                {
                    result.Message = "SpeciesBoost perk isn't active.";
                    return false;
                }

                input = Util.ListNameSanitize(input).Replace("'", "").Replace("-", "").Replace(" ", "").Replace(".", "");
                if (!Enum.TryParse(input, out Gen8Dex species))
                {
                    result.Message = "Entered species was not recognized.";
                    return false;
                }

                user.Perks.SpeciesBoost = (int)species;
                var names = new string[] { "@species_boost", "@user_id" };
                var obj = new object[] { (int)species, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("perks", "species_boost = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                result.Message = $"Catch chance for {species} was slightly boosted!";
                return true;
            }

            result.Success = FuncSpeciesBoost();
            result.User = user;
            return result;
        }

        private Results BuddyHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncBuddy()
            {
                int id = 0;
                if (input == "remove" && user.Buddy.ID != 0)
                {
                    user.Buddy = new();
                    result.Message = "Buddy removed!";
                    var names = new string[] { "@id", "@name", "@ability", "@user_id" };
                    var obj = new object[] { 0, string.Empty, 0, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("buddy", "id = ?, name = ?, ability = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                    return true;
                }
                else if (input != string.Empty && !int.TryParse(input, out id))
                {
                    result.Message = "Please enter a numerical catch ID.";
                    return false;
                }
                else if (input == string.Empty && user.Buddy.ID == 0)
                {
                    result.Message = "You don't have an active buddy.";
                    return false;
                }
                else if (id == user.Buddy.ID)
                {
                    result.Message = "This is already your buddy!";
                    return false;
                }

                var found = user.Catches.TryGetValue(input != string.Empty ? id : user.Buddy.ID, out TCCatch match);
                if (!found)
                {
                    result.Message = "Could not find this Pokémon.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }

                result.Poke = pk;
                if (input == string.Empty)
                {
                    result.EmbedName = $"{user.UserInfo.Username}'s {(match.Shiny ? "★" : "")}{(pk.IsNicknamed ? $"{match.Nickname}" : $"{match.Species}{match.Form}")}";
                    result.EmbedName += $" (ID: {match.ID})";
                    return true;
                }
                else
                {
                    user.Buddy = new()
                    {
                        ID = id,
                        Nickname = pk.Nickname,
                        Ability = (Ability)pk.Ability,
                    };

                    var names = new string[] { "@id", "@name", "@ability", "@user_id" };
                    var obj = new object[] { id, pk.Nickname, pk.Ability, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("buddy", "id = ?, name = ?, ability = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                    result.Message = $"Set your {(match.Shiny ? "★" : "")}{(pk.IsNicknamed ? $"{user.Buddy.Nickname}" : $"{match.Species}{match.Form}")} as your new buddy!";
                    return true;
                }
            }

            result.Success = FuncBuddy();
            result.User = user;
            return result;
        }

        private Results NicknameHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncNickname()
            {
                if (user.Buddy.ID == 0)
                {
                    result.Message = "You don't have an active buddy!";
                    return false;
                }               
                else if (WordFilter.IsFiltered(input, out _))
                {
                    result.Message = "Nickname triggered the word filter. Please choose a different nickname.";
                    return false;
                }
                else if (input.Length > 12)
                {
                    result.Message = "Nickname is too long.";
                    return false;
                }

                var found = user.Catches.TryGetValue(user.Buddy.ID, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Could not find this Pokémon.";
                    return false;
                }
                else if (match.Egg)
                {
                    result.Message = "Cannot nickname eggs.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }

                bool clear = input.ToLower() == "clear";
                if (clear)
                    pk.ClearNickname();
                else pk.SetNickname(input);

                var la = new LegalityAnalysis(pk);
                if (!la.Valid)
                {
                    result.Message = "Nickname is not valid.";
                    return false;
                }

                user.Buddy.Nickname = clear ? pk.Nickname : input;
                result.Message = clear ? "Your buddy's nickname was cleared!" : "Your buddy's nickname was updated!";

                var names = new string[] { "@name", "@user_id" };
                var obj = new object[] { user.Buddy.Nickname, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("buddy", "name = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                names = new string[] { "@nickname", "@user_id", "@id" };
                obj = new object[] { user.Buddy.Nickname, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("catches", "nickname = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                names = new string[] { "@data", "@user_id", "@id" };
                obj = new object[] { pk.DecryptedPartyData, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "data = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                return true;
            }

            result.Success = FuncNickname();
            result.User = user;
            return result;
        }

        private Results EvolutionHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncEvolution()
            {
                if (user.Buddy.ID == 0)
                {
                    result.Message = "You don't have an active buddy.";
                    return false;
                }

                bool alc = Enum.TryParse(input, true, out AlcremieForms alcremie);
                if (!alc)
                    alcremie = AlcremieForms.None;

                bool reg = Enum.TryParse(input, true, out RegionalFormArgument regional);
                if (!reg)
                    regional = RegionalFormArgument.None;

                if (input != "" && alcremie < 0 && regional < 0)
                {
                    result.Message = "Unable to parse input.";
                    return false;
                }

                var found = user.Catches.TryGetValue(user.Buddy.ID, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Could not find this Pokémon.";
                    return false;
                }
                else if (match.Egg)
                {
                    result.Message = "Eggs cannot evolve.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }
                else if (pk.HeldItem == (int)TCItems.Everstone)
                {
                    result.Message = "Your buddy cannot evolve while holding an Everstone.";
                    return false;
                }

                var oldName = pk.IsNicknamed ? pk.Nickname : $"{SpeciesName.GetSpeciesNameGeneration(pk.Species, 2, 8)}{TradeCordHelperUtil.FormOutput(pk.Species, pk.Form, out _)}";
                var timeStr = TradeCordHelperUtil.TimeOfDayString(user.UserInfo.TimeZoneOffset, false);
                var tod = TradeExtensions.EnumParse<TimeOfDay>(timeStr);
                if (tod == TimeOfDay.Dawn)
                    tod = TimeOfDay.Morning;

                if (!Util.EvolvePK(pk, tod, out string message, out PK8? shedinja, alcremie, regional))
                {
                    result.Message = message;
                    if (message.Contains("Failed to evolve"))
                        result.Message += $"\nUser ID: {user.UserInfo.UserID}\nBuddy ID: {match.ID}";
                    return false;
                }

                var form = TradeCordHelperUtil.FormOutput(pk.Species, pk.Form, out _);
                var species = SpeciesName.GetSpeciesNameGeneration(pk.Species, 2, 8);

                user.Catches[match.ID].Species = species;
                user.Catches[match.ID].Form = form;
                user.Catches[match.ID].Nickname = pk.Nickname;
                if (user.Daycare.ID1 == match.ID)
                {
                    user.Daycare.Species1 = pk.Species;
                    user.Daycare.Form1 = form;
                    var namesDC = new string[] { "@form1", "@species1", "@user_id", "@id1" };
                    var objDC = new object[] { form, pk.Species, user.UserInfo.UserID, match.ID };
                    result.SQLCommands.Add(DBCommandConstructor("daycare", "form1 = ?, species1 = ?", "where user_id = ? and id1 = ?", namesDC, objDC, SQLTableContext.Update));
                }
                else if (user.Daycare.ID2 == match.ID)
                {
                    user.Daycare.Form2 = form;
                    user.Daycare.Species2 = pk.Species;
                    var namesDC = new string[] { "@form2", "@species2", "@user_id", "@id2" };
                    var objDC = new object[] { form, pk.Species, user.UserInfo.UserID, match.ID };
                    result.SQLCommands.Add(DBCommandConstructor("daycare", "form2 = ?, species2 = ?", "where user_id = ? and id2 = ?", namesDC, objDC, SQLTableContext.Update));
                }

                var names = new string[] { "@form", "@species", "@nickname", "@user_id", "@id" };
                var obj = new object[] { form, species, pk.Nickname, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("catches", "form = ?, species = ?, nickname = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                if (shedinja != null)
                    result.Shedinja = shedinja;

                user.Buddy.Ability = (Ability)pk.Ability;
                user.Buddy.Nickname = pk.Nickname;
                names = new string[] { "@name", "@ability", "@user_id" };
                obj = new object[] { pk.Nickname, pk.Ability, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("buddy", "name = ?, ability = ?", "where user_id = ?", names, obj, SQLTableContext.Update));

                names = new string[] { "@data", "@user_id", "@id" };
                obj = new object[] { pk.DecryptedPartyData, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "data = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));

                result.Message = $"{oldName} evolved into {(pk.IsShiny ? $"**{species + form}**" : species + form)}!";
                result.Message += DexCount(user, result, pk.Species);
                result.Poke = pk;
                return true;
            }

            result.Success = FuncEvolution();
            result.User = user;
            return result;
        }

        private Results GiveItemHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncGiveItem()
            {
                if (user.Buddy.ID == 0)
                {
                    result.Message = "You don't have an active buddy to give an item to.";
                    return false;
                }

                var item = TradeExtensions.EnumParse<TCItems>(input);
                var userItem = user.Items.FirstOrDefault(x => x.Item == item);
                if (userItem == default || userItem.ItemCount == 0)
                {
                    result.Message = "You do not have this item.";
                    return false;
                }

                var found = user.Catches.TryGetValue(user.Buddy.ID, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Could not find this Pokémon.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }
                else if (pk.IsEgg)
                {
                    result.Message = "Eggs cannot hold items!";
                    return false;
                }

                if (pk.HeldItem != 0)
                {
                    var itemCheck = (TCItems)pk.HeldItem;
                    if (itemCheck > 0)
                    {
                        var heldItem = user.Items.FirstOrDefault(x => x.Item == itemCheck);
                        if (heldItem == default)
                        {
                            user.Items.Add(new() { Item = itemCheck, ItemCount = 1 });
                            var namesI = ItemsValues.Replace(" ", "").Split(',');
                            var objI = new object[] { user.UserInfo.UserID, (int)itemCheck, 1 };
                            result.SQLCommands.Add(DBCommandConstructor("items", ItemsValues, "", namesI, objI, SQLTableContext.Insert));
                        }
                        else
                        {
                            heldItem.ItemCount++;
                            var namesH = new string[] { "@count", "@user_id", "@id" };
                            var objH = new object[] { heldItem.ItemCount, user.UserInfo.UserID, (int)itemCheck };
                            result.SQLCommands.Add(DBCommandConstructor("items", "count = ?", "where user_id = ? and id = ?", namesH, objH, SQLTableContext.Update));
                        }
                    }
                }

                pk.HeldItem = (int)item;
                bool updateBuddy = false;
                if ((int)item >= 904 && (int)item <= 920 && pk.Species == (int)Species.Silvally)
                    pk.Form = Util.SilvallyFormMath(0, (int)item);
                else if (item == TCItems.GriseousOrb && pk.Species == (int)Species.Giratina)
                {
                    updateBuddy = true;
                    pk.Form = 1;
                    pk.RefreshAbility(pk.AbilityNumber);
                }

                if (updateBuddy)
                {
                    var namesB = new string[] { "@ability", "@user_id" };
                    var objB = new object[] { pk.Ability, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("buddy", "ability = ?", "where user_id = ?", namesB, objB, SQLTableContext.Update));
                }

                var la = new LegalityAnalysis(pk);
                if (!la.Valid)
                {
                    result.Message = $"Oops, something went wrong while giving an item to {pk.Nickname}!";
                    return false;
                }

                userItem.ItemCount--;
                if (userItem.ItemCount == 0)
                {
                    var names = new string[] { "@user_id", "@id" };
                    var obj = new object[] { user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                }
                else
                {
                    var names = new string[] { "@count", "@user_id", "@id" };
                    var obj = new object[] { userItem.ItemCount, user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "count = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                }

                var namesU = new string[] { "@data", "@user_id", "@id" };
                var objU = new object[] { pk.DecryptedPartyData, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "data = ?", "where user_id = ? and id = ?", namesU, objU, SQLTableContext.Update));

                var itemStr = Util.GetItemString((int)item);
                result.Message = $"You gave {(TradeCordHelperUtil.ArticleChoice(itemStr[0]) ? "an" : "a")} {itemStr} to your buddy!";
                return true;
            }

            result.Success = FuncGiveItem();
            result.User = user;
            return result;
        }

        private Results GiftItemHandler(TCUser user, TCUser m_user, string input, string countInput)
        {
            Results result = new();
            bool FuncGiftItem()
            {
                var item = TradeExtensions.EnumParse<TCItems>(input);
                var userItem = user.Items.FirstOrDefault(x => x.Item == item);
                var count = int.Parse(countInput);
                if (userItem == default || userItem.ItemCount == 0)
                {
                    result.Message = "You do not have this item.";
                    return false;
                }
                else if (count > userItem.ItemCount)
                {
                    result.Message = "You do not have enough of this item.";
                    return false;
                }

                var gifteeItem = m_user.Items.FirstOrDefault(x => x.Item == item);
                if (gifteeItem == default)
                {
                    m_user.Items.Add(new() { Item = item, ItemCount = count });
                    var namesN = ItemsValues.Replace(" ", "").Split(',');
                    var objN = new object[] { m_user.UserInfo.UserID, (int)item, 1 };
                    result.SQLCommands.Add(DBCommandConstructor("items", ItemsValues, "", namesN, objN, SQLTableContext.Insert));
                }
                else
                {
                    gifteeItem.ItemCount += count;
                    var namesU = new string[] { "@count", "@user_id", "@id" };
                    var objU = new object[] { gifteeItem.ItemCount, m_user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "count = ?", "where user_id = ? and id = ?", namesU, objU, SQLTableContext.Update));
                }

                userItem.ItemCount -= count;
                if (userItem.ItemCount == 0)
                {
                    var names = new string[] { "@user_id", "@id" };
                    var obj = new object[] { user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "", "where user_id = ? and id = ?", names, obj, SQLTableContext.Delete));
                }
                else
                {
                    var names = new string[] { "@count", "@user_id", "@id" };
                    var obj = new object[] { userItem.ItemCount, user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "count = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                }

                var itemStr = Util.GetItemString((int)item);
                result.Message = $"You gifted {count} {itemStr}{(count == 1 ? "" : "s")} to {m_user.UserInfo.Username}!";
                return true;
            }

            result.Success = FuncGiftItem();
            result.User = user;
            result.Giftee = m_user;
            return result;
        }

        private Results TakeItemHandler(TCUser user)
        {
            Results result = new();
            bool FuncTakeItem()
            {
                if (user.Buddy.ID == 0)
                {
                    result.Message = "You don't have an active buddy to take an item from.";
                    return false;
                }

                var found = user.Catches.TryGetValue(user.Buddy.ID, out TCCatch match);
                if (!found || match.Traded)
                {
                    result.Message = "Could not find this Pokémon.";
                    return false;
                }

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                {
                    result.Message = "Oops, something happened when converting your Pokémon!";
                    return false;
                }

                var item = (TCItems)pk.HeldItem;
                if (pk.HeldItem == 0)
                {
                    result.Message = "Your buddy isn't holding an item.";
                    return false;
                }
                else if (item <= 0)
                {
                    result.Message = "Oops, this item is not yet available!";
                    return false;
                }

                var heldItem = user.Items.FirstOrDefault(x => x.Item == item);
                if (heldItem == default)
                {
                    user.Items.Add(new() { Item = item, ItemCount = 1 });
                    var namesH = ItemsValues.Replace(" ", "").Split(',');
                    var objH = new object[] { user.UserInfo.UserID, (int)item, 1 };
                    result.SQLCommands.Add(DBCommandConstructor("items", ItemsValues, "", namesH, objH, SQLTableContext.Insert));
                }
                else
                {
                    heldItem.ItemCount++;
                    var namesU = new string[] { "@count", "@user_id", "@id" };
                    var objU = new object[] { heldItem.ItemCount, user.UserInfo.UserID, (int)item };
                    result.SQLCommands.Add(DBCommandConstructor("items", "count = ?" , "where user_id = ? and id = ?", namesU, objU, SQLTableContext.Update));
                }

                var itemStr = Util.GetItemString(pk.HeldItem);
                result.Message = $"You took {(TradeCordHelperUtil.ArticleChoice(itemStr[0]) ? "an" : "a")} {itemStr} from your buddy!";

                pk.HeldItem = 0;
                bool updateBuddy = false;
                if ((int)item >= 904 && (int)item <= 920 && pk.Species == (int)Species.Silvally)
                    pk.Form = 0;
                else if (item == TCItems.GriseousOrb && pk.Species == (int)Species.Giratina)
                {
                    updateBuddy = true;
                    pk.Form = 0;
                    pk.RefreshAbility(pk.AbilityNumber);
                }

                if (updateBuddy)
                {
                    var namesB = new string[] { "@ability", "@user_id" };
                    var objB = new object[] { pk.Ability, user.UserInfo.UserID };
                    result.SQLCommands.Add(DBCommandConstructor("buddy", "ability = ?", "where user_id = ?", namesB, objB, SQLTableContext.Update));
                }

                var names = new string[] { "@data", "@user_id", "@id" };
                var obj = new object[] { pk.DecryptedPartyData, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "data = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                return true;
            }

            result.Success = FuncTakeItem();
            result.User = user;
            return result;
        }

        private Results ItemListHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncItemList()
            {
                var item = TradeExtensions.EnumParse<TCItems>(input);
                if (input != "all" && item <= 0)
                {
                    result.Message = input == "" ? "Nothing to search for." : "Unrecognized item.";
                    return false;
                }

                var list = user.Items;
                List<TCItem> items = input switch
                {
                    "all" => list.FindAll(x => x.ItemCount > 0),
                    _ => list.FindAll(x => x.Item == item && x.ItemCount > 0),
                };

                if (items.Count == 0)
                {
                    result.Message = "Nothing found that meets the search criteria, or you have no items left.";
                    return false;
                }

                string content = string.Empty;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Item <= 0)
                        continue;

                    var name = Util.GetItemString((int)items[i].Item);
                    content += $"**{name}**: {items[i].ItemCount}{(i + 1 < items.Count ? " | " : "")}";
                }

                result.EmbedName = item <= 0 ? $"{user.UserInfo.Username}'s Item List" : $"{user.UserInfo.Username}'s {Util.GetItemString((int)item)} List";
                result.Message = content;
                return true;
            }

            result.Success = FuncItemList();
            return result;
        }

        private Results ItemDropHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncItemDrop()
            {
                var item = TradeExtensions.EnumParse<TCItems>(input);
                if (input != "all" && item <= 0)
                {
                    result.Message = input == "" ? "Nothing specified to drop." : "Unrecognized item.";
                    return false;
                }

                var list = user.Items;
                List<TCItem> items = input switch
                {
                    "all" => list.FindAll(x => x.ItemCount >= 0),
                    _ => list.FindAll(x => x.Item == item && x.ItemCount > 0),
                };

                var count = items.Count;
                if (count == 0)
                {
                    result.Message = "Nothing found that meets the search criteria, or you have no items.";
                    return false;
                }

                List<int> idList = new();
                foreach (var entry in items)
                {
                    user.Items.Remove(entry);
                    idList.Add((int)entry.Item);
                }

                var names = new string[idList.Count + 1];
                var obj = new object[idList.Count + 1];
                names[0] = "@user_id";
                obj[0] = user.UserInfo.UserID;

                string questionM = string.Empty;
                for (int i = 0; i < idList.Count; i++)
                {
                    names[i + 1] = $"@id{i}";
                    obj[i + 1] = idList[i];
                    questionM += i + 1 == idList.Count ? "?" : "?, ";
                }

                result.SQLCommands.Add(DBCommandConstructor("items", "", $"where user_id = ? and id in ({questionM})", names, obj, SQLTableContext.Delete));

                result.Message = count > 1 ? "Dropped all items!" : $"Dropped all {Util.GetItemString((int)item)}{(items.First().ItemCount > 1 ? "s" : "")}!";
                return true;
            }

            result.Success = FuncItemDrop();
            result.User = user;
            return result;
        }

        private Results TimeZoneHandler(TCUser user, string input)
        {
            Results result = new();
            bool FuncTimeZone()
            {
                if (!int.TryParse(input, out int offset))
                {
                    result.Message = "Input must be a number (i.e. -2, 5...), or a zero.";
                    return false;
                }
                else if (offset < -12 || offset > 14)
                {
                    result.Message = "Invalid UTC time offset.";
                    return false;
                }

                result.Message = $"UTC time offset set to **{offset}**. Your current time should be **{DateTime.UtcNow.AddHours(offset)}**.";
                user.UserInfo.TimeZoneOffset = offset;

                var names = new string[] { "@time_offset", "@user_id" };
                var obj = new object[] { offset, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("users", "time_offset = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
                return true;
            }

            result.Success = FuncTimeZone();
            result.User = user;
            return result;
        }

        public Results DeleteUserData(string input)
        {
            Results result = new();
            result.EmbedName = "User Deletion";
            bool FuncDelete()
            {
                var id = ulong.Parse(input);
                var user = GetLookupAsClassObject<TCUserInfo>(id, "users");
                if (user.UserID == 0)
                    return false;

                var names = new string[] { "@user_id" };
                var obj = new object[] { id };
                result.SQLCommands.Add(DBCommandConstructor("users", "", "where user_id = ?", names, obj, SQLTableContext.Delete));
                UserDict.Remove(id);
                return true;
            }

            result.Success = FuncDelete();
            return result;
        }

        private void PerkBoostApplicator(TCUser user)
        {
            Util.Rng.SpeciesBoostRNG += user.Perks.ActivePerks.FindAll(x => x == DexPerks.SpeciesBoost).Count;
            Util.Rng.CatchRNG += user.Perks.ActivePerks.FindAll(x => x == DexPerks.CatchBoost).Count;
            Util.Rng.ItemRNG += user.Perks.ActivePerks.FindAll(x => x == DexPerks.ItemBoost).Count;
            Util.Rng.CherishRNG += user.Perks.ActivePerks.FindAll(x => x == DexPerks.CherishBoost).Count * 2;
            Util.Rng.GmaxRNG += user.Perks.ActivePerks.FindAll(x => x == DexPerks.GmaxBoost).Count * 2;

            var sc = user.Items.FirstOrDefault(x => x.Item == TCItems.ShinyCharm);
            double count = sc == default ? 0 : (double)sc.ItemCount / 2;
            Util.Rng.ShinyRNG += count;
            Util.Rng.EggShinyRNG += count;
        }

        private PK8 EggProcess(TCDaycare dc, EvoCriteria evo1, EvoCriteria evo2, int ball1, int ball2, string trainerInfo, out string msg)
        {
            msg = string.Empty;
            if (evo1.Species == 0 || evo2.Species == 0)
                return new();

            bool star = false, square = false;
            if (Util.Rng.EggShinyRNG + (dc.Shiny1 && dc.Shiny2 ? 5 : 0) >= 150 - Settings.SquareShinyRate)
                square = true;
            else if (Util.Rng.EggShinyRNG + (dc.Shiny1 && dc.Shiny2 ? 5 : 0) >= 150 - Settings.StarShinyRate)
                star = true;

            var pk = Util.EggRngRoutine(evo1, evo2, ball1, ball2, trainerInfo, star, square);
            var eggSpeciesName = SpeciesName.GetSpeciesNameGeneration(pk.Species, 2, 8);
            var eggForm = TradeCordHelperUtil.FormOutput(pk.Species, pk.Form, out _);
            var finalEggName = eggSpeciesName + eggForm;

            pk.ResetPartyStats();
            msg = $"&^&You got {(pk.IsShiny ? "a **shiny egg**" : "an egg")} from the daycare! Welcome, {(pk.IsShiny ? $"**{finalEggName}**" : $"{finalEggName}")}!";
            return pk;
        }

        private void BuddySystem(TCUser user, Results result, out string buddyMsg)
        {
            buddyMsg = string.Empty;
            if (user.Buddy.ID != 0)
            {
                var found = user.Catches.TryGetValue(user.Buddy.ID, out TCCatch match);
                if (!found || match.Traded)
                    return;

                var pk = GetLookupAsClassObject<PK8>(user.UserInfo.UserID, "binary_catches", $"and id = {match.ID}");
                if (pk.Species == 0)
                    return;

                if (pk.IsEgg)
                {
                    double status = (pk.CurrentFriendship - 5) / (double)pk.PersonalInfo.HatchCycles;
                    if (status > 0)
                        pk.CurrentFriendship -= 5;
                    else
                    {
                        buddyMsg = "\nUh-oh!... You've just hatched an egg!";
                        CommonEdits.ForceHatchPKM(pk);
                        pk.CurrentFriendship = pk.PersonalInfo.BaseFriendship;

                        var namesH = new string[] { "@name", "@user_id" };
                        var objH = new object[] { pk.Nickname, user.UserInfo.UserID };
                        result.SQLCommands.Add(DBCommandConstructor("buddy", "name = ?", "where user_id = ?", namesH, objH, SQLTableContext.Update));

                        namesH = new string[] { "@is_egg", "@user_id", "@id" };
                        objH = new object[] { 0, user.UserInfo.UserID, match.ID };
                        result.SQLCommands.Add(DBCommandConstructor("catches", "is_egg = ?", "where user_id = ? and id = ?", namesH, objH, SQLTableContext.Update));

                        user.Buddy.Nickname = pk.Nickname;
                        user.Catches[match.ID].Egg = false;
                    }
                }
                else if (pk.CurrentLevel < 100 && result.Poke.Species != 0)
                {
                    var enc = result.Poke;
                    var sootheBell = pk.HeldItem == 218 && pk.CurrentFriendship + 2 <= 255 ? 2 : 0;
                    var shiny = enc.IsShiny && pk.CurrentFriendship + 5 + sootheBell <= 255 ? 5 : 0;
                    pk.CurrentFriendship += sootheBell + shiny;

                    int levelOld = pk.CurrentLevel;
                    var xpMin = Experience.GetEXP(pk.CurrentLevel + 1, pk.PersonalInfo.EXPGrowth);
                    var calc = enc.PersonalInfo.BaseEXP * enc.CurrentLevel / 5.0 * Math.Pow((2.0 * enc.CurrentLevel + 10.0) / (enc.CurrentLevel + pk.CurrentLevel + 10.0), 2.5);
                    var bonus = enc.IsShiny ? 1.1 : 1.0;
                    var xpGet = (uint)Math.Round(calc * bonus, 0, MidpointRounding.AwayFromZero);
                    if (xpGet < 100)
                        xpGet = 175;

                    pk.EXP += xpGet;
                    while (pk.EXP >= Experience.GetEXP(pk.CurrentLevel + 1, pk.PersonalInfo.EXPGrowth) && pk.CurrentLevel < 100)
                        pk.CurrentLevel++;

                    if (pk.CurrentLevel == 100)
                        pk.EXP = xpMin;

                    if (pk.EXP >= xpMin)
                    {
                        buddyMsg = $"\n{user.Buddy.Nickname} gained {xpGet} EXP and leveled up to level {pk.CurrentLevel}!";
                        if (pk.CurrentFriendship < 255)
                        {
                            var delta = pk.CurrentLevel - levelOld;
                            for (int i = 0; i < delta; i++)
                            {
                                if (pk.CurrentFriendship + 2 >= 255)
                                {
                                    pk.CurrentFriendship = 255;
                                    break;
                                }
                                pk.CurrentFriendship += 2;
                            }
                        }
                    }
                    else buddyMsg = $"\n{user.Buddy.Nickname} gained {xpGet} EXP!";
                }

                var names = new string[] { "@data", "@user_id", "@id" };
                var obj = new object[] { pk.DecryptedPartyData, user.UserInfo.UserID, match.ID };
                result.SQLCommands.Add(DBCommandConstructor("binary_catches", "data = ?", "where user_id = ? and id = ?", names, obj, SQLTableContext.Update));
                result.User = user;
            }
        }

        public string DexCount(TCUser user, Results result, int species, string gifteeName = "")
        {
            if (user.Dex.DexCompletionCount >= 20)
                return "";

            bool entry = !user.Dex.Entries.Contains(species);
            if (entry)
            {
                user.Dex.Entries.Add(species);
                string entryStr = Util.ArrayStringify(user.Dex.Entries.ToArray());
                string[] names = new string[] { "@entries", "@user_id" };
                var obj = new object[] { entryStr, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("dex", "entries = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
            }

            string msg = gifteeName != "" && entry ? $"\n{gifteeName} registered a new entry to the Pokédex!" : entry ? "\nRegistered to the Pokédex." : "";
            if (user.Dex.Entries.Count >= 664 && user.Dex.DexCompletionCount < 20)
            {
                user.Dex.Entries.Clear();
                user.Dex.DexCompletionCount += 1;
                bool sc = false;
                if (user.Dex.DexCompletionCount == 1 && user.Items.FirstOrDefault(x => x.Item == TCItems.ShinyCharm) == default)
                {
                    sc = true;
                    user.Items.Add(new() { Item = TCItems.ShinyCharm, ItemCount = 1 });
                    var namesI = ItemsValues.Replace(" ", "").Split(',');
                    var objI = new object[] { user.UserInfo.UserID, (int)TCItems.ShinyCharm, 1 };
                    result.SQLCommands.Add(DBCommandConstructor("items", ItemsValues, "", namesI, objI, SQLTableContext.Insert));
                }

                msg += user.Dex.DexCompletionCount < 20 ? $" Level increased!{(sc ? " Received a ★**Shiny Charm**★" : "")}" : " Highest level achieved!";
                string[] names = new string[] { "@entries", "@dex_count", "@user_id" };
                var obj = new object[] { string.Empty, user.Dex.DexCompletionCount, user.UserInfo.UserID };
                result.SQLCommands.Add(DBCommandConstructor("dex", "entries = ?, dex_count = ?", "where user_id = ?", names, obj, SQLTableContext.Update));
            }
            result.User = user;
            return msg;
        }

        private void CatchRegister(Results result, PK8 pk, out int index)
        {
            var speciesName = SpeciesName.GetSpeciesNameGeneration(pk.Species, 2, 8);
            var form = TradeCordHelperUtil.FormOutput(pk.Species, pk.Form, out _);
            bool isLegend = Util.IsLegendaryOrMythical(speciesName);
            if (speciesName.Contains("Nidoran"))
            {
                speciesName = speciesName.Remove(speciesName.Length - 1);
                form = pk.Species == (int)Species.NidoranF ? "-F" : "-M";
            }

            int[] array = result.User.Catches.Select(x => x.Value.ID).ToArray();
            array = array.OrderBy(x => x).ToArray();
            index = Util.Indexing(array);
            result.User.Catches.Add(index, new() { Species = speciesName, Nickname = pk.Nickname, Ball = $"{(Ball)pk.Ball}", Egg = pk.IsEgg, Form = form, ID = index, Shiny = pk.IsShiny, Traded = false, Favorite = false, Legendary = isLegend, Event = pk.FatefulEncounter });

            var names = CatchValues.Replace(" ", "").Split(',');
            var obj = new object[] { result.User.UserInfo.UserID, index, pk.IsShiny, $"{(Ball)pk.Ball}", pk.Nickname, speciesName, form, pk.IsEgg, false, false , isLegend, pk.FatefulEncounter };
            result.SQLCommands.Add(DBCommandConstructor("catches", CatchValues, "", names, obj, SQLTableContext.Insert));

            names = BinaryCatchesValues.Replace(" ", "").Split(',');
            obj = new object[] { result.User.UserInfo.UserID, index, pk.DecryptedPartyData };
            result.SQLCommands.Add(DBCommandConstructor("binary_catches", BinaryCatchesValues, "", names, obj, SQLTableContext.Insert));
        }

        private TCUser HandleNewCatches(Results result)
        {
            if (result.Shedinja != null)
            {
                CatchRegister(result, result.Shedinja, out int shedinjaID);
                var shedStr = $"\n\nA spare Poké Ball in your bag clicks quietly... You also caught {(result.Shedinja.IsShiny ? "**Shedinja**" : "Shedinja")} (ID: {shedinjaID})!";
                shedStr += DexCount(result.User, result, result.Shedinja.Species);
                result.Message += shedStr;
            }

            if (result.Poke.Species != 0)
            {
                CatchRegister(result, result.Poke, out int pokeID);
                result.PokeID = pokeID;
            }

            if (result.EggPoke.Species != 0)
            {
                CatchRegister(result, result.EggPoke, out int eggID);
                result.EggPokeID = eggID;
            }
            return result.User;
        }

        public string GetDexFlavorText(int species, int form, bool gmax) => GetDexFlavorFromTable(species, form, gmax);

        private SQLCommand DBCommandConstructor(string table, string vals, string filter, string[] names, object[] values, SQLTableContext ctx)
        {
            string cmd = ctx switch
            {
                SQLTableContext.Update => $"update {table} set {vals} {filter}",
                SQLTableContext.Insert => $"insert into {table} values ({vals}) {filter}",
                SQLTableContext.Delete => $"PRAGMA foreign_keys = ON;delete from {table} {filter}",
                SQLTableContext.Select => $"select * from {table} {filter}",
                _ => "",
            };

            return new()
            {
                CommandText = cmd,
                Names = names,
                Values = values,
            };
        }
    }
}