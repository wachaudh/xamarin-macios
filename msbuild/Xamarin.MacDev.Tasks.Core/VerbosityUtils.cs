﻿// Copyright (c) Microsoft Corp

using System;
using Microsoft.Build.Framework;
using Xamarin.Utils;

namespace Xamarin.MacDev.Tasks {

	// Helper code for Task delegating work to external tools but that still
	// needs to be verbosity-aware
	public static class VerbosityUtils {

		// verbosity can be set in multiple ways
		// this makes it a consistent interpretation of them
		static public string[] Merge (string extraArguments, LoggerVerbosity taskVerbosity)
		{
			string[] result = Array.Empty<string> ();
			var empty_extra = String.IsNullOrEmpty (extraArguments);
			// We give the priority to the extra arguments given to the tools
  			if (empty_extra || (!empty_extra && !extraArguments.Contains ("-q") && !extraArguments.Contains ("-v"))) {
				// if nothing is specified fall back to msbuild settings
				// first check if some were supplied on the command-line
				result = GetVerbosityLevel (Environment.CommandLine);
				// if not then use the default from the msbuild config files, which Visual Studio for Mac can override (to match the IDE setting for msbuild)
				if (result.Length == 0)
					result = GetVerbosityLevel (taskVerbosity);
			}
			return result;
		}

		//
		// This is an hack, since there can be multiple loggers.
		// However it's the most common use case and `mtouch` logs
		// are often the most important to gather and developers expect
		// a single change (in verbosity) to do the job and be consistent in CI.
		//
		// msbuild argument format
		//	-verbosity:<level> Display this amount of information in the event log.
		//		The available verbosity levels are: q[uiet], m[inimal],
		//		n[ormal], d[etailed], and diag[nostic]. (Short form: -v)
		//
		static public string[] GetVerbosityLevel (string commandLine)
		{
			const string shortForm = "v:";
			const string longForm = "verbosity:";

			if (!StringUtils.TryParseArguments (commandLine, out var args, out _))
				return GetVerbosityLevel (LoggerVerbosity.Normal);

			foreach (var arg in args) {
				// the minimum length we're looking for is `/v:q`
				if (arg.Length < 4)
					continue;
				// msbuild accepts two types of argument separator
				if (arg [0] != '/' && arg [0] != '-')
					continue;

				var verbosity = String.Empty;
				// short and long forms are possible, always case sensitive
				if (arg.IndexOf (shortForm, StringComparison.Ordinal) == 1) {
					// skip argument prefix (+1) and the (short) argument to get the verbosity level
					verbosity = arg.Substring (shortForm.Length + 1);
				} else if (arg.IndexOf (longForm, StringComparison.Ordinal) == 1) {
					verbosity = arg.Substring (longForm.Length + 1);
				} else
					continue;

				// case sensitive
				switch (verbosity) {
				case "q":
				case "quiet":
					return GetVerbosityLevel (LoggerVerbosity.Quiet);
				case "m":
				case "minimal":
					return GetVerbosityLevel (LoggerVerbosity.Minimal);
				case "n":
				case "normal":
				default:
					return GetVerbosityLevel (LoggerVerbosity.Normal);
				case "d":
				case "detailed":
					return GetVerbosityLevel (LoggerVerbosity.Detailed);
				case "diag":
				case "diagnostic":
					return GetVerbosityLevel (LoggerVerbosity.Diagnostic);
				}
			}
			// nothing is normal
			return GetVerbosityLevel (LoggerVerbosity.Normal);
		}

		// The values here come from: https://github.com/mono/monodevelop/blob/143f9b6617123a0841a5cc5a2a4e13b309535792/main/src/core/MonoDevelop.Projects.Formats.MSBuild/MonoDevelop.Projects.MSBuild.Shared/RemoteBuildEngineMessages.cs#L186
		// Assume 'Normal (2)' is the default verbosity (no change), and the other values follow from there.
		static public string[] GetVerbosityLevel (LoggerVerbosity v)
		{
			switch ((LoggerVerbosity) v) {
			case LoggerVerbosity.Quiet:
				return new [] { "-q", "-q", "-q", "-q" };
			case LoggerVerbosity.Minimal:
				return new [] { "-q", "-q" };
			case LoggerVerbosity.Normal:
			default:
				return Array.Empty<string> ();
			case LoggerVerbosity.Detailed:
				return new [] { "-v", "-v" };
			case LoggerVerbosity.Diagnostic:
				return new [] { "-v", "-v", "-v", "-v" };
			}
		}
	}
}
