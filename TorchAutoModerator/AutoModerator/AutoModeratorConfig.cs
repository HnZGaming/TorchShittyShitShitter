using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using AutoModerator.Punishes;
using AutoModerator.Punishes.Broadcasts;
using AutoModerator.Warnings;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch;
using Torch.Views;
using Utils.Torch;
using VRage.Game.ModAPI;

namespace AutoModerator
{
    public sealed class AutoModeratorConfig :
        ViewModel,
        FileLoggingConfigurator.IConfig,
        Core.AutoModerator.IConfig
    {
        const string OpGroupName = "Auto Moderator";
        const string OpGridGroupName = "Auto Moderator (Grids)";
        const string OpPlayerGroupName = "Auto Moderator (Players)";
        const string BroadcastGroupName = "Punishment (Broadcast)";
        const string DamageGroupName = "Punishment (Damage)";
        const string WarningGroupName = "Warning";
        const string WarningNotificationGroupName = "Warning (Notification)";
        const string WarningQuestlogGroupName = "Warning (Questlog)";
        const string PunishGroupName = "Punishment";
        const string LogGroupName = "_Logging_";
        public const string DefaultLogFilePath = "Logs/AutoModerator-${shortdate}.log";

        bool _enableLagWarningNotification = true;
        double _firstIdleTime = 180;
        MyPromoteLevel _broadcastVisiblePromoLevel = MyPromoteLevel.Admin;
        int _maxLaggyGpsCountPerScan = 3;
        double _trackingTime = 300d;
        double _punishTime = 600d;
        double _maxGridMspf = 0.5f;
        double _maxPlayerMspf = 0.5f;
        double _sampleFrequency = 5;
        double _warningNormal = 0.7d;
        bool _exemptNpcFactions = true;
        string _gridGpsNameFormat = "[{faction}] {grid} {ratio} ({time})";
        string _gridGpsDescriptionFormat = "The {rank} laggiest grid. Get 'em!";
        string _gpsColor = "#FF00FF";
        List<ulong> _gpsMutedPlayerIds = new List<ulong>();
        List<string> _exemptFactionTags = new List<string>();
        bool _suppressWpfOutput;
        bool _enableLoggingTrace;
        bool _enableLoggingDebug;
        bool _enableWarningQuestlog = true;
        string _logFilePath = DefaultLogFilePath;
        string _warningTitle = LagWarningDefaultTexts.Title;
        string _warningDetailMustProfileSelfText = LagWarningDefaultTexts.MustProfileSelf;
        string _warningDetailMustDelagSelfText = LagWarningDefaultTexts.MustDelagSelf;
        string _warningDetailMustWaitUnpinnedText = LagWarningDefaultTexts.MustWaitUnpinned;
        string _warningDetailEndedText = LagWarningDefaultTexts.Ended;
        LagPunishType _punishType;
        double _damageNormal = 0.05d;
        string _warningCurrentLevelText = LagWarningDefaultTexts.CurrentLevel;
        double _minIntegrityNormal = 0.5d;
        bool _enablePunishChatFeed = true;
        string _punishReportChatName = "Auto Moderator";
        string _punishReportChatFormat = "[{faction}] {player} \"{grid}\" ({level})";
        double _outlierFenceNormal = 2;
        double _gracePeriodTime = 20;
        bool _isEnabled = true;
        List<string> _exemptBlockTypePairs = new List<string>();

        [XmlElement]
        [Display(Order = 1, Name = "Enable plugin", GroupName = OpGroupName)]
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetValue(ref _isEnabled, value);
        }

        [XmlElement]
        [Display(Order = 2, Name = "First idle seconds", GroupName = OpGroupName,
            Description = "Waits for N seconds when the session starts. Game is generally laggy at startup due to concealment or cleanup.")]
        public double FirstIdleTime
        {
            get => _firstIdleTime;
            set => SetValue(ref _firstIdleTime, value);
        }

        [XmlElement]
        [Display(Order = 5, Name = "Interval frequency (seconds)", GroupName = OpGroupName,
            Description = "Profiles N seconds per interval.")]
        public double IntervalFrequency
        {
            get => _sampleFrequency;
            set => SetValue(ref _sampleFrequency, Math.Max(value, 5));
        }

        [ConfigProperty(MyPromoteLevel.None)]
        [XmlElement]
        [Display(Order = 6, Name = "Tracking time (seconds)", GroupName = OpGroupName,
            Description = "Gives players a chance of N seconds before the punishment of per-grid lag violation.")]
        public double TrackingTime
        {
            get => _trackingTime;
            set => SetValue(ref _trackingTime, value);
        }

        [ConfigProperty(MyPromoteLevel.None)]
        [XmlElement]
        [Display(Order = 7, Name = "Pinned time (seconds)", GroupName = OpGroupName,
            Description = "Punishes players for N seconds for per-grid lag violation.")]
        public double PunishTime
        {
            get => _punishTime;
            set => SetValue(ref _punishTime, value);
        }

        [ConfigProperty(MyPromoteLevel.None)]
        [XmlElement]
        [Display(Order = 10, Name = "Grace period (seconds)", GroupName = OpGroupName,
            Description = "Grids younger than N seconds will not be warned/punished.")]
        public double GracePeriodTime
        {
            get => _gracePeriodTime;
            set => SetValue(ref _gracePeriodTime, value);
        }

        [XmlElement]
        [Display(Order = 20, Name = "Outlier fence normal", GroupName = OpGroupName,
            Description = "Ignores spontaneous lags (N times larger than the standard deviation) of given grid/player's timeline.")]
        public double OutlierFenceNormal
        {
            get => _outlierFenceNormal;
            set => SetValue(ref _outlierFenceNormal, value);
        }

        [XmlElement]
        [Display(Order = 22, Name = "Ignore NPC factions", GroupName = OpGroupName)]
        public bool IgnoreNpcFactions
        {
            get => _exemptNpcFactions;
            set => SetValue(ref _exemptNpcFactions, value);
        }

        [XmlElement]
        [Display(Order = 24, Name = "Exempt faction tags", GroupName = OpGroupName)]
        public List<string> ExemptFactionTags
        {
            get => _exemptFactionTags;
            set => SetValue(ref _exemptFactionTags, new HashSet<string>(value).ToList());
        }

        [XmlElement]
        [Display(Order = 3, Name = "Max grid ms/f", GroupName = OpGridGroupName,
            Description = "Allows N milliseconds per game loop for each grid to consume.")]
        public double MaxGridMspf
        {
            get => _maxGridMspf;
            set => SetValue(ref _maxGridMspf, Math.Max(value, 0.001f));
        }

        [XmlElement]
        [Display(Order = 3, Name = "Max player ms/f", GroupName = OpPlayerGroupName,
            Description = "Allows N milliseconds per game loop for each player to consume.")]
        public double MaxPlayerMspf
        {
            get => _maxPlayerMspf;
            set => SetValue(ref _maxPlayerMspf, value);
        }

        [XmlElement]
        [Display(Order = 1, Name = "Lag threshold (0-1)", GroupName = WarningGroupName,
            Description = "Send a warning to players when they exceed N times the max allowed lag per grid or player.")]
        public double WarningLagNormal
        {
            get => _warningNormal;
            set => SetValue(ref _warningNormal, value);
        }

        [XmlElement]
        [Display(Order = 0, Name = "Enable warning notification", GroupName = WarningNotificationGroupName)]
        public bool EnableLagWarningNotification
        {
            get => _enableLagWarningNotification;
            set => SetValue(ref _enableLagWarningNotification, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 1, Name = "Current level", GroupName = WarningNotificationGroupName)]
        public string WarningCurrentLevelText
        {
            get => _warningCurrentLevelText;
            set => SetValue(ref _warningCurrentLevelText, value);
        }

        [XmlElement]
        [Display(Order = 0, Name = "Enable Questlog", GroupName = WarningQuestlogGroupName)]
        public bool EnableWarningQuestlog
        {
            get => _enableWarningQuestlog;
            set => SetValue(ref _enableWarningQuestlog, value);
        }

        public bool EnableWarningChatFeed => !_enableWarningQuestlog;

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 2, Name = "Title", GroupName = WarningQuestlogGroupName)]
        public string WarningTitle
        {
            get => _warningTitle;
            set => SetValue(ref _warningTitle, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 3, Name = "Detail (1)", GroupName = WarningQuestlogGroupName)]
        public string WarningDetailMustProfileSelfText
        {
            get => _warningDetailMustProfileSelfText;
            set => SetValue(ref _warningDetailMustProfileSelfText, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 4, Name = "Detail (2)", GroupName = WarningQuestlogGroupName)]
        public string WarningDetailMustDelagSelfText
        {
            get => _warningDetailMustDelagSelfText;
            set => SetValue(ref _warningDetailMustDelagSelfText, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 5, Name = "Detail (3)", GroupName = WarningQuestlogGroupName)]
        public string WarningDetailMustWaitUnpinnedText
        {
            get => _warningDetailMustWaitUnpinnedText;
            set => SetValue(ref _warningDetailMustWaitUnpinnedText, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 6, Name = "Detail (4)", GroupName = WarningQuestlogGroupName)]
        public string WarningDetailEndedText
        {
            get => _warningDetailEndedText;
            set => SetValue(ref _warningDetailEndedText, value);
        }

        [ConfigProperty(MyPromoteLevel.None)]
        [XmlElement]
        [Display(Order = 1, Name = "Punishment type", GroupName = PunishGroupName)]
        public LagPunishType PunishType
        {
            get => _punishType;
            set => SetValue(ref _punishType, value);
        }

        [XmlElement]
        [Display(Order = 2, Name = "Enable punishment chat", GroupName = PunishGroupName)]
        public bool EnablePunishChatFeed
        {
            get => _enablePunishChatFeed;
            set => SetValue(ref _enablePunishChatFeed, value);
        }

        [XmlElement]
        [Display(Order = 3, Name = "Chat name", GroupName = PunishGroupName)]
        public string PunishReportChatName
        {
            get => _punishReportChatName;
            set => SetValue(ref _punishReportChatName, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 4, Name = "Chat format", GroupName = PunishGroupName)]
        public string PunishReportChatFormat
        {
            get => _punishReportChatFormat;
            set => SetValue(ref _punishReportChatFormat, value);
        }

        [XmlElement]
        [Display(Order = 200, Name = "Exempt block types", GroupName = PunishGroupName)]
        public List<string> ExemptBlockTypePairs
        {
            get => _exemptBlockTypePairs;
            set => SetValue(ref _exemptBlockTypePairs, value);
        }

        IEnumerable<string> Core.AutoModerator.IConfig.ExemptBlockTypePairs => ExemptBlockTypePairs;

        [XmlElement]
        [Display(Order = 2, Name = "Damage per interval (0-1)", GroupName = DamageGroupName,
            Description = "Applies damage to subject blocks by N times the block type's max integrity.")]
        public double DamageNormalPerInterval
        {
            get => _damageNormal;
            set => SetValue(ref _damageNormal, value);
        }

        [XmlElement]
        [Display(Order = 2, Name = "Lowest integrity (0-1)", GroupName = DamageGroupName,
            Description = "Applies damage to subject blocks until reaching N times integrity.")]
        public double MinIntegrityNormal
        {
            get => _minIntegrityNormal;
            set => SetValue(ref _minIntegrityNormal, value);
        }

        [XmlElement]
        [Display(Order = 5, Name = "Broadcast visible promo level", GroupName = BroadcastGroupName,
            Description = "Broadcasts GPS to permitted players only.")]
        public MyPromoteLevel GpsVisiblePromoteLevel
        {
            get => _broadcastVisiblePromoLevel;
            set => SetValue(ref _broadcastVisiblePromoLevel, value);
        }

        [ConfigProperty(MyPromoteLevel.None)]
        [XmlElement]
        [Display(Order = 6, Name = "Max GPS count", GroupName = BroadcastGroupName,
            Description = "Shows N number of GPS of laggy grids on every player's HUD.")]
        public int MaxGpsCount
        {
            get => _maxLaggyGpsCountPerScan;
            set => SetValue(ref _maxLaggyGpsCountPerScan, value);
        }

        [XmlElement]
        [Display(Order = 12, Name = "Muted players", GroupName = BroadcastGroupName,
            Description = "Won't send chat or GPS to muted players.")]
        public List<ulong> GpsMutedPlayerIds
        {
            get => _gpsMutedPlayerIds;
            set => SetValue(ref _gpsMutedPlayerIds, new HashSet<ulong>(value).ToList());
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 17, Name = "GPS name format", GroupName = BroadcastGroupName)]
        public string GpsNameFormat
        {
            get => _gridGpsNameFormat;
            set => SetValue(ref _gridGpsNameFormat, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 18, Name = "GPS description format", GroupName = BroadcastGroupName)]
        public string GpsDescriptionFormat
        {
            get => _gridGpsDescriptionFormat;
            set => SetValue(ref _gridGpsDescriptionFormat, value);
        }

        [XmlElement]
        [Display(Order = 19, Name = "GPS text color", GroupName = BroadcastGroupName)]
        public string GpsColorCode
        {
            get => _gpsColor;
            set => SetValue(ref _gpsColor, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 12, Name = "Suppress Console Output", GroupName = LogGroupName)]
        public bool SuppressWpfOutput
        {
            get => _suppressWpfOutput;
            set => SetValue(ref _suppressWpfOutput, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 13, Name = "Enable Logging Trace", GroupName = LogGroupName)]
        public bool EnableLoggingTrace
        {
            get => _enableLoggingTrace;
            set => SetValue(ref _enableLoggingTrace, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 13, Name = "Enable Logging Debug", GroupName = LogGroupName)]
        public bool EnableLoggingDebug
        {
            get => _enableLoggingDebug;
            set => SetValue(ref _enableLoggingDebug, value);
        }

        [ConfigPropertyIgnore]
        [XmlElement]
        [Display(Order = 14, Name = "Log File Path", GroupName = LogGroupName)]
        public string LogFilePath
        {
            get => _logFilePath;
            set => SetValue(ref _logFilePath, value);
        }

        IEnumerable<ulong> BroadcastListenerCollection.IConfig.GpsMutedPlayers => _gpsMutedPlayerIds;

        public void AddMutedPlayer(ulong mutedPlayerId)
        {
            if (!_gpsMutedPlayerIds.Contains(mutedPlayerId))
            {
                _gpsMutedPlayerIds.Add(mutedPlayerId);
                OnPropertyChanged(nameof(GpsMutedPlayerIds));
            }
        }

        public void RemoveMutedPlayer(ulong unmutedPlayerId)
        {
            if (_gpsMutedPlayerIds.Remove(unmutedPlayerId))
            {
                OnPropertyChanged(nameof(GpsMutedPlayerIds));
            }
        }

        public void RemoveAllMutedPlayers()
        {
            _gpsMutedPlayerIds.Clear();
            OnPropertyChanged(nameof(GpsMutedPlayerIds));
        }

        public void AddExemptBlockType(string blockType)
        {
            if (!_exemptBlockTypePairs.Contains(blockType))
            {
                _exemptBlockTypePairs.Add(blockType);
                OnPropertyChanged(nameof(ExemptBlockTypePairs));
            }
        }

        public void RemoveExemptBlockType(string blockType)
        {
            if (_exemptBlockTypePairs.Remove(blockType))
            {
                OnPropertyChanged(nameof(ExemptBlockTypePairs));
            }
        }

        public bool IsIdentityExempt(long identityId)
        {
            var isNpc = Sync.Players.IdentityIsNpc(identityId);
            if (isNpc && IgnoreNpcFactions) return true;

            var faction = (IMyFaction) MySession.Static.Factions.GetPlayerFaction(identityId);
            if (faction == null) return false;

            return _exemptFactionTags.Contains(faction.Tag);
        }
    }
}