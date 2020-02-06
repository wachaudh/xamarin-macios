﻿using System;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.MacDev.Tasks;
using MSBLocalization;

namespace Xamarin.iOS.Tasks
{
	public abstract class PrepareResourceRulesTaskBase : Task
	{
		#region Inputs

		public string SessionId { get; set; }

		[Required]
		public string AppBundleDir { get; set; }

		public string ResourceRules { get; set; }

		[Required]
		public string SdkVersion { get; set; }

		#endregion

		#region Outputs

		[Output]
		public string PreparedResourceRules { get; set; }

		#endregion

		public override bool Execute ()
		{
			if (!string.IsNullOrEmpty (ResourceRules)) {
				var destination = Path.Combine (AppBundleDir, "ResourceRules.plist");

				if (!File.Exists (ResourceRules)) {
					Log.LogError (MSBStrings.E0065, ResourceRules);
					return false;
				}

				File.Copy (ResourceRules, destination, true);

				PreparedResourceRules = destination;
			} else {
				PreparedResourceRules = string.Empty;
			}

			return !Log.HasLoggedErrors;
		}
	}
}
