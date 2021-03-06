﻿using MixItUp.Base.Services;
using MixItUp.Base.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MixItUp.Base.ViewModel.Controls.Settings
{
    public class OverlayEndpointListingViewModel : UIViewModelBase
    {
        public string Name { get; set; }
        public int Port { get; set; }

        public string Address { get { return string.Format(OverlayEndpointService.RegularOverlayHttpListenerServerAddressFormat, this.Port); } }

        public bool CanDelete { get { return !ChannelSession.Services.Overlay.DefaultOverlayName.Equals(this.Name); } }

        public ICommand DeleteCommand { get; set; }

        private OverlaySettingsControlViewModel viewModel;

        public OverlayEndpointListingViewModel(OverlaySettingsControlViewModel viewModel, string name, int port)
        {
            this.viewModel = viewModel;
            this.Name = name;
            this.Port = port;

            this.DeleteCommand = this.CreateCommand(async (parameter) =>
            {
                ChannelSession.Settings.OverlayCustomNameAndPorts.Remove(this.Name);
                await ChannelSession.Services.Overlay.RemoveOverlay(this.Name);
                this.viewModel.Endpoints.Remove(this);
            });
        }
    }

    public class OverlaySettingsControlViewModel : UIViewModelBase
    {
        public ObservableCollection<OverlayEndpointListingViewModel> Endpoints { get; set; } = new ObservableCollection<OverlayEndpointListingViewModel>();

        public string NewEndpointName
        {
            get { return this.newEndpointName; }
            set
            {
                this.newEndpointName = value;
                this.NotifyPropertyChanged();
            }
        }
        private string newEndpointName;

        public ICommand AddCommand { get; set; }

        public OverlaySettingsControlViewModel()
        {
            this.AddCommand = this.CreateCommand(async (parameter) =>
            {
                if (!string.IsNullOrEmpty(this.NewEndpointName))
                {
                    if (!this.Endpoints.Any(p => p.Name.Equals(this.NewEndpointName)))
                    {
                        int port = this.Endpoints.Max(o => o.Port) + 1;
                        OverlayEndpointListingViewModel overlay = new OverlayEndpointListingViewModel(this, this.NewEndpointName, port);

                        ChannelSession.Settings.OverlayCustomNameAndPorts[overlay.Name] = overlay.Port;
                        await ChannelSession.Services.Overlay.AddOverlay(overlay.Name, overlay.Port);
                        this.Endpoints.Add(overlay);
                    }
                }
                this.NewEndpointName = string.Empty;
            });
        }

        protected override Task OnLoadedInternal()
        {
            this.Endpoints.Clear();
            foreach (var kvp in ChannelSession.Services.Overlay.AllOverlayNameAndPorts.OrderBy(kvp => kvp.Value))
            {
                this.Endpoints.Add(new OverlayEndpointListingViewModel(this, kvp.Key, kvp.Value));
            }
            return Task.FromResult(0);
        }
    }
}
