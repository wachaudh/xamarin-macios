﻿using System;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.MacDev.Tasks;
using Xamarin.MacDev;
using MSBLocalization;

namespace Xamarin.iOS.Tasks
{
	public abstract class ResolveNativeWatchAppTaskBase : Task
	{

		#region Inputs

		public string SessionId { get; set; }

		[Required]
		public bool SdkIsSimulator { get; set; }

		[Required]
		public string SdkVersion { get; set; }

		[Required]
		public string TargetFrameworkIdentifier { get; set; }

		#endregion

		#region Outputs

		[Output]
		public string NativeWatchApp { get; set; }

		#endregion

		bool IsWatchFramework {
			get {
				return TargetFrameworkIdentifier == "Xamarin.WatchOS";
			}
		}

		public override bool Execute ()
		{
			var currentSdk = IPhoneSdks.GetSdk (TargetFrameworkIdentifier);
			IPhoneSdkVersion version;
			string sdk_path;

			if (IsWatchFramework) {
				if (!IPhoneSdkVersion.TryParse (SdkVersion, out version)) {
					Log.LogError (MSBStrings.E0066, SdkVersion);
					return false;
				}

				version = currentSdk.GetClosestInstalledSdk (version, false);
				sdk_path = currentSdk.GetSdkPath (version, SdkIsSimulator);
			} else {
				if (AppleSdkSettings.XcodeVersion.Major >= 10) {
					Log.LogError (MSBStrings.E0067);
					return false;
				}
				if (!(AppleSdkSettings.XcodeVersion.Major > 6 || (AppleSdkSettings.XcodeVersion.Major == 6 && AppleSdkSettings.XcodeVersion.Minor >= 2))) {
					Log.LogError (MSBStrings.E0068);
					return false;
				}

				if (!IPhoneSdkVersion.TryParse (SdkVersion, out version)) {
					Log.LogError (MSBStrings.E0066, SdkVersion);
					return false;
				}

				if (version < IPhoneSdkVersion.V8_2) {
					Log.LogError (MSBStrings.E0069, version);
					return false;
				}

				version = currentSdk.GetClosestInstalledSdk (version, false);
				sdk_path = currentSdk.GetSdkPath (version, SdkIsSimulator);
			}

			NativeWatchApp = Path.Combine (sdk_path, "Library", "Application Support", "WatchKit", "WK");
			if (File.Exists (NativeWatchApp))
				return true;

			Log.LogError (MSBStrings.E0070);

			return false;
		}
	}
}
