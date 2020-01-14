﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.MacDev.Tasks;

namespace Xamarin.iOS.Tasks
{
	public abstract class CollectITunesArtworkTaskBase : Task
	{
		#region Inputs

		public string SessionId { get; set; }

		public ITaskItem[] ITunesArtwork { get; set; }

		#endregion

		#region Outputs

		[Output]
		public ITaskItem[] ITunesArtworkWithLogicalNames { get; set; }

		#endregion

		static int ReadInt (BinaryReader reader)
		{
			int result = 0;

			result += (int) reader.ReadByte () << 24;
			result += (int) reader.ReadByte () << 16;
			result += (int) reader.ReadByte () << 8;
			result += (int) reader.ReadByte ();

			return result;
		}

		static bool GetPngImageSize (string path, out int width, out int height)
		{
			// PNG file format specification can be found at: http://www.w3.org/TR/PNG
			BinaryReader reader = null;

			try {
				reader = new BinaryReader (File.OpenRead (path));
				var pngHeader = reader.ReadBytes (8);
				// Check for valid PNG header.
				if (pngHeader[0] != 137 ||
					pngHeader[1] != 80 || pngHeader[2] != 78 || pngHeader [3] != 71 ||
					pngHeader [4] != 13 || pngHeader [5] != 10 || pngHeader [6] != 26 || pngHeader [7] != 10)
					throw new IOException ("File has no valid PNG header.");

				// First chunk is always the IHDR header chunk
				reader.ReadBytes (4); // skip chunk size
				var ihdrID = reader.ReadBytes (4);
				if (ihdrID[0] != 73 || ihdrID[1] != 72 || ihdrID[2] != 68 || ihdrID[3] != 82)
					throw new IOException ("IHDR missing or corrupt.");

				width = ReadInt (reader);
				height = ReadInt (reader);
				return true;
			} catch {
				width = height = -1;
				return false;
			} finally {
				if (reader != null)
					reader.Close ();
			}
		}

		const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

		[DllImport (CoreGraphicsLibrary)]
		extern static /* CGDataProviderRef */ IntPtr CGDataProviderCreateWithFilename (/* const char* */ string filename);

		[DllImport (CoreGraphicsLibrary)]
		extern static void CGDataProviderRelease (/* CGDataProviderRef */ IntPtr provider);

		[DllImport (CoreGraphicsLibrary)]
		extern static IntPtr CGImageGetWidth (/* CGImageRef */ IntPtr image);

		[DllImport (CoreGraphicsLibrary)]
		extern static IntPtr CGImageGetHeight (/* CGImageRef */ IntPtr image);

		[DllImport (CoreGraphicsLibrary)]
		extern static /* CGImageRef */ IntPtr CGImageCreateWithJPEGDataProvider (/* CGDataProviderRef */ IntPtr source, /* CGFloat[] */ IntPtr decode, bool shouldInterpolate, CGColorRenderingIntent intent);

		[DllImport (CoreGraphicsLibrary)]
		extern static void CGImageRelease (/* CGImageRef */ IntPtr image);

		// untyped enum -> CGColorSpace.h
		enum CGColorRenderingIntent
		{
			Default,
			AbsoluteColorimetric,
			RelativeColorimetric,
			Perceptual,
			Saturation,
		};

		static bool GetJpgImageSize (string path, out int width, out int height)
		{
			width = height = -1;
			try {
				var provider = CGDataProviderCreateWithFilename (path);
				if (provider == IntPtr.Zero)
					return false;

				try {
					var img = CGImageCreateWithJPEGDataProvider (provider, IntPtr.Zero, false, CGColorRenderingIntent.Default);
					if (img == IntPtr.Zero)
						return false;

					try {
						width = (int) CGImageGetWidth (img);
						height = (int) CGImageGetHeight (img);
					} finally {
						CGImageRelease (img);
					}
				} finally {
					CGDataProviderRelease (provider);
				}
			} catch {
				return false;
			}

			return true;
		}

		public override bool Execute ()
		{
			var artworkWithLogicalNames = new List<ITaskItem> ();
			var artwork = new HashSet<string> ();

			if (ITunesArtwork != null) {
				foreach (var item in ITunesArtwork) {
					// We need a physical path here, ignore the Link element
					var path = item.GetMetadata ("FullPath");
					string logicalName;
					int width, height;

					if (!File.Exists (path)) {
						Log.LogError ("  '{0}' not found.", path);
						continue;
					}

					if (!GetPngImageSize (path, out width, out height) && !GetJpgImageSize (path, out width, out height)) {
						Log.LogError (null, null, null, path, 0, 0, 0, 0, "Error loading '{0}': Unknown image format.", path);
						return false;
					}

					if (width != height || (width != 512 && width != 1024)) {
						Log.LogError (null, null, null, path, 0, 0, 0, 0, "Invalid iTunesArtwork dimensions ({0}x{1}) for '{2}'.", width, height, path);
						return false;
					}

					logicalName = width == 1024 ? "iTunesArtwork@2x" : "iTunesArtwork";

					if (!artwork.Add (logicalName)) {
						Log.LogError (null, null, null, path, 0, 0, 0, 0, "Multiple iTunesArtwork files with the same dimensions detected ({0}x{1}) for '{2}'.", width, height, path);
						return false;
					}

					var bundleResource = new TaskItem (item);
					bundleResource.SetMetadata ("LogicalName", logicalName);
					bundleResource.SetMetadata ("Optimize", "false");

					artworkWithLogicalNames.Add (bundleResource);
				}
			}

			ITunesArtworkWithLogicalNames = artworkWithLogicalNames.ToArray ();

			return !Log.HasLoggedErrors;
		}
	}
}
