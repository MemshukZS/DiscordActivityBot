using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using MySqlConnector;

namespace DiscordActivityBot
{
    public class MySQLUtils
    {
        private readonly string _connectionString;

        public MySQLUtils(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                Program.WriteLog(LogSeverity.Info, "Подключение к MySQL установленно!");
                return true;
            }
            catch (Exception ex)
            {
                Program.WriteLog(LogSeverity.Critical, $"Ошибка подключения к MySQL: {ex.Message}");
                return false;
            }
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string createUserTable = @"
                CREATE TABLE IF NOT EXISTS `User` (
                    `UID` BIGINT NOT NULL,
                    `Active` TINYINT NOT NULL DEFAULT '0',
                    PRIMARY KEY (`UID`)
                ) ENGINE=InnoDB
                DEFAULT CHARACTER SET = utf8mb4
                COLLATE = utf8mb4_0900_ai_ci;";

            string createStatsTable = @"
                CREATE TABLE IF NOT EXISTS `UserStatistics` (
                    `UID` BIGINT NOT NULL,
                    `Date` DATE NOT NULL,
                    `MessageCount` INT NOT NULL DEFAULT '0',
                    `VoiceTime` INT NOT NULL DEFAULT '0',
                    PRIMARY KEY (`UID`, `Date`)
                ) ENGINE=InnoDB
                DEFAULT CHARACTER SET = utf8mb4
                COLLATE = utf8mb4_0900_ai_ci;";

            using var cmd1 = new MySqlCommand(createUserTable, connection);
            await cmd1.ExecuteNonQueryAsync();

            using var cmd2 = new MySqlCommand(createStatsTable, connection);
            await cmd2.ExecuteNonQueryAsync();

            Program.WriteLog(LogSeverity.Info, "Таблицы инициализированы");
        }

        public async Task UpdateUserAsync(ulong userId, bool active)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                INSERT INTO `User` (`UID`, `Active`) 
                VALUES (@uid, @active)
                ON DUPLICATE KEY UPDATE 
                `Active` = @active";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);
            cmd.Parameters.AddWithValue("@active", active ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureUserExistsAsync(ulong userId)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            
            string sql = @"
                INSERT INTO `User` (`UID`, `Active`) 
                VALUES (@uid, 0)
                ON DUPLICATE KEY UPDATE `UID` = `UID`";  // Ничего не меняем, если уже есть
            
            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task AddMessageStatAsync(ulong userId)
        {
            await EnsureUserExistsAsync(userId);
            
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                INSERT INTO `UserStatistics` (`UID`, `Date`, `MessageCount`) 
                VALUES (@uid, CURDATE(), 1)
                ON DUPLICATE KEY UPDATE 
                `MessageCount` = `MessageCount` + 1";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task AddVoiceTimeAsync(ulong userId, int minutes)
        {
            await EnsureUserExistsAsync(userId);
            
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                INSERT INTO `UserStatistics` (`UID`, `Date`, `VoiceTime`) 
                VALUES (@uid, CURDATE(), @minutes)
                ON DUPLICATE KEY UPDATE 
                `VoiceTime` = `VoiceTime` + @minutes";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);
            cmd.Parameters.AddWithValue("@minutes", minutes);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<(int totalMessages, int totalVoiceTime, int activeDays)> GetUserStatsAsync(ulong userId)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                SELECT 
                    COALESCE(SUM(MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(VoiceTime), 0) as TotalVoiceTime,
                    COUNT(*) as ActiveDays
                FROM UserStatistics
                WHERE UID = @uid";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)
                );
            }

            return (0, 0, 0);
        }

        public async Task<List<(ulong userId, int messageCount, int voiceTime)>> GetTopUsersAsync(int days = 7, int limit = 10)
        {
            var result = new List<(ulong userId, int messageCount, int voiceTime)>();

            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                SELECT 
                    UID,
                    COALESCE(SUM(MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(VoiceTime), 0) as TotalVoiceTime
                FROM UserStatistics
                WHERE Date >= DATE_SUB(CURDATE(), INTERVAL @days DAY)
                GROUP BY UID
                ORDER BY TotalMessages DESC
                LIMIT @limit";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@days", days);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add((
                    (ulong)reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)
                ));
            }

            return result;
        }

        public async Task<double> CalculateUserActivityAsync(ulong userId, double messageCoeff, double voiceCoeff)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // Считаем за последние 30 дней
            string sql = @"
                SELECT 
                    COALESCE(SUM(MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(VoiceTime), 0) as TotalVoiceTime
                FROM UserStatistics
                WHERE UID = @uid AND Date >= DATE_SUB(CURDATE(), INTERVAL 30 DAY)";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@uid", (long)userId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                int messages = reader.GetInt32(0);
                int voiceMinutes = reader.GetInt32(1);
                
                return (messages * messageCoeff) + (voiceMinutes * voiceCoeff);
            }

            return 0;
        }

        public async Task UpdateAllUsersActivityAsync(double messageCoeff, double voiceCoeff, double threshold)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // Получаем всех пользователей с их статистикой за 30 дней
            string sql = @"
                SELECT 
                    u.UID,
                    COALESCE(SUM(us.MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(us.VoiceTime), 0) as TotalVoiceTime
                FROM User u
                LEFT JOIN UserStatistics us ON u.UID = us.UID 
                    AND us.Date >= DATE_SUB(CURDATE(), INTERVAL 30 DAY)
                GROUP BY u.UID";

            var users = new List<(ulong uid, int messages, int voice, bool wasActive)>();
            
            using (var cmd = new MySqlCommand(sql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    users.Add((
                        (ulong)reader.GetInt64(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        false
                    ));
                }
            }

            // Обновляем активность каждого пользователя
            int activeCount = 0;
            foreach (var user in users)
            {
                double activity = (user.messages * messageCoeff) + (user.voice * voiceCoeff);
                bool isActive = activity >= threshold;
                
                if (isActive) activeCount++;
                
                string updateSql = "UPDATE User SET Active = @active WHERE UID = @uid";
                using var updateCmd = new MySqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@uid", (long)user.uid);
                updateCmd.Parameters.AddWithValue("@active", isActive ? 1 : 0);
                await updateCmd.ExecuteNonQueryAsync();
            }

            Program.WriteLog(LogSeverity.Info, $"Обновлена активность пользователей: {activeCount} активных из {users.Count}");
        }

        public async Task<int> CleanupOldRecordsAsync(int daysToKeep = 30)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = "DELETE FROM UserStatistics WHERE Date < DATE_SUB(CURDATE(), INTERVAL @days DAY)";
            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@days", daysToKeep);
            
            int deleted = await cmd.ExecuteNonQueryAsync();
            Program.WriteLog(LogSeverity.Info, $"Удалено {deleted} старых записей");
            
            return deleted;
        }
    }
}