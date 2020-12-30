// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.RealtimeMultiplayer;
using osu.Server.Spectator.DatabaseModels;

namespace osu.Server.Spectator.Hubs
{
    public class MultiplayerHub : StatefulUserHub<IMultiplayerClient, MultiplayerClientState>, IMultiplayerServer
    {
        protected static readonly EntityStore<MultiplayerRoom> ACTIVE_ROOMS = new EntityStore<MultiplayerRoom>();

        public MultiplayerHub(IDistributedCache cache)
            : base(cache)
        {
        }

        /// <summary>
        /// Temporary method to reset in-memory storage (used only for tests).
        /// </summary>
        public new static void Reset()
        {
            StatefulUserHub<IMultiplayerClient, MultiplayerClientState>.Reset();

            Console.WriteLine("Resetting ALL tracked rooms.");
            ACTIVE_ROOMS.Clear();
        }

        public async Task<MultiplayerRoom> JoinRoom(long roomId)
        {
            bool isRestricted = await CheckIsUserRestricted();

            if (isRestricted)
            {
                throw new InvalidStateException("Can't join a room when restricted.");
            }

            using (var userUsage = await GetOrCreateLocalUserState())
            {
                if (userUsage.Item != null)
                {
                    // if the user already has a state, it means they are already in a room and can't join another without first leaving.
                    throw new InvalidStateException("Can't join a room when already in another room.");
                }

                // add the user to the room.
                var roomUser = new MultiplayerRoomUser(CurrentContextUserId);

                MultiplayerRoom room;

                using (var roomUsage = await ACTIVE_ROOMS.GetForUse(roomId, true))
                {
                    if (roomUsage.Item == null)
                    {
                        var retrievedRoom = await RetrieveRoom(roomId);
                        retrievedRoom.Host = roomUser;
                        roomUsage.Item = retrievedRoom;
                    }

                    room = roomUsage.Item;

                    room.Users.Add(roomUser);
                }

                await Clients.Group(GetGroupId(roomId)).UserJoined(roomUser);
                await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(roomId));

                userUsage.Item = new MultiplayerClientState(Context.ConnectionId, CurrentContextUserId, roomId);

                // read only database operations are allowed to be done outside of the lock for performance reasons.
                await UpdateDatabaseParticipants(room);
                await MarkRoomActive(room);

                return room;
            }
        }

        /// <summary>
        /// Attempt to construct and a room based on a room ID specification.
        /// This will check the database backing to ensure things are in a consistent state.
        /// </summary>
        /// <param name="roomId">The proposed room ID.</param>
        /// <exception cref="InvalidStateException">If anything is wrong with this request.</exception>
        protected virtual async Task<MultiplayerRoom> RetrieveRoom(long roomId)
        {
            using (var conn = Database.GetConnection())
            {
                var databaseRoom = await conn.QueryFirstOrDefaultAsync<multiplayer_room>("SELECT * FROM multiplayer_rooms WHERE category = 'realtime' AND id = @RoomID", new
                {
                    RoomID = roomId
                });

                if (databaseRoom == null)
                    throw new InvalidStateException("Specified match does not exist.");

                if (databaseRoom.ends_at != null && databaseRoom.ends_at < DateTimeOffset.Now)
                    throw new InvalidStateException("Match has already ended.");

                if (databaseRoom.user_id != CurrentContextUserId)
                    throw new InvalidStateException("Non-host is attempting to join match before host");

                var playlistItem = await conn.QuerySingleAsync<multiplayer_playlist_item>("SELECT * FROM multiplayer_playlist_items WHERE room_id = @RoomID", new
                {
                    RoomID = roomId
                });

                var beatmapChecksum = await conn.QuerySingleAsync<string>("SELECT checksum from osu_beatmaps where beatmap_id = @BeatmapID", new
                {
                    BeatmapId = playlistItem.beatmap_id
                });

                return new MultiplayerRoom(roomId)
                {
                    Settings = new MultiplayerRoomSettings
                    {
                        BeatmapChecksum = beatmapChecksum,
                        BeatmapID = playlistItem.beatmap_id,
                        RulesetID = playlistItem.ruleset_id,
                        Name = databaseRoom.name,
                        Mods = playlistItem.required_mods != null ? JsonConvert.DeserializeObject<IEnumerable<APIMod>>(playlistItem.required_mods) : Array.Empty<APIMod>()
                    }
                };
            }
        }

        /// <summary>
        /// Marks a room active at the database, implying the host has joined and this server is now in control of the room's lifetime.
        /// </summary>
        protected virtual async Task MarkRoomActive(MultiplayerRoom room)
        {
            using (var conn = Database.GetConnection())
            {
                await conn.ExecuteAsync("UPDATE multiplayer_rooms SET ends_at = null WHERE id = @RoomID", new
                {
                    RoomID = room.RoomID
                });
            }
        }

        public async Task LeaveRoom()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                if (userUsage.Item == null)
                    throw new NotJoinedRoomException();

                await leaveRoom(userUsage.Item);

                userUsage.Destroy();
            }
        }

        public async Task TransferHost(int userId)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                ensureIsHost(room);

                var newHost = room.Users.Find(u => u.UserID == userId);

                if (newHost == null)
                    throw new Exception("Target user is not in the current room");

                await setNewHost(room, newHost);
            }
        }

        public async Task ChangeState(MultiplayerUserState newState)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.Find(u => u.UserID == CurrentContextUserId);

                if (user == null)
                    failWithInvalidState("Local user was not found in the expected room");

                if (user.State == newState)
                    return;

                ensureValidStateSwitch(room, user.State, newState);
                user.State = newState;

                // handle whether this user should be receiving gameplay messages or not.
                switch (newState)
                {
                    case MultiplayerUserState.FinishedPlay:
                    case MultiplayerUserState.Idle:
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupId(room.RoomID, true));
                        break;

                    case MultiplayerUserState.Ready:
                        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(room.RoomID, true));
                        break;
                }

                await Clients.Group(GetGroupId(room.RoomID)).UserStateChanged(CurrentContextUserId, newState);

                await updateRoomStateIfRequired(room);
            }
        }

        public async Task StartMatch()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                ensureIsHost(room);

                if (room.State != MultiplayerRoomState.Open)
                    throw new InvalidStateException("Can't start match when already in a running state.");

                var readyUsers = room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray();

                if (readyUsers.Length == 0)
                    throw new InvalidStateException("Can't start match when no users are ready.");

                if (room.Host != null && room.Host.State != MultiplayerUserState.Ready)
                    throw new InvalidStateException("Can't start match when the host is not ready.");

                await ClearDatabaseScores(room);

                foreach (var u in readyUsers)
                    await changeAndBroadcastUserState(room, u, MultiplayerUserState.WaitingForLoad);

                await changeRoomState(room, MultiplayerRoomState.WaitingForLoad);

                await Clients.Group(GetGroupId(room.RoomID, true)).LoadRequested();
            }
        }

        public async Task ChangeSettings(MultiplayerRoomSettings settings)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                if (room.State != MultiplayerRoomState.Open)
                    throw new InvalidStateException("Attempted to change settings while game is active");

                ensureIsHost(room);

                if (room.Settings.Equals(settings))
                    return;

                var previousSettings = room.Settings;

                try
                {
                    room.Settings = settings;
                    await UpdateDatabaseSettings(room);
                }
                catch
                {
                    // rollback settings if an error occurred when updating the database.
                    room.Settings = previousSettings;
                    throw;
                }

                // this should probably only happen for gameplay-related changes, but let's just keep things simple for now.
                foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray())
                    await changeAndBroadcastUserState(room, u, MultiplayerUserState.Idle);

                await Clients.Group(GetGroupId(room.RoomID)).SettingsChanged(settings);
            }
        }

        /// <summary>
        /// Get the group ID to be used for multiplayer messaging.
        /// </summary>
        /// <param name="roomId">The databased room ID.</param>
        /// <param name="gameplay">Whether the group ID should be for active gameplay, or room control messages.</param>
        public static string GetGroupId(long roomId, bool gameplay = false) => $"room:{roomId}:{gameplay}";

        protected virtual async Task ClearDatabaseScores(MultiplayerRoom room)
        {
            // for now, clear all existing scores out of the playlist item to ensure no duplicates.
            // eventually we will want to increment to a new playlist item rather than reusing the same one.
            using (var conn = Database.GetConnection())
            {
                long playlistItemId = await conn.QuerySingleAsync<long>("SELECT id FROM multiplayer_playlist_items WHERE room_id = @RoomID", new
                {
                    RoomID = room.RoomID,
                });

                await conn.ExecuteAsync("DELETE FROM multiplayer_scores WHERE playlist_item_id = @PlaylistItemID", new { PlaylistItemID = playlistItemId });
                await conn.ExecuteAsync("DELETE FROM multiplayer_scores_high WHERE playlist_item_id = @PlaylistItemID", new { PlaylistItemID = playlistItemId });
            }
        }

        protected virtual async Task UpdateDatabaseSettings(MultiplayerRoom room)
        {
            using (var conn = Database.GetConnection())
            {
                var dbPlaylistItem = new multiplayer_playlist_item(room);

                var beatmapChecksum = await conn.QuerySingleOrDefaultAsync<string>("SELECT checksum from osu_beatmaps where beatmap_id = @BeatmapID", new
                {
                    BeatmapId = dbPlaylistItem.beatmap_id
                });

                if (beatmapChecksum == null)
                    throw new InvalidStateException("Attempted to select a beatmap which does not exist online.");

                if (room.Settings.BeatmapChecksum != beatmapChecksum)
                    throw new InvalidStateException("Attempted to select a beatmap which has been modified.");

                await conn.ExecuteAsync("UPDATE multiplayer_rooms SET name = @Name WHERE id = @RoomID", new
                {
                    RoomID = room.RoomID,
                    Name = room.Settings.Name
                });

                await conn.ExecuteAsync("UPDATE multiplayer_playlist_items SET beatmap_id = @beatmap_id, ruleset_id = @ruleset_id, required_mods = @required_mods, updated_at = NOW() WHERE room_id = @room_id", dbPlaylistItem);
            }
        }

        protected virtual async Task UpdateDatabaseHost(MultiplayerRoom room)
        {
            Debug.Assert(room.Host != null);

            using (var conn = Database.GetConnection())
            {
                await conn.ExecuteAsync("UPDATE multiplayer_rooms SET user_id = @HostUserID WHERE id = @RoomID", new
                {
                    HostUserID = room.Host.UserID,
                    RoomID = room.RoomID
                });
            }
        }

        protected virtual async Task EndDatabaseMatch(MultiplayerRoom room)
        {
            using (var conn = Database.GetConnection())
            {
                await conn.ExecuteAsync("UPDATE multiplayer_rooms SET ends_at = NOW() WHERE id = @RoomID", new
                {
                    RoomID = room.RoomID
                });
            }
        }

        protected virtual async Task<bool> CheckIsUserRestricted()
        {
            using (var conn = Database.GetConnection())
            {
                return await conn.QueryFirstOrDefaultAsync<byte>("SELECT user_warnings FROM phpbb_users WHERE user_id = @UserID", new
                {
                    UserID = CurrentContextUserId
                }) != 0;
            }
        }

        protected virtual async Task UpdateDatabaseParticipants(MultiplayerRoom room)
        {
            using (var conn = Database.GetConnection())
            {
                using (var transaction = await conn.BeginTransactionAsync())
                {
                    // This should be considered *very* temporary, and for display purposes only!
                    await conn.ExecuteAsync("DELETE FROM multiplayer_rooms_high WHERE room_id = @RoomID", new
                    {
                        RoomID = room.RoomID
                    }, transaction);

                    foreach (var u in room.Users)
                    {
                        await conn.ExecuteAsync("INSERT INTO multiplayer_rooms_high (room_id, user_id) VALUES (@RoomID, @UserID)", new
                        {
                            RoomID = room.RoomID,
                            UserID = u.UserID
                        }, transaction);
                    }

                    await transaction.CommitAsync();
                }

                await conn.ExecuteAsync("UPDATE multiplayer_rooms SET participant_count = @Count WHERE id = @RoomID", new
                {
                    RoomID = room.RoomID,
                    Count = room.Users.Count
                });
            }
        }

        protected override async Task CleanUpState(MultiplayerClientState state)
        {
            await leaveRoom(state);
            await base.CleanUpState(state);
        }

        private async Task setNewHost(MultiplayerRoom room, MultiplayerRoomUser newHost)
        {
            room.Host = newHost;

            await UpdateDatabaseHost(room);

            await Clients.Group(GetGroupId(room.RoomID)).HostChanged(newHost.UserID);
        }

        /// <summary>
        /// Should be called when user states change, to check whether the new overall room state can trigger a room-level state change.
        /// </summary>
        private async Task updateRoomStateIfRequired(MultiplayerRoom room)
        {
            //check whether a room state change is required.
            switch (room.State)
            {
                case MultiplayerRoomState.WaitingForLoad:
                    if (room.Users.All(u => u.State != MultiplayerUserState.WaitingForLoad))
                    {
                        var loadedUsers = room.Users.Where(u => u.State == MultiplayerUserState.Loaded).ToArray();

                        if (loadedUsers.Length == 0)
                        {
                            // all users have bailed from the load sequence. cancel the game start.
                            await changeRoomState(room, MultiplayerRoomState.Open);
                            return;
                        }

                        foreach (var u in loadedUsers)
                            await changeAndBroadcastUserState(room, u, MultiplayerUserState.Playing);

                        await Clients.Group(GetGroupId(room.RoomID)).MatchStarted();

                        await changeRoomState(room, MultiplayerRoomState.Playing);
                    }

                    break;

                case MultiplayerRoomState.Playing:
                    if (room.Users.All(u => u.State != MultiplayerUserState.Playing))
                    {
                        foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.FinishedPlay))
                            await changeAndBroadcastUserState(room, u, MultiplayerUserState.Results);

                        await changeRoomState(room, MultiplayerRoomState.Open);
                        await Clients.Group(GetGroupId(room.RoomID)).ResultsReady();
                    }

                    break;
            }
        }

        private Task changeAndBroadcastUserState(MultiplayerRoom room, MultiplayerRoomUser user, MultiplayerUserState state)
        {
            user.State = state;
            return Clients.Group(GetGroupId(room.RoomID)).UserStateChanged(user.UserID, user.State);
        }

        /// <summary>
        /// Changes the provided room's state and notifies all users.
        /// </summary>
        private async Task changeRoomState(MultiplayerRoom room, MultiplayerRoomState newState)
        {
            room.State = newState;
            await Clients.Group(GetGroupId(room.RoomID)).RoomStateChanged(newState);
        }

        /// <summary>
        /// Given a room and a state transition, throw if there's an issue with the sequence of events.
        /// </summary>
        /// <param name="room">The room.</param>
        /// <param name="oldState">The old state.</param>
        /// <param name="newState">The new state.</param>
        private void ensureValidStateSwitch(MultiplayerRoom room, MultiplayerUserState oldState, MultiplayerUserState newState)
        {
            switch (newState)
            {
                case MultiplayerUserState.Idle:
                    // any state can return to idle.
                    break;

                case MultiplayerUserState.Ready:
                    if (oldState != MultiplayerUserState.Idle)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.WaitingForLoad:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.Loaded:
                    if (oldState != MultiplayerUserState.WaitingForLoad)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Playing:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.FinishedPlay:
                    if (oldState != MultiplayerUserState.Playing)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Results:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                default:
                    throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
            }
        }

        /// <summary>
        /// Ensure the local user is the host of the room, and throw if they are not.
        /// </summary>
        private void ensureIsHost(MultiplayerRoom room)
        {
            if (room.Host?.UserID != CurrentContextUserId)
                throw new NotHostException();
        }

        /// <summary>
        /// Retrieve the <see cref="MultiplayerRoom"/> for the local context user.
        /// </summary>
        private async Task<ItemUsage<MultiplayerRoom>> getLocalUserRoom(MultiplayerClientState? state)
        {
            if (state == null)
                throw new NotJoinedRoomException();

            long roomId = state.CurrentRoomID;

            return await ACTIVE_ROOMS.GetForUse(roomId);
        }

        private async Task leaveRoom(MultiplayerClientState state)
        {
            using (var roomUsage = await getLocalUserRoom(state))
            {
                var room = roomUsage.Item;

                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                await Groups.RemoveFromGroupAsync(state.ConnectionId, GetGroupId(room.RoomID, true));
                await Groups.RemoveFromGroupAsync(state.ConnectionId, GetGroupId(room.RoomID));

                var user = room.Users.Find(u => u.UserID == state.UserId);

                if (user == null)
                    failWithInvalidState("User was not in the expected room.");

                room.Users.Remove(user);

                await UpdateDatabaseParticipants(room);

                if (room.Users.Count == 0)
                {
                    Console.WriteLine($"Stopping tracking of room {room.RoomID} (all users left).");
                    roomUsage.Destroy();

                    await EndDatabaseMatch(room);

                    return;
                }

                var clients = Clients.Group(GetGroupId(room.RoomID));

                // if this user was the host, we need to arbitrarily transfer host so the room can continue to exist.
                if (room.Host?.Equals(user) == true)
                {
                    // there *has* to still be at least one user in the room (see user check above).
                    var newHost = room.Users.First();

                    await setNewHost(room, newHost);
                }

                await clients.UserLeft(user);
            }
        }

        [ExcludeFromCodeCoverage]
        [DoesNotReturn]
        private void failWithInvalidState(string message) => throw new InvalidStateException(message);
    }
}
