﻿using System;
using System.IO;

using Xamarin.MacDev;
using Xamarin.MacDev.Tasks;

namespace Xamarin.iOS.Tasks
{
	public static class IPhoneSdks
	{
		const string MTOUCH_LOCATION_ENV_VAR = "MD_MTOUCH_SDK_ROOT";

		public static AppleIPhoneSdk Native { get; private set; }
		public static MonoTouchSdk MonoTouch { get; internal set; }
		public static AppleWatchSdk Watch { get; private set; }
		public static AppleTVOSSdk TVOS { get; private set; }

		static IPhoneSdks ()
		{
			Reload ();
		}

		public static void CheckInfoCaches ()
		{
			MonoTouch.CheckCaches ();
		}

		public static void Reload ()
		{
			var monotouch = Environment.GetEnvironmentVariable (MTOUCH_LOCATION_ENV_VAR);

			if (string.IsNullOrEmpty (monotouch)) {
				foreach (var location in MonoTouchSdk.DefaultLocations) {
					if (Directory.Exists (location)) {
						monotouch = location;
						break;
					}
				}
			}

			Native = new AppleIPhoneSdk (AppleSdkSettings.DeveloperRoot, AppleSdkSettings.DeveloperRootVersionPlist);
			MonoTouch = new MonoTouchSdk (monotouch);
			Watch = new AppleWatchSdk (AppleSdkSettings.DeveloperRoot, AppleSdkSettings.DeveloperRootVersionPlist);
			TVOS = new AppleTVOSSdk (AppleSdkSettings.DeveloperRoot, AppleSdkSettings.DeveloperRootVersionPlist);
		}

		public static AppleSdk GetSdk (PlatformFramework framework)
		{
			switch (framework) {
			case PlatformFramework.iOS:
				return IPhoneSdks.Native;
			case PlatformFramework.WatchOS:
				return IPhoneSdks.Watch;
			case PlatformFramework.TVOS:
				return IPhoneSdks.TVOS;
			default:
				throw new InvalidOperationException (string.Format ("Invalid framework: {0}", framework));
			}
		}

		public static AppleSdk GetSdk (string targetFrameworkIdentifier)
		{
			return GetSdk (PlatformFrameworkHelper.GetFramework (targetFrameworkIdentifier));
		}
	}
}
