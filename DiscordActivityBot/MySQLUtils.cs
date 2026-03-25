using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
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

        public async Task<(int totalMessages, int totalVoiceTime, DateOnly startDate, DateOnly endDate)> GetUserStatsAsync(ulong userId)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                SELECT 
                    COALESCE(SUM(MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(VoiceTime), 0) as TotalVoiceTime,
                    MIN(Date) as MinDate,
                    MAX(Date) as MaxDate
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
                    reader.GetDateOnly(2),
                    reader.GetDateOnly(3)
                );
            }

            return (0, 0, new DateOnly(), new DateOnly());
        }

        public async Task<List<(ulong userId, int messageCount, int voiceTime, double activityScore)>> GetTopUsersAsync(int days = 7, int limit = 10)
        {
            var result = new List<(ulong userId, int messageCount, int voiceTime, double activityScore)>();

            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"
                SELECT 
                    UID,
                    COALESCE(SUM(MessageCount), 0) as TotalMessages,
                    COALESCE(SUM(VoiceTime), 0) as TotalVoiceTime,
                    COALESCE(SUM(MessageCount), 0) * @messageCoeff + 
                    COALESCE(SUM(VoiceTime), 0) * @voiceCoeff as ActivityScore
                FROM UserStatistics
                WHERE Date >= DATE_SUB(CURDATE(), INTERVAL @days DAY)
                GROUP BY UID
                ORDER BY ActivityScore DESC
                LIMIT @limit";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@days", days);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@messageCoeff", Program.config.ActivitySettings.MessageCoefficient);
            cmd.Parameters.AddWithValue("@voiceCoeff", Program.config.ActivitySettings.VoiceCoefficient);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add((
                    (ulong)reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3)
                ));
            }

            return result;
        }
        public async Task UpdateUsersActivityAsync()
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"UPDATE User u
            LEFT JOIN (
                SELECT UID, COALESCE(SUM(MessageCount), 0) * @messageCoeff + COALESCE(SUM(VoiceTime), 0) * @voiceCoeff as ActivityScore
                FROM UserStatistics 
                WHERE Date >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) 
                GROUP BY UID
            ) us ON u.UID = us.UID
            SET u.Active = us.ActivityScore >= @threshold
            WHERE (us.ActivityScore >= @threshold) != (u.Active = 1);";

            using var cmd = new MySqlCommand(sql, connection);
        }
        public async Task<List<(ulong, bool)>> GetUsersActivityUpdatedAsync(bool firstLoad)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"WITH UserStats AS (
                    SELECT UID,
                        COALESCE(SUM(MessageCount), 0) * @messageCoeff + 
                        COALESCE(SUM(VoiceTime), 0) * @voiceCoeff as Score
                    FROM UserStatistics 
                    WHERE Date >= DATE_SUB(CURDATE(), INTERVAL 30 DAY) 
                    GROUP BY UID
                )";
                sql += firstLoad ?
                    @"SELECT u.UID, (us.Score < @threshold) FROM User u
                LEFT JOIN UserStats us ON u.UID = us.UID;"
                :   @"SELECT u.UID, u.Active FROM User u
                LEFT JOIN UserStats us ON u.UID = us.UID
                WHERE (us.Score >= @threshold) != u.Active;";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@messageCoeff", Program.config.ActivitySettings.MessageCoefficient);
            cmd.Parameters.AddWithValue("@voiceCoeff", Program.config.ActivitySettings.VoiceCoefficient);
            cmd.Parameters.AddWithValue("@threshold", Program.config.ActivitySettings.ActivityThreshold);

            using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<(ulong, bool)>();

            while (await reader.ReadAsync())
                result.Add(((ulong)reader.GetInt64(0), reader.GetBoolean(1)));

            return result;
        }

        public async Task CleanupOldRecordsAsync(int daysToKeep = 30)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = "DELETE FROM UserStatistics WHERE Date < DATE_SUB(CURDATE(), INTERVAL @days DAY)";
            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@days", daysToKeep);
            
            int deleted = await cmd.ExecuteNonQueryAsync();
            if (deleted > 0)
                Program.WriteLog(LogSeverity.Verbose, $"Удалено {deleted} старых записей");
            
            return;
        }
    }
}