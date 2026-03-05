using System.Collections.Generic;

namespace DiscordActivityBot
{
    public class BotConfig
    {
        public string Token { get; set; }
        public char Prefix { get; set; }        
        public string ConnectionStrings { get; set; }        
        public List<ulong> AdminIds { get; set; }
        public ulong? ActiveRoleId { get; set; }
        public ulong? InactivaRoleId { get; set; }
        public string LogLevel { get; set; }
        public ActivitySettings ActivitySettings { get; set; }
    }

    public class ActivitySettings
    {
        public double MessageCoefficient { get; set; }
        public double VoiceCoefficient { get; set; }
        public double ActivityThreshold { get; set; }
        public int UpdateIntervalHours { get; set; }
    }
    
}