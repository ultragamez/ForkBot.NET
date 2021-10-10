using System;
using System.IO;
using System.Linq;
using System.Data.SQLite;
using System.Collections.Generic;
using System.Threading;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace SysBot.Pokemon
{
    public abstract class TradeCordDatabase<T> : TradeCordUserBase where T : PKM, new()
    {
        private static readonly string DatabasePath = "TradeCord/TradeCordDB_SWSH.db";
        private static SQLiteConnection Connection = new();

        private static readonly string UsersValues = "@user_id, @username, @catch_count, @time_offset, @last_played";
        private static readonly string TrainerInfoValues = "@user_id, @ot, @ot_gender, @tid, @sid, @language";
        private static readonly string DexValues = "@user_id, @dex_count, @entries";
        private static readonly string PerksValues = "@user_id, @perks, @species_boost";
        private static readonly string DaycareValues = "@user_id, @shiny1, @id1, @species1, @form1, @ball1, @shiny2, @id2, @species2, @form2, @ball2";
        private static readonly string BuddyValues = "@user_id, @id, @name, @ability";
        protected static readonly string ItemsValues = "@user_id, @id, @count";
        protected static readonly string CatchValues = "@user_id, @id, @is_shiny, @ball, @nickname, @species, @form, @is_egg, @is_favorite, @was_traded, @is_legendary, @is_event";
        protected static readonly string BinaryCatchesValues = "@user_id, @id, @data";

        private readonly string[] TableCreateCommands =
        {
            "create table if not exists users(user_id integer primary key, username text not null, catch_count int default 0, time_offset int default 0, last_played text default '')",
            "create table if not exists trainerinfo(user_id integer, ot text default 'Carp', ot_gender text default 'Male', tid int default 12345, sid int default 54321, language text default 'English', foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists dex(user_id integer, dex_count int default 0, entries text default '', foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists dex_flavor(species integer primary key, base text default '', gmax text default '', form1 text default '', form2 text default '', form3 text default '', form4 text default '', form5 text default '', form6 text default '', form7 text default '', form8 text default '', form9 text default '', form10 text default '', form11 text default '', form12 text default '', form13 text default '', form14 text default '', form15 text default '', form16 text default '', form17 text default '')",
            "create table if not exists perks(user_id integer, perks text default '', species_boost int default 0, foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists daycare(user_id integer, shiny1 int default 0, id1 int default 0, species1 int default 0, form1 text default '', ball1 int default 0, shiny2 int default 0, id2 int default 0, species2 int default 0, form2 text default '', ball2 int default 0, foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists buddy(user_id integer, id int default 0, name text default '', ability int default 0, foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists items(user_id integer, id int default 0, count int default 0, foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists catches(user_id integer, id int default 0, is_shiny int default 0, ball text default '', nickname text default '', species text default '', form text default '', is_egg int default 0, is_favorite int default 0, was_traded int default 0, is_legendary int default 0, is_event int default 0, foreign key (user_id) references users (user_id) on delete cascade)",
            "create table if not exists binary_catches(user_id integer, id int default 0, data blob default null, foreign key (user_id) references users (user_id) on delete cascade)",
        };

        private readonly string[] TableInsertCommands =
        {
            $"insert into users(user_id, username, catch_count, time_offset, last_played) values({UsersValues})",
            $"insert into trainerinfo(user_id, ot, ot_gender, tid, sid, language) values({TrainerInfoValues})",
            $"insert into dex(user_id, dex_count, entries) values({DexValues})",
            $"insert into perks(user_id, perks, species_boost) values({PerksValues})",
            $"insert into daycare(user_id, shiny1, id1, species1, form1, ball1, shiny2, id2, species2, form2, ball2) values({DaycareValues})",
            $"insert into buddy(user_id, id, name, ability) values({BuddyValues})",
            $"insert into items(user_id, id, count) values({ItemsValues})",
            $"insert into catches(user_id, id, is_shiny, ball, nickname, species, form, is_egg, is_favorite, was_traded, is_legendary, is_event) values({CatchValues})",
        };

        private readonly string[] IndexCreateCommands =
        {
            "create index if not exists catch_index on catches(user_id, id, ball, nickname, species, form, is_shiny, is_egg, is_favorite, was_traded, is_legendary, is_event)",
            "create index if not exists item_index on items(user_id, id)",
            "create index if not exists binary_catches_index on binary_catches(user_id, id)",
        };

        private static bool Connected { get; set; }

        public sealed class TCUser
        {
            public TCUserInfo UserInfo { get; set; } = new();
            public TCTrainerInfo TrainerInfo { get; set; } = new();
            public TCDaycare Daycare { get; set; } = new();
            public TCBuddy Buddy { get; set; } = new();
            public TCDex Dex { get; set; } = new();
            public TCPerks Perks { get; set; } = new();
            public List<TCItem> Items { get; set; } = new();
            public Dictionary<int, TCCatch> Catches { get; set; } = new();
        }

        public sealed class SQLCommand
        {
            public string CommandText { get; set; } = string.Empty;
            public string[]? Names { get; set; } = null;
            public object[]? Values { get; set; } = null;
        }

        protected bool Initialize()
        {
            if (!Connected)
            {
                try
                {
                    Connection = new($"Data Source={DatabasePath};Version=3;");
                    Connection.Open();
                    Connected = true;
                }
                catch (Exception)
                {
                    Connection.Dispose();
                    Connected = false;
                    return false;
                }
            }
            return Connected;
        }

        protected TCUser GetCompleteUser(ulong id, string name, bool gift = false)
        {
            TCUser user = new();
            user.UserInfo = GetLookupAsClassObject<TCUserInfo>(id, "users");
            if (user.UserInfo.UserID == 0)
            {
                user.UserInfo.UserID = id;
                user.UserInfo.Username = name;
                InitializeNewUser(id, name);
            }

            user.TrainerInfo = GetLookupAsClassObject<TCTrainerInfo>(id, "trainerinfo");
            user.Buddy = GetLookupAsClassObject<TCBuddy>(id, "buddy");
            user.Daycare = GetLookupAsClassObject<TCDaycare>(id, "daycare");
            user.Items = GetLookupAsClassObject<List<TCItem>>(id, "items");
            user.Dex = GetLookupAsClassObject<TCDex>(id, "dex");
            user.Perks = GetLookupAsClassObject<TCPerks>(id, "perks");
            user.Catches = GetLookupAsClassObject<Dictionary<int, TCCatch>>(id, "catches");

            if (!gift)
            {
                user.UserInfo.LastPlayed = DateTime.Now;
                UpdateRows(id, "users", $"last_played = '{DateTime.Now}'{(name != "" && name != user.UserInfo.Username ? $", username = '{name}'" : "")}");
            }
            return user;
        }

        protected A GetLookupAsClassObject<A>(ulong id, string table, string filter = "", bool tableJoin = false)
        {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = $"select * from {table} where {(tableJoin ? "c." : "")}user_id = {id} {filter}";
            if (tableJoin)
                table = table.Split(' ')[0];

            using SQLiteDataReader reader = cmd.ExecuteReader();
            object returnObj = table switch
            {
                "dex" => DexReader(reader),
                "perks" => PerkReader(reader),
                "buddy" => BuddyReader(reader),
                "daycare" => DaycareReader(reader),
                "trainerinfo" => TrainerInfoReader(reader),
                "users" => UserInfoReader(reader),
                "items" => ItemReader(reader),
                "catches" => CatchReader(reader, id),
                "binary_catches" => CatchPKMReader(reader),
                _ => throw new NotImplementedException(),
            };
            return (A)returnObj;
        }

        protected void ProcessBulkCommands(List<SQLCommand> cmds, bool delete = false)
        {
            if (delete)
            {
                var cmd = Connection.CreateCommand();
                cmd.CommandText = cmds[0].CommandText;
                var parameters = ParameterConstructor(cmds[0].Names, cmds[0].Values);
                cmd.Parameters.AddRange(parameters);
                cmd.ExecuteNonQuery();
                return;
            }

            using var tran = Connection.BeginTransaction();
            for (int i = 0; i < cmds.Count; i++)
            {
                var cmd = Connection.CreateCommand();
                cmd.Transaction = tran;
                cmd.CommandText = cmds[i].CommandText;
                var parameters = ParameterConstructor(cmds[i].Names, cmds[i].Values);
                cmd.Parameters.AddRange(parameters);
                cmd.ExecuteNonQuery();
            }
            tran.Commit();
        }

        private SQLiteParameter[] ParameterConstructor(string[]? parameters, object[]? values)
        {
            if (parameters == null || values == null)
                throw new ArgumentNullException();

            SQLiteParameter[] sqParams = new SQLiteParameter[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                sqParams[i] = new() { ParameterName = parameters[i], Value = values[i] };
            return sqParams;
        }

        protected string GetDexFlavorFromTable(int species, int form, bool gmax)
        {
            var cmd = Connection.CreateCommand();
            var selection = gmax ? "gmax" : form == 0 ? "base" : $"form{form}";
            cmd.CommandText = $"select {selection} from dex_flavor where species = {species}";
            using SQLiteDataReader reader = cmd.ExecuteReader();
            if (reader.Read())
                return reader[selection].ToString();
            return "";
        }

        private void InitializeNewUser(ulong id, string name)
        {
            using var tran = Connection.BeginTransaction();
            var cmd = Connection.CreateCommand();
            cmd.CommandText = $"insert into users (user_id, username, last_played) values (@user_id, @username, @last_played)";
            cmd.Parameters.AddWithValue("@user_id", id);
            cmd.Parameters.AddWithValue("@username", name);
            cmd.Parameters.AddWithValue("@last_played", $"{DateTime.Now}");
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"insert into trainerinfo(user_id) values({id})";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"insert into dex(user_id) values({id})";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"insert into perks(user_id) values({id})";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"insert into daycare(user_id) values({id})";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"insert into buddy(user_id) values({id})";
            cmd.ExecuteNonQuery();
            tran.Commit();
        }

        protected static void UpdateRows(ulong id, string table, string values, string filter = "")
        {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = $"update {table} set {values} where user_id = {id} {filter}";
            cmd.ExecuteNonQuery();
        }

        protected static void RemoveRows(ulong id, string table, string filter = "")
        {
            var cmd = Connection.CreateCommand();
            cmd.CommandText = $"delete from {table} where user_id = {id} {filter}";
            cmd.ExecuteNonQuery();
        }

        private Dictionary<int, TCCatch> CatchReader(SQLiteDataReader reader, ulong id)
        {
            Dictionary<int, TCCatch> catches = new();
            while (reader.Read())
            {
                TCCatch entry = new();
                entry.ID = (int)reader["id"];
                entry.Shiny = (int)reader["is_shiny"] != 0;
                entry.Ball = reader["ball"].ToString();
                entry.Nickname = reader["nickname"].ToString();
                entry.Species = reader["species"].ToString();
                entry.Form = reader["form"].ToString();
                entry.Egg = (int)reader["is_egg"] != 0;
                entry.Favorite = (int)reader["is_favorite"] != 0;
                entry.Traded = (int)reader["was_traded"] != 0;
                entry.Legendary = (int)reader["is_legendary"] != 0;
                entry.Event = (int)reader["is_event"] != 0;
                try
                {
                    catches.Add(entry.ID, entry);
                }
                catch
                {
                    Base.LogUtil.LogError("Duplicate entry found, removing...", "[SQL Catch Reader]");
                    RemoveRows(id, "catches", $"and id = {entry.ID}");
                    RemoveRows(id, "binary_catches", $"and id = {entry.ID}");
                }
            }
            return catches;
        }

        private T CatchPKMReader(SQLiteDataReader reader)
        {
            T? pk = null;
            if (reader.Read())
                pk = (T?)PKMConverter.GetPKMfromBytes((byte[])reader["data"]);
            return pk ?? new();
        }

        private List<TCItem> ItemReader(SQLiteDataReader reader)
        {
            List<TCItem> items = new();
            while (reader.Read())
            {
                TCItem item = new();
                item.Item = (TCItems)reader["id"];
                item.ItemCount = (int)reader["count"];
                items.Add(item);
            }
            return items;
        }

        private TCPerks PerkReader(SQLiteDataReader reader)
        {
            TCPerks perks = new();
            if (reader.Read())
            {
                var perkArr = reader["perks"].ToString().Split(',');
                var boost = (int)reader["species_boost"];
                if (perkArr[0] != "")
                {
                    for (int i = 0; i < perkArr.Length; i++)
                        perks.ActivePerks.Add((DexPerks)int.Parse(perkArr[i]));
                }
                perks.SpeciesBoost = boost;
            }
            return perks;
        }

        private TCBuddy BuddyReader(SQLiteDataReader reader)
        {
            TCBuddy buddy = new();
            if (reader.Read())
            {
                buddy.ID = (int)reader["id"];
                buddy.Nickname = reader["name"].ToString();
                buddy.Ability = (Ability)(int)reader["ability"];
            }
            return buddy;
        }

        private TCDex DexReader(SQLiteDataReader reader)
        {
            TCDex dex = new();
            if (reader.Read())
            {
                var dexEntries = reader["entries"].ToString().Split(',');
                var count = (int)reader["dex_count"];
                if (dexEntries[0] != "")
                {
                    for (int i = 0; i < dexEntries.Length; i++)
                        dex.Entries.Add(int.Parse(dexEntries[i]));
                }
                dex.DexCompletionCount = count;
            }
            return dex;
        }

        private TCTrainerInfo TrainerInfoReader(SQLiteDataReader reader)
        {
            TCTrainerInfo info = new();
            if (reader.Read())
            {
                info.OTName = reader["ot"].ToString();
                info.OTGender = reader["ot_gender"].ToString();
                info.TID = (int)reader["tid"];
                info.SID = (int)reader["sid"];
                info.Language = reader["language"].ToString();
            }
            return info;
        }

        private TCDaycare DaycareReader(SQLiteDataReader reader)
        {
            TCDaycare dc = new();
            if (reader.Read())
            {
                dc.ID1 = (int)reader["id1"];
                dc.Species1 = (int)reader["species1"];
                dc.Form1 = reader["form1"].ToString();
                dc.Ball1 = (int)reader["ball1"];
                dc.Shiny1 = (int)reader["shiny1"] != 0;

                dc.ID2 = (int)reader["id2"];
                dc.Species2 = (int)reader["species2"];
                dc.Form2 = reader["form2"].ToString();
                dc.Ball2 = (int)reader["ball2"];
                dc.Shiny2 = (int)reader["shiny2"] != 0;
            }
            return dc;
        }

        private TCUserInfo UserInfoReader(SQLiteDataReader reader)
        {
            TCUserInfo info = new();
            if (reader.Read())
            {
                info.UserID = ulong.Parse(reader["user_id"].ToString());
                info.Username = reader["username"].ToString();
                info.CatchCount = (int)reader["catch_count"];
                info.TimeZoneOffset = (int)reader["time_offset"];
                info.LastPlayed = DateTime.Parse(reader["last_played"].ToString());
            }
            return info;
        }

        protected static void CleanDatabase()
        {
            try
            {
                TradeCordHelper<T>.VacuumLock = true;
                Thread.Sleep(0_500);
                var path = "TradeCord/TradeCordDB_SWSH_backup.db";
                var path2 = "TradeCord/TradeCordDB_SWSH_backup2.db";
                if (File.Exists(path))
                {
                    File.Copy(path, path2, true);
                    File.Delete(path);
                }

                var cmd = Connection.CreateCommand();
                cmd.CommandText = $"vacuum main into '{path}'";
                cmd.ExecuteNonQuery();
                Connection.Dispose();
                Connected = false;

                File.Copy(path, DatabasePath, true);
                if (File.Exists(path2))
                    File.Delete(path2);
            }
            catch (Exception ex)
            {
                Base.LogUtil.LogError($"Failed to vacuum and back up the database.\n{ex.Message}\n{ex.InnerException}\n{ex.StackTrace}", "[SQLite]");
                Connection.Dispose();
                Connected = false;
            }
        }

        protected void ClearInactiveUsers()
        {
            List<ulong> ids = new();
            var cmd = Connection.CreateCommand();
            cmd.CommandText = $"select * from users";

            Base.EchoUtil.Echo("Checking for inactive TradeCord users...");
            using SQLiteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var date = DateTime.Parse(reader["last_played"].ToString());
                var id = ulong.Parse(reader["user_id"].ToString());
                if (DateTime.Now.Subtract(date).TotalDays >= 30)
                    ids.Add(id);
            }
            reader.Close();

            if (ids.Count > 0)
            {
                var users = string.Join(",", ids);
                cmd.CommandText = $"PRAGMA foreign_keys = ON;delete from users where user_id in ({users})";
                cmd.ExecuteNonQuery();
                Base.EchoUtil.Echo($"Removed {ids.Count} inactive TradeCord users.");
            }
            else Base.EchoUtil.Echo("No inactive TradeCord users found to remove.");
        }

        protected bool CreateDB()
        {
            bool exists = File.Exists(DatabasePath);
            if (!Initialize())
                return false;

            if (!exists)
            {
                Base.LogUtil.LogInfo("Beginning to migrate TradeCord to SQLite...", "[SQLite]");
                using var tran = Connection.BeginTransaction();
                var cmd = Connection.CreateCommand();
                for (int i = 0; i < TableCreateCommands.Length; i++)
                {
                    cmd.CommandText = TableCreateCommands[i];
                    cmd.ExecuteNonQuery();
                }

                for (int i = 0; i < 900; i++)
                {
                    if (!Enum.IsDefined(typeof(Gen8Dex), i))
                        continue;

                    cmd.CommandText = "insert into dex_flavor(species) values(@species)";
                    cmd.Parameters.AddWithValue("@species", i);
                    cmd.ExecuteNonQuery();
                    TradeCordHelperUtil<T>.FormOutput(i, 0, out string[] forms);

                    for (int f = 0; f < forms.Length; f++)
                    {
                        var name = SpeciesName.GetSpeciesNameGeneration(i, 2, 8);
                        bool gmax = new ShowdownSet($"{name}{forms[f]}").CanToggleGigantamax(i, f);
                        string gmaxFlavor = gmax ? DexText(i, f, true) : "";
                        string flavor = DexText(i, f, false);

                        var vals = gmax && f > 0 ? $"set gmax = '{gmaxFlavor}', form{f} = '{flavor}'" : gmax ? $"set base = '{flavor}', gmax = '{gmaxFlavor}'" : f == 0 ? $"set base = '{flavor}'" : $"set form{f} = '{flavor}'";
                        cmd.CommandText = $"update dex_flavor {vals} where species = {i}";
                        cmd.ExecuteNonQuery();
                    }
                }

                if (!File.Exists("TradeCord/UserInfo.json"))
                {
                    for (int i = 0; i < IndexCreateCommands.Length; i++)
                    {
                        cmd = Connection.CreateCommand();
                        cmd.Transaction = tran;
                        cmd.CommandText = IndexCreateCommands[i];
                        cmd.ExecuteNonQuery();
                    }
                }
                tran.Commit();
            }

            if (File.Exists("TradeCord/UserInfo.json"))
            {
                if (!MigrateToDB())
                {
                    Connection.Dispose();
                    Connected = false;
                    return false;
                }
            }

            if (typeof(T) == typeof(PK8))
            {
                var cmd = Connection.CreateCommand();
                cmd.CommandText = "create table if not exists legality_fix(issue text not null, fixed int default 0)";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "insert into legality_fix(issue,fixed) select 'ht_var', 0 where not exists(select 1 from legality_fix where issue = 'ht_var')";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "insert into legality_fix(issue,fixed) select 'egg_bug', 0 where not exists(select 1 from legality_fix where issue = 'egg_bug')";
                cmd.ExecuteNonQuery();

                bool wasFixedHT = false;
                cmd.CommandText = "select * from legality_fix where issue = 'ht_var'";
                var reader = cmd.ExecuteReader();
                if (reader.Read())
                    wasFixedHT = (int)reader["fixed"] == 1;
                reader.Close();

                if (!wasFixedHT)
                    LegalityFixPK8();

                bool wasFixedEgg = false;
                cmd.CommandText = "select * from legality_fix where issue = 'egg_bug'";
                reader = cmd.ExecuteReader();
                if (reader.Read())
                    wasFixedEgg = (int)reader["fixed"] == 1;
                reader.Close();

                if (!wasFixedEgg)
                    EggBug();
            }

            return true;
        }

        private bool MigrateToDB()
        {
            try
            {
                var users = GetRoot<TCUserInfoRoot>("TradeCord/UserInfo.json").Users;
                using var tran = Connection.BeginTransaction();
                for (int u = 0; u < users.Count; u++)
                {
                    var dir = $"TradeCord\\{users[u].UserID}";
                    if (users[u].Username == "" || users[u].UserID == 0)
                        continue;

                    string dexStr = "";
                    var dexArr = users[u].Dex.ToArray();
                    for (int i = 0; i < dexArr.Length; i++)
                        dexStr += $"{dexArr[i]}{(i + 1 < dexArr.Length ? "," : "")}";

                    string perkStr = "";
                    for (int i = 0; i < users[u].ActivePerks.Count; i++)
                        perkStr += $"{(int)users[u].ActivePerks[i]}{(i + 1 < users[u].ActivePerks.Count ? "," : "")}";

                    string favStr = "";
                    var favArr = users[u].Favorites.ToArray();
                    for (int i = 0; i < favArr.Length; i++)
                        favStr += $"{favArr[i]}{(i + 1 < favArr.Length ? "," : "")}";

                    var itemList = users[u].Items.ToList();
                    if (users[u].DexCompletionCount >= 1 && itemList.FirstOrDefault(x => x.Item == TCItems.ShinyCharm) == default)
                        itemList.Add(new() { Item = TCItems.ShinyCharm, ItemCount = 1 });

                    var cmd = Connection.CreateCommand();
                    cmd.Transaction = tran;
                    for (int i = 0; i < TableInsertCommands.Length; i++)
                    {
                        var enumType = (TableEnum)i;
                        switch (enumType)
                        {
                            case TableEnum.Users:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@username", users[u].Username);
                                    cmd.Parameters.AddWithValue("@catch_count", users[u].CatchCount);
                                    cmd.Parameters.AddWithValue("@time_offset", users[u].TimeZoneOffset);
                                    cmd.Parameters.AddWithValue("@last_played", $"{DateTime.Now}");
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.TrainerInfo:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@ot", users[u].OTName == "" ? "Carp" : users[u].OTName);
                                    cmd.Parameters.AddWithValue("@ot_gender", users[u].OTGender == "" ? "Male" : users[u].OTGender);
                                    cmd.Parameters.AddWithValue("@tid", users[u].TID);
                                    cmd.Parameters.AddWithValue("@sid", users[u].SID);
                                    cmd.Parameters.AddWithValue("@language", users[u].Language == "" ? "English" : users[u].Language);
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.Dex:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@dex_count", users[u].DexCompletionCount);
                                    cmd.Parameters.AddWithValue("@entries", dexStr);
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.Perks:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@perks", perkStr);
                                    cmd.Parameters.AddWithValue("@species_boost", users[u].SpeciesBoost);
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.Daycare:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@shiny1", users[u].Daycare1.Shiny);
                                    cmd.Parameters.AddWithValue("@id1", users[u].Daycare1.ID);
                                    cmd.Parameters.AddWithValue("@species1", users[u].Daycare1.Species);
                                    cmd.Parameters.AddWithValue("@form1", users[u].Daycare1.Form);
                                    cmd.Parameters.AddWithValue("@ball1", users[u].Daycare1.Ball);

                                    cmd.Parameters.AddWithValue("@shiny2", users[u].Daycare2.Shiny);
                                    cmd.Parameters.AddWithValue("@id2", users[u].Daycare2.ID);
                                    cmd.Parameters.AddWithValue("@species2", users[u].Daycare2.Species);
                                    cmd.Parameters.AddWithValue("@form2", users[u].Daycare2.Form);
                                    cmd.Parameters.AddWithValue("@ball2", users[u].Daycare2.Ball);
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.Buddy:
                                {
                                    cmd.CommandText = TableInsertCommands[i];
                                    cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                    cmd.Parameters.AddWithValue("@id", users[u].Buddy.ID);
                                    cmd.Parameters.AddWithValue("@name", users[u].Buddy.Nickname);
                                    cmd.Parameters.AddWithValue("@ability", users[u].Buddy.Ability);
                                    cmd.ExecuteNonQuery();
                                }; break;
                            case TableEnum.Items:
                                {
                                    if (itemList.Count > 0)
                                    {
                                        for (int it = 0; it < itemList.Count; it++)
                                        {
                                            if (itemList[it].Item == TCItems.ShinyCharm)
                                                continue;

                                            cmd.CommandText = TableInsertCommands[i];
                                            cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                            cmd.Parameters.AddWithValue("@id", (int)itemList[it].Item);
                                            cmd.Parameters.AddWithValue("@count", itemList[it].ItemCount);
                                            cmd.ExecuteNonQuery();
                                        }
                                    }

                                    var sc = itemList.FirstOrDefault(x => x.Item == TCItems.ShinyCharm);
                                    var count = users[u].DexCompletionCount + users[u].ActivePerks.Count + (sc != default ? sc.ItemCount : 0);
                                    if (count > 0)
                                    {
                                        cmd.CommandText = TableInsertCommands[i];
                                        cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                        cmd.Parameters.AddWithValue("@id", (int)TCItems.ShinyCharm);
                                        cmd.Parameters.AddWithValue("@count", count);
                                        cmd.ExecuteNonQuery();
                                    }
                                }; break;
                            case TableEnum.Catches:
                                {
                                    var catches = users[u].Catches.ToList();
                                    if (catches.Count > 0)
                                    {
                                        for (int c = 0; c < catches.Count; c++)
                                        {
                                            bool dupe = catches.FindAll(x => x.ID == catches[c].ID).Count > 1;
                                            if (dupe)
                                            {
                                                var array = Directory.GetFiles(dir).Where(x => x.Contains(".pk")).Select(x => int.Parse(x.Split('\\')[2].Split('-', '_')[0].Replace("★", "").Trim())).ToArray();
                                                array = array.OrderBy(x => x).ToArray();
                                                catches[c].ID = new TradeCordHelperUtil<T>().Indexing(array);
                                            }

                                            PK8? pk = null;
                                            if (File.Exists(catches[c].Path))
                                                pk = (PK8?)PKMConverter.GetPKMfromBytes(File.ReadAllBytes(catches[c].Path));
                                            if (pk == null)
                                                continue;

                                            cmd.CommandText = "insert into binary_catches(user_id, id, data) values(@user_id, @id, @data)";
                                            cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                            cmd.Parameters.AddWithValue("@id", catches[c].ID);
                                            cmd.Parameters.AddWithValue("@data", pk.DecryptedPartyData);
                                            cmd.ExecuteNonQuery();

                                            cmd.CommandText = TableInsertCommands[i];
                                            cmd.Parameters.AddWithValue("@user_id", users[u].UserID);
                                            cmd.Parameters.AddWithValue("@is_shiny", catches[c].Shiny);
                                            cmd.Parameters.AddWithValue("@id", catches[c].ID);
                                            cmd.Parameters.AddWithValue("@ball", catches[c].Ball);
                                            cmd.Parameters.AddWithValue("@nickname", pk.Nickname);
                                            cmd.Parameters.AddWithValue("@species", catches[c].Species);
                                            cmd.Parameters.AddWithValue("@form", catches[c].Form);
                                            cmd.Parameters.AddWithValue("@is_egg", catches[c].Egg);
                                            cmd.Parameters.AddWithValue("@is_favorite", favArr.Contains(catches[c].ID));
                                            cmd.Parameters.AddWithValue("@was_traded", catches[c].Traded);
                                            cmd.Parameters.AddWithValue("@is_legendary", Enum.IsDefined(typeof(Legends), SpeciesName.GetSpeciesID(catches[c].Species)));
                                            cmd.Parameters.AddWithValue("@is_event", pk.FatefulEncounter);
                                            cmd.ExecuteNonQuery();
                                        }
                                    }
                                }; break;
                            default: throw new NotImplementedException();
                        };
                    }
                }

                for (int i = 0; i < IndexCreateCommands.Length; i++)
                {
                    var cmd = Connection.CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandText = IndexCreateCommands[i];
                    cmd.ExecuteNonQuery();
                }
                tran.Commit();

                var dirs = Directory.GetDirectories("TradeCord");
                for (int i = 0; i < dirs.Length; i++)
                    Directory.Delete(dirs[i], true);

                if (File.Exists("TradeCord/UserInfo_sqlbackup.json"))
                    File.Delete("TradeCord/UserInfo_sqlbackup.json");
                File.Move("TradeCord/UserInfo.json", "TradeCord/UserInfo_sqlbackup.json");
                return true;
            }
            catch (Exception ex)
            {
                Base.LogUtil.LogError($"Failed to migrate database.\n{ex.Message}\n{ex.InnerException}\n{ex.StackTrace}", "[SQLite Migration]");
                return false;
            }
        }

        private string DexText(int species, int form, bool gmax)
        {
            if (FormInfo.IsBattleOnlyForm(species, form, 8) || FormInfo.IsTotemForm(species, form, 8))
                return "";

            var resourcePath = "SysBot.Pokemon.TradeCord.Resources.DexFlavor.txt";
            using StreamReader reader = new(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath));

            if (Enum.IsDefined(typeof(Foreign), species) && form > 0)
                form = 0;

            if (!gmax)
            {
                if (form > 0)
                    return reader.ReadToEnd().Split('_')[1].Split('\n')[species].Split('|')[species == 80 && form == 2 ? 0 : form - 1].Replace("'", "''");
                else return reader.ReadToEnd().Split('\n')[species].Replace("'", "''");
            }

            string[] str = reader.ReadToEnd().Split('_')[1].Split('\n')[species].Split('|');
            return str[^1].Replace("'", "''");
        }

        private void LegalityFixPK8()
        {
            Base.EchoUtil.Echo("Beginning to scan for and fix legality errors. This may take a while.");
            int updated = 0;
            List<SQLCommand> cmds = new();
            var cmd = Connection.CreateCommand();
            cmd.CommandText = "select * from binary_catches";

            var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                bool write = false;
                ulong user_id = ulong.Parse(reader["user_id"].ToString());
                int catch_id = (int)reader["id"];
                PK8 pk = (PK8?)PKMConverter.GetPKMfromBytes((byte[])reader["data"]) ?? new();

                var la = new LegalityAnalysis(pk);
                if (!la.Valid)
                {
                    var sav = new SimpleTrainerInfo() { OT = pk.OT_Name, Gender = pk.OT_Gender, Generation = pk.Generation, Language = pk.Language, SID = pk.TrainerSID7, TID = pk.TrainerID7 };
                    var results = la.Results.FirstOrDefault(x => !x.Valid && x.Identifier != CheckIdentifier.Memory);
                    pk.SetHandlerandMemory(sav);
                    if (results != default)
                    {
                        switch (results.Identifier)
                        {
                            case CheckIdentifier.Evolution:
                                {
                                    if (pk.Species == (int)Species.Lickilicky && pk.Met_Location != 162 && pk.Met_Location != 244 && !pk.RelearnMoves.Contains(205))
                                        SetMoveOrRelearnByIndex(pk, 205, true);
                                }; break;
                            case CheckIdentifier.Encounter:
                                {
                                    if (pk.Met_Location == 162)
                                    {
                                        pk.SetAbilityIndex(0);
                                        while (!new LegalityAnalysis(pk).Valid && pk.Met_Level < 60)
                                        {
                                            pk.Met_Level += 1;
                                            if (pk.CurrentLevel < pk.Met_Level)
                                                pk.CurrentLevel = pk.Met_Level + 1;
                                        }
                                    }
                                }; break;
                            case CheckIdentifier.Form:
                                {
                                    if (pk.Species == (int)Species.Keldeo && pk.Form == 1 && !pk.Moves.Contains(548))
                                        SetMoveOrRelearnByIndex(pk, 548, false);
                                }; break;
                            case CheckIdentifier.Nickname:
                                {
                                    if (la.EncounterMatch is MysteryGift mg)
                                    {
                                        var mgPkm = mg.ConvertToPKM(sav);
                                        if (mgPkm.IsNicknamed)
                                            pk.SetNickname(mgPkm.Nickname);
                                        else pk.SetDefaultNickname(la);
                                        pk.SetHandlerandMemory(sav);
                                    }
                                    else pk.SetDefaultNickname(la);
                                }; break;
                        };
                    }

                    la = new LegalityAnalysis(pk);
                    if (!la.Valid)
                    {
                        Base.LogUtil.LogError($"Catch {catch_id} (user {user_id}) is illegal, trying to legalize.", "[SQLite]");
                        pk = (PK8)AutoLegalityWrapper.LegalizePokemon(pk);
                        if (!new LegalityAnalysis(pk).Valid)
                        {
                            Base.LogUtil.LogError($"Failed to legalize, removing entry...\n{la.Report()}", "[SQLite]");
                            var namesR = new string[] { "@user_id", "@id" };
                            var objR = new object[] { user_id, catch_id };
                            cmds.Add(new() { CommandText = "delete from binary_catches where user_id = ? and id = ?", Names = namesR, Values = objR });
                            cmds.Add(new() { CommandText = "delete from catches where user_id = ? and id = ?", Names = namesR, Values = objR });
                            updated++;
                            continue;
                        }
                    }
                }

                if (write)
                {
                    var names = new string[] { "@data", "@user_id", "@id" };
                    var obj = new object[] { pk.DecryptedPartyData, user_id, catch_id };
                    cmds.Add(new() { CommandText = "update binary_catches set data = ? where user_id = ? and id = ?", Names = names, Values = obj });

                    names = new string[] { "@is_shiny", "@ball", "@nickname", "@form", "@is_egg", "@is_event", "@user_id", "@id" };
                    obj = new object[] { pk.IsShiny, (Ball)pk.Ball, pk.Nickname, TradeCordHelperUtil<T>.FormOutput(pk.Species, pk.Form, out _), pk.IsEgg, pk.FatefulEncounter, user_id, catch_id };
                    cmds.Add(new() { CommandText = "update catches set is_shiny = ?, ball = ?, nickname = ?, form = ?, is_egg = ?, is_event = ? where user_id = ? and id = ?", Names = names, Values = obj });
                    updated++;
                }
            }
            reader.Close();

            if (updated > 0)
            {
                using var tran = Connection.BeginTransaction();
                for (int i = 0; i < cmds.Count; i++)
                {
                    cmd.Transaction = tran;
                    cmd.CommandText = cmds[i].CommandText;
                    var parameters = ParameterConstructor(cmds[i].Names, cmds[i].Values);
                    cmd.Parameters.AddRange(parameters);
                    cmd.ExecuteNonQuery();
                }
                tran.Commit();

                cmd.CommandText = $"update legality_fix set fixed = 1 where issue = 'ht_var'";
                cmd.ExecuteNonQuery();
            }
            Base.EchoUtil.Echo($"Scan complete! Updated {updated} records.");
        }

        private void EggBug()
        {
            Base.EchoUtil.Echo("Beginning to scan for species nicknamed \"Egg\". This may take a while.");
            List<SQLCommand> cmds = new();
            int updated = 0;

            var cmd = Connection.CreateCommand();
            cmd.CommandText = "select * from binary_catches b inner join catches c on b.user_id = c.user_id and b.id = c.id";
            var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ulong user_id = ulong.Parse(reader["user_id"].ToString());
                int catch_id = (int)reader["id"];
                string nickname = reader["nickname"].ToString();
                PK8 pk = (PK8?)PKMConverter.GetPKMfromBytes((byte[])reader["data"]) ?? new();
                var nick = pk.Language switch
                {
                    1 => "タマゴ",
                    3 => "Œuf",
                    4 => "Uovo",
                    5 => "Ei",
                    7 => "Huevo",
                    8 => "알",
                    9 or 10 => "蛋",
                    _ => "Egg",
                };

                if ((pk.Nickname == nick || nickname == nick || nickname == "Egg") && !pk.IsEgg)
                {
                    pk.IsNicknamed = false;
                    pk.Nickname = SpeciesName.GetSpeciesNameGeneration(pk.Species, pk.Language, 8);
                    var la = new LegalityAnalysis(pk);
                    if (la.Valid)
                    {
                        var names = new string[] { "@data", "@user_id", "@id" };
                        var obj = new object[] { pk.DecryptedPartyData, user_id, catch_id };
                        cmds.Add(new() { CommandText = "update binary_catches set data = ? where user_id = ? and id = ?", Names = names, Values = obj });

                        names = new string[] { "@nickname", "@user_id", "@id" };
                        obj = new object[] { pk.Nickname, user_id, catch_id };
                        cmds.Add(new() { CommandText = "update catches set nickname = ? where user_id = ? and id = ?", Names = names, Values = obj });

                        names = new string[] { "@name", "@ability", "@user_id", "@id" };
                        obj = new object[] { pk.Nickname, pk.Ability, user_id, catch_id };
                        cmds.Add(new() { CommandText = "update buddy set name = ?, ability = ? where user_id = ? and id = ?", Names = names, Values = obj });
                        updated++;
                    }
                    else Base.LogUtil.LogError($"Catch {catch_id} (user {user_id}) is illegal.", "[SQLite]");
                }
            }
            reader.Close();

            if (updated > 0)
            {
                using var tran = Connection.BeginTransaction();
                for (int i = 0; i < cmds.Count; i++)
                {
                    cmd = Connection.CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandText = cmds[i].CommandText;
                    var parameters = ParameterConstructor(cmds[i].Names, cmds[i].Values);
                    cmd.Parameters.AddRange(parameters);
                    cmd.ExecuteNonQuery();
                }
                tran.Commit();

                cmd.CommandText = $"update legality_fix set fixed = 1 where issue = 'egg_bug'";
                cmd.ExecuteNonQuery();
            }
            Base.EchoUtil.Echo($"Scan complete! Updated {updated} records.");
        }

        private void SetMoveOrRelearnByIndex(PK8 pk, int move, bool relearn)
        {
            int index = relearn ? pk.RelearnMoves.ToList().IndexOf(0) : pk.Moves.ToList().IndexOf(0);
            if (index == -1 && !relearn)
                pk.Move4 = move;
            else if (index == -1 && relearn)
                return;

            switch (index)
            {
                case 0: _ = relearn ? pk.RelearnMove1 = move : pk.Move1 = move; break;
                case 1: _ = relearn ? pk.RelearnMove2 = move : pk.Move2 = move; break;
                case 2: _ = relearn ? pk.RelearnMove3 = move : pk.Move3 = move; break;
                case 3: _ = relearn ? pk.RelearnMove4 = move : pk.Move4 = move; break;
            };
            pk.HealPP();
        }
    }
}
