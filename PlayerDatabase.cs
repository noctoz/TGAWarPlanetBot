using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jitbit.Utils;
using Discord;
using Discord.Commands;
using Discord.WebSocket;

namespace TGAWarPlanetBot
{
	public class Faction
	{
		public static int Unknown = 1;

		public int Id { get; set; }
		public string Name { get; set; }
	} 

	public class User
	{
		public static int Default = 1;

		public int Id { get; set; }
		public string Name { get; set; }
		public ulong DiscordId { get; set; }
	}

	public class Player
	{
		public int Id { get; set; }
		public string Name { get; set; }
		public int Level { get; set; }
		public bool IsFarm { get; set; }
		[JsonIgnore]
		public User User { get; set; }
		//public ulong DiscordId { get; set; }
		public string GameId { get; set; }
		[JsonIgnore]
		public List<Faction> Factions { get; set; }
		[JsonPropertyName("Faction")]
		public string FactionName { get; set; } // Legacy field for reading data

		public Player()
		{
			Id = -1;
			Name = "NoName";
			Level = 0;
			IsFarm = false;
			User = null;
			GameId = "N/A";
			Factions = new List<Faction>();
			FactionName = "NoFaction";
		}
	}

	public class PlayerDatabase
	{
		public ulong Id { get; set; }
		public string Name { get; set; }
		public List<Player> Players { get; set; }
		[JsonIgnore]
		public List<Faction> Factions { get; set; }
		[JsonIgnore]
		public SQLiteConnection Conn { get; set; }

		public PlayerDatabase()
		{
			Players = new List<Player>();
			Factions = new List<Faction>();
			Conn = null;
		}
	}

	// Filter for the player query. Only one member can be set
	public class PlayerFilter
	{
		public Faction Faction { get; set; }
		public string Name { get; set; }
		public string GameId { get; set; }
		public User User { get; set; }
	}

	public class PlayerDatabaseService
	{
		private readonly string m_databaseDir = "db";
		private Dictionary<ulong, PlayerDatabase> m_databases;

		private void CreateDatabase(SQLiteConnection conn)
		{
			using var cmd = new SQLiteCommand(conn);

			// This is just for testing purpose
			cmd.CommandText = "DROP TABLE IF EXISTS user";
			cmd.ExecuteNonQuery();
			cmd.CommandText = "DROP TABLE IF EXISTS base";
			cmd.ExecuteNonQuery();
			// End test block

			// Create user table
			cmd.CommandText = @"CREATE TABLE user(id INTEGER PRIMARY KEY, name TEXT, discord_id INT)";
			cmd.ExecuteNonQuery();

			// Insert a default user
			cmd.CommandText = @"INSERT INTO user(name, discord_id) VALUES('NoUser', 0)";
			cmd.ExecuteNonQuery();

			// Create base table
			cmd.CommandText = @"CREATE TABLE base(id INTEGER PRIMARY KEY, user_id INT, name TEXT, game_id TEXT, is_farm INT, FOREIGN KEY(user_id) REFERENCES user(id))";
			cmd.ExecuteNonQuery();

			// Create faction table
			cmd.CommandText = @"CREATE TABLE faction(id INTEGER PRIMARY KEY, name TEXT)";
			cmd.ExecuteNonQuery();

			// Insert unknown faction to use as default
			cmd.CommandText = @"INSERT INTO faction(name) VALUES('NoFaction')";
			cmd.ExecuteNonQuery();

			// Create base_faction table
			cmd.CommandText = @"CREATE TABLE base_faction(id INTEGER PRIMARY KEY, base_id INT, faction_id INT, FOREIGN KEY(base_id) REFERENCES base(id), FOREIGN KEY(faction_id) REFERENCES faction(id))";
			cmd.ExecuteNonQuery();
		}

		public PlayerDatabaseService()
		{
			Console.WriteLine(Directory.GetCurrentDirectory());
			m_databases = new Dictionary<ulong, PlayerDatabase>();

			// If db directory does not exist we create it
			if (!Directory.Exists(m_databaseDir))
			{
				Directory.CreateDirectory(m_databaseDir);
			}

			// Open json files (This is the legacy path)
			var jsonFiles = Directory.EnumerateFiles(m_databaseDir, "*.json", SearchOption.AllDirectories);

			foreach (string currentFile in jsonFiles)
			{
				var jsonString = File.ReadAllText(currentFile);
				PlayerDatabase database = JsonSerializer.Deserialize<PlayerDatabase>(jsonString);

				// If there is a db file we open a Sqlite connection
				string dbFile = m_databaseDir + "/" + database.Id + ".db";
				bool dbExist = File.Exists(dbFile);

				// We always open the connection
				// If the file does not exist this will create it
				string cs = @"URI=file:" + dbFile;
				var conn = new SQLiteConnection(cs);
				conn.Open();
				database.Conn = conn;

				// If the file did not exist we have a new db so we create the tables
				// If it did exist we read some data
				if (!dbExist)
				{
					CreateDatabase(conn);

					foreach (Player player in database.Players)
					{
						Faction faction = FindFaction(database, player.FactionName);
						// Create factions that don't exist
						if (faction == null)
						{
							faction = AddFaction(database, player.FactionName);
						}

						AddPlayer(database, player.Name, faction, player.GameId);
					}
				}
				else
				{
					string stm = "SELECT * FROM faction";
					using var cmd = new SQLiteCommand(stm, database.Conn);
					using SQLiteDataReader rdr = cmd.ExecuteReader();

					while (rdr.Read())
					{
						Faction faction = new Faction() { Id = rdr.GetInt32(0), Name = rdr.GetString(1) };
						database.Factions.Add(faction);
					}
				}

				m_databases[database.Id] = database;
			}
		}

		public PlayerDatabase GetDatabase(SocketGuild guild)
		{
			PlayerDatabase database;
			ulong databaseId = guild.Id;
			if (!m_databases.ContainsKey(databaseId))
			{
				database = new PlayerDatabase();
				database.Id = databaseId;
				database.Name = guild.Name;
				
				var options = new JsonSerializerOptions
				{
					WriteIndented = true
				};

				string jsonFile = m_databaseDir + "/" + database.Id + ".json";
				string jsonText = JsonSerializer.Serialize<PlayerDatabase>(database, options);
				File.WriteAllText(jsonFile, jsonText);

				// Create database
				string dbFile = m_databaseDir + "/" + database.Id + ".db";
				string cs = @"URI=file:" + dbFile;
				var conn = new SQLiteConnection(cs);
				conn.Open();
				database.Conn = conn;

				CreateDatabase(conn);

				m_databases[databaseId] = database;
			}
			else
			{
				database = m_databases[databaseId];
			}

			return database;
		}

		public Faction AddFaction(PlayerDatabase database, string name)
		{
			Faction faction = new Faction() { Name = name };

			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "INSERT INTO faction(name) VALUES(@name)";
			cmd.Parameters.AddWithValue("@name", name);
			cmd.ExecuteNonQuery();

			faction.Id = (Int32)database.Conn.LastInsertRowId;
			database.Factions.Add(faction);

			return faction;
		}

		public Faction FindFaction(PlayerDatabase database, string name)
		{
			return database.Factions.FirstOrDefault(f => f.Name == name);
		}

		public Faction FindFaction(PlayerDatabase database, int id)
		{
			return database.Factions.FirstOrDefault(f => f.Id == id);
		}

		public void UpdateFaction(PlayerDatabase database, Faction faction)
		{
			using var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "UPDATE faction SET name = @name WHERE id = @factionId";
			cmd.Parameters.AddWithValue("@name", faction.Name);
			cmd.Parameters.AddWithValue("@factionId", faction.Id);
			cmd.ExecuteNonQuery();
		}

		public void UpdatePlayerFaction(PlayerDatabase database, Player player)
		{
			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "INSERT INTO base_faction(base_id, faction_id) VALUES(@baseId, @factionId)";
			cmd.Parameters.AddWithValue("@baseId", player.Id);
			cmd.Parameters.AddWithValue("@factionId", player.Factions[0].Id);
			cmd.ExecuteNonQuery();
		}

		public Player AddPlayer(PlayerDatabase database, string name, Faction faction, string gameId)
		{
			if (faction == null)
			{
				faction = database.Factions.First(f => f.Id == Faction.Unknown);
			}

			Player newPlayer = new Player() { Name = name, GameId = gameId };
			newPlayer.Factions.Insert(0, faction);

			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "INSERT INTO base(user_id, name, game_id) VALUES(@userId, @name, @gameId)";
			cmd.Parameters.AddWithValue("@userId", User.Default);
			cmd.Parameters.AddWithValue("@name", name);
			cmd.Parameters.AddWithValue("@gameId", gameId);
			cmd.ExecuteNonQuery();

			newPlayer.Id = (Int32)database.Conn.LastInsertRowId;

			// Add faction mapping
			UpdatePlayerFaction(database, newPlayer);

			return newPlayer;
		}

		public void RemovePlayer(PlayerDatabase database, Player player)
		{
			// Delete the player
			using var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "DELETE FROM base WHERE id = @playerId)";
			cmd.Parameters.AddWithValue("@playerId", player.Id);
			cmd.ExecuteNonQuery();

			// Delete faction mappings
			cmd.CommandText = "DELETE FROM base_faction WHERE base_id = @playerId)";
			cmd.Parameters.AddWithValue("@playerId", player.Id);
			cmd.ExecuteNonQuery();
		}

		public void UpdatePlayer(PlayerDatabase database, Player player)
		{
			using var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "UPDATE base SET name = @name, user_id = @userId, game_id = @gameId WHERE id = @playerId";
			cmd.Parameters.AddWithValue("@name", player.Name);
			cmd.Parameters.AddWithValue("@userId", player.User.Id);
			cmd.Parameters.AddWithValue("@gameId", player.GameId);
			cmd.Parameters.AddWithValue("@playerId", player.Id);
			cmd.ExecuteNonQuery();
		}

		public List<Player> FindPlayers(PlayerDatabase database, PlayerFilter filter = null)
		{
			string stm = 
				"SELECT " +
					"base.id AS id, " +
					"base.name AS name, " +
					"base.game_id AS game_id, " +
					"user.name AS username, " +
					"user.id AS user_id, " +
					"user.discord_id AS discord_id, " +
					"base_faction.faction_id AS faction_id " +
				"FROM base " +
				"LEFT JOIN user ON user.id = base.user_id " +
				"LEFT JOIN base_faction ON base_faction.base_id = base.id";

			// We can't filter on faction here since it will give results from previous factions
			if (filter != null)
			{
				// if (filter.Faction != null)
				// {
				// 	stm += "faction_id = " + filter.Faction.Id;
				// }
				if (filter.Name != null)
				{
					stm += " WHERE base.name = '" + filter.Name + "'";
				}
				else if (filter.GameId != null)
				{
					stm += " WHERE base.game_id = '" + filter.GameId + "'";
				}
				else if (filter.User != null)
				{
					stm += " WHERE user_id = " + filter.User.Id;
				}
			}
			
			stm += " ORDER BY base.id ASC, base_faction.id DESC";

			using var cmd = new SQLiteCommand(stm, database.Conn);
			using SQLiteDataReader rdr = cmd.ExecuteReader();

			List<Player> players = new List<Player>();
			Player previousPlayer = null; // This is used to check if we have multiple entries for the same player
			while (rdr.Read())
			{
				// If we have multiple entries for a player, we add subsequent ones to the faction array
				int playerId = rdr.GetInt32(0);
				if (previousPlayer != null && playerId == previousPlayer.Id)
				{
					int factionId = rdr.GetInt32(6);
					previousPlayer.Factions.Add(database.Factions.First(f => f.Id == factionId));
				}
				else
				{
					User user = new User() { Id = rdr.GetInt32(4), Name = rdr.GetString(3), DiscordId = (ulong)rdr.GetInt64(5) };
					Player player = new Player() { Id = playerId, Name = rdr.GetString(1), GameId = rdr.GetString(2), User = user };
					int factionId = rdr.GetInt32(6);
					player.Factions.Add(database.Factions.First(f => f.Id == factionId));

					// We have to do the faction filtering here until we figure out the correct SQL
					if (filter == null || filter.Faction == null || filter.Faction.Id == factionId)
					{
						players.Add(player);
					}
					
					previousPlayer = player;
				}
			}

			return players;
		}

		public Player FindPlayer(PlayerDatabase database, int id)
		{
			string stm = 
				"SELECT " +
					"base.id AS id, " +
					"base.name AS name, " +
					"base.game_id AS game_id, " +
					"user.name AS username, " +
					"user.id AS user_id, " +
					"user.discord_id AS discord_id, " +
					"base_faction.faction_id AS faction_id " +
				"FROM base " +
				"LEFT JOIN user ON user.id = base.user_id " +
				"LEFT JOIN base_faction ON base_faction.base_id = base.id " +
				"WHERE base.id = " + id +
				" ORDER BY base.id ASC, base_faction.id DESC";

			var cmd = new SQLiteCommand(stm, database.Conn);
			SQLiteDataReader rdr = cmd.ExecuteReader();
			if (rdr.Read())
			{
				User user = new User() { 
					Id = rdr.GetInt32(4),
					Name = rdr.GetString(3),
					DiscordId = (ulong)rdr.GetInt64(5) };
				
				Player player = new Player() {
					Id = rdr.GetInt32(0),
					Name = rdr.GetString(1),
					GameId = rdr.GetString(2), User = user };
				
				int factionId = rdr.GetInt32(6);
				player.Factions.Add(database.Factions.First(f => f.Id == factionId));

				// We might have additional entries if there are multiple factions
				while (rdr.Read())
				{
					factionId = rdr.GetInt32(6);
					player.Factions.Add(database.Factions.First(f => f.Id == factionId));
				}
				
				return player;
			}
			else
			{
				return null;
			}
		}

		public bool ConnectPlayer(PlayerDatabase database, Player player, SocketUser discordUser)
		{
			// If user is default user we create a new user
			if (player.User.Id == User.Default)
			{
				// Check if there already is a user for this discord id
				User user = FindUser(database, discordUser);
				if (user == null)
				{
					user = AddUser(database, discordUser.Username, discordUser.Id);
				}

				player.User = user;
				UpdatePlayer(database, player);
			}
			else
			{
				player.User.DiscordId = discordUser.Id;
				UpdateUser(database, player.User);
			}

			return true;
		}

		private User FindUser(SQLiteCommand cmd)
		{
			SQLiteDataReader rdr = cmd.ExecuteReader();
			if (rdr.Read())
			{
				User user = new User() { 
					Id = rdr.GetInt32(0),
					Name = rdr.GetString(1),
					DiscordId = (ulong)rdr.GetInt64(2) };

				return user;
			}
			else
			{
				return null;
			}
		}

		public User FindUser(PlayerDatabase database, string name)
		{
			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "SELECT * FROM user WHERE name = @name";
			cmd.Parameters.AddWithValue("@name", name);
			
			return FindUser(cmd);
		}

		public User FindUser(PlayerDatabase database, SocketUser discordUser)
		{
			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "SELECT * FROM user WHERE discord_id = @discordId";
			cmd.Parameters.AddWithValue("@discordId", discordUser.Id);
			
			return FindUser(cmd);
		}

		public User FindUser(PlayerDatabase database, int id)
		{
			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "SELECT * FROM user WHERE id = @id";
			cmd.Parameters.AddWithValue("@id", id);
			
			return FindUser(cmd);
		}

		public User AddUser(PlayerDatabase database, string name, ulong discordId)
		{
			var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "INSERT INTO user(name, discord_id) VALUES(@name, @discordId)";
			cmd.Parameters.AddWithValue("@name", name);
			cmd.Parameters.AddWithValue("@discordId", discordId);
			cmd.ExecuteNonQuery();

			User user = new User() { 
				Id = (Int32)database.Conn.LastInsertRowId, 
				Name = name,
				DiscordId = discordId};

			return user;
		}

		public void UpdateUser(PlayerDatabase database, User user)
		{
			using var cmd = new SQLiteCommand(database.Conn);
			cmd.CommandText = "UPDATE user SET name = @name, discord_id = @discordId WHERE id = @userId";
			cmd.Parameters.AddWithValue("@name", user.Name);
			cmd.Parameters.AddWithValue("@discordId", user.DiscordId);
			cmd.Parameters.AddWithValue("@userId", user.Id);
			cmd.ExecuteNonQuery();
		}
	}

	[Group("player")]
	public class PlayerModule : ModuleBase<SocketCommandContext>
	{
		private readonly PlayerDatabaseService m_databaseService;
		public PlayerModule(PlayerDatabaseService databaseService)
		{
			m_databaseService = databaseService;
		}

		// !player
		[Command]
		public async Task DefaultAsync()
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("```");
			sb.Append("usage: !player <command> [<args>]\n\n");
			sb.Append(String.Format("\t{0,-15} {1}\n", "add", "Add player with given name and properties."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "connect", "Connect a player to a discord user."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "set", "Set name, game id and faction for given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setid", "Set the game id for given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setname", "Set the game name for given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setfaction", "Set faction for the given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setuser", "Set user for given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "list", "List all players."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "whois", "Display information for the given player."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "search", "Same as whois."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "find", "Same as whois."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "addfaction", "Add faction with given name."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "findfaction", "Find faction with given name."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setfactionname", "Set name for given faction."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "adduser", "Add user with given name."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "finduser", "Find user with given name."));
			sb.Append(String.Format("\t{0,-15} {1}\n", "setusername", "Set name for given user."));
			sb.Append("```");
			await ReplyAsync(sb.ToString());
		}

		// !player details
		[Command("details")]
		[Summary("List all players with full details.")]
		public async Task DetailsAsync()
		{
			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			var players = m_databaseService.FindPlayers(database);

			// We have a limit for message size so we send one message for every 20 players
			for (int i = 0; i < players.Count(); i += 20)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append(String.Format("{0,-3} {1,-20} {2,-20} {3, -15} {4, -5}\n", "Id", "Name", "User", "GameId", "Faction"));
				sb.Append("---------------------------------------------------------------------\n");
				foreach (var player in players.Skip(i).Take(20))
				{
					string gameId = player.GameId != null ? player.GameId : "<N/A>";
					sb.Append(String.Format("{0,-3} {1,-20} {2,-20} {3, -15} {4, -5}\n", player.Id, player.Name, player.User.Name, gameId, player.Factions[0].Name));
				}
				sb.Append("```");

				await ReplyAsync(sb.ToString());
			}
		}

		// private string GetPlayerList(IEnumerable<Player> players)
		// {
		// 	var sb = new System.Text.StringBuilder();
		// 	sb.Append("```");
		// 	sb.Append(String.Format("{0,-20} {1, -5}\n", "Name", "Faction"));
		// 	sb.Append("----------------------------\n");
		// 	foreach (var player in players)
		// 	{
		// 		sb.Append(String.Format("{0,-20} {1, -5}\n", player.Name, player.Factions[0].Name));
		// 	}
		// 	sb.Append("```");
		// 	return sb.ToString();
		// }

		// !player list
		[Command("list")]
		[Summary("List all players.")]
		public async Task ListAsync(string factionName = "")
		{
			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);

			PlayerFilter filter = null;
			if (factionName.Length > 0)
			{
				Faction faction = database.Factions.FirstOrDefault(f => f.Name == factionName);
				if (faction == null)
				{
					var sb = new System.Text.StringBuilder();
					sb.Append("```");
					sb.Append("Faction with name '" + factionName + "' not found!");
					sb.Append("```");
					await ReplyAsync(sb.ToString());
					return;
				}

				filter = new PlayerFilter() { Faction = faction };
			}

			var players = m_databaseService.FindPlayers(database, filter);

			// We have a limit for message size so we send one message for every 20 players
			for (int i = 0; i < players.Count(); i += 20)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append(String.Format("{0,-20} {1, -5}\n", "Name", "Faction"));
				sb.Append("----------------------------\n");
				foreach (var player in players.Skip(i).Take(20))
				{
					sb.Append(String.Format("{0,-20} {1, -5}\n", player.Name, player.Factions[0].Name));
				}
				sb.Append("```");
				await ReplyAsync(sb.ToString());
			}
		}

		// !player addfaction Name
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("addfaction")]
		[Summary("Add new faction.")]
		public async Task AddFactionAsync(string name = "")
		{
			if (name.Length == 0 || name.StartsWith("!"))
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player addfaction <name>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}
			else
			{
				PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
				// Make sure we do not add players that already exist
				Faction faction = database.Factions.FirstOrDefault(f => f.Name == name);
				if (faction == null)
				{
					m_databaseService.AddFaction(database, name);
					await ReplyAsync("Faction " + name + " created!");
				}
				else
				{
					await ReplyAsync("Faction " + name + " already exist!");
				}
			}
		}

		// !player findFaction Name
		[Command("findfaction")]
		[Summary("Get faction with matching name.")]
		public async Task FindFactionAsync(string name = "")
		{
			if (name.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player faction <name>\n");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Faction matchingFaction = m_databaseService.FindFaction(database, name);
			if (matchingFaction != null)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");

				// Only show id for users that can change player data
				SocketGuildUser commandUser = Context.User as SocketGuildUser;
				if (commandUser.GuildPermissions.ManageNicknames)
				{
					sb.Append(String.Format("{0,-15} {1,-20}\n", "Id:", matchingFaction.Id));
				}
				sb.Append(String.Format("{0,-15} {1,-20}\n", "Name:", matchingFaction.Name));

				// Find how many players faction have
				PlayerFilter filter = new PlayerFilter() { Faction = matchingFaction };
				List<Player> players = m_databaseService.FindPlayers(database, filter);
				sb.Append(String.Format("{0,-15} {1,-20}\n", "Members", players.Count));

				sb.Append("```");
				await ReplyAsync(sb.ToString());
			}
			else
			{
				await ReplyAsync($"Failed to find faction {name}");
			}
		}

		// !player setfactionname Id Name
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setfactionname")]
		[Summary("Set name for faction.")]
		public async Task SetFactionNameAsync(int id = -1, string name = "")
		{
			if (id < 0 || name.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setfactionname <id> <name>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Faction matchingFaction = m_databaseService.FindFaction(database, id);
			if (matchingFaction != null)
			{
				matchingFaction.Name = name;
				m_databaseService.UpdateFaction(database, matchingFaction);

				await ReplyAsync($"Set name of faction {id} -> {name}");
			}
			else
			{
				await ReplyAsync($"Failed to find faction with id {id}");
			}
		}

		// !player adduser Name
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("adduser")]
		[Summary("Add new User.")]
		public async Task AddUserAsync(string name = "")
		{
			if (name.Length == 0 || name.StartsWith("!"))
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player adduser <name>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}
			else
			{
				PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
				// Make sure we do not add users that already exist
				User user = m_databaseService.FindUser(database, name);
				if (user == null)
				{
					m_databaseService.AddUser(database, name, 0);
					await ReplyAsync("User " + name + " created!");
				}
				else
				{
					await ReplyAsync("User " + name + " already exist!");
				}
			}
		}

		// !player finduser Name
		[Command("finduser")]
		[Summary("Get user with matching name.")]
		public async Task FindUserAsync(string name = "")
		{
			if (name.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player finduser <name>\n");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			User matchingUser = m_databaseService.FindUser(database, name);
			if (matchingUser != null)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");

				// Only show id for users that can change player data
				SocketGuildUser commandUser = Context.User as SocketGuildUser;
				if (commandUser.GuildPermissions.ManageNicknames)
				{
					sb.Append(String.Format("{0,-15} {1,-20}\n", "Id:", matchingUser.Id));
				}
				sb.Append(String.Format("{0,-15} {1,-20}\n", "Name:", matchingUser.Name));

				// Find all players for user
				PlayerFilter filter = new PlayerFilter() { User = matchingUser };
				List<Player> players = m_databaseService.FindPlayers(database, filter);
				if (players.Count > 0)
				{
					var playerNames = new List<string>();
					int count = 0;
					foreach (Player player in players)
					{
						playerNames.Add(player.Name);
						count += 1;
						if (count > 50)
						{
							playerNames.Add("...");
							break;
						}
					}
					sb.Append(String.Format("{0,-15} {1,-20}\n", "Bases:", string.Join(", ", playerNames)));
				}

				sb.Append("```");
				await ReplyAsync(sb.ToString());
			}
			else
			{
				await ReplyAsync($"Failed to find user with {name}");
			}
		}

		// !player setusername Id Name
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setusername")]
		[Summary("Set name for user.")]
		public async Task SetUserNameAsync(int id = -1, string name = "")
		{
			if (id < 0 || name.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setusername <id> <name>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			User matchingUser = m_databaseService.FindUser(database, id);
			if (matchingUser != null)
			{
				matchingUser.Name = name;
				m_databaseService.UpdateUser(database, matchingUser);

				await ReplyAsync($"Set name of user {id} -> {name}");
			}
			else
			{
				await ReplyAsync($"Failed to find user with id {id}");
			}
		}

		// !player add Name
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("add")]
		[Summary("Add new player.")]
		public async Task AddAsync(string name = "", string id = "<N/A>", string factionName = "")
		{
			if (name.Length == 0 || name.StartsWith("!"))
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player add <name> [<game-id>] [<faction>]");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}
			else
			{
				PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);

				Faction faction = null;
				if (factionName.Length > 0)
				{
					faction = m_databaseService.FindFaction(database, factionName);
					if (faction == null)
					{
						var sb = new System.Text.StringBuilder();
						sb.Append("```");
						sb.Append("Failed to find faction " + factionName);
						sb.Append("```");
						await ReplyAsync(sb.ToString());
						return;
					}
				}
				m_databaseService.AddPlayer(database, name, faction, id);
				await ReplyAsync("Player " + name + " created!");
			}
		}

		// !player add Name
		[RequireUserPermission(GuildPermission.Administrator)]
		[Command("remove")]
		[Summary("Remove existing player.")]
		public async Task RemoveAsync(int id = -1)
		{
			if (id < 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player remove <id>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}
			else
			{
				PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
				Player matchingPlayer = m_databaseService.FindPlayer(database, id);
				if (matchingPlayer != null)
				{
					m_databaseService.RemovePlayer(database, matchingPlayer);
					await ReplyAsync($"Removed player {matchingPlayer.Name} with id {id}");
				}
				else
				{
					await ReplyAsync($"Failed to find player with id {id}");
				}
			}
		}

		// !player connect Id DiscordUser
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("connect")]
		[Summary("Connect player to discord id.")]
		public async Task ConnectAsync(int id = -1, SocketUser user = null)
		{
			if (id < 0 || user == null)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player connect <id> <@discord-user>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				m_databaseService.ConnectPlayer(database, matchingPlayer, user);
				await ReplyAsync($"Connected {id} -> {user.Username}#{user.Discriminator}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player setuser Id User
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setuser")]
		[Summary("Connect player to discord id.")]
		public async Task SetUserAsync(int id = -1, string username = "")
		{
			if (id < 0 || username.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setuser <id> <username>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				User user = m_databaseService.FindUser(database, username);
				if (user == null)
				{
					var sb = new System.Text.StringBuilder();
					sb.Append("```");
					sb.Append("Failed to find user " + username);
					sb.Append("```");
					await ReplyAsync(sb.ToString());
					return;
				}

				matchingPlayer.User = user;
				m_databaseService.UpdatePlayer(database, matchingPlayer);
				await ReplyAsync($"Set user of {id} -> {username}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player set Name GameId Faction
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("set")]
		[Summary("Set game id and faction for player.")]
		public async Task SetAsync(int id = -1, string name = "", string gameId = "", string factionName = "")
		{
			if (id < 0 || name.Length == 0 || gameId.Length == 0 || factionName.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player set <id> <name> <game-id> <faction>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				Faction faction = m_databaseService.FindFaction(database, factionName);
				if (faction == null)
				{
					var sb = new System.Text.StringBuilder();
					sb.Append("```");
					sb.Append("Failed to find faction " + factionName);
					sb.Append("```");
					await ReplyAsync(sb.ToString());
					return;
				}

				// Only update if values have changed
				if (matchingPlayer.Name != name || matchingPlayer.GameId != gameId)
				{
					matchingPlayer.Name = name;
					matchingPlayer.GameId = gameId;
					m_databaseService.UpdatePlayer(database, matchingPlayer);
				}
				
				if (matchingPlayer.Factions[0] != faction)
				{
					matchingPlayer.Factions.Insert(0, faction);
					m_databaseService.UpdatePlayerFaction(database, matchingPlayer);
				}
				
				await ReplyAsync($"Updated player {matchingPlayer.Name} with id {id}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player setid Name GameId
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setid")]
		[Summary("Set game id for player.")]
		public async Task SetIdAsync(int id = -1, string gameId = "")
		{
			if (id < 0 || gameId.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setid <id> <game-id>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				matchingPlayer.GameId = gameId;
				m_databaseService.UpdatePlayer(database, matchingPlayer);

				await ReplyAsync($"Set game id of {id} -> {gameId}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player setname Name GameName
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setname")]
		[Summary("Set name for player.")]
		public async Task SetNameAsync(int id = -1, string name = "")
		{
			if (id < 0 || name.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setname <id> <name>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				matchingPlayer.Name = name;
				m_databaseService.UpdatePlayer(database, matchingPlayer);
				await ReplyAsync($"Set name of {id} -> {name}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player setfaction Name Faction
		[RequireUserPermission(GuildPermission.ManageNicknames)]
		[Command("setfaction")]
		[Summary("Set faction for player.")]
		public async Task SetFactionAsync(int id = -1, string factionName = "")
		{
			if (id < 0 || factionName.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player setfaction <id> <faction>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			Player matchingPlayer = m_databaseService.FindPlayer(database, id);
			if (matchingPlayer != null)
			{
				Faction faction = m_databaseService.FindFaction(database, factionName);
				if (faction == null)
				{
					var sb = new System.Text.StringBuilder();
					sb.Append("```");
					sb.Append("Failed to find faction " + factionName);
					sb.Append("```");
					await ReplyAsync(sb.ToString());
					return;
				}

				// Only update if value changed
				if (matchingPlayer.Factions[0] != faction)
				{
					matchingPlayer.Factions.Insert(0, faction);
					m_databaseService.UpdatePlayerFaction(database, matchingPlayer);
				}
				
				await ReplyAsync($"Set faction of {id} -> {faction.Name}");
			}
			else
			{
				await ReplyAsync($"Failed to find player with id {id}");
			}
		}

		// !player whois Name/GameId
		[Command("whois")]
		[Alias("search", "find")]
		[Summary("Get player matching given name or it.")]
		public async Task WhoIsAsync(string nameOrId = "")
		{
			if (nameOrId.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player whois <name>\n");
				sb.Append("usage: !player whois <game-id>\n");
				sb.Append("usage: !player search <name>\n");
				sb.Append("usage: !player search <game-id>");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);

			PlayerFilter filter = new PlayerFilter() { Name = nameOrId };

			// Try to find player with name and if that fails from game id
			List<Player> matchingPlayers = m_databaseService.FindPlayers(database, filter);
			if (matchingPlayers.Count == 0)
			{
				filter = new PlayerFilter() { GameId = nameOrId };
				matchingPlayers = m_databaseService.FindPlayers(database, filter);
			}

			if (matchingPlayers.Count > 0)
			{
				bool first = true;
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				foreach (var player in matchingPlayers)
				{
					if (first)
					{
						first = false;
					}
					else
					{
						sb.Append("-----------------------\n");
					}
					// Only show id for users that can change player data
					SocketGuildUser commandUser = Context.User as SocketGuildUser;
					if (commandUser.GuildPermissions.ManageNicknames)
					{
						sb.Append(String.Format("{0,-15} {1,-20}\n", "Id:", player.Id));
					}
					sb.Append(String.Format("{0,-15} {1,-20}\n", "Name:", player.Name));
					sb.Append(String.Format("{0,-15} {1,-20}\n", "User:", player.User.Name));
					string gameId = player.GameId != null ? player.GameId : "<N/A>";
					sb.Append(String.Format("{0,-15} {1,-20}\n", "GameId:", gameId));
					sb.Append(String.Format("{0,-15} {1,-20}\n", "Faction:", player.Factions[0].Name));
					if (player.Factions.Count > 1)
					{
						var factions = new List<string>();
						foreach (Faction faction in player.Factions.Skip(1))
						{
							factions.Add(faction.Name);
						}
						sb.Append(String.Format("{0,-15} {1,-20}\n", "Previous:", string.Join(", ", factions)));
					}

				}
				sb.Append("```");
				await ReplyAsync(sb.ToString());
			}
			else
			{
				await ReplyAsync($"Failed to find player matching {nameOrId}");
			}
		}

		// Commands for admins
		#region admin

		// !player setfaction Name Faction
		[RequireUserPermission(GuildPermission.Administrator)]
		[Command("exporttocvs")]
		[Summary("Export to CVS.")]
		public async Task ExportToCvsAsync(string fileName = "")
		{
			if (fileName.Length == 0)
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player exporttocvs <fileName.csv>\n");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			if (Path.GetExtension(fileName) != ".csv")
			{
				var sb = new System.Text.StringBuilder();
				sb.Append("```");
				sb.Append("usage: !player exporttocvs <fileName.csv>\n");
				sb.Append("```");
				await ReplyAsync(sb.ToString());
				return;
			}

			PlayerDatabase database = m_databaseService.GetDatabase(Context.Guild);
			var exporter = new CsvExport();

			List<Player> players = m_databaseService.FindPlayers(database);
			foreach (Player player in players)
			{
				exporter.AddRow();
				exporter["UserName"] = player.User.Name;
				exporter["BaseName"] = player.Name;
				exporter["BaseLevel"] = player.Level;
				exporter["GameId"] = player.GameId;
				exporter["Faction"] = player.Factions[0].Name;
				exporter["Farm"] = player.IsFarm ? "Y" : "N";
			}

			exporter.ExportToFile(fileName);
			await ReplyAsync($"Exported to CSV");
		}

		#endregion // #region admin
	}
}