using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using MopsBot.Data.Tracker;
using MongoDB.Driver;

namespace MopsBot.Data
{
    public abstract class TrackerWrapper : MopsBot.Api.IAPIHandler
    {
        public abstract Task UpdateDBAsync(BaseTracker tracker);
        public abstract Task RemoveFromDBAsync(BaseTracker tracker);
        public abstract Task InsertToDBAsync(BaseTracker tracker);
        public abstract Task MergeCapitalisation();
        public abstract Task<bool> TryRemoveTrackerAsync(string name, ulong channelID);
        public abstract Task<bool> TrySetNotificationAsync(string name, ulong channelID, string notificationMessage);
        public abstract Task AddTrackerAsync(string name, ulong channelID, string notification = "");
        public abstract HashSet<Tracker.BaseTracker> GetTrackerSet();
        public abstract Dictionary<string, Tracker.BaseTracker> GetTrackers();
        public abstract IEnumerable<BaseTracker> GetTrackers(ulong channelID);
        public abstract IEnumerable<BaseTracker> GetGuildTrackers(ulong guildId);
        public abstract IEnumerable<Embed> GetTrackersEmbed(ulong channelID);
        public abstract BaseTracker GetTracker(ulong channelID, string name);
        public abstract Type GetTrackerType();
        public abstract void PostInitialisation();
        public abstract Task AddContent(Dictionary<string, string> args);
        public abstract Task UpdateContent(Dictionary<string, Dictionary<string, string>> args);
        public abstract Task RemoveContent(Dictionary<string, string> args);
        public abstract Dictionary<string, object> GetContent(ulong userId, ulong guildId);
    }

    /// <summary>
    /// A class containing all Trackers
    /// </summary>
    public class TrackerHandler<T> : TrackerWrapper where T : Tracker.BaseTracker
    {
        public Dictionary<string, T> trackers;
        public TrackerHandler()
        {
        }

        public override void PostInitialisation()
        {
            var collection = StaticBase.Database.GetCollection<T>(typeof(T).Name).FindSync<T>(x => true).ToList();
            trackers = collection.ToDictionary(x => x.Name);

            trackers = (trackers == null ? new Dictionary<string, T>() : trackers);

            if (collection.Count > 0)
            {
                int gap = 1200000 / collection.Count;

                for (int i = trackers.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var cur = trackers[trackers.Keys.ElementAt(i)];
                        cur.SetTimer(1200000, gap * (i + 1));
                        cur.PostInitialisation(trackers.Count - i);
                        cur.OnMinorEventFired += OnMinorEvent;
                        cur.OnMajorEventFired += OnMajorEvent;
                    }
                    catch (Exception e)
                    {
                        Program.MopsLog(new LogMessage(LogSeverity.Error, "", $"Error on PostInitialisation, {e.Message}", e));
                    }
                }

                //Start Twitter STREAM after all are initialised
                if(typeof(T) == typeof(TwitterTracker)){
                    TwitterTracker.STREAM.StreamStopped += (sender, args) => {Program.MopsLog(new LogMessage(LogSeverity.Info, "", $"TwitterSTREAM stopped. {args.DisconnectMessage?.Reason ?? ""}", args.Exception)); TwitterTracker.RestartStream();};
                    TwitterTracker.STREAM.StreamStarted += (sender, args) => Program.MopsLog(new LogMessage(LogSeverity.Info, "", "TwitterSTREAM started."));
                    TwitterTracker.STREAM.WarningFallingBehindDetected += (sender, args) => Program.MopsLog(new LogMessage(LogSeverity.Warning, "", $"TwitterSTREAM falling behind, {args.WarningMessage.Message} ({args.WarningMessage.PercentFull}%)"));
                    TwitterTracker.STREAM.FilterLevel = Tweetinvi.Streaming.Parameters.StreamFilterLevel.Low;
                    TwitterTracker.STREAM.StartStreamMatchingAllConditionsAsync();
                }
            }
        }

        public override async Task UpdateDBAsync(BaseTracker tracker)
        {
            await StaticBase.Database.GetCollection<BaseTracker>(typeof(T).Name).ReplaceOneAsync(x => x.Name.Equals(tracker.Name), tracker);
        }

        public override async Task InsertToDBAsync(BaseTracker tracker)
        {
            await StaticBase.Database.GetCollection<BaseTracker>(typeof(T).Name).InsertOneAsync(tracker);
        }

        public override async Task RemoveFromDBAsync(BaseTracker tracker)
        {
            await StaticBase.Database.GetCollection<T>(typeof(T).Name).DeleteOneAsync(x => x.Name.Equals(tracker.Name));
        }

        public override async Task MergeCapitalisation()
        {
            if (typeof(T) == typeof(TwitterTracker) ||
               typeof(T) == typeof(TwitchTracker) ||
               typeof(T) == typeof(TwitchClipTracker) ||
               typeof(T) == typeof(OsuTracker) ||
               typeof(T) == typeof(OSRSTracker))
            {
                foreach (var tracker in trackers.ToList())
                {
                    if (!trackers.ContainsKey(tracker.Key.ToLower().Replace("@", "")))
                    {
                        await RemoveFromDBAsync(tracker.Value);
                        tracker.Value.Name = tracker.Key.ToLower().Replace("@", "");
                        trackers.Add(tracker.Key.ToLower().Replace("@", ""), tracker.Value);
                        trackers.Remove(tracker.Key);
                        await InsertToDBAsync(trackers[tracker.Key.ToLower().Replace("@", "")]);
                    }
                    else if (!tracker.Key.Equals(tracker.Key.ToLower().Replace("@", "")))
                    {
                        foreach (var channel in tracker.Value.ChannelMessages)
                        {
                            await AddTrackerAsync(tracker.Key.ToLower().Replace("@", ""), channel.Key, channel.Value);

                            if (typeof(T) == typeof(TwitchTracker))
                            {
                                var curTracker = trackers[tracker.Key.ToLower().Replace("@", "")] as TwitchTracker;
                                curTracker.Specifications[channel.Key] = (tracker.Value as TwitchTracker).Specifications[channel.Key];
                            }
                        }

                        tracker.Value.Dispose();
                        trackers.Remove(tracker.Key);
                        await RemoveFromDBAsync(tracker.Value);
                        await UpdateDBAsync(trackers[tracker.Key.ToLower().Replace("@", "")]);
                    }
                }
            }
        }

        public override async Task<bool> TryRemoveTrackerAsync(string name, ulong channelId)
        {
            if (trackers.ContainsKey(name) && trackers[name].ChannelMessages.ContainsKey(channelId))
            {
                if (typeof(T) == typeof(BaseUpdatingTracker))
                    foreach (var channel in (trackers[name] as BaseUpdatingTracker).ToUpdate.Where(x => x.Key.Equals(channelId)))
                        try
                        {
                            Program.ReactionHandler.ClearHandler((IUserMessage)((ITextChannel)Program.Client.GetChannel(channelId)).GetMessageAsync(channel.Value).Result).Wait();
                        }
                        catch
                        {
                        }

                if (trackers[name].ChannelMessages.Keys.Count > 1)
                {
                    trackers[name].ChannelMessages.Remove(channelId);

                    if (trackers.First().Value.GetType() == typeof(Tracker.TwitchTracker))
                    {
                        (trackers[name] as Tracker.TwitchTracker).ToUpdate.Remove(channelId);
                    }

                    else if (trackers.First().Value.GetType() == typeof(Tracker.YoutubeLiveTracker))
                    {
                        (trackers[name] as Tracker.YoutubeLiveTracker).ToUpdate.Remove(channelId);
                    }

                    await UpdateDBAsync(trackers[name]);
                    await Program.MopsLog(new LogMessage(LogSeverity.Info, "", $"Removed a {typeof(T).FullName} for {name}\nChannel: {channelId}"));
                }

                else
                {
                    await RemoveFromDBAsync(trackers[name]);
                    trackers[name].Dispose();
                    trackers.Remove(name);
                    //SaveJson();
                    await Program.MopsLog(new LogMessage(LogSeverity.Info, "", $"Removed a {typeof(T).FullName} for {name}\nChannel: {channelId}; Last channel left."));
                }

                return true;
            }
            return false;
        }

        public override async Task AddTrackerAsync(string name, ulong channelID, string notification = "")
        {
            if (trackers.ContainsKey(name))
            {
                if (!trackers[name].ChannelMessages.ContainsKey(channelID))
                {
                    trackers[name].ChannelMessages.Add(channelID, notification);
                    await UpdateDBAsync(trackers[name]);
                    trackers[name].PostChannelAdded(channelID);
                }
            }
            else
            {
                var tracker = (T)Activator.CreateInstance(typeof(T), new object[] { name });
                name = tracker.Name;
                trackers.Add(name, tracker);
                trackers[name].ChannelMessages.Add(channelID, notification);
                trackers[name].OnMajorEventFired += OnMajorEvent;
                trackers[name].OnMinorEventFired += OnMinorEvent;
                await InsertToDBAsync(trackers[name]);
                tracker.PostInitialisation();
            }

            await Program.MopsLog(new LogMessage(LogSeverity.Info, "", $"Started a new {typeof(T).Name} for {name}\nChannels: {string.Join(",", trackers[name].ChannelMessages.Keys)}\nMessage: {notification}"));
        }

        public override async Task<bool> TrySetNotificationAsync(string name, ulong channelID, string notificationMessage)
        {
            var tracker = GetTracker(channelID, name);

            if (tracker != null)
            {
                tracker.ChannelMessages[channelID] = notificationMessage;
                await UpdateDBAsync(tracker);
                return true;
            }

            return false;
        }

        public override IEnumerable<BaseTracker> GetTrackers(ulong channelID)
        {
            return trackers.Select(x => x.Value).Where(x => x.ChannelMessages.ContainsKey(channelID));
        }

        public override IEnumerable<BaseTracker> GetGuildTrackers(ulong guildId)
        {
            var channels = Program.Client.GetGuild(guildId).TextChannels;
            var allTrackers = trackers.Select(x => x.Value as TwitchTracker).ToList();
            var guildTrackers = allTrackers.Where(x => x.ChannelMessages.Keys.Any(y => channels.Select(z => z.Id).Contains(y))).ToList();
            return guildTrackers;
        }

        public async Task<bool> TryModifyTrackerAsync(string name, ulong channelId, Action<T> modifier)
        {
            var tracker = GetTracker(channelId, name) as T;
            if (tracker != null)
            {
                modifier(tracker);
                await UpdateDBAsync(tracker);
                return true;
            }
            else
                return false;
        }

        public override IEnumerable<Embed> GetTrackersEmbed(ulong channelID)
        {
            var trackerStrings = trackers.Where(x => x.Value.ChannelMessages.ContainsKey(channelID)).Select(x => x.Value.TrackerUrl() != null ? $"[``{x.Key}``]({x.Value.TrackerUrl()})\n" : $"``{x.Key}``\n");
            var embeds = new List<EmbedBuilder>(){new EmbedBuilder().WithTitle(typeof(T).Name).WithCurrentTimestamp().WithColor(Discord.Color.Blue)};
            
            foreach(var tracker in trackerStrings){
                if((embeds.Last().Description?.Length ?? 0) + tracker.Length > 2048){
                    embeds.Add(new EmbedBuilder());
                    embeds.Last().WithTitle(typeof(T).Name).WithCurrentTimestamp().WithColor(Discord.Color.Blue);
                }
                embeds.Last().Description += tracker;
            }

            return embeds.Select(x => x.Build());
        }

        public override Dictionary<string, BaseTracker> GetTrackers()
        {
            return trackers.Select(x => new KeyValuePair<string, BaseTracker>(x.Key, (BaseTracker)x.Value)).ToDictionary(x => x.Key, x => x.Value);
        }

        public override BaseTracker GetTracker(ulong channelID, string name)
        {
            return trackers.FirstOrDefault(x => x.Key.Equals(name) && x.Value.ChannelMessages.ContainsKey(channelID)).Value;
        }

        public override HashSet<BaseTracker> GetTrackerSet()
        {
            return trackers.Values.Select(x => (BaseTracker)x).ToHashSet();
        }

        public override Type GetTrackerType()
        {
            return typeof(T);
        }

        //IAPIHandler implementation
        public async override Task AddContent(Dictionary<string, string> args)
        {
            T tmp = (T)Activator.CreateInstance(typeof(T), new object[] { args });
            trackers[tmp.Name] = tmp;
            tmp.OnMajorEventFired += OnMajorEvent;
            tmp.OnMinorEventFired += OnMinorEvent;
            await InsertToDBAsync(tmp);
        }

        public async override Task UpdateContent(Dictionary<string, Dictionary<string, string>> args)
        {
            trackers[args["OldValue"]["Id"]].Update(args);
            await UpdateDBAsync(trackers[args["OldValue"]["Id"]]);
        }

        public async override Task RemoveContent(Dictionary<string, string> args)
        {
            await TryRemoveTrackerAsync(args["Id"], ulong.Parse(args["Channel"].Split(":")[1]));
        }

        public override Dictionary<string, object> GetContent(ulong userId, ulong guildId)
        {
            var tmp = ((T)Activator.CreateInstance(typeof(T)));
            var parameters = tmp.GetParameters(guildId);
            tmp.Dispose();

            List<ulong> channels = ((string[])((Dictionary<string, object>)parameters["Parameters"])["Channel"]).Select(x => ulong.Parse((x.Split(":")[1]))).ToList();
            var rawTrackers = trackers.Values.Where(x => x.ChannelMessages.Any(y => channels.Contains(y.Key)));

            parameters["Content"] = new List<object>();
            foreach (var tracker in rawTrackers)
            {
                foreach (var channel in tracker.ChannelMessages.Keys.Where(x => channels.Contains(x)))
                {
                    (parameters["Content"] as List<object>).Add(tracker.GetAsScope(channel));
                }
            }

            return parameters;
        }


        /// <summary>
        /// Event that is called when the Tracker fetches new data containing no Embed
        /// </summary>
        /// <returns>A Task that can be awaited</returns>
        private async Task OnMinorEvent(ulong channelID, Tracker.BaseTracker sender, string notification)
        {
            if (!Program.Client.ConnectionState.Equals(Discord.ConnectionState.Connected))
                return;
            try
            {
                await ((Discord.WebSocket.SocketTextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync(notification);
            }
            catch
            {
                if (Program.Client.GetChannel(channelID) == null || (await ((IGuildChannel)Program.Client.GetChannel(channelID)).Guild.GetCurrentUserAsync()) == null)
                {
                    await TryRemoveTrackerAsync(sender.Name, channelID);
                    await Program.MopsLog(new LogMessage(LogSeverity.Warning, "", $"Removed Tracker: {sender.Name} Channel {channelID} is missing"));
                }
                else
                {
                    var permission = (await ((IGuildChannel)Program.Client.GetChannel(channelID)).Guild.GetCurrentUserAsync()).GetPermissions(((IGuildChannel)Program.Client.GetChannel(channelID)));
                    if (!permission.SendMessages || !permission.ViewChannel || !permission.ReadMessageHistory)
                    {
                        await TryRemoveTrackerAsync(sender.Name, channelID);
                        await Program.MopsLog(new LogMessage(LogSeverity.Warning, "", $"Removed Tracker: {sender.Name} Channel {channelID} due to missing permissions"));
                        if (permission.SendMessages)
                        {
                            await ((ITextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync($"Removed tracker for `{sender.Name}` due to missing Permissions");
                        }
                    }
                }

            }
        }

        /// <summary>
        /// Event that is called when the Tracker fetches new data containing an Embed
        /// Updates or creates the notification message with it
        /// </summary>
        /// <returns>A Task that can be awaited</returns>
        private async Task OnMajorEvent(ulong channelID, Embed embed, Tracker.BaseTracker sender, string notification)
        {
            if (!Program.Client.ConnectionState.Equals(Discord.ConnectionState.Connected))
                return;
            try
            {
                if (sender is BaseUpdatingTracker)
                {
                    BaseUpdatingTracker tracker = sender as BaseUpdatingTracker;
                    if (tracker.ToUpdate.ContainsKey(channelID))
                    {
                        var message = ((IUserMessage)((ITextChannel)Program.Client.GetChannel(channelID)).GetMessageAsync(tracker.ToUpdate[channelID]).Result);
                        if (message != null)
                            await message.ModifyAsync(x =>
                            {
                                x.Content = notification;
                                x.Embed = embed;
                            });
                        else
                        {
                            var newMessage = await ((Discord.WebSocket.SocketTextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync(notification, embed: embed);
                            tracker.ToUpdate[channelID] = newMessage.Id;
                            await tracker.setReaction((IUserMessage)message);
                            await UpdateDBAsync(tracker);
                        }
                    }
                    else
                    {
                        var message = await ((Discord.WebSocket.SocketTextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync(notification, embed: embed);
                        tracker.ToUpdate.Add(channelID, message.Id);
                        await tracker.setReaction((IUserMessage)message);
                        await UpdateDBAsync(tracker);
                    }
                }
                else
                    await ((Discord.WebSocket.SocketTextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync(notification, embed: embed);
            }
            catch
            {
                //Check if channel still exists, or existing only in cache
                if (Program.Client.GetChannel(channelID) == null || (await ((IGuildChannel)Program.Client.GetChannel(channelID)).Guild.GetCurrentUserAsync()) == null)
                {
                    //await TryRemoveTrackerAsync(sender.Name, channelID);
                    await Program.MopsLog(new LogMessage(LogSeverity.Warning, "", $"Removed {typeof(T).Name}: {sender.Name} Channel {channelID} is missing"));
                }
                //Check if permissions were modified, to an extend of making the tracker unusable
                else
                {
                    var permission = (await ((IGuildChannel)Program.Client.GetChannel(channelID)).Guild.GetCurrentUserAsync()).GetPermissions(((IGuildChannel)Program.Client.GetChannel(channelID)));
                    if (!permission.SendMessages || !permission.ViewChannel || !permission.ReadMessageHistory || (sender is Tracker.BaseUpdatingTracker && (!permission.AddReactions || !permission.ManageMessages)))
                    {
                        await TryRemoveTrackerAsync(sender.Name, channelID);
                        await Program.MopsLog(new LogMessage(LogSeverity.Warning, "", $"Removed a {typeof(T).Name} for {sender.Name} from Channel {channelID} due to missing Permissions"));
                        if (permission.SendMessages)
                        {
                            await ((ITextChannel)Program.Client.GetChannel(channelID)).SendMessageAsync($"Removed tracker for `{sender.Name}` due to missing Permissions");
                        }
                    }
                }
            }
        }
    }
}
