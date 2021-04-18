﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using CustomFloorPlugin.Configuration;
using CustomFloorPlugin.UI;

using IPA.Loader;
using IPA.Utilities.Async;

using SiraUtil;

using Zenject;


namespace CustomFloorPlugin
{
    /// <summary>
    /// A class that handles all interaction with outside plugins, atm just SongCore and Cinema
    /// Also detects changes in the platforms directory and loads added platforms automaticly / deletes removed ones
    /// </summary>
    internal class API : IInitializable, IDisposable
    {
        private readonly WebClient _webClient;
        private readonly PluginConfig _config;
        private readonly PlatformManager _platformManager;
        private readonly PlatformSpawner _platformSpawner;
        private readonly PlatformListsView _platformListsView;
        private readonly FileSystemWatcher _fileSystemWatcher;
        private bool _apiRequest;

        public API(WebClient webClient,
                   PluginConfig config,
                   PlatformManager platformManager,
                   PlatformSpawner platformSpawner,
                   PlatformListsView platformListsView)
        {
            _webClient = webClient;
            _config = config;
            _platformManager = platformManager;
            _platformSpawner = platformSpawner;
            _platformListsView = platformListsView;
            _fileSystemWatcher = new(_config.CustomPlatformsDirectory, "*.plat");
        }

        public void Initialize()
        {
            _fileSystemWatcher.Changed += OnFileChanged;
            _fileSystemWatcher.Created += OnFileCreated;
            _fileSystemWatcher.Deleted += OnFileDeleted;
            _fileSystemWatcher.EnableRaisingEvents = true;
            if (PluginManager.GetPlugin("SongCore") != null)
                SubscribeToSongCoreEvent();
            if (PluginManager.GetPlugin("Cinema") != null)
                SubscribeToCinemaEvent();
        }

        public void Dispose()
        {
            _fileSystemWatcher.Changed -= OnFileChanged;
            _fileSystemWatcher.Created -= OnFileCreated;
            _fileSystemWatcher.Deleted -= OnFileDeleted;
            _fileSystemWatcher.Dispose();
            if (PluginManager.GetPlugin("SongCore") != null)
                UnsubscribeFromSongCoreEvent();
            if (PluginManager.GetPlugin("Cinema") != null)
                UnsubscribeFromCinemaEvent();
        }

        /// <summary>
        /// Replaces the old platform with the updated version
        /// </summary>
        private async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            await await UnityMainThreadTaskScheduler.Factory.StartNew(async () => 
            {
                if (_platformManager.platformFilePaths.TryGetValue(e.FullPath, out CustomPlatform platform))
                {
                    bool wasActivePlatform = _platformManager.activePlatform == platform;
                    CustomPlatform? newPlatform = await _platformManager.CreatePlatformAsync(e.FullPath);
                    if (newPlatform == null)
                        return;
                    if (wasActivePlatform)
                    {
                        int index = _platformManager.loadPlatformsTask!.Result.IndexOf(newPlatform);
                        await _platformSpawner.ChangeToPlatformAsync(index);
                    }
                }
            });
        }

        /// <summary>
        /// Create the new platform and add it to the UI
        /// </summary>
        private async void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            await await UnityMainThreadTaskScheduler.Factory.StartNew(async () =>
            {
                CustomPlatform? newPlatform = await _platformManager.CreatePlatformAsync(e.FullPath);
                if (newPlatform == null)
                    return;
                _platformManager.loadPlatformsTask!.Result.Add(newPlatform);
                _platformListsView.AddCellForPlatform(newPlatform, true);
                if (_apiRequest)
                {
                    _platformManager.apiRequestedPlatform = newPlatform;
                    _apiRequest = false;
                }
            });
        }

        /// <summary>
        /// Destroy the platform and remove all references
        /// </summary>
        private async void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            await await UnityMainThreadTaskScheduler.Factory.StartNew(async () =>
            {
                if (_platformManager.platformFilePaths.TryGetValue(e.FullPath, out CustomPlatform platform))
                {
                    await _platformManager.loadPlatformsTask!;
                    _platformListsView.RemoveCellForPlatform(platform);
                    if (_platformManager.activePlatform == platform)
                        await _platformSpawner.ChangeToPlatformAsync(0);
                    _platformManager.platformFilePaths.Remove(platform.fullPath);
                    _platformManager.loadPlatformsTask.Result.Remove(platform);
                    UnityEngine.Object.Destroy(platform.gameObject);
                }
            });
        }

        private void SubscribeToSongCoreEvent() => SongCore.Plugin.CustomSongPlatformSelectionDidChange += OnSongCoreEvent;
        private void SubscribeToCinemaEvent() => BeatSaberCinema.Events.AllowCustomPlatform += OnCinemaEvent;
        private void UnsubscribeFromSongCoreEvent() => SongCore.Plugin.CustomSongPlatformSelectionDidChange -= OnSongCoreEvent;
        private void UnsubscribeFromCinemaEvent() => BeatSaberCinema.Events.AllowCustomPlatform -= OnCinemaEvent;

        /// <summary>
        /// Disable platform spawning as required by Cinema
        /// </summary>
        private async void OnCinemaEvent(bool allowPlatform)
        {
            if (!allowPlatform)
            {
                await _platformManager.loadPlatformsTask!;
                _platformManager.apiRequestedPlatform = _platformManager.loadPlatformsTask.Result[0];
            }
        }

        /// <summary>
        /// The class the API response of modelsaber is deserialized on
        /// </summary>
        [Serializable]
        public class PlatformDownloadData
        {
            public string? name;
            public string? download;
        }

        /// <summary>
        /// Asynchronously downloads a <see cref="CustomPlatform"/> from modelsaber if the selected level requires it
        /// </summary>
        /// <param name="usePlatform">Whether the selected song requests a platform or not</param>
        /// <param name="name">The name of the requested platform</param>
        /// <param name="hash">The hash of the requested platform</param>
        /// <param name="level">The song the platform was requested for</param>
        private async void OnSongCoreEvent(bool usePlatform, string name, string hash, IPreviewBeatmapLevel level)
        {
            // No platform is requested, abort
            if (!usePlatform)
            {
                _platformManager.apiRequestedPlatform = null;
                _platformManager.apiRequestedLevelId = null;
                return;
            }

            _platformManager.apiRequestedLevelId = level.levelID;

            // Check if the requested platform is already downloaded
            foreach (CustomPlatform platform in await _platformManager.loadPlatformsTask!)
            {
                if (platform.platHash == hash || platform.platName.StartsWith(name))
                {
                    _platformManager.apiRequestedPlatform = platform;
                    return;
                }
            }

            string url = hash != null ? $"https://modelsaber.com/api/v2/get.php?type=platform&filter=hash:{hash}"
                       : name != null ? $"https://modelsaber.com/api/v2/get.php?type=platform&filter=name:{name}"
                       : throw new ArgumentNullException($"{nameof(hash)}, {nameof(name)}", "Invalid platform request");

            WebResponse downloadDataWebResponse = await _webClient.GetAsync(url, CancellationToken.None);
            if (downloadDataWebResponse.IsSuccessStatusCode)
            {
                PlatformDownloadData platformDownloadData = downloadDataWebResponse.ContentToJson<Dictionary<string, PlatformDownloadData>>().FirstOrDefault().Value;
                WebResponse platDownloadWebResponse = await _webClient.GetAsync(platformDownloadData.download, CancellationToken.None);
                if (platDownloadWebResponse.IsSuccessStatusCode)
                {
                    byte[] platData = platDownloadWebResponse.ContentToBytes();
                    _apiRequest = true;
                    string destination = Path.Combine(_config.CustomPlatformsDirectory, platformDownloadData.name + ".plat");
                    File.WriteAllBytes(destination, platData);
                }
            }
        }
    }
}