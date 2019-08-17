﻿using Mixer.Base.Model.Chat;
using Mixer.Base.Model.User;
using MixItUp.Base.Commands;
using MixItUp.Base.Model;
using MixItUp.Base.Services.Mixer;
using MixItUp.Base.Util;
using MixItUp.Base.ViewModel.Chat;
using MixItUp.Base.ViewModel.User;
using StreamingClient.Base.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MixItUp.Base.Services
{
    public interface IChatService
    {
        IMixerChatService MixerChatService { get; }

        Task Initialize(MixerChatService mixerChatService);

        bool DisableChat { get; set; }

        ObservableCollection<ChatMessageViewModel> Messages { get; }

        LockedDictionary<string, UserViewModel> AllUsers { get; }
        IEnumerable<UserViewModel> DisplayUsers { get; }
        event EventHandler DisplayUsersUpdated;

        Task SendMessage(PlatformTypeEnum platform, string message, bool sendAsStreamer = false);
        Task Whisper(PlatformTypeEnum platform, UserViewModel user, string message, bool sendAsStreamer = false, bool waitForResponse = false);
        Task Whisper(PlatformTypeEnum platform, string username, string message, bool sendAsStreamer = false, bool waitForResponse = false);
        Task DeleteMessage(ChatMessageViewModel message);
        Task AddMessage(ChatMessageViewModel message);

        Task ClearMessages();

        Task PurgeUser(UserViewModel user);
    }

    public class ChatService : IChatService
    {
        private const string ChatEventLogDirectoryName = "ChatEventLogs";
        private const string ChatEventLogFileNameFormat = "ChatEventLog-{0}.txt";

        public bool DisableChat { get; set; }

        public ObservableCollection<ChatMessageViewModel> Messages { get; private set; } = new ObservableCollection<ChatMessageViewModel>();
        private LockedDictionary<string, ChatMessageViewModel> messagesLookup = new LockedDictionary<string, ChatMessageViewModel>();

        public LockedDictionary<string, UserViewModel> AllUsers { get; private set; } = new LockedDictionary<string, UserViewModel>();
        public IEnumerable<UserViewModel> DisplayUsers
        {
            get
            {
                IEnumerable<UserViewModel> users = this.displayUsers.Values;
                users = users.ToList();
                users = users.Take(ChannelSession.Settings.MaxUsersShownInChat);
                return users;
            }
        }
        public event EventHandler DisplayUsersUpdated = delegate { };
        private SortedList<string, UserViewModel> displayUsers = new SortedList<string, UserViewModel>();

        private HashSet<string> userEntranceCommands = new HashSet<string>();

        private SemaphoreSlim whisperNumberLock = new SemaphoreSlim(1);
        private Dictionary<string, int> whisperMap = new Dictionary<string, int>();

        public IMixerChatService MixerChatService { get; private set; }

        private string currentChatEventLogFilePath;

        public ChatService() { }

        public async Task Initialize(MixerChatService mixerChatService)
        {
            this.MixerChatService = mixerChatService;

            await ChannelSession.Services.FileService.CreateDirectory(ChatEventLogDirectoryName);
            this.currentChatEventLogFilePath = Path.Combine(ChatEventLogDirectoryName, string.Format(ChatEventLogFileNameFormat, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)));

            this.MixerChatService.OnMessageOccurred += MixerChatService_OnMessageOccurred;
            this.MixerChatService.OnDeleteMessageOccurred += MixerChatService_OnDeleteMessageOccurred;
            this.MixerChatService.OnClearMessagesOccurred += MixerChatService_OnClearMessagesOccurred;
            this.MixerChatService.OnUsersJoinOccurred += MixerChatService_OnUsersJoinOccurred;
            this.MixerChatService.OnUserUpdateOccurred += MixerChatService_OnUserUpdateOccurred;
            this.MixerChatService.OnUsersLeaveOccurred += MixerChatService_OnUsersLeaveOccurred;
            this.MixerChatService.OnUserPurgeOccurred += MixerChatService_OnUserPurgeOccurred;
            this.MixerChatService.OnUserBanOccurred += MixerChatService_OnUserBanOccurred;

            foreach (ChatMessageEventModel message in await this.MixerChatService.GetChatHistory(50))
            {
                await this.AddMessage(new ChatMessageViewModel(message));
            }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
            {
                await ChannelSession.MixerStreamerConnection.GetChatUsers(ChannelSession.MixerChannel, async (collection) =>
                {
                    List<UserViewModel> users = new List<UserViewModel>();
                    foreach (ChatUserModel chatUser in collection)
                    {
                        users.Add(new UserViewModel(chatUser));
                    }
                    await this.UsersJoined(users);
                }, int.MaxValue);
            });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        public async Task SendMessage(PlatformTypeEnum platform, string message, bool sendAsStreamer = false)
        {
            if (platform == PlatformTypeEnum.Mixer)
            {
                await this.MixerChatService.SendMessage(message, sendAsStreamer);
            }
        }

        public async Task Whisper(PlatformTypeEnum platform, UserViewModel user, string message, bool sendAsStreamer = false, bool waitForResponse = false) { await this.Whisper(platform, user.UserName, message, sendAsStreamer); }

        public async Task Whisper(PlatformTypeEnum platform, string username, string message, bool sendAsStreamer = false, bool waitForResponse = false)
        {
            if (platform == PlatformTypeEnum.Mixer)
            {
                if (waitForResponse)
                {
                    ChatMessageEventModel messageEvent = await this.MixerChatService.WhisperWithResponse(username, message, sendAsStreamer);
                    if (messageEvent != null)
                    {
                        await this.AddMessage(new ChatMessageViewModel(messageEvent));
                    }
                }
                else
                {
                    await this.MixerChatService.Whisper(username, message, sendAsStreamer);
                }
            }
        }

        public async Task DeleteMessage(ChatMessageViewModel message)
        {
            message.IsDeleted = true;
            if (message.Platform == PlatformTypeEnum.Mixer)
            {
                await this.MixerChatService.DeleteMessage(message);
            }
        }

        public async Task ClearMessages()
        {
            this.messagesLookup.Clear();
            this.Messages.Clear();
            await this.MixerChatService.ClearMessages();
        }

        public async Task PurgeUser(UserViewModel user)
        {
            if (user.Platform == PlatformTypeEnum.Mixer)
            {
                await this.MixerChatService.PurgeUser(user.UserName);
            }
        }

        public async Task AddMessage(ChatMessageViewModel message)
        {
            if (message.User != null)
            {
                await message.User.RefreshDetails();
            }
            message.User.UpdateLastActivity();

            Logger.Log(LogLevel.Debug, string.Format("Message Received - {0}", message.ToString()));

            if (!message.IsWhisper && !this.userEntranceCommands.Contains(message.User.ID.ToString()))
            {
                this.userEntranceCommands.Add(message.User.ID.ToString());
                if (message.User.Data.EntranceCommand != null)
                {
                    await message.User.Data.EntranceCommand.Perform(message.User);
                }
            }

            this.messagesLookup[message.ID] = message;
            if (ChannelSession.Settings.LatestChatAtTop)
            {
                this.Messages.Insert(0, message);
            }
            else
            {
                this.Messages.Add(message);
            }

            if (this.Messages.Count > ChannelSession.Settings.MaxMessagesInChat)
            {
                ChatMessageViewModel removedMessage = (ChannelSession.Settings.LatestChatAtTop) ? this.Messages.Last() : this.Messages.First();
                this.messagesLookup.Remove(removedMessage.ID);
                this.Messages.Remove(removedMessage);
            }

            if (this.DisableChat && !message.ID.Equals(Guid.Empty))
            {
                Logger.Log(LogLevel.Debug, string.Format("Deleting Message As Chat Disabled - {0}", message.PlainTextMessage));
                await this.DeleteMessage(message);
                return;
            }

            //if (!ModerationHelper.MeetsChatInteractiveParticipationRequirement(message.User) || !ModerationHelper.MeetsChatEmoteSkillsOnlyParticipationRequirement(message.User, message))
            //{
            //    Logger.Log(LogLevel.Debug, string.Format("Deleting Message As User does not meet requirement - {0} - {1}", ChannelSession.Settings.ModerationChatInteractiveParticipation, message.PlainTextMessage));

            //    await this.DeleteMessage(message);

            //    await ModerationHelper.SendChatInteractiveParticipationWhisper(message.User, isChat: true);
            //    return;
            //}

            //string moderationReason = await message.ShouldBeModerated();
            //if (!string.IsNullOrEmpty(moderationReason))
            //{
            //    Logger.Log(LogLevel.Debug, string.Format("Moderation Being Performed - {0}", message.ToString()));

            //    message.ModerationReason = moderationReason;
            //    await this.DeleteMessage(message);
            //}

            //if (!string.IsNullOrEmpty(ChannelSession.Settings.NotificationChatWhisperSoundFilePath) && message.IsWhisper)
            //{
            //    await ChannelSession.Services.AudioService.Play(ChannelSession.Settings.NotificationChatWhisperSoundFilePath, ChannelSession.Settings.NotificationChatWhisperSoundVolume);
            //}
            //else if (!string.IsNullOrEmpty(ChannelSession.Settings.NotificationChatTaggedSoundFilePath) && message.IsUserTagged)
            //{
            //    await ChannelSession.Services.AudioService.Play(ChannelSession.Settings.NotificationChatTaggedSoundFilePath, ChannelSession.Settings.NotificationChatTaggedSoundVolume);
            //}
            //else if (!string.IsNullOrEmpty(ChannelSession.Settings.NotificationChatMessageSoundFilePath))
            //{
            //    await ChannelSession.Services.AudioService.Play(ChannelSession.Settings.NotificationChatMessageSoundFilePath, ChannelSession.Settings.NotificationChatMessageSoundVolume);
            //}

            //if (message.IsWhisper && ChannelSession.Settings.TrackWhispererNumber && !message.IsStreamerOrBot())
            //{
            //    await this.whisperNumberLock.WaitAndRelease(() =>
            //    {
            //        if (!whisperMap.ContainsKey(message.User.ID.ToString()))
            //        {
            //            whisperMap[message.User.ID.ToString()] = whisperMap.Count + 1;
            //        }
            //        message.User.WhispererNumber = whisperMap[message.User.ID.ToString()];
            //        return Task.FromResult(0);
            //    });

            //    await ChannelSession.Chat.Whisper(message.User.UserName, $"You are whisperer #{message.User.WhispererNumber}.", false);
            //}

            //GlobalEvents.ChatMessageReceived(message);

            //Logger.Log(LogLevel.Debug, string.Format("Checking Message For Command - {0}", message.ToString()));

            //if (!ChannelSession.Settings.AllowCommandWhispering && message.IsWhisper)
            //{
            //    return;
            //}

            //if (ChannelSession.MixerBotUser != null && ChannelSession.Settings.IgnoreBotAccountCommands && message.User != null && message.User.ID.Equals(ChannelSession.MixerBotUser.id))
            //{
            //    return;
            //}

            //if (ChannelSession.Settings.CommandsOnlyInYourStream && !message.IsInUsersChannel)
            //{
            //    return;
            //}

            //if (ChannelSession.IsStreamer && !message.User.MixerRoles.Contains(MixerRoleEnum.Banned))
            //{
            //    GlobalEvents.ChatCommandMessageReceived(message);

            //    List<PermissionsCommandBase> commandsToCheck = new List<PermissionsCommandBase>(ChannelSession.AllEnabledChatCommands);
            //    commandsToCheck.AddRange(message.User.Data.CustomCommands);

            //    PermissionsCommandBase command = commandsToCheck.FirstOrDefault(c => c.MatchesCommand(message.PlainTextMessage));
            //    if (command == null)
            //    {
            //        command = commandsToCheck.FirstOrDefault(c => c.ContainsCommand(message.PlainTextMessage));
            //    }

            //    if (command != null)
            //    {
            //        Logger.Log(LogLevel.Debug, string.Format("Command Found For Message - {0} - {1}", message.ToString(), command.ToString()));

            //        await command.Perform(message.User, command.GetArgumentsFromText(message.PlainTextMessage));

            //        bool delete = false;
            //        if (ChannelSession.Settings.DeleteChatCommandsWhenRun)
            //        {
            //            if (!command.Requirements.Settings.DontDeleteChatCommandWhenRun)
            //            {
            //                delete = true;
            //            }
            //        }
            //        else if (command.Requirements.Settings.DeleteChatCommandWhenRun)
            //        {
            //            delete = true;
            //        }

            //        if (delete)
            //        {
            //            Logger.Log(LogLevel.Debug, string.Format("Deleting Message As Chat Command - {0}", message.PlainTextMessage));
            //            await this.DeleteMessage(message);
            //        }
            //    }
            //}
        }

        private Task UsersJoined(IEnumerable<UserViewModel> users)
        {
            foreach (UserViewModel user in users)
            {
                this.AllUsers[user.ID.ToString()] = user;
                this.displayUsers[user.SortableID] = user;
            }
            this.DisplayUsersUpdated(this, new EventArgs());
            return Task.FromResult(0);
        }

        private async Task UsersUpdated(IEnumerable<UserViewModel> users)
        {
            await this.UsersLeft(users);
            await this.UsersJoined(users);
        }

        private Task UsersLeft(IEnumerable<UserViewModel> users)
        {
            foreach (UserViewModel user in users)
            {
                if (this.AllUsers.Remove(user.ID.ToString()))
                {
                    this.displayUsers.Remove(user.UserName);
                }
            }
            this.DisplayUsersUpdated(this, new EventArgs());
            return Task.FromResult(0);
        }

        #region Mixer Events

        private async void MixerChatService_OnMessageOccurred(object sender, ChatMessageViewModel message)
        {
            await this.AddMessage(message);
            if (ChannelSession.Settings.SaveChatEventLogs)
            {
                try
                {
                    await ChannelSession.Services.FileService.AppendFile(this.currentChatEventLogFilePath, string.Format("{0} ({1}){2}",
                        message, DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), Environment.NewLine));
                }
                catch (Exception) { }
            }
        }

        private void MixerChatService_OnDeleteMessageOccurred(object sender, Guid id)
        {
            if (this.messagesLookup.TryGetValue(id.ToString(), out ChatMessageViewModel message))
            {
                message.IsDeleted = true;
                GlobalEvents.ChatMessageDeleted(id);
            }
        }

        private void MixerChatService_OnClearMessagesOccurred(object sender, EventArgs e)
        {
            this.messagesLookup.Clear();
            this.Messages.Clear();
        }

        private async void MixerChatService_OnUsersJoinOccurred(object sender, IEnumerable<UserViewModel> users)
        {
            await this.UsersJoined(users);
        }

        private async void MixerChatService_OnUserUpdateOccurred(object sender, UserViewModel user)
        {
            await this.UsersUpdated(new List<UserViewModel>() { user });
        }

        private async void MixerChatService_OnUsersLeaveOccurred(object sender, IEnumerable<UserViewModel> users)
        {
            await this.UsersLeft(users);
        }

        private async void MixerChatService_OnUserPurgeOccurred(object sender, Tuple<UserViewModel, UserViewModel> e)
        {
            foreach (ChatMessageViewModel message in this.Messages.ToList())
            {
                if (message.User.Equals(e.Item1))
                {
                    message.IsDeleted = true;
                    if (e.Item2 != null)
                    {
                        message.DeletedBy = e.Item2.UserName;
                    }
                }
            }

            if (ChannelSession.Constellation.CanUserRunEvent(e.Item1, EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserPurge)))
            {
                ChannelSession.Constellation.LogUserRunEvent(e.Item1, EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserPurge));
                UserViewModel targetUser = e.Item1;
                UserViewModel modUser = e.Item2;
                if (e.Item2 == null)
                {
                    modUser = new UserViewModel(ChannelSession.MixerStreamerUser);
                }
                await ChannelSession.Constellation.RunEventCommand(ChannelSession.Constellation.FindMatchingEventCommand(EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserPurge)), modUser, arguments: new List<string>() { targetUser.UserName });
            }
        }

        private async void MixerChatService_OnUserBanOccurred(object sender, UserViewModel user)
        {
            if (ChannelSession.Constellation.CanUserRunEvent(user, EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserBan)))
            {
                ChannelSession.Constellation.LogUserRunEvent(user, EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserBan));
                await ChannelSession.Constellation.RunEventCommand(ChannelSession.Constellation.FindMatchingEventCommand(EnumHelper.GetEnumName(OtherEventTypeEnum.MixerUserBan)), user);
            }
        }

        #endregion Mixer Events
    }
}
