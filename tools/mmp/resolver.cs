/*
 * Copyright (c) 2010, Geoff Norton <gnorton@novell.com>
 * Copyright (c) 2010, JB Evain <jbevain@novell.com>
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
using System;
using System.Collections.Generic;
using System.IO;

using Mono.Cecil;

namespace Xamarin.Bundler {
	public partial class MonoMacResolver : CoreResolver {
		public List <string> CommandLineAssemblies { get; set; }
		public List<Exception> Exceptions = new List<Exception> ();


		public AssemblyDefinition GetAssembly (string fileName)
		{
			return Resolve (new AssemblyNameReference (Path.GetFileNameWithoutExtension (fileName), null), new ReaderParameters { AssemblyResolver = this });
		}

		public AssemblyDefinition Resolve (string fullName)
		{
			return Resolve (AssemblyNameReference.Parse (fullName), new ReaderParameters { AssemblyResolver = this });
		}

		public override AssemblyDefinition Resolve (AssemblyNameReference reference, ReaderParameters parameters)
		{
			var name = reference.Name;

			AssemblyDefinition assembly;
			if (cache.TryGetValue (name, out assembly))
				return assembly;
			
			if (CommandLineAssemblies != null && CommandLineAssemblies.Count > 0) {
				string cmdasm = CommandLineAssemblies.Find (t => {
					if (String.IsNullOrEmpty (t))
						return false;
					return String.Compare (name, Path.GetFileNameWithoutExtension (t), StringComparison.Ordinal) == 0;
				});
				assembly = String.IsNullOrEmpty (cmdasm) ? null : Load (cmdasm);
				if (assembly != null)
					return assembly;
			}

			if (ArchDirectory != null) {
				assembly = SearchDirectory (name, ArchDirectory);
				if (assembly != null)
					return assembly;
			}
			
			assembly = SearchDirectory (name, FrameworkDirectory);
			if (assembly != null)
				return assembly;

			var pclPath = Path.Combine (FrameworkDirectory, "Facades");
			if (Directory.Exists (pclPath)) {
				assembly = SearchDirectory (name, pclPath);
				if (assembly != null)
					return assembly;
			}

			assembly = SearchDirectory (name, RootDirectory, ".exe");
			if (assembly != null)
				return assembly;

			return null;
		}
	}
}
