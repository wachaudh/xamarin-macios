﻿using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Xamarin.MMP.Tests
{
	[TestFixture]
	public class PackageReferenceTests
	{
		const string PackageReference = @"<ItemGroup><PackageReference Include = ""Newtonsoft.Json"" Version = ""10.0.3"" /></ItemGroup>";
		const string TestCode = @"var output = Newtonsoft.Json.JsonConvert.SerializeObject (new int[] { 1, 2, 3 });";

		[TestCase (true)]
		[TestCase (false)]
		public void AppsWithPackageReferencs_BuildAndRun (bool full)
		{
			MMPTests.RunMMPTest (tmpDir => {
				var config = new TI.UnifiedTestConfig (tmpDir) {
					ItemGroup = PackageReference,
					TestCode = TestCode + @"			if (output == ""[1,2,3]"")
				",
					XM45 = full
				};
				TI.AddGUIDTestCode (config);

				string project = TI.GenerateUnifiedExecutableProject (config);
				TI.NugetRestore (project);
				TI.BuildProject (project);
				TI.RunGeneratedUnifiedExecutable (config);
			});
		}

		[TestCase (true)]
		[TestCase (false)]
		// context https://github.com/xamarin/xamarin-macios/issues/7113
		public void SatellitesFromNuget (bool full)
		{
			MMPTests.RunMMPTest (tmpDir => {
				var config = new TI.UnifiedTestConfig (tmpDir) {
					ItemGroup = @"<ItemGroup><PackageReference Include = ""Humanizer"" Version = ""2.7.2"" /></ItemGroup>",
					TestCode = "Humanizer.DateHumanizeExtensions.Humanize (System.DateTime.UtcNow.AddHours (-30));\n",
					XM45 = full
				};
				TI.AddGUIDTestCode (config);

				string project = TI.GenerateUnifiedExecutableProject (config);
				TI.NugetRestore (project);
				TI.BuildProject (project);

				var appDir = Path.Combine (tmpDir, "bin", "Debug", full ? "XM45Example.app" : "UnifiedExample.app");
				Assert.True (File.Exists (Path.Combine (appDir, "Contents", "MonoBundle", "fr", "Humanizer.resources.dll")), "fr");
			});
		}

		[Test]
		public void ExtensionProjectPackageReferencs_Build ()
		{
			MMPTests.RunMMPTest (tmpDir => {
				TI.CopyDirectory (Path.Combine (TI.FindSourceDirectory (), @"Today"), tmpDir);

				string project = Path.Combine (tmpDir, "Today/TodayExtensionTest.csproj");
				string main = Path.Combine (tmpDir, "Today/TodayViewController.cs");

				TI.CopyFileWithSubstitutions (project, project, s => s.Replace ("%ITEMGROUP%", PackageReference));
				TI.CopyFileWithSubstitutions (main, main, s => s.Replace ("%TESTCODE%", TestCode));

				TI.NugetRestore (project);
				string output = TI.BuildProject (Path.Combine (tmpDir, "Today/TodayExtensionTest.csproj"));
				Assert.IsTrue (!output.Contains ("MM2013"));
			});
		}
	}
}
