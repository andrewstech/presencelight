﻿using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using PresenceLight.Core;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LifxCloud.NET.Models;

namespace PresenceLight.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ConfigWrapper Config;
        private readonly IHueService _hueService;
        private readonly AppState _appState;
        private readonly ILogger<Worker> _logger;
        private readonly UserAuthService _userAuthService;
        private readonly GraphServiceClient _graphClient;

        private LIFXService _lifxService;

        public Worker(IHueService hueService,
                      ILogger<Worker> logger,
                      IOptionsMonitor<ConfigWrapper> optionsAccessor,
                      AppState appState,
                      LIFXService lifxService,
                      UserAuthService userAuthService)
        {
            Config = optionsAccessor.CurrentValue;
            _hueService = hueService;
            _lifxService = lifxService;
            _logger = logger;
            _appState = appState;
            _userAuthService = userAuthService;

            _graphClient = new GraphServiceClient(userAuthService);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Debugger.IsAttached)
            {
                Helpers.OpenBrowser("https://localhost:5001");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (await _userAuthService.IsUserAuthenticated())
                {
                    try
                    {
                        await GetData();
                    }
                    catch { }
                    await Task.Delay(Convert.ToInt32(Config.PollingInterval * 1000), stoppingToken);
                }
                await Task.Delay(1000, stoppingToken);
            }
        }

        private async Task GetData()
        {
            var token = await _userAuthService.GetAccessToken();

            var user = await GetUserInformation(token);

            var photo = await GetPhotoAsBase64Async(token);

            var presence = await GetPresence(token);

            _appState.SetUserInfo(user, photo, presence);

            if (!string.IsNullOrEmpty(Config.HueApiKey) && !string.IsNullOrEmpty(Config.HueIpAddress) && !string.IsNullOrEmpty(Config.SelectedHueLightId))
            {
                await _hueService.SetColor(presence.Availability, Config.SelectedHueLightId);
            }

            if (Config.IsLIFXEnabled && !string.IsNullOrEmpty(Config.LIFXApiKey))
            {
                await _lifxService.SetColor(presence.Availability, (Selector)Config.SelectedLIFXItemId);
            }

            while (await _userAuthService.IsUserAuthenticated())
            {
                token = await _userAuthService.GetAccessToken();
                presence = await GetPresence(token);

                _appState.SetPresence(presence);
                _logger.LogInformation($"Presence is {presence.Availability}");
                if (!string.IsNullOrEmpty(Config.HueApiKey) && !string.IsNullOrEmpty(Config.HueIpAddress) && !string.IsNullOrEmpty(Config.SelectedHueLightId))
                {
                    await _hueService.SetColor(presence.Availability, Config.SelectedHueLightId);
                }

                if (Config.IsLIFXEnabled && !string.IsNullOrEmpty(Config.LIFXApiKey))
                {
                    await _lifxService.SetColor(presence.Availability, (Selector)Config.SelectedLIFXItemId);
                }

                Thread.Sleep(5000);
            }

            _logger.LogInformation("User logged out, no longer polling for presence.");
        }

        public async Task<User> GetUserInformation(string accessToken)
        {
            try
            {
                var me = await _graphClient.Me.Request().GetAsync();
                _logger.LogInformation($"User is {me.DisplayName}");
                return me;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception getting me: {ex.Message}");
                throw ex;
            }
        }

        public async Task<string> GetPhotoAsBase64Async(string accessToken)
        {
            try
            {
                var photoStream = await _graphClient.Me.Photo.Content.Request().GetAsync();
                var memoryStream = new MemoryStream();
                photoStream.CopyTo(memoryStream);

                var photoBytes = memoryStream.ToArray();
                var base64Photo = $"data:image/gif;base64,{Convert.ToBase64String(photoBytes)}";

                return base64Photo;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception getting photo: {ex.Message}");
            }

            return null;
        }

        public async Task<Presence> GetPresence(string accessToken)
        {
            try
            {
                var presence = await _graphClient.Me.Presence.Request().GetAsync();
                _logger.LogInformation($"Presence is {presence.Availability}");
                return presence;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception getting presence: {ex.Message}");
                throw ex;
            }
        }
    }
}
