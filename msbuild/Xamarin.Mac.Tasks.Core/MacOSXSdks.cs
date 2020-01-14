﻿using System;
using System.IO;

using Xamarin.MacDev;

namespace Xamarin.Mac.Tasks
{
	public static class MacOSXSdks
	{
		public static MacOSXSdk Native { get; private set; }
		public static XamMacSdk XamMac { get; private set; }

		static MacOSXSdks ()
		{
			Native = new MacOSXSdk (AppleSdkSettings.DeveloperRoot, AppleSdkSettings.DeveloperRootVersionPlist);
			XamMac = new XamMacSdk (null);
		}

		public static void CheckInfoCaches ()
		{
			XamMac.CheckCaches ();
		}
	}
}
