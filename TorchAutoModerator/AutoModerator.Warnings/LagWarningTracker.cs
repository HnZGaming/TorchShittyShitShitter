using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox.Game.World;
using Utils.General;
using Utils.Torch;

namespace AutoModerator.Warnings
{
    public sealed class LagWarningTracker
    {
        public interface IConfig
        {
            double WarningLagNormal { get; }
        }

        public interface IListener
        {
        }

        public interface IQuestListener : IListener
        {
            void OnQuestUpdated(long playerId, LagQuest quest);
        }

        public interface ILagListener : IListener
        {
            void OnLagCleared(long playerId);
            void OnLagUpdated(LagWarningSource player);
        }

        static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        readonly IConfig _config;
        readonly HashSet<IListener> _listeners;
        readonly ConcurrentDictionary<long, LagPlayerState> _quests;
        readonly HashSet<long> _lastOnlinePlayerIds;

        public LagWarningTracker(IConfig config)
        {
            _config = config;
            _listeners = new HashSet<IListener>();
            _quests = new ConcurrentDictionary<long, LagPlayerState>();
            _lastOnlinePlayerIds = new HashSet<long>();
        }

        public void AddListener(IListener stateListener)
        {
            _listeners.Add(stateListener);
        }

        public void RemoveListener(IListener stateListener)
        {
            _listeners.Remove(stateListener);
        }

        public void ClearListeners()
        {
            _listeners.Clear();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Clear()
        {
            foreach (var (playerId, _) in _quests)
            {
                OnPlayerQuestUpdated(playerId, LagQuest.Cleared);
                OnPlayerLagCleared(playerId);
            }

            _quests.Clear();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Remove(long playerId)
        {
            _quests.Remove(playerId);
            OnPlayerQuestUpdated(playerId, LagQuest.Cleared);
            OnPlayerLagCleared(playerId);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Update(IEnumerable<LagWarningSource> players)
        {
            // clear quest log of just-logged-in players
            {
                var onlinePlayerIds = MySession.Static.Players.GetOnlinePlayers().Select(p => p.PlayerId()).ToSet();
                var newPlayerIds = new HashSet<long>();
                newPlayerIds.UnionWith(onlinePlayerIds);
                newPlayerIds.ExceptWith(_lastOnlinePlayerIds);
                foreach (var newPlayerId in newPlayerIds)
                {
                    if (!_quests.ContainsKey(newPlayerId))
                    {
                        OnPlayerQuestUpdated(newPlayerId, LagQuest.Cleared);
                        OnPlayerLagCleared(newPlayerId);
                        Log.Info($"Cleared quest log state for player: {newPlayerId}");
                    }
                }

                _lastOnlinePlayerIds.Clear();
                _lastOnlinePlayerIds.UnionWith(onlinePlayerIds);
            }

            var laggyPlayers = players
                .Where(p => p.LongLagNormal >= _config.WarningLagNormal || p.IsPinned)
                .ToArray();

            foreach (var laggyPlayer in laggyPlayers)
            {
                var playerId = laggyPlayer.PlayerId;
                var lag = laggyPlayer.LongLagNormal;

                // new entry
                if (!_quests.TryGetValue(playerId, out var playerState))
                {
                    var newPlayerState = new LagPlayerState
                    {
                        Quest = LagQuest.MustProfileSelf,
                        Latest = laggyPlayer,
                        LastWarningLagNormal = laggyPlayer.LongLagNormal / _config.WarningLagNormal,
                    };

                    _quests[playerId] = newPlayerState;
                    OnPlayerQuestUpdated(playerId, newPlayerState.Quest);

                    Log.Info($"new warning issued: \"{laggyPlayer.PlayerName}\" {lag * 100:0}%");
                }
                else if (laggyPlayer.IsPinned && playerState.Quest < LagQuest.MustWaitUnpinned)
                {
                    playerState.Quest = LagQuest.MustWaitUnpinned;
                    OnPlayerQuestUpdated(playerId, playerState.Quest);
                }
                else if (!(lag > _config.WarningLagNormal) && playerState.Quest <= LagQuest.MustDelagSelf)
                {
                    playerState.Quest = LagQuest.Ended;
                    OnPlayerQuestUpdated(playerId, playerState.Quest);
                }
                else if (lag > _config.WarningLagNormal && playerState.Quest >= LagQuest.Ended)
                {
                    playerState.Quest = LagQuest.MustProfileSelf;
                    OnPlayerQuestUpdated(playerId, playerState.Quest);
                }
                else if (!laggyPlayer.IsPinned && playerState.Quest == LagQuest.MustWaitUnpinned)
                {
                    playerState.Quest = lag > _config.WarningLagNormal ? LagQuest.MustDelagSelf : LagQuest.Ended;
                    OnPlayerQuestUpdated(playerId, playerState.Quest);
                }

                var playerQuestState = _quests[playerId];
                playerQuestState.Latest = laggyPlayer;
                playerQuestState.LastWarningLagNormal = laggyPlayer.LongLagNormal / _config.WarningLagNormal;

                OnPlayerLagUpdated(laggyPlayer);
            }

            var latestLaggyPlayerIds = laggyPlayers.Select(s => s.PlayerId).ToSet();
            foreach (var (existingPlayerId, state) in _quests.ToArray())
            {
                // remove quests ended during the last interval
                if (state.Quest >= LagQuest.Ended)
                {
                    _quests.Remove(existingPlayerId);
                    OnPlayerQuestUpdated(existingPlayerId, LagQuest.Cleared);
                    OnPlayerLagCleared(existingPlayerId);
                    continue;
                }

                // end quests of players that aren't laggy anymore
                if (!latestLaggyPlayerIds.Contains(existingPlayerId))
                {
                    state.Quest = LagQuest.Ended;
                    OnPlayerQuestUpdated(existingPlayerId, state.Quest);
                    Log.Info($"warning withdrawn: {state.Latest.PlayerName}");
                }
            }

            foreach (var (_, state) in _quests)
            {
                Log.Debug($"warning ongoing: {state.Latest} {state.Quest}");
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void OnSelfProfiled(long playerId)
        {
            if (_quests.TryGetValue(playerId, out var state))
            {
                var lagNormal = state.Latest.LongLagNormal;
                if (state.Quest <= LagQuest.MustProfileSelf)
                {
                    var warningLagNormal = lagNormal / _config.WarningLagNormal;
                    state.LastWarningLagNormal = warningLagNormal;
                    state.Quest = warningLagNormal >= 1
                        ? LagQuest.MustDelagSelf
                        : LagQuest.MustWaitUnpinned;

                    OnPlayerQuestUpdated(playerId, state.Quest);
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IReadOnlyDictionary<long, LagPlayerState> GetInternalSnapshot()
        {
            return _quests
                .Select(q => (q.Key, q.Value.Snapshot()))
                .ToDictionary();
        }

        void OnPlayerLagCleared(long playerId)
        {
            foreach (var listener in _listeners)
            {
                if (listener is ILagListener lagListener)
                {
                    lagListener.OnLagCleared(playerId);
                }
            }
        }

        void OnPlayerLagUpdated(LagWarningSource player)
        {
            foreach (var listener in _listeners)
            {
                if (listener is ILagListener lagListener)
                {
                    lagListener.OnLagUpdated(player);
                }
            }
        }

        void OnPlayerQuestUpdated(long playerId, LagQuest quest)
        {
            foreach (var listener in _listeners)
            {
                if (listener is IQuestListener stateListener)
                {
                    stateListener.OnQuestUpdated(playerId, quest);
                }
            }
        }
    }
}