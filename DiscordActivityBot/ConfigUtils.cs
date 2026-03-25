using System;
using System.IO;
using System.Text.Json;
using Discord;

namespace DiscordActivityBot
{        
    class ConfigUtils
    {
        public static bool LoadConfig(string path, out BotConfig config)
        {
            try
            {
                string jsonString = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<BotConfig>(jsonString, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                Program.WriteLog(LogSeverity.Info, "Конфиг загружен.");
                return true;
            }
            catch (Exception ex)
            {
                Program.WriteLog(LogSeverity.Critical, $"Ошибка загрузки конфига: {ex.Message}");
                
                if (ex is FileNotFoundException)
                {
                    Program.WriteLog(LogSeverity.Info, "Создание конфига по умолчанию...");
                    config = CreateDefaultConfig(path);
                    return false;
                }
                
                throw;
            }
        }

        public static void SaveConfig(string path, BotConfig config)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(config, options);
            File.WriteAllText(path, jsonString);
            
            Program.WriteLog(LogSeverity.Info, $"Файл  сохранен: {path}");
        }
        
        public static BotConfig CreateDefaultConfig(string path)
        {
            var defaultConfig = new BotConfig
            {
                Token = "YOUR_BOT_TOKEN_HERE",
                Prefix = '!',
                ConnectionStrings = "Server=localhost;Database=DiscordActivityBot;User=root;Password=;",
                LogLevel = "Information",
                AdminIds = new System.Collections.Generic.List<ulong>(),
                ActiveRoleId = null,
                InactiveRoleId = null,
                ActivitySettings = new ActivitySettings()
                {
                    MessageCoefficient = 1,
                    VoiceCoefficient = 0.5,
                    ActivityThreshold = 100,
                    UpdateIntervalHours = 24
                }
            };
            
            SaveConfig(path, defaultConfig);
            
            Program.WriteLog(LogSeverity.Critical, $"НЕ ЗАБУДЬТЕ отредактировать token и connectionStrings в config.json!");
            
            return defaultConfig;
        }
        
        public static LogSeverity GetLogLevel(string level)
        {
            return level?.ToLower() switch
            {
                "critical" => LogSeverity.Critical,
                "error" => LogSeverity.Error,
                "warning" => LogSeverity.Warning,
                "info" or "information" => LogSeverity.Info,
                "verbose" => LogSeverity.Verbose,
                "debug" => LogSeverity.Debug,
                _ => LogSeverity.Info
            };
        }
    }
}