﻿using System;
using Microsoft.Extensions.Options;
using Q42.HueApi;
using Q42.HueApi.ColorConverters;
using Q42.HueApi.ColorConverters.HSB;
using Q42.HueApi.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using Microsoft.Extensions.Logging;

namespace PresenceLight.Core
{
    public interface IRemoteHueService
    {
        Task SetColor(string availability, string lightId, string bridgeId);
        Task<(string bridgeId, string apiKey, string bridgeIp)> RegisterBridge();
        Task<IEnumerable<Light>> GetLights();
        void Initialize(BaseConfig options);
    }
    public class RemoteHueService : IRemoteHueService
    {
        private BaseConfig _options;
        private RemoteHueClient _client;
        private IRemoteAuthenticationClient _authClient;
        private readonly ILogger<RemoteHueService> _logger;
        private MediatR.IMediator _mediator;

        public RemoteHueService(IOptionsMonitor<BaseConfig> optionsAccessor, ILogger<RemoteHueService> logger, MediatR.IMediator mediator)
        {
            _mediator = mediator;
            _logger = logger;
            _options = optionsAccessor.CurrentValue;
            if (!string.IsNullOrWhiteSpace(_options.LightSettings.Hue.RemoteHueClientId))
            {
                _authClient = new RemoteAuthenticationClient(_options.LightSettings.Hue.RemoteHueClientId, _options.LightSettings.Hue.RemoteHueClientSecret, _options.LightSettings.Hue.RemoteHueClientAppName);
            }
            else {
                _logger.LogWarning("Remote Hue Client Id is empty");
            } 
        }

        public void Initialize(BaseConfig options)
        {
            _options = options;
            _authClient = new RemoteAuthenticationClient(_options.LightSettings.Hue.RemoteHueClientId, _options.LightSettings.Hue.RemoteHueClientSecret, _options.LightSettings.Hue.RemoteHueClientAppName);
        }

        private async Task GetAccessToken()
        {
            try
            {
                Uri authorizeUri = _authClient.BuildAuthorizeUri(_options.LightSettings.Hue.RemoteHueClientAppName, _options.LightSettings.Hue.RemoteHueClientAppName);

                TryBindListenerOnFreePort(out HttpListener http, out int port, out string redirectURI);

                Helpers.OpenBrowser(authorizeUri.ToString());

                // Waits for the OAuth authorization response.
                var context = await http.GetContextAsync();

                //Sends an HTTP response to the browser.
                var response = context.Response;

                string responseString = string.Format("<html><head><meta http-equiv='refresh' content='10;url=https://www.philips-hue.com/'></head><body>Please return to the app.</body></html>");
                var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                var responseOutput = response.OutputStream;
                Task responseTask = responseOutput.WriteAsync(buffer, 0, buffer.Length).ContinueWith((task) =>
                {
                    responseOutput.Close();
                    http.Stop();
                    Debug.WriteLine("HTTP server stopped.");
                });


                // extracts the code
                var code = context.Request.QueryString.Get("code") ?? "";
                var incoming_state = context.Request.QueryString.Get("state");

                var accessToken = await _authClient.GetToken(code);

                _client = new RemoteHueClient(_authClient.GetValidToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error Occured Processing Access Token for Remote Bridge");
                throw;
            }
        }

        private static bool TryBindListenerOnFreePort(out HttpListener httpListener, out int port, out string uri)
        {
            // IANA suggested range for dynamic or private ports
            const int MinPort = 49215;
            const int MaxPort = 65535;

            for (port = MinPort; port < MaxPort; port++)
            {
                httpListener = new HttpListener();
                uri = $"http://localhost:{port}/";
                httpListener.Prefixes.Add(uri);
                try
                {
                    httpListener.Start();
                    return true;
                }
                catch
                {
                    // nothing to do here -- the listener disposes itself when Start throws
                }
            }

            port = 0;
            uri = null;
            httpListener = null;
            return false;
        }

        public async Task<(string bridgeId, string apiKey, string bridgeIp)> RegisterBridge()
        {
            try
            {
                await GetAccessToken();
                var bridges = await _client.GetBridgesAsync();

                string bridgeId;
                string bridgeIp;
                if (string.IsNullOrEmpty(_options.LightSettings.Hue.RemoteBridgeId))
                {
                    bridgeId = bridges.First().Id;
                    bridgeIp = bridges.First().InternalIpaddress;
                }
                else
                {
                    bridgeId = _options.LightSettings.Hue.RemoteBridgeId;
                    bridgeIp = _options.LightSettings.Hue.HueIpAddress;
                }

                string apiKey;
                if (string.IsNullOrEmpty(_options.LightSettings.Hue.HueApiKey))
                {
                    apiKey = await _client.RegisterAsync(bridgeId, _options.LightSettings.Hue.RemoteHueClientAppName);
                }
                else
                {
                    apiKey = _options.LightSettings.Hue.HueApiKey;
                }

                if (!_client.IsInitialized)
                {
                    _client.Initialize(bridgeId, apiKey);
                }

                //Register app
                return (bridgeId, apiKey, bridgeIp);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error Occured Registering Remote Bridge" );
                throw;
            }
        }

        public async Task SetColor(string availability, string lightId, string bridgeId)
        {
            try
            {
                if (string.IsNullOrEmpty(lightId))
                {
                    throw new ArgumentNullException("Remote Hue Selected Light Id Invalid");
                }


                if (_client == null)
                {
                    throw new ArgumentNullException("Remote Hue Client Not Configured");
                }
                bool useWorkingHours = await _mediator.Send(new WorkingHoursServices.UseWorkingHoursCommand());
                bool IsInWorkingHours = await _mediator.Send(new WorkingHoursServices.IsInWorkingHoursCommand());

                if (!useWorkingHours || (useWorkingHours && IsInWorkingHours))
                {

                    var command = new LightCommand();
                    string color = "";
                    string message = "";
                    switch (availability)
                    {
                        case "Available":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityAvailableStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityAvailableStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "Busy":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityBusyStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityBusyStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message); 
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "BeRightBack":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityBeRightBackStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityBeRightBackStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "Away":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityAwayStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityAwayStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "DoNotDisturb":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityDoNotDisturbStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityDoNotDisturbStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "Offline":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityOfflineStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityOfflineStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        case "Off":
                            if (!_options.LightSettings.Hue.Statuses.AvailabilityOffStatus.Disabled)
                            {
                                command.On = true;
                                color = _options.LightSettings.Hue.Statuses.AvailabilityOffStatus.Colour;
                            }
                            else
                            {
                                message = $"Turning Hue Light {lightId} Off";
                                _logger.LogInformation(message);
                                command.On = false;
                                await _client.SendCommandAsync(command, new List<string> { lightId });
                                return;
                            }
                            break;
                        default:
                            command.On = true;
                            color = availability;
                            break;
                    }

                    color = color.Replace("#", "");

                    switch (color.Length)
                    {

                        case var length when color.Length == 6:
                            // Do Nothing
                            break;
                        case var length when color.Length > 6:
                            // Get last 6 characters
                            color = color.Substring(color.Length - 6);
                            break;
                        default:
                            throw new ArgumentException("Supplied Color had an issue");
                    }

                    command.SetColor(new RGBColor(color));


                    if (availability == "Off")
                    {
                        command.On = false;
                        message = $"Turning Hue Light {lightId} Off";
                        _logger.LogInformation(message);
                        await _client.SendCommandAsync(command, new List<string> { lightId });
                        return;
                    }

                    if (_options.LightSettings.UseDefaultBrightness)
                    {
                        if (_options.LightSettings.DefaultBrightness == 0)
                        {
                            command.On = false;
                        }
                        else
                        {
                            command.On = true;
                            command.Brightness = Convert.ToByte(((Convert.ToDouble(_options.LightSettings.DefaultBrightness) / 100) * 254));
                            command.TransitionTime = new TimeSpan(0);
                        }
                    }
                    else
                    {
                        if (_options.LightSettings.Hue.Brightness == 0)
                        {
                            command.On = false;
                        }
                        else
                        {
                            command.On = true;
                            command.Brightness = Convert.ToByte(((Convert.ToDouble(_options.LightSettings.Hue.Brightness) / 100) * 254));
                            command.TransitionTime = new TimeSpan(0);
                        }
                    }
                    message = $"Setting LIFX Light {lightId} to {color}";
                    _logger.LogInformation(message);
                    await _client.SendCommandAsync(command, new List<string> { lightId });
                }
            }
            catch (Exception e)
            {
               _logger.LogError(e,"Error Occured Setting Color");
                throw;
            }
        }

        public async Task<IEnumerable<Light>> GetLights()
        {
            try
            {
                if (_client == null || !_client.IsInitialized)
                {
                    _client = new RemoteHueClient(_authClient.GetValidToken);
                    _client.Initialize(_options.LightSettings.Hue.RemoteBridgeId, _options.LightSettings.Hue.HueApiKey);
                }

                var lights = await _client.GetLightsAsync();
                // if there are no lights, get some
                if (lights.Count() == 0)
                {
                    await _client.SearchNewLightsAsync();
                    Thread.Sleep(40000);
                    lights = await _client.GetNewLightsAsync();
                }
                return lights;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error Getting Lights", e);
                throw;
            }
        }

        public Task<string> FindBridge()
        {
            throw new NotImplementedException();
        }
    }
}
