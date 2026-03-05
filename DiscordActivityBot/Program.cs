using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace DiscordActivityBot
{
    class Program
    {
        private static DiscordSocketClient _client;
        private static readonly string configPath = "config.json";
        private static BotConfig _config;
        private static MySQLUtils _db;
        private static LogSeverity _logLevel;
        private static Dictionary<ulong, DateTime> _voiceStartTimes = new Dictionary<ulong, DateTime>();
        
        static async Task Main(string[] args)
        {
           if (!ConfigUtils.LoadConfig(configPath, out _config)) return;

            _db = new MySQLUtils(_config.ConnectionStrings);

            if (!await _db.TestConnectionAsync()) return;
            await _db.InitializeDatabaseAsync();

            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.GuildMembers,
                AlwaysDownloadUsers = true,
                LogLevel = _logLevel = ConfigUtils.GetLogLevel(_config.LogLevel)
            };
            
            _client = new DiscordSocketClient(config);
            
            _client.Log += Log;
            _client.Ready += Ready;
            _client.MessageReceived += MessageReceived;
            _client.UserVoiceStateUpdated += UserVoiceStateUpdated;    
            
            await _client.LoginAsync(TokenType.Bot, _config.Token);
            await _client.StartAsync();
            
            // Бесконечное ожидание
            await Task.Delay(-1);
        }        
        private static Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }        
        private static Task Ready()
        {
            #region Logs
            WriteLog(LogSeverity.Info, $"Бот подклчюен как {_client.CurrentUser.Username}.");            

            if (_config.AdminIds.Count() == 0) WriteLog(LogSeverity.Warning, "Админы не заданы в конфиге. Для доступа к админ функциям будет использована проверка на администратора (право Administrator)");
            if (_config.ActiveRoleId == null) WriteLog(LogSeverity.Warning, "В конфиге не установлена роль для активных пользователей (ActiveRoleId)");
            if (_config.InactivaRoleId == null) WriteLog(LogSeverity.Warning, "В конфиге не установлена роль для не активных пользователей (DeactivateRoleId)");
            #endregion
            return Task.CompletedTask;
        }
        public static void WriteLog(LogSeverity logSeverity, string Description)
        {
            if (_logLevel > logSeverity) return;

            Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{logSeverity}]\t   {Description}");
        }
        private static async Task MessageReceived(SocketMessage message)
        {
            if (message.Author.IsBot) return;            

            _ = _db.AddMessageStatAsync(message.Author.Id);

            if (message.Content.Length == 0) return;
            if (message.Content[0] != _config.Prefix) return;

            string msg = message.Content;
            string[] command = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            EmbedBuilder embed = new();
                    
            switch (command[0][1..].ToLower())
            {
                case "ping" or "пинг":
                    var pong = await message.Channel.SendMessageAsync("Понг!");
                    var delay = (pong.Timestamp - message.Timestamp).TotalMilliseconds;
                    
                    embed = new EmbedBuilder()
                        .WithTitle("Понг!")
                        .WithDescription($"Задержка: **{delay:F0} ms**")
                        .WithColor(Color.Green);
                    
                    await pong.ModifyAsync(m =>
                    {
                        m.Content = null;
                        m.Embed = embed.Build();
                    });
                    break;                
                case "role" or "роль":
                    if (!AdminCheck(message.Author)) { _ = SendError(message.Channel, "У вас нет прав администратора для использования данной команды."); return; }
                    if (command.Length < 3) { _ = SendError(message.Channel, $"Не достаточно аргуменнтов для использования команды.\n{_config.Prefix}Роль <тип> <роль>\nДля подробной информации используйте {_config.Prefix}Помощь."); return; }
                    if (!new List<string>{"active", "активный", "inactive", "неактивный"}.Contains(command[1].ToLower())) { _ = SendError(message.Channel, $"\"{command[1]}\" недопустимое значение для аргумента. Используйте: active/inactive или активный/неактивный\nДля подробной информации используйте {_config.Prefix}Помощь."); return; }
                    string arg2 = command.Length > 3 ? arg2 = string.Join(' ', command.Skip(1)) : command[2];
                    if (!FindRole((message.Channel as SocketGuildChannel).Guild, arg2, out SocketRole role)) { _ = SendError(message.Channel, $"Не удалось найти роль \"{arg2}\""); return; }

                    bool active = command[1].Equals("active", StringComparison.OrdinalIgnoreCase) || command[1].Equals("активный", StringComparison.OrdinalIgnoreCase);
                    if (active)
                        _config.ActiveRoleId = role.Id;
                    else
                        _config.InactivaRoleId = role.Id;

                    ConfigUtils.SaveConfig(configPath, _config);
                    embed = new EmbedBuilder()
                        .WithTitle($"Успех!")
                        .WithDescription($"В качестве {(active ? "активной" : "неактивной")} роли задана \"{role.Name}\".")
                        .WithColor(role.Color);
                    
                    _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break;                
                case "statistics" or "stat" or "статистика" or "стат":
                    SocketUser user;
                    if (command.Length > 1)
                    {
                        string arg = string.Join(' ', command.Skip(1));
                        if(!FindUser(arg, out user))
                        {
                            _ = SendError(message.Channel, $"Не удалось найти пользователя \"{arg}\"");
                            return;
                        }
                    }
                    else
                        user = message.Author;

                    (int totalMessages, int totalVoiceTime, int activeDays) = await _db.GetUserStatsAsync(user.Id);
                    
                    if (activeDays == 0)
                    {                        
                        embed = new EmbedBuilder()
                            .WithTitle($"Статистика {(user as SocketGuildUser)?.Nickname ?? user.GlobalName}.")
                            .WithDescription("Данные отсутствуют.")
                            .WithColor(Color.Gold);
                    }
                    else
                    {
                        embed = new EmbedBuilder()
                            .WithTitle($"Статистика {(user as SocketGuildUser).Nickname} за {GetDaysString(activeDays)}.")
                            .AddField("Сообщения", $"{totalMessages} шт.", true)
                            .AddField("Войс", FormatVoiceTimeCompact(totalVoiceTime), true)
                            .WithColor(Color.Green);
                    }
                    _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break;
            }
        }
        private static bool FindRole(SocketGuild guild, string str, out SocketRole role)
        {
            if (ulong.TryParse(str.Contains("<@&") ? str[3..^1] : str, out ulong id))
            {
                role = guild.GetRole(id);
                return true;
            }

            role = guild.Roles.FirstOrDefault(r => 
                !r.IsEveryone &&
                r.Name?.Contains(str, StringComparison.OrdinalIgnoreCase) == true);

            return role != null;
        }
        private static bool AdminCheck(SocketUser user) =>
            _config.AdminIds.Count > 0 ?
            _config.AdminIds.Contains(user.Id) :
            (user as SocketGuildUser).GuildPermissions.Administrator;
        private static string FormatVoiceTimeCompact(int minutes)
        {
            if (minutes < 60) return $"{minutes} мин.";
            int hours = minutes / 60;
            int mins = minutes % 60;
            return mins == 0 ? $"{hours} ч." : $"{hours} ч. {mins} мин.";
        }
        public static string GetDaysString(int days)
        {
            if (days < 0) days = 0;
            
            // Особые случаи: 11-14 всегда "дней"
            int lastTwoDigits = days % 100;
            if (lastTwoDigits >= 11 && lastTwoDigits <= 14)
                return $"{days} дней";
            
            // Проверяем последнюю цифру
            return (days % 10) switch
            {
                1 => $"{days} день",
                2 or 3 or 4 => $"{days} дня",
                _ => $"{days} дней"
            };
        }
        private static bool FindUser(string str, out SocketUser user)
        {
            if (ulong.TryParse(str.Contains("<@") ? str[2..^1] : str, out ulong id))
            {
                user = _client.GetUser(id);
                if (user == null || user.IsBot == true) return false;
                return true;
            }

            user = _client.Guilds
                .SelectMany(g => g.Users)
                .Where(u => !u.IsBot)
                .FirstOrDefault(u => 
                    u.Username?.Contains(str, StringComparison.OrdinalIgnoreCase) == true ||
                    u.Nickname?.Contains(str, StringComparison.OrdinalIgnoreCase) == true ||
                    u.GlobalName?.Contains(str, StringComparison.OrdinalIgnoreCase) == true);

            return user != null;
        }
        private static async Task SendError(ISocketMessageChannel channel, string Description)
        {
            EmbedBuilder embed = new EmbedBuilder()
                .WithTitle("Ошибка!")
                .WithDescription(Description)
                .WithColor(Color.Red);

            _ = channel.SendMessageAsync(embed: embed.Build());
        }
        private static async Task UserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            if (user.IsBot) return;
            
            try
            {
                // Пользователь зашел в голосовой канал
                if (after.VoiceChannel != null && before.VoiceChannel == null)
                {
                    _voiceStartTimes[user.Id] = DateTime.UtcNow;
                    WriteLog(LogSeverity.Verbose, $"{user.Username} зашел в {after.VoiceChannel.Name}");
                }
                
                // Пользователь вышел из голосового канала
                else if (before.VoiceChannel != null && after.VoiceChannel == null)
                {
                    if (_voiceStartTimes.TryGetValue(user.Id, out DateTime startTime))
                    {
                        int minutes = (int)Math.Ceiling((DateTime.UtcNow - startTime).TotalMinutes);
                        
                        if (minutes > 0)
                        {
                            await _db.AddVoiceTimeAsync(user.Id, minutes);
                            WriteLog(LogSeverity.Verbose, $"{user.Username} вышел из {before.VoiceChannel.Name}. Провел {minutes} мин.");
                        }
                        
                        _voiceStartTimes.Remove(user.Id);
                    }
                }
                
                // Пользователь переключился между каналами
                else if (before.VoiceChannel != null && after.VoiceChannel != null && before.VoiceChannel != after.VoiceChannel)
                {
                    WriteLog(LogSeverity.Verbose, $"{user.Username} перешел из {before.VoiceChannel.Name}.");
                }
            }
            catch (Exception ex)
            {
                WriteLog(LogSeverity.Error, $"Ошибка в голосовом статусе: {ex.Message}");
            }
        }
        private static async Task StartActivityUpdater()
        {
            while (true)
            {
                try
                {
                    WriteLog(LogSeverity.Info, $"Обновление активности пользователей...");
                    
                    await _db.UpdateAllUsersActivityAsync(
                        _config.ActivitySettings.MessageCoefficient,
                        _config.ActivitySettings.VoiceCoefficient,
                        _config.ActivitySettings.ActivityThreshold);
                    
                    WriteLog(LogSeverity.Info, $"Активность обновлена. Следующее обновление через {_config.ActivitySettings.UpdateIntervalHours} ч.");
                }
                catch (Exception ex)
                {
                    WriteLog(LogSeverity.Error, $"Ошибка при обновлении активности: {ex.Message}");
                }
                
                await Task.Delay(TimeSpan.FromHours(_config.ActivitySettings.UpdateIntervalHours));
            }
        }
    }
}