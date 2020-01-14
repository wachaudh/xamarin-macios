﻿//
// MAImageCaptioning Unit Tests
//
// Authors:
//	Sebastien Pouliot  <sebastien.pouliot@microsoft.com>
//
// Copyright 2019 Microsoft Corporation
//

#if !__WATCHOS__

using System;
using System.IO;
using Foundation;
using MediaAccessibility;
using ObjCRuntime;
using NUnit.Framework;
using MonoTests.System.Net.Http;

namespace MonoTouchFixtures.MediaAccessibility {

	[TestFixture]
	// we want the test to be availble if we use the linker
	[Preserve (AllMembers = true)]
	public class ImageCaptioningTest {

		[Test]
		public void GetCaption ()
		{
			TestRuntime.AssertXcodeVersion (11, 0);
			Assert.Throws<ArgumentNullException> (() => MAImageCaptioning.GetCaption (null, out _));
			using (NSUrl url = new NSUrl (NetworkResources.MicrosoftUrl)) {
				var s = MAImageCaptioning.GetCaption (url, out var e);
				Assert.Null (s, "remote / return value");
				if (e != null && e.Description.Contains ("Invalid url:")) {
					Assert.Fail ("Ignore this failure when network is down"); // could not connect to the network, fail and add a nice reason
				} else {
					Assert.Null (e, "remote / no error"); // weird should be an "image on disk"

				}
			}
			string file = Path.Combine (NSBundle.MainBundle.ResourcePath, "basn3p08.png");
			file = file.Replace (" ", "%20");
			using (NSUrl url = new NSUrl (file)) {
				var s = MAImageCaptioning.GetCaption (url, out var e);
				Assert.Null (s, "local / return value");
				Assert.NotNull (e, "local / error"); // does not like the URL (invalid)
			}
			file = NSBundle.MainBundle.ResourceUrl.AbsoluteString + "basn3p08.png";
			file = file.Replace (" ", "%20");
			using (NSUrl url = new NSUrl (file)) {
				var s = MAImageCaptioning.GetCaption (url, out var e);
				Assert.Null (s, "local / return value");
				Assert.Null (e, "local / no error");
			}
		}

		[Test]
		public void GetMetadataTagPath ()
		{
			TestRuntime.AssertXcodeVersion (11, 0);
			// https://iptc.org/standards/photo-metadata/iptc-standard/
			// but headers mention `Iptc4xmpExt:AOContentDescription` for the Get/Set API
			Assert.That (MAImageCaptioning.GetMetadataTagPath (), Is.EqualTo ("Iptc4xmpExt:ArtworkContentDescription"));
		}

		[Test]
		public void SetCaption ()
		{
			TestRuntime.AssertXcodeVersion (11, 0);
			Assert.Throws<ArgumentNullException> (() => MAImageCaptioning.SetCaption (null, "xamarin", out _));
			// note: calling on a remote URL crash the process - not that it should work but...

			var temp = String.Empty;
			using (NSUrl url = new NSUrl (NSBundle.MainBundle.ResourceUrl.AbsoluteString + "basn3p08.png")) {
#if __MACOS__
				var read_only = false;
#else
				var read_only = Runtime.Arch == Arch.DEVICE;
#endif
				if (read_only) {
					Assert.False (MAImageCaptioning.SetCaption (url, "xamarin", out var e), "Set");
					Assert.NotNull (e, "ro / set / no error"); // weird, it can't be saved back to the file metadata

					var s = MAImageCaptioning.GetCaption (url, out e);
					Assert.Null (s, "ro / roundtrip"); // not very surprising since Set can't save it
					Assert.Null (e, "ro / get / no error");

					Assert.False (MAImageCaptioning.SetCaption (url, "xamarin", out e), "Set 2");
					s = MAImageCaptioning.GetCaption (url, out e);
					Assert.Null (s, "ro / back to original");
					Assert.Null (e, "ro / get back / no error");
				} else {
					Assert.True (MAImageCaptioning.SetCaption (url, "xamarin", out var e), "Set");
					Assert.Null (e, "ro / set / no error"); // weird, it can't be saved back to the file metadata

					var s = MAImageCaptioning.GetCaption (url, out e);
					Assert.Null (s, "ro / roundtrip"); // not very surprising since Set can't save it
					Assert.Null (e, "ro / get / no error");

					Assert.True (MAImageCaptioning.SetCaption (url, "xamarin", out e), "Set 2");
					s = MAImageCaptioning.GetCaption (url, out e);
					Assert.Null (s, "ro / back to original");
					Assert.Null (e, "ro / get back / no error");
				}

				// 2nd try with a read/write copy
				temp = Path.Combine (Path.GetTempPath (), "basn3p08.png");
				File.Copy (url.Path, temp, overwrite: true);
			}
			using (var rw_url = NSUrl.FromFilename (temp)) {
				Assert.True (MAImageCaptioning.SetCaption (rw_url, "xamarin", out var e), "Set");
				Assert.Null (e, "rw / set / no error"); // weird, it can't be saved back to the file metadata

				var s = MAImageCaptioning.GetCaption (rw_url, out e);
				Assert.Null (s, "rw / roundtrip"); // :(
				Assert.Null (e, "rw / get / no error");

				Assert.True (MAImageCaptioning.SetCaption (rw_url, "xamarin", out e), "Set 2");
				s = MAImageCaptioning.GetCaption (rw_url, out e);
				Assert.Null (s, "rw / back to original");
				Assert.Null (e, "rw / get back / no error");
			}
		}
	}
}

#endif
