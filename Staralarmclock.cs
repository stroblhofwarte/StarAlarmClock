using Stroblhofwarte.NINA.Staralarmclock.Properties;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Settings = Stroblhofwarte.NINA.Staralarmclock.Properties.Settings;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Image.ImageAnalysis;
using Nito.AsyncEx.Synchronous;
using System.Net.NetworkInformation;

namespace Stroblhofwarte.NINA.Staralarmclock {
    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    /// 
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "Staralarmclock_Options" where Staralarmclock corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class Staralarmclock : PluginBase, INotifyPropertyChanged {
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;
        private readonly IImagingMediator imagingMediator;
        private string _info = string.Empty;
        private uPLibrary.Networking.M2Mqtt.MqttClient _mqtt = null;

        [ImportingConstructor]
        public Staralarmclock(IProfileService profileService, IOptionsVM options, 
            IImagingMediator imagingMediator) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }


            System.Diagnostics.Debugger.Launch();


            // This helper class can be used to store plugin settings that are dependent on the current profile
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));
            this.profileService = profileService;
            // React on a changed profile
            profileService.ProfileChanged += ProfileService_ProfileChanged;

            // Hook for new images to do the analysation:
            imagingMediator.ImagePrepared += ImagingMediator_ImagePrepared;

        }

        private void ImagingMediator_ImagePrepared(object sender, ImagePreparedEventArgs e) {

            IStarDetection analy = new StarDetection();
            StarDetectionParams param = new StarDetectionParams();
            param.OuterCropRatio = 1.0;
            param.InnerCropRatio = 0.8;
            param.NoiseReduction = NoiseReductionEnum.Normal;
            param.Sensitivity = StarSensitivityEnum.Normal;
            param.IsAutoFocus = false;

            System.Threading.CancellationToken cancel = new System.Threading.CancellationToken();
            
            var task = Task.Run(async () => await analy.Detect(e.RenderedImage, e.RenderedImage.Image.Format, param, null, cancel));
            StarDetectionResult result = task.WaitAndUnwrapException();
            IStarDetectionAnalysis starAnal = new StarDetectionAnalysis();
            analy.UpdateAnalysis(starAnal, param, result);
            
        }

        public override Task Teardown() {
            // Make sure to unregister an event when the object is no longer in use. Otherwise garbage collection will be prevented.
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
            imagingMediator.ImagePrepared -= ImagingMediator_ImagePrepared;
            return base.Teardown();
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            // Rase the event that this profile specific value has been changed due to the profile switch
            RaisePropertyChanged(nameof(MqttBrokerIp));
        }

        private bool Pingable(string ip) {
            try {
                Ping pinger = new Ping();
                PingReply reply = pinger.Send(ip);
                if (reply.Status == IPStatus.Success)
                    return true;
                return false;
            } catch (Exception) {
                return false;
            }
        }


        private bool Connect() {
            if (_mqtt == null) {
                try {
                    // Check if the given broker ip is online:
                    if(!Pingable(Settings.Default.MqttBrokerIp)) {
                        Info = "Broker not online!";
                        return false;
                    }
                    int port = Convert.ToInt32(Settings.Default.MqttBrokerPort);
                    _mqtt = new uPLibrary.Networking.M2Mqtt.MqttClient(Settings.Default.MqttBrokerIp, port, false, null, null, uPLibrary.Networking.M2Mqtt.MqttSslProtocols.None);
                    _mqtt.Connect("Stroblhofwarte.NINA.StarAlarmClock");
                    Info = "MQTT Broker connected";
                    return true;
                } catch (Exception ex) {
                    Info = "MQTT Broker could not be connected";
                    return false;
                }
            }
            return false;
        }

        public string MqttBrokerIp {
            get {
                return Settings.Default.MqttBrokerIp;
            }
            set {
                Settings.Default.MqttBrokerIp = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string MqttBrokerPort {
            get {
                return Settings.Default.MqttBrokerPort;
            }
            set {
                Settings.Default.MqttBrokerPort = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool UpdateSettings {
            get {
                return Settings.Default.UpdateSettings;
            }
            set {
                Settings.Default.UpdateSettings = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool ConnectMqtt {
            get {
                return Settings.Default.ConnectMqtt;
            }
            set {
                Settings.Default.ConnectMqtt = value;
                if (value) {
                    bool ret = Connect();
                    if (!ret) Settings.Default.ConnectMqtt = false;
                }
                else {
                    if(_mqtt != null) {
                        try {
                            _mqtt.Disconnect();
                            _mqtt = null;
                            Info = "Connection closed";
                        } catch (Exception ex) {
                            _mqtt = null;
                            Info = "Connection closed";
                        }
                    }
                }
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string Info {
            get {
                return _info;
            }
            set {
                _info = value;
                RaisePropertyChanged();
            }
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
