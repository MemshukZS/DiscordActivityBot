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
        public static BotConfig config;
        private static MySQLUtils _db;
        private static LogSeverity _logLevel;
        private static Dictionary<ulong, DateTime> _voiceStartTimes = new Dictionary<ulong, DateTime>();
        private static SocketGuild _guild;
        private static DateTime lastUpdateActive;
        private static DateTime nextAutoUpdateActive;
        
        static async Task Main(string[] args)
        {
           if (!ConfigUtils.LoadConfig(configPath, out Program.config)) return;

            _db = new MySQLUtils(Program.config.ConnectionStrings);

            if (!await _db.TestConnectionAsync()) return;
            await _db.InitializeDatabaseAsync();

            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.GuildMembers,
                AlwaysDownloadUsers = true,
                LogLevel = _logLevel = ConfigUtils.GetLogLevel(Program.config.LogLevel)
            };
            
            _client = new DiscordSocketClient(config);
            
            _client.Log += Log;
            _client.Ready += Ready;
            _client.MessageReceived += MessageReceived;
            _client.UserVoiceStateUpdated += UserVoiceStateUpdated;    
            
            await _client.LoginAsync(TokenType.Bot, Program.config.Token);
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
            
            // Получаем ID первой гильдии (или можно настроить в конфиге)
            _guild = _client.Guilds.FirstOrDefault();
            if (_guild != null)
            {
                WriteLog(LogSeverity.Info, $"Гильдия: {_guild.Name}");
                
                // Запускаем обновление активности
                _ = AutoActivityUpdater();
            }
            else
                WriteLog(LogSeverity.Warning, "Бот не состоит ни в одной гильдии!");    

            if (config.AdminIds.Count() == 0) WriteLog(LogSeverity.Warning, "Админы не заданы в конфиге. Для доступа к админ функциям будет использована проверка на администратора (право Administrator)");
            if (config.ActiveRoleId == null) WriteLog(LogSeverity.Warning, "В конфиге не установлена роль для активных пользователей (ActiveRoleId)");
            if (config.InactiveRoleId == null) WriteLog(LogSeverity.Warning, "В конфиге не установлена роль для не активных пользователей (DeactivateRoleId)");
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
            if (message.Content[0] != config.Prefix) return;

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
                case "loadconfig" or "lc" or "загрузитьконфиг" or "зк":
                    if (!AdminCheck(message.Author)) { _ = SendError(message.Channel, "У вас нет прав администратора для использования данной команды."); return; }
                    if (ConfigUtils.LoadConfig(configPath, out Program.config))
                    {                        
                        embed = new EmbedBuilder()
                            .WithTitle($"Успех!")
                            .WithDescription($"Конфиг загружен.")
                            .WithColor(Color.Green);
                        _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    }
                    else
                        _ = SendError(message.Channel, "Не удалось загрузить конфиг.");
                    break;
                case "help" or "помощь":
                        embed = new EmbedBuilder()
                            .WithTitle($"Помощь!")
                            .WithDescription($@"
                            **Основные команды**

                            `{config.Prefix}пинг` | `{config.Prefix}ping`
                            Проверка работы бота и измерение задержки реакции

                            `{config.Prefix}помощь` | `{config.Prefix}help`
                            Показать это сообщение

                            `{config.Prefix}статистика [пользователь]` | `{config.Prefix}statistics [пользователь]`
                            Статистика активности пользователя. Сокращения: стат и stat.
                            *Примеры:* `{config.Prefix}статистика`, `{config.Prefix}стат @User`, `{config.Prefix}stat 123456789`

                            `{config.Prefix}лидеры | {config.Prefix}leaderboard`
                            Таблица лидеров по активности за неделю. Сокращения: тл и lb.

                            **Администрирование**

                            `{config.Prefix}загрузитьконфиг | {config.Prefix}loadconfig`
                            Загрузить конфиг в память. Сокращения: зк и lc.

                            `{config.Prefix}роль <активный|неактивный> <роль>` | `{config.Prefix}role <active|inactive> <role>`
                            Назначить роли для автоматической выдачи
                            *Аргументы:* название, упоминание (@Роль) или ID роли
                            *Пример:* `{config.Prefix}role активный @Активные`

                            `{config.Prefix}роли` | `{config.Prefix}poles`
                            Просмотр текущих настроенных ролей

                            `{config.Prefix}активность [обновить]` | `{config.Prefix}active [update]`
                            Информация о времени обновления активности. Сокращения: актив.
                            *С параметром `update`* - принудительное обновление статуса

                            **ℹДополнительно**

                            **Период расчета:** последние 30 дней
                            **Формула активности:** Сообщения × {config.ActivitySettings.MessageCoefficient} + Войс-минуты × {config.ActivitySettings.VoiceCoefficient}
                            **Порог активности:** {config.ActivitySettings.ActivityThreshold}
                            **Интервал обновления:** каждые {config.ActivitySettings.UpdateIntervalHours} ч.")
                            .WithColor(Color.Green);
                    
                        _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break;
                case "role" or "роль":
                    if (!AdminCheck(message.Author)) { _ = SendError(message.Channel, "У вас нет прав администратора для использования данной команды."); return; }
                    if (command.Length < 3) { _ = SendError(message.Channel, $"Не достаточно аргуменнтов для использования команды.\n{config.Prefix}Роль <тип> <роль>\nДля подробной информации используйте {config.Prefix}Помощь."); return; }
                    if (!new List<string>{"active", "активный", "inactive", "неактивный"}.Contains(command[1].ToLower())) { _ = SendError(message.Channel, $"\"{command[1]}\" недопустимое значение для аргумента. Используйте: active/inactive или активный/неактивный\nДля подробной информации используйте {config.Prefix}Помощь."); return; }
                    string arg2 = command.Length > 3 ? arg2 = string.Join(' ', command.Skip(1)) : command[2];
                    if (!FindRole((message.Channel as SocketGuildChannel).Guild, arg2, out SocketRole role)) { _ = SendError(message.Channel, $"Не удалось найти роль \"{arg2}\""); return; }

                    bool active = command[1].Equals("active", StringComparison.OrdinalIgnoreCase) || command[1].Equals("активный", StringComparison.OrdinalIgnoreCase);
                    if (active)
                        config.ActiveRoleId = role.Id;
                    else
                        config.InactiveRoleId = role.Id;

                    ConfigUtils.SaveConfig(configPath, config);
                    embed = new EmbedBuilder()
                        .WithTitle($"Успех!")
                        .WithDescription($"В качестве {(active ? "активной" : "неактивной")} роли задана \"{role.Name}\".")
                        .WithColor(role.Color);
                    
                    _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break;           
                case "roles" or "роли":
                    SocketRole activeRole = config.ActiveRoleId.HasValue ? _guild.GetRole(config.ActiveRoleId.Value) : null;
                    SocketRole inactiveRole = config.InactiveRoleId.HasValue ? _guild.GetRole(config.InactiveRoleId.Value) : null;

                    embed = new EmbedBuilder()
                        .WithTitle($"Установленые роли")
                        .WithDescription($"{(activeRole != null ? $"В качестве активной роли задана \"{activeRole.Name}\"." : "Активная роль не задана!")}\n{(inactiveRole != null ? $"В качестве неактивной роли задана \"{inactiveRole.Name}\"." : "Неактивная роль не задана!")}")
                        .WithColor((activeRole == null || inactiveRole == null) ? Color.Red : Color.Green);
                    
                    _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break; 
                case "active" or "активность" or "актив":
                    if (command.Length == 1)
                    {
                        embed = new EmbedBuilder()
                            .WithTitle($"Статус активности")
                            .WithDescription($"Автоматическое обновление активности пользователей каждые **{config.ActivitySettings.UpdateIntervalHours} ч.**\nПоследнее обновление активности пользователей: **{lastUpdateActive:dd.MM HH:mm}**\nСледующее авто. обновление активности пользователей: **{nextAutoUpdateActive:dd.MM HH:mm}**")
                            .WithColor(Color.Green);                        
                    }
                    else
                    {
                        if (!AdminCheck(message.Author)) { _ = SendError(message.Channel, "У вас нет прав администратора для использования аргументов данной команды."); return; }
                        if (!new List<string>{"update", "обновить"}.Contains(command[1].ToLower())) { _ = SendError(message.Channel, $"\"{command[1]}\" недопустимое значение для аргумента. Используйте: update/обновить\nДля подробной информации используйте {config.Prefix}Помощь."); return; }
                    
                        embed = new EmbedBuilder()
                            .WithTitle($"Обновление активности")
                            .WithDescription($"Запущено принудительное обновление активности пользователей\nПоследнее обновление активности пользователей: **{lastUpdateActive:dd.MM HH:mm}**\nСледующее авто. обновление активности пользователей: **{nextAutoUpdateActive:dd.MM HH:mm}**")
                            .WithColor(Color.Green);  

                        _ = ActivityUpdater();
                    }
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

                    (int totalMessages, int totalVoiceTime, DateOnly start, DateOnly end) = await _db.GetUserStatsAsync(user.Id);
                    
                    if (totalMessages + totalVoiceTime == 0)
                    {                        
                        embed = new EmbedBuilder()
                            .WithTitle($"Статистика {_guild.GetUser(user.Id).DisplayName}.")
                            .WithDescription("Данные отсутствуют.")
                            .WithColor(Color.Gold);
                    }
                    else
                    {
                        embed = new EmbedBuilder()
                            .WithTitle($"Статистика {_guild.GetUser(user.Id).DisplayName} {(start == end ? $" за {start:dd.MM}" : $"c {start:dd.MM} по {end:dd.MM}" )}.")
                            .AddField("Активность", $"{totalMessages * config.ActivitySettings.MessageCoefficient + totalVoiceTime * config.ActivitySettings.VoiceCoefficient}", true)
                            .AddField("Сообщения", $"{totalMessages} шт.", true)
                            .AddField("Войс", FormatVoiceTimeCompact(totalVoiceTime), true)
                            .WithColor(Color.Green);
                    }
                    _ = message.Channel.SendMessageAsync(embed: embed.Build());
                    break;
                case "leaderboard" or "lb" or "лидеры" or "тл":
                    List<(ulong userId, int messageCount, int voiceTime, double activityScore)> users = await _db.GetTopUsersAsync();

                    embed = new EmbedBuilder()
                        .WithTitle($"Таблица лидеров.")
                        .WithDescription($"Список самых активных пользователей! отображено: {users.Count}/{_guild.MemberCount}")
                        .WithColor(Color.Gold);
                    
                    for (int i = 0; i < users.Count; i++)
                    {
                        var luser = _guild.GetUser(users[i].userId)?.DisplayName;
                        if (luser == null) luser = "Пользователь не найден";
                        embed.AddField($"{i + 1} - {luser}", users[i].activityScore); 
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
            config.AdminIds.Count > 0 ?
            config.AdminIds.Contains(user.Id) :
            (user as SocketGuildUser).GuildPermissions.Administrator;
        private static string FormatVoiceTimeCompact(int minutes)
        {
            if (minutes < 60) return $"{minutes} мин.";
            int hours = minutes / 60;
            int mins = minutes % 60;
            return mins == 0 ? $"{hours} ч." : $"{hours} ч. {mins} мин.";
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
        
        private static async Task AutoActivityUpdater()
        {
            bool firstLoad = true;
            while (true)
            {
                _ = ActivityUpdater(firstLoad);
                firstLoad = false;
                byte hours = (byte)config.ActivitySettings.UpdateIntervalHours;
                nextAutoUpdateActive = DateTime.Now.AddHours(hours);
                await Task.Delay(TimeSpan.FromHours(hours));
            }
        }

        private static async Task ActivityUpdater(bool firstLoad = false)
        {
            try
            {
                WriteLog(LogSeverity.Info, $"Проверка активности пользователей...");
                SocketRole activeRole = config.ActiveRoleId.HasValue ? _guild.GetRole(config.ActiveRoleId.Value) : null;
                SocketRole inactiveRole = config.InactiveRoleId.HasValue ? _guild.GetRole(config.InactiveRoleId.Value) : null;
                if (activeRole == null) WriteLog(LogSeverity.Warning, $"Не удалось получить Активную роль, возможно ActiveRoleId задан не коректно.");
                if (inactiveRole == null) WriteLog(LogSeverity.Warning, $"Не удалось получить Неактивную роль, возможно InactiveRoleId задан не коректно.");
                
                await _db.CleanupOldRecordsAsync();
                var Users = await _db.GetUsersActivityUpdatedAsync(firstLoad);
                _ = _db.UpdateUsersActivityAsync();

                var counter = 0;
                foreach ((ulong uid, bool oldActive) in Users)
                {
                    var user =_guild.GetUser(uid);
                    if (user == null) { WriteLog(LogSeverity.Warning, $"Пользователь {uid} не обнаружен. Возможно он покинул гильдию."); continue; }

                    (var removeRole, var addRole) = oldActive ? (activeRole, inactiveRole) : (inactiveRole, activeRole);
                    byte editRole = 0;
                    if (removeRole != null && user.Roles.Contains(removeRole)) { editRole++; _ = user.RemoveRoleAsync(removeRole); } 
                    if (addRole != null && !user.Roles.Contains(addRole)) { editRole++; _ = user.AddRoleAsync(addRole); }

                    if (editRole != 0) counter++;
                };
                
                lastUpdateActive = DateTime.Now;

                WriteLog(LogSeverity.Info, Users.Count > 0 ? $"Активность изменена у {counter}/{Users.Count} пользователей." : "Активность пользователей не изменилась.");
            }
            catch (Exception ex)
            {
                WriteLog(LogSeverity.Error, $"Ошибка при обновлении активности: {ex.Message}");
            }
        }
    }
}