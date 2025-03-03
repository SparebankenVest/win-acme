﻿using System;
using PKISharp.WACS.Services;

namespace PKISharp.WACS.UnitTests.Mock.Services
{
    class MockSettingsService : ISettingsService
    {
        public Uri BaseUri => new Uri("https://pkisharp.github.io/win-acme/");
        public SettingsService.UiSettings UI => new SettingsService.UiSettings();
        public SettingsService.AcmeSettings Acme => new SettingsService.AcmeSettings();
        public SettingsService.ProxySettings Proxy => new SettingsService.ProxySettings();
        public SettingsService.CacheSettings Cache => new SettingsService.CacheSettings();
        public SettingsService.ScheduledTaskSettings ScheduledTask => new SettingsService.ScheduledTaskSettings();
        public SettingsService.NotificationSettings Notification => new SettingsService.NotificationSettings();
        public SettingsService.SecuritySettings Security => new SettingsService.SecuritySettings();
        public SettingsService.ClientSettings Client => new SettingsService.ClientSettings();
        public SettingsService.ValidationSettings Validation => new SettingsService.ValidationSettings();
        public SettingsService.StoreSettings Store => new SettingsService.StoreSettings();
        public string ExePath => "";
        public SettingsService.ScriptSettings Script => new SettingsService.ScriptSettings();
    }
}
