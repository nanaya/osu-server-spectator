// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs
{
    public class MultiplayerQueue
    {
        public const int PER_USER_LIMIT = 3;

        public MultiplayerPlaylistItem CurrentItem => room.Playlist[currentIndex];

        private readonly ServerMultiplayerRoom room;
        private readonly IMultiplayerServerMatchCallbacks hub;

        private IDatabaseFactory? dbFactory;
        private int currentIndex;

        public MultiplayerQueue(ServerMultiplayerRoom room, IMultiplayerServerMatchCallbacks hub)
        {
            this.room = room;
            this.hub = hub;
        }

        /// <summary>
        /// Initialises the queue from the database.
        /// </summary>
        public async Task Initialise(IDatabaseFactory dbFactory)
        {
            this.dbFactory = dbFactory;

            using (var db = dbFactory.GetInstance())
            {
                foreach (var item in await db.GetAllPlaylistItemsAsync(room.RoomID))
                    room.Playlist.Add(await item.ToMultiplayerPlaylistItem(db));
            }

            await updateCurrentItem();
        }

        /// <summary>
        /// Updates the queue as a result of a change in the queueing mode.
        /// </summary>
        public async Task UpdateFromQueueModeChange()
        {
            if (dbFactory == null) throw new InvalidOperationException($"Call {nameof(Initialise)} first.");

            // When changing to host-only mode, ensure that at least one non-expired playlist item exists by duplicating the current item.
            if (room.Settings.QueueMode == QueueMode.HostOnly && room.Playlist.All(item => item.Expired))
            {
                using (var db = dbFactory.GetInstance())
                    await duplicateCurrentItem(db);
            }

            // When changing modes, items could have been added (above) or the queueing order could have changed.
            await updateCurrentItem();
        }

        /// <summary>
        /// Expires the current playlist item and advances to the next one in the order defined by the queueing mode.
        /// </summary>
        public async Task FinishCurrentItem()
        {
            if (dbFactory == null) throw new InvalidOperationException($"Call {nameof(Initialise)} first.");

            using (var db = dbFactory.GetInstance())
            {
                // Expire and let clients know that the current item has finished.
                await db.ExpirePlaylistItemAsync(CurrentItem.ID);
                CurrentItem.Expired = true;

                await hub.OnPlaylistItemChanged(room, CurrentItem);

                // In host-only mode, duplicate the playlist item for the next round if no other non-expired items exist.
                if (room.Settings.QueueMode == QueueMode.HostOnly)
                {
                    if (room.Playlist.All(item => item.Expired))
                        await duplicateCurrentItem(db);
                }
            }

            await updateCurrentItem();
        }

        /// <summary>
        /// Add a playlist item to the room's queue.
        /// </summary>
        /// <param name="item">The item to add.</param>
        /// <param name="user">The user adding the item.</param>
        /// <exception cref="NotHostException">If the adding user is not the host in host-only mode.</exception>
        /// <exception cref="InvalidStateException">If the given playlist item is not valid.</exception>
        public async Task AddItem(MultiplayerPlaylistItem item, MultiplayerRoomUser user)
        {
            if (dbFactory == null) throw new InvalidOperationException($"Call {nameof(Initialise)} first.");

            if (room.Settings.QueueMode == QueueMode.HostOnly && !user.Equals(room.Host))
                throw new NotHostException();

            if (room.Settings.QueueMode != QueueMode.HostOnly && room.Playlist.Count(i => i.OwnerID == user.UserID && !i.Expired) >= PER_USER_LIMIT)
                throw new InvalidStateException($"Can't enqueue more than {PER_USER_LIMIT} items at once.");

            using (var db = dbFactory.GetInstance())
            {
                string? beatmapChecksum = await db.GetBeatmapChecksumAsync(item.BeatmapID);

                if (beatmapChecksum == null)
                    throw new InvalidStateException("Attempted to add a beatmap which does not exist online.");

                if (item.BeatmapChecksum != beatmapChecksum)
                    throw new InvalidStateException("Attempted to add a beatmap which has been modified.");

                if (item.RulesetID < 0 || item.RulesetID > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                    throw new InvalidStateException("Attempted to select an unsupported ruleset.");

                item.EnsureModsValid();

                switch (room.Settings.QueueMode)
                {
                    case QueueMode.HostOnly:
                        // In host-only mode, the current item is re-used.
                        item.ID = CurrentItem.ID;
                        item.OwnerID = CurrentItem.OwnerID;

                        await db.UpdatePlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, item));
                        room.Playlist[currentIndex] = item;

                        await hub.OnPlaylistItemChanged(room, item);
                        break;

                    default:
                        item.OwnerID = user.UserID;
                        item.ID = await db.AddPlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, item));
                        room.Playlist.Add(item);

                        await hub.OnPlaylistItemAdded(room, item);

                        // The current item can change as a result of an item being added. For example, if all items earlier in the queue were expired.
                        await updateCurrentItem();
                        break;
                }
            }
        }

        /// <summary>
        /// Duplicates <see cref="CurrentItem"/> into the database.
        /// </summary>
        /// <param name="db">The database connection.</param>
        private async Task duplicateCurrentItem(IDatabaseAccess db)
        {
            var newItem = new MultiplayerPlaylistItem
            {
                OwnerID = CurrentItem.OwnerID,
                BeatmapID = CurrentItem.BeatmapID,
                BeatmapChecksum = CurrentItem.BeatmapChecksum,
                RulesetID = CurrentItem.RulesetID,
                AllowedMods = CurrentItem.AllowedMods,
                RequiredMods = CurrentItem.RequiredMods
            };

            newItem.ID = await db.AddPlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, newItem));
            room.Playlist.Add(newItem);

            await hub.OnPlaylistItemAdded(room, newItem);
        }

        /// <summary>
        /// Updates <see cref="CurrentItem"/> and the playlist item ID stored in the room's settings.
        /// </summary>
        private async Task updateCurrentItem()
        {
            MultiplayerPlaylistItem newItem;

            switch (room.Settings.QueueMode)
            {
                default:
                    // Pick the first available non-expired playlist item, or default to the last item for when all items are expired.
                    newItem = room.Playlist.FirstOrDefault(i => !i.Expired) ?? room.Playlist.Last();
                    break;

                case QueueMode.AllPlayersRoundRobin:
                    newItem =
                        room.Playlist
                            // Group items by their owner.
                            .GroupBy(i => i.OwnerID)
                            // Order by descending number of expired (already played) items for each owner.
                            .OrderBy(g => g.Count(i => i.Expired))
                            // Get the first unexpired item from each owner.
                            .Select(g => g.FirstOrDefault(i => !i.Expired))
                            // Select the first unexpired item in order.
                            .FirstOrDefault(i => i != null)
                        // Default to the last item for when all items are expired.
                        ?? room.Playlist.Last();
                    break;
            }

            currentIndex = room.Playlist.IndexOf(newItem);

            long lastItemID = room.Settings.PlaylistItemId;
            room.Settings.PlaylistItemId = newItem.ID;

            if (newItem.ID != lastItemID)
                await hub.OnMatchSettingsChanged(room);
        }
    }
}
