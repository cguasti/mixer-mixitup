﻿using MixItUp.Base.Services.External;
using MixItUp.Base.Util;
using StreamingClient.Base.Util;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MixItUp.Base.ViewModel.Controls.Services
{
    public class TwitterServiceControlViewModel : ServiceControlViewModelBase
    {
        public bool AuthorizationInProgress
        {
            get { return authorizationInProgress; }
            set
            {
                this.authorizationInProgress = value;
                this.NotifyPropertyChanged();
            }
        }
        private bool authorizationInProgress;

        public string AuthorizationPin
        {
            get { return this.authorizationPin; }
            set
            {
                this.authorizationPin = value;
                this.NotifyPropertyChanged();
            }
        }
        private string authorizationPin;

        public ICommand LogInCommand { get; set; }
        public ICommand LogOutCommand { get; set; }
        public ICommand AuthorizePinCommand { get; set; }

        public TwitterServiceControlViewModel()
            : base("Twitter")
        {
            this.LogInCommand = this.CreateCommand((parameter) =>
            {
                this.AuthorizationInProgress = true;
                Task.Run(async () =>
                {
                    Result result = await ChannelSession.Services.Twitter.Connect();
                    await DispatcherHelper.InvokeDispatcher(async () =>
                    {
                        if (result.Success)
                        {
                            this.IsConnected = true;
                        }
                        else
                        {
                            await this.ShowConnectFailureMessage(result);
                        }

                        this.AuthorizationInProgress = false;
                        this.AuthorizationPin = string.Empty;
                    });
                });
                return Task.FromResult(0);
            });

            this.LogOutCommand = this.CreateCommand(async (parameter) =>
            {
                await ChannelSession.Services.Twitter.Disconnect();

                ChannelSession.Settings.TwitterOAuthToken = null;

                this.IsConnected = false;
            });

            this.AuthorizePinCommand = this.CreateCommand((parameter) =>
            {
                if (!string.IsNullOrEmpty(this.AuthorizationPin))
                {
                    ChannelSession.Services.Twitter.SetAuthPin(this.AuthorizationPin);
                }
                return Task.FromResult(0);
            });

            this.IsConnected = ChannelSession.Services.Twitter.IsConnected;
        }
    }
}
